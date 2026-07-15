// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using FiveOS.Services;

namespace FiveOS.ViewModels;

/// <summary>Outcome category for a txAdmin asset row — drives the status icon
/// in the view. Distinct from <see cref="OptimizeStatusKind"/> because this
/// flow has a "reduced but still over the line" middle state (Warn) the plain
/// optimize queue doesn't.</summary>
public enum TxRowStatus { None, Running, Ok, Warn, Error, Skipped }

/// <summary>
/// Backs the "txAdmin Optimizer" tab: paste/load a server console log, parse
/// out every "oversized asset" streaming warning, resolve each to a file under
/// the configured server resources folder, then batch-optimize them just
/// enough to clear the warning (or with manual controls). Reuses
/// <see cref="TxAdminLogParser"/>, <see cref="ServerAssetResolver"/> and
/// <see cref="TxAdminAutoOptimizer"/>.
///
/// Mirrors the repo's no-DI convention: constructed in MainViewModel with an
/// <c>Action&lt;string&gt;</c> status callback; user actions are public methods
/// called from the view's code-behind rather than ICommands.
/// </summary>
public partial class TxAdminOptimizeViewModel : ObservableObject
{
    private readonly Action<string> _setStatus;
    private readonly TxAdminAutoOptimizer _optimizer = new();

    // {8192,4096,2048,1024,512} — the W+H trigger presets for Manual mode,
    // mirroring the labels in the view's threshold combo.
    private static readonly ushort[] ManualThresholdValues = { 8192, 4096, 2048, 1024, 512 };

    public TxAdminOptimizeViewModel(Action<string> setStatus)
    {
        _setStatus = setStatus;
        RefreshServerFolder();
    }

    public ObservableCollection<TxAdminAssetRow> Rows { get; } = new();

    // ─── Log input ───────────────────────────────────────────────────────

    [ObservableProperty] private string _logText = "";

    [ObservableProperty]
    private string _summary = "Paste your txAdmin / server console log, then Parse.";

    // ─── Server folder ───────────────────────────────────────────────────

    [ObservableProperty] private string _serverFolderDisplay = "";
    [ObservableProperty] private bool _hasServerFolder;

    /// <summary>Re-read the configured server resources folder, drop the
    /// resolver cache, and re-resolve any rows already parsed.</summary>
    public void RefreshServerFolder()
    {
        var root = ServerAssetResolver.ServerRoot();
        HasServerFolder = root != null;
        ServerFolderDisplay = root ?? "No server resources folder set — click Browse to point at your /resources.";
        ServerAssetResolver.InvalidateCache();
        if (Rows.Count > 0) ResolveAll();
    }

    // ─── Options ─────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualMode))]
    private bool _autoMode = true;

    public bool IsManualMode => !AutoMode;

    /// <summary>0 = Just-enough (quality-first), 1 = Fully clear, 2 = Severe only.</summary>
    [ObservableProperty] private int _aggressivenessIndex;

    [ObservableProperty] private bool _manualDownsize = true;
    [ObservableProperty] private bool _manualFormatOpt;

    /// <summary>Index into <see cref="ManualThresholdValues"/>; default 2 = 2048 (1K+).</summary>
    [ObservableProperty] private int _manualThresholdIndex = 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualKeepRatioDisplay))]
    private double _manualKeepRatio = 0.6;

    public string ManualKeepRatioDisplay => $"{ManualKeepRatio * 100:F0}% tris kept";

    [ObservableProperty] private bool _backupOriginals = true;

    // ─── Lifecycle ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProcess))]
    private bool _isProcessing;

    [ObservableProperty] private double _progress;

    /// <summary>Backup root created by the last run (null if backups were off
    /// or none were written). Drives the footer's "Open backup" button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackup))]
    private string? _lastBackupRoot;

    public bool HasBackup => LastBackupRoot != null && Directory.Exists(LastBackupRoot);

    public int RowCount => Rows.Count;
    public bool HasRows => RowCount > 0;
    public int ActionableCount => Rows.Count(r => r.Found && r.Supported);
    public bool CanProcess => ActionableCount > 0 && !IsProcessing;

    // ─── Parse + resolve ─────────────────────────────────────────────────

    /// <summary>Parse the pasted log into rows and resolve each to a file.</summary>
    public void ParseAndResolve()
    {
        Rows.Clear();
        var warnings = TxAdminLogParser.Parse(LogText);
        foreach (var w in warnings)
            Rows.Add(new TxAdminAssetRow(w));

        ResolveAll();

        if (warnings.Count == 0)
        {
            Summary = "No streaming warnings found in that text. Paste the full console log including the \"uses … MiB of physical memory\" lines.";
        }
        RaiseQueueState();
        _setStatus($"txAdmin: parsed {warnings.Count} warning(s)");
    }

    /// <summary>(Re)resolve every row against the current server folder and
    /// refresh the summary counts.</summary>
    private void ResolveAll()
    {
        foreach (var row in Rows)
            row.Resolve();

        int resolved = Rows.Count(r => r.Found);
        int unsupported = Rows.Count(r => !r.Supported);
        int missing = Rows.Count(r => r.Supported && !r.Found);
        Summary = Rows.Count == 0
            ? Summary
            : $"{Rows.Count} warning(s) · {resolved} resolved · {missing} not found · {unsupported} unsupported type";
        RaiseQueueState();
    }

    public void ClearAll()
    {
        LogText = "";
        Rows.Clear();
        Progress = 0;
        LastBackupRoot = null;
        Summary = "Paste your txAdmin / server console log, then Parse.";
        RaiseQueueState();
    }

    private void RaiseQueueState()
    {
        OnPropertyChanged(nameof(RowCount));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ActionableCount));
        OnPropertyChanged(nameof(CanProcess));
    }

    // ─── Run ─────────────────────────────────────────────────────────────

    public async Task RunAsync()
    {
        if (!CanProcess) return;
        IsProcessing = true;
        Progress = 0;
        LastBackupRoot = null;
        Summary = "Optimizing…";

        var targets = Rows.Where(r => r.Found && r.Supported).ToList();
        var plan = new TxAdminAutoOptimizer.Plan(
            Auto: AutoMode,
            Level: (TxAdminAutoOptimizer.Aggressiveness)AggressivenessIndex,
            ManualThreshold: ManualThresholdValues[Math.Clamp(ManualThresholdIndex, 0, ManualThresholdValues.Length - 1)],
            ManualDownsize: ManualDownsize,
            ManualFormatOpt: ManualFormatOpt,
            ManualKeepRatio: ManualKeepRatio);

        // One backup root per run, created lazily on the first real write.
        string? backupRoot = BackupOriginals
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "FiveOS_txAdmin_Backup_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"))
            : null;

        int cleared = 0, reduced = 0, failed = 0, skipped = 0;
        float mibBefore = 0, mibAfter = 0;

        try
        {
        await Task.Run(() =>
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var row = targets[i];
                Marshal(() => { row.RowStatus = TxRowStatus.Running; row.Status = "optimizing…"; });

                try
                {
                    if (backupRoot != null) BackupOriginal(row, backupRoot);

                    var outcome = _optimizer.Optimize(row.ResolvedPath!, row.Ext, row.MemKind, plan);

                    mibBefore += outcome.BeforeMiB;
                    mibAfter += outcome.Changed ? outcome.AfterMiB : outcome.BeforeMiB;

                    Marshal(() =>
                    {
                        row.AfterMiB = outcome.Changed ? outcome.AfterMiB : 0;
                        if (outcome.Error != null)
                        {
                            row.RowStatus = TxRowStatus.Error;
                            row.Status = Truncate(outcome.Error, 70);
                            failed++;
                        }
                        else if (outcome.Cleared)
                        {
                            row.RowStatus = TxRowStatus.Ok;
                            row.Status = outcome.Detail;
                            cleared++;
                        }
                        else if (outcome.Changed)
                        {
                            row.RowStatus = TxRowStatus.Warn;
                            row.Status = outcome.Detail;
                            reduced++;
                        }
                        else
                        {
                            row.RowStatus = TxRowStatus.Skipped;
                            row.Status = outcome.Detail;
                            skipped++;
                        }
                    });
                }
                catch (Exception ex)
                {
                    Marshal(() =>
                    {
                        row.RowStatus = TxRowStatus.Error;
                        row.Status = Truncate(ex.Message, 70);
                        failed++;
                    });
                }

                int done = i + 1;
                Marshal(() =>
                {
                    Progress = done / (double)targets.Count;
                    _setStatus($"txAdmin: {done}/{targets.Count} · {cleared} cleared · {reduced} reduced · {failed} failed");
                });
            }
        });
        }
        finally { IsProcessing = false; Progress = 0; }  // never leave the run stuck

        var savedPct = mibBefore > 0 ? 1.0 - (mibAfter / mibBefore) : 0;
        // Surface the backup path through the dedicated "Open backup" button
        // rather than crammed into the ellipsized summary line.
        LastBackupRoot = (backupRoot != null && Directory.Exists(backupRoot)) ? backupRoot : null;
        var backupNote = LastBackupRoot != null ? " · originals backed up" : "";
        Summary =
            $"Done. {cleared} cleared, {reduced} reduced (still over), {skipped} unchanged, {failed} failed · " +
            $"{mibBefore:F0} → {mibAfter:F0} MiB ({savedPct:P0} smaller). Restart the affected resources to apply.{backupNote}";
        _setStatus(Summary);
    }

    private static void BackupOriginal(TxAdminAssetRow row, string backupRoot)
    {
        try
        {
            var dst = Path.Combine(backupRoot, row.ResourceName, row.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(row.ResolvedPath!, dst, overwrite: true);
        }
        catch (Exception ex)
        {
            FosLogger.Warn("txadmin", $"backup failed for {row.FileName}", ex);
        }
    }

    private static void Marshal(Action a)
    {
        var app = System.Windows.Application.Current;
        if (app != null) app.Dispatcher.Invoke(a);
        else a();
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

/// <summary>One parsed-and-resolved asset row in the txAdmin tab.</summary>
public partial class TxAdminAssetRow : ObservableObject
{
    private static readonly HashSet<string> SupportedExts =
        new(StringComparer.OrdinalIgnoreCase) { ".ytd", ".ydr", ".ydd", ".yft" };

    public TxAdminAssetRow(TxAdminWarning w)
    {
        ResourceName = w.ResourceName;
        FileName = w.FileName;
        Ext = w.Ext;
        MemKind = w.MemKind;
        ReportedMiB = w.SizeMiB;
        _beforeMiB = w.SizeMiB;
        Supported = SupportedExts.Contains(w.Ext);
    }

    public string ResourceName { get; }
    public string FileName { get; }
    public string Ext { get; }
    public AssetMemKind MemKind { get; }
    public float ReportedMiB { get; }
    public bool Supported { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Found))]
    private string? _resolvedPath;

    public bool Found => ResolvedPath != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    private float _beforeMiB;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    private float _afterMiB;

    [ObservableProperty] private TxRowStatus _rowStatus = TxRowStatus.None;
    [ObservableProperty] private string _status = "ready";

    /// <summary>Resolve this row's file under the configured server folder and
    /// set the initial status text accordingly.</summary>
    public void Resolve()
    {
        if (!Supported)
        {
            ResolvedPath = null;
            RowStatus = TxRowStatus.Skipped;
            Status = "no FiveOS optimizer for this type";
            return;
        }
        ResolvedPath = ServerAssetResolver.Resolve(ResourceName, FileName);
        RowStatus = TxRowStatus.None;
        Status = Found ? "ready" : "not found in server folder";
    }

    // ─── Display helpers ─────────────────────────────────────────────────

    public string TypeBadge => Ext.TrimStart('.').ToUpperInvariant();
    public string MemBadge => MemKind == AssetMemKind.Physical ? "PHYS" : "VIRT";
    public string AssetRefDisplay => $"{ResourceName}/{FileName}";

    public string SizeDisplay => AfterMiB > 0
        ? $"{BeforeMiB:F1} → {AfterMiB:F1} MiB"
        : $"{BeforeMiB:F1} MiB";
}
