// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FiveOS.Services;

namespace FiveOS.ViewModels;

public enum GizmoMode { Move, Rotate, Scale }

/// <summary>Output target the user is converting toward. Prop is the
/// default (placeable .ydr). Weapon overlays a gun_root skeleton on the
/// same drawable and scaffolds weapons.meta / weaponarchetypes.meta /
/// weaponanimations.meta so the asset is usable via <c>give weapon</c>.
/// The 3D-load pipeline, layers panel, gizmo, and viewport are shared
/// across both modes — only the sidebar's bottom section and the
/// footer button label diverge.</summary>
public enum ExportMode { Prop }

/// <summary>
/// Top-level view shown by MainWindow. Replaces the previous tab strip:
/// the app boots into <see cref="Dashboard"/> (4 home-screen tiles) and
/// drills into one of the feature panes when a tile is clicked. The
/// header back button (visible when not on Dashboard) returns to it.
/// </summary>
public enum AppView { Dashboard, Props, AnimatedProps, Optimize, Rpf, Vehicles, ImageTo3D, Emotes }

/// <summary>One rotation key on the Animated props timeline (degrees, seconds).</summary>
public sealed partial class PropAnimKey : ObservableObject
{
    [ObservableProperty] private double _time;
    [ObservableProperty] private double _rotX;
    [ObservableProperty] private double _rotY;
    [ObservableProperty] private double _rotZ;
}

/// <summary>Per-part visual material preset. Drives the RAGE shader written
/// into the YDR (glass / emissive* / metal / cutout) and, for glass, the
/// collision material index assigned to that part's polygons so the engine
/// plays shatter VFX on hit. Metal emits a spec shader (a synthesized highlight
/// when the source has no spec map); Cutout forces the alpha-tested shader.</summary>
public enum MaterialPreset { Standard, Glass, Emissive, EmissiveStrong, EmissiveNight, Metal, Cutout }

public partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        OptimizeVm = new OptimizeViewModel(s => StatusText = s);
        TxAdminVm = new TxAdminOptimizeViewModel(s => StatusText = s);
        RpfVm = new RpfConverterViewModel(s => StatusText = s);
        VehiclesVm = new VehiclesViewModel(s => StatusText = s);
        CarcolsVm = new CarcolsFixerViewModel(s => StatusText = s);
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

        // Keep the GLASS sidebar section in sync with layer Material → Glass.
        ModelParts.CollectionChanged += OnModelPartsCollectionChanged;
        ModelParts.CollectionChanged += (_, _) => RebuildOutlinerWorking();
        PackSession.Entries.CollectionChanged += (_, _) => EnsureOutlinerStructure();
        // Queue changes can park/un-park the loaded model's top row
        // (drag-into-group hides it; removing the pending row restores it).
        PackSession.ConvertQueue.CollectionChanged += (_, _) => RebuildOutlinerWorking();
        // Session rebuilds recreate its nodes, so mirror them into
        // OutlinerRoots every time the tree changes shape.
        PackSession.TreeChanged += (_, _) => SyncPackOutlinerRoot();
        PackSession.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Services.PropPackSession.HasEntries)
                or nameof(Services.PropPackSession.HasConvertQueue)
                or nameof(Services.PropPackSession.Count))
                EnsureOutlinerStructure();
        };
        EnsureOutlinerStructure();
        RebuildOutlinerWorking();

        // Undo/redo: capture the initial state and start listening for
        // changes to the tracked properties. Has to happen at the end of
        // construction so all field initialisers have already run.
        _currentSnapshot = TakeSnapshot();
        PropertyChanged += OnTrackedPropertyChanged;

        // Chrome document tabs — restore last session (or seed Assets).
        WorkspaceDocs = new WorkspaceDocumentSet();
        RestoreWorkspaceSession();
        WorkspaceDocs.Documents.CollectionChanged += (_, _) => ScheduleSaveWorkspaceSession();
        WorkspaceDocs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WorkspaceDocumentSet.ActiveDocument)
                or nameof(WorkspaceDocumentSet.ActiveDocumentId))
                ScheduleSaveWorkspaceSession();
        };
    }

    /// <summary>App-chrome document tabs — one entry per open section
    /// document (Assets / Optimize / Emotes / …). Emotes multi-docs link
    /// via <see cref="WorkspaceDocument.EmoteDocumentId"/>.</summary>
    public WorkspaceDocumentSet WorkspaceDocs { get; }

    /// <summary>When true, <see cref="SyncWorkspaceTabForActiveView"/> is a
    /// no-op (startup Emotes viewer warm must not spawn chrome tabs).</summary>
    public bool SuppressWorkspaceTabSync { get; set; }

    /// <summary>Set by MainWindow: close the emote document whose chrome tab
    /// was just navigated to another section, so the orphaned doc can't
    /// resurrect as a duplicate tab. Arg is the EmoteDocument id.</summary>
    public System.Action<string>? DetachEmoteDocument { get; set; }

    private System.Windows.Threading.DispatcherTimer? _workspaceSessionSaveTimer;

    private void ScheduleSaveWorkspaceSession()
    {
        if (SuppressWorkspaceTabSync) return;
        _workspaceSessionSaveTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _workspaceSessionSaveTimer.Tick -= OnWorkspaceSessionSaveTick;
        _workspaceSessionSaveTimer.Tick += OnWorkspaceSessionSaveTick;
        _workspaceSessionSaveTimer.Stop();
        _workspaceSessionSaveTimer.Start();
    }

    private void OnWorkspaceSessionSaveTick(object? sender, EventArgs e)
    {
        _workspaceSessionSaveTimer?.Stop();
        SaveWorkspaceSessionNow();
    }

    public void SaveWorkspaceSessionNow()
    {
        var tabs = WorkspaceDocs.Documents.Select(d => new Services.WorkspaceSessionTabBlob
        {
            Kind = d.Kind.ToString(),
            Title = d.Title,
        }).ToList();
        var activeIdx = 0;
        if (WorkspaceDocs.ActiveDocument != null)
        {
            var i = WorkspaceDocs.Documents.IndexOf(WorkspaceDocs.ActiveDocument);
            if (i >= 0) activeIdx = i;
        }
        Services.UserSettings.SaveWorkspaceSession(tabs, activeIdx, ActiveView.ToString());
    }

    private void RestoreWorkspaceSession()
    {
        // Open a single tab for the last active page — not the whole prior
        // strip (Assets + leftover Emotes / GTA Male, etc.).
        var viewName = Services.UserSettings.LoadLastActiveView();
        AppView view = AppView.Props;
        if (!string.IsNullOrWhiteSpace(viewName)
            && Enum.TryParse<AppView>(viewName, ignoreCase: true, out var parsed)
            && parsed is not AppView.Dashboard)
            view = parsed;

        var saved = Services.UserSettings.LoadWorkspaceSessionTabs();
        WorkspaceKind kind;
        string title;
        if (saved.Count > 0)
        {
            var idx = Math.Clamp(Services.UserSettings.LoadWorkspaceSessionActiveIndex(), 0, saved.Count - 1);
            var t = saved[idx];
            kind = Enum.TryParse<WorkspaceKind>(t.Kind, ignoreCase: true, out var k)
                ? k
                : WorkspaceDocument.KindFromAppView(view) ?? WorkspaceKind.Assets;
            title = string.IsNullOrWhiteSpace(t.Title)
                ? WorkspaceDocument.DefaultTitleFor(kind)
                : t.Title.Trim();
            // Prefer LastActiveView when it disagrees with a stale tab kind
            // (e.g. saved Emotes title but user left on Assets).
            var viewKind = WorkspaceDocument.KindFromAppView(view);
            if (viewKind != null && viewKind != kind)
            {
                kind = viewKind.Value;
                title = WorkspaceDocument.DefaultTitleFor(kind);
            }
        }
        else
        {
            kind = WorkspaceDocument.KindFromAppView(view) ?? WorkspaceKind.Assets;
            title = WorkspaceDocument.DefaultTitleFor(kind);
        }

        // Emotes tabs need a live EmoteDocument link — open as Untitled;
        // SyncEmoteWorkspaceTabs binds when Emotes is shown.
        if (kind == WorkspaceKind.Emotes
            && (string.IsNullOrWhiteSpace(title)
                || title.StartsWith("GTA ", StringComparison.OrdinalIgnoreCase)))
            title = "Untitled";

        var doc = new WorkspaceDocument { Kind = kind, Title = title };
        WorkspaceDocs.ReplaceAll(new[] { doc }, doc);

        ActiveView = kind switch
        {
            WorkspaceKind.Assets => view is AppView.AnimatedProps ? AppView.AnimatedProps : AppView.Props,
            WorkspaceKind.Optimize => AppView.Optimize,
            WorkspaceKind.Emotes => AppView.Emotes,
            WorkspaceKind.Vehicles => AppView.Vehicles,
            WorkspaceKind.Rpf => AppView.Rpf,
            _ => AppView.Props,
        };
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

    /// <summary>True when any layer is tagged Material → Glass (including
    /// auto-detected windows). Drives visibility of the GLASS sidebar
    /// block — Appearance / Breakable only matter once glass exists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGlassSettings))]
    private bool _hasGlassMaterial;

    /// <summary>GLASS section in the prop sidebar — prop mode AND at least
    /// one part tagged Glass.</summary>
    public bool ShowGlassSettings => IsPropMode && HasGlassMaterial;

    private readonly System.Collections.Generic.List<ModelPart> _glassWatchParts = new();

    private void OnModelPartsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        ResyncGlassMaterialWatchers();
        RefreshHasGlassMaterial();
    }

    private void ResyncGlassMaterialWatchers()
    {
        foreach (var p in _glassWatchParts)
            p.PropertyChanged -= OnWatchedPartPropertyChanged;
        _glassWatchParts.Clear();
        foreach (var p in ModelParts)
        {
            p.PropertyChanged += OnWatchedPartPropertyChanged;
            _glassWatchParts.Add(p);
        }
    }

    private void OnWatchedPartPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModelPart.MaterialPreset))
            RefreshHasGlassMaterial();
    }

    private void RefreshHasGlassMaterial()
    {
        var has = false;
        foreach (var p in ModelParts)
        {
            if (p.MaterialPreset == MaterialPreset.Glass) { has = true; break; }
        }
        HasGlassMaterial = has;
    }

    /// <summary>Names of parts the user has deleted from the layers panel.
    /// Deleted parts are removed from <see cref="ModelParts"/> (so they
    /// disappear from the panel) but stay tracked here so the convert
    /// path still passes them as <c>--exclude-mesh</c> alongside hidden
    /// ones. Cleared on every model load.</summary>
    public System.Collections.Generic.HashSet<string> DeletedPartNames { get; } =
        new(System.StringComparer.Ordinal);

    /// <summary>Per-part diffuse overrides from Add Missing Textures / the
    /// layer "Change textures" menu. Keyed by <see cref="ModelPart.OriginalName"/>
    /// → absolute path to a staged image copy. Fed to convert as
    /// <c>--part-diffuse</c> so the YDR bake uses them (not preview-only).</summary>
    public System.Collections.Generic.Dictionary<string, string> PartDiffuseOverrides { get; } =
        new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Diffuse maps found on the loaded model (sidebar thumbnails).</summary>
    public ObservableCollection<ModelTextureItem> ModelTextures { get; } = new();

    /// <summary>Extra recolor textures the user added — each becomes its own
    /// prop when Build pack runs.</summary>
    public TextureVariantsViewModel TextureVariants { get; private set; } =
        new(System.Array.Empty<string>(), "");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModelTexturesEmpty))]
    private bool _modelTexturesLoading;

    public bool ShowModelTexturesEmpty =>
        !ModelTexturesLoading && ModelTextures.Count == 0;

    public bool HasExtraTextures =>
        ModelTextures.Any(t => t.CanRemove && !string.IsNullOrEmpty(t.Path));

    [ObservableProperty]
    private ModelTextureItem? _selectedTexture;

    /// <summary>Create a fresh variants VM only when empty — never wipe staged
    /// textures mid-session (parts-sync used to DisposeStage and break Build pack).
    /// Also keep the stage alive when the library still lists user images even
    /// if Variants was Cleared after a pack — otherwise Ensure would DisposeStage
    /// and Build would claim the list is empty.</summary>
    public void EnsureTextureVariants(System.Collections.Generic.IReadOnlyList<string> partNames, string propBaseName)
    {
        bool libraryExtras = ModelTextures.Any(t =>
            t.CanRemove && !string.IsNullOrWhiteSpace(t.Path));
        if (TextureVariants.HasItems || libraryExtras)
        {
            TextureVariants.UpdatePartNames(partNames);
            return;
        }
        ResetTextureVariants(partNames, propBaseName);
    }

    public void ResetTextureVariants(System.Collections.Generic.IReadOnlyList<string> partNames, string propBaseName)
    {
        TextureVariants.DisposeStage();
        TextureVariants = new TextureVariantsViewModel(partNames, propBaseName);
        TextureVariants.Variants.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasExtraTextures));
            OnPropertyChanged(nameof(ShowModelTexturesEmpty));
            OnPropertyChanged(nameof(SimplePrimaryButtonLabel));
            OnPropertyChanged(nameof(SimplePrimaryButtonHint));
        };
        OnPropertyChanged(nameof(TextureVariants));
        OnPropertyChanged(nameof(HasExtraTextures));
        OnPropertyChanged(nameof(ShowModelTexturesEmpty));
        OnPropertyChanged(nameof(SimplePrimaryButtonLabel));
        OnPropertyChanged(nameof(SimplePrimaryButtonHint));
    }

    public void ClearModelTextures()
    {
        SelectedTexture = null;
        ModelTextures.Clear();
        ModelTexturesLoading = false;
        NotifyTextureListUi();
    }

    public void NotifyTextureListUi()
    {
        OnPropertyChanged(nameof(ShowModelTexturesEmpty));
        OnPropertyChanged(nameof(HasExtraTextures));
        OnPropertyChanged(nameof(SimplePrimaryButtonLabel));
        OnPropertyChanged(nameof(SimplePrimaryButtonHint));
    }

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
    private bool _isLayersPanelCollapsed = Services.UserSettings.LoadLayersPanelCollapsed();

    partial void OnIsLayersPanelCollapsedChanged(bool value)
        => Services.UserSettings.SaveLayersPanelCollapsed(value);

    /// <summary>True = PANELS sidebar left of the viewport; false = right.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LayersDockColumn))]
    [NotifyPropertyChangedFor(nameof(LayersPanelMargin))]
    [NotifyPropertyChangedFor(nameof(PanelsResizeThumbAlign))]
    private bool _isLayersDockedLeft = Services.UserSettings.LoadLayersDockedLeft();

    partial void OnIsLayersDockedLeftChanged(bool value)
        => Services.UserSettings.SaveLayersDockedLeft(value);

    /// <summary>Expanded width of the PANELS sidebar (drag the edge to resize).</summary>
    [ObservableProperty]
    private double _layersPanelWidth = Services.UserSettings.LoadLayersPanelWidth();

    /// <summary>Persist width after a resize drag ends (not on every pixel).</summary>
    public void CommitLayersPanelWidth()
    {
        if (LayersPanelWidth >= 280 && LayersPanelWidth <= 640)
            Services.UserSettings.SaveLayersPanelWidth(LayersPanelWidth);
    }

    /// <summary>Column index inside PropsCenterGrid (0 left / 2 right).</summary>
    public int LayersDockColumn => IsLayersDockedLeft ? 0 : 2;

    public System.Windows.Thickness LayersPanelMargin => IsLayersDockedLeft
        ? new System.Windows.Thickness(0, 0, 4, 0)
        : new System.Windows.Thickness(4, 0, 0, 0);

    /// <summary>Resize grip sits on the viewport-facing edge.</summary>
    public System.Windows.HorizontalAlignment PanelsResizeThumbAlign => IsLayersDockedLeft
        ? System.Windows.HorizontalAlignment.Right
        : System.Windows.HorizontalAlignment.Left;

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

    /// <summary>Owns the "Carcols Fixer" tab — scan a resources folder for
    /// modkit / siren / lightSettings id collisions across carcols.meta
    /// files and auto-remap them (updating carvariations references).</summary>
    public CarcolsFixerViewModel CarcolsVm { get; }

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

    /// <summary>Optional breakable glass. When on, glass parts (tagged or
    /// auto-detected) export with GLASS_SHOOT_THROUGH collision so bullets
    /// pass through and the engine plays the glass-shatter VFX + sound when
    /// shot. Off leaves glass solid. Shatter-on-shot, not a true .yft
    /// fragment. Off by default so a plain solid conversion is the norm.</summary>
    [ObservableProperty]
    private bool _breakableGlass;

    /// <summary>Glass appearance 0..1: 0 = clear see-through, 1 = opaque and
    /// reflective (mirror-like). Drives the generated glass alpha + glass.sps
    /// reflection strength on export.</summary>
    [ObservableProperty]
    private double _glassOpacity = 0.6;

    [ObservableProperty]
    private bool _includeYtyp = true;

    [ObservableProperty]
    private bool _extractTextures = true;

    /// <summary>When on, and the loaded model ships a rig + animation clip,
    /// the prop exports as an ANIMATED prop: the engine attaches a skeleton
    /// to the drawable and rigidly binds each part to a bone, then writes a
    /// matching .ycd + a client.lua that drives it in-game via
    /// PlayEntityAnim. Falls back to a normal static prop when the source
    /// has no animation. Rigid bone-binding animates WHOLE parts, so it fits
    /// mechanical models whose moving pieces are separate meshes; a single
    /// one-piece skinned mesh won't articulate this way. Off by default.</summary>
    [ObservableProperty]
    private bool _animatedProp;

    /// <summary>Auto-spin: when on (with Animated prop), the engine SYNTHESIZES
    /// a 360° spin for a model that has no animation of its own — one rotation
    /// bone at the centroid, whole model bound to it. Lets a plain gear / fan /
    /// wheel spin with zero animation prep.</summary>
    [ObservableProperty]
    private bool _autoSpin;

    /// <summary>Spin axis index: 0 = X, 1 = Y, 2 = Z (vertical, default).</summary>
    [ObservableProperty]
    private int _spinAxisIndex = 2;

    /// <summary>Seconds per full revolution for auto-spin.</summary>
    [ObservableProperty]
    private double _spinSeconds = 4.0;

    /// <summary>Reverse the auto-spin direction.</summary>
    [ObservableProperty]
    private bool _spinReverse;

    // ── Animated props keys timeline ────────────────────────────────
    // Author rotation keys for rigid animated props. On convert with 2+
    // keys the engine builds a .ycd from the timeline instead of (or in
    // addition to) sampling a source clip / auto-spin.

    public ObservableCollection<PropAnimKey> PropAnimKeys { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PropAnimPlayheadLabel))]
    private double _propAnimDuration = 4.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PropAnimPlayheadLabel))]
    private double _propAnimPlayhead;

    [ObservableProperty]
    private int _propAnimFps = 30;

    [ObservableProperty]
    private bool _isPropAnimPlaying;

    public string PropAnimPlayheadLabel =>
        $"{PropAnimPlayhead:0.00}s / {PropAnimDuration:0.00}s  ·  {PropAnimKeys.Count} key{(PropAnimKeys.Count == 1 ? "" : "s")}";

    public void EnsureDefaultPropAnimKeys()
    {
        if (PropAnimKeys.Count > 0) return;
        PropAnimDuration = SpinSeconds > 0.05 ? SpinSeconds : 4.0;
        PropAnimPlayhead = 0;
        // Default loop: identity → full turn on the chosen spin axis.
        PropAnimKeys.Add(new PropAnimKey { Time = 0, RotX = 0, RotY = 0, RotZ = 0 });
        PropAnimKeys.Add(SpinAxisIndex switch
        {
            0 => new PropAnimKey { Time = PropAnimDuration, RotX = 360, RotY = 0, RotZ = 0 },
            1 => new PropAnimKey { Time = PropAnimDuration, RotX = 0, RotY = 360, RotZ = 0 },
            _ => new PropAnimKey { Time = PropAnimDuration, RotX = 0, RotY = 0, RotZ = 360 },
        });
        OnPropertyChanged(nameof(PropAnimPlayheadLabel));
    }

    [RelayCommand]
    private void AddPropAnimKey()
    {
        EnsureDefaultPropAnimKeys();
        var t = Math.Clamp(PropAnimPlayhead, 0, PropAnimDuration);
        // Replace an existing key at the same frame snap, otherwise insert.
        var frame = 1.0 / Math.Max(1, PropAnimFps);
        var existing = PropAnimKeys.FirstOrDefault(k => Math.Abs(k.Time - t) < frame * 0.5);
        if (existing != null)
        {
            existing.RotX = TransformRotX;
            existing.RotY = TransformRotY;
            existing.RotZ = TransformRotZ;
        }
        else
        {
            PropAnimKeys.Add(new PropAnimKey
            {
                Time = t,
                RotX = TransformRotX,
                RotY = TransformRotY,
                RotZ = TransformRotZ,
            });
            SortPropAnimKeys();
        }
        OnPropertyChanged(nameof(PropAnimPlayheadLabel));
    }

    [RelayCommand]
    private void DeletePropAnimKey()
    {
        if (PropAnimKeys.Count == 0) return;
        var frame = 1.0 / Math.Max(1, PropAnimFps);
        var hit = PropAnimKeys
            .OrderBy(k => Math.Abs(k.Time - PropAnimPlayhead))
            .FirstOrDefault();
        if (hit == null || Math.Abs(hit.Time - PropAnimPlayhead) > frame * 1.5) return;
        // Keep at least two keys so export still has a motion span.
        if (PropAnimKeys.Count <= 2) return;
        PropAnimKeys.Remove(hit);
        OnPropertyChanged(nameof(PropAnimPlayheadLabel));
    }

    [RelayCommand]
    private void ClearPropAnimKeys()
    {
        PropAnimKeys.Clear();
        EnsureDefaultPropAnimKeys();
    }

    public void SortPropAnimKeys()
    {
        var sorted = PropAnimKeys.OrderBy(k => k.Time).ToList();
        PropAnimKeys.Clear();
        foreach (var k in sorted) PropAnimKeys.Add(k);
    }

    public void NotifyPropAnimKeysChanged() => OnPropertyChanged(nameof(PropAnimPlayheadLabel));

    /// <summary>Lerp rotation at the playhead and push into the gizmo for preview.</summary>
    public void ApplyPropAnimPlayheadToTransform()
    {
        if (PropAnimKeys.Count == 0) return;
        var sorted = PropAnimKeys.OrderBy(k => k.Time).ToList();
        var t = Math.Clamp(PropAnimPlayhead, 0, PropAnimDuration);
        if (t <= sorted[0].Time)
        {
            TransformRotX = sorted[0].RotX;
            TransformRotY = sorted[0].RotY;
            TransformRotZ = sorted[0].RotZ;
            return;
        }
        var last = sorted[^1];
        if (t >= last.Time)
        {
            TransformRotX = last.RotX;
            TransformRotY = last.RotY;
            TransformRotZ = last.RotZ;
            return;
        }
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var a = sorted[i];
            var b = sorted[i + 1];
            if (t < a.Time || t > b.Time) continue;
            var u = (b.Time - a.Time) < 1e-9 ? 0 : (t - a.Time) / (b.Time - a.Time);
            TransformRotX = a.RotX + (b.RotX - a.RotX) * u;
            TransformRotY = a.RotY + (b.RotY - a.RotY) * u;
            TransformRotZ = a.RotZ + (b.RotZ - a.RotZ) * u;
            return;
        }
    }

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

    // Weapon export was removed (to be re-added later). These stay as constant
    // properties so existing XAML visibility bindings keep resolving: weapon-
    // only sections are always collapsed, prop sections always shown.
    public bool IsWeaponMode => false;
    public bool IsPropMode   => true;

    public string ConvertButtonLabel => IsPackMode ? "Add to Pack" : "Convert";

    /// <summary>Yes = each added texture becomes its own prop in a pack
    /// (TextureVariantPipeline). No = embed the selected texture into
    /// this one model on Convert.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SimplePrimaryButtonLabel))]
    [NotifyPropertyChangedFor(nameof(SimplePrimaryButtonHint))]
    private bool _packTextureVariants;

    /// <summary>Primary action on the simple Props panel.</summary>
    public string SimplePrimaryButtonLabel
    {
        get
        {
            if (PackTextureVariants && HasExtraTextures)
                return "Build pack";
            return IsPackMode ? "Add to Pack" : "Convert";
        }
    }

    public string SimplePrimaryButtonHint => PackTextureVariants
        ? "Each added texture becomes its own prop (Ctrl+Enter / File → Convert)."
        : "Embed the current texture into this one model (Ctrl+Enter / File → Convert).";

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
    [NotifyPropertyChangedFor(nameof(SimplePrimaryButtonLabel))]
    [NotifyPropertyChangedFor(nameof(IsPackPanelVisible))]
    private bool _isPackMode;

    /// <summary>Direct binding target for the pack panel. Backed by the
    /// process-wide singleton — quitting the app drops the pack.</summary>
    public Services.PropPackSession PackSession => Services.PropPackSession.Current;

    /// <summary>Unified Layers outliner roots: current prop + pack tree.</summary>
    public System.Collections.ObjectModel.ObservableCollection<Services.PropPackTreeNode> OutlinerRoots { get; } = new();

    /// <summary>Multi-part prop container (named after <see cref="PropName"/>).</summary>
    private Services.PropPackTreeNode? _workingRoot;

    /// <summary>Single-mesh prop root (a Part node labeled with the prop name).</summary>
    private Services.PropPackTreeNode? _flatPropRoot;

    /// <summary>Multi-select set for outliner context actions.</summary>
    public System.Collections.ObjectModel.ObservableCollection<Services.PropPackTreeNode> SelectedOutlinerNodes { get; } = new();

    /// <summary>Prop leaf selected in the pack outliner — drives the
    /// compact housing-field editors under the tree.</summary>
    [ObservableProperty]
    private Services.PropPackEntry? _selectedPackEntry;

    /// <summary>Part selected under the current prop — drives mesh-optimize strip.</summary>
    [ObservableProperty]
    private ModelPart? _selectedModelPart;

    /// <summary>Footer pack panel collapses when pack mode is off so the
    /// regular convert flow has the full footer width.</summary>
    public bool IsPackPanelVisible => IsPackMode;

    public void EnsureOutlinerStructure()
    {
        // Model roots are owned by RebuildOutlinerWorking; only sync pack here.
        if (ModelParts.Count > 0 && _workingRoot is null && _flatPropRoot is null)
            RebuildOutlinerWorking();
        SyncPackOutlinerRoot();
    }

    private void SyncPackOutlinerRoot()
    {
        // Photoshop layout: model roots stay on top, then every session
        // root (group folders, loose staged layers, loose queue rows) in
        // session order. Session rebuilds recreate nodes wholesale, so
        // drop all non-model roots and re-add the current set.
        for (int i = OutlinerRoots.Count - 1; i >= 0; i--)
        {
            var n = OutlinerRoots[i];
            if (n.IsPack || n.IsProp || n.IsQueueItem || n.IsStream || n.IsQueue)
                OutlinerRoots.RemoveAt(i);
        }

        if (PackSession.HasOutlinerContent)
        {
            foreach (var root in PackSession.TreeRoots)
                OutlinerRoots.Add(root);
        }

        // Session rebuilds recreate their nodes, which would silently drop
        // the highlight (IsSelected lives on node objects). Rebind the
        // selection to the NEW nodes by identity so it survives.
        RestoreOutlinerSelection();
    }

    /// <summary>Re-point <see cref="SelectedOutlinerNodes"/> at the current
    /// tree's nodes after a rebuild, matching by what a node REPRESENTS
    /// (entry / queue item / part reference, group key) rather than object
    /// identity.</summary>
    private void RestoreOutlinerSelection()
    {
        if (SelectedOutlinerNodes.Count == 0) return;

        var wanted = SelectedOutlinerNodes.ToList();
        var fresh = new System.Collections.Generic.List<Services.PropPackTreeNode>();
        foreach (var old in wanted)
        {
            var match = FindEquivalentNode(old);
            if (match is not null && !fresh.Contains(match))
                fresh.Add(match);
        }

        // Nothing changed object-wise? Leave everything alone.
        if (fresh.Count == wanted.Count && fresh.Zip(wanted, ReferenceEquals).All(x => x))
            return;

        foreach (var n in wanted) n.IsSelected = false;
        SelectedOutlinerNodes.Clear();
        foreach (var n in fresh)
        {
            n.IsSelected = true;
            SelectedOutlinerNodes.Add(n);
        }
    }

    private Services.PropPackTreeNode? FindEquivalentNode(Services.PropPackTreeNode old)
    {
        Services.PropPackTreeNode? Walk(System.Collections.Generic.IEnumerable<Services.PropPackTreeNode> nodes)
        {
            foreach (var n in nodes)
            {
                bool match =
                    (old.Entry is not null && ReferenceEquals(n.Entry, old.Entry)) ||
                    (old.QueueItem is not null && ReferenceEquals(n.QueueItem, old.QueueItem)) ||
                    (old.Part is not null && ReferenceEquals(n.Part, old.Part) && n.Kind == old.Kind) ||
                    (old.IsPack && n.IsPack && string.Equals(n.GroupKey, old.GroupKey, System.StringComparison.OrdinalIgnoreCase)) ||
                    (old.IsWorking && n.IsWorking);
                if (match) return n;
                var inner = Walk(n.Children);
                if (inner is not null) return inner;
            }
            return null;
        }
        return Walk(OutlinerRoots);
    }

    /// <summary>First selected group in the outliner — new converts and
    /// "Add model(s)" target it; with nothing selected they land loose.</summary>
    public string? TargetOutlinerGroup =>
        SelectedOutlinerNodes.FirstOrDefault(n => n.IsPack)?.GroupKey
        ?? SelectedOutlinerNodes.FirstOrDefault(n => n.Entry?.GroupName is not null)?.Entry?.GroupName;

    /// <summary>Photoshop "new group" — empty folder, named uniquely;
    /// the view starts an inline rename on the fresh row.</summary>
    public string CreateOutlinerGroup() => PackSession.AddGroup();

    /// <summary>Ctrl+G — move the selected staged layers into a fresh
    /// group. Selected queue rows retarget too. Returns the group name,
    /// or null when the selection holds no groupable rows.</summary>
    public string? GroupSelectedOutlinerLayers()
    {
        var entries = SelectedOutlinerNodes.Where(n => n.Entry is not null).Select(n => n.Entry!).ToList();
        var queued = SelectedOutlinerNodes.Where(n => n.QueueItem is not null).Select(n => n.QueueItem!).ToList();
        if (entries.Count == 0 && queued.Count == 0) return null;

        var name = PackSession.AddGroup();
        foreach (var e in entries) e.GroupName = name;
        foreach (var q in queued) q.GroupName = name;
        PackSession.RebuildTree();
        return name;
    }

    private void RemoveModelOutlinerRoots()
    {
        for (int i = OutlinerRoots.Count - 1; i >= 0; i--)
        {
            var n = OutlinerRoots[i];
            if (n.IsWorking || n.IsPart)
                OutlinerRoots.RemoveAt(i);
        }
        _workingRoot = null;
        _flatPropRoot = null;
    }

    /// <summary>Outliner root label — prop name, else first mesh name.</summary>
    private string ResolveOutlinerPropName()
    {
        if (!string.IsNullOrWhiteSpace(PropName))
            return PropName.Trim();
        if (ModelParts.Count > 0 && !string.IsNullOrWhiteSpace(ModelParts[0].Name))
            return ModelParts[0].Name;
        return "Untitled";
    }

    partial void OnPropNameChanged(string value) => UpdateOutlinerPropRootName();

    private void UpdateOutlinerPropRootName()
    {
        var name = ResolveOutlinerPropName();
        if (_workingRoot is not null)
            _workingRoot.Name = name;
        if (_flatPropRoot is not null)
            _flatPropRoot.Name = name;
    }

    /// <summary>True while the LOADED model has been dragged into a group:
    /// its pending queue row inside the group IS its layer row, so the
    /// top-level working root hides (no duplicate). The model itself stays
    /// in the viewport.</summary>
    public bool IsModelParkedInPack =>
        !string.IsNullOrEmpty(SourcePath) &&
        PackSession.ConvertQueue.Any(q => q.IsPending &&
            string.Equals(q.SourcePath, SourcePath, System.StringComparison.OrdinalIgnoreCase));

    public void RebuildOutlinerWorking()
    {
        foreach (var p in ModelParts)
            p.PropertyChanged -= OnOutlinerPartPropertyChanged;

        RemoveModelOutlinerRoots();

        if (ModelParts.Count == 0 || IsModelParkedInPack)
        {
            SyncPackOutlinerRoot();
            return;
        }

        var propName = ResolveOutlinerPropName();

        // One mesh: tree starts at the prop name (no "Working" wrapper).
        if (ModelParts.Count == 1)
        {
            var p = ModelParts[0];
            _flatPropRoot = new Services.PropPackTreeNode
            {
                Kind = Services.PropPackTreeNode.NodeKind.Part,
                Name = propName,
                Part = p,
                IsExpanded = false,
                IsEyeOn = p.IsVisible,
            };
            OutlinerRoots.Insert(0, _flatPropRoot);
            p.PropertyChanged += OnOutlinerPartPropertyChanged;
            SyncPackOutlinerRoot();
            return;
        }

        // Several meshes: prop name is the parent, parts underneath.
        _workingRoot = new Services.PropPackTreeNode
        {
            Kind = Services.PropPackTreeNode.NodeKind.Working,
            Name = propName,
            IsExpanded = true,
            Detail = $"{ModelParts.Count} parts",
            IsEyeOn = ModelParts.Any(p => p.IsVisible),
        };
        foreach (var p in ModelParts)
        {
            var node = new Services.PropPackTreeNode
            {
                Kind = Services.PropPackTreeNode.NodeKind.Part,
                Name = p.Name,
                Part = p,
                IsExpanded = false,
                IsEyeOn = p.IsVisible,
            };
            _workingRoot.Children.Add(node);
            p.PropertyChanged += OnOutlinerPartPropertyChanged;
        }
        OutlinerRoots.Insert(0, _workingRoot);
        SyncPackOutlinerRoot();
    }

    private void OnOutlinerPartPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not ModelPart part) return;
        if (e.PropertyName is not (nameof(ModelPart.Name) or nameof(ModelPart.IsVisible))) return;

        // Flat prop root mirrors visibility but keeps the PropName label.
        if (_flatPropRoot is not null && ReferenceEquals(_flatPropRoot.Part, part))
        {
            if (e.PropertyName == nameof(ModelPart.IsVisible))
                _flatPropRoot.IsEyeOn = part.IsVisible;
            return;
        }

        if (_workingRoot is null) return;
        foreach (var n in _workingRoot.Children)
        {
            if (!ReferenceEquals(n.Part, part)) continue;
            if (e.PropertyName == nameof(ModelPart.Name))
                n.Name = part.Name;
            else if (e.PropertyName == nameof(ModelPart.IsVisible))
                n.IsEyeOn = part.IsVisible;
            break;
        }
        // Group eye = aggregate of the parts.
        _workingRoot.IsEyeOn = ModelParts.Any(p => p.IsVisible);
    }

    public void ClearOutlinerSelection()
    {
        foreach (var n in SelectedOutlinerNodes)
            n.IsSelected = false;
        SelectedOutlinerNodes.Clear();
        SelectedPackEntry = null;
        SelectedModelPart = null;
    }

    public void SetOutlinerSelection(IEnumerable<Services.PropPackTreeNode> nodes, bool additive)
    {
        var list = nodes as IList<Services.PropPackTreeNode> ?? nodes.ToList();

        // Re-selecting the exact current selection is a no-op — the click
        // path fires from BOTH PreviewMouseDown and SelectedItemChanged,
        // and clearing + re-adding made the highlight flicker.
        if (!additive && list.Count == SelectedOutlinerNodes.Count &&
            list.All(n => SelectedOutlinerNodes.Contains(n)))
            return;

        if (!additive)
            ClearOutlinerSelection();
        nodes = list;

        foreach (var n in nodes)
        {
            if (n is null) continue;
            if (SelectedOutlinerNodes.Contains(n))
            {
                if (additive)
                {
                    n.IsSelected = false;
                    SelectedOutlinerNodes.Remove(n);
                }
                continue;
            }
            n.IsSelected = true;
            SelectedOutlinerNodes.Add(n);
        }

        SelectedPackEntry = SelectedOutlinerNodes.Select(x => x.Entry).FirstOrDefault(e => e is not null);
        SelectedModelPart = SelectedOutlinerNodes.Select(x => x.Part).FirstOrDefault(p => p is not null);
    }

    partial void OnIsPackModeChanged(bool value)
    {
        // Default pack name to the current prop name (or a sensible
        // fallback) the first time the user flips into pack mode, so the
        // panel isn't blank.
        if (value && string.IsNullOrWhiteSpace(PackSession.PackName))
            PackSession.PackName = string.IsNullOrWhiteSpace(PropName) ? "props_pack" : PropName + "_pack";

        if (value)
        {
            IsLayersPanelOpen = true;
            IsLayersPanelCollapsed = false;
            if (LayersPanelWidth < 300)
                LayersPanelWidth = 340;
        }
        EnsureOutlinerStructure();
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
    [NotifyPropertyChangedFor(nameof(IsAnimatedPropsView))]
    [NotifyPropertyChangedFor(nameof(IsOptimizeView))]
    [NotifyPropertyChangedFor(nameof(IsRpfView))]
    [NotifyPropertyChangedFor(nameof(IsVehiclesView))]
    [NotifyPropertyChangedFor(nameof(IsImageTo3DView))]
    [NotifyPropertyChangedFor(nameof(IsEmotesView))]
    [NotifyPropertyChangedFor(nameof(Is3DView))]
    [NotifyPropertyChangedFor(nameof(ShowAssetsEditMenu))]
    [NotifyPropertyChangedFor(nameof(ShowAssetsViewMenu))]
    [NotifyPropertyChangedFor(nameof(ActiveViewTitle))]
    private AppView _activeView = AppView.Props;   // boot into Props; Welcome splash floats over it

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
    // these flags directly. Same when Settings is open, so WebView2
    // HwndHosts drop their native windows (airspace) and Settings paints cleanly.
    public bool IsDashboard      => !IsPluginActive && !IsSettingsOpen && ActiveView == AppView.Dashboard;
    /// <summary>Static Props and Animated share the same convert workspace.</summary>
    public bool IsPropsView      => !IsPluginActive && !IsSettingsOpen && ActiveView is AppView.Props or AppView.AnimatedProps;
    public bool IsAnimatedPropsView => !IsPluginActive && !IsSettingsOpen && ActiveView == AppView.AnimatedProps;
    public bool IsOptimizeView   => !IsPluginActive && !IsSettingsOpen && ActiveView == AppView.Optimize;
    public bool IsRpfView        => !IsPluginActive && !IsSettingsOpen && ActiveView == AppView.Rpf;
    public bool IsVehiclesView   => !IsPluginActive && !IsSettingsOpen && ActiveView == AppView.Vehicles;
    public bool IsImageTo3DView  => !IsPluginActive && !IsSettingsOpen && ActiveView == AppView.ImageTo3D;
    public bool IsEmotesView     => !IsPluginActive && !IsSettingsOpen && ActiveView == AppView.Emotes;
    /// <summary>True when the Assets rail entry is active — Props and
    /// Animated (prop) share one rail slot and the top segmented toggle.
    /// Vehicles is its own rail peer.</summary>
    public bool Is3DView         => !IsPluginActive && !IsSettingsOpen
        && ActiveView is AppView.Props or AppView.AnimatedProps;

    /// <summary>True when Edit (undo/redo) applies — Assets prop workspace.</summary>
    public bool ShowAssetsEditMenu => Is3DView;
    /// <summary>True when View (reference ped / layers) applies.</summary>
    public bool ShowAssetsViewMenu => Is3DView;

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
        foreach (var old in AllPlugins)
            old.PropertyChanged -= OnPluginEntryChanged;

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
        foreach (var old in AllPlugins)
            old.PropertyChanged -= OnPluginEntryChanged;

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
        OnPropertyChanged(nameof(IsAnimatedPropsView));
        OnPropertyChanged(nameof(IsOptimizeView));
        OnPropertyChanged(nameof(IsRpfView));
        OnPropertyChanged(nameof(IsVehiclesView));
        OnPropertyChanged(nameof(IsImageTo3DView));
        OnPropertyChanged(nameof(IsEmotesView));
        OnPropertyChanged(nameof(Is3DView));
        OnPropertyChanged(nameof(ShowAssetsEditMenu));
        OnPropertyChanged(nameof(ShowAssetsViewMenu));
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
        AppView.Props          => "Props",
        AppView.AnimatedProps  => "Animated",
        AppView.Optimize       => "Optimize",
        AppView.Rpf            => "RPF",
        AppView.Vehicles       => "Vehicles",
        AppView.ImageTo3D      => "Image → 3D",
        AppView.Emotes         => "Emotes",
        _                      => "FiveOS",
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

        // Built-in target — drop any active plugin / Settings pane first.
        ActivePluginId = null;
        IsSettingsOpen = false;

        // Assets rail entry — keep the current sub-mode if already inside
        // Assets; otherwise open Props.
        if (string.Equals(view, "3D", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(view, "Assets", System.StringComparison.OrdinalIgnoreCase))
        {
            if (ActiveView is not (AppView.Props or AppView.AnimatedProps))
                ActiveView = AppView.Props;
            SyncWorkspaceTabForActiveView();
            return;
        }

        if (string.Equals(view, "Props", System.StringComparison.OrdinalIgnoreCase))
        {
            ExportMode = ExportMode.Prop;
            ActiveView = AppView.Props;
            SyncWorkspaceTabForActiveView();
            return;
        }

        if (string.Equals(view, "Animated", System.StringComparison.OrdinalIgnoreCase))
        {
            ExportMode = ExportMode.Prop;
            AnimatedProp = true;
            EnsureDefaultPropAnimKeys();
            ActiveView = AppView.AnimatedProps;
            SyncWorkspaceTabForActiveView();
            return;
        }

        // txAdmin is a mode card inside the Optimize tab rather than its own
        // AppView — deep-links (dashboard tile) land on Optimize with the
        // txAdmin card preselected.
        if (string.Equals(view, "TxAdmin", System.StringComparison.OrdinalIgnoreCase))
        {
            OptimizeVm.Mode = OptimizeMode.TxAdmin;
            ActiveView = AppView.Optimize;
            SyncWorkspaceTabForActiveView();
            return;
        }

        if (string.Equals(view, "Carcols", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(view, "Livery", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(view, "Vehicles", System.StringComparison.OrdinalIgnoreCase))
        {
            ActiveView = AppView.Vehicles;
            SyncWorkspaceTabForActiveView();
            return;
        }

        // The former Animation → Emote / Pose → Emote tabs are one combined
        // Emotes workspace now — keep the old names as deep-link aliases.
        if (string.Equals(view, "AnimToEmote", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(view, "PoseToEmote", System.StringComparison.OrdinalIgnoreCase))
        {
            OpenEmotesAsAnimLibrary = false;
            ActiveView = AppView.Emotes;
            SyncWorkspaceTabForActiveView();
            return;
        }

        // Animation Library is a mode inside Emotes (browse/preview vanilla
        // .ycd clips, then edit on the timeline).
        if (string.Equals(view, "AnimLibrary", System.StringComparison.OrdinalIgnoreCase))
        {
            OpenEmotesAsAnimLibrary = true;
            ActiveView = AppView.Emotes;
            SyncWorkspaceTabForActiveView();
            return;
        }

        if (System.Enum.TryParse<AppView>(view, ignoreCase: true, out var parsed))
        {
            if (parsed == AppView.Emotes)
                OpenEmotesAsAnimLibrary = false;
            ActiveView = parsed;
            SyncWorkspaceTabForActiveView();
        }
    }

    public void SyncWorkspaceTabForActiveView()
    {
        if (SuppressWorkspaceTabSync) return;
        var kind = WorkspaceDocument.KindFromAppView(ActiveView);
        if (kind == null) return;

        var active = WorkspaceDocs.ActiveDocument;
        if (active != null && active.Kind == kind.Value)
        {
            WorkspaceDocs.Activate(active);
            return;
        }

        // The workspace bar navigates the CURRENT tab, browser-style:
        // switching workspaces converts this tab in place — never jumps to a
        // sibling tab and never opens a new one. An Emotes tab converted this
        // way starts unlinked; SyncEmoteWorkspaceTabs binds it to an
        // EmoteDocument once the view is shown.
        if (active != null)
        {
            // Converting an Emotes tab detaches its EmoteDocument. Close that
            // document, otherwise it stays alive with no tab and later gets a
            // fresh tab re-materialised — the "duplicate tab" bug.
            var detachEmoteId = active.Kind == WorkspaceKind.Emotes
                ? active.EmoteDocumentId : null;
            WorkspaceDocs.Repurpose(active, kind.Value);
            if (!string.IsNullOrEmpty(detachEmoteId))
                DetachEmoteDocument?.Invoke(detachEmoteId);
            return;
        }

        WorkspaceDocs.EnsureKind(kind.Value, activate: true);
    }

    /// <summary>Create a new chrome tab for the current section (+ button).</summary>
    public WorkspaceDocument NewWorkspaceTabForActiveView()
    {
        var kind = WorkspaceDocument.KindFromAppView(ActiveView) ?? WorkspaceKind.Assets;
        // Number duplicate non-emote tabs: "Assets", "Assets 2", …
        string? title = null;
        if (kind != WorkspaceKind.Emotes)
        {
            var baseTitle = WorkspaceDocument.DefaultTitleFor(kind);
            int n = 0;
            foreach (var d in WorkspaceDocs.Documents)
                if (d.Kind == kind) n++;
            title = n == 0 ? baseTitle : $"{baseTitle} {n + 1}";
        }
        return WorkspaceDocs.NewDocument(kind, activate: true, title: title);
    }

    /// <summary>When true, opening Emotes should land on Animation Library
    /// mode. Consumed by MainWindow / PoseToEmoteView, then cleared.</summary>
    public bool OpenEmotesAsAnimLibrary { get; set; }

    [RelayCommand]
    private void GoHome()
    {
        IsSettingsOpen = false;
        ActivePluginId = null;
        ActiveView = AppView.Dashboard;
    }

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

    /// <summary>Toggles the Settings pane (rail entry + menu Ctrl+,).
    /// When open, built-in IsXView flags go false so WebView2 airspace
    /// collapses and the Settings host fills the content column.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewportVisible))]
    [NotifyPropertyChangedFor(nameof(IsDashboard))]
    [NotifyPropertyChangedFor(nameof(IsPropsView))]
    [NotifyPropertyChangedFor(nameof(IsAnimatedPropsView))]
    [NotifyPropertyChangedFor(nameof(IsOptimizeView))]
    [NotifyPropertyChangedFor(nameof(IsRpfView))]
    [NotifyPropertyChangedFor(nameof(IsVehiclesView))]
    [NotifyPropertyChangedFor(nameof(IsImageTo3DView))]
    [NotifyPropertyChangedFor(nameof(IsEmotesView))]
    [NotifyPropertyChangedFor(nameof(Is3DView))]
    [NotifyPropertyChangedFor(nameof(ShowAssetsEditMenu))]
    [NotifyPropertyChangedFor(nameof(ShowAssetsViewMenu))]
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

    // Universal undo/redo: Assets form history, Emotes pose/timeline (via
    // External* hooks wired by MainWindow), otherwise nothing to undo.
    // Window-level Ctrl+Z/Y KeyBindings always hit these commands — without
    // the view gate, Emotes focus would silently mutate the hidden Assets stack.
    public Func<bool>? ExternalCanUndo { get; set; }
    public Func<bool>? ExternalCanRedo { get; set; }
    public Action? ExternalUndo { get; set; }
    public Action? ExternalRedo { get; set; }

    public bool CanUndo
    {
        get
        {
            if (IsPluginActive || IsSettingsOpen) return false;
            if (ActiveView is AppView.Props or AppView.AnimatedProps)
                return _undoStack.Count > 0;
            if (ActiveView == AppView.Emotes)
                return ExternalCanUndo?.Invoke() ?? false;
            return false;
        }
    }

    public bool CanRedo
    {
        get
        {
            if (IsPluginActive || IsSettingsOpen) return false;
            if (ActiveView is AppView.Props or AppView.AnimatedProps)
                return _redoStack.Count > 0;
            if (ActiveView == AppView.Emotes)
                return ExternalCanRedo?.Invoke() ?? false;
            return false;
        }
    }

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
        if (ActiveView == AppView.Emotes)
        {
            ExternalUndo?.Invoke();
            NotifyHistoryChanged();
            return;
        }
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
        if (ActiveView == AppView.Emotes)
        {
            ExternalRedo?.Invoke();
            NotifyHistoryChanged();
            return;
        }
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

    public void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    // CanUndo/CanRedo are gated on the active tab, so the window-level
    // Ctrl+Z KeyBinding must re-query CanExecute whenever the tab changes.
    partial void OnActiveViewChanged(AppView value)
    {
        NotifyHistoryChanged();
        if (!SuppressWorkspaceTabSync)
            ScheduleSaveWorkspaceSession();
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
