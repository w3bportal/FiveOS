// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FiveOS.Services;

namespace FiveOS.ViewModels;

public partial class PoseBoneEntry : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _parentName = "";
    [ObservableProperty] private int _index;
    [ObservableProperty] private bool _isModified;
    [ObservableProperty] private bool _isSelected;
    /// <summary>True when an imported clip drives this bone (Outliner
    /// green dot). Cleared when no clip drive-map is active.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriveTooltip))]
    private bool _isDriven;
    /// <summary>Source bone name from the clip (e.g. Mixamo/CC), shown in
    /// the row tooltip when <see cref="IsDriven"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriveTooltip))]
    private string _driveSource = "";
    /// <summary>A clip drive-map is active (greys out undriven bones).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriveTooltip))]
    private bool _hasDriveMap;
    /// <summary>Hidden by the outliner's search filter. Bound to the bone
    /// row's Visibility so a non-matching bone collapses without
    /// reshuffling the underlying ordered list.</summary>
    [ObservableProperty] private bool _isHidden;

    public string DriveTooltip
    {
        get
        {
            if (!HasDriveMap) return Name;
            if (IsDriven)
                return string.IsNullOrEmpty(DriveSource)
                    ? $"{Name} — driven by clip"
                    : $"{Name} ← {DriveSource}";
            return $"{Name} — at rest (not in clip)";
        }
    }
    /// <summary>Auto-classified body region — used by the outliner so the
    /// user sees "Arms (L)", "Legs (R)", etc. instead of a flat 80-bone
    /// wall. Also drives [[pose_bone_group]] membership.</summary>
    [ObservableProperty] private string _group = "Other";
    /// <summary>Stable within-group sort hint. Matches the order limbs are
    /// usually read (proximal -> distal): clavicle, upper-arm, forearm,
    /// hand, fingers. Falls back to alphabetical when patterns don't match.</summary>
    [ObservableProperty] private int _sortKey;
}

/// <summary>One collapsible row in the Blender-style outliner. Owns a
/// child <see cref="Bones"/> collection so the XAML can render a nested
/// tree, plus per-group expand / visibility / lock state. Visibility +
/// lock are echoed to the viewer so the joint spheres for that region
/// can be hidden or made non-pickable.</summary>
public partial class PoseBoneGroup : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isLocked;
    /// <summary>Whole group is hidden because the outliner search filter
    /// excluded every child bone. Collapses the group row itself, not
    /// just its children, so the tree shrinks instead of leaving empty
    /// section headers.</summary>
    [ObservableProperty] private bool _isFilteredOut;
    public ObservableCollection<PoseBoneEntry> Bones { get; } = new();
    public int Count => Bones.Count;
    public string CountLabel => Bones.Count.ToString();
    public void NotifyCountChanged()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountLabel));
    }
}

/// <summary>
/// Classifies a bone name into a body region and sort weight. Handles GTA's
/// <c>SKEL_*</c> convention, Mixamo's <c>mixamorig:*</c> convention, VRM's
/// <c>J_Bip_*</c> convention, and falls back to keyword matching ("Spine",
/// "Hand", "Finger", "L" / "R" markers) for anything else.
/// </summary>
public static class BoneGroupClassifier
{
    public static (string Group, int Sort) Classify(string raw)
    {
        var n = (raw ?? "").Replace(":", "_").ToLowerInvariant();

        // Side detection — order matters: more-specific patterns first.
        bool isL = HasMarker(n, "_l_") || HasMarker(n, "_left_") || n.Contains("left")
                   || n.EndsWith("_l") || n.EndsWith(".l");
        bool isR = HasMarker(n, "_r_") || HasMarker(n, "_right_") || n.Contains("right")
                   || n.EndsWith("_r") || n.EndsWith(".r");

        // Fingers — must check before generic "hand/arm" since finger bones
        // often live under hand and would otherwise sweep into Arms.
        if (n.Contains("finger") || n.Contains("thumb") || n.Contains("index")
            || n.Contains("middle") || n.Contains("ring") || n.Contains("pinky")
            || n.Contains("little"))
        {
            int s = 600 + FingerSort(n);
            return ($"Fingers ({Side(isL, isR)})", s);
        }

        // Face — eyes, jaw, eyebrows, tongue. Doesn't subdivide L/R because
        // faces have lots of tiny bones and grouping them all under Face
        // keeps the sidebar readable.
        if (n.Contains("eye") || n.Contains("jaw") || n.Contains("brow")
            || n.Contains("tongue") || n.Contains("lip") || n.Contains("cheek")
            || n.Contains("mouth") || n.Contains("face") || n.Contains("nose"))
            return ("Face", 200);

        // Head / neck.
        if (n.Contains("head") || n.Contains("neck") || n.Contains("skull"))
            return ("Head", 100 + (n.Contains("head") ? 1 : 0));

        // Spine / torso.
        if (n.Contains("spine") || n.Contains("chest") || n.Contains("torso")
            || n.Contains("pelvis") || n.Contains("hip") || n.Contains("waist")
            || n == "root" || n.EndsWith("_root") || n.Contains("skel_root"))
            return ("Torso", TorsoSort(n));

        // Arms: clavicle / shoulder / upper-arm / forearm / hand / wrist.
        if (n.Contains("clavicle") || n.Contains("shoulder") || n.Contains("upperarm")
            || n.Contains("upper_arm") || n.Contains("uparm") || n.Contains("forearm")
            || n.Contains("lower_arm") || n.Contains("lowerarm") || n.Contains("hand")
            || n.Contains("wrist") || (n.Contains("arm") && !n.Contains("armor")))
        {
            int s = 500 + ArmSort(n);
            return ($"Arms ({Side(isL, isR)})", s);
        }

        // Legs: thigh / upleg / leg / calf / shin / knee / foot / ankle / toe.
        if (n.Contains("thigh") || n.Contains("upleg") || n.Contains("calf")
            || n.Contains("shin") || n.Contains("knee") || n.Contains("foot")
            || n.Contains("ankle") || n.Contains("toe") || n.Contains("heel")
            || (n.Contains("leg") && !n.Contains("legging")))
        {
            int s = 700 + LegSort(n);
            return ($"Legs ({Side(isL, isR)})", s);
        }

        // Tail / breast / accessory bones — common on rigs but don't fit
        // a body region cleanly. Bucket them so they don't pollute Other.
        if (n.Contains("tail") || n.Contains("breast") || n.Contains("cloth")
            || n.Contains("hair") || n.Contains("skirt"))
            return ("Accessory", 900);

        return ("Other", 999);
    }

    private static string Side(bool l, bool r) => l ? "L" : r ? "R" : "C";

    private static bool HasMarker(string n, string m) => n.Contains(m);

    private static int TorsoSort(string n)
    {
        if (n.Contains("root")) return 0;
        if (n.Contains("pelvis") || n.Contains("hip")) return 10;
        if (n.Contains("spine0") || n.Contains("spine_1") || n.EndsWith("spine")) return 20;
        if (n.Contains("spine1") || n.Contains("spine_2")) return 21;
        if (n.Contains("spine2") || n.Contains("spine_3")) return 22;
        if (n.Contains("spine3") || n.Contains("chest")) return 23;
        return 30;
    }

    private static int ArmSort(string n)
    {
        if (n.Contains("clavicle") || n.Contains("shoulder")) return 0;
        if (n.Contains("upperarm") || n.Contains("upper_arm") || n.Contains("uparm")) return 10;
        if (n.Contains("forearm") || n.Contains("lower_arm") || n.Contains("lowerarm")) return 20;
        if (n.Contains("hand") || n.Contains("wrist")) return 30;
        return 40;
    }

    private static int LegSort(string n)
    {
        if (n.Contains("thigh") || n.Contains("upleg")) return 0;
        if (n.Contains("calf") || n.Contains("shin") || n.Contains("knee")) return 10;
        if (n.Contains("foot") || n.Contains("ankle")) return 20;
        if (n.Contains("toe")) return 30;
        return 40;
    }

    private static int FingerSort(string n)
    {
        int finger = n.Contains("thumb") ? 0
                   : n.Contains("index") ? 1
                   : n.Contains("middle") ? 2
                   : n.Contains("ring") ? 3
                   : n.Contains("pinky") || n.Contains("little") ? 4
                   : 5;
        // Pull trailing digits (GTA's SKEL_L_Finger00..43 packs finger+joint
        // here; many other rigs use a "_01" suffix for joint position).
        // Walk forward through the run of trailing digits to read them in
        // their original order.
        int digitStart = n.Length;
        while (digitStart > 0 && char.IsDigit(n[digitStart - 1])) digitStart--;
        int trailing = 0;
        for (int i = digitStart; i < n.Length; i++) trailing = (trailing * 10) + (n[i] - '0');
        return (finger * 100) + Math.Min(trailing, 99);
    }
}

/// <summary>
/// View-model for the Pose -> Emote workspace. Owns the sidebar state
/// (status text, bone list, "model loaded" flag) that flanks the
/// PoseToEmoteView's WebView2 viewport. Pose data itself lives in the
/// viewer (three.js scene graph) — this VM tracks just what the host
/// needs to drive its UI.
/// </summary>
public partial class PoseToEmoteViewModel : ObservableObject
{
    public PoseToEmoteViewModel()
    {
        // ClipLibrary derived props (HasClipLibrary, ClipLibraryCountLabel)
        // need to repaint whenever entries are added/removed/cleared.
        // The collection is mutated from OnViewerMessage, so the handler
        // lives here (in the VM) where OnPropertyChanged is accessible.
        ClipLibrary.CollectionChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(HasClipLibrary));
            OnPropertyChanged(nameof(ClipLibraryCountLabel));
            OnPropertyChanged(nameof(HasAnythingToClear));
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModel))]
    [NotifyPropertyChangedFor(nameof(BoneCountLabel))]
    private bool _hasRig;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModel))]
    private string _loadedModelPath = "";

    public bool HasModel => HasRig && !string.IsNullOrEmpty(LoadedModelPath);

    [ObservableProperty]
    private string _statusText = "Open a rigged .glb / .fbx to start posing.";

    // ── Unified right sidebar: one panel, three vertical tabs
    //    (Emotes | FiveOS Motion | Outliner). SidebarTab drives which content
    //    the single docked panel shows. Emotes and Motion share the library
    //    panel body (IsMotionPanelSelected picks library vs motion rows).
    public enum EmoteSidebarTab { Emotes, Motion, Outliner }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarShowsOutliner))]
    [NotifyPropertyChangedFor(nameof(SidebarShowsPanel))]
    [NotifyPropertyChangedFor(nameof(TabIsEmotes))]
    [NotifyPropertyChangedFor(nameof(TabIsMotion))]
    private EmoteSidebarTab _sidebarTab = EmoteSidebarTab.Outliner;

    public bool SidebarShowsOutliner => SidebarTab == EmoteSidebarTab.Outliner;
    public bool SidebarShowsPanel => SidebarTab != EmoteSidebarTab.Outliner;
    public bool TabIsEmotes => SidebarTab == EmoteSidebarTab.Emotes;
    public bool TabIsMotion => SidebarTab == EmoteSidebarTab.Motion;

    // ── Animation Library right sidebar (toggleable, default open) ───
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnimLibraryPanelWidth))]
    private bool _isAnimLibraryOpen = true;

    /// <summary>User-dragged width while the panel is open (clamped in the view).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnimLibraryPanelWidth))]
    private double _animLibraryOpenWidth = 360;

    /// <summary>Width of the library panel body (0 when collapsed to the edge tab).</summary>
    public double AnimLibraryPanelWidth => IsAnimLibraryOpen ? AnimLibraryOpenWidth : 0d;

    /// <summary>Right-panel mode: Library browse vs FiveOS Motion cloud.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnimLibraryMode))]
    [NotifyPropertyChangedFor(nameof(IsMotionMode))]
    [NotifyPropertyChangedFor(nameof(EmotePanelHeader))]
    private bool _isMotionPanelSelected;

    public bool IsAnimLibraryMode => !IsMotionPanelSelected;
    public bool IsMotionMode => IsMotionPanelSelected;

    /// <summary>Header title for the shared panel — reflects the active tab.</summary>
    public string EmotePanelHeader => IsMotionMode ? "Motion" : "Animation Library";

#if FIVEOS_MOTION
    public bool MotionFeatureEnabled => true;
#else
    public bool MotionFeatureEnabled => false;
#endif

    /// <summary>How many dictionaries to push into the ListBox per chunk.
    /// The full index stays in memory for search; only this many bind at once.</summary>
    public const int AnimLibraryPageSize = 5000;

    /// <summary>All indexed dicts from <see cref="AnimRpfIndex"/> (unfiltered).</summary>
    public ObservableCollection<AnimDictEntry> AnimLibraryAll { get; } = new();

    /// <summary>Visible page of search-filtered dicts bound to the ListBox.</summary>
    public ObservableCollection<AnimDictEntry> AnimLibraryFiltered { get; } = new();

    /// <summary>Full match set for the current search (not bound — used for paging).</summary>
    private readonly System.Collections.Generic.List<AnimDictEntry> _animLibraryMatches = new();

    public ObservableCollection<string> AnimLibraryClips { get; } = new();

    public string AnimLibraryClipCountLabel => AnimLibraryClips.Count.ToString();

    public void NotifyAnimLibraryClipsChanged()
        => OnPropertyChanged(nameof(AnimLibraryClipCountLabel));

    /// <summary>When true, clip lists and (after a scan) dictionaries only
    /// include entries that resolve to a playable Animation.</summary>
    [ObservableProperty]
    private bool _animLibraryWorkingOnly = true;

    /// <summary>True while a background "scan working" pass is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnimLibraryCanScan))]
    private bool _animLibraryScanning;

    /// <summary>Session cache of dictionary names that have ≥1 playable clip.
    /// Null = not scanned yet (show all dicts; still filter clips on load).</summary>
    public System.Collections.Generic.HashSet<string>? AnimLibraryWorkingDicts { get; set; }

    public bool AnimLibraryCanScan => AnimLibraryIsReady && !AnimLibraryScanning;

    public string AnimLibraryScanLabel => AnimLibraryScanning
        ? "Scanning…"
        : (AnimLibraryWorkingDicts is null ? "Scan working…" : "Re-scan working…");

    public void NotifyAnimLibraryScanChanged()
    {
        OnPropertyChanged(nameof(AnimLibraryCanScan));
        OnPropertyChanged(nameof(AnimLibraryScanLabel));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnimLibraryCountLabel))]
    [NotifyPropertyChangedFor(nameof(AnimLibraryNeedsGta))]
    [NotifyPropertyChangedFor(nameof(AnimLibraryIsReady))]
    private int _animLibraryTotalCount;

    /// <summary>Shown count: "5,000 / 20,343" while paged, else full total.</summary>
    public string AnimLibraryCountLabel
    {
        get
        {
            if (AnimLibraryTotalCount <= 0) return "—";
            int matches = _animLibraryMatches.Count;
            int shown = AnimLibraryFiltered.Count;
            if (matches <= 0) return $"{AnimLibraryTotalCount:N0}";
            if (shown >= matches) return $"{matches:N0}";
            return $"{shown:N0} / {matches:N0}";
        }
    }

    public bool HasAnimLibraryMore => AnimLibraryFiltered.Count < _animLibraryMatches.Count;

    [ObservableProperty]
    private string _animLibrarySearch = "";

    /// <summary>Category combo labels ("All (20,343)", "Movement (1,204)", …).</summary>
    public ObservableCollection<string> AnimLibraryCategories { get; } = new();

    /// <summary>Selected combo label; parsed back to a bare category name on filter.</summary>
    [ObservableProperty]
    private string _animLibraryCategory = AnimDictCategories.All;

    /// <summary>Bare category key currently filtering (All / Movement / …).</summary>
    private string _animLibraryCategoryKey = AnimDictCategories.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnimLibrarySelection))]
    [NotifyPropertyChangedFor(nameof(CanRecordPreviewGif))]
    private AnimDictEntry? _selectedAnimDict;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnimLibraryCodeString))]
    [NotifyPropertyChangedFor(nameof(HasAnimLibrarySelection))]
    [NotifyPropertyChangedFor(nameof(CanRecordPreviewGif))]
    private string? _selectedAnimClip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnimLibraryNeedsGta))]
    [NotifyPropertyChangedFor(nameof(AnimLibraryIsReady))]
    private bool _animLibraryLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnimLibraryNeedsGta))]
    [NotifyPropertyChangedFor(nameof(AnimLibraryIsReady))]
    private string _animLibraryStatus = "";

    [ObservableProperty]
    private string _animLibraryGtaFolderLabel = "GTA V folder not set";

    [ObservableProperty]
    private string _animLibraryDurationLabel = "Max duration: —";

    [ObservableProperty]
    private string _animLibraryTracksLabel = "Total tracks: —";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnimLibrarySelection))]
    [NotifyPropertyChangedFor(nameof(CanRecordPreviewGif))]
    private bool _animLibraryClipReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRecordPreviewGif))]
    private bool _isRecordingPreviewGif;

    public bool HasAnimLibrarySelection =>
        AnimLibraryClipReady
        && !string.IsNullOrEmpty(SelectedAnimClip)
        && SelectedAnimDict != null;

    /// <summary>Download GIF is available when a library clip is ready and not already recording.</summary>
    public bool CanRecordPreviewGif => HasAnimLibrarySelection && !IsRecordingPreviewGif;

    public string AnimLibraryCodeString
    {
        get
        {
            if (SelectedAnimDict is null || string.IsNullOrEmpty(SelectedAnimClip))
                return "";
            return $"[\"{SelectedAnimDict.Name}\", \"{SelectedAnimClip}\"]";
        }
    }

    public bool AnimLibraryNeedsGta =>
        !AnimLibraryLoading && AnimLibraryTotalCount == 0;

    public bool AnimLibraryIsReady => AnimLibraryTotalCount > 0 && !AnimLibraryLoading;

    partial void OnAnimLibraryWorkingOnlyChanged(bool value)
    {
        RebuildAnimLibraryCategories();
        ApplyAnimLibraryFilter();
    }

    partial void OnAnimLibraryLoadingChanged(bool value)
        => NotifyAnimLibraryScanChanged();

    partial void OnAnimLibraryTotalCountChanged(int value)
        => NotifyAnimLibraryScanChanged();

    /// <summary>Rebuild the category combo labels from the current index.</summary>
    public void RebuildAnimLibraryCategories()
    {
        IEnumerable<AnimDictEntry> source = AnimLibraryAll;
        if (AnimLibraryWorkingOnly && AnimLibraryWorkingDicts is { } working)
            source = AnimLibraryAll.Where(e => working.Contains(e.Name));
        var counts = AnimDictCategories.CountByCategory(source);
        var prevKey = _animLibraryCategoryKey;
        AnimLibraryCategories.Clear();
        foreach (var cat in AnimDictCategories.Ordered)
        {
            if (!counts.TryGetValue(cat, out int n)) n = 0;
            // Hide empty buckets except All (keeps the combo short).
            if (n == 0 && !string.Equals(cat, AnimDictCategories.All, StringComparison.Ordinal))
                continue;
            AnimLibraryCategories.Add($"{cat} ({n:N0})");
        }
        // Restore selection if that category still exists.
        string? restore = null;
        foreach (var label in AnimLibraryCategories)
        {
            if (label.StartsWith(prevKey + " (", StringComparison.Ordinal)
                || string.Equals(label, prevKey, StringComparison.Ordinal))
            {
                restore = label;
                break;
            }
        }
        AnimLibraryCategory = restore ?? (AnimLibraryCategories.Count > 0
            ? AnimLibraryCategories[0]
            : AnimDictCategories.All);
        _animLibraryCategoryKey = ParseAnimLibraryCategoryKey(AnimLibraryCategory);
    }

    partial void OnAnimLibraryCategoryChanged(string value)
    {
        var key = ParseAnimLibraryCategoryKey(value);
        if (string.Equals(key, _animLibraryCategoryKey, StringComparison.Ordinal))
            return;
        _animLibraryCategoryKey = key;
        ApplyAnimLibraryFilter();
    }

    private static string ParseAnimLibraryCategoryKey(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return AnimDictCategories.All;
        var s = label.Trim();
        int paren = s.LastIndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) s = s[..paren];
        foreach (var cat in AnimDictCategories.Ordered)
        {
            if (string.Equals(cat, s, StringComparison.OrdinalIgnoreCase))
                return cat;
        }
        return AnimDictCategories.All;
    }

    public void ApplyAnimLibraryFilter()
    {
        var q = (AnimLibrarySearch ?? "").Trim();
        var cat = _animLibraryCategoryKey;
        bool allCats = string.IsNullOrEmpty(cat)
            || string.Equals(cat, AnimDictCategories.All, StringComparison.OrdinalIgnoreCase);
        _animLibraryMatches.Clear();
        AnimLibraryFiltered.Clear();

        if (AnimLibraryAll.Count == 0)
        {
            OnPropertyChanged(nameof(AnimLibraryCountLabel));
            OnPropertyChanged(nameof(HasAnimLibraryMore));
            return;
        }

        var working = AnimLibraryWorkingOnly ? AnimLibraryWorkingDicts : null;

        foreach (var e in AnimLibraryAll)
        {
            if (!allCats && !string.Equals(e.Category, cat, StringComparison.OrdinalIgnoreCase))
                continue;
            if (working is not null && !working.Contains(e.Name))
                continue;
            if (q.Length > 0
                && !e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !e.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !e.Path.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !e.Category.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            _animLibraryMatches.Add(e);
        }

        LoadMoreAnimLibrary();
    }

    /// <summary>Append the next page of matches into the visible ListBox.</summary>
    public void LoadMoreAnimLibrary()
    {
        int already = AnimLibraryFiltered.Count;
        int end = System.Math.Min(already + AnimLibraryPageSize, _animLibraryMatches.Count);
        for (int i = already; i < end; i++)
            AnimLibraryFiltered.Add(_animLibraryMatches[i]);
        OnPropertyChanged(nameof(AnimLibraryCountLabel));
        OnPropertyChanged(nameof(HasAnimLibraryMore));
    }

    // Splash-style overlay gate. Stays true from VM construction until the
    // viewer reports pose-mode-entered (i.e. WebView2 booted, viewer.html
    // loaded, default skeleton imported, joint markers built). While true
    // the host paints a full-bleed loading screen over the workspace so the
    // user never sees the half-built UI flashing through.
    [ObservableProperty]
    private bool _isViewerLoading = true;

    /// <summary>Caption shown under the loader on the Pose → Emote splash
    /// overlay. Code-behind moves this through the init phases so the user
    /// can see *what* is happening rather than just a spinner.</summary>
    [ObservableProperty]
    private string _viewerLoadingCaption = "Starting up...";

    // ── Sidebar section expand state (Blender-style chevron sections) ──
    /// <summary>Advanced authoring UI is always enabled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTimelineGutter))]
    private bool _showAdvanced = true;

    /// <summary>Left track list column (Cascadeur-style) — always visible.</summary>
    public bool ShowTimelineGutter => true;

    /// <summary>Keyframe diamond lane.</summary>
    public bool ShowTimelineKeyframes => ShowKeyframeLane && TimelineKeyframeTrackVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTimelineKeyframes))]
    private bool _timelineClipTrackVisible = true;

    [ObservableProperty]
    private bool _timelineClipTrackLocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTimelineKeyframes))]
    private bool _timelineKeyframeTrackVisible = true;

    [ObservableProperty]
    private bool _timelineKeyframeTrackLocked;

    partial void OnTimelineClipTrackVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTimelineKeyframes));
    }

    partial void OnTimelineKeyframeTrackVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTimelineKeyframes));
    }

    partial void OnShowAdvancedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTimelineGutter));
        OnPropertyChanged(nameof(ShowTimelineKeyframes));
    }

    // ── Outliner slide-out panel (Blender N-panel tab) ───────────────
    //
    // The outliner used to live inside the sidebar; it's now a separate
    // collapsible panel between the sidebar and the viewport. A tab
    // anchored to the sidebar's right edge stays visible at all times;
    // clicking it slides the outliner panel INTO the viewport area
    // (panel width animates between 0 and 280 px). When closed, the
    // viewport reclaims the freed width because its column is `*`.

    /// <summary>True when the outliner panel is visible. False folds
    /// the panel to zero width, leaving only the edge tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutlinerPanelWidth))]
    private bool _isOutlinerOpen = true;   // default OPEN (user directive 2026-07-16); edge tab folds it away

    /// <summary>Pixel width of the outliner panel. Bound to the panel
    /// Border's Width so flipping the bool slides the panel in / out.
    /// The parent column is Auto so this width drives the column's
    /// total span.</summary>
    public double OutlinerPanelWidth => IsOutlinerOpen ? 280d : 0d;

    // The Inspector's slide-out panel and IsInspectorOpen/InspectorPanelWidth
    // are gone: those settings now live in PoseSettingsWindow, opened from the
    // toolbar gear. The calibration / secondary-motion / onion-skin properties
    // below stayed put — the dialog binds to this same VM instance, which is
    // what keeps the preview live while it's open.

    /// <summary>Spring-damper follow-through on auto-detected
    /// secondary bones (head, hands, hair, spine tip, tail). Default
    /// on. Pushes the toggle state down to the JS evaluator via the
    /// host's view-changed hook.</summary>
    /// <summary>Emote playback mode: 0 = in place, 1 = upper body, 2 = root
    /// motion (ped travels). Default is root motion — imported / library
    /// clips should move. Static pose emotes force in-place on export.</summary>
    // Default to Root motion (ped travels): imported clips keep their baked mover
    // so the ped physically moves in-game. The Movement dropdown switches to
    // In Place / Upper body when a clip should stay put.
    [ObservableProperty] private int _movementIndex = (int)Services.EmoteMovement.RootMotion;

    public Services.EmoteMovement Movement =>
        (Services.EmoteMovement)System.Math.Clamp(MovementIndex, 0, (int)Services.EmoteMovement.RootMotion);

    /// <summary>Movement used for export: static poses stay in-place; animated
    /// clips honour the combo (default root motion).</summary>
    public Services.EmoteMovement EffectiveExportMovement =>
        IsAnimatedExport ? Movement : Services.EmoteMovement.InPlace;

    // ── Body calibration (retarget → this ped) ─────────────────────────
    // A retarget copies joint ANGLES, which is only complete if the target has
    // the source's build. It never does, so identical angles land the hands
    // somewhere else on the ped — sometimes inside its own chest or pelvis.
    // These fit the result to the body; the skeleton alone can't say where the
    // flesh is, so they are exposed rather than hardcoded.

    /// <summary>Master switch for the calibration passes.</summary>
    [ObservableProperty] private bool _calibrationEnabled = true;

    /// <summary>0 = let limbs pass through the body, 1 = fully push them out.</summary>
    [ObservableProperty] private double _calibrationClearance = 1.0;

    /// <summary>Torso thickness the clearance solves against, relative to the
    /// bones. ~1.7 matches the freemode silhouette (the hip JOINTS are ~18 cm
    /// apart; the pelvis is nearer 35 cm). Raise it for a bulkier ped.</summary>
    [ObservableProperty] private double _calibrationBodyWidth = 1.7;

    /// <summary>Constant outward swing of both arms, as a fraction of shoulder
    /// width. Opens up a pose that reads too closed on a stockier ped.</summary>
    [ObservableProperty] private double _calibrationArmSpread = 0.0;

    public Services.BodyCalibration.Settings Calibration => new(
        Enabled: CalibrationEnabled,
        Clearance: (float)CalibrationClearance,
        BodyWidth: (float)CalibrationBodyWidth,
        ArmSpread: (float)CalibrationArmSpread);

    [ObservableProperty] private bool _secondaryMotionEnabled = true;

    /// <summary>Spring tuning, 0..1. 0 = snappy (no overshoot), 0.5 =
    /// default soft overshoot, 1 = noticeably floaty. Mapped to
    /// stiffness/damping in the JS setter.</summary>
    [ObservableProperty] private double _secondaryMotionIntensity = 0.5;

    /// <summary>Onion-skinning toggle: ghost rigs at the prev/next
    /// keyframes around the scrubber. Default on — distinctive
    /// posing aid, doesn't affect playback motion.</summary>
    [ObservableProperty] private bool _onionSkinEnabled = true;

    /// <summary>Colored joint marker spheres in the viewport. Synced from
    /// <see cref="RigDisplayMode"/> (true when mode is markers/both).</summary>
    [ObservableProperty] private bool _jointMarkersEnabled = false;

    /// <summary>Viewport skeleton overlay style pushed to the viewer:
    /// <c>fiveos</c> (FK dots + hand/foot IK handles — default),
    /// <c>markers</c> (every joint + skeleton lines), or <c>none</c>.
    /// The UI exposes only <see cref="ControlRigEnabled"/>; the mode
    /// string survives for the viewer protocol and old saved values.</summary>
    [ObservableProperty] private string _rigDisplayMode = "fiveos";

    partial void OnRigDisplayModeChanged(string value)
    {
        OnPropertyChanged(nameof(ControlRigEnabled));
    }

    /// <summary>Simple on/off for the viewport control rig (the settings
    /// toggle). On = FiveOS rig (FK dots + IK handles), off = hidden.
    /// The viewer additionally hides the rig while an animation plays,
    /// regardless of this switch.</summary>
    public bool ControlRigEnabled
    {
        get => RigDisplayMode != "none";
        set
        {
            var mode = value ? "fiveos" : "none";
            if (RigDisplayMode != mode) RigDisplayMode = mode;
        }
    }

    /// <summary>Rig manipulation mode. <c>false</c> = FK (click a limb to
    /// rotate it — wrist, spine, fingers). <c>true</c> = IK (drag a hand or
    /// foot and the shoulder, chest and other side follow, via full-body
    /// FABRIK). Toggled from the pose toolbar; pushed to the viewer as
    /// <c>poseSetIkMode</c>.</summary>
    [ObservableProperty] private bool _poseIkMode = false;

    /// <summary>"FK" / "IK" text for the toolbar toggle, kept in sync with
    /// <see cref="PoseIkMode"/> by the view.</summary>
    [ObservableProperty] private string _poseModeLabel = "FK";

    // The Inspector POSE LIBRARY section now binds to the existing
    // CustomPoses collection (populated from PoseLibraryService). The
    // brief PosePresets parallel collection that lived here was a
    // duplicate that didn't share state with the sidebar list; users
    // saw two "save pose" buttons that wrote to different files.
    // Consolidated to one library via PoseLibraryService.

    /// <summary>Outliner search filter. Lower-case substring match against
    /// bone names; empty string means "show everything". Code-behind
    /// listens for changes and toggles
    /// <see cref="PoseBoneEntry.IsHidden"/> /
    /// <see cref="PoseBoneGroup.IsFilteredOut"/> accordingly.</summary>
    [ObservableProperty] private string _outlinerFilter = "";

    public ObservableCollection<PoseBoneEntry> Bones { get; } = new();

    /// <summary>Outliner tree — each entry is a body region with its own
    /// (sorted) child Bones collection. Rebuilt alongside <see cref="Bones"/>
    /// whenever a rig is loaded. The flat <see cref="Bones"/> list stays
    /// the source of truth for index lookups (the viewer talks bone
    /// indices, not group names); this collection is purely for the
    /// outliner UI.</summary>
    public ObservableCollection<PoseBoneGroup> BoneGroups { get; } = new();

    /// <summary>Hierarchical outliner: rigs at the top, each rig's
    /// BoneGroups nested below it. Replaces the flat BoneGroups
    /// presentation in the Outliner panel; the flat collection is
    /// kept around so the rest of the app (drag handlers, mirror,
    /// search) can keep treating bones as one pool.</summary>
    public ObservableCollection<PoseRig> Rigs { get; } = new();

    /// <summary>How many bones the active clip drives (Outliner subtitle).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoneCountLabel))]
    private int _drivenBoneCount;

    public string BoneCountLabel =>
        !HasRig ? "—"
        : DrivenBoneCount > 0
            ? $"{Bones.Count} bones · {DrivenBoneCount} driven"
            : $"{Bones.Count} bone{(Bones.Count == 1 ? "" : "s")}";

    [ObservableProperty]
    private PoseBoneEntry? _selectedBone;

    /// <summary>Count of bones whose rotation has been touched since pose-mode
    /// entry. Drives the "X modified" indicator in the sidebar.</summary>
    public int ModifiedBoneCount
    {
        get
        {
            int n = 0;
            foreach (var b in Bones) if (b.IsModified) n++;
            return n;
        }
    }

    public string ModifiedLabel => ModifiedBoneCount == 0
        ? "No edits yet"
        : $"{ModifiedBoneCount} bone{(ModifiedBoneCount == 1 ? "" : "s")} modified";

    public void NotifyBonesChanged()
    {
        OnPropertyChanged(nameof(BoneCountLabel));
        OnPropertyChanged(nameof(ModifiedBoneCount));
        OnPropertyChanged(nameof(ModifiedLabel));
    }

    // ── Timeline / keyframes ────────────────────────────────────────
    //
    // State here mirrors the JS side's poseKeyframes / poseTime /
    // poseDuration / poseFps. The host C# is the source-of-truth for the
    // UI bindings (NumberBoxes, scrubber slider) but the JS owns the
    // ground-truth keyframe data and playback. Round-tripping happens via
    // pose-timeline-update messages from the viewer.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineTimeLabel))]
    [NotifyPropertyChangedFor(nameof(TimelineTimecodeLabel))]
    [NotifyPropertyChangedFor(nameof(TimelineCurrentFrame))]
    private double _timelineTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineTimeLabel))]
    [NotifyPropertyChangedFor(nameof(TimelineTimecodeLabel))]
    [NotifyPropertyChangedFor(nameof(TimelineTotalFrames))]
    private double _timelineDuration = 2.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineTimecodeLabel))]
    [NotifyPropertyChangedFor(nameof(TimelineCurrentFrame))]
    [NotifyPropertyChangedFor(nameof(TimelineTotalFrames))]
    private int _timelineFps = 30;

    [ObservableProperty]
    private bool _timelinePlaying;

    [ObservableProperty]
    private bool _timelineLoop = true;

    [ObservableProperty]
    private double _timelineZoom = 1;

    [ObservableProperty]
    private double _timelineScrollOffset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSequencerMode))]
    [NotifyPropertyChangedFor(nameof(IsDopeSheetMode))]
    private Services.TimelineEditorMode _timelineMode = Services.TimelineEditorMode.Sequencer;

    // Unified AE-style layers timeline (2026-07-16): BOTH sections are always
    // live — layer bars on top, per-bone key lanes beneath. TimelineMode is
    // kept only so legacy setters (double-click strip, etc.) stay harmless.
    public bool IsSequencerMode => true;
    public bool IsDopeSheetMode => true;

    [ObservableProperty] private bool _timelineSnapEnabled = true;
    [ObservableProperty] private string _timelineTrackFilter = "";

    // Playback-range boxes in the transport (AE-style Start/End + Trim,
    // 2026-07-17). Frame numbers; End re-syncs to the clip length whenever
    // the duration changes (see the pose-timeline-update handler).
    [ObservableProperty] private int _trimStartFrame;
    [ObservableProperty] private int _trimEndFrame;

    /// <summary>When false (default), Dope Sheet hides bone tracks with no keys.</summary>
    [ObservableProperty] private bool _showEmptyTracks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimelineSelection))]
    [NotifyPropertyChangedFor(nameof(TimelineSelectionLabel))]
    private int _timelineSelectionCount;

    public bool HasTimelineSelection => TimelineSelectionCount > 0;
    public string TimelineSelectionLabel => TimelineSelectionCount == 0
        ? "Nothing selected"
        : $"{TimelineSelectionCount} item{(TimelineSelectionCount == 1 ? "" : "s")} selected";

    /// <summary>Viewer-synced track list (bones + specials). Source of truth for keys/mute/lock.</summary>
    public ObservableCollection<TimelineTrackRow> TimelineTracks { get; } = new();

    /// <summary>Dope Sheet gutter/canvas rows — specials + body-part group headers + bones.</summary>
    public ObservableCollection<TimelineTrackRow> DopeDisplayTracks { get; } = new();

    /// <summary>Persists Dope Sheet body-group expand state across snapshot rebuilds.</summary>
    public Dictionary<string, bool> DopeGroupExpanded { get; } = new(StringComparer.Ordinal);

    // Long-running import/mocap state is intentionally separate from the
    // startup-only IsViewerLoading splash so the timeline layout never vanishes.
    [ObservableProperty] private bool _isOperationRunning;
    [ObservableProperty] private string _operationCaption = "";
    [ObservableProperty] private double _operationProgress;
    [ObservableProperty] private bool _operationCanCancel;

    /// <summary>True when export should default to fxresource (root motion).</summary>
    public bool PreferWalkingExport =>
        Movement == Services.EmoteMovement.RootMotion;

    public bool SuggestFxResourceForExport =>
        Movement == Services.EmoteMovement.RootMotion;

    /// <summary>Width of the scrubber track (px). Set by code-behind on
    /// SizeChanged so keyframe markers can be positioned absolutely
    /// across the slider's pixel range.</summary>
    [ObservableProperty]
    private double _timelineTrackWidth;

    /// <summary>Keyframe markers for the canvas overlay -- one per
    /// keyframe time, with a pre-computed PixelX so XAML doesn't need a
    /// custom converter.</summary>
    public ObservableCollection<KeyframeMarker> TimelineKeyframes { get; } = new();

    /// <summary>Clip library shown in the Outliner. Populated by the
    /// viewer via 'clip-library-update' messages — entries appear when
    /// the user bakes a pose-keyframe sequence or imports a model with
    /// animation tracks. The id matches the viewer's poseClips entry
    /// id so host->JS calls (delete, rename, future strip-add) can
    /// round-trip.</summary>
    public ObservableCollection<ClipLibraryEntry> ClipLibrary { get; } = new();

    public bool HasClipLibrary => ClipLibrary.Count > 0;
    public string ClipLibraryCountLabel => ClipLibrary.Count == 1 ? "1 clip" : $"{ClipLibrary.Count} clips";

    /// <summary>Strips placed on the NLA timeline. Mirrors the viewer's
    /// poseStrips array — each entry is a placement of a ClipLibrary
    /// clip with a start time and duration. M2 is single-lane; M3 adds
    /// a Lane field for vertical stacking.</summary>
    public ObservableCollection<TimelineStrip> Strips { get; } = new();

    public string TimelineTimeLabel =>
        $"{TimelineTime,5:F2}s / {TimelineDuration,4:F1}s";

    /// <summary>SMPTE-style readout for the transport bar (Cascadeur: 0:00:00:15).</summary>
    public string TimelineTimecodeLabel
    {
        get
        {
            var fps = System.Math.Max(1, TimelineFps);
            var f = TimelineCurrentFrame;
            var h = (int)(TimelineTime / 3600);
            var m = ((int)TimelineTime % 3600) / 60;
            var s = (int)TimelineTime % 60;
            var ff = f % fps;
            return $"{h}:{m:D2}:{s:D2}:{ff:D2}";
        }
    }

    public int TimelineCurrentFrame =>
        (int)System.Math.Round(TimelineTime * System.Math.Max(1, TimelineFps));

    public int TimelineTotalFrames =>
        (int)System.Math.Max(1, System.Math.Round(TimelineDuration * System.Math.Max(1, TimelineFps)));

    /// <summary>Label for the main animation track row in the gutter.</summary>
    public string PrimaryTrackLabel =>
        Strips.Count > 0 ? Strips[0].ClipName : "Animation";

    /// <summary>Clip track row — gutter entry and its lane. An empty track
    /// has nothing to act on: its eye/lock toggles would mutate state that
    /// no clip reads, so the row stays hidden until the first clip lands.</summary>
    public bool ShowClipTrack => Strips.Count > 0;

    // ── Prop (emote-with-prop authoring) ────────────────────────────

    /// <summary>True once the user has loaded a prop via window.loadProp.
    /// Drives the visibility of the Prop section in the sidebar and the
    /// dpemotes export's PropEmotes branching.</summary>
    [ObservableProperty]
    private bool _hasProp;

    /// <summary>GTA in-game model name the dpemotes runtime will spawn.
    /// E.g. "prop_phone_ing", "p_amb_brolly_01", "prop_beer_bottle".
    /// The mesh the user imported in FiveOS is purely visual reference;
    /// the in-game prop is whatever GTA model this name resolves to.</summary>
    [ObservableProperty]
    private string _propModelName = "prop_phone_ing";

    /// <summary>RAGE runtime bone tag the prop attaches to. dpemotes
    /// conventionally uses these as plain integers in PropBone.
    /// Common: 28422 = R hand, 60309 = L hand, 24818 = Spine3.</summary>
    [ObservableProperty]
    private int _propBoneId = 28422;

    /// <summary>SKEL_* name the prop attaches to in the FiveOS viewport
    /// (NOT necessarily the same as PropBoneId -- the in-game RAGE bone
    /// tag and our local bone name diverge for hands etc.). Drives
    /// the in-viewport offset math.</summary>
    [ObservableProperty]
    private string _propAttachBoneName = "SKEL_R_Hand";

    // ── Animated-vs-static export mode chip ─────────────────────────
    //
    // Surfaces "what would Export actually produce right now" in the
    // sidebar. Recomputed whenever the keyframe list changes; the chip
    // colour shifts so the user sees at a glance whether they're about
    // to bake a single pose or a multi-frame clip.

    public string AnimatedChipText => TimelineStripsLabel();

    private string TimelineStripsLabel()
    {
        if (Strips.Count > 0 && TimelineKeyframes.Count < 2)
        {
            var s = Strips[0];
            return Strips.Count == 1
                ? $"Clip · {s.ClipName} · {s.Duration:F1}s"
                : $"Clips · {Strips.Count} on timeline · {TimelineDuration:F1}s";
        }
        if (TimelineKeyframes.Count >= 2)
            return $"Animated · {TimelineKeyframes.Count} KFs · {TimelineDuration:F1}s @ {TimelineFps}fps";
        if (TimelineKeyframes.Count == 1)
            return "Static · 1 KF won't animate (need 2+)";
        return "Static pose";
    }

    public bool IsAnimatedExport => TimelineKeyframes.Count >= 2 || Strips.Count > 0;
    public bool ShowKeyframeLane => TimelineKeyframes.Count > 0;

    /// <summary>True when Clear all has something to destroy. Gates the button
    /// so an empty workspace can't raise a confirm dialog about nothing. The
    /// pose itself isn't counted: it can't be observed from here, and Revert
    /// already covers "just reset the rig".</summary>
    public bool HasAnythingToClear =>
        Strips.Count > 0 || ClipLibrary.Count > 0 || TimelineKeyframes.Count > 0;

    /// <summary>Helper for code-behind to fire change notifications on
    /// the derived chip text/state whenever keyframes mutate.</summary>
    public void NotifyAnimatedChipChanged()
    {
        OnPropertyChanged(nameof(AnimatedChipText));
        OnPropertyChanged(nameof(IsAnimatedExport));
        OnPropertyChanged(nameof(ShowKeyframeLane));
        OnPropertyChanged(nameof(ShowTimelineKeyframes));
        OnPropertyChanged(nameof(HasAnythingToClear));
    }

    public void NotifyStripsChanged()
    {
        OnPropertyChanged(nameof(AnimatedChipText));
        OnPropertyChanged(nameof(IsAnimatedExport));
        OnPropertyChanged(nameof(ShowKeyframeLane));
        OnPropertyChanged(nameof(ShowTimelineKeyframes));
        OnPropertyChanged(nameof(PrimaryTrackLabel));
        OnPropertyChanged(nameof(ShowClipTrack));
        OnPropertyChanged(nameof(HasAnythingToClear));
    }

    // ── Undo / redo depth (from viewer's poseHistoryDepth) ──────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    [NotifyPropertyChangedFor(nameof(UndoDepthLabel))]
    private int _undoDepth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRedo))]
    [NotifyPropertyChangedFor(nameof(RedoDepthLabel))]
    private int _redoDepth;

    public bool CanUndo => UndoDepth > 0;
    public bool CanRedo => RedoDepth > 0;
    public string UndoDepthLabel => UndoDepth == 0 ? "" : $"({UndoDepth})";
    public string RedoDepthLabel => RedoDepth == 0 ? "" : $"({RedoDepth})";

    // ── Last-export skipped-bones report ───────────────────────────
    //
    // After each .ycd or dpemotes-zip export the code-behind stamps
    // these so the sidebar can show a "view report" affordance when
    // some rig bones didn't map to GTA tags.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkippedBones))]
    [NotifyPropertyChangedFor(nameof(SkippedSummary))]
    private int _lastExportSkipped;

    /// <summary>Up-to-32 unmapped bone names, joined for the report.
    /// Trimmed so the UI doesn't blow up on rigs with hundreds of
    /// helper bones the user already knows they don't care about.</summary>
    [ObservableProperty]
    private string _lastExportSkippedNames = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SkippedSummary))]
    private int _lastExportMapped;

    public bool HasSkippedBones => LastExportSkipped > 0;
    public string SkippedSummary => LastExportMapped == 0 && LastExportSkipped == 0
        ? ""
        : $"{LastExportMapped} mapped · {LastExportSkipped} skipped";

    // ── Custom pose library ────────────────────────────────────────
    //
    // ObservableCollection of saved poses; the sidebar binds to it to
    // show a clickable list. Items are tiny — display name + saved-at
    // timestamp — the full JSON loads on demand when the user clicks
    // "Apply".

    public ObservableCollection<SavedPoseEntry> CustomPoses { get; } = new();

    /// <summary>Re-emit when the collection size shifts so any
    /// "X saved poses" labels in the XAML stay in sync.</summary>
    public void NotifyCustomPosesChanged()
    {
        OnPropertyChanged(nameof(CustomPoseCount));
        OnPropertyChanged(nameof(HasCustomPoses));
    }
    public int CustomPoseCount => CustomPoses.Count;
    public bool HasCustomPoses => CustomPoses.Count > 0;

    // The debug stream (DebugEntries / DebugLogEntry / the error-count badge
    // / IsDebugPanelOpen) lived here to feed a floating debug popup. That
    // popup is gone: host events now go straight into the viewer's ring
    // buffer and render in the bottom-left ticker, so there is nothing on the
    // C# side left to hold them. The badge properties were already dead — no
    // XAML ever bound them.
}

/// <summary>One entry in the custom-pose library shown in the sidebar.
/// Mirrors PoseLibraryService.PoseEntry but as an ObservableObject so
/// XAML bindings can refresh without rebuilding the list.</summary>
public partial class SavedPoseEntry : ObservableObject
{
    [ObservableProperty] private string _slug = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private DateTime _savedAt;
    [ObservableProperty] private string _sourceRig = "";
    [ObservableProperty] private int _boneCount;
    [ObservableProperty] private string _filePath = "";

    public string Subtitle
    {
        get
        {
            var when = SavedAt.ToString("yyyy-MM-dd HH:mm");
            return string.IsNullOrEmpty(SourceRig)
                ? $"{when} · {BoneCount} bones"
                : $"{when} · {SourceRig} · {BoneCount}";
        }
    }
}

/// <summary>One keyframe rendered on the timeline strip. PixelX is the
/// scrubber-track x coordinate, recomputed when keyframes change or the
/// scrubber resizes.</summary>
public partial class KeyframeMarker : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString("N");
    [ObservableProperty] private double _time;
    [ObservableProperty] private double _pixelX;
    [ObservableProperty] private string _boneName = "Summary";
    [ObservableProperty] private string _ease = "auto";
    [ObservableProperty] private bool _isSelected;
    public string TooltipText => $"Keyframe at {Time:F2}s";
}

public partial class TimelineTrackRow : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupCountLabel))]
    private string _displayName = "";
    [ObservableProperty] private string _parentId = "";
    [ObservableProperty] private string _group = "";
    [ObservableProperty] private int _depth;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isFilteredOut;
    [ObservableProperty] private bool _isGroupHeader;
    [ObservableProperty] private bool _hasKeys;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupCountLabel))]
    private int _groupKeyCount;
    public ObservableCollection<KeyframeMarker> Keys { get; } = new();

    // Key-count suffix removed (was "Arms (R)  (964)") — the numbers read as
    // clutter and mean nothing while dense imported clips key every frame.
    public string GroupCountLabel => string.IsNullOrEmpty(DisplayName) ? Name : DisplayName;

    /// <summary>Dim channel-type suffix for the flat gutter ("· Rot" / "· Pos"),
    /// AE-style. Empty for special lanes (Summary/Clips) and group headers —
    /// matched by Name too, because the viewer snapshot rewrites row Ids.</summary>
    public string LaneTypeLabel =>
        IsGroupHeader ? ""
        : string.Equals(Id, "root-motion", StringComparison.OrdinalIgnoreCase)
          || string.Equals(Name, "Root Motion", StringComparison.OrdinalIgnoreCase) ? "· Pos"
        : string.Equals(Id, "summary", StringComparison.OrdinalIgnoreCase)
          || string.Equals(Name, "Summary", StringComparison.OrdinalIgnoreCase)
          || string.Equals(Name, "Clips", StringComparison.OrdinalIgnoreCase)
          || (Id?.Contains("pose-keys", StringComparison.OrdinalIgnoreCase) ?? false) ? ""
        : "· Rot";

    /// <summary>Human-readable bone label for the Dope gutter (keeps <see cref="Name"/> for viewer IDs).</summary>
    public static string FriendlyName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var n = raw.Trim();
        if (n.StartsWith("mixamorig:", StringComparison.OrdinalIgnoreCase))
            n = n["mixamorig:".Length..];
        else if (n.StartsWith("mixamorig", StringComparison.OrdinalIgnoreCase))
            n = n["mixamorig".Length..].TrimStart('_', ':');
        if (n.StartsWith("SKEL_", StringComparison.OrdinalIgnoreCase))
            n = n[5..];
        // Drop the glTF dedup suffix ("SKEL_R_Clavicle_1" → "R Clavicle", not
        // "R Clavicle 1") — pure noise in the gutter; Name keeps the raw id.
        n = System.Text.RegularExpressions.Regex.Replace(n, @"_\d+$", "");
        n = n.Replace('_', ' ').Trim();
        return string.IsNullOrEmpty(n) ? raw.Trim() : n;
    }

    public static bool IsSpecialTrack(TimelineTrackRow track)
    {
        if (track.IsGroupHeader) return false;
        var id = track.Id ?? "";
        var name = track.Name ?? "";
        if (id.Equals("summary", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Summary", StringComparison.OrdinalIgnoreCase))
            return true;
        if (id.Equals("root-motion", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Root Motion", StringComparison.OrdinalIgnoreCase))
            return true;
        if (id.StartsWith("clip:", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("layer:", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Equals("Clips", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>Stable sort for body-part groups in the Dope Sheet.</summary>
    public static int GroupSortKey(string group)
    {
        return group switch
        {
            "Head" => 10,
            "Face" => 20,
            "Torso" => 30,
            "Arms (L)" => 40,
            "Arms (C)" => 45,
            "Arms (R)" => 50,
            "Fingers (L)" => 60,
            "Fingers (C)" => 65,
            "Fingers (R)" => 70,
            "Legs (L)" => 80,
            "Legs (C)" => 85,
            "Legs (R)" => 90,
            "Accessory" => 100,
            "Other" => 110,
            _ => 200,
        };
    }

    // AE-style: every group starts COLLAPSED (user directive 2026-07-17 —
    // the fully expanded tree read as clutter). Twirl open what you edit;
    // the expand state sticks per group for the session.
    public static bool DefaultGroupExpanded(string group) => false;
}

/// <summary>One row in the Outliner's clip-library list. Mirrors the
/// metadata view emitted by the viewer's postClipLibrary() — the actual
/// THREE.AnimationClip object lives in JS and is referenced by Id.
/// Kind: "pose" (baked from the current keyframe sequence) or
/// "imported" (came in with a .glb/.fbx animation track).</summary>
public partial class ClipLibraryEntry : ObservableObject
{
    [ObservableProperty] private int _id;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _kind = "pose";
    [ObservableProperty] private double _duration;
    [ObservableProperty] private int _trackCount;

    public string DurationLabel => $"{Duration:F2}s";
    public string KindLabel => Kind == "imported" ? "Imported" : "Baked";
    public bool IsImported => Kind == "imported";
}

/// <summary>One top-level entry in the Blender-style outliner. Maps
/// to a rig (primary ped, secondary ped, …). Holds the same per-region
/// BoneGroup buckets the legacy flat outliner used, just scoped under
/// a parent that can be selected on its own to put the model — not a
/// bone — under the move/rot/scale gizmo.</summary>
public partial class PoseRig : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isExpanded = true;
    /// <summary>True while this rig is the model-transform target
    /// (gizmo attached to its root, translate/rotate/scale toolbar
    /// visible). Used by the row template to highlight the selected
    /// rig and to flip a chevron icon.</summary>
    [ObservableProperty] private bool _isModelSelected;

    public ObservableCollection<PoseBoneGroup> BoneGroups { get; } = new();
}

/// <summary>One placement of a clip on the NLA timeline. Mirrors the
/// viewer's poseStrips entry. The Rectangle field is the live canvas
/// element the redraw stage stamps with the rectangle width/position
/// so the drag handlers can mutate it without hunting the visual tree.</summary>
public partial class TimelineStrip : ObservableObject
{
    [ObservableProperty] private int _id;
    [ObservableProperty] private int _clipId;
    [ObservableProperty] private string _clipName = "";
    [ObservableProperty] private string _kind = "pose";
    [ObservableProperty] private double _start;
    [ObservableProperty] private double _duration;
    // Fade-in / fade-out ramps in seconds. Zero = hard cut.
    [ObservableProperty] private double _fadeIn;
    [ObservableProperty] private double _fadeOut;
    [ObservableProperty] private string _fadeInEase = "linear";
    [ObservableProperty] private string _fadeOutEase = "linear";
    // Trim window inside the source clip (seconds from clip start).
    [ObservableProperty] private double _sourceStart;
    [ObservableProperty] private double _sourceEnd;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isMuted;
    public string TrimLabel => $"{SourceStart:F2}s – {SourceEnd:F2}s";
    public string StableId => $"strip:{Id}";
}
