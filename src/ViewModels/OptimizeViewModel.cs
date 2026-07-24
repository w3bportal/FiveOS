// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using FiveOS.Services;

namespace FiveOS.ViewModels;

/// <summary>
/// What kind of FiveM resource the active queue is targeting. Each value
/// maps to a specific file extension and a specific optimizer pipeline.
/// </summary>
public enum OptimizeMode
{
    Props,            // .ydr — DrawableOptimizer.OptimizeYdr
    Clothing,         // .ydd — DrawableOptimizer.OptimizeYdd
    Textures,         // .ytd — YtdOptimizer
    EmbeddedTextures, // .ydd/.ydr/.yft — optimize embedded TXDs only (geometry untouched)
    TxAdmin,          // no queue/extension — swaps the workspace for the txAdmin
                      // log-driven auto-optimizer (its own VM + view, TxAdminVm)
}

/// <summary>
/// Single unified VM behind the four "Optimize ___" cards. Each mode keeps
/// its own queue + its own options so switching cards doesn't lose state.
///
/// Replaces the former MeshOptimize / TextureOptimize / MappingOptimize
/// view-models — all three were doing the same drop-zone-and-queue dance
/// against different file types.
/// </summary>
public partial class OptimizeViewModel : ObservableObject
{
    private readonly Action<string> _setStatus;

    public OptimizeViewModel(Action<string> setStatus)
    {
        _setStatus = setStatus;
    }

    // ─── Mode + per-mode queues ──────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPropsMode))]
    [NotifyPropertyChangedFor(nameof(IsClothingMode))]
    [NotifyPropertyChangedFor(nameof(IsTexturesMode))]
    [NotifyPropertyChangedFor(nameof(IsEmbeddedTexturesMode))]
    [NotifyPropertyChangedFor(nameof(IsTxAdminMode))]
    [NotifyPropertyChangedFor(nameof(IsHostedToolMode))]
    [NotifyPropertyChangedFor(nameof(ActiveExtension))]
    [NotifyPropertyChangedFor(nameof(ActiveBrowseFilter))]
    [NotifyPropertyChangedFor(nameof(ActiveQueue))]
    [NotifyPropertyChangedFor(nameof(HasFiles))]
    [NotifyPropertyChangedFor(nameof(CanProcess))]
    [NotifyPropertyChangedFor(nameof(FileCount))]
    [NotifyPropertyChangedFor(nameof(EmptyHint))]
    [NotifyPropertyChangedFor(nameof(IsLodMode))]
    [NotifyPropertyChangedFor(nameof(IsTextureMode))]
    private OptimizeMode _mode = OptimizeMode.Props;

    public bool IsPropsMode            => Mode == OptimizeMode.Props;
    public bool IsClothingMode         => Mode == OptimizeMode.Clothing;
    public bool IsTexturesMode         => Mode == OptimizeMode.Textures;
    public bool IsEmbeddedTexturesMode => Mode == OptimizeMode.EmbeddedTextures;
    public bool IsTxAdminMode          => Mode == OptimizeMode.TxAdmin;

    /// <summary>Modes whose workspace is a self-contained hosted view (own VM
    /// on MainViewModel) rather than the shared drop-zone/queue workspace.</summary>
    public bool IsHostedToolMode => Mode == OptimizeMode.TxAdmin;

    /// <summary>The three geometry-decimation modes — drives the REDUCTION
    /// options panel and the 3D LOD workbench on the right.</summary>
    public bool IsLodMode => Mode is OptimizeMode.Props or OptimizeMode.Clothing;

    /// <summary>The two texture-compression modes (standalone .ytd + embedded)
    /// — drives the COMPRESSION options panel and the flat texture gallery.</summary>
    public bool IsTextureMode => Mode is OptimizeMode.Textures or OptimizeMode.EmbeddedTextures;

    public string ActiveExtension => Mode switch
    {
        OptimizeMode.Props            => ".ydr",
        OptimizeMode.Clothing         => ".ydd",
        OptimizeMode.Textures         => ".ytd",
        OptimizeMode.EmbeddedTextures => ".ydd",   // representative; mode accepts all drawables
        _ => "",
    };

    /// <summary>OpenFileDialog filter for the active mode. EmbeddedTextures
    /// accepts every drawable type at once, so it can't use a single ext.</summary>
    public string ActiveBrowseFilter => Mode switch
    {
        OptimizeMode.EmbeddedTextures =>
            "Drawable models (*.ydd;*.ydr;*.yft)|*.ydd;*.ydr;*.yft|All files (*.*)|*.*",
        _ => $"{ActiveExtension.TrimStart('.').ToUpperInvariant()} files (*{ActiveExtension})|*{ActiveExtension}|All files (*.*)|*.*",
    };

    public string EmptyHint => Mode switch
    {
        OptimizeMode.Props            => "Drop .ydr files here to optimize prop geometry",
        OptimizeMode.Clothing         => "Drop .ydd files here to optimize clothing meshes",
        OptimizeMode.Textures         => "Drop .ytd files (or a folder of them) to compress textures",
        OptimizeMode.EmbeddedTextures => "Drop models (.ydd / .ydr / .yft) to compress their embedded textures",
        _ => "",
    };

    public ObservableCollection<OptimizeQueueItem> PropsQueue            { get; } = new();
    public ObservableCollection<OptimizeQueueItem> ClothingQueue         { get; } = new();
    public ObservableCollection<OptimizeQueueItem> TexturesQueue         { get; } = new();
    public ObservableCollection<OptimizeQueueItem> EmbeddedTexturesQueue { get; } = new();

    public ObservableCollection<OptimizeQueueItem> ActiveQueue => Mode switch
    {
        OptimizeMode.Props            => PropsQueue,
        OptimizeMode.Clothing         => ClothingQueue,
        OptimizeMode.Textures         => TexturesQueue,
        OptimizeMode.EmbeddedTextures => EmbeddedTexturesQueue,
        _ => PropsQueue,
    };

    public int FileCount => ActiveQueue.Count;
    public bool HasFiles => FileCount > 0;
    public bool CanProcess => FileCount > 0 && !IsProcessing;

    // ─── Drawable decimation options (Props/Clothing/Vehicles) ───────

    /// <summary>Fraction of triangles to keep, 0.05..0.95. Default 0.5
    /// (halve the polycount) — a sane "fast win" for most prop YDRs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeepRatioDisplay))]
    private double _keepRatio = 0.5;

    [ObservableProperty] private bool _preserveBoundary = true;

    public string KeepRatioDisplay => $"{KeepRatio * 100:F0}% kept ({(1 - KeepRatio) * 100:F0}% reduction)";

    // ─── YTD (Textures mode) options ─────────────────────────────────

    [ObservableProperty] private bool _ytdDownsize = true;
    [ObservableProperty] private bool _ytdFormatOptimization;
    [ObservableProperty] private bool _ytdOnlyOversized;

    /// <summary>0 = 8192 (W+H, i.e. >2K), 1 = 4096, 2 = 2048, 3 = 1024.</summary>
    [ObservableProperty] private int _ytdThresholdIndex;
    private static readonly ushort[] YtdThresholdValues = { 8192, 4096, 2048, 1024 };

    /// <summary>Resolution ceiling on the longest side. 0 = 2048 = 1024 = 512
    /// at indices 0..3, where index 0 means "no cap" (defer to Downsize 2×).
    /// When a cap is set it shrinks any oversized texture down TO that size
    /// (repeated halving), rather than the single halve Downsize does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YtdNoCap))]
    private int _ytdMaxSizeIndex;
    private static readonly int[] YtdMaxSizeValues = { 0, 2048, 1024, 512 };

    /// <summary>True when no resolution cap is selected. Drives the enabled
    /// state of the "Downsize 2×" toggle — the cap supersedes it, so the two
    /// aren't offered at once.</summary>
    public bool YtdNoCap => YtdMaxSizeValues[YtdMaxSizeIndex] == 0;

    // ─── Lifecycle ───────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProcess))]
    private bool _isProcessing;

    [ObservableProperty] private double _progress;

    [ObservableProperty]
    private string _summary = "Pick a mode above and drop files to begin.";

    // ─── Cancellation ────────────────────────────────────────────────

    /// <summary>Backs the Cancel button. The queue loop checks this token
    /// between files and <see cref="YtdOptimizer"/> honours it mid-file, so a
    /// long batch (thousands of YTDs) stops promptly instead of running to the
    /// end. Recreated per <see cref="RunAsync"/>; disposed in its finally.</summary>
    private CancellationTokenSource? _cts;

    /// <summary>Signals an in-flight <see cref="RunAsync"/> to stop after the
    /// current file. Safe to call when idle — it's a no-op if nothing runs.</summary>
    public void RequestCancel()
    {
        _cts?.Cancel();
        Summary = "Cancelling — finishing the current file…";
    }

    // ─── Selection (drives the inline LOD preview panel in OptimizeView) ──

    /// <summary>Row selected in the queue list. The inline preview panel
    /// (OptimizeView code-behind) observes this and renders the model.</summary>
    [ObservableProperty]
    private OptimizeQueueItem? _selectedItem;

    // ─── Queue management ────────────────────────────────────────────

    public void AddPaths(IEnumerable<string> paths)
    {
        // Route every discovered file to the queue for its own type so a
        // mixed drop (e.g. a clothing pack of .ydd + .ytd) fans out instead
        // of being filtered down to the active card's extension. Tallying by
        // MODE (not extension) keeps the dominant-queue switch correct for
        // EmbeddedTextures, which shares .ydd/.ydr/.yft with the geometry modes.
        var added = new Dictionary<OptimizeMode, int>();
        foreach (var (f, fromFolder) in ExpandSupportedFiles(paths))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            // When EmbeddedTextures is the active mode, capture dropped drawables
            // for IT rather than their geometry mode (.ydd would otherwise route
            // to Clothing). .ytd still falls through to the Textures queue.
            var mode = (Mode == OptimizeMode.EmbeddedTextures && ext is ".ydd" or ".ydr" or ".yft")
                ? OptimizeMode.EmbeddedTextures
                : ModeForExtension(ext);
            if (mode is not { } m) continue;
            var queue = QueueForMode(m);
            if (queue.Any(q => string.Equals(q.Path, f, StringComparison.OrdinalIgnoreCase)))
                continue;
            queue.Add(new OptimizeQueueItem(f) { ForceInPlace = fromFolder });
            added[m] = added.TryGetValue(m, out var n) ? n + 1 : 1;
        }

        if (added.Count == 0) return;

        // If the visible queue got nothing but another did, switch to the
        // mode that received the most files so the user sees what landed.
        // Setting Mode fires the [NotifyPropertyChangedFor] cluster on _mode.
        if (!added.ContainsKey(Mode))
            Mode = added.OrderByDescending(kv => kv.Value).First().Key;

        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(CanProcess));
        OnPropertyChanged(nameof(ActiveQueue));

        var parts = added.OrderByDescending(kv => kv.Value)
                         .Select(kv => $"+{kv.Value} {ModeLabel(kv.Key)}");
        _setStatus($"Optimize: {string.Join(", ", parts)}");
    }

    public void ClearActive()
    {
        ActiveQueue.Clear();
        Progress = 0;
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(CanProcess));
    }

    /// <summary>Every extension the Optimize tab understands. A dropped or
    /// picked folder is scanned for all of these at once — a clothing pack
    /// mixes .ydd (garments) and .ytd (textures), so a single drop has to
    /// fan out across queues rather than honour only the active card.</summary>
    private static readonly string[] KnownExtensions = { ".ydr", ".ydd", ".ytd", ".yft" };

    private static OptimizeMode? ModeForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".ydr" => OptimizeMode.Props,
        ".ydd" => OptimizeMode.Clothing,
        ".ytd" => OptimizeMode.Textures,
        ".yft" => OptimizeMode.EmbeddedTextures,
        _ => null,
    };

    private ObservableCollection<OptimizeQueueItem> QueueForMode(OptimizeMode m) => m switch
    {
        OptimizeMode.Props            => PropsQueue,
        OptimizeMode.Clothing         => ClothingQueue,
        OptimizeMode.Textures         => TexturesQueue,
        OptimizeMode.EmbeddedTextures => EmbeddedTexturesQueue,
        _ => PropsQueue,
    };

    /// <summary>Short label for the add-paths status line (e.g. "+12 YDD",
    /// "+3 EMBEDDED").</summary>
    private static string ModeLabel(OptimizeMode m) => m switch
    {
        OptimizeMode.Props            => "YDR",
        OptimizeMode.Clothing         => "YDD",
        OptimizeMode.Textures         => "YTD",
        OptimizeMode.EmbeddedTextures => "EMBEDDED",
        _ => "?",
    };

    /// <summary>Flattens dropped/picked paths into the supported files they
    /// contain. Folders are recursed (one "*" pass filtered in-code is
    /// cheaper than four "*"+ext passes over a deep [assets]/[clothing]
    /// tree). FromFolder marks files discovered inside a dropped folder so
    /// the run can keep them in place (and preserve the pack's subfolders)
    /// even when server mode is on.</summary>
    private static IEnumerable<(string Path, bool FromFolder)> ExpandSupportedFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    if (KnownExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        yield return (f, true);
            }
            else if (File.Exists(p) && KnownExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
            {
                yield return (p, false);
            }
        }
    }

    /// <summary>Whether a drag payload is worth a copy cursor. Any directory
    /// or any of the four known types qualifies, regardless of the active
    /// card — drops are routed to the right queue by extension, so the user
    /// no longer has to pick the matching mode first.</summary>
    public static bool IsAcceptedDrop(string path, OptimizeMode mode)
    {
        if (Directory.Exists(path)) return true;
        return KnownExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    }

    // ─── Run ─────────────────────────────────────────────────────────

    /// <summary>Output directory for a given source file. Server mode forces
    /// <server>/stream/ so the optimized asset is live next reload; otherwise
    /// we write next to the source file itself so the user finds the result
    /// in the same folder they dragged from — the previous
    /// "FiveOS_optimize/<mode>/" subfolder under Downloads surprised mappers
    /// who expected an in-place workflow.
    ///
    /// <paramref name="forceInPlace"/> overrides the server-mode redirect for
    /// files that came from a dropped folder: a clothing pack is a nested
    /// tree, and flattening it into a single flat stream/ folder would both
    /// destroy the subfolders the user wants kept and collide same-named
    /// files across packs. Folder-origin items therefore always write back
    /// next to their source.</summary>
    public static string ResolveOutputDir(string sourcePath, bool forceInPlace)
    {
        if (!forceInPlace && UserSettings.IsServerModeActive())
            return Path.Combine(UserSettings.LoadServerResourceFolder()!, "stream");
        return Path.GetDirectoryName(sourcePath) ?? UserSettings.ResolveSingleOutputFolder();
    }

    public void OpenOutputFolder()
    {
        string? dir = null;
        // A folder/in-place queue writes next to each source, not to stream/,
        // even when server mode is on — open the first item's own folder so
        // the button matches where files actually landed.
        var anyInPlace = ActiveQueue.Any(i => i.ForceInPlace);
        if (UserSettings.IsServerModeActive() && !anyInPlace)
        {
            dir = Path.Combine(UserSettings.LoadServerResourceFolder()!, "stream");
        }
        else
        {
            var first = ActiveQueue.FirstOrDefault();
            dir = first != null ? Path.GetDirectoryName(first.Path) : null;
        }
        if (string.IsNullOrEmpty(dir))
        {
            _setStatus("Drop files first — optimize writes next to each source.");
            return;
        }
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _setStatus($"Open output folder failed: {ex.Message}");
        }
    }

    public async Task RunAsync()
    {
        if (!CanProcess) return;
        IsProcessing = true;
        Progress = 0;
        Summary = "Optimizing...";
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var mode = Mode;
        var queue = ActiveQueue.ToList();

        int ok = 0, fail = 0;
        long bytesIn = 0, bytesOut = 0;
        long trisIn = 0, trisOut = 0;
        long vertsIn = 0, vertsOut = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool canceled = false;

        try
        {
        await Task.Run(() =>
        {
            for (int i = 0; i < queue.Count; i++)
            {
                if (token.IsCancellationRequested) break;
                var item = queue[i];
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    item.StatusKind = OptimizeStatusKind.Running;
                    item.Status = "running…";
                });

                try
                {
                    var outDir = ResolveOutputDir(item.Path, item.ForceInPlace);
                    Directory.CreateDirectory(outDir);
                    if (mode == OptimizeMode.Textures)
                    {
                        RunOneYtd(item, outDir, token);
                    }
                    else if (mode == OptimizeMode.EmbeddedTextures)
                    {
                        RunOneEmbeddedTextures(item, outDir);
                    }
                    else
                    {
                        RunOneDrawable(item, mode, outDir);
                    }
                    bytesIn += item.BytesBefore;
                    bytesOut += item.BytesAfter;
                    trisIn += item.TrisBefore;
                    trisOut += item.TrisAfter;
                    vertsIn += item.VertsBefore;
                    vertsOut += item.VertsAfter;
                    if (item.Error == null) ok++; else fail++;
                }
                catch (OperationCanceledException)
                {
                    // User hit Cancel mid-file — stop the batch cleanly without
                    // marking this row as a failure. Its "running…" status is
                    // reset below so it reads as merely un-processed.
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        item.StatusKind = OptimizeStatusKind.None;
                        item.Status = "cancelled";
                    });
                    break;
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        item.Error = ex.Message;
                        item.StatusKind = OptimizeStatusKind.Error;
                        item.Status = Truncate(ex.Message, 60);
                    });
                    fail++;
                }

                int idx = i + 1;
                var elapsed = sw.Elapsed;
                // ETA from the running average per item; only meaningful once a
                // couple have finished, so suppress it on the very first item.
                var eta = idx >= 1 && idx < queue.Count
                    ? TimeSpan.FromSeconds(elapsed.TotalSeconds / idx * (queue.Count - idx))
                    : TimeSpan.Zero;
                var etaPart = idx < queue.Count ? $" · ~{FormatDuration(eta)} left" : "";
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    Progress = idx / (double)queue.Count;
                    Summary = $"Optimizing {idx}/{queue.Count} · {FormatDuration(elapsed)} elapsed{etaPart}";
                    _setStatus($"Optimize ({mode}): {idx}/{queue.Count} · {ok} ok · {fail} failed · {FormatDuration(elapsed)} elapsed{etaPart}");
                });
            }
        });
        canceled = token.IsCancellationRequested;
        }
        finally { sw.Stop(); IsProcessing = false; Progress = 0; _cts?.Dispose(); _cts = null; }  // never leave the run stuck

        var savedPct = bytesIn > 0 ? 1.0 - (bytesOut / (double)bytesIn) : 0;
        var triPart = trisIn > 0 ? $" · {trisIn:N0}→{trisOut:N0} tris" : "";
        var vertPart = vertsIn > 0 ? $" · {vertsIn:N0}→{vertsOut:N0} verts" : "";
        // Folder-origin items always write in place (keeping pack subfolders),
        // so only report the stream/ destination when nothing was pinned in place.
        var anyInPlace = queue.Any(i => i.ForceInPlace);
        var outNote = UserSettings.IsServerModeActive() && !anyInPlace
            ? $" · written to {Path.Combine(UserSettings.LoadServerResourceFolder() ?? "", "stream")}"
            : " · written in place (next to each source)";
        var verb = canceled ? "Cancelled" : "Done";
        Summary =
            $"{verb} in {FormatDuration(sw.Elapsed)}. {ok} ok, {fail} failed · {FormatBytes(bytesIn)} → {FormatBytes(bytesOut)} ({savedPct:P0} smaller){triPart}{vertPart}{outNote}";
        _setStatus(Summary);
    }

    private void RunOneDrawable(OptimizeQueueItem item, OptimizeMode mode, string outDir)
    {
        var optimizer = new DrawableOptimizer();
        var opts = new DrawableOptimizer.Options(KeepRatio, PreserveBoundary);
        // In-place overwrite. DrawableOptimizer reads the whole resource into
        // memory before writing, so input == output is safe. The pre-run
        // dialog warns the user to back up first.
        var outPath = Path.Combine(outDir, Path.GetFileName(item.Path));
        var r = mode switch
        {
            OptimizeMode.Props    => optimizer.OptimizeYdr(item.Path, outPath, opts),
            OptimizeMode.Clothing => optimizer.OptimizeYdd(item.Path, outPath, opts),
            _ => throw new InvalidOperationException("RunOneDrawable called with non-drawable mode"),
        };
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            item.BytesBefore = r.BytesBefore;
            item.BytesAfter = r.BytesAfter;
            item.TrisBefore = r.TrianglesBefore;
            item.TrisAfter = r.TrianglesAfter;
            item.VertsBefore = r.VerticesBefore;
            item.VertsAfter = r.VerticesAfter;
            if (r.Error != null)
            {
                item.Error = r.Error;
                item.StatusKind = OptimizeStatusKind.Error;
                item.Status = Truncate(r.Error, 60);
            }
            else
            {
                item.StatusKind = OptimizeStatusKind.Ok;
                var texPart = r.TexturesOptimized > 0 ? $" · {r.TexturesOptimized} tex" : "";
                // Show the vertex delta too — at high reduction the triangle list
                // can be unchanged while verts (and file size) still drop.
                var vertPart = r.VerticesAfter > 0 && r.VerticesAfter != r.VerticesBefore
                    ? $" · {r.VerticesBefore:N0}→{r.VerticesAfter:N0} verts" : "";
                item.Status = $"{r.TrianglesBefore:N0}→{r.TrianglesAfter:N0} tris{vertPart}{texPart}";
            }
        });
    }

    /// <summary>Optimize ONLY the embedded textures of a drawable model
    /// (.ydd/.ydr/.yft), leaving its geometry untouched. Reuses the same
    /// DrawableOptimizer entry points as the geometry modes, but with a
    /// textures-only Options built from the shared YTD compression controls.</summary>
    private void RunOneEmbeddedTextures(OptimizeQueueItem item, string outDir)
    {
        var optimizer = new DrawableOptimizer();
        var opts = new DrawableOptimizer.Options(
            TargetRatio: 1.0,
            PreserveBoundary: true,
            OptimizeEmbeddedTextures: true,
            TextureDownsize: YtdDownsize,
            TextureFormatOptimization: YtdFormatOptimization,
            TextureSizeThreshold: YtdThresholdValues[YtdThresholdIndex],
            TexturesOnly: true,
            TextureMaxSize: YtdMaxSizeValues[YtdMaxSizeIndex]);

        // In-place overwrite (input == output is safe — the resource is read
        // fully into memory before writing). The pre-run dialog warns to back up.
        var outPath = Path.Combine(outDir, Path.GetFileName(item.Path));
        var ext = Path.GetExtension(item.Path).ToLowerInvariant();
        var r = ext switch
        {
            ".ydd" => optimizer.OptimizeYdd(item.Path, outPath, opts),
            ".ydr" => optimizer.OptimizeYdr(item.Path, outPath, opts),
            ".yft" => optimizer.OptimizeYft(item.Path, outPath, opts),
            _ => null,
        };

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (r == null)
            {
                item.StatusKind = OptimizeStatusKind.Skipped;
                item.Status = "unsupported file type";
                return;
            }
            item.BytesBefore = r.BytesBefore;
            item.BytesAfter = r.BytesAfter;
            if (r.Error != null)
            {
                item.Error = r.Error;
                item.StatusKind = OptimizeStatusKind.Error;
                item.Status = Truncate(r.Error, 60);
            }
            else if (r.TexturesOptimized == 0)
            {
                item.StatusKind = OptimizeStatusKind.Skipped;
                item.Status = "no textures touched";
            }
            else
            {
                item.StatusKind = OptimizeStatusKind.Ok;
                item.Status = $"{r.TexturesOptimized} tex · {FormatBytes(r.BytesBefore)} → {FormatBytes(r.BytesAfter)}";
            }
        });
    }

    private void RunOneYtd(OptimizeQueueItem item, string outDir, CancellationToken token)
    {
        var optimizer = new YtdOptimizer();
        var opts = new YtdOptimizer.Options(
            DownSize: YtdDownsize,
            FormatOptimization: YtdFormatOptimization,
            OptimizeSizeThreshold: YtdThresholdValues[YtdThresholdIndex],
            OnlyOversized: YtdOnlyOversized,
            BackupRoot: null,
            MaxSize: YtdMaxSizeValues[YtdMaxSizeIndex]);

        // YtdOptimizer scans a directory for *.ytd recursively, so we can't
        // point it at the source folder — it would re-process unrelated YTDs
        // sitting next to this one. Isolate the file in a unique temp dir,
        // run, then copy the result back over the original.
        var tempDir = Path.Combine(Path.GetTempPath(), "FiveOS_ytd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        long inputBytes = 0, outBytes = 0;
        YtdOptimizer.FileResult? match = null;
        try
        {
            var tempPath = Path.Combine(tempDir, Path.GetFileName(item.Path));
            File.Copy(item.Path, tempPath, overwrite: true);
            inputBytes = new FileInfo(tempPath).Length;

            var results = optimizer.Optimize(tempDir, opts,
                onFile: null, progress: null, cancel: token);
            match = results.FirstOrDefault(r => string.Equals(
                Path.GetFileName(r.Path), Path.GetFileName(tempPath), StringComparison.OrdinalIgnoreCase));

            var finalPath = Path.Combine(outDir, Path.GetFileName(item.Path));
            File.Copy(tempPath, finalPath, overwrite: true);
            outBytes = File.Exists(finalPath) ? new FileInfo(finalPath).Length : 0;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            item.BytesBefore = inputBytes;
            item.BytesAfter = outBytes;
            if (match?.Error != null)
            {
                item.Error = match.Error;
                item.StatusKind = OptimizeStatusKind.Error;
                item.Status = Truncate(match.Error, 60);
            }
            else if (match?.Skipped == true)
            {
                item.StatusKind = OptimizeStatusKind.Skipped;
                item.Status = "skipped (no triggers)";
            }
            else
            {
                item.StatusKind = OptimizeStatusKind.Ok;
                item.Status = $"{match?.TexturesOptimized ?? 0} tex · {FormatBytes(inputBytes)} → {FormatBytes(outBytes)}";
            }
        });
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static string FormatBytes(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:F1} KB";
        return $"{b / (1024.0 * 1024):F1} MB";
    }

    /// <summary>Compact elapsed/ETA formatting for the footer (e.g. "45s",
    /// "2m 05s", "1h 03m").</summary>
    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:D2}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:D2}s";
        return $"{t.Seconds}s";
    }
}

/// <summary>Outcome category for an optimize queue row. Drives the
/// status icon (no emojis — proper SymbolIcon in XAML), separate from
/// the human-readable detail text.</summary>
public enum OptimizeStatusKind { None, Running, Ok, Error, Skipped }

/// <summary>
/// One row in any of the four queues. Status / size / triangle fields are
/// populated by the optimizer dispatch as work progresses; the displays
/// below format whatever has been filled in so far.
/// </summary>
public partial class OptimizeQueueItem : ObservableObject
{
    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();

    /// <summary>True when this file was discovered inside a dropped/picked
    /// folder. Such items always write back next to their source so a pack's
    /// nested subfolders survive, even if server mode would otherwise redirect
    /// everything into a single flat stream/ folder. See ResolveOutputDir.</summary>
    public bool ForceInPlace { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    private string _status = "queued";

    /// <summary>Outcome flag the row template uses to pick a SymbolIcon
    /// + tint. Was previously baked into the Status string as a ✓ / ✗
    /// glyph; split out so XAML can render the proper icon glyph and
    /// the status text stays plain.</summary>
    [ObservableProperty] private OptimizeStatusKind _statusKind = OptimizeStatusKind.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    private long _bytesBefore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    private long _bytesAfter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrisDisplay))]
    private int _trisBefore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrisDisplay))]
    private int _trisAfter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VertsDisplay))]
    private int _vertsBefore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VertsDisplay))]
    private int _vertsAfter;

    [ObservableProperty] private string? _error;

    public OptimizeQueueItem(string path)
    {
        Path = path;
        try { _bytesBefore = new System.IO.FileInfo(path).Length; }
        catch { _bytesBefore = 0; }
    }

    public string StatusDisplay => Status;

    public string SizeDisplay
    {
        get
        {
            var inK = BytesBefore / 1024.0;
            if (BytesAfter <= 0) return $"{inK:F0} KB";
            var outK = BytesAfter / 1024.0;
            return $"{inK:F0} → {outK:F0} KB";
        }
    }

    public string TrisDisplay =>
        TrisAfter > 0 ? $"{TrisBefore:N0} → {TrisAfter:N0} tris" : "—";

    public string VertsDisplay =>
        VertsAfter > 0 ? $"{VertsBefore:N0} → {VertsAfter:N0} verts" : "—";
}
