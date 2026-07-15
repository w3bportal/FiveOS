// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FiveOS.ViewModels;

public partial class PoseBoneEntry : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _parentName = "";
    [ObservableProperty] private int _index;
    [ObservableProperty] private bool _isModified;
    [ObservableProperty] private bool _isSelected;
    /// <summary>Hidden by the outliner's search filter. Bound to the bone
    /// row's Visibility so a non-matching bone collapses without
    /// reshuffling the underlying ordered list.</summary>
    [ObservableProperty] private bool _isHidden;
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
    //
    // Each top-stack section (Library / Character / Prop) is a tight
    // chevron header the user can collapse to reclaim vertical space —
    // same pattern as Blender's Properties editor. Defaults to expanded
    // so a fresh rig load shows everything; persists across the VM's
    // lifetime (a tab switch doesn't reset).

    [ObservableProperty] private bool _libraryExpanded = true;
    [ObservableProperty] private bool _characterExpanded = true;
    [ObservableProperty] private bool _propExpanded = true;

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
    private bool _isOutlinerOpen = false;   // default closed → clean, viewport-first layout; edge tab reopens it

    /// <summary>Pixel width of the outliner panel. Bound to the panel
    /// Border's Width so flipping the bool slides the panel in / out.
    /// The parent column is Auto so this width drives the column's
    /// total span.</summary>
    public double OutlinerPanelWidth => IsOutlinerOpen ? 280d : 0d;

    /// <summary>Right-side Inspector panel — collapsible like the
    /// outliner, mirrors the same width-bound pattern. Holds knobs
    /// for derived/auto features (secondary motion, future ML
    /// in-between, ballistic ghost) that don't belong in the per-pose
    /// sidebar on the left.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InspectorPanelWidth))]
    private bool _isInspectorOpen = false;   // default closed → clean, viewport-first layout; edge tab reopens it

    public double InspectorPanelWidth => IsInspectorOpen ? 260d : 0d;

    /// <summary>Spring-damper follow-through on auto-detected
    /// secondary bones (head, hands, hair, spine tip, tail). Default
    /// on. Pushes the toggle state down to the JS evaluator via the
    /// host's view-changed hook.</summary>
    /// <summary>Emote playback mode: 0 = in place (full body), 1 = upper
    /// body (can move), 2 = walkable, 3 = root motion (ped travels — Video →
    /// Emote). Bound to the export mode combo; maps to
    /// <see cref="Services.EmoteMovement"/> for the resource/dpemotes builders.</summary>
    [ObservableProperty] private int _movementIndex;

    public Services.EmoteMovement Movement => (Services.EmoteMovement)System.Math.Clamp(MovementIndex, 0, 3);

    [ObservableProperty] private bool _secondaryMotionEnabled = true;

    /// <summary>Spring tuning, 0..1. 0 = snappy (no overshoot), 0.5 =
    /// default soft overshoot, 1 = noticeably floaty. Mapped to
    /// stiffness/damping in the JS setter.</summary>
    [ObservableProperty] private double _secondaryMotionIntensity = 0.5;

    /// <summary>Onion-skinning toggle: ghost rigs at the prev/next
    /// keyframes around the scrubber. Default on — distinctive
    /// posing aid, doesn't affect playback motion.</summary>
    [ObservableProperty] private bool _onionSkinEnabled = true;

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

    public string BoneCountLabel =>
        HasRig ? $"{Bones.Count} bone{(Bones.Count == 1 ? "" : "s")}" : "—";

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

    /// <summary>Helper for code-behind to fire change notifications on
    /// the derived chip text/state whenever keyframes mutate.</summary>
    public void NotifyAnimatedChipChanged()
    {
        OnPropertyChanged(nameof(AnimatedChipText));
        OnPropertyChanged(nameof(IsAnimatedExport));
        OnPropertyChanged(nameof(ShowKeyframeLane));
        OnPropertyChanged(nameof(ShowTimelineKeyframes));
    }

    public void NotifyStripsChanged()
    {
        OnPropertyChanged(nameof(AnimatedChipText));
        OnPropertyChanged(nameof(IsAnimatedExport));
        OnPropertyChanged(nameof(ShowKeyframeLane));
        OnPropertyChanged(nameof(ShowTimelineKeyframes));
        OnPropertyChanged(nameof(PrimaryTrackLabel));
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

    // ── Multi-instance (Ped A / Ped B) ──────────────────────────────
    //
    // The viewer holds a second skeleton on +X offset that mirrors the
    // primary editing surface. Switching active focus exits pose mode
    // on the inactive ped, then re-enters on the new primary.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryPedLabel))]
    private bool _hasSecondaryPed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryPedLabel))]
    private string _activePedSlot = "A";

    public string SecondaryPedLabel => HasSecondaryPed
        ? $"Active: Ped {ActivePedSlot}"
        : "Single character";

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

    // ── Debug telemetry stream ─────────────────────────────────────
    //
    // Capped collection (200 newest) — anything older falls off as new
    // events arrive. The viewer's debug overlay holds its own 500-entry
    // buffer; this VM collection is for the C# side only (sidebar's
    // collapsed debug pane).

    public const int DebugCapacity = 200;
    public ObservableCollection<DebugLogEntry> DebugEntries { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DebugBadgeText))]
    [NotifyPropertyChangedFor(nameof(HasRecentErrors))]
    private int _recentErrorCount;

    public bool HasRecentErrors => RecentErrorCount > 0;
    public string DebugBadgeText => RecentErrorCount == 0 ? "Debug" : $"Debug · {RecentErrorCount} err";

    [ObservableProperty]
    private bool _isDebugPanelOpen;
}

/// <summary>One row in the sidebar's debug log. Categories mirror the
/// viewer-side telemetry tags (pose / timeline / export / hotkey /
/// system / error) so filter logic is symmetric across host + viewer.</summary>
public partial class DebugLogEntry : ObservableObject
{
    [ObservableProperty] private DateTime _time = DateTime.Now;
    [ObservableProperty] private string _level = "info";       // info | warn | err
    [ObservableProperty] private string _category = "system";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _payload = "";
    public string TimeLabel => Time.ToString("HH:mm:ss.fff");
    public string DisplayText => string.IsNullOrEmpty(Payload) ? Text : Text + " · " + Payload;
    /// <summary>Single-string render used by the floating debug window's
    /// ListBox so the row template stays a one-binding TextBlock.</summary>
    public string FormattedLine =>
        $"{TimeLabel}  [{Level.ToUpperInvariant(),-4}] {Category,-9} {DisplayText}";
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
    [ObservableProperty] private double _time;
    [ObservableProperty] private double _pixelX;
    public string TooltipText => $"Keyframe at {Time:F2}s";
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
    public string TrimLabel => $"{SourceStart:F2}s – {SourceEnd:F2}s";
}
