// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using CodeWalker.GameFiles;
using FiveOS.Services;
using FiveOS.ViewModels;
using DrawableLod = FiveOS.Services.DrawableLodBuilder.DrawableLod;

namespace FiveOS.Views;

public partial class OptimizeView : UserControl
{
    // ─── Inline LOD-preview state (migrated from the old PreviewWindow) ──
    private List<DrawableBase> _drawables = new();
    private int _sourceTris;     // of the CURRENT LOD
    private int _sourceVerts;    // of the CURRENT LOD
    private bool _hasMed, _hasLow;   // which LODs this model actually has
    private DrawableMeshExtractor? _extractor;
    private string? _sessionDir;
    private bool _webViewReady, _viewerReady, _resourceReady, _suppressUi;
    private string _currentLod = "HIGH";
    private readonly Dictionary<string, double> _ratios = new(StringComparer.Ordinal)
    {
        ["HIGH"] = 1.0, ["MED"] = 0.5, ["LOW"] = 0.2,   // no VLOW
    };
    private const bool PreserveBoundary = true;
    private readonly DispatcherTimer _debounce;
    private OptimizeQueueItem? _loadedItem;
    private CancellationTokenSource? _loadCts;
    private string? _detectedYtdName;   // sibling .ytd surfaced for the current model

    // ─── Projected-size estimate (workbench toolbar) ─────────────────────
    private readonly DispatcherTimer _sizeDebounce;
    private long _origFileBytes;
    private int _sizeGen;            // bumps each estimate; stale results are ignored
    private bool _sizeBusy, _sizePending;
    private PropertyChangedEventHandler? _vmHandler;
    private bool _cameraFramed;   // frame the camera once per selection, then keep the user's orbit

    // ─── Flat texture gallery state (texture modes) ──────────────────────
    private readonly ObservableCollection<TextureTile> _galleryTiles = new();
    private CancellationTokenSource? _galleryCts;

    public OptimizeView()
    {
        InitializeComponent();
        TextureGallery.ItemsSource = _galleryTiles;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RenderCurrentAsync(); };
        // Separate, slightly slower debounce for the projected-size estimate —
        // it's a full in-memory save, so only fire once the user has settled.
        _sizeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _sizeDebounce.Tick += (_, _) => { _sizeDebounce.Stop(); _ = ComputeSizeEstimateAsync(); };
        Loaded += (_, _) => { WirePreviewPanel(); ApplySavedQueueWidth(); };
        DataContextChanged += (_, _) => WirePreviewPanel();
        Unloaded += (_, _) =>
        {
            _debounce.Stop();
            _sizeDebounce.Stop();
        };
        // Spawn the WebView2 only when the Optimize tab is first shown — the
        // Edge process tree (~40 MB) shouldn't exist for users who never open it.
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && !_webViewReady) _ = InitWebViewAsync(); };
    }

    /// <summary>Dispose WebView2, cancel loads, and delete the session viewer dir.</summary>
    public void Teardown()
    {
        _debounce.Stop();
        _sizeDebounce.Stop();

        try { _loadCts?.Cancel(); _loadCts?.Dispose(); } catch { /* */ }
        _loadCts = null;
        try { _galleryCts?.Cancel(); _galleryCts?.Dispose(); } catch { /* */ }
        _galleryCts = null;

        if (_vmHandler != null && Vm != null)
        {
            try { Vm.PropertyChanged -= _vmHandler; } catch { /* */ }
            _vmHandler = null;
        }

        try
        {
            if (OptimizeViewport?.CoreWebView2 != null)
                OptimizeViewport.CoreWebView2.WebMessageReceived -= OnViewerMessage;
        }
        catch { /* */ }

        try { OptimizeViewport?.Dispose(); } catch { /* already gone */ }
        _webViewReady = false;
        _viewerReady = false;

        _galleryTiles.Clear();

        if (!string.IsNullOrEmpty(_sessionDir))
        {
            try
            {
                if (Directory.Exists(_sessionDir))
                    Directory.Delete(_sessionDir, recursive: true);
            }
            catch { /* CacheService sweeps leftovers */ }
            _sessionDir = null;
        }
        _extractor = null;
    }

    private OptimizeViewModel? Vm => DataContext as OptimizeViewModel;

    private void WirePreviewPanel()
    {
        if (Vm == null) return;
        if (_vmHandler != null) Vm.PropertyChanged -= _vmHandler;
        _vmHandler = OnVmPropertyChanged;
        Vm.PropertyChanged += _vmHandler;
        UpdateOptionsColumn();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OptimizeViewModel.Mode))
            UpdateOptionsColumn();
        if (e.PropertyName is nameof(OptimizeViewModel.SelectedItem) or nameof(OptimizeViewModel.Mode))
            OnSelectionChanged();
    }

    /// <summary>Collapse the left options column for the geometry/LOD modes
    /// (everything is driven from the workbench); keep it for texture modes,
    /// which use the compression settings. Texture modes honour the width the
    /// user last dragged the options splitter to.</summary>
    private void UpdateOptionsColumn()
        => OptionsColumn.Width = (Vm?.IsTextureMode == true)
            ? new GridLength(UserSettings.LoadOptimizeOptionsWidth())
            : new GridLength(0);

    /// <summary>Restore the queue column to the pixel width the user last
    /// dragged the content splitter to. Left at its default proportional
    /// (2*) share until then.</summary>
    private void ApplySavedQueueWidth()
    {
        var w = UserSettings.LoadOptimizeQueueWidth();
        if (w > 0) QueueColumn.Width = new GridLength(w);
    }

    private void OnOptionsSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        => UserSettings.SaveOptimizeOptionsWidth(OptionsColumn.ActualWidth);

    private void OnContentSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        // The queue starts as a star column; pin it to the dragged pixel width
        // so the preview/gallery keeps every remaining pixel as the window grows.
        QueueColumn.Width = new GridLength(QueueColumn.ActualWidth);
        UserSettings.SaveOptimizeQueueWidth(QueueColumn.ActualWidth);
    }

    // ─── Mode picker ──────────────────────────────────────────────────

    private void OnModeProps(object sender, RoutedEventArgs e)    => SetMode(OptimizeMode.Props);
    private void OnModeClothing(object sender, RoutedEventArgs e) => SetMode(OptimizeMode.Clothing);
    private void OnModeTextures(object sender, RoutedEventArgs e) => SetMode(OptimizeMode.Textures);
    private void OnModeEmbeddedTextures(object sender, RoutedEventArgs e) => SetMode(OptimizeMode.EmbeddedTextures);
    private void OnModeTxAdmin(object sender, RoutedEventArgs e) => SetMode(OptimizeMode.TxAdmin);

    private void SetMode(OptimizeMode mode)
    {
        if (Vm == null) return;
        Vm.Mode = mode;
    }

    // ─── Drag-drop / browse ──────────────────────────────────────────

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // Hosted tool modes (txAdmin) run their own drag-drop inside the
        // swapped-in view — don't intercept the tunneling events here or
        // the hosted view never sees them.
        if (Vm?.IsHostedToolMode == true) return;

        e.Effects = DragDropEffects.None;
        if (Vm == null) { e.Handled = true; return; }
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            if (files.Any(f => OptimizeViewModel.IsAcceptedDrop(f, Vm.Mode)))
                e.Effects = DragDropEffects.Copy;
        }
        e.Handled = true;
    }

    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (Vm == null) return;
        // Let the hosted tool view (txAdmin) take its own drops instead of
        // routing everything into the asset queues.
        if (Vm.IsHostedToolMode) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        Vm.AddPaths(files);
        // Drop and PreviewDrop are both wired to this handler; mark handled so
        // the tunnel pass doesn't also bubble and run AddPaths a second time.
        e.Handled = true;
    }

    private void OnDropZoneClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var dlg = new OpenFileDialog
        {
            Title = Vm.IsEmbeddedTexturesMode
                ? "Add model files (.ydd / .ydr / .yft) to optimize"
                : $"Add {Vm.ActiveExtension.TrimStart('.').ToUpperInvariant()} files to optimize",
            Filter = Vm.ActiveBrowseFilter,
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true)
            Vm.AddPaths(dlg.FileNames);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        // A folder picker is the natural way to throw a whole resource or
        // clothing pack at the optimizer — AddPaths recurses it for every
        // supported type (.ydr/.ydd/.ytd/.yft) and keeps the subfolders.
        var dlg = new OpenFolderDialog
        {
            Title = "Pick a folder (a resource or clothing pack) to optimize",
            Multiselect = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog() == true)
            Vm.AddPaths(dlg.FolderNames);
    }

    private void OnClear(object sender, RoutedEventArgs e) => Vm?.ClearActive();

    private void OnOpenOutput(object sender, RoutedEventArgs e) => Vm?.OpenOutputFolder();

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;

        // Optimize overwrites the source files in place. Warn before every
        // run — these are usually one-of-a-kind exports the user can't
        // re-decimate from once they're gone.
        var count = Vm.FileCount;
        var ext = Vm.IsEmbeddedTexturesMode ? "MODEL" : Vm.ActiveExtension.ToUpperInvariant();
        var pick = AppDialog.Show(
            $"Optimize will overwrite {count} {ext} file(s) in place — the originals will be replaced.\n\n" +
            "Back up the files first if you don't have copies elsewhere.\n\n" +
            "Continue?",
            "Back up before optimizing",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning,
            Window.GetWindow(this));
        if (pick != System.Windows.MessageBoxResult.OK) return;

        await Vm.RunAsync();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Vm?.RequestCancel();

    // ─── Inline preview: WebView2 init (lazy, PoseToEmote pattern) ─────

    private async System.Threading.Tasks.Task InitWebViewAsync()
    {
        if (_webViewReady) return;
        try
        {
            var userDataDir = Path.Combine(Path.GetTempPath(), "FiveOS", "WebView2-Optimize");
            Directory.CreateDirectory(userDataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await OptimizeViewport.EnsureCoreWebView2Async(env);
            OptimizeViewport.CoreWebView2.Settings.AreDevToolsEnabled = true;

            _sessionDir = Path.Combine(Path.GetTempPath(), "FiveOS", "ViewerOptimize-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_sessionDir);
            CopyDirectory(RuntimeAssets.ViewerDir, _sessionDir);
            _extractor = new DrawableMeshExtractor(_sessionDir);

            OptimizeViewport.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "optimize-viewer.local", _sessionDir, CoreWebView2HostResourceAccessKind.Allow);
            WebViewDialogs.Theme(OptimizeViewport.CoreWebView2);
            OptimizeViewport.CoreWebView2.WebMessageReceived += OnViewerMessage;
            OptimizeViewport.Source = new Uri("https://optimize-viewer.local/viewer.html");
            _webViewReady = true;

            // If a drawable row is already selected, start its load now (in
            // parallel with the viewer boot); the render waits for 'ready'.
            if (Vm?.SelectedItem != null) OnSelectionChanged();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowMessage("Microsoft Edge WebView2 Runtime is not installed. Install it and re-launch.");
        }
        catch (Exception ex)
        {
            ShowMessage($"Viewer failed to start: {ex.Message}");
        }
    }

    private void OnViewerMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.WebMessageAsJson; } catch { return; }

        if (json.Contains("\"workbench-loaded\""))
        {
            Dispatcher.Invoke(() =>
            {
                var t = ExtractInt(json, "\"tris\":");
                var v = ExtractInt(json, "\"verts\":");
                if (t >= 0)
                    OptOptimizedTrisText.Text = v >= 0
                        ? $"Optimized: {t:N0} tris · {v:N0} verts"
                        : $"Optimized: {t:N0} tris";
            });
        }
        else if (json.Contains("\"ready\"") && !_viewerReady)
        {
            _viewerReady = true;
            Dispatcher.Invoke(ApplyReadyState);
        }
    }

    // Apply the right in-page state once the viewer reports ready (messages
    // sent before 'ready' were no-ops, so re-assert the current state here).
    private void ApplyReadyState()
    {
        if (_resourceReady && _drawables.Count > 0) { TryRender(); return; }
        if (_loadedItem != null) { ShowMessage("Loading drawable…"); return; }   // load in flight
        ShowMessage("Select a model on the left to preview it");
    }

    // ─── Inline preview: selection → load → render ────────────────────

    private void OnSelectionChanged()
    {
        var vm = Vm;
        if (vm == null) return;
        var item = vm.SelectedItem;

        // Texture modes (YTD + Embedded) show a flat thumbnail gallery on the
        // right instead of the 3D LOD workbench.
        if (vm.IsTextureMode)
        {
            LoadTextureGallery(item);
            return;
        }

        // Geometry modes: the 3D workbench. Nothing to preview when no row is
        // selected or we're not in a LOD mode.
        if (item == null || !vm.IsLodMode)
        {
            _loadedItem = null;
            _resourceReady = false;
            _drawables = new();
            _sizeDebounce.Stop();
            OptSizeText.Text = "—";
            _extractor?.SetExternalTextures(null);
            OptYtdToggle.IsEnabled = false;
            OptYtdNameText.Text = "—";
            OptSaveButton.IsEnabled = false;
            ShowMessage("Select a model on the left to preview it");
            return;
        }

        if (ReferenceEquals(item, _loadedItem) && _resourceReady) return;   // already showing it

        // New drawable selection — reset per-LOD state to defaults.
        _loadedItem = item;
        _resourceReady = false;
        _cameraFramed = false;   // frame once when this new model first renders
        _currentLod = "HIGH";
        _ratios["HIGH"] = 1.0; _ratios["MED"] = 0.5; _ratios["LOW"] = 0.2;
        _suppressUi = true;
        OptLodHigh.IsChecked = true;
        _suppressUi = false;
        SyncSliderToLod();
        OptSaveButton.IsEnabled = false;
        ShowMessage("Loading drawable…");
        // Drop the previous model's external textures so the new one renders
        // honestly (flat) until its own .ytd is detected/loaded below.
        _extractor?.SetExternalTextures(null);
        OptYtdToggle.IsEnabled = false;
        OptYtdNameText.Text = "scanning for .ytd…";

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        _ = LoadAsync(item, item.Path, vm.Mode, _loadCts.Token);
    }

    private async System.Threading.Tasks.Task LoadAsync(OptimizeQueueItem item, string path, OptimizeMode mode, CancellationToken token)
    {
        try
        {
            var drawables = await System.Threading.Tasks.Task.Run(() => LoadDrawables(path, mode), token);
            if (token.IsCancellationRequested || !ReferenceEquals(item, _loadedItem)) return;

            _drawables = drawables;
            _resourceReady = true;
            try { _origFileBytes = new FileInfo(path).Length; } catch { _origFileBytes = 0; }
            OptSizeText.Text = _origFileBytes > 0 ? $"File {FormatBytes(_origFileBytes)} → ~…" : "—";
            RefreshLodInfo();   // which LODs exist (+ warnings) + current-LOD source counts
            if (drawables.Count == 0) { OptSaveButton.IsEnabled = false; ShowMessage("No drawable geometry to preview."); return; }
            UpdateSaveButton();
            TryRender();
            ScheduleSizeEstimate();
            ApplyYtdForSelection(item, path);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && ReferenceEquals(item, _loadedItem))
                ShowMessage($"Couldn't load drawable: {ex.Message}");
        }
    }

    private List<DrawableBase> LoadDrawables(string path, OptimizeMode mode)
    {
        var list = new List<DrawableBase>();
        switch (mode)
        {
            case OptimizeMode.Props:
                var ydr = DrawableOptimizer.LoadResource<YdrFile>(path);
                if (ydr.Drawable != null) list.Add(ydr.Drawable);
                break;
            case OptimizeMode.Clothing:
                var ydd = DrawableOptimizer.LoadResource<YddFile>(path);
                if (ydd.Drawables != null)
                    foreach (var d in ydd.Drawables) if (d != null) list.Add(d);
                break;
        }
        return list;
    }

    // ─── Per-LOD info (existence, counts) ────────────────────────────────

    private static DrawableLod LodOf(string s) => s switch
    {
        "MED" => DrawableLod.Med,
        "LOW" => DrawableLod.Low,
        _ => DrawableLod.High,
    };

    /// <summary>Sum (triangles, vertices) across the model's geometry for a LOD.
    /// Zero tris ⇒ the model has no usable geometry at that tier.</summary>
    private (int Tris, int Verts) CountLod(DrawableLod lod)
    {
        int t = 0, v = 0;
        foreach (var d in _drawables)
        {
            var models = DrawableLodBuilder.GetLodModels(d, lod);
            if (models == null) continue;
            foreach (var m in models)
            {
                if (m?.Geometries == null) continue;
                foreach (var g in m.Geometries)
                {
                    if (g?.IndexBuffer?.Indices != null) t += g.IndexBuffer.Indices.Length / 3;
                    if (g?.VertexData != null) v += g.VertexData.VertexCount;
                }
            }
        }
        return (t, v);
    }

    /// <summary>Detect which LODs the model has, drive the ⚠ icons next to the
    /// MED/LOW tabs, and refresh the current LOD's source counts.</summary>
    private void RefreshLodInfo()
    {
        _hasMed = CountLod(DrawableLod.Med).Tris > 0;
        _hasLow = CountLod(DrawableLod.Low).Tris > 0;

        OptLodMedWarn.Visibility = _hasMed ? Visibility.Collapsed : Visibility.Visible;
        OptLodLowWarn.Visibility = _hasLow ? Visibility.Collapsed : Visibility.Visible;
        OptLodMedWarn.ToolTip = _hasMed ? null
            : "This model has no Medium LOD — there's nothing to optimize on this tab. Optimizing only ever reduces LODs that already exist (it never adds them, which would grow the file).";
        OptLodLowWarn.ToolTip = _hasLow ? null
            : "This model has no Low LOD — there's nothing to optimize on this tab. Optimizing only ever reduces LODs that already exist (it never adds them, which would grow the file).";

        UpdateLodSource();
    }

    /// <summary>Refresh the Source: tris/verts readout for the current LOD.</summary>
    private void UpdateLodSource()
    {
        var (t, v) = CountLod(LodOf(_currentLod));
        _sourceTris = t;
        _sourceVerts = v;
        OptSourceTrisText.Text = t > 0
            ? $"Source: {t:N0} tris · {v:N0} verts"
            : $"Source: — (no {_currentLod} LOD)";
        UpdateEstimate();
    }

    /// <summary>Label + enabled-state for the per-LOD save button and slider.
    /// Save is offered only when the current LOD exists AND the ratio actually
    /// reduces (under 100%).</summary>
    private void UpdateSaveButton()
    {
        bool exists = _resourceReady && _sourceTris > 0;
        bool willReduce = _ratios[_currentLod] < 0.999;
        OptSaveButton.Content = $"Save {_currentLod}";
        OptSaveButton.IsEnabled = exists && willReduce;
        OptRatioSlider.IsEnabled = exists;
    }

    // ─── Flat texture gallery: selection → decode → tiles ────────────────

    /// <summary>Decode and show every texture inside the selected file as a
    /// thumbnail grid. Used by the two texture modes (YTD + Embedded) in place
    /// of the 3D workbench. Pure-WPF, so no WebView2 airspace concerns.</summary>
    private void LoadTextureGallery(OptimizeQueueItem? item)
    {
        _galleryCts?.Cancel();
        _galleryCts?.Dispose();
        _galleryTiles.Clear();
        GalleryHeader.Text = "TEXTURES";

        if (item == null)
        {
            GalleryEmpty.Text = "Select a file to view its textures";
            GalleryEmpty.Visibility = Visibility.Visible;
            return;
        }

        GalleryEmpty.Text = "Loading textures…";
        GalleryEmpty.Visibility = Visibility.Visible;

        _galleryCts = new CancellationTokenSource();
        _ = LoadTextureGalleryAsync(item, item.Path, _galleryCts.Token);
    }

    private async System.Threading.Tasks.Task LoadTextureGalleryAsync(
        OptimizeQueueItem item, string path, CancellationToken token)
    {
        try
        {
            var infos = await System.Threading.Tasks.Task.Run(
                () => TextureGalleryExtractor.Extract(path), token);
            if (token.IsCancellationRequested || !ReferenceEquals(item, Vm?.SelectedItem)) return;

            _galleryTiles.Clear();
            foreach (var t in infos)
            {
                ImageSource? img = null;
                if (t.ThumbPng != null)
                {
                    try
                    {
                        using var ms = new MemoryStream(t.ThumbPng);
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();
                        img = bmp;
                    }
                    catch { img = null; }
                }
                _galleryTiles.Add(new TextureTile
                {
                    Image = img,
                    Name = t.Name,
                    Detail = $"{t.Width}×{t.Height} · {t.Format}",
                });
            }

            GalleryHeader.Text = $"TEXTURES · {_galleryTiles.Count}";
            if (_galleryTiles.Count == 0)
            {
                GalleryEmpty.Text = "No textures in this file";
                GalleryEmpty.Visibility = Visibility.Visible;
            }
            else
            {
                GalleryEmpty.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && ReferenceEquals(item, Vm?.SelectedItem))
            {
                GalleryEmpty.Text = $"Couldn't read textures: {ex.Message}";
                GalleryEmpty.Visibility = Visibility.Visible;
            }
        }
    }

    private void TryRender()
    {
        if (!_viewerReady || !_resourceReady || _drawables.Count == 0) return;
        SyncSliderToLod();
        _ = RenderCurrentAsync();
    }

    // ─── Inline preview: LOD / slider interaction ─────────────────────

    private void OnLodChecked(object sender, RoutedEventArgs e)
    {
        // Fires while the XAML is still being parsed (IsChecked="True" on HIGH)
        // — before sibling controls exist. Ignore until the view is built.
        if (!IsInitialized || _suppressUi || !_resourceReady) return;
        if (sender is RadioButton rb && rb.Tag is string lod && _ratios.ContainsKey(lod))
        {
            _currentLod = lod;
            SyncSliderToLod();
            UpdateLodSource();      // refresh source counts for the new LOD
            UpdateSaveButton();
            ScheduleSizeEstimate();
            if (_sourceTris > 0) _ = RenderCurrentAsync();
            else ShowMessage($"This model has no {lod} LOD — nothing to preview or optimize here.");
        }
    }

    private void OnRatioChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // The Slider raises ValueChanged while the panel is still being parsed
        // (Minimum/Value coercion), before OptRatioPctText etc. exist — guard
        // against the null-deref that otherwise kills OptimizeView construction.
        if (!IsInitialized || _suppressUi) return;
        _ratios[_currentLod] = e.NewValue;
        OptRatioPctText.Text = $"{e.NewValue * 100:F0}%";
        UpdateEstimate();
        UpdateSaveButton();   // crossing 100% toggles whether there's anything to save
        if (!_resourceReady || _sourceTris <= 0) return;   // absent LOD: nothing to preview
        _debounce.Stop();
        _debounce.Start();
        ScheduleSizeEstimate();
    }

    private void SyncSliderToLod()
    {
        _suppressUi = true;
        OptRatioSlider.Value = _ratios[_currentLod];
        OptLodNameRun.Text = _currentLod;
        OptRatioPctText.Text = $"{_ratios[_currentLod] * 100:F0}%";
        _suppressUi = false;
    }

    private void UpdateEstimate()
    {
        var ratio = _ratios[_currentLod];
        var estT = (int)Math.Round(_sourceTris * ratio);
        var estV = (int)Math.Round(_sourceVerts * ratio);
        OptOptimizedTrisText.Text = $"Optimized ≈ {estT:N0} tris · {estV:N0} verts";
    }

    // ─── Projected file-size estimate ────────────────────────────────────

    private void ScheduleSizeEstimate()
    {
        if (!_resourceReady) return;
        _sizeDebounce.Stop();
        _sizeDebounce.Start();
    }

    /// <summary>Project the on-disk size after reducing the CURRENT LOD to its
    /// ratio — exactly what "Save [LOD]" would write — by serializing in memory
    /// off the UI thread. Debounced + generation-guarded; one at a time, latest
    /// change coalesced. At 100% or for an absent LOD there's nothing to project.</summary>
    private async System.Threading.Tasks.Task ComputeSizeEstimateAsync()
    {
        var vm = Vm;
        if (vm?.SelectedItem == null || !_resourceReady || !vm.IsLodMode || _origFileBytes <= 0) return;

        var lod = LodOf(_currentLod);
        float ratio = (float)_ratios[_currentLod];
        // Nothing to project: absent LOD, or full-res (no reduction).
        if (_sourceTris <= 0 || ratio >= 0.999f)
        {
            OptSizeText.Text = $"File {FormatBytes(_origFileBytes)}";
            return;
        }

        if (_sizeBusy) { _sizePending = true; return; }
        _sizeBusy = true;

        var item = vm.SelectedItem;
        var path = item.Path;
        int gen = ++_sizeGen;
        OptSizeText.Text = $"File {FormatBytes(_origFileBytes)} → ~…";

        long est;
        try { est = await System.Threading.Tasks.Task.Run(() => DrawableLodBuilder.MeasureLodReducedSize(path, lod, ratio, PreserveBoundary)); }
        catch { est = -1; }

        _sizeBusy = false;

        // Apply only if this is still the current selection and latest request.
        if (gen == _sizeGen && ReferenceEquals(item, Vm?.SelectedItem) && _resourceReady)
        {
            if (est > 0)
            {
                double delta = 1.0 - est / (double)_origFileBytes;
                var note = delta >= 0 ? $"{delta:P0} smaller" : $"{-delta:P0} larger";
                OptSizeText.Text = $"File {FormatBytes(_origFileBytes)} → ~{FormatBytes(est)} ({note})";
            }
            else
            {
                OptSizeText.Text = $"File {FormatBytes(_origFileBytes)}";
            }
        }

        // A change landed mid-compute — recompute with the newest ratio.
        if (_sizePending)
        {
            _sizePending = false;
            ScheduleSizeEstimate();
        }
    }

    private static string FormatBytes(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:F1} KB";
        return $"{b / (1024.0 * 1024):F1} MB";
    }

    // ─── External texture (.ytd) toggle ──────────────────────────────────

    /// <summary>On a new model: surface the sibling .ytd name and, if the
    /// toggle is already on (so texturing persists while browsing a pack),
    /// load it onto the model.</summary>
    private void ApplyYtdForSelection(OptimizeQueueItem item, string path)
    {
        string? cand = null;
        try { cand = ClothingTextureResolver.FindCandidateName(path); } catch { cand = null; }
        _detectedYtdName = cand;
        OptYtdToggle.IsEnabled = cand != null;

        if (OptYtdToggle.IsChecked == true && cand != null)
        {
            _ = LoadExternalTexturesAsync(item, path);
        }
        else
        {
            _extractor?.SetExternalTextures(null);
            OptYtdNameText.Text = cand ?? "no .ytd next to this file";
        }
    }

    private async void OnYtdToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressUi) return;
        var item = _loadedItem;
        if (item == null || _extractor == null) return;

        if (OptYtdToggle.IsChecked == true)
        {
            await LoadExternalTexturesAsync(item, item.Path);
        }
        else
        {
            _extractor.SetExternalTextures(null);
            OptYtdNameText.Text = _detectedYtdName ?? "no .ytd next to this file";
            await RenderCurrentAsync();
        }
    }

    /// <summary>Load the model's external .ytd textures (off the UI thread),
    /// hand them to the extractor, and re-render so the surface paints.</summary>
    private async System.Threading.Tasks.Task LoadExternalTexturesAsync(OptimizeQueueItem item, string path)
    {
        if (_extractor == null) return;
        OptYtdNameText.Text = "loading texture…";
        var drawables = _drawables;

        (string? Name, Dictionary<uint, Texture> Map) res;
        try { res = await System.Threading.Tasks.Task.Run(() => ClothingTextureResolver.Load(path, drawables)); }
        catch (Exception ex) { if (ReferenceEquals(item, _loadedItem)) OptYtdNameText.Text = $"texture load failed: {ex.Message}"; return; }

        if (!ReferenceEquals(item, _loadedItem)) return;   // selection moved on

        if (res.Map.Count > 0)
        {
            _extractor.SetExternalTextures(res.Map);
            OptYtdNameText.Text = $"{res.Name}  ·  {res.Map.Count} tex";
            await RenderCurrentAsync();
        }
        else
        {
            _extractor.SetExternalTextures(null);
            OptYtdNameText.Text = res.Name != null ? $"{res.Name} (no matching textures)" : "no .ytd found";
        }
    }

    private async System.Threading.Tasks.Task RenderCurrentAsync()
    {
        if (!_viewerReady || _extractor == null || OptimizeViewport.CoreWebView2 == null || !_resourceReady) return;
        var lod = _currentLod;
        var ratio = _ratios[lod];
        // Frame the camera only on the first render of a newly-selected model;
        // keep the user's orbit across slider/LOD changes.
        bool frame = !_cameraFramed;
        _cameraFramed = true;

        string payload;
        try { payload = await System.Threading.Tasks.Task.Run(() => BuildLodPayload(lod, ratio, frame)); }
        catch (Exception ex) { ShowMessage($"Preview failed: {ex.Message}"); return; }

        try { await OptimizeViewport.CoreWebView2.ExecuteScriptAsync($"window.fiveosWorkbench.loadLod({payload})"); }
        catch { /* leave whatever the viewer is currently showing */ }
    }

    private string BuildLodPayload(string lod, double ratio, bool frame)
    {
        var lodEnum = LodOf(lod);
        var parts = new StringBuilder(1 << 18);
        bool first = true;
        int tris = 0, verts = 0;
        foreach (var d in _drawables)
        {
            // Preview the ACTUAL geometry of the selected LOD (High/Med/Low),
            // not a tier derived from High — so MED/LOW show what's really there.
            var lodModels = DrawableLodBuilder.GetLodModels(d, lodEnum);
            if (lodModels == null || lodModels.Length == 0) continue;
            DrawableModel[]? models = ratio >= 0.999
                ? lodModels
                : DrawableLodBuilder.BuildTierModels(lodModels, (float)ratio, PreserveBoundary);
            if (models == null) continue;

            var lodMesh = _extractor!.ExtractLod(models, d);
            foreach (var part in lodMesh.Parts)
            {
                if (!first) parts.Append(',');
                first = false;
                AppendPart(parts, part);
                tris += part.Indices.Length / 3;
                verts += part.Positions.Length / 3;
            }
        }
        var sb = new StringBuilder(parts.Length + 64);
        sb.Append("{\"lod\":\"").Append(lod).Append("\",\"tris\":").Append(tris)
          .Append(",\"verts\":").Append(verts)
          .Append(",\"frame\":").Append(frame ? "true" : "false")
          .Append(",\"parts\":[").Append(parts).Append("]}");
        return sb.ToString();
    }

    private static void AppendPart(StringBuilder sb, DrawableMeshExtractor.Part part)
    {
        sb.Append("{\"positions\":["); AppendFloats(sb, part.Positions); sb.Append(']');
        sb.Append(",\"normals\":");
        if (part.Normals != null) { sb.Append('['); AppendFloats(sb, part.Normals); sb.Append(']'); } else sb.Append("null");
        sb.Append(",\"uvs\":");
        if (part.Uvs != null) { sb.Append('['); AppendFloats(sb, part.Uvs); sb.Append(']'); } else sb.Append("null");
        sb.Append(",\"indices\":[");
        for (int i = 0; i < part.Indices.Length; i++) { if (i > 0) sb.Append(','); sb.Append(part.Indices[i]); }
        sb.Append(']');
        sb.Append(",\"textureUrl\":");
        if (part.TextureFile != null) sb.Append('"').Append(part.TextureFile).Append('"'); else sb.Append("null");
        sb.Append('}');
    }

    private static void AppendFloats(StringBuilder sb, float[] a)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(a[i].ToString("R", CultureInfo.InvariantCulture));
        }
    }

    // ─── Inline preview: toolbar ──────────────────────────────────────

    private async void OnWireChanged(object sender, RoutedEventArgs e) => await ExecAsync($"window.fiveosWorkbench.setWireframe({Js(OptWireToggle.IsChecked)})");
    private async void OnEdgedChanged(object sender, RoutedEventArgs e) => await ExecAsync($"window.fiveosWorkbench.setEdgedFaces({Js(OptEdgedToggle.IsChecked)})");
    private async void OnRecenter(object sender, RoutedEventArgs e) => await ExecAsync("window.fiveosWorkbench.recenter()");

    /// <summary>Optimize ONLY the current LOD: reduce its existing geometry to
    /// the slider ratio and overwrite the file in place. No new LODs are ever
    /// generated, so the file shrinks. After saving, the model is reloaded so
    /// the reduced LOD becomes the new baseline.</summary>
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm?.SelectedItem == null || !_resourceReady) return;
        var lodName = _currentLod;
        var lod = LodOf(lodName);
        float ratio = (float)_ratios[lodName];
        if (_sourceTris <= 0 || ratio >= 0.999f) return;   // button shouldn't be enabled
        var path = vm.SelectedItem.Path;
        var item = vm.SelectedItem;
        var mode = vm.Mode;

        var pick = AppDialog.Show(
            $"Reduce this model's {lodName} LOD to {ratio * 100:F0}% and overwrite the file in place?\n\n" +
            "Only this LOD's geometry is changed — no new LODs are added, so the file shrinks.\n\n" +
            "Back up the original first if you don't have a copy elsewhere.",
            $"Optimize {lodName} LOD",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning,
            Window.GetWindow(this));
        if (pick != System.Windows.MessageBoxResult.OK) return;

        OptSaveButton.IsEnabled = false;
        var origContent = OptSaveButton.Content;
        OptSaveButton.Content = "Saving…";
        try
        {
            await System.Threading.Tasks.Task.Run(() => DrawableLodBuilder.SaveLodReduced(path, lod, ratio, PreserveBoundary));

            // Reload so the now-reduced LOD becomes the baseline (counts + size
            // refresh, slider back to 100%), keeping the user on the same tab.
            _ratios[lodName] = 1.0;
            SyncSliderToLod();
            if (ReferenceEquals(item, _loadedItem))
            {
                _loadCts?.Cancel();
                _loadCts?.Dispose();
                _loadCts = new CancellationTokenSource();
                await LoadAsync(item, path, mode, _loadCts.Token);
            }
            AppDialog.Show($"Saved {lodName} LOD in place.", "Saved",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information, Window.GetWindow(this));
        }
        catch (Exception ex)
        {
            AppDialog.Show($"Save failed: {ex.Message}", "Save failed",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error, Window.GetWindow(this));
        }
        finally
        {
            OptSaveButton.Content = origContent;
            UpdateSaveButton();
        }
    }

    // ─── Inline preview: helpers ──────────────────────────────────────

    private static string Js(bool? b) => b == true ? "true" : "false";

    private async System.Threading.Tasks.Task ExecAsync(string js)
    {
        if (!_viewerReady || OptimizeViewport.CoreWebView2 == null) return;
        try { await OptimizeViewport.CoreWebView2.ExecuteScriptAsync(js); } catch { /* best-effort */ }
    }

    /// <summary>Show a centered message INSIDE the viewer (placeholder /
    /// loading / error). A WPF overlay can't sit over the WebView2 (airspace),
    /// so the empty/status text is rendered by the page via setMessage.</summary>
    private void ShowMessage(string text) => _ = ExecAsync($"window.fiveosWorkbench.setMessage({JsStr(text)})");

    private static string JsStr(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static int ExtractInt(string json, string key)
    {
        var i = json.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return -1;
        i += key.Length;
        while (i < json.Length && (json[i] == ' ' || json[i] == ':')) i++;
        int start = i;
        while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-')) i++;
        return i > start && int.TryParse(json.AsSpan(start, i - start), out var v) ? v : -1;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }
}

/// <summary>One decoded texture shown in the flat gallery (texture modes).
/// <see cref="Image"/> is a frozen thumbnail (null when decode failed);
/// <see cref="Detail"/> is "WxH · FORMAT".</summary>
public sealed class TextureTile
{
    public ImageSource? Image { get; init; }
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
}
