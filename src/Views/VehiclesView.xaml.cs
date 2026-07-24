// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CodeWalker.GameFiles;
using FiveOS.Services;
using FiveOS.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace FiveOS.Views;

public partial class VehiclesView : UserControl
{
    public VehiclesView()
    {
        InitializeComponent();
    }

    private VehiclesViewModel? Vm => DataContext as VehiclesViewModel;

    private bool _busy;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LayersCol.Width = new GridLength(UserSettings.LoadVehiclesLayersWidth());
        PaintCol.Width = new GridLength(UserSettings.LoadVehiclesPaintWidth());
        DetailsRow.Height = new GridLength(UserSettings.LoadVehiclesDetailsHeight());
        SyncMetaModeButtons();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => PersistLayout();

    private void PersistLayout()
    {
        UserSettings.SaveVehiclesLayersWidth(LayersCol.Width.Value);
        UserSettings.SaveVehiclesPaintWidth(PaintCol.Width.Value);
        UserSettings.SaveVehiclesDetailsHeight(DetailsRow.Height.Value);
    }

    private void OnLayersSplitterDragCompleted(object sender, DragCompletedEventArgs e)
        => UserSettings.SaveVehiclesLayersWidth(LayersCol.Width.Value);

    private void OnPaintSplitterDragCompleted(object sender, DragCompletedEventArgs e)
        => UserSettings.SaveVehiclesPaintWidth(PaintCol.Width.Value);

    private void OnDetailsSplitterDragCompleted(object sender, DragCompletedEventArgs e)
        => UserSettings.SaveVehiclesDetailsHeight(DetailsRow.Height.Value);

    private async void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Vm == null) return;
        if (e.NewValue is not VehicleTreeNode node)
        {
            if (ReferenceEquals(sender, FilesTree))
            {
                Vm.ActiveCar = null;
                Vm.ClearMetaEditor();
                SyncMetaModeButtons();
            }
            return;
        }
        var carOwner = FindCarOwner(node);
        if (carOwner != null) Vm.ActiveCar = carOwner;

        if (node.File is { IsMeta: true } meta)
        {
            Vm.OpenMetaEditor(meta);
            SyncMetaModeButtons();
            return;
        }

        Vm.ClearMetaEditor();
        SyncMetaModeButtons();
        if (node.PreviewPath == null) return;
        await InitCarPreviewAsync();
        if (_carViewerReady) await LoadModelAsync(node.PreviewPath);
        else _pendingModelPath = node.PreviewPath;
    }

    /// <summary>Make the queue behave like a normal selectable list: clicking the
    /// already-highlighted car (or empty space) un-highlights it. Clicks on the
    /// expand chevron or the × button are left alone.</summary>
    private void OnQueueMouseDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(src) != null) return;
        if (ReferenceEquals(sender, QueueList))
        {
            if (FindAncestor<ListBoxItem>(src) == null)
            {
                QueueList.SelectedItem = null;
                if (Vm != null) Vm.SelectedQueueItem = null;
            }
            return;
        }
        var tvi = FindAncestor<TreeViewItem>(src);
        if (tvi == null)
        {
            DeselectTree(FilesTree);
        }
        else if (tvi.IsSelected)
        {
            tvi.IsSelected = false;
            e.Handled = true;
        }
    }

    private static void DeselectTree(TreeView tree)
    {
        if (tree.SelectedItem != null && SelectedContainer(tree) is { } c) c.IsSelected = false;
    }

    private static TreeViewItem? SelectedContainer(ItemsControl parent)
    {
        for (int i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem tvi) continue;
            if (tvi.IsSelected) return tvi;
            if (SelectedContainer(tvi) is { } nested) return nested;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private static VehicleTreeNode? FindCarOwner(VehicleTreeNode? node)
    {
        while (node != null)
        {
            if (node.IsCar) return node;
            node = node.Parent;
        }
        return null;
    }

    // ─── Search & sort toolbar ───────────────────────────────────────────

    private void OnSortSize(object sender, RoutedEventArgs e) { if (Vm != null) Vm.SortMode = VehicleSort.Size; }
    private void OnSortType(object sender, RoutedEventArgs e) { if (Vm != null) Vm.SortMode = VehicleSort.Type; }
    private void OnSortDate(object sender, RoutedEventArgs e) { if (Vm != null) Vm.SortMode = VehicleSort.Date; }
    private void OnToggleSortDir(object sender, RoutedEventArgs e) { if (Vm != null) Vm.SortDescending = !Vm.SortDescending; }

    private void OnTreeDoubleClick(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: VehicleTreeNode { File: { IsMeta: true } vf } } && Vm != null)
        {
            Vm.OpenMetaEditor(vf);
            SyncMetaModeButtons();
        }
    }

    private async void OnBrowseInput(object sender, RoutedEventArgs e)
        => await RunBrowseInputFromMenuAsync();

    public async Task RunBrowseInputFromMenuAsync()
    {
        if (Vm == null) return;
        var dlg = new OpenFileDialog
        {
            Title = Vm.MergeIntoPack ? "Add cars to the queue" : "Add a car",
            Filter = "RAGE package (*.rpf)|*.rpf|All files (*.*)|*.*",
            Multiselect = Vm.MergeIntoPack,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog() == true)
            await AddOrSetAsync(dlg.FileNames);
    }

    private Task AddOrSetAsync(IReadOnlyList<string> paths)
    {
        if (Vm == null || paths.Count == 0 || Vm.IsProcessing) return Task.CompletedTask;
        if (Vm.MergeIntoPack) Vm.AddInputs(paths);
        else Vm.SetInputs(paths);
        return Task.CompletedTask;
    }

    private void OnLoadFolder(object sender, RoutedEventArgs e)
    {
        if (Vm == null || Vm.IsProcessing) return;
        var dlg = new OpenFolderDialog
        {
            Title = "Open an existing FiveM car resource folder",
            InitialDirectory = GetDir(Vm.OutputPath)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog() == true)
            Vm.LoadResourceFolder(dlg.FolderName);
    }

    private void OnNewTemplate(object sender, RoutedEventArgs e)
    {
        if (Vm == null || Vm.IsProcessing) return;
        var name = string.IsNullOrWhiteSpace(Vm.PackName) ? "car_pack" : Vm.PackName.Trim();
        var rename = new RenameDialog("New template", "Pack / resource name:", name, isFile: false,
            hint: "Creates stream/, data/ (meta stubs), audio/sfx/, fxmanifest.lua, and vehicle_names.lua.")
        { Owner = Window.GetWindow(this) };
        if (rename.ShowDialog() != true) return;

        var parent = GetDir(Vm.OutputPath)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads";
        var folderDlg = new OpenFolderDialog
        {
            Title = "Where should the empty pack be created?",
            InitialDirectory = Directory.Exists(parent) ? parent
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (folderDlg.ShowDialog() != true) return;

        var err = Vm.CreateTemplate(folderDlg.FolderName, rename.ResultName);
        if (err != null)
            AppDialog.Show(err, "New template", MessageBoxButton.OK, MessageBoxImage.Warning, Window.GetWindow(this));
    }

    private void OnMetaModeForm(object sender, RoutedEventArgs e)
    {
        Vm?.SetMetaEditorMode(false);
        SyncMetaModeButtons();
    }

    private void OnMetaModeRaw(object sender, RoutedEventArgs e)
    {
        Vm?.SetMetaEditorMode(true);
        SyncMetaModeButtons();
    }

    private void SyncMetaModeButtons()
    {
        if (Vm == null || MetaModeFormBtn == null || MetaModeRawBtn == null) return;
        var form = Vm.IsMetaFormMode;
        MetaModeFormBtn.Appearance = form
            ? Wpf.Ui.Controls.ControlAppearance.Primary
            : Wpf.Ui.Controls.ControlAppearance.Secondary;
        MetaModeRawBtn.Appearance = form
            ? Wpf.Ui.Controls.ControlAppearance.Secondary
            : Wpf.Ui.Controls.ControlAppearance.Primary;
    }

    private void OnMetaImport(object sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedMetaFile == null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Import meta into form",
            Filter = "Meta / XML (*.meta;*.xml)|*.meta;*.xml|All files (*.*)|*.*",
            Multiselect = false,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Vm.ImportMetaText(File.ReadAllText(dlg.FileName), Path.GetFileName(dlg.FileName));
            SyncMetaModeButtons();
        }
        catch (Exception ex)
        {
            Vm.MetaStatusNote = "Import failed: " + ex.Message;
        }
    }

    private void OnMetaReset(object sender, RoutedEventArgs e)
    {
        Vm?.ReloadMetaFromDisk();
        SyncMetaModeButtons();
    }

    private void OnMetaSave(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var err = Vm.SaveMetaEditor();
        if (err != null) Vm.MetaStatusNote = err;
        SyncMetaModeButtons();
    }

    private void OnRemoveCar(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CarInput ci }) Vm?.RemoveInput(ci);
    }

    private void OnRemoveCarNode(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VehicleTreeNode { IsCar: true } node } || Vm == null) return;
        if (Vm.IsProcessing) return;
        if (!Vm.RemoveCarByModel(node.Name)) return;
        _loadedModelPath = null;
        _pendingModelPath = null;
    }

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var dlg = new OpenFolderDialog
        {
            Title = "Pick the output folder",
            InitialDirectory = GetDir(Vm.OutputPath)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog() == true) Vm.SetOutputFolder(dlg.FolderName);
    }

    private static string? GetDir(string path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path); }
        catch { return null; }
    }

    private async void OnConvert(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        await Vm.ConvertAsync();
        var preview = Vm.ActiveCar?.PreviewPath;
        if (string.IsNullOrWhiteSpace(preview)) return;
        await InitCarPreviewAsync();
        if (_carViewerReady) await LoadModelAsync(preview);
        else _pendingModelPath = preview;
    }

    private void OnCancelConvert(object sender, RoutedEventArgs e) => Vm?.RequestCancel();

    private async void OnImportFromUrl(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        if (FiveOS.Services.Net.LikelyOffline())
        { AppDialog.NoInternet("Importing a car from a gta5-mods link", Window.GetWindow(this)); return; }
        await Vm.ImportFromUrlAsync();
    }

    private void OnOpenOutput(object sender, RoutedEventArgs e)
    {
        var path = Vm?.LastOutputPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        OpenInExplorer(path);
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { /* explorer failure is non-fatal */ }
    }

    // ─── Drag-drop ───────────────────────────────────────────────────────

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedInputs(e).Count > 0
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (GetDroppedInputs(e).Count > 0) DropOverlay.Visibility = Visibility.Visible;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var inputs = GetDroppedInputs(e);
        if (inputs.Count == 0 || Vm == null) return;
        await AddOrSetAsync(inputs);
    }

    /// <summary>Every dropped path that is a directory or a .rpf file —
    /// several at once = a multipack.</summary>
    private static List<string> GetDroppedInputs(DragEventArgs e)
    {
        var list = new List<string>();
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return list;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return list;
        foreach (var p in paths)
            if (Directory.Exists(p) || (File.Exists(p) && p.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)))
                list.Add(p);
        return list;
    }

    // ═══════════════ File tree — right-click actions ═══════════════

    /// <summary>The VehicleFile a menu was opened on, if the node is a file.</summary>
    private static VehicleFile? NodeFile(object sender)
        => sender is MenuItem { DataContext: VehicleTreeNode node } ? node.File : null;

    private void OnOpenFileFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: VehicleTreeNode node }) return;
        // A file leaf → reveal the file; a car/branch → open its folder.
        if (node.File != null) OpenInExplorer(node.File.FullPath);
        else if (node.PreviewPath != null) OpenInExplorer(Path.GetDirectoryName(node.PreviewPath) ?? "");
        else OpenInExplorer(Vm?.LastOutputPath ?? "");
    }

    private void OnEditXml(object sender, RoutedEventArgs e)
    {
        if (NodeFile(sender) is not { } vf || Vm == null) return;
        Vm.OpenMetaEditor(vf);
        SyncMetaModeButtons();
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: VehicleTreeNode node } || Vm == null) return;

        if (node.File is { } vf)   // a single file
        {
            var dlg = new RenameDialog("Rename file", "New file name:", vf.Name, isFile: true,
                hint: "Renaming a model/texture on its own can break the vehicle — rename the whole car instead to keep it working.")
            { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            var err = Vm.RenameFile(vf, dlg.ResultName);
            Vm.PopulateFiles();
            Vm.Summary = err == null ? $"Renamed to {dlg.ResultName}." : "Rename failed: " + err;
        }
        else if (node.IsCar)   // the whole car
        {
            var dlg = new RenameDialog("Rename car", "New spawn name:", node.Name, isFile: false,
                hint: "Renames the car's models + textures AND updates the spawn-name references in the metas (vehicles / handling / carcols). Lowercase, no spaces.")
            { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            var err = Vm.RenameCar(node.Name, dlg.ResultName);
            // The previewed model may have moved — drop the stale path.
            if (err == null) { _loadedModelPath = null; _pendingModelPath = null; }
            Vm.PopulateFiles();
            Vm.Summary = err == null
                ? $"Renamed car to {dlg.ResultName}. Restart the resource on your server to apply."
                : "Rename failed: " + err;
        }
    }

    // ─── Optimize (single file) ──────────────────────────────────────────

    private async void OnOptimizeFile(object sender, RoutedEventArgs e)
    {
        if (NodeFile(sender) is not { } vf || Vm == null) return;
        if (!vf.CanOptimize || _busy || Vm.IsProcessing) return;
        await OptimizeFilesAsync(new[] { vf }, $"“{vf.Name}”");
    }

    private async void OnOptimizeAll(object sender, RoutedEventArgs e)
    {
        if (Vm == null || _busy || Vm.IsProcessing) return;
        var targets = Vm.Files.Where(f => f.CanOptimize).ToList();
        if (targets.Count == 0) { Vm.Summary = "Nothing to optimize — no models or textures in this resource."; return; }

        var pick = AppDialog.Show(
            $"Optimize all {targets.Count} model(s) and texture(s) in this resource and overwrite them in place?\n\n" +
            "The originals in the converted resource are replaced — reconvert from the source mod if you need them back.",
            "Optimize all files",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, Window.GetWindow(this));
        if (pick != MessageBoxResult.OK) return;

        await OptimizeFilesAsync(targets, $"all {targets.Count} file(s)");
    }

    /// <summary>Decimate every model / compress every texture in
    /// <paramref name="files"/> in place, using the sidebar's Optimize
    /// Settings, then refresh the list so the new sizes show. Only files that
    /// actually shrank (or reported work) are counted as optimized — a file
    /// already below the thresholds is reported as "unchanged", not a save.</summary>
    private async Task OptimizeFilesAsync(IReadOnlyList<VehicleFile> files, string label)
    {
        if (Vm == null) return;
        _busy = true;
        Vm.IsProcessing = true;
        Vm.Summary = $"Optimizing {label}…";

        double ratio = Vm.KeepRatio;
        bool pb = Vm.PreserveBoundary;
        bool shrink = TextureDownsizeCheck.IsChecked == true;

        // Same texture logic the Optimize tab uses when it actually shrinks:
        // a real MaxSize resolution CAP (repeated-halve down to the ceiling),
        // not the single-halve that left most vehicle textures untouched. Cap
        // the longest side at 1024 when "Shrink huge textures" is on — the
        // standard FiveM vehicle target — and process anything ≥1K (W+H ≥ 2048).
        int maxSize = shrink ? 1024 : 0;
        ushort texThreshold = 2048;

        var drawOpts = new DrawableOptimizer.Options(
            TargetRatio: ratio,
            PreserveBoundary: pb,
            OptimizeEmbeddedTextures: true,
            TextureDownsize: shrink,
            TextureFormatOptimization: false,
            TextureSizeThreshold: texThreshold,
            TextureMaxSize: maxSize);
        var ytdOpts = new YtdOptimizer.Options(
            DownSize: shrink,
            FormatOptimization: false,
            OptimizeSizeThreshold: texThreshold,
            OnlyOversized: false,
            BackupRoot: null,
            MaxSize: maxSize);

        long before = 0, after = 0;
        int changed = 0, unchanged = 0, failed = 0;
        string? firstError = null;
        try
        {
            (before, after, changed, unchanged, failed, firstError) = await Task.Run(() =>
            {
                long b = 0, a = 0; int chg = 0, same = 0, bad = 0; string? err = null;
                var drawable = new DrawableOptimizer();
                foreach (var f in files)
                {
                    if (!File.Exists(f.FullPath)) { bad++; continue; }
                    long fb = new FileInfo(f.FullPath).Length;
                    try
                    {
                        var ext = Path.GetExtension(f.FullPath).ToLowerInvariant();
                        bool didWork;
                        switch (ext)
                        {
                            case ".yft":
                            case ".ydr":
                            case ".ydd":
                                var dr = ext switch
                                {
                                    ".yft" => drawable.OptimizeYft(f.FullPath, f.FullPath, drawOpts),
                                    ".ydr" => drawable.OptimizeYdr(f.FullPath, f.FullPath, drawOpts),
                                    _ => drawable.OptimizeYdd(f.FullPath, f.FullPath, drawOpts),
                                };
                                if (dr.Error != null) { bad++; err ??= dr.Error; continue; }
                                didWork = dr.TrianglesAfter < dr.TrianglesBefore
                                          || dr.VerticesAfter < dr.VerticesBefore
                                          || dr.TexturesOptimized > 0;
                                break;
                            case ".ytd":
                                var yr = YtdOptimizer.OptimizeFile(f.FullPath, ytdOpts);
                                if (yr.Error != null) { bad++; err ??= yr.Error; continue; }
                                // A same-format re-encode counts textures as
                                // "changed" even when bytes don't drop; only
                                // treat it as a real optimize if the file shrank.
                                didWork = yr.Changed && new FileInfo(f.FullPath).Length < fb;
                                break;
                            default:
                                continue;
                        }

                        long fa = new FileInfo(f.FullPath).Length;
                        if (didWork) { b += fb; a += fa; chg++; }
                        else { same++; }
                    }
                    catch (Exception ex) { bad++; err ??= ex.Message; }
                }
                return (b, a, chg, same, bad, err);
            });
        }
        catch (Exception ex)
        {
            firstError = ex.Message;
            changed = 0;
        }
        finally
        {
            // Refresh the list (new sizes) and release the busy state in a
            // finally so a throw here can never brick the tab — a stuck
            // IsProcessing disables Convert forever.
            try { Vm.PopulateFiles(); } catch { /* enumeration errors are non-fatal */ }
            Vm.IsProcessing = false;
            _busy = false;
        }

        // Reload the preview so the optimized model (lower tri count / smaller
        // textures) is visibly reflected — the clearest "it worked" signal.
        if (_carViewerReady && _loadedModelPath != null
            && files.Any(f => string.Equals(f.FullPath, _loadedModelPath, StringComparison.OrdinalIgnoreCase)))
            await LoadModelAsync(_loadedModelPath);

        if (changed == 0)
        {
            Vm.Summary = failed > 0
                ? $"Optimize failed — {failed} file(s) errored{(firstError != null ? $": {firstError}" : "")}."
                : firstError != null
                    ? "Optimize failed: " + firstError
                    : $"Nothing to shrink — {unchanged} file(s) are already optimal (small textures / low-poly).";
            return;
        }
        var saved = before > 0 ? (1 - (double)after / before) * 100 : 0;
        var extra = (unchanged > 0 ? $", {unchanged} already optimal" : "")
                    + (failed > 0 ? $", {failed} failed" : "");
        Vm.Summary = $"Optimized {changed} file(s){extra} — "
            + $"{VehiclesViewModel.HumanBytes(before)} → {VehiclesViewModel.HumanBytes(after)} ({saved:F0}% smaller). Sizes updated in the list.";
    }

    // ═══════════════ Small 3D preview ═══════════════
    // Renders the SELECTED .yft in a compact WebView2 (viewer.html workbench),
    // with materials classified from the RAGE shader (painted body, glass,
    // emissive lights, decals) — no more black bodies. Shared vehshare textures
    // load from the user's GTA install when configured.

    private bool _carWebReady;
    private bool _carViewerReady;
    private string? _carSessionDir;
    private DrawableMeshExtractor? _carExtractor;
    private VehicleMeshBuilder? _carMeshBuilder;
    private string? _pendingModelPath;
    private string? _loadedModelPath;
    private bool _sharedTexKicked;

    private async Task InitCarPreviewAsync()
    {
        if (_carWebReady) return;
        try
        {
            var userDataDir = Path.Combine(Path.GetTempPath(), "FiveOS", "WebView2-CarPreview");
            Directory.CreateDirectory(userDataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await CarPreview.EnsureCoreWebView2Async(env);

            _carSessionDir = Path.Combine(Path.GetTempPath(), "FiveOS", "CarPrev-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_carSessionDir);
            CopyDirectory(RuntimeAssets.ViewerDir, _carSessionDir);
            _carExtractor = new DrawableMeshExtractor(_carSessionDir);
            _carMeshBuilder = new VehicleMeshBuilder(_carExtractor);

            CarPreview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "carprev-viewer.local", _carSessionDir, CoreWebView2HostResourceAccessKind.Allow);
            WebViewDialogs.Theme(CarPreview.CoreWebView2);
            CarPreview.CoreWebView2.WebMessageReceived += OnCarViewerMessage;
            CarPreview.Source = new Uri("https://carprev-viewer.local/viewer.html");
            _carWebReady = true;
        }
        catch (Exception ex)
        {
            CarMeshInfoText.Text = $"Preview failed to start: {ex.Message}";
        }
    }

    /// <summary>Free the car-preview Edge process and delete its session
    /// viewer copy. Call from MainWindow.OnClosed.</summary>
    public void Teardown()
    {
        try
        {
            if (CarPreview?.CoreWebView2 != null)
                CarPreview.CoreWebView2.WebMessageReceived -= OnCarViewerMessage;
        }
        catch { /* */ }

        try { CarPreview?.Dispose(); } catch { /* already gone */ }
        _carWebReady = false;
        _carViewerReady = false;

        if (!string.IsNullOrEmpty(_carSessionDir))
        {
            try
            {
                if (Directory.Exists(_carSessionDir))
                    Directory.Delete(_carSessionDir, recursive: true);
            }
            catch { /* CacheService sweeps leftovers */ }
            _carSessionDir = null;
        }

        try { GameTextureCache.Reset(null); } catch { /* optional */ }
    }

    private void OnCarViewerMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.WebMessageAsJson; } catch { return; }
        if (json.Contains("\"ready\"") && !_carViewerReady)
        {
            _carViewerReady = true;
            Dispatcher.Invoke(() =>
            {
                if (_pendingModelPath != null) _ = LoadModelAsync(_pendingModelPath);
            });
        }
    }

    private async Task LoadModelAsync(string yftPath)
    {
        _pendingModelPath = null;
        _loadedModelPath = yftPath;
        if (!File.Exists(yftPath)) return;
        var name = Path.GetFileName(yftPath);
        PreviewHint.Visibility = Visibility.Collapsed;
        SetPreviewLoader(true, "Loading model…");
        DoorsToggle.IsChecked = false;

        // Kick off the one-time GTA shared-texture (vehshare) load so textured
        // trim/interior parts show real textures; reloads when it lands.
        EnsureSharedTextures();
        bool pb = Vm?.PreserveBoundary ?? true;
        var shared = GameTextureCache.Status == GameTextureCache.State.Ready
            ? GameTextureCache.SharedTextures : null;

        VehicleMeshBuilder.BuildResult r;
        try
        {
            r = await Task.Run(() => _carMeshBuilder!.Build(yftPath, frameCamera: true, 1f, pb, shared));
        }
        catch (Exception ex)
        {
            if (_loadedModelPath != yftPath) return;
            SetPreviewLoader(false);
            CarMeshInfoText.Text = $"{name}: {ex.Message}";
            await CarExecAsync($"window.fiveosWorkbench.setMessage({JsStr($"Couldn't preview {name}")})");
            return;
        }
        if (_loadedModelPath != yftPath) return;   // selection moved on
        await CarExecAsync($"window.fiveosWorkbench.loadLod({r.PayloadJson})");
        PopulateParts(r.Groups);

        // Keep the loader up while GTA's shared textures load — the car reloads
        // fully-textured when they land. Otherwise reveal the model now.
        if (GameTextureCache.Status == GameTextureCache.State.Loading)
            SetPreviewLoader(true, "Loading textures…");
        else
            SetPreviewLoader(false);

        string texNote = GameTextureCache.Status is GameTextureCache.State.NotConfigured
                         && r.TotalParts > 0 && r.TexturedParts < r.TotalParts / 2
            ? " · set your GTA folder for shared textures →" : "";
        CarMeshInfoText.Text = $"{name} · {r.Tris:N0} tris — drag to orbit, wheel to zoom{texNote}";
        Vm?.SetPreviewPolygonCount(r.Tris);
    }

    private void SetPreviewLoader(bool on, string text = "")
    {
        PreviewLoaderText.Text = text;
        PreviewLoader.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Mods controls: open doors + toggle parts/extras ──────────────────

    private void OnDoorsToggled(object sender, RoutedEventArgs e)
        => _ = CarExecAsync($"window.fiveosWorkbench.setDoorsOpen({(DoorsToggle.IsChecked == true ? "true" : "false")})");

    private void OnPaintColor(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex } && hex.Length == 6)
            _ = CarExecAsync($"window.fiveosWorkbench.setPaint(0x{hex})");
    }

    private void PopulateParts(List<VehicleMeshBuilder.GroupInfo> groups)
    {
        bool canDoors = groups.Any(g => g.Kind is "door_l" or "door_r" or "bonnet" or "boot");
        DoorsToggle.IsEnabled = canDoors;
        DoorsToggle.ToolTip = canDoors
            ? "Swing the doors / bonnet / boot open."
            : "This model has no separate door meshes — doors are welded into the body.";
        DoorsHint.Text = canDoors
            ? "Toggle to swing doors, bonnet, and boot."
            : "No separate doors on this model (baked into the chassis). Try the _hi.yft if you picked the low LOD.";

        PartsList.Items.Clear();
        foreach (var g in groups.OrderBy(g => g.Kind).ThenBy(g => g.Name))
        {
            var cb = new CheckBox
            {
                Content = g.Name,
                IsChecked = true,
                FontSize = 11,
                Margin = new Thickness(0, 1, 0, 1),
            };
            cb.Checked += (_, _) => _ = CarExecAsync($"window.fiveosWorkbench.setGroupVisible({JsStr(g.Name)}, true)");
            cb.Unchecked += (_, _) => _ = CarExecAsync($"window.fiveosWorkbench.setGroupVisible({JsStr(g.Name)}, false)");
            PartsList.Items.Add(cb);
        }
        if (PartsList.Items.Count == 0)
            PartsList.Items.Add(new TextBlock { Text = "(no separate parts on this model)", FontSize = 11, Opacity = 0.6 });
    }

    private void EnsureSharedTextures()
    {
        if (_sharedTexKicked || GameTextureCache.Status == GameTextureCache.State.Ready) return;
        if (GameTextureCache.Status is GameTextureCache.State.NotConfigured or GameTextureCache.State.Failed
            && !GtaInstall.IsValidFolder(GtaInstall.Resolve()))
            return;   // nothing to load from — the "Set GTA folder" button covers this
        _sharedTexKicked = true;
        _ = GameTextureCache.EnsureLoadedAsync().ContinueWith(t =>
        {
            if (t.Result)
                Dispatcher.Invoke(() => { if (_loadedModelPath != null) _ = LoadModelAsync(_loadedModelPath); });
        }, TaskScheduler.Default);
    }

    private async void OnSetGtaFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Pick your GTA V folder (the one with GTA5.exe)",
            InitialDirectory = GameTextureCache.GtaFolder
                ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        if (dlg.ShowDialog() != true) return;
        if (!GtaInstall.IsValidFolder(dlg.FolderName))
        {
            CarMeshInfoText.Text = "That folder has no GTA5.exe — pick the game's install folder.";
            return;
        }
        UserSettings.SaveGtaFolder(dlg.FolderName);
        GameTextureCache.Reset(dlg.FolderName);
        _sharedTexKicked = false;
        SetPreviewLoader(true, "Loading textures…");
        bool ok = await GameTextureCache.EnsureLoadedAsync(dlg.FolderName);
        if (ok && _loadedModelPath != null) await LoadModelAsync(_loadedModelPath);
        else SetPreviewLoader(false);
    }

    private void OnCarRecenter(object sender, RoutedEventArgs e)
        => _ = CarExecAsync("window.fiveosWorkbench.recenter && window.fiveosWorkbench.recenter()");

    private async Task CarExecAsync(string js)
    {
        if (!_carWebReady || CarPreview.CoreWebView2 == null) return;
        try { await CarPreview.CoreWebView2.ExecuteScriptAsync(js); } catch { /* best-effort */ }
    }

    private static string JsStr(string s)
        => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(src, dst));
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(src, dst), overwrite: true);
    }

}
