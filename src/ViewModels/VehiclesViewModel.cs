// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using FiveOS.Services;

namespace FiveOS.ViewModels;

/// <summary>One file in the converted resource — drives the middle file
/// list. The Kind flags gate the right-click "Optimize" / "Edit XML" menu.</summary>
public sealed class VehicleFile
{
    public string Name { get; init; } = "";
    public string Folder { get; init; } = "";     // stream / data / audio / (root)
    public string Type { get; init; } = "";        // YFT / YTD / META / …
    public string SizeText { get; init; } = "";
    public long Bytes { get; init; }
    public System.DateTime Modified { get; init; }  // last write time, for sorting
    public string FullPath { get; init; } = "";
    public bool CanOptimizeGeometry { get; init; } // .yft / .ydr / .ydd
    public bool CanOptimizeTexture { get; init; }  // .ytd
    public bool IsMeta { get; init; }              // .meta / .xml
    public bool CanOptimize => CanOptimizeGeometry || CanOptimizeTexture;

    /// <summary>Plain-English "what is this file" for the list — beginners
    /// don't know what a .yft is.</summary>
    public string Kind => Name.Equals("fxmanifest.lua", StringComparison.OrdinalIgnoreCase)
            ? "FiveM manifest"
        : Type switch
        {
            "YFT" or "YDR" or "YDD" => "3D model",
            "YTD"                    => "Textures",
            "META" or "XML"          => "Settings (XML)",
            "AWC"                    => "Sounds",
            "REL" or "DAT" or "NAMETABLE" => "Audio config",
            "LUA"                    => "Script",
            ""                       => "File",
            _                        => Type + " file",
        };
}

/// <summary>How the middle files list is ordered.</summary>
public enum VehicleSort { Size, Type, Date, Name }

/// <summary>One car queued for a car pack — a display name and its input path
/// (dlc.rpf or folder).</summary>
public sealed record CarInput(string Name, string Path);

/// <summary>A node in the resource file TREE: a car branch (🚗, groups its
/// files), the shared "Data &amp; config" branch (📁), or a file leaf.
/// Selecting a car or a .yft leaf previews it.</summary>
public sealed class VehicleTreeNode
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string SizeText { get; set; } = "";
    public Wpf.Ui.Controls.SymbolRegular Icon { get; init; }
    public bool IsCar { get; init; }
    public bool IsExpanded { get; set; } = true;
    public VehicleFile? File { get; init; }
    public string? PreviewPath { get; init; }
    public VehicleTreeNode? Parent { get; set; }
    public System.Collections.ObjectModel.ObservableCollection<VehicleTreeNode> Children { get; } = new();

    public bool CanOptimizeNode => File?.CanOptimize ?? false;
    public bool IsMetaNode => File?.IsMeta ?? false;
    public bool IsBranch => File == null;
    /// <summary>A file leaf, or a whole car — both are renameable. The shared
    /// "Data &amp; config" branch is not.</summary>
    public bool CanRename => File != null || IsCar;

    /// <summary>True for a CAR that still carries a meaningful amount of
    /// optimizable data (textures + models) — drives the "unoptimized" badge in
    /// the pack list. A well-optimized car (textures capped, geometry decimated)
    /// falls below the threshold. Computed at node-build time, so it refreshes
    /// whenever the file tree is rebuilt (after a convert or an optimize re-scan).</summary>
    public bool IsUnoptimized => IsCar && OptimizableBytes(this) >= UnoptimizedThresholdBytes;

    private const long UnoptimizedThresholdBytes = 8L * 1024 * 1024;   // ~8 MB of textures/models

    private static long OptimizableBytes(VehicleTreeNode node)
    {
        long sum = 0;
        foreach (var child in node.Children)
        {
            if (child.File is { CanOptimize: true } f) sum += f.Bytes;
            if (child.Children.Count > 0) sum += OptimizableBytes(child);
        }
        return sum;
    }

    /// <summary>Per-type icon so the tree is scannable at a glance.</summary>
    public static Wpf.Ui.Controls.SymbolRegular IconFor(VehicleFile f)
        => f.CanOptimizeGeometry ? Wpf.Ui.Controls.SymbolRegular.Cube24
         : f.CanOptimizeTexture ? Wpf.Ui.Controls.SymbolRegular.Image24
         : f.IsMeta ? Wpf.Ui.Controls.SymbolRegular.Settings24
         : f.Name.Equals("fxmanifest.lua", StringComparison.OrdinalIgnoreCase) ? Wpf.Ui.Controls.SymbolRegular.DocumentJavascript24
         : Wpf.Ui.Controls.SymbolRegular.Document24;
}

/// <summary>
/// Backs the "Vehicles" tab — convert singleplayer add-on cars (dlc.rpf / mod
/// folder / gta5-mods link) into ready-to-ensure FiveM resources, then browse
/// the produced files in a detail list: right-click a model to optimize
/// (geometry decimate or texture compress, the same engine as the Optimize
/// tab) or a meta to edit its XML.
///
/// Follows the repo's no-DI convention: constructed in MainViewModel with an
/// <c>Action&lt;string&gt;</c> status callback; user actions are public methods
/// called from the view's code-behind rather than ICommands.
/// </summary>
public partial class VehiclesViewModel : ObservableObject
{
    private readonly Action<string> _setStatus;

    public VehiclesViewModel(Action<string> setStatus) => _setStatus = setStatus;

    public ObservableCollection<RpfFileRow> Rows { get; } = new();
    public ObservableCollection<VehicleFile> Files { get; } = new();
    /// <summary>LEFT car-pack queue — CARS ONLY (each a 🚗 branch, collapsed).</summary>
    public ObservableCollection<VehicleTreeNode> CarNodes { get; } = new();
    /// <summary>MIDDLE detail — the highlighted car's files + the shared
    /// "Data &amp; config" branch (metas/manifest).</summary>
    public ObservableCollection<VehicleTreeNode> MiddleNodes { get; } = new();
    private VehicleTreeNode? _dataConfig;   // the shared metas/manifest branch
    public bool HasFiles => Files.Count > 0;

    /// <summary>The car (or branch) whose files show in the MIDDLE detail panel.
    /// Set by selecting a car in the queue; auto = the single car in single mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFilesSection))]
    [NotifyPropertyChangedFor(nameof(ActiveCarName))]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    [NotifyPropertyChangedFor(nameof(DetailTitle))]
    [NotifyPropertyChangedFor(nameof(DetailSubtitle))]
    [NotifyPropertyChangedFor(nameof(DetailStats))]
    [NotifyPropertyChangedFor(nameof(DetailSource))]
    private VehicleTreeNode? _activeCar;

    public string ActiveCarName => ActiveCar?.Name ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    [NotifyPropertyChangedFor(nameof(DetailTitle))]
    [NotifyPropertyChangedFor(nameof(DetailSubtitle))]
    [NotifyPropertyChangedFor(nameof(DetailStats))]
    [NotifyPropertyChangedFor(nameof(DetailSource))]
    private CarInput? _selectedQueueItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMetaEditor))]
    [NotifyPropertyChangedFor(nameof(ShowCarDetails))]
    [NotifyPropertyChangedFor(nameof(MetaSupportsForm))]
    [NotifyPropertyChangedFor(nameof(ShowMetaVehicles))]
    [NotifyPropertyChangedFor(nameof(ShowMetaHandling))]
    [NotifyPropertyChangedFor(nameof(ShowMetaCarvariations))]
    [NotifyPropertyChangedFor(nameof(MetaFormVisible))]
    [NotifyPropertyChangedFor(nameof(MetaRawVisible))]
    private VehicleFile? _selectedMetaFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetaFormVisible))]
    [NotifyPropertyChangedFor(nameof(MetaRawVisible))]
    [NotifyPropertyChangedFor(nameof(IsMetaFormMode))]
    [NotifyPropertyChangedFor(nameof(IsMetaRawMode))]
    private bool _metaRawMode;

    [ObservableProperty] private string _metaRawText = "";
    [ObservableProperty] private string _metaTitle = "";
    [ObservableProperty] private string _metaStatusNote = "";
    [ObservableProperty] private VehicleMetaKind _metaKind = VehicleMetaKind.Other;

    [ObservableProperty] private string _metaModelName = "";
    [ObservableProperty] private string _metaGameName = "";
    [ObservableProperty] private string _metaVehicleMake = "";
    [ObservableProperty] private string _metaVehicleClass = "VC_SEDAN";
    [ObservableProperty] private string _metaHandlingId = "";
    [ObservableProperty] private string _metaFrequency = "100";
    [ObservableProperty] private string _metaFlags = "";
    [ObservableProperty] private string _metaMass = "";
    [ObservableProperty] private string _metaInitialDriveForce = "";
    [ObservableProperty] private string _metaBrakeForce = "";
    [ObservableProperty] private string _metaTractionCurveMax = "";
    [ObservableProperty] private string _metaTractionCurveMin = "";
    [ObservableProperty] private string _metaTopSpeedKph = "";
    [ObservableProperty] private string _metaDownforceModifier = "";
    [ObservableProperty] private string _metaColor1 = "0";
    [ObservableProperty] private string _metaColor2 = "0";
    [ObservableProperty] private string _metaPearlescent = "0";
    [ObservableProperty] private string _metaKits = "";

    public bool ShowMetaEditor => SelectedMetaFile != null;
    public bool ShowCarDetails => SelectedMetaFile == null;
    public bool MetaSupportsForm => VehicleMetaFormService.SupportsForm(MetaKind);
    public bool ShowMetaVehicles => MetaKind == VehicleMetaKind.Vehicles;
    public bool ShowMetaHandling => MetaKind == VehicleMetaKind.Handling;
    public bool ShowMetaCarvariations => MetaKind == VehicleMetaKind.Carvariations;
    public bool IsMetaFormMode => !MetaRawMode && MetaSupportsForm;
    public bool IsMetaRawMode => MetaRawMode || !MetaSupportsForm;
    public bool MetaFormVisible => ShowMetaEditor && IsMetaFormMode;
    public bool MetaRawVisible => ShowMetaEditor && IsMetaRawMode;

    [ObservableProperty] private string _statCars = "—";
    [ObservableProperty] private string _statSizeText = "—";
    [ObservableProperty] private string _statPolygonsText = "—";
    [ObservableProperty] private string _statUnoptimized = "—";
    [ObservableProperty] private string _statOptimized = "—";

    public bool HasDetail => ActiveCar != null || SelectedQueueItem != null;

    public string DetailTitle => ActiveCar?.Name ?? SelectedQueueItem?.Name ?? "";

    public string DetailSubtitle => ActiveCar != null
        ? (HasFiles ? "Imported" : "Queued")
        : SelectedQueueItem != null ? "Queued — waiting for Import" : "";

    public string DetailStats
    {
        get
        {
            if (ActiveCar != null)
            {
                long bytes = 0;
                int n = 0;
                foreach (var f in EnumerateFileNodes(ActiveCar))
                {
                    n++;
                    bytes += f.File!.Bytes;
                }
                var size = n == 0 ? "—" : HumanBytes(bytes);
                var files = n == 1 ? "1 file" : $"{n} files";
                return ActiveCar.IsUnoptimized ? $"{files} · {size} · needs optimize" : $"{files} · {size}";
            }
            return SelectedQueueItem != null ? "In queue" : "";
        }
    }

    private static IEnumerable<VehicleTreeNode> EnumerateFileNodes(VehicleTreeNode root)
    {
        foreach (var child in root.Children)
        {
            if (child.File != null) yield return child;
            else foreach (var nested in EnumerateFileNodes(child)) yield return nested;
        }
    }

    public string DetailSource
    {
        get
        {
            if (SelectedQueueItem != null) return SelectedQueueItem.Path;
            if (ActiveCar?.Name is { } model)
            {
                for (int i = 0; i < LastCarSources.Count && i < Queue.Count; i++)
                    if (LastCarSources[i].Models.Any(m => m.Equals(model, StringComparison.OrdinalIgnoreCase)))
                        return Queue[i].Path;
                var q = Queue.FirstOrDefault(c =>
                    c.Name.Contains(model, StringComparison.OrdinalIgnoreCase)
                    || model.Contains(c.Name, StringComparison.OrdinalIgnoreCase));
                if (q != null) return q.Path;
            }
            return "";
        }
    }

    public bool ShowPendingQueue => !HasFiles && HasQueue;
    public bool ShowLayersTree => HasFiles;
    public bool ShowLayersEmpty => !HasFiles && !HasQueue;

    partial void OnActiveCarChanged(VehicleTreeNode? value)
    {
        if (value != null) SelectedQueueItem = null;
        RebuildMiddle();
    }

    partial void OnSelectedQueueItemChanged(CarInput? value)
    {
        if (value != null) ActiveCar = null;
    }

    // ─── Search & sort (the middle files list) ───────────────────────────

    /// <summary>Filter the middle list by file name (live as you type).</summary>
    [ObservableProperty] private string _fileSearch = "";
    partial void OnFileSearchChanged(string value) => RebuildMiddle();

    /// <summary>Sort the middle list by size / type / date-modified / name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirLabel))]
    [NotifyPropertyChangedFor(nameof(SortBySize))]
    [NotifyPropertyChangedFor(nameof(SortByType))]
    [NotifyPropertyChangedFor(nameof(SortByDate))]
    private VehicleSort _sortMode = VehicleSort.Size;
    partial void OnSortModeChanged(VehicleSort value) => RebuildMiddle();

    /// <summary>Descending (largest / newest / Z→A) first. Defaults to true so
    /// "biggest files first" is the out-of-the-box view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirLabel))]
    private bool _sortDescending = true;
    partial void OnSortDescendingChanged(bool value) => RebuildMiddle();

    public bool SortBySize => SortMode == VehicleSort.Size;
    public bool SortByType => SortMode == VehicleSort.Type;
    public bool SortByDate => SortMode == VehicleSort.Date;

    /// <summary>Human label for the current direction, tuned to the sort field.</summary>
    public string SortDirLabel => SortMode switch
    {
        VehicleSort.Size => SortDescending ? "Biggest ↓" : "Smallest ↑",
        VehicleSort.Date => SortDescending ? "Newest ↓"  : "Oldest ↑",
        _                => SortDescending ? "Z–A ↓"     : "A–Z ↑",
    };

    private void RebuildMiddle()
    {
        MiddleNodes.Clear();
        if (ActiveCar == null) return;
        foreach (var leaf in OrderLeaves(EnumerateFileNodes(ActiveCar)))
            MiddleNodes.Add(leaf);
    }

    /// <summary>Apply the live search filter and current sort to a set of file
    /// leaves (branch nodes are ignored).</summary>
    private IEnumerable<VehicleTreeNode> OrderLeaves(IEnumerable<VehicleTreeNode> leaves)
    {
        var q = leaves.Where(n => n.File != null);
        var s = (FileSearch ?? "").Trim();
        if (s.Length > 0)
            q = q.Where(n => n.File!.Name.Contains(s, StringComparison.OrdinalIgnoreCase));
        bool d = SortDescending;
        return SortMode switch
        {
            VehicleSort.Size => d ? q.OrderByDescending(n => n.File!.Bytes)
                                  : q.OrderBy(n => n.File!.Bytes),
            VehicleSort.Type => (d ? q.OrderByDescending(n => n.File!.Kind, StringComparer.OrdinalIgnoreCase)
                                   : q.OrderBy(n => n.File!.Kind, StringComparer.OrdinalIgnoreCase))
                                  .ThenBy(n => n.File!.Name, StringComparer.OrdinalIgnoreCase),
            VehicleSort.Date => d ? q.OrderByDescending(n => n.File!.Modified)
                                  : q.OrderBy(n => n.File!.Modified),
            _                => d ? q.OrderByDescending(n => n.File!.Name, StringComparer.OrdinalIgnoreCase)
                                  : q.OrderBy(n => n.File!.Name, StringComparer.OrdinalIgnoreCase),
        };
    }

    public bool ShowFilesSection => HasFiles;
    public ObservableCollection<string> ReviewNotes { get; } = new();
    public bool HasReviewNotes => ReviewNotes.Count > 0;

    /// <summary>Rebuild the file list from the converted resource on disk.
    /// Called after a conversion and after any in-place optimize so sizes
    /// refresh. Every file under the resource is listed with its details.</summary>
    public void PopulateFiles()
    {
        Files.Clear();
        var root = LastOutputPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) { OnPropertyChanged(nameof(HasFiles)); return; }

        // IgnoreInaccessible so an antivirus-locked / permission-denied entry
        // can't throw mid-enumeration; the outer try guards a directory removed
        // out from under us (TOCTOU after the Exists check).
        var eo = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", eo)
                         .OrderBy(f => Path.GetRelativePath(root, f), StringComparer.OrdinalIgnoreCase))
            {
                long len; System.DateTime modified;
                try { var fi = new FileInfo(f); len = fi.Length; modified = fi.LastWriteTime; } catch { continue; }
                var rel = Path.GetRelativePath(root, f);
                var folder = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
                var ext = Path.GetExtension(f).ToLowerInvariant();
                bool geom = ext is ".yft" or ".ydr" or ".ydd";
                bool tex = ext == ".ytd";
                bool meta = ext is ".meta" or ".xml";
                Files.Add(new VehicleFile
                {
                    Name = Path.GetFileName(f),
                    Folder = folder.Length == 0 ? "(root)" : folder,
                    Type = ext.TrimStart('.').ToUpperInvariant(),
                    Bytes = len,
                    SizeText = HumanBytes(len),
                    Modified = modified,
                    FullPath = f,
                    CanOptimizeGeometry = geom,
                    CanOptimizeTexture = tex,
                    IsMeta = meta,
                });
            }
        }
        catch { /* directory vanished mid-scan — show what we gathered */ }
        BuildTree();
        RefreshPackStats();
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(ShowFilesSection));
    }

    public void SetPreviewPolygonCount(long? tris)
    {
        StatPolygonsText = tris is null or < 0 ? "—" : tris.Value.ToString("N0");
    }

    public void RefreshPackStats()
    {
        if (!HasFiles)
        {
            StatCars = "—";
            StatSizeText = "—";
            StatPolygonsText = "—";
            StatUnoptimized = "—";
            StatOptimized = "—";
            return;
        }
        var cars = CarNodes.Count(n => n.IsCar);
        StatCars = cars.ToString();
        long bytes = Files.Sum(f => f.Bytes);
        StatSizeText = HumanBytes(bytes);
        int heavy = CarNodes.Count(n => n.IsCar && n.IsUnoptimized);
        int light = Math.Max(0, cars - heavy);
        StatUnoptimized = heavy.ToString();
        StatOptimized = light.ToString();
    }

    private void BuildTree()
    {
        CarNodes.Clear();
        _dataConfig = null;
        if (Files.Count == 0) { OnActiveCarChanged(ActiveCar); return; }

        var cars = LastCarModels.Count > 0
            ? LastCarModels.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : Files.Where(f => f.Type == "YFT")
                   .Select(f => StripHi(Path.GetFileNameWithoutExtension(f.Name)))
                   .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var assigned = new HashSet<VehicleFile>();
        var built = new List<VehicleTreeNode>();
        foreach (var car in cars.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            var carFiles = Files.Where(f => !assigned.Contains(f) && BelongsToCar(f, car)).ToList();
            if (carFiles.Count == 0) continue;
            foreach (var f in carFiles) assigned.Add(f);
            built.Add(BuildCarNode(car, carFiles, isCar: true));
        }

        var rest = Files.Where(f => !assigned.Contains(f)).ToList();
        if (rest.Count > 0)
        {
            if (built.Count == 1)
            {
                MergeFilesIntoCar(built[0], rest);
            }
            else
            {
                var shared = BuildCarNode("shared", rest, isCar: false);
                built.Add(shared);
                _dataConfig = shared;
            }
        }

        foreach (var n in built) CarNodes.Add(n);

        ActiveCar = ActiveCar?.Name is { } prev
            ? CarNodes.FirstOrDefault(n => n.IsCar && n.Name.Equals(prev, StringComparison.OrdinalIgnoreCase))
            : CarNodes.FirstOrDefault(n => n.IsCar);
        OnActiveCarChanged(ActiveCar);
        OnPropertyChanged(nameof(ShowPendingQueue));
        OnPropertyChanged(nameof(ShowLayersTree));
        OnPropertyChanged(nameof(ShowLayersEmpty));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailSubtitle));
        OnPropertyChanged(nameof(DetailStats));
        OnPropertyChanged(nameof(DetailSource));
    }

    private static VehicleTreeNode BuildCarNode(string name, List<VehicleFile> files, bool isCar)
    {
        var streamFiles = files.Where(IsStreamFile).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var dataFiles = files.Where(f => !IsStreamFile(f)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var node = new VehicleTreeNode
        {
            Name = name,
            Icon = isCar ? Wpf.Ui.Controls.SymbolRegular.VehicleCar24 : Wpf.Ui.Controls.SymbolRegular.Folder24,
            IsCar = isCar,
            Detail = files.Count == 1 ? "1 file" : $"{files.Count} files",
            SizeText = HumanBytes(files.Sum(f => f.Bytes)),
            PreviewPath = MainYft(files),
            IsExpanded = true,
        };
        if (streamFiles.Count > 0)
            node.Children.Add(MakeFolderNode("stream", streamFiles, node));
        if (dataFiles.Count > 0)
            node.Children.Add(MakeFolderNode("data", dataFiles, node));
        return node;
    }

    private static void MergeFilesIntoCar(VehicleTreeNode car, List<VehicleFile> files)
    {
        var all = EnumerateFileNodes(car).Select(n => n.File!).Concat(files).ToList();
        car.Children.Clear();
        var streamFiles = all.Where(IsStreamFile).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var dataFiles = all.Where(f => !IsStreamFile(f)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (streamFiles.Count > 0)
            car.Children.Add(MakeFolderNode("stream", streamFiles, car));
        if (dataFiles.Count > 0)
            car.Children.Add(MakeFolderNode("data", dataFiles, car));
    }

    private static VehicleTreeNode MakeFolderNode(string name, List<VehicleFile> files, VehicleTreeNode parent)
    {
        var folder = new VehicleTreeNode
        {
            Name = name,
            Icon = Wpf.Ui.Controls.SymbolRegular.Folder24,
            Detail = files.Count == 0 ? "" : files.Count == 1 ? "1 file" : $"{files.Count} files",
            SizeText = HumanBytes(files.Sum(f => f.Bytes)),
            IsExpanded = true,
            Parent = parent,
        };
        foreach (var f in files)
        {
            var leaf = FileLeaf(f);
            leaf.Parent = folder;
            folder.Children.Add(leaf);
        }
        return folder;
    }

    private static bool IsStreamFile(VehicleFile f)
    {
        var folder = (f.Folder ?? "").Replace('\\', '/');
        if (folder.StartsWith("stream", StringComparison.OrdinalIgnoreCase)
            || folder.Contains("/stream/", StringComparison.OrdinalIgnoreCase)
            || folder.EndsWith("/stream", StringComparison.OrdinalIgnoreCase))
            return true;
        if (folder.StartsWith("data", StringComparison.OrdinalIgnoreCase)
            || folder.Contains("/data/", StringComparison.OrdinalIgnoreCase)
            || folder.EndsWith("/data", StringComparison.OrdinalIgnoreCase))
            return false;
        if (folder.StartsWith("audio", StringComparison.OrdinalIgnoreCase)
            || folder.Contains("/audio/", StringComparison.OrdinalIgnoreCase))
            return true;
        return f.CanOptimizeGeometry || f.CanOptimizeTexture
            || f.Type is "YFT" or "YTD" or "YDR" or "YDD" or "AWC" or "REL" or "DAT" or "NAMETABLE";
    }

    private static string StripHi(string b)
        => b.EndsWith("_hi", StringComparison.OrdinalIgnoreCase) ? b[..^3] : b;

    private static bool BelongsToCar(VehicleFile f, string car)
    {
        var b = Path.GetFileNameWithoutExtension(f.Name);
        if (b.Equals(car, StringComparison.OrdinalIgnoreCase)) return true;
        if (b.Equals(car + "_hi", StringComparison.OrdinalIgnoreCase)) return true;
        if (b.Equals(car + "+hi", StringComparison.OrdinalIgnoreCase)) return true;
        // A pack subfolder named after the car claims everything under it.
        return f.Folder.Split('/', '\\').Any(p => p.Equals(car, StringComparison.OrdinalIgnoreCase));
    }

    private static string? MainYft(List<VehicleFile> carFiles)
    {
        var yfts = carFiles.Where(f => f.Type == "YFT").ToList();
        var hi = yfts.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.Name)
            .EndsWith("_hi", StringComparison.OrdinalIgnoreCase));
        return (hi ?? yfts.FirstOrDefault())?.FullPath;
    }

    /// <summary>Rename a single file in place (keeps it in its folder). Returns
    /// an error message, or null on success. Caller re-populates the tree.</summary>
    public string? RenameFile(VehicleFile f, string newFileName)
    {
        try
        {
            newFileName = newFileName.Trim();
            if (newFileName.Length == 0 || newFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "That's not a valid file name.";
            var dir = Path.GetDirectoryName(f.FullPath)!;
            var dest = Path.Combine(dir, newFileName);
            if (string.Equals(dest, f.FullPath, StringComparison.OrdinalIgnoreCase)) return null;
            if (File.Exists(dest)) return "A file with that name already exists here.";
            File.Move(f.FullPath, dest);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Rename a whole car (its spawn code): renames every
    /// <c>oldCar[.*]</c> / <c>oldCar_hi[.*]</c> file AND replaces whole-word
    /// occurrences of the old name in the resource's metas (vehicles / handling
    /// / carcols / carvariations). Returns an error message, or null.</summary>
    public string? RenameCar(string oldCar, string rawNew)
    {
        var root = LastOutputPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return "Resource folder is missing.";
        // Spawn names are lowercase alphanumeric/underscore, no spaces.
        var newCar = new string((rawNew ?? "").Trim().Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray())
            .ToLowerInvariant();
        if (newCar.Length == 0) return "The name needs letters or digits (no spaces or symbols).";
        if (newCar.Equals(oldCar, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            // 1. Rename the car's model/texture files.
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList())
            {
                var b = Path.GetFileNameWithoutExtension(file);
                var ext = Path.GetExtension(file);
                string? nb = b.Equals(oldCar, StringComparison.OrdinalIgnoreCase) ? newCar
                    : b.Equals(oldCar + "_hi", StringComparison.OrdinalIgnoreCase) ? newCar + "_hi"
                    : null;
                if (nb == null) continue;
                var dest = Path.Combine(Path.GetDirectoryName(file)!, nb + ext);
                if (!File.Exists(dest)) File.Move(file, dest);
            }
            // 2. Update spawn-name references in the metas (whole-word, any case).
            var rx = new System.Text.RegularExpressions.Regex(
                $@"\b{System.Text.RegularExpressions.Regex.Escape(oldCar)}\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (var meta in Directory.EnumerateFiles(root, "*.meta", SearchOption.AllDirectories)
                         .Concat(Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories)))
            {
                var text = File.ReadAllText(meta);
                var updated = rx.Replace(text, newCar);
                if (updated != text) File.WriteAllText(meta, updated);
            }
            // Keep the conversion's model list in sync so the tree regroups.
            for (int i = 0; i < LastCarModels.Count; i++)
                if (LastCarModels[i].Equals(oldCar, StringComparison.OrdinalIgnoreCase)) LastCarModels[i] = newCar;
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static VehicleTreeNode FileLeaf(VehicleFile f) => new()
    {
        Name = f.Name,
        Detail = $"{f.Kind} · {f.SizeText}",
        SizeText = f.SizeText,
        Icon = VehicleTreeNode.IconFor(f),
        File = f,
        PreviewPath = f.Type == "YFT" ? f.FullPath : null,
    };

    internal static string HumanBytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.#} {u[i]}";
    }

    // ─── Input ───────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInput))]
    [NotifyPropertyChangedFor(nameof(CanConvert))]
    [NotifyPropertyChangedFor(nameof(InputDisplay))]
    private string _inputFolder = "";

    /// <summary>All selected inputs — several = a multipack. The first is
    /// mirrored into <see cref="InputFolder"/> for display.</summary>
    public List<string> InputPaths { get; } = new();

    /// <summary>The car-pack queue: one row per added car, shown in the ①
    /// "Add" panel when in pack mode. Kept in lockstep with
    /// <see cref="InputPaths"/>.</summary>
    public ObservableCollection<CarInput> Queue { get; } = new();

    public bool HasQueue => Queue.Count > 0;
    public string QueueCountText => Queue.Count == 1 ? "1 in queue" : $"{Queue.Count} in queue";

    /// <summary>Empty-file-panel heading — context-aware so a queued-but-not-yet-
    /// converted pack doesn't wrongly say "no car added".</summary>
    public string FilesEmptyTitle => HasFiles
        ? "Select a layer"
        : HasInput ? "Ready to import" : "No cars in queue";
    public string FilesEmptyHint => HasFiles
        ? "Pick a car on the left to preview it here."
        : HasInput
            ? "Cars are queued. Click Import to build the pack."
            : "Add cars to the queue, then Import.";

    [ObservableProperty] private string _outputPath = "";

    private static bool IsValidInput(string p)
        => Directory.Exists(p)
           || (File.Exists(p) && p.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase));

    public bool HasInput => InputPaths.Count > 0 && InputPaths.All(IsValidInput);

    /// <summary>Friendly name for a queued car — the folder name, or for a
    /// bare "dlc.rpf" the mod folder that holds it.</summary>
    private static string NameFor(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        if (File.Exists(trimmed))
        {
            var n = Path.GetFileNameWithoutExtension(trimmed);
            if (n.Equals("dlc", StringComparison.OrdinalIgnoreCase))
                n = new DirectoryInfo(Path.GetDirectoryName(trimmed) ?? trimmed).Name;
            return n;
        }
        try { return new DirectoryInfo(trimmed).Name; } catch { return trimmed; }
    }

    public string InputDisplay => !HasInput
        ? "No input selected — drop SP car mods (dlc.rpf files or their folders) here, click Browse, or paste a gta5-mods link."
        : InputPaths.Count == 1
            ? InputPaths[0]
            : MergeIntoPack
                ? $"{InputPaths.Count} car mods (pack):\n" + string.Join("\n", InputPaths.Select(Path.GetFileName))
                : $"{InputPaths.Count} selected — Single-car mode keeps the largest. Switch to Car pack to merge them all.";

    /// <summary>FiveM resource name; empty = auto (dlc device / folder name).</summary>
    [ObservableProperty] private string _packName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputDisplay))]
    [NotifyPropertyChangedFor(nameof(ShowFilesSection))]
    [NotifyPropertyChangedFor(nameof(AddCarsButtonText))]
    [NotifyPropertyChangedFor(nameof(ImportButtonText))]
    [NotifyPropertyChangedFor(nameof(MultiCarHint))]
    private bool _mergeIntoPack = true;

    public string AddCarsButtonText => MergeIntoPack ? "Add to queue" : "Add car";
    public string ImportButtonText => MergeIntoPack ? "Import all" : "Import";
    public string MultiCarHint => MergeIntoPack
        ? "On — queue cars, then Import all"
        : "Off — one car only";

    /// <summary>gta5-mods.com page (or direct archive) link to import.</summary>
    [ObservableProperty] private string _modUrl = "";

    /// <summary>Import-method tab: false = local files, true = gta5-mods link.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportByFile))]
    private bool _importByLink;

    public bool ImportByFile => !ImportByLink;

    // ─── Optimize (decimate) — moved here from the Optimize tab ──────────

    /// <summary>Fraction of triangles to KEEP when decimating the car's HIGH
    /// LOD in place (0.05..0.95). Default 0.5 = halve the polycount.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeepRatioDisplay))]
    private double _keepRatio = 0.5;

    [ObservableProperty] private bool _preserveBoundary = true;

    public string KeepRatioDisplay => $"{KeepRatio * 100:F0}% kept ({(1 - KeepRatio) * 100:F0}% reduction)";

    // ─── Conversion results the car workspace feeds on ──────────────────

    public List<string> LastCarModels { get; } = new();
    public List<SpVehicleConverter.SourceInfo> LastCarSources { get; } = new();
    public Dictionary<string, string> LastModelLabels { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasCarPack => LastCarModels.Count > 0
        && !string.IsNullOrWhiteSpace(LastOutputPath) && Directory.Exists(LastOutputPath);

    // ─── Lifecycle ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConvert))]
    private bool _isProcessing;

    [ObservableProperty]
    private string _summary = "Add cars to the queue, then Import.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    private string _lastOutputPath = "";

    public bool HasOutput => !string.IsNullOrWhiteSpace(LastOutputPath)
        && (File.Exists(LastOutputPath) || Directory.Exists(LastOutputPath));

    public bool HasRows => Rows.Count > 0;
    public bool CanConvert => HasInput && !IsProcessing;

    public void SetInputFolder(string? folder)
        => SetInputs(string.IsNullOrWhiteSpace(folder) ? Array.Empty<string>() : new[] { folder });

    /// <summary>True once the user picked an output folder by hand — stops the
    /// auto-default from clobbering their choice on the next add/remove.</summary>
    private bool _outputUserSet;

    /// <summary>User chose the output folder explicitly (Advanced → Change folder).</summary>
    public void SetOutputFolder(string path)
    {
        OutputPath = path;
        _outputUserSet = true;
    }

    /// <summary>The last conversion no longer matches the current queue — drop
    /// its produced-file list so Optimize can't act on a stale resource.</summary>
    private void InvalidateConversion()
    {
        LastCarModels.Clear();
        Files.Clear();
        CarNodes.Clear();
        _dataConfig = null;
        ActiveCar = null;
        ClearMetaEditor();
        RefreshPackStats();
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(ShowFilesSection));
        OnPropertyChanged(nameof(HasCarPack));
        OnPropertyChanged(nameof(ShowPendingQueue));
        OnPropertyChanged(nameof(ShowLayersTree));
        OnPropertyChanged(nameof(ShowLayersEmpty));
    }

    public void ClearMetaEditor()
    {
        SelectedMetaFile = null;
        MetaRawText = "";
        MetaTitle = "";
        MetaStatusNote = "";
        MetaKind = VehicleMetaKind.Other;
        MetaRawMode = false;
    }

    public void OpenMetaEditor(VehicleFile file)
    {
        if (file == null || !file.IsMeta) { ClearMetaEditor(); return; }
        SelectedMetaFile = file;
        MetaTitle = file.Name;
        MetaKind = VehicleMetaFormService.DetectKind(file.Name);
        MetaRawMode = !VehicleMetaFormService.SupportsForm(MetaKind);
        MetaStatusNote = "";
        ReloadMetaFromDisk();
    }

    public void ReloadMetaFromDisk()
    {
        if (SelectedMetaFile == null) return;
        try
        {
            MetaRawText = File.ReadAllText(SelectedMetaFile.FullPath);
            ApplyRawToFormFields();
            MetaStatusNote = "";
        }
        catch (Exception ex)
        {
            MetaStatusNote = "Couldn't open: " + ex.Message;
        }
    }

    public void ImportMetaText(string xml, string? sourceName = null)
    {
        if (SelectedMetaFile == null) return;
        MetaRawText = xml ?? "";
        ApplyRawToFormFields();
        MetaStatusNote = string.IsNullOrWhiteSpace(sourceName)
            ? "Imported — review, then Save meta."
            : $"Imported from {sourceName} — review, then Save meta.";
    }

    public void SetMetaEditorMode(bool raw)
    {
        if (!MetaSupportsForm) { MetaRawMode = true; return; }
        if (raw == MetaRawMode) return;
        if (raw)
        {
            MetaRawText = VehicleMetaFormService.ApplyFields(MetaRawText, MetaKind, CaptureFormFields());
            MetaRawMode = true;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(MetaRawText))
                ApplyRawToFormFields();
            MetaRawMode = false;
        }
    }

    public string? SaveMetaEditor()
    {
        if (SelectedMetaFile == null) return "No meta selected.";
        string xml;
        if (IsMetaFormMode)
            xml = VehicleMetaFormService.ApplyFields(MetaRawText, MetaKind, CaptureFormFields());
        else
            xml = MetaRawText ?? "";

        var err = VehicleMetaFormService.TryValidateXml(xml);
        if (err != null) return "Not valid XML — not saved: " + err;
        try
        {
            File.WriteAllText(SelectedMetaFile.FullPath, xml);
            MetaRawText = xml;
            ApplyRawToFormFields();
            MetaStatusNote = "Saved into the selected layer file.";
            PopulateFiles();
            return null;
        }
        catch (Exception ex) { return "Save failed: " + ex.Message; }
    }

    private void ApplyRawToFormFields()
    {
        var f = VehicleMetaFormService.LoadFields(MetaRawText, MetaKind);
        MetaModelName = f.ModelName;
        MetaGameName = f.GameName;
        MetaVehicleMake = f.VehicleMake;
        MetaVehicleClass = string.IsNullOrWhiteSpace(f.VehicleClass) ? "VC_SEDAN" : f.VehicleClass;
        MetaHandlingId = f.HandlingId;
        MetaFrequency = f.Frequency;
        MetaFlags = f.Flags;
        MetaMass = f.Mass;
        MetaInitialDriveForce = f.InitialDriveForce;
        MetaBrakeForce = f.BrakeForce;
        MetaTractionCurveMax = f.TractionCurveMax;
        MetaTractionCurveMin = f.TractionCurveMin;
        MetaTopSpeedKph = f.TopSpeedKph;
        MetaDownforceModifier = f.DownforceModifier;
        MetaColor1 = f.Color1;
        MetaColor2 = f.Color2;
        MetaPearlescent = f.Pearlescent;
        MetaKits = f.Kits;
    }

    private VehicleMetaFormFields CaptureFormFields() => new()
    {
        ModelName = MetaModelName,
        GameName = MetaGameName,
        VehicleMake = MetaVehicleMake,
        VehicleClass = MetaVehicleClass,
        HandlingId = MetaHandlingId,
        Frequency = MetaFrequency,
        Flags = MetaFlags,
        Mass = MetaMass,
        InitialDriveForce = MetaInitialDriveForce,
        BrakeForce = MetaBrakeForce,
        TractionCurveMax = MetaTractionCurveMax,
        TractionCurveMin = MetaTractionCurveMin,
        TopSpeedKph = MetaTopSpeedKph,
        DownforceModifier = MetaDownforceModifier,
        Color1 = MetaColor1,
        Color2 = MetaColor2,
        Pearlescent = MetaPearlescent,
        Kits = MetaKits,
    };

    public void LoadResourceFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        LastOutputPath = path;
        OutputPath = path;
        _outputUserSet = true;
        if (string.IsNullOrWhiteSpace(PackName))
            PackName = new DirectoryInfo(path.TrimEnd('\\', '/')).Name;
        LastCarModels.Clear();
        LastCarSources.Clear();
        LastModelLabels.Clear();
        ClearMetaEditor();
        PopulateFiles();
        Summary = $"Loaded resource folder — {Files.Count} file(s).";
        OnPropertyChanged(nameof(HasOutput));
        OnPropertyChanged(nameof(HasCarPack));
        RaiseState();
    }

    public string? CreateTemplate(string parentFolder, string resourceName)
    {
        try
        {
            var dir = VehiclePackTemplate.Create(parentFolder, resourceName);
            PackName = new DirectoryInfo(dir).Name;
            LoadResourceFolder(dir);
            Summary = $"Created empty pack template at {dir}.";
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Replace the whole input set (single-car add — one car in, the
    /// previous one out). Resets the manual-output flag: a brand-new selection
    /// gets a fresh default location.</summary>
    public void SetInputs(IReadOnlyList<string> paths)
    {
        _outputUserSet = false;
        InputPaths.Clear();
        Queue.Clear();
        AddInputsCore(paths);
    }

    /// <summary>Append cars to the queue without dropping what's already there —
    /// the car-pack "add another" path. Duplicates are ignored.</summary>
    public void AddInputs(IReadOnlyList<string> paths) => AddInputsCore(paths);

    private void AddInputsCore(IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (InputPaths.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) continue;
            InputPaths.Add(p);
            Queue.Add(new CarInput(NameFor(p), p));
        }
        InputFolder = InputPaths.FirstOrDefault() ?? "";
        InvalidateConversion();
        SelectedQueueItem = Queue.LastOrDefault();
        if (HasInput) RecomputeDefaultOutput();
        else Summary = "Add cars to the queue, then Import.";
        RaiseState();
    }

    /// <summary>Remove a car from the pack by its converted MODEL name (what the
    /// tree shows) — maps it back to the queued source and drops it. The caller
    /// then reconverts. Returns true if something was removed.</summary>
    public bool RemoveCarByModel(string model)
    {
        // LastCarSources is built from the inputs in order, so index-aligns with the queue.
        for (int i = 0; i < LastCarSources.Count && i < Queue.Count; i++)
            if (LastCarSources[i].Models.Any(m => m.Equals(model, StringComparison.OrdinalIgnoreCase)))
            { RemoveInput(Queue[i]); return true; }
        // Fallback: match by name overlap (sp_bobcatxl ↔ bobcatxl).
        var q = Queue.FirstOrDefault(c =>
            c.Name.Contains(model, StringComparison.OrdinalIgnoreCase) ||
            model.Contains(c.Name, StringComparison.OrdinalIgnoreCase));
        if (q != null) { RemoveInput(q); return true; }
        return false;
    }

    /// <summary>Remove one car from the pack queue.</summary>
    public void RemoveInput(CarInput? item)
    {
        if (item == null) return;
        Queue.Remove(item);
        InputPaths.RemoveAll(x => string.Equals(x, item.Path, StringComparison.OrdinalIgnoreCase));
        InputFolder = InputPaths.FirstOrDefault() ?? "";
        InvalidateConversion();
        if (HasInput)
        {
            RecomputeDefaultOutput();
            SelectedQueueItem = Queue.LastOrDefault();
            Summary = "Queue updated — Import again to rebuild the pack.";
        }
        else
        {
            _outputUserSet = false;
            OutputPath = "";
            LastOutputPath = "";
            Summary = "Add cars to the queue, then Import.";
            SelectedQueueItem = null;
        }
        RaiseState();
    }

    private void RecomputeDefaultOutput()
    {
        if (_outputUserSet) { UpdateReadySummary(); return; }   // respect the user's Change folder
        if (!HasInput) { OutputPath = ""; return; }
        var trimmed = InputFolder.TrimEnd('\\', '/');
        var name = new DirectoryInfo(trimmed).Name;
        var parent = Path.GetDirectoryName(trimmed) ?? trimmed;
        if (File.Exists(trimmed))
        {
            // .rpf-file input: "dlc.rpf" says nothing — name the output after
            // the mod folder that contains it.
            name = Path.GetFileNameWithoutExtension(trimmed);
            if (name.Equals("dlc", StringComparison.OrdinalIgnoreCase))
                name = new DirectoryInfo(parent).Name;
        }
        // Imported mods live under %TEMP% — nobody can find output there.
        // Default those (and packs built from them) into Downloads instead.
        if (IsUnderTemp(parent))
            parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        // For a pack, prefer the pack name the user typed for the folder stem.
        var stem = MergeIntoPack && !string.IsNullOrWhiteSpace(PackName) ? PackName.Trim() : name;
        OutputPath = Path.Combine(parent, stem + "_fivem");
        UpdateReadySummary();
    }

    private void UpdateReadySummary()
        => Summary = InputPaths.Count > 1
            ? $"{InputPaths.Count} cars queued — Import to build the pack"
            : $"1 car queued — Import to build the resource";

    private static bool IsUnderTemp(string path)
    {
        try
        {
            var tmp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/');
            return Path.GetFullPath(path).StartsWith(tmp, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public void Clear()
    {
        _outputUserSet = false;
        InputPaths.Clear();
        Queue.Clear();
        InputFolder = "";
        OutputPath = "";
        LastOutputPath = "";
        Rows.Clear();
        InvalidateConversion();
        Summary = "Add cars to the queue, then Import.";
        SelectedQueueItem = null;
        RaiseState();
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(HasInput));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(QueueCountText));
        OnPropertyChanged(nameof(CanConvert));
        OnPropertyChanged(nameof(InputDisplay));
        OnPropertyChanged(nameof(FilesEmptyTitle));
        OnPropertyChanged(nameof(FilesEmptyHint));
        OnPropertyChanged(nameof(ShowPendingQueue));
        OnPropertyChanged(nameof(ShowLayersTree));
        OnPropertyChanged(nameof(ShowLayersEmpty));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailSubtitle));
        OnPropertyChanged(nameof(DetailStats));
        OnPropertyChanged(nameof(DetailSource));
    }

    // ─── Import from link ────────────────────────────────────────────────

    /// <summary>Download a mod from <see cref="ModUrl"/>, extract it, make it
    /// the input, and convert. Returns true when a pack came out the end.</summary>
    public async Task<bool> ImportFromUrlAsync()
    {
        if (IsProcessing || string.IsNullOrWhiteSpace(ModUrl)) return false;
        IsProcessing = true;
        RaiseState();
        ModUrlImporter.Result? res;
        try
        {
            Summary = "Downloading mod…";
            res = await new ModUrlImporter().ImportAsync(ModUrl, msg => PostStatus(msg));
        }
        finally
        {
            IsProcessing = false;
            RaiseState();
        }
        if (res is not { Success: true, ExtractedDir: not null })
        {
            Summary = res?.Error ?? "Import failed.";
            _setStatus(Summary);
            return false;
        }

        AddInputs(new[] { res.ExtractedDir });
        if (string.IsNullOrWhiteSpace(PackName) && res.ModName != null) PackName = res.ModName;
        Summary = "Added to queue — Import when ready.";
        return false;
    }

    // ─── Convert ─────────────────────────────────────────────────────────

    /// <summary>Backs the Cancel button. The converter checks it between car
    /// sources, so a multi-mod pack stops after the current car. Recreated per
    /// <see cref="ConvertAsync"/>; disposed in its finally.</summary>
    private CancellationTokenSource? _cts;

    /// <summary>Signals an in-flight <see cref="ConvertAsync"/> to stop. Idle-safe.</summary>
    public void RequestCancel()
    {
        _cts?.Cancel();
        PostStatus("Cancelling — finishing the current car…");
    }

    public async Task ConvertAsync()
    {
        if (!CanConvert) return;
        IsProcessing = true;
        _cts = new CancellationTokenSource();
        Rows.Clear();
        // Drop the previous conversion's file rows now — a failed reconvert
        // must not leave stale rows that optimize the OLD resource.
        Files.Clear();
        CarNodes.Clear();
        _dataConfig = null;
        OnPropertyChanged(nameof(HasFiles));
        ReviewNotes.Clear();
        OnPropertyChanged(nameof(HasReviewNotes));
        ClearMetaEditor();
        Summary = "Importing cars to FiveM…";
        RaiseState();

        try
        {
            var input = InputFolder;
            var outRoot = string.IsNullOrWhiteSpace(OutputPath)
                ? (Path.GetDirectoryName(input.TrimEnd('\\', '/')) ?? input)
                : OutputPath;

            var inputs = InputPaths.Count > 0 ? InputPaths.ToList() : new List<string> { input };
            // MergeAll follows the mode toggle ONLY. Single-car mode with several
            // inputs keeps the largest (converter dedup); pack mode merges.
            var opts = new SpVehicleConverter.Options(
                string.IsNullOrWhiteSpace(PackName) ? null : PackName,
                MergeAll: MergeIntoPack);

            var conv = new SpVehicleConverter();
            SpVehicleConverter.Result? res = null;
            var token = _cts.Token;
            await Task.Run(() =>
                res = conv.Convert(inputs, outRoot, opts, msg => PostStatus(msg), token));

            if (res == null) return;
            LastCarModels.Clear();
            LastCarSources.Clear();
            LastModelLabels.Clear();
            if (res.Success && res.OutputPath != null)
            {
                LastOutputPath = res.OutputPath;
                LastCarModels.AddRange(res.Models);
                LastCarSources.AddRange(res.Sources);
                if (res.ModelLabels != null)
                    foreach (var (k, v) in res.ModelLabels) LastModelLabels[k] = v;
            }
            OnPropertyChanged(nameof(HasCarPack));
            foreach (var f in res.Files)
                Rows.Add(new RpfFileRow(f));
            foreach (var w in res.Warnings) ReviewNotes.Add("⚠ " + w);
            // Only surface files for a conversion that actually produced output —
            // on failure LastOutputPath still points at the prior resource.
            if (res.Success && res.OutputPath != null) PopulateFiles();

            var packWord = res.Sources.Count > 1 ? $"pack from {res.Sources.Count} mods" : "resource";
            Summary = res.Error != null
                ? $"Failed: {res.Error}"
                : $"Built FiveM {packWord} '{res.ResourceName}' — {res.Models.Count} vehicle(s): "
                  + $"{string.Join(", ", res.Models)}. Copy it into resources/ and add `ensure {res.ResourceName}`.";
            _setStatus(Summary);
        }
        catch (OperationCanceledException)
        {
            // User pressed Cancel — stop cleanly, don't surface it as a failure.
            Summary = "Cancelled.";
            _setStatus(Summary);
        }
        catch (Exception ex)
        {
            Summary = "Failed: " + ex.Message;
            _setStatus(Summary);
        }
        finally  // never leave the Convert button stuck disabled on an exception
        {
            IsProcessing = false;
            _cts?.Dispose();
            _cts = null;
            OnPropertyChanged(nameof(HasReviewNotes));
            RaiseState();
        }
    }

    private void PostStatus(string msg)
    {
        var app = System.Windows.Application.Current;
        if (app != null) app.Dispatcher.BeginInvoke(() => { _setStatus(msg); if (IsProcessing) Summary = msg; });
        else { _setStatus(msg); if (IsProcessing) Summary = msg; }
    }
}
