// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using FiveOS.Services;

namespace FiveOS.ViewModels;

/// <summary>Row lifecycle for the Carcols Fixer results list.</summary>
public enum CarcolsRowStatus { Planned, Fixed, Error, Unfixable }

/// <summary>
/// Backs the "Carcols Fixer" tab: point at a FiveM resources folder, scan
/// every carcols.meta / carvariations.meta for modkit / siren / lightSettings
/// id collisions (plus duplicate kit names), preview the planned remaps, then
/// apply them in place with the usual timestamped Downloads backup.
///
/// Mirrors the repo's no-DI convention: constructed in MainViewModel with an
/// <c>Action&lt;string&gt;</c> status callback; user actions are public
/// methods called from the view's code-behind rather than ICommands.
/// </summary>
public partial class CarcolsFixerViewModel : ObservableObject
{
    private readonly Action<string> _setStatus;
    private readonly CarcolsFixerService _service = new();
    private CarcolsFixerService.ScanResult? _lastScan;
    private CancellationTokenSource? _cts;

    public CarcolsFixerViewModel(Action<string> setStatus)
    {
        _setStatus = setStatus;
        // Own folder setting, seeded from the shared server-resources folder
        // so txAdmin users land ready to scan — but browsing here never
        // stomps the txAdmin/output routing setting.
        _folder = UserSettings.LoadCarcolsScanFolder() ?? UserSettings.LoadServerResourceFolder();
        RefreshFolderDisplay();
    }

    public ObservableCollection<CarcolsConflictRow> Rows { get; } = new();

    // ─── Folder ──────────────────────────────────────────────────────────

    private string? _folder;

    [ObservableProperty] private string _folderDisplay = "";
    [ObservableProperty] private bool _hasFolder;

    public void SetFolder(string path)
    {
        _folder = path;
        UserSettings.SaveCarcolsScanFolder(path);
        RefreshFolderDisplay();
    }

    private void RefreshFolderDisplay()
    {
        HasFolder = _folder != null && Directory.Exists(_folder);
        FolderDisplay = HasFolder
            ? _folder!
            : "No resources folder set — click Browse to point at your server's /resources.";
        OnPropertyChanged(nameof(CanScan));
    }

    // ─── Options / lifecycle ─────────────────────────────────────────────

    [ObservableProperty] private bool _backupOriginals = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    [NotifyPropertyChangedFor(nameof(CanFix))]
    private bool _isProcessing;

    [ObservableProperty] private double _progress;

    [ObservableProperty]
    private string _summary = "Point FiveOS at your server's /resources folder and click Scan.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackup))]
    private string? _lastBackupRoot;

    public bool HasBackup => LastBackupRoot != null && Directory.Exists(LastBackupRoot);

    public bool CanScan => HasFolder && !IsProcessing;
    public int FixableCount => Rows.Count(r => r.Status == CarcolsRowStatus.Planned);
    public bool CanFix => FixableCount > 0 && !IsProcessing;
    public bool HasRows => Rows.Count > 0;

    /// <summary>Signals an in-flight scan/fix to stop. Safe to call when idle.</summary>
    public void RequestCancel()
    {
        _cts?.Cancel();
        Summary = "Cancelling…";
    }

    private void RaiseQueueState()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(FixableCount));
        OnPropertyChanged(nameof(CanFix));
    }

    // ─── Scan ────────────────────────────────────────────────────────────

    public async Task ScanAsync()
    {
        if (!CanScan) return;
        IsProcessing = true;
        Progress = 0;
        Rows.Clear();
        LastBackupRoot = null;
        _lastScan = null;
        Summary = "Scanning metas…";
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var folder = _folder!;
            var scan = await Task.Run(() => _service.Scan(
                folder, token,
                p => Marshal(() => Progress = p)), token);

            _lastScan = scan;
            foreach (var item in scan.Items.OrderBy(i => i.ResourceName, StringComparer.OrdinalIgnoreCase))
                Rows.Add(new CarcolsConflictRow(item));

            Summary = scan.Items.Count == 0
                ? $"No conflicts. {scan.CarcolsFileCount} carcols + {scan.CarvariationsFileCount} carvariations file(s) scanned — " +
                  $"{scan.KitCount} kits, {scan.SirenCount} sirens, {scan.LightCount} light settings, all ids unique."
                : $"{scan.Items.Count} conflict(s) across {scan.CarcolsFileCount} carcols file(s) — " +
                  $"review the planned remaps below, then Fix All. Nothing has been written yet.";
            _setStatus($"Carcols: {scan.Items.Count} conflict(s) found");
        }
        catch (OperationCanceledException)
        {
            Summary = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            Summary = "Scan failed: " + ex.Message;
            FosLogger.Warn("carcols", "scan failed", ex);
        }
        finally
        {
            IsProcessing = false;
            Progress = 0;
            _cts?.Dispose();
            _cts = null;
            RaiseQueueState();
        }
    }

    // ─── Fix ─────────────────────────────────────────────────────────────

    public async Task FixAsync()
    {
        if (!CanFix || _lastScan == null) return;
        IsProcessing = true;
        Progress = 0;
        Summary = "Applying remaps…";
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var scan = _lastScan;

        try
        {
            var byItem = Rows.ToDictionary(r => r.Item);
            var result = await Task.Run(() => _service.Apply(
                scan, BackupOriginals, token,
                (item, ok, error) => Marshal(() =>
                {
                    if (!byItem.TryGetValue(item, out var row)) return;
                    row.Status = ok ? CarcolsRowStatus.Fixed : CarcolsRowStatus.Error;
                    row.StatusText = ok ? "fixed" : Truncate(error ?? "failed", 70);
                })), token);

            LastBackupRoot = result.BackupRoot;
            var backupNote = result.BackupRoot != null ? " · originals backed up" : "";
            var errNote = result.Errors.Count > 0 ? $" · {result.Errors.Count} error(s) — see log" : "";
            Summary =
                $"Done. {result.EntriesFixed} id(s) remapped across {result.FilesChanged} file(s). " +
                $"Restart the affected resources to apply.{backupNote}{errNote}";
            _setStatus($"Carcols: {result.EntriesFixed} fixed, {result.FilesChanged} file(s) written");

            // A fixed tree needs a fresh scan before fixing again — the old
            // plan's in-memory documents are now stale.
            _lastScan = null;
        }
        catch (OperationCanceledException)
        {
            Summary = "Fix cancelled — re-scan before applying again.";
            _lastScan = null;
        }
        catch (Exception ex)
        {
            Summary = "Fix failed: " + ex.Message;
            FosLogger.Warn("carcols", "apply failed", ex);
            _lastScan = null;
        }
        finally
        {
            IsProcessing = false;
            Progress = 0;
            _cts?.Dispose();
            _cts = null;
            RaiseQueueState();
        }
    }

    public void ClearAll()
    {
        Rows.Clear();
        _lastScan = null;
        Progress = 0;
        LastBackupRoot = null;
        Summary = "Point FiveOS at your server's /resources folder and click Scan.";
        RaiseQueueState();
    }

    private static void Marshal(Action a)
    {
        var app = System.Windows.Application.Current;
        if (app != null) app.Dispatcher.Invoke(a);
        else a();
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

/// <summary>One planned remap (or unfixable finding) in the Carcols Fixer.</summary>
public partial class CarcolsConflictRow : ObservableObject
{
    public CarcolsConflictRow(CarcolsFixerService.PlanItem item)
    {
        Item = item;
        _status = item.CanFix ? CarcolsRowStatus.Planned : CarcolsRowStatus.Unfixable;
        _statusText = item.CanFix ? "planned" : (item.Note.Length > 0 ? item.Note : "can't auto-fix");
    }

    public CarcolsFixerService.PlanItem Item { get; }

    [ObservableProperty] private CarcolsRowStatus _status;
    [ObservableProperty] private string _statusText;

    // ─── Display helpers ─────────────────────────────────────────────────

    public string KindBadge => Item.Kind switch
    {
        CarcolsIdKind.ModKit => "MODKIT",
        CarcolsIdKind.Siren => "SIREN",
        CarcolsIdKind.LightSettings => "LIGHTS",
        _ => "KITNAME",
    };

    /// <summary>"12 → 65534" for id remaps; "old → new" names for renames.</summary>
    public string ChangeDisplay => Item.Kind == CarcolsIdKind.KitName
        ? $"{Item.OldName} → {Item.NewName}"
        : Item.CanFix ? $"{Item.OldId} → {Item.NewId}" : Item.OldId.ToString();

    public string EntryDisplay => $"{Item.ResourceName} · {Item.EntryName}";
    public string RefsDisplay => Item.RefUpdates > 0 ? $"+{Item.RefUpdates} ref(s)" : "";
    public string ConflictTooltip =>
        $"{Item.FilePath}\nClashes with: {Item.ConflictsWith}" +
        (Item.RefUpdates > 0 ? $"\nAlso rewrites {Item.RefUpdates} carvariations reference(s) in this resource." : "");
}
