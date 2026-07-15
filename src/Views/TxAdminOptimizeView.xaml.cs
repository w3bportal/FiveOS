// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using FiveOS.Services;
using FiveOS.ViewModels;

namespace FiveOS.Views;

/// <summary>
/// Code-behind for the txAdmin Optimizer tab. Like <see cref="OptimizeView"/>,
/// user actions are plain event handlers that call public methods on the VM —
/// the file pickers and the overwrite-confirm dialog are inherently view
/// concerns, so they live here rather than in the VM.
/// </summary>
public partial class TxAdminOptimizeView : UserControl
{
    public TxAdminOptimizeView()
    {
        InitializeComponent();
        // Re-read the configured server folder whenever the tab is shown — the
        // user may have set it in Settings since the VM was constructed.
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Vm?.RefreshServerFolder(); };
    }

    private TxAdminOptimizeViewModel? Vm => DataContext as TxAdminOptimizeViewModel;

    private void OnBrowseServer(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var dlg = new OpenFolderDialog
        {
            Title = "Pick your FiveM server's resources folder",
            InitialDirectory = ServerAssetResolver.ServerRoot()
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog() == true)
        {
            UserSettings.SaveServerResourceFolder(dlg.FolderName);
            Vm.RefreshServerFolder();
        }
    }

    private void OnLoadLog(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Load a txAdmin / server console log",
            Filter = "Log files (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Vm.LogText = File.ReadAllText(dlg.FileName);
            Vm.ParseAndResolve();
        }
        catch (Exception ex)
        {
            AppDialog.Show(
                $"Couldn't read that log file:\n\n{ex.Message}",
                "Load failed", MessageBoxButton.OK, MessageBoxImage.Error, Window.GetWindow(this));
        }
    }

    private void OnParse(object sender, RoutedEventArgs e) => Vm?.ParseAndResolve();

    private void OnClear(object sender, RoutedEventArgs e) => Vm?.ClearAll();

    // ─── Drag-drop a .log / .txt onto the tab ────────────────────────────

    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = TryGetDroppedLog(e) != null
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (TryGetDroppedLog(e) != null) DropOverlay.Visibility = Visibility.Visible;
    }

    private void OnDragLeave(object sender, System.Windows.DragEventArgs e) =>
        DropOverlay.Visibility = Visibility.Collapsed;

    private void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (Vm == null) return;
        var file = TryGetDroppedLog(e);
        if (file == null) return;
        try
        {
            Vm.LogText = File.ReadAllText(file);
            Vm.ParseAndResolve();
        }
        catch (Exception ex)
        {
            AppDialog.Show(
                $"Couldn't read that log file:\n\n{ex.Message}",
                "Load failed", MessageBoxButton.OK, MessageBoxImage.Error, Window.GetWindow(this));
        }
    }

    /// <summary>First dropped path that is a .log/.txt file, or null.</summary>
    private static string? TryGetDroppedLog(System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return null;
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths) return null;
        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue;
            var ext = Path.GetExtension(p);
            if (ext.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return null;
    }

    private void OnOpenBackup(object sender, RoutedEventArgs e)
    {
        var path = Vm?.LastBackupRoot;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")
                { UseShellExecute = true });
        }
        catch { /* explorer failure is non-fatal */ }
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;

        var pick = AppDialog.Show(
            $"Optimize {Vm.ActionableCount} asset(s) in place — the originals in your server folder will be overwritten.\n\n" +
            (Vm.BackupOriginals
                ? "Originals will be backed up to Downloads\\FiveOS_txAdmin_Backup_… first.\n\n"
                : "Backups are OFF — make sure you have copies elsewhere.\n\n") +
            "Continue?",
            "Optimize assets from log",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            Window.GetWindow(this));
        if (pick != MessageBoxResult.OK) return;

        await Vm.RunAsync();
    }
}
