// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using FiveOS.Services;
using FiveOS.ViewModels;
using Wpf.Ui.Controls;

namespace FiveOS.Views;

/// <summary>
/// Batch convert dialog. Up to 30 source meshes go through the existing
/// <see cref="EngineRunner"/> pipeline back-to-back, each routed into
/// the global <see cref="PropPackSession"/>. When the queue completes
/// and "Finalise pack when done" is on, <see cref="PropPackBuilder"/>
/// compiles the merged resource and the dialog reports the output path
/// back to the owner so MainWindow can show its success overlay.
/// </summary>
public partial class BatchConvertWindow : FluentWindow
{
    private readonly BatchConvertViewModel _vm;
    private CancellationTokenSource? _cts;

    /// <summary>Resource path produced when the dialog auto-finalised
    /// the pack on completion. Null when the user closed without
    /// finalising or chose to defer finalisation to the main window.
    /// Owner reads this after <see cref="ShowDialog"/> returns.</summary>
    public string? ResultPackPath { get; private set; }

    /// <summary>Delivery mode of the resulting pack (zip vs. server
    /// folder). Mirrors <see cref="EngineRunner.OutputMode"/> so the
    /// owner can surface the right "Open in Explorer" affordance.</summary>
    public EngineRunner.OutputMode? ResultMode { get; private set; }

    public BatchConvertWindow()
    {
        InitializeComponent();
        _vm = new BatchConvertViewModel();
        DataContext = _vm;

        // Inherit the active pack-session name when one is already in
        // progress — keeps multi-import inside an existing pack rather
        // than silently creating a parallel one.
        if (PropPackSession.Current.Entries.Count > 0)
        {
            _vm.PackName = PropPackSession.Current.PackName;
        }
    }

    /// <summary>Pre-populate the queue from initial paths (e.g. dropped
    /// onto the main window). Returns the dialog so the caller can
    /// chain <c>.ShowDialog()</c>.</summary>
    public BatchConvertWindow WithInitialFiles(System.Collections.Generic.IEnumerable<string> paths)
    {
        _vm.AddPaths(paths);
        return this;
    }

    // ─── Window-level drag/drop ──────────────────────────────────────

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (!_vm.CanAdd) { e.Handled = true; return; }
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            if (files.Any(BatchConvertViewModel.IsSupportedForBatch))
                e.Effects = DragDropEffects.Copy;
        }
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!_vm.CanAdd) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        _vm.AddPaths(files);
        e.Handled = true;
    }

    // ─── Empty-zone click → browse ───────────────────────────────────

    private void OnEmptyZoneClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => OnBrowse(sender, new RoutedEventArgs());

    // ─── Toolbar buttons ─────────────────────────────────────────────

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add 3D files to the batch",
            Multiselect = true,
            Filter = "3D models (*.obj;*.glb;*.gltf;*.fbx;*.dae;*.ply;*.stl)|" +
                     "*.obj;*.glb;*.gltf;*.fbx;*.dae;*.ply;*.stl|" +
                     "All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true)
            _vm.AddPaths(dlg.FileNames);
    }

    private void OnClear(object sender, RoutedEventArgs e) => _vm.Clear();

    private void OnRemoveItem(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is BatchConvertItem item)
            _vm.Remove(item);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        // If a run is in progress, signal cancellation but let the
        // current item finish — the engine isn't cancellable mid-spawn
        // without leaving half-written staging on disk.
        if (_vm.IsRunning)
        {
            _cts?.Cancel();
            _vm.StatusText = "Stopping after current item…";
            return;
        }
        DialogResult = ResultPackPath != null;
        Close();
    }

    // ─── Run the queue ───────────────────────────────────────────────

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanStart) return;

        if (!EngineRunner.IsEngineAvailable())
        {
            AppDialog.Show(
                $"Conversion engine is missing.\n\nExpected: {EngineRunner.EnginePath}\n\nRe-install FiveOS.",
                "Engine not available",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning,
                this);
            return;
        }

        // Latch the pack-session name to whatever the dialog has —
        // PropPackBuilder reads it back when staging the resource
        // folder. Empty input falls back to the sanitised default
        // already provided by the VM.
        var sessionPackName = string.IsNullOrWhiteSpace(_vm.PackName) ? "props_pack" : _vm.PackName;
        PropPackSession.Current.PackName = sessionPackName;

        _vm.IsRunning = true;
        _vm.CurrentIndex = 0;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var runner = new EngineRunner();
        int done = 0, failed = 0;
        try
        {
            // Snapshot the queue at start time. New rows added mid-run
            // would race the CanStart guard anyway, but a snapshot also
            // means a "Remove" click during the run can't shift indexes
            // out from under us.
            var snapshot = _vm.Items.Where(i => i.Status == BatchConvertStatus.Pending).ToList();
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (token.IsCancellationRequested)
                {
                    _vm.StatusText = $"Stopped after {done} of {snapshot.Count} items.";
                    break;
                }

                var item = snapshot[i];
                _vm.CurrentIndex = i + 1;
                item.Status = BatchConvertStatus.Converting;
                _vm.StatusText = $"Converting {i + 1} of {snapshot.Count} — {item.SourceName}";

                var req = new EngineRunner.ConvertRequest(
                    SourcePath:      item.SourcePath,
                    AssetName:       item.AssetName,
                    Up:              EngineRunner.UpAxis.Auto,
                    CollisionMaterial: string.IsNullOrWhiteSpace(_vm.CollisionMaterial) ? "CONCRETE" : _vm.CollisionMaterial,
                    IncludeCollision: _vm.IncludeCollision,
                    EmbedCollision:   _vm.EmbedCollision,
                    IncludeYtyp:      _vm.IncludeYtyp,
                    ExtractTextures:  _vm.ExtractTextures,
                    // Identity transforms — the dialog has no viewer
                    // gizmo, so each file ships as-authored. Users
                    // who need per-item nudges should use the single-
                    // file convert flow.
                    ScaleHint:        (1d, 1d, 1d),
                    PositionHint:     "0,0,0",
                    RotationHint:     "0,0,0",
                    ExcludeMeshes:    null,
                    Mode:             EngineRunner.ConvertMode.Prop,
                    GenerateLods:     _vm.GenerateLods,
                    RouteToPack:      true);

                EngineRunner.ConvertOutcome outcome;
                try
                {
                    outcome = await runner.RunAsync(req, onLog: null, cancel: token);
                }
                catch (OperationCanceledException)
                {
                    item.Status = BatchConvertStatus.Failed;
                    item.Error = "Cancelled.";
                    break;
                }
                catch (Exception ex)
                {
                    item.Status = BatchConvertStatus.Failed;
                    item.Error = ex.Message;
                    failed++;
                    continue;
                }

                if (outcome.Success)
                {
                    item.Status = BatchConvertStatus.Done;
                    item.Error = null;
                    done++;
                }
                else
                {
                    item.Status = BatchConvertStatus.Failed;
                    item.Error = outcome.Error ?? "Engine reported failure with no detail.";
                    failed++;
                }
            }

            _vm.CurrentIndex = 0;
            _vm.StatusText = failed == 0
                ? $"✓ Converted {done} of {snapshot.Count} item(s)."
                : $"Converted {done}, failed {failed}. See per-row error details.";

            // Auto-finalise — only when the user asked for it AND at
            // least one item landed. A pack with zero entries can't be
            // built; we leave the dialog open so the user can fix the
            // queue and retry.
            if (_vm.FinalizeWhenDone && done > 0)
            {
                _vm.StatusText = $"Finalising pack '{sessionPackName}' ({PropPackSession.Current.Count} prop(s))…";
                PropPackBuilder.BuildResult buildResult;
                try
                {
                    buildResult = PropPackBuilder.Build(PropPackSession.Current);
                }
                catch (Exception ex)
                {
                    _vm.StatusText = $"✗ Pack finalize failed: {ex.Message}";
                    AppDialog.Show(
                        $"Pack finalize failed:\n\n{ex.Message}",
                        "Pack finalize failed",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error,
                        this);
                    return;
                }

                if (buildResult.Success && buildResult.ResultPath is not null)
                {
                    ResultPackPath = buildResult.ResultPath;
                    ResultMode = buildResult.Mode;
                    // Clear staging so the next batch starts clean —
                    // matches what OnFinalizePack does in MainWindow.
                    PropPackSession.Current.Clear();
                    _vm.StatusText = $"✓ Pack ready · {Path.GetFileName(buildResult.ResultPath)}";
                    DialogResult = true;
                    Close();
                    return;
                }

                _vm.StatusText = $"✗ Pack finalize failed: {buildResult.Error}";
                AppDialog.Show(
                    buildResult.Error ?? "Pack finalize failed.",
                    "Pack finalize failed",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error,
                    this);
            }
        }
        finally
        {
            _vm.IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }
}
