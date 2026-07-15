// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using FiveOS.Services;

namespace FiveOS.ViewModels;

/// <summary>Outcome of a single file in the pack — drives the row icon.</summary>
public enum RpfRowStatus { Packed, Skipped, Error }

/// <summary>
/// Backs the "RPF" tab (Phase 1 — raw packer): point at a loose FiveM
/// resource folder and pack its stream assets + meta into a single OPEN
/// (unencrypted) .rpf via <see cref="RpfPacker"/>. Singleplayer dlc.rpf
/// scaffolding (content.xml / setup2.xml, ped/clothing focus) is Phase 2.
///
/// Follows the repo's no-DI convention: constructed in MainViewModel with an
/// <c>Action&lt;string&gt;</c> status callback; user actions are public methods
/// called from the view's code-behind rather than ICommands.
/// </summary>
public partial class RpfConverterViewModel : ObservableObject
{
    private readonly Action<string> _setStatus;
    private readonly RpfPacker _packer = new();

    public RpfConverterViewModel(Action<string> setStatus) => _setStatus = setStatus;

    public ObservableCollection<RpfFileRow> Rows { get; } = new();

    // ─── Input / output ──────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInput))]
    [NotifyPropertyChangedFor(nameof(CanConvert))]
    [NotifyPropertyChangedFor(nameof(InputDisplay))]
    private string _inputFolder = "";

    [ObservableProperty] private string _outputPath = "";

    public bool HasInput => !string.IsNullOrWhiteSpace(InputFolder) && Directory.Exists(InputFolder);

    public string InputDisplay => HasInput
        ? InputFolder
        : "No folder selected — drop a FiveM resource folder here or click Browse.";

    // ─── Output mode ─────────────────────────────────────────────────────

    /// <summary>Output mode index (matches the combo order):
    /// 0 = raw packed .rpf; 1 = singleplayer ped dlc.rpf; 2 = singleplayer ped
    /// OpenIV mods-folder tree; 3 = ADD-ON → server resource (FiveM, new named
    /// asset + ytyp, replaces nothing); 4 = REPLACE an existing asset → server
    /// resource (FiveM, reliable); 5 = REPLACE → client-side overlay (FiveM,
    /// local-only). (SP car → FiveM moved to its own Vehicles tab.)
    /// Index, not enum, so the ComboBox binds simply.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRawMode))]
    [NotifyPropertyChangedFor(nameof(IsSpMode))]
    [NotifyPropertyChangedFor(nameof(IsOpenIvModsMode))]
    [NotifyPropertyChangedFor(nameof(IsAddonMode))]
    [NotifyPropertyChangedFor(nameof(IsReplaceServer))]
    [NotifyPropertyChangedFor(nameof(IsReplaceClient))]
    [NotifyPropertyChangedFor(nameof(IsDlcMode))]
    [NotifyPropertyChangedFor(nameof(IsReplaceMode))]
    [NotifyPropertyChangedFor(nameof(IsFolderOutput))]
    [NotifyPropertyChangedFor(nameof(ConvertButtonText))]
    [NotifyPropertyChangedFor(nameof(OutputLabel))]
    private int _outputModeIndex;

    public bool IsRawMode => OutputModeIndex == 0;
    public bool IsSpMode => OutputModeIndex == 1;
    public bool IsOpenIvModsMode => OutputModeIndex == 2;
    public bool IsAddonMode => OutputModeIndex == 3;
    public bool IsReplaceServer => OutputModeIndex == 4;
    public bool IsReplaceClient => OutputModeIndex == 5;

    /// <summary>Ped-dlc add-on modes (SP dlc.rpf or OpenIV mods tree) — run the scaffolder.</summary>
    public bool IsDlcMode => IsSpMode || IsOpenIvModsMode;
    /// <summary>Replace-existing-asset modes — run ReplaceBuilder.</summary>
    public bool IsReplaceMode => IsReplaceServer || IsReplaceClient;
    /// <summary>Anything whose output is a FOLDER (everything except raw .rpf).</summary>
    public bool IsFolderOutput => !IsRawMode;

    /// <summary>Target asset to replace (vanilla stem, e.g. prop_phone_ing) — replace modes only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConvert))]
    private string _targetAssetName = "";

    /// <summary>Optional NEW archetype name for add-on mode (single-model
    /// inputs only). Empty keeps the model's own filename.</summary>
    [ObservableProperty]
    private string _newAssetName = "";

    public string ConvertButtonText =>
        IsAddonMode ? "BUILD ADD-ON"
        : IsReplaceMode ? "BUILD REPLACEMENT" : IsDlcMode ? "BUILD DLC.RPF" : "CONVERT TO RPF";
    public string OutputLabel => IsFolderOutput ? "OUTPUT FOLDER" : "OUTPUT .RPF";

    partial void OnOutputModeIndexChanged(int value)
    {
        RecomputeDefaultOutput();
        RaiseState();
    }

    /// <summary>Manual-review reasons + warnings surfaced for the SP-DLC path
    /// (e.g. "this is freemode clothing"). Empty for a clean raw pack.</summary>
    public ObservableCollection<string> ReviewNotes { get; } = new();
    public bool HasReviewNotes => ReviewNotes.Count > 0;

    // ─── Options ─────────────────────────────────────────────────────────

    /// <summary>false (default): only RAGE assets (stream files + meta) go in.
    /// true: pack everything except hard cruft (still skips lua/manifest).</summary>
    [ObservableProperty] private bool _includeAllFiles;

    /// <summary>Drop a leading <c>stream\</c> so assets land at the RPF root.</summary>
    [ObservableProperty] private bool _flattenStreamFolder = true;

    // ─── Lifecycle ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConvert))]
    private bool _isProcessing;

    [ObservableProperty]
    private string _summary = "Drop a FiveM resource folder (or Browse) to begin.";

    /// <summary>Last produced output path (the .rpf, dlc.rpf, or replacement
    /// pack) — drives the footer's "Open output" button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    private string _lastOutputPath = "";

    public bool HasOutput => !string.IsNullOrWhiteSpace(LastOutputPath)
        && (File.Exists(LastOutputPath) || Directory.Exists(LastOutputPath));

    public bool HasRows => Rows.Count > 0;
    public bool CanConvert => HasInput && !IsProcessing
        && (!IsReplaceMode || !string.IsNullOrWhiteSpace(TargetAssetName));

    /// <summary>Set the input folder and default an output path beside it
    /// (<c>&lt;parent&gt;\&lt;foldername&gt;.rpf</c>) if the user hasn't picked one.</summary>
    public void SetInputFolder(string? folder)
    {
        InputFolder = folder ?? "";
        if (HasInput) RecomputeDefaultOutput();
        else Summary = "Folder not found.";
        RaiseState();
    }

    /// <summary>Default the output path beside the input, per mode: a
    /// <c>&lt;name&gt;.rpf</c> file for raw, or a <c>&lt;name&gt;_dlcpack</c> output
    /// folder for the SP DLC (the scaffolder creates <c>&lt;NAME&gt;/dlc.rpf</c>
    /// under it — a sibling so it never collides with the input folder).</summary>
    private void RecomputeDefaultOutput()
    {
        if (!HasInput) { OutputPath = ""; return; }
        var trimmed = InputFolder.TrimEnd('\\', '/');
        var name = new DirectoryInfo(trimmed).Name;
        var parent = Path.GetDirectoryName(trimmed) ?? trimmed;
        if (IsReplaceClient)
        {
            // Client overlay produces mods\<pack>.rpf — drop into FiveM.app.
            // Default straight there if found.
            OutputPath = FiveMAppDir() ?? Path.Combine(parent, name + "_replace");
            Summary = "Ready — builds a CLIENT-SIDE replacement (local only). Enter the asset to replace.";
        }
        else if (IsReplaceServer)
        {
            OutputPath = Path.Combine(parent, name + "_replace");
            Summary = "Ready — builds a server-side replacement resource. Enter the asset to replace.";
        }
        else if (IsAddonMode)
        {
            OutputPath = Path.Combine(parent, name + "_addon");
            Summary = "Ready — builds a FiveM ADD-ON resource (new spawnable asset, replaces nothing).";
        }
        else if (IsOpenIvModsMode)
        {
            OutputPath = Path.Combine(parent, name + "_openiv_mods");
            Summary = $"Ready — builds a singleplayer OpenIV mods-folder tree from {name}";
        }
        else if (IsSpMode)
        {
            OutputPath = Path.Combine(parent, name + "_dlcpack");
            Summary = $"Ready — builds a singleplayer dlc.rpf from {name}";
        }
        else
        {
            OutputPath = Path.Combine(parent, name + ".rpf");
            Summary = $"Ready — packs into {Path.GetFileName(OutputPath)}";
        }
    }

    /// <summary>The local FiveM client install dir, or null if not found.</summary>
    internal static string? FiveMAppDir()
    {
        try
        {
            var p = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FiveM", "FiveM.app");
            return Directory.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    public void Clear()
    {
        InputFolder = "";
        OutputPath = "";
        Rows.Clear();
        Summary = "Drop a FiveM resource folder (or Browse) to begin.";
        RaiseState();
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(HasInput));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(CanConvert));
        OnPropertyChanged(nameof(InputDisplay));
    }

    // ─── Run ─────────────────────────────────────────────────────────────

    public async Task ConvertAsync()
    {
        if (!CanConvert) return;
        IsProcessing = true;
        Rows.Clear();
        ReviewNotes.Clear();
        OnPropertyChanged(nameof(HasReviewNotes));
        Summary = IsAddonMode ? "Building add-on…"
            : IsReplaceMode ? "Building replacement…" : IsDlcMode ? "Building dlc.rpf…" : "Packing…";
        RaiseState();

        var input = InputFolder;
        try
        {
            if (IsAddonMode) await RunAddonAsync(input);
            else if (IsReplaceMode) await RunReplaceAsync(input, IsReplaceClient);
            else if (IsDlcMode) await RunSpAsync(input, IsOpenIvModsMode);
            else await RunRawAsync(input);
        }
        catch (Exception ex)
        {
            Summary = "Failed: " + ex.Message;
            _setStatus(Summary);
        }
        finally  // never leave the Convert button stuck disabled on an exception
        {
            IsProcessing = false;
            OnPropertyChanged(nameof(HasReviewNotes));
            RaiseState();
        }
    }

    private async Task RunRawAsync(string input)
    {
        var output = string.IsNullOrWhiteSpace(OutputPath)
            ? Path.Combine(Path.GetDirectoryName(input.TrimEnd('\\', '/')) ?? input,
                           new DirectoryInfo(input.TrimEnd('\\', '/')).Name + ".rpf")
            : OutputPath;
        var opts = new RpfPacker.Options(IncludeAllFiles, FlattenStreamFolder);

        RpfPacker.Result? result = null;
        await Task.Run(() =>
            // Status lines stream in from the worker thread; post them
            // non-blocking so per-file logging never stalls the pack.
            result = _packer.Pack(input, output, opts, msg => PostStatus(msg)));

        if (result == null) return;
        if (result.Error == null) LastOutputPath = result.RpfPath;
        foreach (var f in result.Files)
            Rows.Add(new RpfFileRow(f));
        Summary = result.Error != null
            ? $"Failed: {result.Error}"
            : $"Done. {result.Packed} packed, {result.Skipped} skipped, {result.Failed} failed → "
              + $"{Path.GetFileName(result.RpfPath)} ({HumanBytes(result.RpfBytes)}).";
        _setStatus(Summary);
    }

    private async Task RunSpAsync(string input, bool openIvModsLayout)
    {
        var outRoot = string.IsNullOrWhiteSpace(OutputPath)
            ? (Path.GetDirectoryName(input.TrimEnd('\\', '/')) ?? input)
            : OutputPath;

        var scaffolder = new PedDlcScaffolder();
        var opts = new PedDlcScaffolder.Options(FiveMModsLayout: openIvModsLayout);
        PedDlcScaffolder.Result? res = null;
        await Task.Run(() =>
            res = scaffolder.Scaffold(input, outRoot, opts, msg => PostStatus(msg)));

        if (res == null) return;
        if (res.Success && res.DlcRpfPath != null) LastOutputPath = res.DlcRpfPath;
        foreach (var f in res.Files)
            Rows.Add(new RpfFileRow(f));
        foreach (var w in res.Warnings) ReviewNotes.Add("⚠ " + w);
        foreach (var r in res.ManualReviewReasons) ReviewNotes.Add("• " + r);

        var okInstall = openIvModsLayout
            ? $"Built OpenIV mods-folder tree — {res.PedNames.Count} ped(s): {string.Join(", ", res.PedNames)}. "
              + $"Copy the 'mods' folder into your GTA V install (OpenIV mods enabled), then add <Item>dlcpacks:/{res.DlcName}/</Item> to dlclist.xml in mods\\update\\update.rpf (see the README)."
            : $"Built {res.DlcName}\\dlc.rpf — {res.PedNames.Count} ped(s): {string.Join(", ", res.PedNames)}. "
              + $"Drop the {res.DlcName} folder into mods\\update\\x64\\dlcpacks and add it to dlclist.xml (see the README beside it).";

        Summary = res.Error != null
            ? $"Failed: {res.Error}"
            : res.Success
                ? okInstall
                : res.Classification == PedDlcScaffolder.Classification.FreemodeClothing
                    ? "Not converted — this resource is freemode/EUP component clothing, which uses a different SP mechanism and needs manual review (see notes)."
                    : "No standalone ped found to convert — see notes.";
        _setStatus(Summary);
    }

    private async Task RunAddonAsync(string input)
    {
        var outRoot = string.IsNullOrWhiteSpace(OutputPath)
            ? (Path.GetDirectoryName(input.TrimEnd('\\', '/')) ?? input)
            : OutputPath;

        var builder = new AddonResourceBuilder();
        var opts = new AddonResourceBuilder.Options(NewAssetName);
        AddonResourceBuilder.Result? res = null;
        await Task.Run(() => res = builder.Build(input, outRoot, opts, msg => PostStatus(msg)));

        if (res == null) return;
        if (res.Success && res.OutputPath != null) LastOutputPath = res.OutputPath;
        foreach (var p in res.ProducedFiles)
            Rows.Add(new RpfFileRow(new RpfPacker.FileResult(
                "", p, 0,
                p.EndsWith(".ydr", StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase),
                true, null, null)));
        foreach (var w in res.Warnings) ReviewNotes.Add("⚠ " + w);

        Summary = res.Error != null
            ? $"Failed: {res.Error}"
            : res.Success
                ? $"Built ADD-ON resource — {res.ArchetypeNames.Count} new archetype(s): {string.Join(", ", res.ArchetypeNames)}. "
                  + "Drop it into resources/, `ensure` it, then spawn by name (nothing vanilla is replaced)."
                : "Add-on build failed — see status.";
        _setStatus(Summary);
    }

    private async Task RunReplaceAsync(string input, bool clientSide)
    {
        if (string.IsNullOrWhiteSpace(TargetAssetName))
        {
            Summary = "Enter the vanilla asset name to replace (e.g. prop_phone_ing).";
            return;
        }
        var outRoot = string.IsNullOrWhiteSpace(OutputPath)
            ? (Path.GetDirectoryName(input.TrimEnd('\\', '/')) ?? input)
            : OutputPath;

        var builder = new ReplaceBuilder();
        var output = clientSide ? ReplaceBuilder.Output.ClientOverlay : ReplaceBuilder.Output.ServerResource;
        var opts = new ReplaceBuilder.Options(TargetAssetName, output);
        ReplaceBuilder.Result? res = null;
        await Task.Run(() => res = builder.Build(input, outRoot, opts, msg => PostStatus(msg)));

        if (res == null) return;
        if (res.Success && res.OutputPath != null) LastOutputPath = res.OutputPath;
        foreach (var p in res.ProducedFiles)
            Rows.Add(new RpfFileRow(new RpfPacker.FileResult(
                "", p, 0, p.EndsWith(".ydr", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase),
                true, null, null)));
        foreach (var w in res.Warnings) ReviewNotes.Add("⚠ " + w);

        Summary = res.Error != null
            ? $"Failed: {res.Error}"
            : res.Success
                ? clientSide
                    ? $"Built CLIENT-SIDE overlay replacing '{res.TargetAssetName}'. Copy the 'mods' folder into FiveM.app — LOCAL ONLY (see the warning + README)."
                    : $"Built server resource replacing '{res.TargetAssetName}'. Drop it into resources/ and `ensure` it — everyone on the server sees it."
                : "Replacement failed — see status.";
        _setStatus(Summary);
    }

    internal static string HumanBytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b:N0} B" : $"{v:0.##} {u[i]}";
    }

    private void PostStatus(string msg)
    {
        var app = System.Windows.Application.Current;
        // Surface each worker message BOTH in the global status bar and in
        // this tab's own footer (Summary), so progress is visible in-context
        // and not only in the shared bar at the bottom of the window. The
        // final result line overwrites Summary when the run finishes.
        if (app != null) app.Dispatcher.BeginInvoke(() => { _setStatus(msg); if (IsProcessing) Summary = msg; });
        else { _setStatus(msg); if (IsProcessing) Summary = msg; }
    }
}

/// <summary>One file's outcome in the pack result list.</summary>
public sealed class RpfFileRow
{
    public RpfFileRow(RpfPacker.FileResult f)
    {
        ArchivePath = f.ArchivePath;
        var ext = Path.GetExtension(f.ArchivePath).TrimStart('.').ToUpperInvariant();
        TypeBadge = string.IsNullOrEmpty(ext) ? "?" : ext;

        if (f.Error != null) { Status = RpfRowStatus.Error; Detail = f.Error; KindBadge = "—"; }
        else if (f.Skipped != null) { Status = RpfRowStatus.Skipped; Detail = f.Skipped; KindBadge = "—"; }
        else { Status = RpfRowStatus.Packed; Detail = ""; KindBadge = f.IsResource ? "RES" : "BIN"; }

        SizeDisplay = f.Packed ? RpfConverterViewModel.HumanBytes(f.Bytes) : "";
    }

    public string ArchivePath { get; }
    public string TypeBadge { get; }
    public string KindBadge { get; }
    public string SizeDisplay { get; }
    public RpfRowStatus Status { get; }
    public string Detail { get; }
}
