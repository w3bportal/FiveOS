// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FiveOS.ViewModels;

/// <summary>One bone of the loaded vehicle skeleton, as listed in the
/// Vehicle Anim outliner. Key counts are echoed from the viewer.</summary>
/// <summary>One bone in the outliner. Vehicles fill <see cref="Kind"/>
/// (door / bonnet / wheel …); prop parts have none and show as plain parts.</summary>
public sealed partial class BoneRow : ObservableObject
{
    public string Name { get; init; } = "";
    public ushort Tag { get; init; }
    public string? Kind { get; init; }
    public int Depth { get; init; }
    public bool HasGeometry { get; init; }

    /// <summary>The part this one sits under in the source file, by name.
    /// Null at the top. The LAYERS tree nests by this, not by depth — a part
    /// whose own parents are filtered out still needs to land in the right
    /// branch.</summary>
    public string? ParentName { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyPropertyChangedFor(nameof(IsKeyed))]
    private int _keyCount;

    [ObservableProperty] private bool _isSelected;

    /// <summary>Outliner eye: unticked hides this bone's geometry (and every
    /// bone under it) in the viewport. View-only — never touches the clip.</summary>
    [ObservableProperty] private bool _isVisible = true;

    public bool IsKeyed => KeyCount > 0;
    /// <summary>Animatable in the everyday sense: has its own mesh, or is a
    /// known hinge/anchor bone. The rest are dummies (seats, lights…) that
    /// only show with "all bones" on.</summary>
    public bool IsInteresting => HasGeometry || Kind != null;
    public Thickness Indent => new(Depth * 10, 0, 0, 0);
    public string Glyph => Kind switch
    {
        "door_l" or "door_r" => "⌂",
        "bonnet" => "▲",
        "boot" => "▼",
        "wheel" => "◎",
        "extra" => "✦",
        "suspension" => "≡",
        "part" => "▪",
        _ => HasGeometry ? "▫" : "·",
    };
    public string Detail => KeyCount > 0 ? $"{KeyCount} key{(KeyCount == 1 ? "" : "s")}" : $"tag {Tag}";
}

/// <summary>
/// State for the Vehicle Animation editor. The viewer (WebView2) owns the
/// keys, the pose and playback; this mirrors what it echoes so the WPF
/// chrome (bone outliner, transform boxes, export toolbar) can bind to it.
/// VehicleAnimView does the plumbing in both directions.
/// </summary>
public sealed partial class BoneAnimViewModel : ObservableObject
{
    private readonly Action<string> _setStatus;

    public BoneAnimViewModel(Action<string> setStatus) => _setStatus = setStatus;

    public void SetStatus(string s)
    {
        StatusText = s;
        _setStatus(s);
    }

    // ── Model ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModel))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    private string? _modelPath;

    [ObservableProperty] private string _modelName = "";
    [ObservableProperty] private string _modelInfo = "";

    public bool HasModel => !string.IsNullOrEmpty(ModelPath);
    public bool ShowEmptyHint => !HasModel;

    // ── Bones ─────────────────────────────────────────────────────────────

    public ObservableCollection<BoneRow> Bones { get; } = new();
    public ObservableCollection<BoneRow> VisibleBones { get; } = new();

    [ObservableProperty] private string _boneFilter = "";
    partial void OnBoneFilterChanged(string value) => RefilterBones();

    [ObservableProperty] private bool _showAllBones;
    partial void OnShowAllBonesChanged(bool value) => RefilterBones();

    /// <summary>Outliner selection (mirrors the viewer's selected bone).</summary>
    [ObservableProperty] private BoneRow? _selectedBone;

    public void RefilterBones()
    {
        var f = (BoneFilter ?? "").Trim();
        // Default view: animatable bones + anything keyed/selected. Typing a
        // filter searches EVERY bone, dummies included.
        var keep = Bones.Where(b => f.Length > 0
                                    ? b.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                                    : (ShowAllBones || b.IsInteresting || b.IsKeyed || b.IsSelected))
                        .ToList();
        // Rebuilding drops the ListBox's selection, so leave it alone when the
        // visible set is unchanged — which is the common case on a state echo.
        if (keep.Count == VisibleBones.Count)
        {
            var same = true;
            for (int i = 0; i < keep.Count; i++)
                if (!ReferenceEquals(keep[i], VisibleBones[i])) { same = false; break; }
            if (same) { OnPropertyChanged(nameof(BoneCountText)); return; }
        }
        VisibleBones.Clear();
        foreach (var b in keep) VisibleBones.Add(b);
        OnPropertyChanged(nameof(BoneCountText));
    }

    public string BoneCountText => Bones.Count == 0 ? "" : $"{VisibleBones.Count} of {Bones.Count} bones";

    // ── Selected bone (echoed from the viewer) ────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedTitle))]
    private string? _selName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTitle))]
    private ushort _selTag;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTitle))]
    private int _selKeys;

    [ObservableProperty] private bool _selKeyHere;

    public bool HasSelection => !string.IsNullOrEmpty(SelName);
    public string SelectedTitle => HasSelection
        ? $"{SelName}  ·  tag {SelTag}  ·  {(SelKeys == 0 ? "no keys" : SelKeys + (SelKeys == 1 ? " key" : " keys"))}"
        : "Nothing selected";

    /// <summary>Rotation offset from the bone's rest pose, degrees, RAGE XYZ.</summary>
    [ObservableProperty] private double _rotX;
    [ObservableProperty] private double _rotY;
    [ObservableProperty] private double _rotZ;
    /// <summary>Position offset from rest, metres, RAGE XYZ.</summary>
    [ObservableProperty] private double _posX;
    [ObservableProperty] private double _posY;
    [ObservableProperty] private double _posZ;

    // ── Clip / timeline ───────────────────────────────────────────────────

    [ObservableProperty] private string _clipName = "";
    [ObservableProperty] private int _fps = 30;
    [ObservableProperty] private double _durationSec = 3;
    [ObservableProperty] private bool _loop = true;
    [ObservableProperty] private bool _autoKey = true;
    [ObservableProperty] private string _ease = "smooth";

    [ObservableProperty] private int _currentFrame;
    [ObservableProperty] private int _totalFrames = 90;
    [ObservableProperty] private string _timeText = "0 / 90 · 0.00s";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeysText))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private int _totalKeys;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeysText))]
    private int _keyedBoneCount;

    public string KeysText => TotalKeys == 0
        ? "No keys yet"
        : $"{TotalKeys} key{(TotalKeys == 1 ? "" : "s")} on {KeyedBoneCount} bone{(KeyedBoneCount == 1 ? "" : "s")}";
    public bool CanExport => HasModel && TotalKeys > 0 && !IsBusy;

    // ── Viewer options ────────────────────────────────────────────────────

    [ObservableProperty] private bool _showDots = true;
    [ObservableProperty] private bool _wireframe;

    // ── Output / session ──────────────────────────────────────────────────

    [ObservableProperty] private string? _outputFolder;
    [ObservableProperty] private string? _sessionPath;
    [ObservableProperty] private string? _lastExportPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = "";

    partial void OnModelPathChanged(string? value) => OnPropertyChanged(nameof(CanExport));

    /// <summary>Set while the view copies viewer state INTO these properties
    /// so the PropertyChanged forwarders don't bounce it straight back.</summary>
    public bool SyncingFromViewer { get; set; }

    /// <summary>Viewer key-mutation counter (object mode) — lets the host tell
    /// whether its export sidecar still matches the timeline.</summary>
    public int KeysRev { get; set; } = -1;
}
