// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FiveOS.ViewModels;

public enum GizmoMode { Move, Rotate, Scale }

/// <summary>Output target the user is converting toward. Prop is the
/// default (placeable .ydr). Weapon overlays a gun_root skeleton on the
/// same drawable and scaffolds weapons.meta / weaponarchetypes.meta /
/// weaponanimations.meta so the asset is usable via <c>give weapon</c>.
/// The 3D-load pipeline, layers panel, gizmo, and viewport are shared
/// across both modes — only the sidebar's bottom section and the
/// footer button label diverge.</summary>
public enum ExportMode { Prop, Weapon }

/// <summary>
/// Top-level view shown by MainWindow. Replaces the previous tab strip:
/// the app boots into <see cref="Dashboard"/> (4 home-screen tiles) and
/// drills into one of the feature panes when a tile is clicked. The
/// header back button (visible when not on Dashboard) returns to it.
/// </summary>
public enum AppView { Dashboard, Props, Optimize, Rpf, Vehicles, ImageTo3D, Emotes }

/// <summary>Per-part visual material preset. Drives both the RAGE shader
/// written into the YDR (glass / emissive / emissivestrong / emissivenight)
/// and, for glass, the collision material index assigned to that part's
/// polygons so the engine plays shatter VFX on hit.</summary>
public enum MaterialPreset { Standard, Glass, Emissive, EmissiveStrong, EmissiveNight }

public partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        OptimizeVm = new OptimizeViewModel(s => StatusText = s);
        TxAdminVm = new TxAdminOptimizeViewModel(s => StatusText = s);
        RpfVm = new RpfConverterViewModel(s => StatusText = s);
        VehiclesVm = new VehiclesViewModel(s => StatusText = s);
        ImageTo3DVm = new ImageTo3DViewModel();

        // Hydrate reference-ped state from persisted settings on launch.
        _showReferencePed = Services.UserSettings.LoadShowReferencePed();
        _referenceModelPath = Services.UserSettings.ResolveReferenceModelPath();

        // UI complexity tier (Beginner/Standard/Advanced). Beginner is the
        // default for new users; controls across the app bind their
        // Always run in Advanced mode — beginner/standard tiers removed.
        _experienceLevel = 2;

        // Discover third-party plugins from %AppData%\FiveOS\plugins\.
        // Discovery is cheap (manifest reads only); each plugin's view is
        // built lazily on first activation by the rail click handler.
        RefreshPlugins();

        // Undo/redo: capture the initial state and start listening for
        // changes to the tracked properties. Has to happen at the end of
        // construction so all field initialisers have already run.
        _currentSnapshot = TakeSnapshot();
        PropertyChanged += OnTrackedPropertyChanged;
    }

    // ── Reference ped (scale-comparison) ──────────────────────────────

    /// <summary>Whether the reference (scale-comparison) model is shown
    /// alongside the user's prop in the 3D preview.</summary>
    [ObservableProperty]
    private bool _showReferencePed;

    /// <summary>Resolved path to the reference model file. Null when no
    /// reference is configured and the default ped_scale.fbx isn't on disk.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReferenceFileName))]
    private string? _referenceModelPath;

    public string ReferenceFileName =>
        string.IsNullOrEmpty(ReferenceModelPath) ? "(none)" : Path.GetFileName(ReferenceModelPath);

    /// <summary>Persist the toggle. The viewer-side push happens in
    /// MainWindow.xaml.cs via <c>SetReferenceVisibleAsync</c>.</summary>
    partial void OnShowReferencePedChanged(bool value)
    {
        Services.UserSettings.SaveShowReferencePed(value);
    }

    /// <summary>Bound to Ctrl+R / View → Show reference ped. Just flips
    /// the property; the partial setter persists, the view watches the
    /// property to update the viewer.</summary>
    [RelayCommand]
    private void ToggleReferencePed() => ShowReferencePed = !ShowReferencePed;

    // ── Layers panel (model parts) ────────────────────────────────────

    /// <summary>One named sub-group of the loaded model, surfaced to the
    /// layers panel. <see cref="IsVisible"/> is two-way bound: flipping it
    /// pushes <c>setPartVisible(name, visible)</c> into the viewer, and the
    /// hidden ones are also passed to the engine as <c>--exclude-mesh</c>
    /// at convert time.</summary>
    public partial class ModelPart : ObservableObject
    {
        /// <summary>Display name. Settable so the layer-panel "Rename"
        /// context-menu action can mutate it; the original name a load
        /// posted from the viewer stays in <see cref="OriginalName"/> so
        /// we can address the part in JS calls (which still know it by
        /// its source name) and in convert-time exclude lists.</summary>
        [ObservableProperty]
        private string _name;

        /// <summary>Name as the viewer first reported it. Used for any
        /// JS bridge call (setPartVisible, setPartTexture) and for the
        /// engine's <c>--exclude-mesh</c> list at convert time. Stays
        /// stable across rename operations.</summary>
        public string OriginalName { get; }

        [ObservableProperty] private bool _isVisible = true;

        /// <summary>RAGE shader override for this part. Standard leaves the
        /// engine's default (alpha-aware) shader pick. Glass / Emissive*
        /// flip the shader name + render bucket + key parameters at
        /// export time. Fired through <see cref="MaterialPresetChanged"/>
        /// so the owner can mirror the choice into the live viewer.</summary>
        [ObservableProperty] private MaterialPreset _materialPreset = MaterialPreset.Standard;

        partial void OnMaterialPresetChanged(MaterialPreset value)
            => MaterialPresetChanged?.Invoke(this, value);

        /// <summary>Fires when a row's preset changes so the host can push
        /// the new look into viewer.html for live preview without going
        /// through a full re-import.</summary>
        public event System.EventHandler<MaterialPreset>? MaterialPresetChanged;

        // ── Inline mesh-optimize state (per-row expanding slider) ───
        [ObservableProperty] private bool _isMeshSliderOpen;
        [ObservableProperty] private bool _isMeshOptimizing;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MeshOptimizeStatsDisplay))]
        private int _meshTargetTris;
        [ObservableProperty] private int _meshSliderMin;
        [ObservableProperty] private int _meshSliderMax;
        [ObservableProperty] private int _meshSliderTick = 50;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MeshOptimizeStatsDisplay))]
        private int _meshOriginalTris;

        public string MeshOptimizeStatsDisplay
        {
            get
            {
                if (MeshOriginalTris <= 0) return "";
                var t = System.Math.Max(1, MeshTargetTris);
                var pct = (int)System.Math.Round(100.0 * (1 - (double)t / MeshOriginalTris));
                return $"{MeshOriginalTris:N0} → {t:N0} tris ({pct}% smaller)";
            }
        }

        public ModelPart(string name, bool visible)
        {
            _name = name;
            OriginalName = name;
            _isVisible = visible;
        }
    }

    public ObservableCollection<ModelPart> ModelParts { get; } = new();

    /// <summary>Names of parts the user has deleted from the layers panel.
    /// Deleted parts are removed from <see cref="ModelParts"/> (so they
    /// disappear from the panel) but stay tracked here so the convert
    /// path still passes them as <c>--exclude-mesh</c> alongside hidden
    /// ones. Cleared on every model load.</summary>
    public System.Collections.Generic.HashSet<string> DeletedPartNames { get; } =
        new(System.StringComparer.Ordinal);

    [ObservableProperty]
    private bool _isLayersPanelOpen = true;

    /// <summary>Collapsed state of the Layers panel. False = expanded full
    /// width with the parts list visible. True = collapsed to a thin
    /// vertical strip with just the panel icon — click the strip to
    /// expand again. Independent of <see cref="IsLayersPanelOpen"/>:
    /// Ctrl+L / View menu / rail button still hide-or-show the panel
    /// outright; the panel's own X button collapses instead so the user
    /// doesn't lose the dock spot.</summary>
    [ObservableProperty]
    private bool _isLayersPanelCollapsed;

    [RelayCommand]
    private void ToggleLayersPanel() => IsLayersPanelOpen = !IsLayersPanelOpen;

    [RelayCommand]
    private void ToggleLayersPanelCollapsed() => IsLayersPanelCollapsed = !IsLayersPanelCollapsed;

    // ── Sub-tab view-models ─────────────────────────────────────────

    /// <summary>Owns the unified Optimize tab — props (.ydr), clothing
    /// (.ydd), textures (.ytd) and vehicles (.yft) all in one place.
    /// Replaces the older Texture/Mapping/Mesh trio.</summary>
    public OptimizeViewModel OptimizeVm { get; }

    /// <summary>Owns the txAdmin Optimizer — paste a server console log
    /// and auto-shrink every oversized asset just enough to clear the
    /// streaming warning (or with manual controls). Hosted as a mode card
    /// inside the Optimize tab (OptimizeView binds this VM directly).</summary>
    public TxAdminOptimizeViewModel TxAdminVm { get; }

    /// <summary>Owns the "RPF" tab — pack a loose FiveM resource folder
    /// into a single OPEN .rpf archive (Phase 1, raw packer).</summary>
    public RpfConverterViewModel RpfVm { get; }
    public VehiclesViewModel VehiclesVm { get; }

    /// <summary>Owns the Image → 3D tab. Cloud generation via various
    /// AI providers; output is saved to disk for the user to convert
    /// through the 3D-to-Props pipeline.</summary>
    public ImageTo3DViewModel ImageTo3DVm { get; }

    // ── Source model ────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConvert))]
    [NotifyPropertyChangedFor(nameof(GeometryLoaded))]
    [NotifyPropertyChangedFor(nameof(IsViewportVisible))]
    private bool _hasModel;

    /// <summary>
    /// Drives <c>ViewportRoot.Visibility</c>. We can't keep WebView2 hosted
    /// while the success overlay is up — WebView2's HwndHost always renders
    /// on top of WPF (airspace), so an overlay positioned z-above is still
    /// covered by the live viewer frame. Same trick during model loading
    /// and while the Settings overlay is open: the WPF surface can't paint
    /// over the WebView2 frame, so we collapse the host instead.
    /// </summary>
    public bool IsViewportVisible => HasModel && !ShowSuccessScreen && !IsModelLoading && !IsSettingsOpen;

    /// <summary>
    /// True from the moment a file is picked until viewer.html reports
    /// "loaded" (or an error). Drives skeleton placeholders in the
    /// sidebar's GEOMETRY card and the viewport overlay.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GeometryLoaded))]
    [NotifyPropertyChangedFor(nameof(IsViewportVisible))]
    private bool _isModelLoading;

    /// <summary>True when geometry stats are real (model finished parsing).</summary>
    public bool GeometryLoaded => HasModel && !IsModelLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceFileName))]
    [NotifyPropertyChangedFor(nameof(SourceFileFormat))]
    private string? _sourcePath;

    public string SourceFileName =>
        string.IsNullOrEmpty(SourcePath) ? "no model loaded" : Path.GetFileName(SourcePath);

    public string SourceFileFormat =>
        string.IsNullOrEmpty(SourcePath) ? "" : Path.GetExtension(SourcePath).TrimStart('.').ToUpperInvariant();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VertsDisplay))]
    [NotifyPropertyChangedFor(nameof(GeometryAvailable))]
    private int _verts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrisDisplay))]
    [NotifyPropertyChangedFor(nameof(GeometryAvailable))]
    private int _tris;

    public string VertsDisplay => Verts > 0 ? Verts.ToString("N0") : "—";
    public string TrisDisplay => Tris > 0 ? Tris.ToString("N0") : "—";
    public bool GeometryAvailable => Verts > 0 || Tris > 0;

    // ── Optimization health banner ──────────────────────────────────
    //
    // After every model load the view-side computes severity from
    // MeshThresholds and pushes the result here. The banner is purely
    // informational — it does not block conversion.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOptimizationHintVisible))]
    [NotifyPropertyChangedFor(nameof(OptimizationHintIsFail))]
    private Services.MeshThresholds.Severity _optimizationSeverity = Services.MeshThresholds.Severity.Ok;

    [ObservableProperty]
    private string _optimizationHintTitle = "";
    [ObservableProperty]
    private string _optimizationHintBody = "";

    /// <summary>User dismissed the banner for the currently-loaded model.
    /// Reset to false on every fresh load.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOptimizationHintVisible))]
    private bool _optimizationHintDismissed;

    /// <summary>True while the auto-optimize action is running so the banner
    /// can show a spinner instead of the buttons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOptimizationHintActionable))]
    [NotifyPropertyChangedFor(nameof(IsOptimizationHintPreviewActionable))]
    private bool _isOptimizing;

    /// <summary>True after the user clicks Auto-optimize but before they
    /// commit (Confirm) or revert (Cancel). The banner swaps from the
    /// 3-button bar to a slider + Confirm/Cancel pair while this is on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOptimizationHintActionable))]
    [NotifyPropertyChangedFor(nameof(IsOptimizationHintPreviewActionable))]
    [NotifyPropertyChangedFor(nameof(IsOptimizationHintPreviewing))]
    private bool _isOptimizePreviewing;

    /// <summary>True while a live preview decimation is in flight (slider was
    /// moved, background re-decimate is running, viewer will swap when done).
    /// Drives an inline spinner inside the preview banner — distinct from
    /// IsOptimizing which represents the Confirm-time final commit.</summary>
    [ObservableProperty]
    private bool _isOptimizePreviewDecimating;

    /// <summary>Slider value: target triangle count. Updates push a debounced
    /// re-decimate job from the view.</summary>
    [ObservableProperty]
    private int _optimizeTargetTris;
    [ObservableProperty]
    private int _optimizeSliderMin;
    [ObservableProperty]
    private int _optimizeSliderMax;
    /// <summary>Tick frequency for the slider — match the slider's actual
    /// scale so dragging feels natural across orders of magnitude.</summary>
    [ObservableProperty]
    private int _optimizeSliderTick = 100;

    /// <summary>Label left of the preview slider — "Target tris" for the
    /// decimate preview, "Max texture" for the texture-optimize preview.</summary>
    [ObservableProperty]
    private string _optimizeSliderLabel = "Target tris";

    /// <summary>Texture mode snaps the slider to its power-of-two ticks
    /// (512/1024/2048/4096); decimate mode drags freely.</summary>
    [ObservableProperty]
    private bool _isOptimizeSliderSnapping;

    /// <summary>True while the banner preview is texture-optimize: swaps
    /// the tri slider for the resolution option chips.</summary>
    [ObservableProperty]
    private bool _isTexturePreviewMode;

    /// <summary>The 512/1024/2048/4096 px choices shown during a
    /// texture-optimize preview. Each fills in its projected file size as
    /// the background pump finishes that resolution.</summary>
    public System.Collections.ObjectModel.ObservableCollection<TextureDimOption> TextureDimOptions { get; } = new();

    /// <summary>Snapshot of the input mesh's tri count when preview mode
    /// began — drives the "X → Y (Z% smaller)" hint without losing the
    /// reference number when the viewer reloads with the preview.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptimizePreviewStats))]
    private int _optimizeOriginalTris;

    /// <summary>When non-empty, replaces the tri-count preview stats — the
    /// texture-optimize path sets this to a "16.7 MB → 4.3 MB" line since
    /// its preview has no slider/tri dimension.</summary>
    [ObservableProperty]
    private string _optimizePreviewCustomStats = "";

    public string OptimizePreviewStats
    {
        get
        {
            if (!string.IsNullOrEmpty(OptimizePreviewCustomStats)) return OptimizePreviewCustomStats;
            if (OptimizeOriginalTris <= 0) return "";
            var target = System.Math.Max(1, OptimizeTargetTris);
            var pct = (int)System.Math.Round(100.0 * (1 - (double)target / OptimizeOriginalTris));
            return $"{OptimizeOriginalTris:N0} → {target:N0} tris ({pct}% smaller)";
        }
    }

    partial void OnOptimizeTargetTrisChanged(int value)
    {
        OnPropertyChanged(nameof(OptimizePreviewStats));
    }

    partial void OnOptimizePreviewCustomStatsChanged(string value)
    {
        OnPropertyChanged(nameof(OptimizePreviewStats));
    }

    /// <summary>False when the banner tripped on file size alone (the mesh
    /// is already within tri budget) — decimation can't fix texture weight,
    /// so the one-click Optimize CTA is hidden for that case.</summary>
    [ObservableProperty]
    private bool _optimizationHintCanDecimate = true;

    public bool IsOptimizationHintVisible =>
        OptimizationSeverity != Services.MeshThresholds.Severity.Ok && !OptimizationHintDismissed;

    /// <summary>The original 3-button bar (Auto-optimize / Open Optimize tab /
    /// Dismiss) is shown only when we're not optimizing AND not in preview.</summary>
    public bool IsOptimizationHintActionable => !IsOptimizing && !IsOptimizePreviewing;

    /// <summary>The slider + Confirm/Cancel cluster is shown when previewing
    /// and not currently re-decimating.</summary>
    public bool IsOptimizationHintPreviewActionable => IsOptimizePreviewing && !IsOptimizing;

    /// <summary>Whole preview block visible while previewing (regardless
    /// of whether a re-decimate is currently in flight — the slider stays
    /// visible so users can drag again as soon as the spinner clears).</summary>
    public bool IsOptimizationHintPreviewing => IsOptimizePreviewing;

    public bool OptimizationHintIsFail =>
        OptimizationSeverity == Services.MeshThresholds.Severity.Fail;

    // ── Output / conversion options ─────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConvert))]
    private string _propName = string.Empty;

    /// <summary>0 = Auto-detect, 1 = Y is up, 2 = Z is up. Bound to the
    /// sidebar's "Up axis" ComboBox SelectedIndex.</summary>
    [ObservableProperty]
    private int _upAxisIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCollisionEmbeddable))]
    private bool _includeCollision = true;

    /// <summary>
    /// CodeWalker collision material name. Common: CONCRETE, METAL, WOOD,
    /// PLASTIC, GLASS, RUBBER, GRASS, DIRT, SAND, ROCK, FOLIAGE.
    /// </summary>
    [ObservableProperty]
    private string _collisionMaterial = "CONCRETE";

    /// <summary>When true the BoundComposite is attached to the YDR's
    /// <c>Drawable.Bound</c> and no external .ybn is written. Falls back to
    /// the legacy sibling-.ybn behaviour when off. Only meaningful while
    /// <see cref="IncludeCollision"/> is also true.</summary>
    // Default on: vanilla GTA .ymap-placed static props load their bound
    // via Drawable.Bound — the .ydr ships both visual + collision in one
    // file. External-.ybn-via-stream/ does work, but only for some load
    // mechanisms (IPL toggling, archetype-explicit physicsDictionary
    // lookup). The embed path is the most reliable for the drag-export-
    // drop-in-stream/ workflow this tool targets. Toggle off only if you
    // need external collision (e.g. swap in a Sollumz-built .ybn).
    [ObservableProperty]
    private bool _embedCollision = true;

    /// <summary>Drives the embed toggle's enabled state — embedding is
    /// meaningless if collision is disabled outright.</summary>
    public bool IsCollisionEmbeddable => IncludeCollision;

    [ObservableProperty]
    private bool _includeYtyp = true;

    [ObservableProperty]
    private bool _extractTextures = true;

    /// <summary>When true the exporter deep-clones the High DrawableModels
    /// into Med/Low/VLow slots and decimates each clone via g3sharp. RAGE
    /// then streams the lower-detail tiers at distance instead of always
    /// rendering High, which is the right call for any prop that's ever
    /// viewed from far away. Defaults to off because it adds size to the
    /// YDR (~+30-50%) and the decimation can produce visible silhouette
    /// pops on cutout-textured props (chain-link, foliage).</summary>
    [ObservableProperty]
    private bool _generateLods = false;

    /// <summary>Per-tier LOD draw distances in metres. High renders 0 →
    /// DistHigh, Med renders DistHigh → DistMed, Low DistMed → DistLow,
    /// VLow DistLow → DistVlow. Past DistVlow the prop disappears entirely
    /// — that value also drives the .ytyp's archetype-level cull radius.
    /// Defaults follow the Dekurwinator LOD guide.</summary>
    [ObservableProperty] private double _lodDistHigh = 60d;
    [ObservableProperty] private double _lodDistMed  = 120d;
    [ObservableProperty] private double _lodDistLow  = 250d;
    [ObservableProperty] private double _lodDistVlow = 500d;

    // ── Weapon mode ─────────────────────────────────────────────────
    //
    // The Props view doubles as the weapon-export workflow because the
    // 3D-loading machinery is identical: same drop target, same layers
    // panel, same gizmo, same viewport. ExportMode flips the sidebar's
    // bottom section to a weapon archetype/slot editor and re-labels
    // the CONVERT button — the OnConvert handler reads ExportMode to
    // decide whether to add --mode=weapon to the engine args.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWeaponMode))]
    [NotifyPropertyChangedFor(nameof(IsPropMode))]
    [NotifyPropertyChangedFor(nameof(ActiveViewTitle))]
    [NotifyPropertyChangedFor(nameof(ConvertButtonLabel))]
    private ExportMode _exportMode = ExportMode.Prop;

    public bool IsWeaponMode => ExportMode == ExportMode.Weapon;
    public bool IsPropMode   => ExportMode == ExportMode.Prop;

    public string ConvertButtonLabel => IsPackMode ? "Add to Pack" : "Convert";

    // ── Pack mode ────────────────────────────────────────────────────
    //
    // When IsPackMode is on, the Convert button accumulates each
    // conversion into PropPackSession.Current instead of zipping /
    // deploying it. The pack panel surfaces the running entry list and
    // exposes a Finalize action that compiles everything into one
    // FiveM resource. Pack mode is prop-only — weapons have their own
    // bundling story via weapons.meta and aren't built up incrementally.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConvertButtonLabel))]
    [NotifyPropertyChangedFor(nameof(IsPackPanelVisible))]
    private bool _isPackMode;

    /// <summary>Direct binding target for the pack panel. Backed by the
    /// process-wide singleton — quitting the app drops the pack.</summary>
    public Services.PropPackSession PackSession => Services.PropPackSession.Current;

    /// <summary>Footer pack panel collapses when pack mode is off so the
    /// regular convert flow has the full footer width.</summary>
    public bool IsPackPanelVisible => IsPackMode;

    partial void OnIsPackModeChanged(bool value)
    {
        // Default pack name to the current prop name (or a sensible
        // fallback) the first time the user flips into pack mode, so the
        // panel isn't blank.
        if (value && string.IsNullOrWhiteSpace(PackSession.PackName))
            PackSession.PackName = string.IsNullOrWhiteSpace(PropName) ? "props_pack" : PropName + "_pack";
    }

    [RelayCommand]
    private void TogglePackMode() => IsPackMode = !IsPackMode;

    [RelayCommand]
    private void ClearPack() => PackSession.Clear();

    [RelayCommand]
    private void RemovePackEntry(Services.PropPackEntry entry)
    {
        if (entry is null) return;
        PackSession.Remove(entry);
    }

    // ─── Update badge (shows below the Convert button when the
    // background update check finds a newer build) ──────────────────
    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _updateBadgeLabel = string.Empty;

    /// <summary>0=Pistol, 1=Rifle, 2=SMG, 3=Shotgun, 4=Sniper. Matches
    /// <c>WeaponMetaWriter.Archetype</c> ordering on the engine side; the
    /// EngineRunner translates index→enum name when building the args.</summary>
    [ObservableProperty]
    private int _weaponArchetypeIndex;

    [ObservableProperty]
    private string _weaponName = string.Empty;

    [ObservableProperty]
    private string _weaponSlot = string.Empty;

    // Bone offsets in metres, drawable-local. Defaults match what the
    // engine uses internally — leave them at zero/default for a single-
    // bone "everything on gun_root" weapon. The defaults are deliberately
    // sensible for a small pistol-sized model; user tweaks via the
    // sidebar's number inputs when needed.
    [ObservableProperty] private double _muzzleX = 0;
    [ObservableProperty] private double _muzzleY = 0.3;
    [ObservableProperty] private double _muzzleZ = 0;
    [ObservableProperty] private double _gripX = 0;
    [ObservableProperty] private double _gripY = 0;
    [ObservableProperty] private double _gripZ = 0;
    [ObservableProperty] private double _magX = 0;
    [ObservableProperty] private double _magY = 0.05;
    [ObservableProperty] private double _magZ = -0.08;
    [ObservableProperty] private double _ejectX = 0.03;
    [ObservableProperty] private double _ejectY = 0.05;
    [ObservableProperty] private double _ejectZ = 0.05;

    // ── Gizmo state (live-mirrored from the three.js viewer) ────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMoveMode))]
    [NotifyPropertyChangedFor(nameof(IsRotMode))]
    [NotifyPropertyChangedFor(nameof(IsScaleMode))]
    private GizmoMode _activeGizmo = GizmoMode.Move;

    public bool IsMoveMode  => ActiveGizmo == GizmoMode.Move;
    public bool IsRotMode   => ActiveGizmo == GizmoMode.Rotate;
    public bool IsScaleMode => ActiveGizmo == GizmoMode.Scale;

    [ObservableProperty]
    private bool _scaleLocked = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScaleDisplay))]
    private double _uniformScale = 1.0;

    public string ScaleDisplay => $"{UniformScale:F2}×";

    // ── Sidebar Transform (live-mirrored from / pushed-back to the
    //    three.js viewer). The viewer posts {kind:"transform", pos, rot,
    //    scale} on every gizmo change and the host echoes those into
    //    these properties; conversely, when the user types into the
    //    sidebar NumberBoxes the host pushes window.applyTransform back
    //    into the viewer. MainWindow code-behind owns the bidirectional
    //    sync (with a suppress flag to break the loop). ────────────────

    [ObservableProperty] private double _transformPosX;
    [ObservableProperty] private double _transformPosY;
    [ObservableProperty] private double _transformPosZ;
    [ObservableProperty] private double _transformRotX;
    [ObservableProperty] private double _transformRotY;
    [ObservableProperty] private double _transformRotZ;

    /// <summary>Per-axis scale factors. Default 1.0 = source size on
    /// every axis. Non-uniform values stretch the prop along that axis
    /// — three.js gizmo emits per-axis when the "Uniform scale" lock is
    /// off, so we have to preserve all three through to the exporter.
    /// <see cref="TransformScale"/> is kept as a uniform-scale convenience
    /// (binding two-way to the sidebar's "uniform" field) and proxies
    /// X for compatibility with older callers.</summary>
    [ObservableProperty] private double _transformScaleX = 1.0;
    [ObservableProperty] private double _transformScaleY = 1.0;
    [ObservableProperty] private double _transformScaleZ = 1.0;

    /// <summary>Legacy uniform-scale accessor — reads X, writes all
    /// three. Kept so existing two-way bindings on the bottom coord
    /// strip and other callers continue to work without churn.</summary>
    public double TransformScale
    {
        get => TransformScaleX;
        set
        {
            if (Math.Abs(TransformScaleX - value) < 1e-9 &&
                Math.Abs(TransformScaleY - value) < 1e-9 &&
                Math.Abs(TransformScaleZ - value) < 1e-9) return;
            TransformScaleX = value;
            TransformScaleY = value;
            TransformScaleZ = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Reset all sidebar transform fields to identity. The
    /// MainWindow PropertyChanged subscriber will push the new values
    /// into the viewer, so the model snaps back as a side-effect.</summary>
    [RelayCommand]
    private void ResetTransform()
    {
        TransformPosX = 0; TransformPosY = 0; TransformPosZ = 0;
        TransformRotX = 0; TransformRotY = 0; TransformRotZ = 0;
        TransformScaleX = 1.0; TransformScaleY = 1.0; TransformScaleZ = 1.0;
    }

    // ── Conversion lifecycle ────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConvert))]
    private bool _isConverting;

    [ObservableProperty]
    private string _statusText = "Ready — drop a 3D file or use File → Open";

    /// <summary>Currently-shown top-level view. Defaults to <see cref="AppView.Dashboard"/>;
    /// the four IsXView projections below are what the XAML binds against
    /// for visibility, since DataTriggers comparing against an enum value
    /// in markup are awkward. <see cref="ActiveViewTitle"/> drives the
    /// header label.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashboard))]
    [NotifyPropertyChangedFor(nameof(IsPropsView))]
    [NotifyPropertyChangedFor(nameof(IsOptimizeView))]
    [NotifyPropertyChangedFor(nameof(IsRpfView))]
    [NotifyPropertyChangedFor(nameof(IsVehiclesView))]
    [NotifyPropertyChangedFor(nameof(IsImageTo3DView))]
    [NotifyPropertyChangedFor(nameof(IsEmotesView))]
    [NotifyPropertyChangedFor(nameof(Is3DView))]
    [NotifyPropertyChangedFor(nameof(ActiveViewTitle))]
    private AppView _activeView = AppView.Props;   // boot into the 3D Model tool; the Welcome splash floats over it

    // ── Experience level (UI complexity tier) ──────────────────────────
    // 0=Beginner, 1=Standard, 2=Advanced. Controls across the app bind
    // visibility to the two projections below; Beginner hides all but the
    // essentials, Standard shows the normal set, Advanced shows everything.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelBeginner))]
    [NotifyPropertyChangedFor(nameof(LevelStandardPlus))]
    [NotifyPropertyChangedFor(nameof(LevelAdvanced))]
    private int _experienceLevel;

    public bool LevelBeginner     => ExperienceLevel <= 0;
    public bool LevelStandardPlus => ExperienceLevel >= 1;
    public bool LevelAdvanced     => ExperienceLevel >= 2;

    partial void OnExperienceLevelChanged(int value)
    {
        Services.UserSettings.SaveExperienceLevel(value);
        // If the current tab is now hidden by the new (lower) level, fall
        // back to the 3D Model tool so the user isn't stranded on a view with
        // no rail entry.
        if (!LevelStandardPlus && ActiveView is AppView.Rpf or AppView.Vehicles or AppView.Emotes)
            ActiveView = AppView.Props;
        // Same for the txAdmin mode card inside the Optimize tab — it's
        // Standard+ only, so bounce the workspace back to Props.
        if (!LevelStandardPlus && OptimizeVm.Mode == OptimizeMode.TxAdmin)
            OptimizeVm.Mode = OptimizeMode.Props;
    }

    // All built-in IsXView flags suppress when a plugin is the active
    // pane content — otherwise the previously-active built-in Grid would
    // still render under the plugin host because Visibility binds to
    // these flags directly.
    public bool IsDashboard      => !IsPluginActive && ActiveView == AppView.Dashboard;
    public bool IsPropsView      => !IsPluginActive && ActiveView == AppView.Props;
    public bool IsOptimizeView   => !IsPluginActive && ActiveView == AppView.Optimize;
    public bool IsRpfView        => !IsPluginActive && ActiveView == AppView.Rpf;
    public bool IsVehiclesView   => !IsPluginActive && ActiveView == AppView.Vehicles;
    public bool IsImageTo3DView  => !IsPluginActive && ActiveView == AppView.ImageTo3D;
    public bool IsEmotesView => !IsPluginActive && ActiveView == AppView.Emotes;
    /// <summary>True when the 3D Model rail entry is active. Optimize now
    /// has its own rail slot so it no longer counts toward this flag.</summary>
    public bool Is3DView         => !IsPluginActive && ActiveView == AppView.Props;

    /// <summary>True if any addon is enabled — drives the visibility of the
    /// rail's "ADDONS" divider + caption so they only appear once at least
    /// one addon row is going to show beneath them. Counts any enabled
    /// discovered plugin.</summary>
    public bool HasAnyAddons => EnabledPluginRailEntries.Count > 0;

    // ── Plugins (third-party addons from %AppData%\FiveOS\plugins\) ───

    /// <summary>Every plugin discovered on disk, regardless of enable state.
    /// Drives the Settings → Addons list (each row gets its own enable
    /// toggle).</summary>
    public ObservableCollection<PluginRailEntry> AllPlugins { get; } = new();

    /// <summary>Subset of <see cref="AllPlugins"/> the user has switched
    /// on. Drives the rail's per-plugin entries; toggling a plugin in
    /// Settings appends/removes a row here in lockstep.</summary>
    public ObservableCollection<PluginRailEntry> EnabledPluginRailEntries { get; } = new();

    /// <summary>Re-scan the plugins directory on disk and rebuild
    /// <see cref="AllPlugins"/>. Async so the file I/O + JSON parsing
    /// doesn't block the UI thread when the folder grows. Anything
    /// currently enabled survives the rebuild as long as a plugin with
    /// the same id is still discovered.</summary>
    public async Task RefreshPluginsAsync()
    {
        var records = await FiveOS.Plugins.PluginManager.DiscoverAsync();
        AllPlugins.Clear();
        EnabledPluginRailEntries.Clear();
        foreach (var rec in records)
        {
            var entry = new PluginRailEntry(rec)
            {
                IsEnabled = Services.UserSettings.LoadPluginEnabled(rec.Id),
            };
            entry.PropertyChanged += OnPluginEntryChanged;
            AllPlugins.Add(entry);
            if (entry.IsEnabled && !rec.IsIncompatible)
                EnabledPluginRailEntries.Add(entry);
        }
        OnPropertyChanged(nameof(HasAnyAddons));
    }

    /// <summary>Synchronous discovery for app startup — keeps the
    /// constructor non-async. Replaced by <see cref="RefreshPluginsAsync"/>
    /// at runtime when the user clicks Refresh.</summary>
    public void RefreshPlugins()
    {
        AllPlugins.Clear();
        EnabledPluginRailEntries.Clear();
        foreach (var rec in FiveOS.Plugins.PluginManager.Discover())
        {
            var entry = new PluginRailEntry(rec)
            {
                IsEnabled = Services.UserSettings.LoadPluginEnabled(rec.Id),
            };
            entry.PropertyChanged += OnPluginEntryChanged;
            AllPlugins.Add(entry);
            if (entry.IsEnabled && !rec.IsIncompatible)
                EnabledPluginRailEntries.Add(entry);
        }
        OnPropertyChanged(nameof(HasAnyAddons));
    }

    private void OnPluginEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PluginRailEntry entry) return;
        if (e.PropertyName != nameof(PluginRailEntry.IsEnabled)) return;
        Services.UserSettings.SavePluginEnabled(entry.Record.Id, entry.IsEnabled);

        if (entry.IsEnabled)
        {
            if (!EnabledPluginRailEntries.Contains(entry)) EnabledPluginRailEntries.Add(entry);
        }
        else
        {
            EnabledPluginRailEntries.Remove(entry);
            if (string.Equals(ActivePluginId, entry.Record.Id, System.StringComparison.OrdinalIgnoreCase))
                ActiveView = AppView.Props;  // bounce out of a now-hidden plugin
        }
        OnPropertyChanged(nameof(HasAnyAddons));
    }

    /// <summary>Id of the currently-active plugin, or null when a built-in
    /// view (Props/Optimize/ImageTo3D) is showing. The view-swap
    /// container reads this to decide whether to show the plugin host.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPluginActive))]
    private string? _activePluginId;

    public bool IsPluginActive => !string.IsNullOrEmpty(ActivePluginId);

    partial void OnActivePluginIdChanged(string? value)
    {
        // Re-poke the IsXView projections — they all depend on
        // IsPluginActive, but the source-generated NotifyPropertyChangedFor
        // attributes on ActiveView don't fire when ActivePluginId changes.
        OnPropertyChanged(nameof(IsDashboard));
        OnPropertyChanged(nameof(IsPropsView));
        OnPropertyChanged(nameof(IsOptimizeView));
        OnPropertyChanged(nameof(IsRpfView));
        OnPropertyChanged(nameof(IsVehiclesView));
        OnPropertyChanged(nameof(IsImageTo3DView));
        OnPropertyChanged(nameof(IsEmotesView));
        OnPropertyChanged(nameof(Is3DView));
        OnPropertyChanged(nameof(ActivePluginView));
    }

    /// <summary>The view to host when a plugin is active. Looked up lazily
    /// off <see cref="ActivePluginId"/> so plugins that aren't activated
    /// never instantiate.</summary>
    public System.Windows.Controls.UserControl? ActivePluginView
    {
        get
        {
            if (string.IsNullOrEmpty(ActivePluginId)) return null;
            foreach (var p in AllPlugins)
                if (string.Equals(p.Record.Id, ActivePluginId, System.StringComparison.OrdinalIgnoreCase))
                    return p.GetOrCreateView();
            return null;
        }
    }

    public string ActiveViewTitle => ActiveView switch
    {
        AppView.Props      => IsWeaponMode ? "Weapons" : "3D Model",
        AppView.Optimize   => "Optimize",
        AppView.Rpf        => "RPF",
        AppView.Vehicles   => "Vehicles",
        AppView.ImageTo3D  => "Image → 3D",
        AppView.Emotes     => "Emotes",
        _                  => "FiveOS",
    };

    /// <summary>Bound to dashboard tiles AND the rail click handler. Accepts
    /// <see cref="AppView"/> names, the synthetic "3D" tag (restores last
    /// 3D sub-mode), and "plugin:&lt;id&gt;" to activate a discovered plugin.
    /// Built-in view targets clear <see cref="ActivePluginId"/> so the
    /// plugin host collapses out of the way.</summary>
    [RelayCommand]
    private void OpenView(string view)
    {
        if (string.IsNullOrEmpty(view)) return;

        if (view.StartsWith("plugin:", System.StringComparison.OrdinalIgnoreCase))
        {
            ActivePluginId = view.Substring("plugin:".Length);
            return;
        }

        // Built-in target — drop any active plugin first.
        ActivePluginId = null;

        if (string.Equals(view, "3D", System.StringComparison.OrdinalIgnoreCase))
        {
            ActiveView = AppView.Props;
            return;
        }

        // "Weapon" is a sub-mode of Props rather than its own AppView —
        // both flows share the 3D-loading machinery; only the sidebar
        // section and footer label diverge. Clicking the Weapon
        // rail/toggle entry lands on the Props view with ExportMode
        // flipped. MainWindow listens for ExportMode changes and clears
        // any loaded model so weapons start from an empty drop zone —
        // the prop and weapon flows take different source meshes, so
        // carrying the active 3D model across the toggle would be
        // misleading.
        if (string.Equals(view, "Weapon", System.StringComparison.OrdinalIgnoreCase))
        {
            ExportMode = ExportMode.Weapon;
            ActiveView = AppView.Props;
            return;
        }
        if (string.Equals(view, "Props", System.StringComparison.OrdinalIgnoreCase))
        {
            ExportMode = ExportMode.Prop;
            ActiveView = AppView.Props;
            return;
        }

        // txAdmin is a mode card inside the Optimize tab rather than its own
        // AppView — deep-links (dashboard tile) land on Optimize with the
        // txAdmin card preselected.
        if (string.Equals(view, "TxAdmin", System.StringComparison.OrdinalIgnoreCase))
        {
            OptimizeVm.Mode = OptimizeMode.TxAdmin;
            ActiveView = AppView.Optimize;
            return;
        }

        // The former Animation → Emote / Pose → Emote tabs are one combined
        // Emotes workspace now — keep the old names as deep-link aliases.
        if (string.Equals(view, "AnimToEmote", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(view, "PoseToEmote", System.StringComparison.OrdinalIgnoreCase))
        {
            ActiveView = AppView.Emotes;
            return;
        }

        if (System.Enum.TryParse<AppView>(view, ignoreCase: true, out var parsed))
            ActiveView = parsed;
    }

    [RelayCommand]
    private void GoHome() => ActiveView = AppView.Dashboard;

    // ── Activity rail (collapsible left nav) ───────────────────────
    //
    // The rail has two width states: 48 (icon-only) and 180 (icon +
    // label). Pinning is the user's persistent preference; hover is
    // transient — both feed IsRailExpanded which the XAML binds for
    // width animation and label visibility. Pin defaults off so the
    // first-launch state is the smaller of the two; the burger button
    // at the top of the rail flips Pin and writes it to UserSettings.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRailExpanded))]
    private bool _isRailPinned = Services.UserSettings.LoadRailPinned();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRailExpanded))]
    private bool _isRailHovered;

    public bool IsRailExpanded => IsRailPinned || IsRailHovered;

    partial void OnIsRailPinnedChanged(bool value)
        => Services.UserSettings.SaveRailPinned(value);

    [RelayCommand]
    private void ToggleRailPin() => IsRailPinned = !IsRailPinned;

    /// <summary>Toggles the Settings overlay (replaces the old Settings tab).
    /// Driven by the gear icon in the corner of the menu bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewportVisible))]
    private bool _isSettingsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewportVisible))]
    private bool _showSuccessScreen;

    [ObservableProperty]
    private string? _resultZipPath;

    public bool CanConvert =>
        HasModel && !string.IsNullOrWhiteSpace(PropName) && !IsConverting;

    // ── Undo / Redo ─────────────────────────────────────────────────
    //
    // Snapshot-based history over the form-like editor properties.
    // Continuous values (gizmo position/rotation/scale) and one-shot
    // events (loading a different model) deliberately stay out of the
    // history — slider drags would flood the stack with intermediate
    // states, and Ctrl+Z to "un-load" a file is more surprising than
    // useful. What's tracked is what the user typically toggles in the
    // sidebar: prop name, up-axis, collision options, asset toggles,
    // gizmo mode and scale lock.

    /// <summary>One frame of editor state. Records exactly the properties
    /// listed in <see cref="TrackedProperties"/>. Transform values are in
    /// here too so Ctrl+Z reverses a gizmo drag — but those changes are
    /// debounced before they hit the stack so a single drag doesn't push
    /// 60 snapshots/sec.</summary>
    private sealed record EditSnapshot(
        string PropName,
        int UpAxisIndex,
        bool IncludeCollision,
        bool EmbedCollision,
        string CollisionMaterial,
        bool IncludeYtyp,
        bool ExtractTextures,
        bool GenerateLods,
        double LodDistHigh,
        double LodDistMed,
        double LodDistLow,
        double LodDistVlow,
        GizmoMode ActiveGizmo,
        bool ScaleLocked,
        double TransformPosX, double TransformPosY, double TransformPosZ,
        double TransformRotX, double TransformRotY, double TransformRotZ,
        double TransformScaleX, double TransformScaleY, double TransformScaleZ);

    private static readonly HashSet<string> TrackedProperties = new(StringComparer.Ordinal)
    {
        nameof(PropName), nameof(UpAxisIndex), nameof(IncludeCollision),
        nameof(EmbedCollision), nameof(CollisionMaterial), nameof(IncludeYtyp),
        nameof(ExtractTextures), nameof(GenerateLods),
        nameof(LodDistHigh), nameof(LodDistMed), nameof(LodDistLow), nameof(LodDistVlow),
        nameof(ActiveGizmo), nameof(ScaleLocked),
        nameof(TransformPosX), nameof(TransformPosY), nameof(TransformPosZ),
        nameof(TransformRotX), nameof(TransformRotY), nameof(TransformRotZ),
        nameof(TransformScaleX), nameof(TransformScaleY), nameof(TransformScaleZ),
    };

    /// <summary>Subset of <see cref="TrackedProperties"/> that gets the
    /// debounce treatment — gizmo drags fire 30-60 changes/sec and we
    /// only want the final value on the undo stack.</summary>
    private static readonly HashSet<string> DebouncedTransformProperties = new(StringComparer.Ordinal)
    {
        nameof(TransformPosX), nameof(TransformPosY), nameof(TransformPosZ),
        nameof(TransformRotX), nameof(TransformRotY), nameof(TransformRotZ),
        nameof(TransformScaleX), nameof(TransformScaleY), nameof(TransformScaleZ),
    };

    private readonly Stack<EditSnapshot> _undoStack = new();
    private readonly Stack<EditSnapshot> _redoStack = new();
    private EditSnapshot? _currentSnapshot;
    private bool _suppressSnapshot;
    private System.Windows.Threading.DispatcherTimer? _transformDebounceTimer;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    private EditSnapshot TakeSnapshot() => new(
        PropName, UpAxisIndex, IncludeCollision, EmbedCollision,
        CollisionMaterial, IncludeYtyp, ExtractTextures, GenerateLods,
        LodDistHigh, LodDistMed, LodDistLow, LodDistVlow,
        ActiveGizmo, ScaleLocked,
        TransformPosX, TransformPosY, TransformPosZ,
        TransformRotX, TransformRotY, TransformRotZ,
        TransformScaleX, TransformScaleY, TransformScaleZ);

    private void OnTrackedPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSnapshot) return;
        if (e.PropertyName == null || !TrackedProperties.Contains(e.PropertyName)) return;

        // Transform values change continuously while a gizmo drag is in
        // flight — coalesce those into one snapshot 350 ms after the
        // user lets go. Discrete edits (form fields) commit immediately.
        if (DebouncedTransformProperties.Contains(e.PropertyName))
        {
            ScheduleTransformSnapshot();
            return;
        }

        CommitSnapshot();
    }

    /// <summary>Start (or restart) the 350 ms debounce window. Each
    /// transform property change resets the timer, so the snapshot only
    /// lands when the user stops dragging.</summary>
    private void ScheduleTransformSnapshot()
    {
        if (_transformDebounceTimer == null)
        {
            _transformDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350),
            };
            _transformDebounceTimer.Tick += (_, _) =>
            {
                _transformDebounceTimer!.Stop();
                CommitSnapshot();
            };
        }
        _transformDebounceTimer.Stop();
        _transformDebounceTimer.Start();
    }

    private void CommitSnapshot()
    {
        var newSnap = TakeSnapshot();
        if (newSnap == _currentSnapshot) return;  // record-equality guards no-op writes

        if (_currentSnapshot != null) _undoStack.Push(_currentSnapshot);
        _currentSnapshot = newSnap;
        _redoStack.Clear();
        NotifyHistoryChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        // Flush any in-flight debounced drag so the most recent state
        // lands on the stack before we pop. Otherwise a Ctrl+Z mid-drag
        // would undo something older than what the user can see.
        FlushPendingSnapshot();
        if (_undoStack.Count == 0) return;
        if (_currentSnapshot != null) _redoStack.Push(_currentSnapshot);
        _currentSnapshot = _undoStack.Pop();
        ApplySnapshot(_currentSnapshot);
        NotifyHistoryChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        FlushPendingSnapshot();
        if (_redoStack.Count == 0) return;
        if (_currentSnapshot != null) _undoStack.Push(_currentSnapshot);
        _currentSnapshot = _redoStack.Pop();
        ApplySnapshot(_currentSnapshot);
        NotifyHistoryChanged();
    }

    /// <summary>If a debounced transform snapshot is pending (gizmo drag
    /// just ended), commit it now. Called by Undo/Redo so the stack
    /// state matches what the user sees on screen.</summary>
    private void FlushPendingSnapshot()
    {
        if (_transformDebounceTimer?.IsEnabled == true)
        {
            _transformDebounceTimer.Stop();
            CommitSnapshot();
        }
    }

    /// <summary>Wipe undo + redo and reset the baseline to the current
    /// state. Called by the host after loading a new model — Ctrl+Z then
    /// can't accidentally restore a previous model's transform onto the
    /// fresh one. Has to run AFTER the host writes the reset transform
    /// values, not before.</summary>
    public void ResetUndoHistory()
    {
        _transformDebounceTimer?.Stop();
        _undoStack.Clear();
        _redoStack.Clear();
        _currentSnapshot = TakeSnapshot();
        NotifyHistoryChanged();
    }

    private void ApplySnapshot(EditSnapshot s)
    {
        // Suppress the snapshot listener while we restore so a single
        // undo doesn't push 20+ individual change snapshots back onto the
        // undo stack. Also cancels any in-flight debounce timer — the
        // applied values are what should land, not whatever was pending.
        _suppressSnapshot = true;
        _transformDebounceTimer?.Stop();
        try
        {
            PropName = s.PropName;
            UpAxisIndex = s.UpAxisIndex;
            IncludeCollision = s.IncludeCollision;
            EmbedCollision = s.EmbedCollision;
            CollisionMaterial = s.CollisionMaterial;
            IncludeYtyp = s.IncludeYtyp;
            ExtractTextures = s.ExtractTextures;
            GenerateLods = s.GenerateLods;
            LodDistHigh = s.LodDistHigh;
            LodDistMed = s.LodDistMed;
            LodDistLow = s.LodDistLow;
            LodDistVlow = s.LodDistVlow;
            ActiveGizmo = s.ActiveGizmo;
            ScaleLocked = s.ScaleLocked;
            // Transform values come last — the host's PropertyChanged
            // handler pushes each one into the three.js viewer, so by the
            // time these assignments finish the viewer is back in sync
            // with the restored snapshot. _suppressSnapshot keeps the
            // resulting echo out of the undo stack; _suppressTransformPush
            // (on the host) is NOT set, so we want the viewer to update.
            TransformPosX = s.TransformPosX;
            TransformPosY = s.TransformPosY;
            TransformPosZ = s.TransformPosZ;
            TransformRotX = s.TransformRotX;
            TransformRotY = s.TransformRotY;
            TransformRotZ = s.TransformRotZ;
            TransformScaleX = s.TransformScaleX;
            TransformScaleY = s.TransformScaleY;
            TransformScaleZ = s.TransformScaleZ;
        }
        finally
        {
            _suppressSnapshot = false;
        }
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One selectable resolution in the texture-optimize preview —
/// e.g. "1024 px / 3.1 MB · lean". SizeText starts as the qualitative hint
/// and gains the real projected size once the background pass for this
/// resolution completes.</summary>
public partial class TextureDimOption : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public int Dim { get; init; }
    public string Hint { get; init; } = "";
    public string Title => $"{Dim} px";

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _sizeText = "";

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isSelected;
}
