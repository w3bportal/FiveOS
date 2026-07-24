// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using FiveOS.Services;
using FiveOS.ViewModels;
using Wpf.Ui.Controls;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfSeparator = System.Windows.Controls.Separator;
using WpfContextMenu = System.Windows.Controls.ContextMenu;

namespace FiveOS.Views;

public partial class MainWindow : FluentWindow
{
    private static readonly string[] SupportedExtensions =
    {
        ".obj", ".glb", ".gltf", ".fbx", ".dae", ".ply", ".stl",
    };

    private readonly MainViewModel _vm = new();
    /// <summary>Cached result of the background update poll so the
    /// footer badge can re-run <see cref="OfferUpdateAsync"/> on click
    /// without doing the HTTP round-trip a second time.</summary>
    private Services.UpdateChecker.CheckResult? _pendingUpdate;
    // Splash-time update flow: _revealed flips when the window is on-screen
    // (ShowStartupDialogs); _updateOffered guards the one-shot themed offer so
    // the reveal path and a late-arriving check can't double-prompt;
    // _updateInstalling suppresses the Welcome screen when the user chose to
    // update (the app is about to restart).
    private bool _revealed;
    private bool _updateOffered;
    private bool _updateInstalling;
    private bool _viewerReady;
    private string? _viewerSessionDir;  // copy of viewer assets + currently-loaded model files
    private string? _pendingModelUrl;   // queued load if viewer wasn't ready yet
    private Task? _webViewInit;         // memoized lazy-init; stays null until first model load
    // Staged copies of images picked via Add Missing Textures / layer Change
    // textures. Paths in PartDiffuseOverrides point here so convert still
    // finds them after the picker closes.
    private string? _partTexOverrideDir;
    // PNGs extracted from the loaded model for the sidebar Textures list.
    private string? _modelTexPreviewDir;
    private int _modelTexRefreshGen;
    private bool _suppressTextureSelection;

    // Latest transform values posted from the three.js gizmo. Applied to
    // the model on Convert so what you see in the preview is what gets baked.
    private (double X, double Y, double Z) _gizmoPos = (0, 0, 0);
    private (double X, double Y, double Z) _gizmoRot = (0, 0, 0);
    // Per-axis scale. Identity = (1,1,1). Non-uniform values stretch the
    // prop on each axis — the three.js gizmo emits per-axis when the
    // "Uniform scale" lock is off, and we plumb all three through to the
    // converter so the export matches what the user saw in preview.
    private (double X, double Y, double Z) _gizmoScale = (1.0, 1.0, 1.0);

    // Set true while we're applying a viewer-originated transform message
    // to the VM, so the VM PropertyChanged handler doesn't immediately
    // echo the same values back into the viewer (would cause an infinite
    // ping-pong on every gizmo drag).
    private bool _suppressTransformPush;

    // Live "still working..." elapsed-timer overlay for CodeWalker.Core's
    // FbxConverter step, which can run silently for many minutes on large
    // meshes (no intra-step progress). Re-armed every time the engine
    // emits a [N/M] step header.
    private DispatcherTimer? _engineStepTimer;
    private DateTime _engineStepStartUtc;
    private string _engineStepBaseStatus = "";
    // Cancels an in-flight convert (single or split-layer). EngineRunner turns
    // a cancel into a Kill(entireProcessTree) of the ydr-writer engine so no
    // orphaned process is left running in the temp workdir. Null when idle.
    private System.Threading.CancellationTokenSource? _convertCts;
    // Matches a step header anywhere on the line. The engine prefixes every
    // line with "[ydr-writer]" before the [N/M] tag, so we can't anchor to ^.
    private static readonly Regex EngineStepHeader = new(@"\[\d+/\d+\]", RegexOptions.Compiled);
    // Strips the engine's internal "[ydr-writer]" tag (with optional FATAL/WARN
    // qualifier) from forwarded log lines so the UI never leaks the binary
    // name — users see FiveOS branding, not the embedded tool.
    private static readonly Regex EngineSelfTag = new(@"^\s*\[ydr-writer\]\s*", RegexOptions.Compiled);

    /// <summary>
    /// Fires when the WebView2 environment is created and the host control
    /// is bound to it. Used by the splash screen as a real-progress
    /// milestone (~70%).
    /// </summary>
    public event EventHandler? WebView2Ready;

    /// <summary>
    /// Fires when viewer.html has booted three.js and posted its first
    /// "ready" message. Used by the splash screen as the final milestone
    /// (~100%) so it can fade out.
    /// </summary>
    public event EventHandler? ViewerReady;

    public MainWindow()
    {
        InitializeComponent();

        // Locked charcoal IDE dark. Accent comes from Settings (default blue).
        // Pass None for backdrop (flat panels, not Mica) and skip system-accent
        // update so Windows personalization colors never leak in.
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
            Wpf.Ui.Appearance.ApplicationTheme.Dark,
            Wpf.Ui.Controls.WindowBackdropType.None,
            updateAccent: false);
        // Re-apply after theme manager so WPF-UI dark resources don't overwrite
        // the accent pinned at App startup.
        ThemeAccent.ApplyFromSettings();
        ThemeAccent.Changed += OnThemeAccentChanged;

        // Parked-ghost lifecycle: a queue row leaving the session (deleted,
        // converted at export, cleared) takes its viewport ghost with it.
        Services.PropPackSession.Current.ConvertQueue.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    if (Viewport?.CoreWebView2 == null) return;
                    try
                    {
                        await Viewport.CoreWebView2.ExecuteScriptAsync(
                            "window.clearParkedModels && window.clearParkedModels()");
                    }
                    catch { /* viewer may be mid-navigate */ }
                });
                return;
            }
            if (args.OldItems is null) return;
            foreach (Services.PropPackQueueItem it in args.OldItems)
                _ = Dispatcher.InvokeAsync(() => RemoveParkedInstanceAsync(it.AssetName));
        };

        DataContext = _vm;
        RefreshWindowTitle();
        ApplySavedPropsSidebarWidth();
        ApplyPropAnimTimelineHeight();
        if (_vm.LayersPanelWidth < 280)
            _vm.LayersPanelWidth = 340;
        _vm.IsLayersPanelCollapsed = false;
        _vm.CommitLayersPanelWidth();
        _vm.WorkspaceDocs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WorkspaceDocumentSet.ActiveDocument)
                or nameof(WorkspaceDocumentSet.ActiveDocumentId))
                RefreshWindowTitle();
        };
        _vm.WorkspaceDocs.Documents.CollectionChanged += (_, _) => RefreshWindowTitle();
        // Mirror Emotes document tabs into the chrome tab strip.
        AttachEmoteWorkspaceTabSync();
        // Universal Undo/Redo: chrome buttons + Ctrl+Z/Y route through
        // MainViewModel, which calls into Emotes when that section is active.
        _vm.ExternalCanUndo = () => EmotesWorkspace.CanUndoPose;
        _vm.ExternalCanRedo = () => EmotesWorkspace.CanRedoPose;
        _vm.ExternalUndo = () => EmotesWorkspace.RunUndoPose();
        _vm.ExternalRedo = () => EmotesWorkspace.RunRedoPose();
        // Close an emote document when its chrome tab is navigated to another
        // section, so it can't resurrect as a duplicate tab later.
        _vm.DetachEmoteDocument = id => EmotesWorkspace.DetachEmoteDocumentById(id);
        EmotesWorkspace.HistoryChanged += (_, _) => _vm.NotifyHistoryChanged();
        // WebView2 is initialized lazily on first model load (see EnsureWebViewAsync)
        // so the Edge process tree (manager + GPU + storage utility, ~40 MB)
        // doesn't spawn for users who never open the 3D viewer this session.

        // Pack dock / splitter layout changes often skip window.resize inside
        // the viewer — poke it when the host control's size changes.
        ViewportRoot.SizeChanged += (_, _) => _ = NudgeViewerResizeAsync();

        // Reference ped: react to VM-side changes by pushing into the viewer.
        // Toggle uses cheap visibility-only path; path swap re-stages.
        _vm.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ShowReferencePed))
                await SetReferenceVisibleAsync(_vm.ShowReferencePed);
            else if (e.PropertyName == nameof(MainViewModel.ReferenceModelPath))
                await LoadReferenceAsync();
            else if (e.PropertyName == nameof(MainViewModel.ExportMode))
                ClearLoadedModel();
            else if (e.PropertyName == nameof(MainViewModel.GlassOpacity))
                await SetGlassAppearanceAsync(_vm.GlassOpacity);
            else if (e.PropertyName == nameof(MainViewModel.IsPackMode)
                     || e.PropertyName == nameof(MainViewModel.IsLayersPanelCollapsed)
                     || e.PropertyName == nameof(MainViewModel.IsLayersPanelOpen))
            {
                _ = NudgeViewerResizeAsync();
            }
            else if (IsTransformProperty(e.PropertyName) && !_suppressTransformPush)
                await PushTransformToViewerAsync();
            else if (e.PropertyName == nameof(MainViewModel.ActiveView)
                     && _vm.ActiveView == AppView.Emotes
                     && _vm.OpenEmotesAsAnimLibrary)
            {
                _vm.OpenEmotesAsAnimLibrary = false;
                EmotesWorkspace.OpenAnimLibraryMode();
            }
            else if (e.PropertyName == nameof(MainViewModel.IsAnimatedPropsView))
                ApplyPropAnimTimelineHeight();
        };

        // The update check now runs on the boot splash ("Checking for
        // updates...") — see App.RunSplashUpdateCheckAsync. Its result is
        // handed here via SetPendingUpdate, which lights the footer badge and
        // arms the themed offer for ShowStartupDialogs() to surface once the
        // window is revealed. Failures are silent (Help → Check for updates
        // still surfaces the error on demand).

        // First-run: the experience-level picker + Welcome screen are shown
        // from App's reveal (ShowStartupDialogs), AFTER the splash hands off and
        // the window is on-screen — NOT on Loaded, which fires while the window
        // is still parked off-screen (both dialogs are CenterOwner and would
        // land off-screen).
        // Guard against a spurious close during startup. Some accessibility /
        // UI-automation clients invoke the main window's TitleBar close button
        // when a new dialog (the Welcome popup) appears — verified via stack
        // trace: ButtonAutomationPeer.Invoke → TitleBar.CloseWindow → WM_CLOSE —
        // which would take the whole app down.
        //
        // Scope the block to EXACTLY that window: while the modal Welcome dialog
        // is open. A real user can't reach the main window's X while a modal
        // child is up, so this never cancels an intentional close. (The previous
        // version ALSO blocked for the first 25s after launch — a blanket lockout
        // that silently ate real X / Alt+F4 / File→Exit closes and drove users to
        // force-kill from Task Manager, which orphaned the WebView2 subprocess
        // tree because OnClosed never ran. Removed.)
        Closing += (_, ev) =>
        {
            if (_welcome != null)
                ev.Cancel = true;
            else
                _vm.SaveWorkspaceSessionNow();
        };

        // Dev hook: FIVEOS_DEV_AUTOIMPORT=<path> opens the Emotes workspace
        // and imports that clip (retarget → timeline keyframes) on startup,
        // so the result can be inspected without hand-driving the UI.
        Loaded += MaybeDevAutoImport;

        // Preload the Emotes viewer during startup so it opens INSTANTLY later.
        // A collapsed WebView2 defers building the three.js rig until the tab is
        // actually shown (measured: ~2.5s stall on first open even after the
        // process is warm). So instead we make Emotes the ACTIVE view for a moment
        // while App still has the window parked OFF-SCREEN for the splash — nothing
        // is visible, but the viewer is composited so it builds + paints the rig —
        // then the instant it reports ready we switch back to the real default view
        // (3D Model), leaving the fully-warm viewer collapsed and instant to open.
        // A cap restores the view even if the viewer never signals ready.
        Loaded += (_, _) =>
        {
            if (_vm.ActiveView == AppView.Emotes) return;   // user/dev already there
            if (!_vm.LevelStandardPlus) return;             // Emotes locked → it bounces to Props anyway
            _warmRestoreView = _vm.ActiveView;
            EmotesWorkspace.ViewerFirstReady += OnEmotesWarmReady;
            _warmCap = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _warmCap.Tick += (_, _) => OnEmotesWarmReady();
            _warmCap.Start();
            // Suppress chrome-tab sync so warming doesn't leave an Emotes tab
            // the user never opened this session.
            _vm.SuppressWorkspaceTabSync = true;
            _vm.ActiveView = AppView.Emotes;   // realize + build the rig off-screen
        };
    }

    private AppView _warmRestoreView = AppView.Props;
    private System.Windows.Threading.DispatcherTimer? _warmCap;
    private bool _warmDone;

    /// <summary>Emotes viewer finished warming during startup (or the cap fired):
    /// hand the view back to the real default, leaving the warm viewer collapsed.
    /// Won't override a view the user navigated to themselves in the meantime.</summary>
    private void OnEmotesWarmReady()
    {
        if (_warmDone) return;
        _warmDone = true;
        _warmCap?.Stop();
        try { EmotesWorkspace.ViewerFirstReady -= OnEmotesWarmReady; } catch { }
        if (_vm.ActiveView == AppView.Emotes)
            _vm.ActiveView = _warmRestoreView;
        _vm.SuppressWorkspaceTabSync = false;
        // Do NOT SyncWorkspaceTabForActiveView here — session restore already
        // placed the correct chrome tabs; EnsureKind would spawn extras.
    }

    private async void MaybeDevAutoImport(object? sender, RoutedEventArgs e)
    {
        Loaded -= MaybeDevAutoImport;
        // Dev hook: FIVEOS_DEV_VIEW=<OpenView target> navigates to any view or
        // mode card on startup ("Optimize", "TxAdmin", "Emotes", ...) so a tab
        // can be screenshotted without hand-driving the UI. No-op when unset.
        var devView = System.Environment.GetEnvironmentVariable("FIVEOS_DEV_VIEW");
        if (!string.IsNullOrWhiteSpace(devView))
            _vm.OpenViewCommand.Execute(devView);
        // Dev hook: FIVEOS_DEV_SETTINGS=1 opens the Settings modal on startup so it
        // can be screenshotted without hand-driving the UI.
        if (System.Environment.GetEnvironmentVariable("FIVEOS_DEV_SETTINGS") == "1")
        {
            var st = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            st.Tick += (_, _) => { st.Stop(); NavigateToSettings(); };
            st.Start();
        }
        var import = System.Environment.GetEnvironmentVariable("FIVEOS_DEV_AUTOIMPORT");
        if (devView != null || import != null)
            Services.FosLogger.Info("dev", $"hooks: view={devView ?? "-"} import={import ?? "-"}");
        // (FIVEOS_DEV_SOURCEFBX retired with the standalone Anim → Emote tab —
        // resurrect from git history if raw-source comparison is needed again.)

        if (string.IsNullOrWhiteSpace(import) || !System.IO.File.Exists(import)) return;
        _vm.OpenViewCommand.Execute("Emotes");
        // Dev hook: FIVEOS_DEV_MODE=0|1|2 preselects the playback/movement mode
        // (in place / upper body / walkable) before the import lands.
        var mode = System.Environment.GetEnvironmentVariable("FIVEOS_DEV_MODE");
        if (int.TryParse(mode, out var mi)) EmotesWorkspace.DevSetMovement(mi);
        await EmotesWorkspace.DevImportAnimationAsync(import);
    }

    /// <summary>First-run dialogs, shown from App's reveal AFTER the splash
    /// hands off and the window is on-screen (so the CenterOwner dialogs don't
    /// land on the off-screen window): first any update the splash-time check
    /// turned up, then the Welcome screen.</summary>
    public void ShowStartupDialogs()
    {
        _revealed = true;
        _ = ShowStartupDialogsAsync();
    }

    private async Task ShowStartupDialogsAsync()
    {
        // Offer the update (if the splash check found one) BEFORE Welcome: if
        // the user updates, the app restarts and Welcome is moot; if they
        // defer, Welcome follows as usual.
        await MaybeOfferPendingUpdateAsync();
        if (_updateInstalling) return;   // updating → app is restarting; skip Welcome
        ShowWelcomeIfEnabled();
    }

    // ── Welcome splash (Blender-style startup screen) ──────────────────

    /// <summary>Show the Welcome splash if the user hasn't disabled it and no
    /// dev-automation hook is driving the app. Called from App startup once the
    /// main window is revealed (App.xaml.cs doFinish), so it floats over the
    /// already-visible 3D Model workspace rather than a hidden window.</summary>
    public void ShowWelcomeIfEnabled()
    {
        if (System.Environment.GetEnvironmentVariable("FIVEOS_DEV_AUTOIMPORT") != null
            || System.Environment.GetEnvironmentVariable("FIVEOS_DEV_VIEW") != null) return;
        if (!Services.UserSettings.LoadShowWelcomeOnStartup()) return;
        ShowWelcome();
    }

    private WelcomeWindow? _welcome;

    private void ShowWelcome()
    {
        try
        {
            if (_welcome != null) { _welcome.Activate(); return; }
            var w = new WelcomeWindow { Owner = this, DataContext = _vm };
            w.Closed += (_, _) => { if (ReferenceEquals(_welcome, w)) _welcome = null; };
            _welcome = w;
            // Modal: disables the main window while open, so the spurious
            // automation close can't reach it (the Closing guard is a second
            // line of defense). Shown from a 200ms timer AFTER the boot splash
            // closed, so there's no re-entrancy with the splash handoff.
            w.ShowDialog();
        }
        catch (Exception ex) { Services.FosLogger.Warn("welcome", "welcome screen failed", ex); _welcome = null; }
    }

    private void OnShowWelcome(object sender, RoutedEventArgs e) => ShowWelcome();

    /// <summary>Welcome-screen tile → navigate to a tool, reusing the same
    /// OpenView funnel the rail uses (tags: 3D / Optimize / Rpf / Emotes).</summary>
    public void NavigateFromWelcome(string tag) => _vm.OpenViewCommand.Execute(tag);

    /// <summary>Welcome-screen recent-file click → open the model in the 3D tool.</summary>
    public void OpenModelFromWelcome(string path)
    {
        _vm.ActiveView = FiveOS.ViewModels.AppView.Props;
        TryLoad(path);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Per-window UIPI unblock so Explorer (Medium integrity) can drag
        // files AND folders onto us when FiveOS is launched as administrator.
        // Complements the process-wide call in App.OnStartup; no-op otherwise.
        App.EnableDragDropForWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        try { ThemeAccent.Changed -= OnThemeAccentChanged; } catch { /* */ }

        // Tear down every WebView2 host so Edge process trees + session dirs
        // don't leak across launches (~40 MB+ each).
        try { EmotesWorkspace?.Teardown(); } catch { /* */ }
        try { FindVehiclesView()?.Teardown(); } catch { /* */ }
        try { FindOptimizeView()?.Teardown(); } catch { /* */ }

        // Dispose the main props WebView2 and delete this session's viewer
        // temp dir — staged models + textures can be hundreds of MB.
        try { Viewport?.Dispose(); } catch { /* already gone */ }
        if (!string.IsNullOrEmpty(_viewerSessionDir))
        {
            try { if (Directory.Exists(_viewerSessionDir)) Directory.Delete(_viewerSessionDir, true); }
            catch { /* locked/best-effort — CacheService.Clear sweeps leftovers */ }
        }

        try { CacheService.PruneStaleViewerCaches(); } catch { /* best-effort */ }

        base.OnClosed(e);
    }

    private void OnThemeAccentChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(PushAccentToViewer);

    /// <summary>
    /// Hand the splash-time update check's result to the window: light the
    /// footer badge and arm the themed offer. Safe to call from any thread
    /// and at any point — if the check outlived the splash (slow link), this
    /// still lights the badge and, since the window is already revealed by
    /// then, offers immediately. No-ops when there's nothing newer.
    /// Called from <c>App.RunSplashUpdateCheckAsync</c>.
    /// </summary>
    public void SetPendingUpdate(Services.UpdateChecker.CheckResult result)
    {
        if (result.Status != Services.UpdateChecker.Status.UpdateAvailable || result.Latest == null)
            return;
        _pendingUpdate = result;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            _vm.UpdateBadgeLabel =
                $"Update v{result.Latest.Major}.{result.Latest.Minor}.{result.Latest.Build} available";
            _vm.IsUpdateAvailable = true;
            // If the window is already on-screen (the check came back after the
            // splash handed off), offer now. Otherwise ShowStartupDialogs()
            // offers it once the window is revealed.
            if (_revealed) await MaybeOfferPendingUpdateAsync();
        });
    }

    /// <summary>Offer the pending update exactly once, via the Fluent-themed
    /// dialog. Guarded so the reveal path and a late-arriving check can't
    /// double-prompt. No-ops when nothing is pending or the user already
    /// skipped this version (the footer badge stays for a change of mind).</summary>
    private async Task MaybeOfferPendingUpdateAsync()
    {
        if (_updateOffered) return;
        // Manual-only mode: never auto-prompt; badge / Help → Check still work.
        if (!Services.UserSettings.LoadGlobalUpdate()) return;
        if (_pendingUpdate is not
            { Status: Services.UpdateChecker.Status.UpdateAvailable, Latest: not null } result)
            return;
        var skipped = Services.UserSettings.LoadSkippedUpdateVersion();
        if (string.Equals(skipped, result.LatestTag, StringComparison.OrdinalIgnoreCase))
            return;
        _updateOffered = true;
        await OfferUpdateAsync(result);
    }

    private async void OnUpdateBadgeClick(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null) return;
        await OfferUpdateAsync(_pendingUpdate);
    }

    private static bool IsTransformProperty(string? name) => name is
        nameof(MainViewModel.TransformPosX) or nameof(MainViewModel.TransformPosY) or nameof(MainViewModel.TransformPosZ) or
        nameof(MainViewModel.TransformRotX) or nameof(MainViewModel.TransformRotY) or nameof(MainViewModel.TransformRotZ) or
        nameof(MainViewModel.TransformScaleX) or nameof(MainViewModel.TransformScaleY) or nameof(MainViewModel.TransformScaleZ) or
        nameof(MainViewModel.TransformScale);

    /// <summary>Push the VM's current sidebar Transform values into the
    /// three.js viewer. Best-effort — silently no-ops if the viewer
    /// hasn't booted yet (the next gizmo interaction or model load will
    /// re-sync). Scale is sent as a [x,y,z] triple so non-uniform
    /// stretches survive the round-trip.</summary>
    private async Task PushTransformToViewerAsync()
    {
        if (Viewport?.CoreWebView2 == null || !_viewerReady) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var payload =
            "{" +
            $"\"pos\":[{_vm.TransformPosX.ToString(inv)},{_vm.TransformPosY.ToString(inv)},{_vm.TransformPosZ.ToString(inv)}]," +
            $"\"rot\":[{_vm.TransformRotX.ToString(inv)},{_vm.TransformRotY.ToString(inv)},{_vm.TransformRotZ.ToString(inv)}]," +
            $"\"scale\":[{_vm.TransformScaleX.ToString(inv)},{_vm.TransformScaleY.ToString(inv)},{_vm.TransformScaleZ.ToString(inv)}]" +
            "}";
        try { await Viewport.CoreWebView2.ExecuteScriptAsync($"window.applyTransform && window.applyTransform({payload})"); }
        catch { /* viewer not ready — gizmo or next load will re-sync */ }

        // Mirror into the legacy gizmo fields so OnConvert keeps baking
        // the right values.
        _gizmoPos = (_vm.TransformPosX, _vm.TransformPosY, _vm.TransformPosZ);
        _gizmoRot = (_vm.TransformRotX, _vm.TransformRotY, _vm.TransformRotZ);
        if (_vm.TransformScaleX > 0 && _vm.TransformScaleY > 0 && _vm.TransformScaleZ > 0)
            _gizmoScale = (_vm.TransformScaleX, _vm.TransformScaleY, _vm.TransformScaleZ);
    }

    /// <summary>
    /// Memoized lazy initializer for WebView2. Multiple concurrent callers
    /// share the same Task and only the first triggers the real init.
    /// </summary>
    private Task EnsureWebViewAsync() => _webViewInit ??= InitWebViewAsync();

    // Title shown in the FluentWindow chrome and taskbar. Pulls Version from
    // the assembly so it stays in lockstep with the <Version> in the csproj.
    // Drop " BETA" here when cutting a stable release.
    private const bool IsBeta = false;

    private static string AppVersionString()
    {
        var asm = typeof(MainWindow).Assembly;
        var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
        if (info.Length > 0)
        {
            var raw = ((System.Reflection.AssemblyInformationalVersionAttribute)info[0]).InformationalVersion;
            var plus = raw.IndexOf('+');
            return plus > 0 ? raw[..plus] : raw;
        }
        var v = asm.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>C4D-style chrome title: <c>FiveOS vX.Y.Z - [tab] - Main</c>.</summary>
    private void RefreshWindowTitle()
    {
        var version = AppVersionString();
        var tab = _vm.WorkspaceDocs.ActiveDocument?.DisplayTitle?.TrimEnd(' ', '*', '•');
        if (string.IsNullOrWhiteSpace(tab)) tab = "Main";
        var beta = IsBeta ? " BETA" : "";
        Title = $"FiveOS {version}{beta} - [{tab}] - Main";
    }

    private static string BuildVersionTitle()
    {
        // Kept for callers that want the slogan form; chrome uses RefreshWindowTitle.
        const string slogan = "All in One FiveM Modding Tool.";
        var version = AppVersionString();
        return IsBeta ? $"FiveOS - {slogan} - v{version} BETA" : $"FiveOS - {slogan} - v{version}";
    }

    // Scroll-to-scrub on the TRANSFORM NumberBoxes. Wheel up adds SmallChange,
    // wheel down subtracts it; Shift swaps in LargeChange for coarser steps.
    // Marking the event handled stops the parent ScrollViewer from also
    // scrolling, which would feel awful while editing a value.
    private void TransformNumberBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not NumberBox box) return;

        var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? box.LargeChange : box.SmallChange;
        var current = box.Value ?? 0;
        var next = current + (e.Delta > 0 ? step : -step);

        if (box.Maximum is double max && next > max) next = max;
        if (box.Minimum is double min && next < min) next = min;

        // Round to the box's display precision so float drift doesn't leave
        // trailing 0.99999... after a few wheel ticks.
        var decimals = Math.Max(0, Math.Min(15, box.MaxDecimalPlaces));
        next = Math.Round(next, decimals);

        box.Value = next;
        e.Handled = true;
    }

    // ─────────────── WebView2 init ───────────────

    private async Task InitWebViewAsync()
    {
        try
        {
            var userDataDir = Path.Combine(Path.GetTempPath(), "FiveOS", "WebView2");
            Directory.CreateDirectory(userDataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            // Dark viewport so there's no WHITE flash before viewer.html paints
            // (a native WebView2 defaults to white, which reads as a broken
            // "blank loading screen" on the dark app during the first seconds).
            try { Viewport.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x1E, 0x1E, 0x1E); } catch { }
            await Viewport.EnsureCoreWebView2Async(env);
            WebView2Ready?.Invoke(this, EventArgs.Empty);

            // Enable F12 dev tools (only in debug builds — strip for shipping if desired).
            Viewport.CoreWebView2.Settings.AreDevToolsEnabled = true;
            Viewport.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

            // Use a per-session temp dir as the WebView2 root. We copy both
            // the viewer html/js bundle AND the user's loaded model into
            // here, so everything is served from a single virtual host
            // (same origin, no CORS pain, no WebResourceRequested fiddling).
            _viewerSessionDir = Path.Combine(Path.GetTempPath(), "FiveOS", "Viewer-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_viewerSessionDir);

            var bundledViewerDir = FiveOS.Services.RuntimeAssets.ViewerDir;
            CopyDirectory(bundledViewerDir, _viewerSessionDir);

            Viewport.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "viewer.local", _viewerSessionDir, CoreWebView2HostResourceAccessKind.Allow);
            WebViewDialogs.Theme(Viewport.CoreWebView2);

            // Listen for messages posted from the viewer's JS (ready, loaded, error).
            Viewport.CoreWebView2.WebMessageReceived += OnViewerMessage;

            Viewport.Source = new Uri("https://viewer.local/viewer.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _vm.StatusText =
                "Microsoft Edge WebView2 Runtime is not installed. " +
                "Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and re-launch.";
        }
        catch (Exception ex) when (HResultOf(ex) == unchecked((int)0x800700AA))
        {
            // ERROR_BUSY: another process holds a lock on the user-data
            // folder. Belt-and-braces — App.xaml.cs already enforces a
            // single-instance mutex, so this only fires for orphan
            // msedgewebview2 helpers left behind by a crashed/detached run.
            _vm.StatusText =
                "Another FiveOS (or its WebView2 helpers) is still running. " +
                "Sign out / close it, or end stray msedgewebview2 processes, then re-launch.";
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"WebView2 failed to initialize: {ex.Message}";
        }
    }

    private static int HResultOf(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is COMException com) return com.HResult;
        return ex.HResult;
    }

    private void PushAccentToViewer()
    {
        if (Viewport?.CoreWebView2 is null) return;
        var c = ThemeAccent.Current;
        if (System.Windows.Application.Current?.TryFindResource("SystemAccentColorBrush")
            is System.Windows.Media.SolidColorBrush brush)
            c = brush.Color;
        var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        _ = Viewport.CoreWebView2.ExecuteScriptAsync(
            $"(function(h){{var r=document.documentElement.style;r.setProperty('--pose-accent',h);r.setProperty('--vscode-accent',h);}})('{hex}')");
    }

    private void OnViewerMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            // Messages are JSON like {"kind":"ready"} / {"kind":"loaded","verts":N,"tris":N} /
            // {"kind":"transform","pos":[x,y,z],"rot":[x,y,z],"scale":s}.
            var json = e.WebMessageAsJson;
            if (json.Contains("\"ready\""))
            {
                var first = !_viewerReady;
                _viewerReady = true;
                if (first)
                    Dispatcher.Invoke(() =>
                    {
                        ViewerReady?.Invoke(this, EventArgs.Empty);
                        // Mirror the host app's accent into the viewer so
                        // the debug overlay (backtick) and any other
                        // accent-coloured HUD reads as part of the app
                        // theme rather than a stale amber default.
                        PushAccentToViewer();
                        // Push the reference ped into the viewer the moment
                        // it's ready so it's already there when the user's
                        // model loads. Best-effort — failures don't block.
                        _ = LoadReferenceAsync();
                    });
                if (_pendingModelUrl != null)
                {
                    var url = _pendingModelUrl;
                    _pendingModelUrl = null;
                    _ = LoadInViewerAsync(url);
                }
            }
            else if (json.Contains("\"loaded\""))
            {
                // Rig / reference-ped loads (the 1.82 m scale ped, pose rigs)
                // also post kind:'loaded' — but with verts:0 / tris:0 and a
                // source:'gta-...' tag. They are NOT the user's model: letting
                // them through clobbers Verts/Tris (banner says "0 tris"),
                // resets the user's transform, and wipes undo history —
                // whichever load finishes last wins the race.
                if (json.Contains("\"source\":\"gta-", StringComparison.Ordinal))
                    return;

                Dispatcher.Invoke(() =>
                {
                    // Live-preview reload path: the viewer hot-swapped to the
                    // next decimate output. Keep the user's placement/scale
                    // (the viewer resets the swapped-in model to identity, so
                    // re-push the current gizmo state), keep undo history,
                    // don't re-run auto-fit or the health check, and don't
                    // overwrite the "Preview — ..." status.
                    if (_inPreviewReload)
                    {
                        _inPreviewReload = false;
                        var (pVerts, pTris) = ExtractVertsTris(json);
                        _vm.Verts = (int)Math.Min(pVerts, int.MaxValue);
                        _vm.Tris = (int)Math.Min(pTris, int.MaxValue);
                        _vm.IsModelLoading = false;
                        _ = PushTransformToViewerAsync();
                        _vm.StatusText = $"Preview · {pTris:N0} tris (drag slider to adjust)";
                        return;
                    }

                    // Reset gizmo state for the new model.
                    _gizmoPos = (0, 0, 0); _gizmoRot = (0, 0, 0); _gizmoScale = (1.0, 1.0, 1.0);

                    // Mirror the reset into the sidebar Transform inputs.
                    // Suppressed so we don't immediately push a redundant
                    // identity transform back into a freshly-loaded model.
                    _suppressTransformPush = true;
                    try
                    {
                        _vm.TransformPosX = 0; _vm.TransformPosY = 0; _vm.TransformPosZ = 0;
                        _vm.TransformRotX = 0; _vm.TransformRotY = 0; _vm.TransformRotZ = 0;
                        _vm.TransformScaleX = 1.0; _vm.TransformScaleY = 1.0; _vm.TransformScaleZ = 1.0;
                    }
                    finally { _suppressTransformPush = false; }

                    // Clear undo/redo so Ctrl+Z after a model load can't
                    // restore the previous model's transform onto the new
                    // one. Has to run AFTER the reset writes above so the
                    // new baseline snapshot reflects identity values.
                    _vm.ResetUndoHistory();

                    // Mirror the geometry counts onto the VM so the sidebar's
                    // GEOMETRY card renders them. Status bar gets a short summary.
                    var (verts, tris) = ExtractVertsTris(json);
                    _vm.Verts = (int)Math.Min(verts, int.MaxValue);
                    _vm.Tris = (int)Math.Min(tris, int.MaxValue);
                    _vm.IsModelLoading = false;

                    // Auto-fit absurdly-scaled models. AI generators (Meshy,
                    // etc.) export at ~10-100x GTA metres, so a phone lands
                    // 10 m+ tall in-game. Normalize the largest dimension to a
                    // sane prop size (~1 m). Applied VISIBLY through the Scale
                    // field (not silently) so the user can tweak or reset it,
                    // and only when the model is clearly out of range so it
                    // never touches correctly-sized props / map objects.
                    string autoFitNote = "";
                    var (sx, sy, sz) = ExtractSize(json);
                    double maxDim = Math.Max(sx, Math.Max(sy, sz));
                    if (maxDim > 4.0 || (maxDim > 1e-4 && maxDim < 0.05))
                    {
                        const double targetMetres = 1.0;
                        double fit = targetMetres / maxDim;
                        _vm.TransformScaleX = fit;
                        _vm.TransformScaleY = fit;
                        _vm.TransformScaleZ = fit;
                        _gizmoScale = (fit, fit, fit);
                        _vm.ResetUndoHistory();   // fold the auto-fit into the baseline
                        autoFitNote = $" · auto-scaled to ~{targetMetres:0.#} m (was {maxDim:0.#} m) — adjust Scale in the sidebar";
                    }

                    if (_vm.SourcePath != null)
                        _vm.StatusText = $"✓ {Path.GetFileName(_vm.SourcePath)} · {verts:N0} verts · {tris:N0} tris{autoFitNote}";

                    // Decide whether the optimization-health banner should
                    // appear, based on tri count + source file size against
                    // the FiveM thresholds. Banner is purely informational —
                    // user can dismiss and convert anyway.
                    EvaluateOptimizationHealth(tris);
                });
            }
            else if (json.Contains("parts-sync"))
            {
                // Incremental parts update (e.g. after the user split a mesh
                // out with "Separate"). MERGE — keep every existing row's
                // rename / material / visibility / deleted state — instead of
                // the full reset a fresh load does. Note: this branch MUST sit
                // before the "parts" one, since a parts-sync payload also
                // carries a "parts":[...] array.
                Dispatcher.Invoke(async () =>
                {
                    SyncModelPartsFromMessage(json);
                    await SetGlassAppearanceAsync(_vm.GlassOpacity);
                });
            }
            else if (json.Contains("\"parts\""))
            {
                Dispatcher.Invoke(async () =>
                {
                    UpdateModelPartsFromMessage(json);
                    // Sync the new model's glass to the current Appearance slider
                    // (the viewer defaults to 0.6 per load until told otherwise).
                    await SetGlassAppearanceAsync(_vm.GlassOpacity);
                    var partNames = _vm.ModelParts.Select(p => p.OriginalName).ToList();
                    _vm.EnsureTextureVariants(partNames, _vm.PropName);

                    // Queued-layer preview: restore the transform the layer
                    // was dropped with (the load reset it to identity).
                    if (_pendingPreviewTransform is { } t)
                    {
                        _pendingPreviewTransform = null;
                        _vm.TransformScaleX = t.Sx; _vm.TransformScaleY = t.Sy; _vm.TransformScaleZ = t.Sz;
                        _vm.TransformRotX = t.Rx; _vm.TransformRotY = t.Ry; _vm.TransformRotZ = t.Rz;
                    }
                });
            }
            else if (json.Contains("\"transform\""))
            {
                // Pull the three numeric arrays out of the message.
                var (px, py, pz) = ExtractVec3(json, "\"pos\":");
                var (rx, ry, rz) = ExtractVec3(json, "\"rot\":");
                // Scale is sent as [x,y,z]; fall back to scalar form for
                // any older viewer.html that's still cached on disk.
                var (sxRaw, syRaw, szRaw) = ExtractVec3(json, "\"scale\":");
                double sx = sxRaw, sy = syRaw, sz = szRaw;
                if (sx <= 0 && sy <= 0 && sz <= 0)
                {
                    var legacy = ExtractScalar(json, "\"scale\":");
                    if (legacy > 0) { sx = sy = sz = legacy; }
                }
                _gizmoPos = (px, py, pz);
                _gizmoRot = (rx, ry, rz);
                if (sx > 0 && sy > 0 && sz > 0) _gizmoScale = (sx, sy, sz);

                // Mirror the gizmo state into the sidebar Transform fields
                // so the user sees live values as they drag. Suppressed
                // around the assignments so the VM PropertyChanged hook
                // doesn't echo us right back into the viewer.
                Dispatcher.Invoke(() =>
                {
                    _suppressTransformPush = true;
                    try
                    {
                        _vm.TransformPosX = px; _vm.TransformPosY = py; _vm.TransformPosZ = pz;
                        _vm.TransformRotX = rx; _vm.TransformRotY = ry; _vm.TransformRotZ = rz;
                        if (sx > 0) _vm.TransformScaleX = sx;
                        if (sy > 0) _vm.TransformScaleY = sy;
                        if (sz > 0) _vm.TransformScaleZ = sz;
                    }
                    finally { _suppressTransformPush = false; }
                });
            }
            else if (json.Contains("\"error\""))
            {
                Dispatcher.Invoke(() =>
                {
                    _vm.IsModelLoading = false;
                    _vm.StatusText = $"⚠ Preview error: {json}";
                    // A failed load during an optimize-preview hot-swap must
                    // not leave the flag armed — the next successful load
                    // would silently skip the normal post-load flow.
                    _inPreviewReload = false;
                    Services.FosLogger.Warn("viewer", $"model load error: {Truncate(json, 400)}");
                });
            }
        }
        catch { /* ignore malformed messages */ }
    }

    private static (double X, double Y, double Z) ExtractVec3(string json, string key)
    {
        var i = json.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return (0, 0, 0);
        var open = json.IndexOf('[', i);
        var close = json.IndexOf(']', open);
        if (open < 0 || close < 0) return (0, 0, 0);
        var parts = json[(open + 1)..close].Split(',');
        if (parts.Length != 3) return (0, 0, 0);
        double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var x);
        double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var y);
        double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var z);
        return (x, y, z);
    }

    private static double ExtractScalar(string json, string key)
    {
        var i = json.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return 0;
        var start = i + key.Length;
        var end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] is '.' or '-' or 'e' or 'E' or '+'))
            end++;
        double.TryParse(json[start..end], System.Globalization.CultureInfo.InvariantCulture, out var v);
        return v;
    }

    /// <summary>Model dimensions (file units == GTA metres) from the viewer's
    /// "loaded" message. Zeros when the viewer didn't report a size.</summary>
    private static (double X, double Y, double Z) ExtractSize(string json)
        => (ExtractScalar(json, "\"sizeX\":"),
            ExtractScalar(json, "\"sizeY\":"),
            ExtractScalar(json, "\"sizeZ\":"));

    private static (long Verts, long Tris) ExtractVertsTris(string json)
    {
        // Pluck "verts":N and "tris":N out of the message JSON without
        // pulling in a JSON dependency. Cheap because the message format
        // is fixed by viewer.html.
        long verts = 0, tris = 0;
        var i = json.IndexOf("\"verts\":", StringComparison.Ordinal);
        if (i >= 0) long.TryParse(NumberAt(json, i + 8), out verts);
        var j = json.IndexOf("\"tris\":", StringComparison.Ordinal);
        if (j >= 0) long.TryParse(NumberAt(json, j + 7), out tris);
        return (verts, tris);
    }

    private static string NumberAt(string s, int start)
    {
        var end = start;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '-')) end++;
        return s[start..end];
    }

    // ─────────────── File menu ───────────────

    private void OnFileOpen(object sender, RoutedEventArgs e)
    {
        // Drill into the 3D → Prop view so the picked model is visible.
        _vm.ActiveView = AppView.Props;
        BrowseForModel();
    }

    /// <summary>Open the batch-convert dialog. Optional
    /// <paramref name="initialPaths"/> seeds the queue, used by the
    /// drag-drop branch that detects multiple files dropped onto the
    /// main window.</summary>
    private void OnFileBatchConvert(object sender, RoutedEventArgs e)
        => OpenBatchConvertDialog(null);

    private void OpenBatchConvertDialog(System.Collections.Generic.IEnumerable<string>? initialPaths)
    {
        if (!EngineRunner.IsEngineAvailable())
        {
            AppDialog.Show(
                $"Conversion engine is missing.\n\nExpected: {EngineRunner.EnginePath}\n\nRe-install FiveOS — the batch converter requires the bundled engine.",
                "Engine not available",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning,
                this);
            return;
        }

        var dlg = new BatchConvertWindow { Owner = this };
        if (initialPaths != null) dlg.WithInitialFiles(initialPaths);
        var ok = dlg.ShowDialog();

        // When the dialog auto-finalised the pack, route through the
        // same success-screen overlay the single-prop convert flow
        // uses so the user gets the "Open folder / Convert another"
        // affordances they're already trained on.
        if (ok == true && dlg.ResultPackPath != null)
        {
            _vm.ActiveView = AppView.Props;
            _vm.ResultZipPath = dlg.ResultPackPath;
            _vm.ShowSuccessScreen = true;
            var modeLabel = dlg.ResultMode switch
            {
                EngineRunner.OutputMode.ServerShared   => "merged into server",
                EngineRunner.OutputMode.ServerPerAsset => "ready in server folder",
                _ => $"ready · {Path.GetFileName(dlg.ResultPackPath)}",
            };
            _vm.StatusText = $"✓ Batch pack {modeLabel}";
        }
    }

    private void OnFileAddTextures(object sender, RoutedEventArgs e)
    {
        // Drill into the unified Optimize view in Textures (YTD) mode.
        _vm.ActiveView = AppView.Optimize;
        _vm.OptimizeVm.Mode = ViewModels.OptimizeMode.Textures;
        var dlg = new OpenFileDialog
        {
            Title = "Add YTD files to optimize",
            Filter = "FiveM textures (*.ytd)|*.ytd|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true)
            _vm.OptimizeVm.AddPaths(dlg.FileNames);
    }

    private void OnFileOpenMapFolder(object sender, RoutedEventArgs e)
    {
        // Drill into the Optimize view in Textures mode and recurse the folder.
        _vm.ActiveView = AppView.Optimize;
        _vm.OptimizeVm.Mode = ViewModels.OptimizeMode.Textures;
        var dlg = new OpenFolderDialog
        {
            Title = "Select your map's resources folder (contains .ytd files)",
        };
        if (dlg.ShowDialog(this) == true)
            _vm.OptimizeVm.AddPaths(new[] { dlg.FolderName });
    }

    private void EnsureEmotesView()
    {
        _vm.ActiveView = AppView.Emotes;
        _vm.SyncWorkspaceTabForActiveView();
        SyncEmoteWorkspaceTabs();
    }

    private void OnFileEmoteOpenRig(object sender, RoutedEventArgs e)
    {
        EnsureEmotesView();
        EmotesWorkspace.RunOpenRiggedModel();
    }

    private void OnFileEmoteGtaMale(object sender, RoutedEventArgs e)
    {
        EnsureEmotesView();
        EmotesWorkspace.RunLoadGtaMale();
    }

    private void OnFileEmoteGtaFemale(object sender, RoutedEventArgs e)
    {
        EnsureEmotesView();
        EmotesWorkspace.RunLoadGtaFemale();
    }

    private void OnFileEmoteVideoToEmote(object sender, RoutedEventArgs e)
    {
        EnsureEmotesView();
        EmotesWorkspace.RunVideoToEmote();
    }

    private void OnSketchfabSidebarImport(object sender, RoutedEventArgs e)
    {
        var url = SketchfabUrlBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(url))
        {
            _vm.StatusText = "Paste a Sketchfab URL first.";
            SketchfabUrlBox.Focus();
            return;
        }
        OpenSketchfabImport(url);
    }

    private void OnSketchfabUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnSketchfabSidebarImport(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    /// <summary>
    /// Token-check + open the Sketchfab dialog (with the URL pre-filled if
    /// provided). Shared between the SOURCE sidebar's Sketchfab tab and any
    /// future entry points.
    /// </summary>
    private void OpenSketchfabImport(string? prefillUrl = null)
    {
        // Sketchfab import always lands in the 3D → Prop view.
        _vm.ActiveView = AppView.Props;

        if (FiveOS.Services.Net.LikelyOffline())
        { AppDialog.NoInternet("Importing a model from Sketchfab", this); return; }

        var token = SecretStore.Load(SketchfabClient.TokenKey);
        if (string.IsNullOrEmpty(token))
        {
            var prompt = AppDialog.Show(
                "No Sketchfab API token is saved. Open Settings to add one?",
                "Sketchfab token required",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Information,
                this);
            if (prompt != System.Windows.MessageBoxResult.OK) return;
            NavigateToSettings(SettingsView.FocusSection.Sketchfab);
            return;
        }

        var dlg = new SketchfabUrlDialog(token, prefillUrl) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.ResultModelPath))
        {
            // Hand off to the existing model-load pipeline. It stages the
            // file + sibling textures into the WebView2 session dir and
            // updates VM stats just like a drag-drop would.
            SketchfabUrlBox.Clear();
            TryLoad(dlg.ResultModelPath, dlg.ResultModelName);
        }
    }

    /// <summary>
    /// "Generate from image..." button in the source picker's AI tab. Opens
    /// the AI generator window (which hosts the full Image → 3D experience),
    /// and on close, loads whichever model the user picked into the viewer.
    /// </summary>
    private void OnOpenAiGenerator(object sender, RoutedEventArgs e)
    {
        var dlg = new AiGeneratorWindow(_vm.ImageTo3DVm) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.ResultModelPath))
        {
            TryLoad(dlg.ResultModelPath);
        }
    }

    /// <summary>
    /// Open Settings as a medium, themed modal dialog (it used to fill the
    /// whole content column). The <see cref="SettingsView"/> is built on
    /// demand — so its on-load cache scan runs only when the user opens
    /// Settings — and handed the MainViewModel as DataContext for the few
    /// bindings that need it (e.g. the addons list). <paramref name="onReady"/>
    /// runs once the window has rendered, for deep-links into a section.
    /// </summary>
    private void OpenSettingsModal(Action<SettingsView>? onReady = null)
    {
        Services.FosLogger.Info("settings", "settings modal opened");
        var view = new SettingsView { DataContext = _vm };
        var win = new SettingsWindow(view) { Owner = this };
        if (onReady != null)
            win.ContentRendered += (_, _) => onReady(view);
        win.ShowDialog();
    }

    private void OnSettingsOpen(object sender, RoutedEventArgs e) => OpenSettingsModal();

    private void OnSettingsClose(object sender, RoutedEventArgs e)
    {
        // Legacy inline-overlay back button — the overlay is retired in favour
        // of the modal, but the dormant XAML still binds this handler.
        _vm.IsSettingsOpen = false;
    }

    /// <summary>
    /// Open Settings focused on a specific section. Called by child views
    /// (MeshOptimizeView) that send the user to Settings when a key is missing.
    /// </summary>
    public void NavigateToSettings(SettingsView.FocusSection section = SettingsView.FocusSection.None)
        => OpenSettingsModal(view => view.Focus(section));

    /// <summary>
    /// Open Settings and scroll to the API-key card for the given AI
    /// provider id (meshy, tripo3d, rodin, replicate, stability). Used by
    /// ImageTo3DView when the user-selected provider has no key saved.
    /// </summary>
    public void NavigateToAiProviderSettings(string providerId)
        => OpenSettingsModal(view => view.FocusAiProvider(providerId));

    private void OnFileOpenOutput(object sender, RoutedEventArgs e)
    {
        // Both the 3D-to-Props zip and the Texture Optimize output land
        // under the user's Downloads folder; just open that.
        try
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            Process.Start(new ProcessStartInfo
            {
                FileName = downloads,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Couldn't open Downloads: {ex.Message}";
        }
    }

    private void OnFileExit(object sender, RoutedEventArgs e) => Close();

    // ── Context menus (page-specific) ────────────────────────────────

    private void OnMenuAssetsProps(object sender, RoutedEventArgs e)
        => _vm.OpenViewCommand.Execute("Props");

    private void OnMenuAssetsAnimated(object sender, RoutedEventArgs e)
        => _vm.OpenViewCommand.Execute("Animated");

    private void OnMenuOptimizeModeProps(object sender, RoutedEventArgs e)
        => _vm.OptimizeVm.Mode = OptimizeMode.Props;

    private void OnMenuOptimizeModeClothing(object sender, RoutedEventArgs e)
        => _vm.OptimizeVm.Mode = OptimizeMode.Clothing;

    private void OnMenuOptimizeModeTextures(object sender, RoutedEventArgs e)
        => _vm.OptimizeVm.Mode = OptimizeMode.Textures;

    private void OnMenuOptimizeModeEmbedded(object sender, RoutedEventArgs e)
        => _vm.OptimizeVm.Mode = OptimizeMode.EmbeddedTextures;

    private void OnMenuOptimizeModeTxAdmin(object sender, RoutedEventArgs e)
        => _vm.OptimizeVm.Mode = OptimizeMode.TxAdmin;

    private void OnMenuOptimizeAddFiles(object sender, RoutedEventArgs e)
    {
        var ov = _vm.OptimizeVm;
        var dlg = new OpenFileDialog
        {
            Title = ov.IsEmbeddedTexturesMode
                ? "Add model files (.ydd / .ydr / .yft) to optimize"
                : $"Add {ov.ActiveExtension.TrimStart('.').ToUpperInvariant()} files to optimize",
            Filter = ov.ActiveBrowseFilter,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true)
            ov.AddPaths(dlg.FileNames);
    }

    private void OnMenuOptimizeAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Pick a folder (a resource or clothing pack) to optimize",
            Multiselect = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog(this) == true)
            _vm.OptimizeVm.AddPaths(dlg.FolderNames);
    }

    private void OnMenuVehiclesCarPack(object sender, RoutedEventArgs e)
        => _vm.OpenViewCommand.Execute("Vehicles");

    private async void OnMenuVehiclesBrowse(object sender, RoutedEventArgs e)
    {
        _vm.OpenViewCommand.Execute("Vehicles");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (FindVehiclesView() is { } vv)
            await vv.RunBrowseInputFromMenuAsync();
        else
        {
            // Template not materialized yet — fall back to VM inputs only.
            var dlg = new OpenFileDialog
            {
                Title = "Pick SP car dlc.rpf file(s) — multi-select for a pack",
                Filter = "RAGE package (*.rpf)|*.rpf|All files (*.*)|*.*",
                Multiselect = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            if (dlg.ShowDialog(this) != true) return;
            var vm = _vm.VehiclesVm;
            if (vm.IsProcessing) return;
            if (vm.MergeIntoPack) vm.AddInputs(dlg.FileNames);
            else vm.SetInputs(dlg.FileNames);
        }
    }

    private void OnMenuVehiclesBrowseOutput(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose FiveM resource output folder",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog(this) == true)
            _vm.VehiclesVm.SetOutputFolder(dlg.FolderName);
    }

    private void OnMenuRpfBrowseInput(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Pick the FiveM resource folder to pack",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog(this) == true)
            _vm.RpfVm.SetInputFolder(dlg.FolderName);
    }

    private void OnMenuRpfBrowseOutput(object sender, RoutedEventArgs e)
    {
        var rpf = _vm.RpfVm;
        if (rpf.IsFolderOutput)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Choose output folder",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            if (dlg.ShowDialog(this) == true)
                rpf.OutputPath = dlg.FolderName;
        }
        else
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save .rpf",
                Filter = "RAGE package (*.rpf)|*.rpf|All files (*.*)|*.*",
                FileName = string.IsNullOrWhiteSpace(rpf.OutputPath) ? "pack.rpf" : Path.GetFileName(rpf.OutputPath),
            };
            if (dlg.ShowDialog(this) == true)
                rpf.OutputPath = dlg.FileName;
        }
    }

    private async void OnMenuRpfConvert(object sender, RoutedEventArgs e)
        => await _vm.RpfVm.ConvertAsync();

    private void OnMenuRpfModeRaw(object sender, RoutedEventArgs e) => _vm.RpfVm.OutputModeIndex = 0;
    private void OnMenuRpfModeSp(object sender, RoutedEventArgs e) => _vm.RpfVm.OutputModeIndex = 1;
    private void OnMenuRpfModeOpenIv(object sender, RoutedEventArgs e) => _vm.RpfVm.OutputModeIndex = 2;
    private void OnMenuRpfModeAddon(object sender, RoutedEventArgs e) => _vm.RpfVm.OutputModeIndex = 3;

    private void OnMenuEmoteNewTab(object sender, RoutedEventArgs e)
        => OnWorkspaceNewTab(sender, e);

    private async void OnWorkspaceNewTab(object sender, RoutedEventArgs e)
    {
        var kind = WorkspaceDocument.KindFromAppView(_vm.ActiveView) ?? WorkspaceKind.Assets;
        if (kind == WorkspaceKind.Emotes)
        {
            EnsureEmotesView();
            // Create the emote doc + its chrome tab, then select that chrome tab
            // UP FRONT. Activating the emote document is async (it awaits a
            // viewer snapshot first); SyncEmoteWorkspaceTabs keeps whichever
            // chrome Emotes tab is active in charge, so unless the NEW tab is
            // already the active chrome tab, the sync snaps selection back to
            // the old tab and the new one looks inactive.
            var newTitle = EmotesWorkspace.EmoteDocs.NextUntitledTitle();
            var newEmote = EmotesWorkspace.EmoteDocs.NewDocument(activate: false, title: newTitle);
            SyncEmoteWorkspaceTabs();
            var chromeTab = _vm.WorkspaceDocs.FindByEmoteId(newEmote.Id);
            if (chromeTab != null)
                _vm.WorkspaceDocs.Activate(chromeTab);
            await EmotesWorkspace.ActivateEmoteDocumentByIdAsync(newEmote.Id);
            SyncEmoteWorkspaceTabs();
            _vm.SaveWorkspaceSessionNow();
            return;
        }

        // Open the section if needed, then add a new chrome tab for it.
        if (WorkspaceDocument.KindFromAppView(_vm.ActiveView) != kind)
            _vm.OpenViewCommand.Execute(kind.ToString());
        _vm.NewWorkspaceTabForActiveView();
    }

    private async void OnWorkspaceTabClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not WorkspaceDocument doc) return;
        if (e.OriginalSource is DependencyObject src
            && FindVisualAncestor<System.Windows.Controls.Primitives.ButtonBase>(src) != null)
            return;

        _vm.WorkspaceDocs.Activate(doc);
        if (doc.Kind == WorkspaceKind.Emotes)
        {
            EnsureEmotesView();
            if (!string.IsNullOrEmpty(doc.EmoteDocumentId))
                await EmotesWorkspace.ActivateEmoteDocumentByIdAsync(doc.EmoteDocumentId);
            SyncEmoteWorkspaceTabs();
            return;
        }

        _vm.OpenViewCommand.Execute(doc.OpenViewTag);
    }

    private async void OnWorkspaceCloseTab(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe) return;
        var doc = fe.DataContext as WorkspaceDocument
                  ?? (fe.Parent as FrameworkElement)?.DataContext as WorkspaceDocument;
        if (doc == null) return;

        var title = doc.DisplayTitle.TrimEnd(' ', '*', '•');
        var pick = AppDialog.Show(
            $"Close \"{title}\"?\n\nUnsaved work in this tab will be lost.",
            "Close tab",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            this);
        if (pick != System.Windows.MessageBoxResult.Yes) return;

        // Emotes: close the linked pose document, then drop the chrome tab.
        // Do NOT EnsureEmotesView / reload GTA Male — that was resurrecting
        // the tab every time the user hit X.
        if (doc.Kind == WorkspaceKind.Emotes)
        {
            if (!string.IsNullOrEmpty(doc.EmoteDocumentId))
            {
                var closed = await EmotesWorkspace.CloseEmoteDocumentByIdAsync(
                    doc.EmoteDocumentId, alreadyConfirmed: true);
                if (!closed) return;
            }

            bool otherEmotes = false;
            foreach (var w in _vm.WorkspaceDocs.Documents)
            {
                if (w.Kind == WorkspaceKind.Emotes && !ReferenceEquals(w, doc))
                { otherEmotes = true; break; }
            }
            if (!otherEmotes)
                EmotesWorkspace.DiscardEmptyEmoteSession();

            // Sole chrome tab was Emotes — swap to Assets (CloseDocument would
            // only reset the same Emotes tab in place).
            if (_vm.WorkspaceDocs.Documents.Count <= 1)
            {
                var assets = new WorkspaceDocument { Kind = WorkspaceKind.Assets, Title = "Assets" };
                _vm.WorkspaceDocs.ReplaceAll(new[] { assets }, assets);
                _vm.OpenViewCommand.Execute("Assets");
                SyncEmoteWorkspaceTabs();
                _vm.SaveWorkspaceSessionNow();
                return;
            }

            var nextEmote = _vm.WorkspaceDocs.CloseDocument(doc);
            if (nextEmote.Kind == WorkspaceKind.Emotes && !string.IsNullOrEmpty(nextEmote.EmoteDocumentId))
            {
                _vm.OpenViewCommand.Execute("Emotes");
                _vm.WorkspaceDocs.Activate(nextEmote);
                await EmotesWorkspace.ActivateEmoteDocumentByIdAsync(nextEmote.EmoteDocumentId);
            }
            else
            {
                _vm.OpenViewCommand.Execute(nextEmote.OpenViewTag);
                _vm.WorkspaceDocs.Activate(nextEmote);
            }
            SyncEmoteWorkspaceTabs();
            _vm.SaveWorkspaceSessionNow();
            return;
        }

        var next = _vm.WorkspaceDocs.CloseDocument(doc);
        _vm.OpenViewCommand.Execute(next.OpenViewTag);
        _vm.WorkspaceDocs.Activate(next);
        if (next.Kind == WorkspaceKind.Emotes && !string.IsNullOrEmpty(next.EmoteDocumentId))
            await EmotesWorkspace.ActivateEmoteDocumentByIdAsync(next.EmoteDocumentId);
        _vm.SaveWorkspaceSessionNow();
    }

    private bool _emoteTabSyncBusy;

    private void AttachEmoteWorkspaceTabSync()
    {
        var set = EmotesWorkspace.EmoteDocs;
        set.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EmoteDocumentSet.ActiveDocument)
                or nameof(EmoteDocumentSet.ActiveDocumentId))
                SyncEmoteWorkspaceTabs();
        };
        foreach (var d in set.Documents)
            d.PropertyChanged += OnEmoteDocumentPropertyChanged;
        set.Documents.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (EmoteDocument d in e.NewItems)
                    d.PropertyChanged += OnEmoteDocumentPropertyChanged;
            if (e.OldItems != null)
                foreach (EmoteDocument d in e.OldItems)
                    d.PropertyChanged -= OnEmoteDocumentPropertyChanged;
            SyncEmoteWorkspaceTabs();
        };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ActiveView) && _vm.IsEmotesView)
            {
                // OpenView sets ActiveView then immediately calls
                // SyncWorkspaceTabForActiveView. Defer so the current tab is
                // repurposed to Emotes first; then we mirror / attach
                // EmoteDocuments.
                Dispatcher.BeginInvoke(
                    new Action(SyncEmoteWorkspaceTabs),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        };
    }

    private void OnEmoteDocumentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EmoteDocument.Title)
            or nameof(EmoteDocument.IsDirty)
            or nameof(EmoteDocument.DisplayTitle)
            or nameof(EmoteDocument.LoadedModelPath))
            SyncEmoteWorkspaceTabs();
    }

    /// <summary>Mirror PoseToEmoteView's EmoteDocumentSet into chrome
    /// WorkspaceDocuments of kind Emotes.</summary>
    private void SyncEmoteWorkspaceTabs()
    {
        if (_emoteTabSyncBusy || _vm.SuppressWorkspaceTabSync) return;
        _emoteTabSyncBusy = true;
        try
        {
            var emoteSet = EmotesWorkspace.EmoteDocs;
            bool anyChromeEmotes = _vm.WorkspaceDocs.FindLastOfKind(WorkspaceKind.Emotes) != null;
            // Only mirror Emotes into the chrome strip when the user is on
            // Emotes or already has Emotes tabs open. A warm/blank GTA load
            // must not spawn tabs the user closed.
            bool needEmotes = _vm.IsEmotesView || anyChromeEmotes
                || emoteSet.Documents.Count > 1;
            if (!needEmotes) return;

            var liveIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ed in emoteSet.Documents)
            {
                liveIds.Add(ed.Id);
                var title = string.IsNullOrWhiteSpace(ed.Title)
                    ? (string.IsNullOrEmpty(ed.LoadedModelPath)
                        ? "Untitled"
                        : Path.GetFileNameWithoutExtension(ed.LoadedModelPath))
                    : ed.Title.Trim();
                var existing = _vm.WorkspaceDocs.FindByEmoteId(ed.Id);
                if (existing == null)
                {
                    // Reuse an orphan Emotes chrome tab (created by workspace-bar
                    // navigation before EmoteDocs were linked). Never repurpose a
                    // non-Emotes tab from here — the workspace bar navigates the
                    // current tab in place, so a tab the user pointed at another
                    // section must stay there.
                    WorkspaceDocument? orphan = null;
                    foreach (var w in _vm.WorkspaceDocs.Documents)
                    {
                        if (w.Kind == WorkspaceKind.Emotes && string.IsNullOrEmpty(w.EmoteDocumentId))
                        { orphan = w; break; }
                    }
                    if (orphan != null)
                    {
                        orphan.EmoteDocumentId = ed.Id;
                        orphan.Title = title;
                        orphan.IsDirty = ed.IsDirty;
                    }
                    else
                    {
                        _vm.WorkspaceDocs.NewDocument(
                            WorkspaceKind.Emotes,
                            activate: false,
                            title: title,
                            emoteDocumentId: ed.Id);
                    }
                }
                else
                {
                    existing.Title = title;
                    existing.IsDirty = ed.IsDirty;
                }
            }

            for (int i = _vm.WorkspaceDocs.Documents.Count - 1; i >= 0; i--)
            {
                var w = _vm.WorkspaceDocs.Documents[i];
                if (w.Kind != WorkspaceKind.Emotes) continue;
                if (!string.IsNullOrEmpty(w.EmoteDocumentId) && !liveIds.Contains(w.EmoteDocumentId))
                    _vm.WorkspaceDocs.Documents.RemoveAt(i);
            }

            // Chrome Emotes tabs created by converting a "+" placeholder still
            // need a backing EmoteDocument when every existing doc already had
            // a chrome row.
            foreach (var w in _vm.WorkspaceDocs.Documents.ToList())
            {
                if (w.Kind != WorkspaceKind.Emotes || !string.IsNullOrEmpty(w.EmoteDocumentId))
                    continue;
                var ed = emoteSet.NewDocument(activate: false);
                w.EmoteDocumentId = ed.Id;
                w.Title = string.IsNullOrWhiteSpace(ed.Title) ? "Untitled" : ed.Title.Trim();
                w.IsDirty = ed.IsDirty;
                liveIds.Add(ed.Id);
            }

            if (_vm.IsEmotesView)
            {
                var activeChrome = _vm.WorkspaceDocs.ActiveDocument;
                // Keep the converted "+" tab selected — don't jump to an older Emotes row.
                if (activeChrome != null
                    && activeChrome.Kind == WorkspaceKind.Emotes
                    && !string.IsNullOrEmpty(activeChrome.EmoteDocumentId))
                {
                    var ed = emoteSet.Find(activeChrome.EmoteDocumentId!);
                    if (ed != null)
                        emoteSet.ActiveDocument = ed;
                }
                else if (emoteSet.ActiveDocument != null)
                {
                    var match = _vm.WorkspaceDocs.FindByEmoteId(emoteSet.ActiveDocument.Id);
                    if (match != null)
                        _vm.WorkspaceDocs.Activate(match);
                }
            }
            RefreshWindowTitle();
        }
        finally
        {
            _emoteTabSyncBusy = false;
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        while (start != null)
        {
            if (start is T match) return match;
            start = System.Windows.Media.VisualTreeHelper.GetParent(start);
        }
        return null;
    }

    private VehiclesView? FindVehiclesView()
    {
        if (FindName("VehiclesHost") is not DependencyObject host) return null;
        return FindVisualChild<VehiclesView>(host);
    }

    private OptimizeView? FindOptimizeView() => FindVisualChild<OptimizeView>(this);

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    // ─────────────── Help menu ───────────────

    private void OnHelpInstall(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Controls.TextBlock
        {
            Text = "1. Unzip the resource folder into your FiveM `resources/` directory.\n" +
                   "2. Add `ensure your_resource_name` to your server.cfg.\n" +
                   "3. Restart the server. The prop is now spawnable in-game.",
            Margin = new Thickness(20),
            TextWrapping = TextWrapping.Wrap
        };
        var win = new Window
        {
            Title = "How to install FiveM props",
            Content = dlg,
            Width = 460,
            Height = 200,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        win.ShowDialog();
    }

    // ─────────────── View menu (reference ped) ───────────────

    private void OnViewToggleReference(object sender, RoutedEventArgs e)
    {
        // The MenuItem's IsChecked is two-way bound to ShowReferencePed,
        // and the VM's partial setter persists. Nothing to do here.
    }

    private void OnViewChooseReference(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Pick a reference model for scale comparison",
            Filter = "3D models (*.fbx;*.glb;*.gltf;*.obj;*.dae;*.ply;*.stl)|" +
                     "*.fbx;*.glb;*.gltf;*.obj;*.dae;*.ply;*.stl|All files (*.*)|*.*",
        };
        var current = _vm.ReferenceModelPath;
        if (!string.IsNullOrEmpty(current) && File.Exists(current))
            dlg.InitialDirectory = Path.GetDirectoryName(current);

        if (dlg.ShowDialog(this) == true)
        {
            Services.UserSettings.SaveReferenceModelPath(dlg.FileName);
            _vm.ReferenceModelPath = dlg.FileName;  // triggers re-load via PropertyChanged
        }
    }

    private void OnViewResetReference(object sender, RoutedEventArgs e)
    {
        Services.UserSettings.SaveReferenceModelPath(null);
        _vm.ReferenceModelPath = Services.UserSettings.ResolveReferenceModelPath();
    }

    private void OnHelpAbout(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    /// <summary>Hits the GitHub Releases API and surfaces the verdict in a
    /// Fluent-themed message box. Includes a "View release" hyperlink when
    /// an update is available so the user can grab the new build.</summary>
    private bool _checkingUpdates;

    private async void OnHelpCheckForUpdates(object sender, RoutedEventArgs e)
    {
        if (_checkingUpdates) return;  // ignore rapid re-clicks (no double dialogs)
        _checkingUpdates = true;
        try
        {
        _vm.StatusText = "Checking for updates...";
        var result = await Services.UpdateChecker.CheckAsync();
        if (result.Status is Services.UpdateChecker.Status.UpToDate
                          or Services.UpdateChecker.Status.UpdateAvailable)
            Services.UserSettings.SaveLastUpdateCheck(DateTime.UtcNow);

        switch (result.Status)
        {
            case Services.UpdateChecker.Status.UpToDate:
                _vm.StatusText = $"You're up to date (v{result.Current.Major}.{result.Current.Minor}.{result.Current.Build}).";
                AppDialog.Show(
                    $"You're running the latest release.\nCurrent: v{result.Current.Major}.{result.Current.Minor}.{result.Current.Build}",
                    "Up to date",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information,
                    this);
                break;

            case Services.UpdateChecker.Status.UpdateAvailable:
                _vm.StatusText = $"Update available: v{result.Latest!.Major}.{result.Latest.Minor}.{result.Latest.Build}.";
                await OfferUpdateAsync(result);
                break;

            case Services.UpdateChecker.Status.NoReleases:
                _vm.StatusText = "No releases published yet.";
                AppDialog.Show(
                    "No update manifest has been published yet.",
                    "No releases",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information,
                    this);
                break;

            default:
                _vm.StatusText = $"Update check failed: {result.Error}";
                AppDialog.Show(
                    $"Couldn't reach the update host:\n\n{result.Error}",
                    "Update check failed",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning,
                    this);
                break;
        }
        }
        finally { _checkingUpdates = false; }
    }

    /// <summary>
    /// Fluent-themed update prompt (<see cref="UpdateAvailableDialog"/>):
    ///   Update → download &amp; install in-place, then relaunch (or open
    ///            the release page when the manifest has no download_url).
    ///   Skip   → persist the version; launch stops auto-prompting for it
    ///            (the title-bar badge stays visible for a change of mind).
    ///   Close  → do nothing; asks again next launch.
    /// </summary>
    private async Task OfferUpdateAsync(Services.UpdateChecker.CheckResult result)
    {
        var hasDownload = !string.IsNullOrWhiteSpace(result.DownloadUrl);
        var hasReleasePage = !string.IsNullOrWhiteSpace(result.ReleaseUrl);

        var dlg = new UpdateAvailableDialog(result) { Owner = this };
        dlg.ShowDialog();

        if (dlg.Result == UpdateAvailableDialog.Choice.Update && hasDownload)
        {
            await RunInPlaceUpdateAsync(result.DownloadUrl!, result.LatestTag ?? "the new version", result.Sha256);
            return;
        }

        if (dlg.Result == UpdateAvailableDialog.Choice.Skip)
        {
            Services.UserSettings.SaveSkippedUpdateVersion(result.LatestTag);
            _vm.StatusText = $"Skipped {result.LatestTag} — the badge stays in the corner if you change your mind.";
            return;
        }

        // "Update" on a download-less manifest — open the release page.
        if (dlg.Result == UpdateAvailableDialog.Choice.Update && !hasDownload && hasReleasePage)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = result.ReleaseUrl,
                    UseShellExecute = true,
                });
            }
            catch { /* swallow */ }
        }
    }

    private async Task RunInPlaceUpdateAsync(string downloadUrl, string versionLabel, string? expectedSha256 = null)
    {
        _vm.StatusText = $"Downloading {versionLabel}...";
        var dlg = new UpdateProgressDialog(downloadUrl, versionLabel, expectedSha256) { Owner = this };
        var ok = dlg.ShowDialog() == true;
        if (ok)
        {
            // The swap script is already running in the background and
            // will relaunch us once this process exits. Shut down now so
            // it can take the file lock.
            _updateInstalling = true;   // suppress the Welcome screen mid-restart
            _vm.StatusText = "Update downloaded — restarting...";
            await Task.Delay(150); // let the status text actually paint
            System.Windows.Application.Current.Shutdown();
            return;
        }

        if (dlg.Failure != null)
        {
            _vm.StatusText = $"Update failed: {dlg.Failure.Message}";
            AppDialog.Show(
                $"The update couldn't be downloaded:\n\n{dlg.Failure.Message}",
                "Update failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning,
                this);
        }
        else
        {
            _vm.StatusText = "Update canceled.";
        }
    }

    // ─────────────── Drag & drop ───────────────

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // These window-level handlers are wired to the TUNNELING Preview
        // events so a drop anywhere over the 3D viewer (a WebView2 airspace
        // island that doesn't bubble) still loads a model. But tunneling
        // fires window→child FIRST, and we used to always mark the drag
        // Handled here — which silently swallowed drops meant for OTHER
        // views (e.g. the Optimize drop zone) before they ever saw them.
        // Only claim the drag on the 3D-model view; otherwise bow out
        // without handling so the event keeps tunnelling down to the
        // active view's own drop target.
        if (!_vm.Is3DView) return;

        // Panel reorder (PanelLayout) — do not claim; let the inspector
        // stack handle Move effects. Claiming here used to set Effects=None
        // and kill every ⠿ drag on Layers / Textures / Pack.
        if (e.Data.GetDataPresent("FiveOS.PanelCard")) return;

        e.Effects = DragDropEffects.None;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            foreach (var f in files)
            {
                if (IsSupported(f)) { e.Effects = DragDropEffects.Copy; break; }
            }
        }
        e.Handled = true;
    }

    private void OnFileDropped(object sender, DragEventArgs e)
    {
        // See OnDragOver: only the 3D-model view loads files at the window
        // level. Let every other view's drop zone handle its own drops.
        if (!_vm.Is3DView) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        // Multi-file drop → open the batch converter pre-seeded with
        // every supported file. Single-file drop keeps the legacy
        // viewer-load path so the user can preview + tweak before
        // converting.
        var batchable = files.Where(ViewModels.BatchConvertViewModel.IsSupportedForBatch).ToList();
        if (batchable.Count > 1)
        {
            e.Handled = true;
            OpenBatchConvertDialog(batchable);
            return;
        }
        foreach (var f in files)
        {
            if (IsSupported(f)) { TryLoad(f); e.Handled = true; return; }
        }
    }

    private void OnDropZoneClick(object sender, RoutedEventArgs e) => BrowseForModel();

    // ─────────────── Keyboard ───────────────

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift)   == ModifierKeys.Shift;

        if (ctrl && e.Key == Key.Return && _vm.CanConvert)
        {
            OnSimplePrimaryAction(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (ctrl && shift && e.Key == Key.S)
        {
            OpenSketchfabImport();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.O)
        {
            if (shift)
                OnFileBatchConvert(this, new RoutedEventArgs());
            else
                OnFileOpen(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.T)
        {
            OnFileAddTextures(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.M)
        {
            OnFileOpenMapFolder(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (e.Key == Key.K && _vm.IsAnimatedPropsView && !ctrl)
        {
            _vm.AddPropAnimKeyCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.OemComma)
        {
            OpenSettingsModal();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _vm.IsSettingsOpen)
        {
            _vm.IsSettingsOpen = false;
            e.Handled = true;
            return;
        }
    }

    // ─────────────── Convert ───────────────

    private async void OnConvert(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.SourcePath))
            return;
        if (string.IsNullOrWhiteSpace(_vm.PropName))
        {
            // Session-restored loads can arrive with a blank prop name —
            // derive one from the source file instead of silently ignoring
            // the convert (drag-to-group and Ctrl+Enter both land here).
            var stem = Path.GetFileNameWithoutExtension(_vm.SourcePath);
            if (string.IsNullOrWhiteSpace(stem))
            {
                _vm.StatusText = "Give the prop a name first (SOURCE card).";
                return;
            }
            _vm.PropName = stem;
        }

        if (!EngineRunner.IsEngineAvailable())
        {
            Services.FosLogger.Err("convert", $"engine missing at {EngineRunner.EnginePath}");
            var msg = "Conversion engine is missing from the install.\n\n" +
                      $"Expected: {EngineRunner.EnginePath}\n\n" +
                      "Re-install FiveOS — the conversion engine ships in the same bundle.";
            AppDialog.Show(msg, "Engine not available",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning, this);
            _vm.StatusText = "Engine not available — see dialog.";
            return;
        }

        Services.FosLogger.Info("convert", $"start: name='{_vm.PropName}' mode={_vm.ExportMode} src={Path.GetFileName(_vm.SourcePath)}");
        _vm.IsConverting = true;
        _vm.StatusText = "Starting conversion...";

        var excludeMeshes = _vm.ModelParts.Where(p => !p.IsVisible).Select(p => p.OriginalName)
            .Concat(_vm.DeletedPartNames)
            .Distinct(System.StringComparer.Ordinal)
            .ToList();
        var req = BuildConvertRequest(_vm.PropName, excludeMeshes, _vm.IsPackMode);

        var runner = new EngineRunner();
        _convertCts = new System.Threading.CancellationTokenSource();
        // Keep the destination visible through every engine stage line —
        // "scene → pack: [5/6] Building collision…" — so a drag-to-group
        // convert doesn't read like unrelated background work.
        _convertStatusContext = req.RouteToPack
            ? $"{_vm.PropName} → {req.PackGroup ?? "layers"}: "
            : null;
        EngineRunner.ConvertOutcome outcome;
        bool wasCancelled;
        try
        {
            outcome = await runner.RunAsync(req, onLog: OnEngineLog, cancel: _convertCts.Token);
            wasCancelled = _convertCts.IsCancellationRequested;
        }
        catch (System.Exception ex)
        {
            // RunAsync can throw before its own guard (e.g. output folder / temp
            // dir creation on a full or read-only disk). Without this the tab
            // stays locked forever (IsConverting never resets) with no error.
            Services.FosLogger.Err("convert", "failed: " + ex.Message);
            StopEngineStepTimer();
            _convertStatusContext = null;
            _vm.IsConverting = false;
            _convertCts?.Dispose();
            _convertCts = null;
            _vm.StatusText = "Conversion failed: " + ex.Message;
            AppDialog.Error("Conversion failed:\n\n" + ex.Message, "Conversion failed", this);
            return;
        }
        _convertCts.Dispose();
        _convertCts = null;

        StopEngineStepTimer();
        _convertStatusContext = null;
        _vm.IsConverting = false;

        if (outcome.Success && outcome.ResultPath != null)
        {
            if (outcome.Mode == EngineRunner.OutputMode.Pack)
            {
                // Pack mode: keep the editor open so the user can
                // immediately load the next prop. No success screen, no
                // ResultZipPath — the pack hasn't been finalised yet.
                // Drop the current model so the drop zone is ready for
                // the next file, same way "Convert another" does it.
                _vm.StatusText = $"✓ Added '{_vm.PropName}' to pack ({_vm.PackSession.Count} prop{(_vm.PackSession.Count == 1 ? "" : "s")})";
                ClearLoadedModel();
            }
            else
            {
                _vm.ResultZipPath = outcome.ResultPath;
                // Setting ShowSuccessScreen flips IsViewportVisible to false,
                // which collapses the WebView2 host so it stops covering the
                // success-screen overlay (WPF airspace issue).
                _vm.ShowSuccessScreen = true;

                // Status line varies by output mode — for zip we report the
                // .zip's size; for the loose server modes we report what
                // landed on disk and where.
                string sizePart = "";
                try
                {
                    if (outcome.Mode == EngineRunner.OutputMode.SingleZip && File.Exists(outcome.ResultPath))
                        sizePart = " · " + FormatBytes(new FileInfo(outcome.ResultPath).Length);
                }
                catch { /* size is cosmetic */ }
                _vm.StatusText = outcome.Mode switch
                {
                    EngineRunner.OutputMode.ServerShared    => $"✓ Done · merged into {outcome.ResultPath}",
                    EngineRunner.OutputMode.ServerPerAsset  => $"✓ Done · {Path.GetFileName(outcome.ResultPath)} ready in server folder",
                    _                                       => $"✓ Done{sizePart} · {Path.GetFileName(outcome.ResultPath)}",
                };

                // Swap the Textures list from source embeds → compiled TXD
                // names/thumbs pulled from the output .ydr.
                _ = LoadCompiledTexturesFromOutcomeAsync(outcome, _vm.PropName);
            }
        }
        else if (wasCancelled)
        {
            // User hit Cancel — the engine was killed. Not a failure, so skip
            // the crash dialog and just report the stop.
            _vm.StatusText = "Conversion cancelled.";
        }
        else
        {
            // Themed crash dialog with copy/save actions. Strips the
            // engine's internal "[ydr-writer]" tag and pattern-matches
            // common failure modes to a friendlier headline.
            CrashDialog.ShowEngineFailure(this, outcome.Error ?? "", outcome.Log ?? "");
            _vm.StatusText = $"✗ Conversion failed: {outcome.Error}";
        }
    }

    /// <summary>Cancels the running convert (single or split-layer). Idle-safe.</summary>
    private void OnCancelConvert(object sender, RoutedEventArgs e)
    {
        _convertCts?.Cancel();
        _vm.StatusText = "Cancelling conversion…";
    }

    /// <summary>
    /// Build a ConvertRequest from the current sidebar/gizmo state with
    /// per-call overrides for the only two fields that diverge across
    /// callers: AssetName (one-shot vs. per-layer) and ExcludeMeshes
    /// (whatever the caller has decided to drop from this pass).
    /// RouteToPack is also override-able so the split-layers path can
    /// force every pass into the pack session even before the user has
    /// toggled Pack mode on the sidebar.
    /// </summary>
    private EngineRunner.ConvertRequest BuildConvertRequest(
        string assetName,
        IReadOnlyCollection<string> excludeMeshes,
        bool routeToPack)
    {
        return new EngineRunner.ConvertRequest(
            SourcePath: _vm.SourcePath!,
            AssetName: assetName,
            Up: _vm.UpAxisIndex switch
            {
                1 => EngineRunner.UpAxis.YUp,
                2 => EngineRunner.UpAxis.ZUp,
                _ => EngineRunner.UpAxis.Auto,
            },
            CollisionMaterial: string.IsNullOrWhiteSpace(_vm.CollisionMaterial) ? "CONCRETE" : _vm.CollisionMaterial,
            IncludeCollision: _vm.IncludeCollision,
            EmbedCollision: _vm.EmbedCollision,
            IncludeYtyp: _vm.IncludeYtyp,
            ExtractTextures: _vm.ExtractTextures,
            ScaleHint: _gizmoScale,
            PositionHint: "0,0,0",
            RotationHint: $"{_gizmoRot.X.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                          $"{_gizmoRot.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                          $"{_gizmoRot.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            ExcludeMeshes: excludeMeshes,
            GenerateLods:   _vm.GenerateLods,
            LodDistHigh:    _vm.LodDistHigh,
            LodDistMed:     _vm.LodDistMed,
            LodDistLow:     _vm.LodDistLow,
            LodDistVlow:    _vm.LodDistVlow,
            RouteToPack:    routeToPack,
            // Converts land in the group selected in the Layers outliner;
            // with none selected they stage as a loose top-level layer.
            PackGroup:      routeToPack ? _vm.TargetOutlinerGroup : null,
            PartMaterials:  BuildPartMaterialMap(),
            BreakableGlass: _vm.BreakableGlass,
            GlassOpacity:   _vm.GlassOpacity,
            PartDiffuseTextures: _vm.PartDiffuseOverrides.Count > 0
                ? new Dictionary<string, string>(_vm.PartDiffuseOverrides, StringComparer.OrdinalIgnoreCase)
                : null,
            AnimatedProp: _vm.IsAnimatedPropsView || _vm.AnimatedProp,
            // Gate AutoSpin on AnimatedProp: the auto-spin controls only SHOW
            // under Animated prop, so a user who enabled auto-spin then turned
            // Animated prop back off must not still ship a spinning prop.
            // Animated workspace prefers timeline keys over auto-spin.
            AutoSpin: !_vm.IsAnimatedPropsView && _vm.AnimatedProp && _vm.AutoSpin,
            SpinAxis: _vm.SpinAxisIndex switch { 0 => "X", 1 => "Y", _ => "Z" },
            SpinSeconds: _vm.SpinSeconds,
            SpinReverse: _vm.SpinReverse,
            AnimKeysPath: WritePropAnimKeysSidecar());
    }

    /// <summary>Snapshot the per-part material presets the user picked
    /// in the layers panel, keyed by the part's <see cref="MainViewModel.ModelPart.OriginalName"/>
    /// (the source mesh name — what the engine sees). Parts left on
    /// Standard are omitted so the engine keeps its default shader pick.</summary>
    private Dictionary<string, string>? BuildPartMaterialMap()
    {
        if (_vm.ModelParts.Count == 0) return null;
        Dictionary<string, string>? map = null;
        foreach (var p in _vm.ModelParts)
        {
            if (p.MaterialPreset == MaterialPreset.Standard) continue;
            (map ??= new(System.StringComparer.OrdinalIgnoreCase))[p.OriginalName] = p.MaterialPreset.ToString();
        }
        return map;
    }

    /// <summary>
    /// Convert each visible layer/part of the loaded source as its own
    /// YDR, accumulating them into the active prop-pack session. Drives N
    /// engine passes back-to-back, isolating one mesh per pass via the
    /// exclude-mesh list. The caller is expected to finalize the pack
    /// from the Pack panel once the loop finishes — we don't auto-build
    /// the resource because the user may want to inspect the pack first
    /// (rename slots, drop entries, etc).
    /// </summary>
    private async void OnSplitLayersToYdrs(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.SourcePath)) return;
        if (_vm.IsConverting) return;

        var parts = _vm.ModelParts.Where(p => p.IsVisible).ToList();
        if (parts.Count < 1)
        {
            AppDialog.Show(
                "No visible parts to export. Toggle the eye icons in the Layers panel for the meshes you want to split out.",
                "Split layers",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information,
                this);
            return;
        }

        if (!EngineRunner.IsEngineAvailable())
        {
            Services.FosLogger.Err("split", $"engine missing at {EngineRunner.EnginePath}");
            AppDialog.Show(
                "Conversion engine is missing from the install.\n\nRe-install FiveOS — the conversion engine ships in the same bundle.",
                "Engine not available",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning,
                this);
            return;
        }

        var pick = AppDialog.Show(
            $"Convert each of the {parts.Count} visible part(s) to its own YDR and add them all to the prop pack?\n\n" +
            $"Pack mode will be enabled automatically. Finalize from the Pack panel when the loop finishes.",
            "Split layers into separate YDRs",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question,
            this);
        if (pick != System.Windows.MessageBoxResult.OK) return;

        if (!_vm.IsPackMode) _vm.IsPackMode = true;

        Services.FosLogger.Info("split", $"start: {parts.Count} parts src={Path.GetFileName(_vm.SourcePath)}");
        _vm.IsConverting = true;

        // Every part name not equal to the kept one becomes an exclude.
        // Pre-built up front so we don't re-enumerate ModelParts N times.
        var allOriginalNames = _vm.ModelParts.Select(p => p.OriginalName)
            .Concat(_vm.DeletedPartNames)
            .Distinct(System.StringComparer.Ordinal)
            .ToList();

        int ok = 0, fail = 0;
        var runner = new EngineRunner();
        _convertCts = new System.Threading.CancellationTokenSource();
        bool wasCancelled = false;
        string? splitError = null;
        try
        {
            for (int i = 0; i < parts.Count; i++)
            {
                if (_convertCts.IsCancellationRequested) break;
                var part = parts[i];
                var keep = part.OriginalName;
                var excludeForThisPass = allOriginalNames
                    .Where(n => !string.Equals(n, keep, StringComparison.Ordinal))
                    .ToList();

                var assetName = SanitizeSlotName(part.Name);
                if (string.IsNullOrEmpty(assetName)) assetName = $"part_{i + 1}";

                _vm.StatusText = $"Split {i + 1}/{parts.Count}: converting '{assetName}'...";
                var req = BuildConvertRequest(assetName, excludeForThisPass, routeToPack: true);
                var outcome = await runner.RunAsync(req, onLog: OnEngineLog, cancel: _convertCts.Token);
                StopEngineStepTimer();
                if (outcome.Success) ok++; else { fail++; Services.FosLogger.Err("split", $"part '{assetName}' failed: {outcome.Error}"); }
            }
            wasCancelled = _convertCts.IsCancellationRequested;
        }
        catch (System.Exception ex)
        {
            // A throw on any part must still reset the tab and surface the error,
            // not leave the split wedged mid-run with the UI locked.
            splitError = ex.Message;
            Services.FosLogger.Err("split", "failed: " + ex.Message);
            StopEngineStepTimer();
        }
        finally
        {
            _convertCts?.Dispose();
            _convertCts = null;
            _vm.IsConverting = false;
        }

        if (splitError != null)
        {
            _vm.StatusText = "Split failed: " + splitError;
            AppDialog.Error("Split failed:\n\n" + splitError, "Split failed", this);
        }
        else
        {
            _vm.StatusText = wasCancelled
                ? $"Split cancelled — {ok} part(s) added before stopping."
                : fail == 0
                    ? $"✓ Split done — {ok} part(s) added to pack. Open the Pack panel to finalize."
                    : $"Split done — {ok} ok, {fail} failed. Check log for errors.";
        }
    }

    /// <summary>Lowercase, ASCII-letter/digit/underscore-only slot name
    /// derived from the user-visible layer label. Spaces/dashes collapse
    /// to underscores; everything else is dropped. Empty string is
    /// returned for inputs that had no usable characters — caller falls
    /// back to a positional placeholder.</summary>
    private static string SanitizeSlotName(string raw)
    {
        var sb = new System.Text.StringBuilder(raw?.Length ?? 0);
        foreach (var ch in raw ?? "")
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ' || ch == '-') sb.Append('_');
        }
        var s = sb.ToString().Trim('_');
        // Never return empty: an empty asset name yields file ".ydr", empty
        // manifest entries, and JenkHash("")=0 as the archetype name — an
        // unresolvable archetype that fails to stream. Fall back like the
        // primary SanitizeAssetName path does.
        return string.IsNullOrEmpty(s) ? "model" : s;
    }

    /// <summary>
    /// Forwards an engine stdout/stderr line to the status bar and re-arms
    /// an elapsed-time ticker whenever a new step header arrives. Without
    /// this ticker, CodeWalker.Core's FbxConverter step ([3/3]) appears to
    /// hang on large meshes — the call is fully synchronous with no
    /// progress callbacks, so the status text would otherwise sit
    /// unchanged for the entire conversion.
    /// </summary>
    /// <summary>"scene → pack: " while a convert targets an outliner group,
    /// so engine stage chatter stays attributable to the user's action.</summary>
    private string? _convertStatusContext;

    private void OnEngineLog(string line)
    {
        Dispatcher.Invoke(() =>
        {
            // Any new line from the engine implies forward progress, so
            // tear down the previous step's elapsed timer. We may re-arm
            // immediately below if this line is itself a step header.
            StopEngineStepTimer();

            var cleaned = EngineSelfTag.Replace(line, "");
            var truncated = Truncate(cleaned, 120);
            var prefix = "FiveOS · " + (_convertStatusContext ?? "");
            _vm.StatusText = prefix + truncated;

            if (!EngineStepHeader.IsMatch(line))
                return;

            _engineStepBaseStatus = prefix + truncated;
            _engineStepStartUtc = DateTime.UtcNow;
            _engineStepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _engineStepTimer.Tick += (_, _) =>
            {
                var elapsed = DateTime.UtcNow - _engineStepStartUtc;
                var clock = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
                // Hint the user that the silence is expected on big meshes,
                // but only after we've been on this step long enough that
                // it's worth saying.
                var hint = elapsed.TotalSeconds >= 30
                    ? "  · large meshes can take many minutes"
                    : "";
                _vm.StatusText = $"{_engineStepBaseStatus}  · {clock} elapsed{hint}";
            };
            _engineStepTimer.Start();
        });
    }

    private void StopEngineStepTimer()
    {
        _engineStepTimer?.Stop();
        _engineStepTimer = null;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.ResultZipPath)) return;
        try
        {
            Process.Start("explorer.exe", $"/select,\"{_vm.ResultZipPath}\"");
        }
        catch { /* swallow */ }
    }

    private void OnConvertAnother(object sender, RoutedEventArgs e) => ClearLoadedModel();

    /// <summary>Dismiss the success overlay but KEEP the loaded model, its
    /// layers, transform, and material presets, so the user can tweak and
    /// re-export without re-importing. The viewport is only hidden (not
    /// cleared) while the success screen shows — IsViewportVisible is
    /// (HasModel &amp;&amp; !ShowSuccessScreen) — so flipping the flag brings the
    /// model straight back. Contrast OnConvertAnother, which wipes it.</summary>
    private void OnBackToModel(object sender, RoutedEventArgs e)
    {
        _vm.ShowSuccessScreen = false;
        if (_vm.HasModel)
            _vm.StatusText = "Back in the editor — tweak and export again, or Convert another to start fresh.";
    }

    private void OnClearSource(object sender, RoutedEventArgs e) => ClearLoadedModel();

    private void ApplySavedPropsSidebarWidth()
    {
        var w = Services.UserSettings.LoadPropsSidebarWidth();
        if (w >= LeftSidebarColumn.MinWidth && w <= LeftSidebarColumn.MaxWidth)
            LeftSidebarColumn.Width = new GridLength(w);
    }

    private void OnSidebarSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        Services.UserSettings.SavePropsSidebarWidth(LeftSidebarColumn.ActualWidth);
    }

    /// <summary>Give the animated-keys timeline its saved (or default) height
    /// in animated-prop mode; collapse the row to 0 otherwise so it reserves no
    /// space for the modes that never show a timeline.</summary>
    private void ApplyPropAnimTimelineHeight()
        => PropAnimTimelineRow.Height = _vm.IsAnimatedPropsView
            ? new GridLength(Services.UserSettings.LoadPropsAnimTimelineHeight())
            : new GridLength(0);

    private void OnPropAnimTimelineSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        Services.UserSettings.SavePropsAnimTimelineHeight(PropAnimTimelineRow.ActualHeight);
    }

    // ─── PANELS sidebar dock (drag whole sidebar left/right of viewport) ──

    private Point _panelsDockPress;
    private bool _panelsDockDragging;

    private void OnPanelsDockHeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src &&
            FindVisualAncestor<System.Windows.Controls.Primitives.ButtonBase>(src) is not null)
            return;

        _panelsDockPress = e.GetPosition(this);
        _panelsDockDragging = false;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnPanelsDockHeaderMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (!((UIElement)sender).IsMouseCaptured) return;

        var pos = e.GetPosition(this);
        if (!_panelsDockDragging)
        {
            if (Math.Abs(pos.X - _panelsDockPress.X) < 4 && Math.Abs(pos.Y - _panelsDockPress.Y) < 4)
                return;
            _panelsDockDragging = true;
            LayersPanel.Opacity = 0.55;
            Mouse.OverrideCursor = Cursors.SizeWE;
        }

        // Live preview: which side would we dock to?
        var local = e.GetPosition(PropsCenterGrid);
        bool wantLeft = local.X < PropsCenterGrid.ActualWidth * 0.5;
        if (wantLeft != _vm.IsLayersDockedLeft)
        {
            _vm.IsLayersDockedLeft = wantLeft;
            _ = NudgeViewerResizeAsync();
        }
        e.Handled = true;
    }

    private void OnPanelsDockHeaderUp(object sender, MouseButtonEventArgs e)
    {
        FinishPanelsDockDrag(sender as UIElement);
        e.Handled = true;
    }

    private void OnPanelsDockHeaderLostCapture(object sender, MouseEventArgs e)
        => FinishPanelsDockDrag(sender as UIElement);

    private void FinishPanelsDockDrag(UIElement? handle)
    {
        if (handle is not null && Mouse.Captured == handle)
            handle.ReleaseMouseCapture();
        LayersPanel.Opacity = 1.0;
        Mouse.OverrideCursor = null;
        if (_panelsDockDragging)
        {
            _vm.StatusText = _vm.IsLayersDockedLeft
                ? "PANELS docked on the left."
                : "PANELS docked on the right.";
            _ = NudgeViewerResizeAsync();
        }
        _panelsDockDragging = false;
    }

    // ─── PANELS sidebar resize (drag edge to extend) ───────────────────

    private bool _panelsResizing;
    private double _panelsResizeStartWidth;
    private double _panelsResizeStartX;

    private void OnPanelsResizeDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.IsLayersPanelCollapsed) return;
        _panelsResizing = true;
        _panelsResizeStartWidth = _vm.LayersPanelWidth;
        _panelsResizeStartX = e.GetPosition(PropsCenterGrid).X;
        ((UIElement)sender).CaptureMouse();
        Mouse.OverrideCursor = Cursors.SizeWE;
        e.Handled = true;
    }

    private void OnPanelsResizeMove(object sender, MouseEventArgs e)
    {
        if (!_panelsResizing || e.LeftButton != MouseButtonState.Pressed) return;

        double x = e.GetPosition(PropsCenterGrid).X;
        double delta = x - _panelsResizeStartX;
        // Docked on the right: drag grip left (negative delta) = wider.
        // Docked on the left: drag grip right (positive delta) = wider.
        double next = _vm.IsLayersDockedLeft
            ? _panelsResizeStartWidth + delta
            : _panelsResizeStartWidth - delta;
        _vm.LayersPanelWidth = Math.Clamp(next, 280, 560);
        e.Handled = true;
    }

    private void OnPanelsResizeUp(object sender, MouseButtonEventArgs e)
    {
        FinishPanelsResize(sender as UIElement);
        e.Handled = true;
    }

    private void OnPanelsResizeLostCapture(object sender, MouseEventArgs e)
        => FinishPanelsResize(sender as UIElement);

    private void FinishPanelsResize(UIElement? handle)
    {
        if (!_panelsResizing) return;
        _panelsResizing = false;
        if (handle is not null && Mouse.Captured == handle)
            handle.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        _vm.CommitLayersPanelWidth();
        _ = NudgeViewerResizeAsync();
    }

    /// <summary>
    /// Primary action on the simple Props panel: build a one-prop-per-texture
    /// pack when that mode is on and extras exist; otherwise Convert / Add to Pack.
    /// </summary>
    private void OnSimplePrimaryAction(object sender, RoutedEventArgs e)
    {
        if (_vm.PackTextureVariants && _vm.HasExtraTextures)
        {
            OnBuildTexturePack(sender, e);
            return;
        }
        OnConvert(sender, e);
    }

    /// <summary>
    /// Compile the running prop-pack into a single FiveM resource.
    /// Reuses the same success-screen overlay the per-prop convert flow
    /// uses, then clears the staging directory so the next pack starts
    /// fresh.
    /// </summary>
    /// <summary>Photoshop-style export: every group with eye-on members
    /// builds into ONE pack resource; every eye-on loose layer exports as
    /// its own standalone resource. Eye-off layers are skipped.</summary>
    private async void OnFinalizePack(object sender, RoutedEventArgs e) => await ExportOutlinerLayers(onlyGroup: null);

    private async Task ExportOutlinerLayers(string? onlyGroup)
    {
        var session = _vm.PackSession;

        // Exporting one group by name → settings modal first: final pack
        // name + Regular vs Furniture (nolag_properties catalog).
        bool emitHousing = false;
        if (onlyGroup is not null)
        {
            int propCount = session.Entries.Count(en => en.IsIncluded &&
                    string.Equals(en.GroupName, onlyGroup, StringComparison.OrdinalIgnoreCase))
                + session.ConvertQueue.Count(q => q.IsPending && q.IsIncluded &&
                    string.Equals(q.GroupName, onlyGroup, StringComparison.OrdinalIgnoreCase));
            var dlg = new ExportPackDialog(onlyGroup, propCount) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            emitHousing = dlg.EmitHousing;
            if (!string.Equals(dlg.PackName, onlyGroup, StringComparison.OrdinalIgnoreCase) &&
                session.RenameGroup(onlyGroup, dlg.PackName) is { } applied)
            {
                onlyGroup = applied;
            }
        }

        // Dragging into a group defers the conversion — export is where
        // the pending rows actually convert. Drain them (scoped to the
        // exported group when one was named) before building.
        // Hidden groups sit this export out entirely — unless the user
        // explicitly exported that one group by name.
        var pendingForScope = session.ConvertQueue.Where(q => q.IsPending && q.IsIncluded &&
            (onlyGroup is null
                ? q.GroupName is null || !session.IsGroupHidden(q.GroupName)
                : string.Equals(q.GroupName, onlyGroup, StringComparison.OrdinalIgnoreCase))).ToList();
        if (pendingForScope.Count > 0)
        {
            if (session.IsQueueRunning || _vm.IsConverting)
            {
                _vm.StatusText = "Conversions are still running — export again when they finish.";
                return;
            }

            // The model may still be open in the viewport after its drag —
            // re-freeze its request now so scale/rotation/eye edits made
            // since the drop are what actually export.
            foreach (var q in pendingForScope.Where(q => q.RequestSnapshot is not null &&
                         string.Equals(q.SourcePath, _vm.SourcePath, StringComparison.OrdinalIgnoreCase)))
            {
                var liveExcludes = _vm.ModelParts.Where(p => !p.IsVisible).Select(p => p.OriginalName)
                    .Concat(_vm.DeletedPartNames)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                q.RequestSnapshot = BuildConvertRequest(q.AssetName, liveExcludes, routeToPack: true)
                    with { PackGroup = q.GroupName };
            }

            _packQueueFilter = pendingForScope.ToHashSet();
            _vm.StatusText = $"Converting {pendingForScope.Count} layer(s) for export…";
            await RunPackQueueAsync();
            if (pendingForScope.Any(q => q.IsFailed))
            {
                _vm.StatusText = "Some layers failed to convert — hover the failed rows for the error, then export again.";
                return;
            }
        }

        var included = session.Entries.Where(en => en.IsIncluded &&
            (en.GroupName is null || onlyGroup is not null || !session.IsGroupHidden(en.GroupName))).ToList();
        if (onlyGroup is not null)
            included = included.Where(en =>
                string.Equals(en.GroupName, onlyGroup, StringComparison.OrdinalIgnoreCase)).ToList();
        if (included.Count == 0)
        {
            _vm.StatusText = "Nothing to export — add layers first (hidden layers are skipped).";
            return;
        }

        // One build per group, one build per loose layer.
        var jobs = new List<(string Name, List<Services.PropPackEntry> Entries)>();
        foreach (var group in included
                     .Where(en => en.GroupName is not null)
                     .GroupBy(en => en.GroupName!, StringComparer.OrdinalIgnoreCase))
            jobs.Add((group.Key, group.ToList()));
        foreach (var loose in included.Where(en => en.GroupName is null))
            jobs.Add((loose.AssetName, new List<Services.PropPackEntry> { loose }));

        var results = new List<(string Name, FiveOS.Services.PropPackBuilder.BuildResult Result)>();
        foreach (var (name, entries) in jobs)
        {
            _vm.StatusText = $"Exporting '{name}' ({entries.Count} prop{(entries.Count == 1 ? "" : "s")})…";
            try
            {
                results.Add((name, FiveOS.Services.PropPackBuilder.Build(entries, name, _vm.LodDistVlow, emitHousing)));
            }
            catch (Exception ex)
            {
                results.Add((name, new FiveOS.Services.PropPackBuilder.BuildResult(
                    false, null, ex.Message, EngineRunner.OutputMode.SingleZip)));
            }
        }

        var failed = results.Where(r => !r.Result.Success || r.Result.ResultPath is null).ToList();
        var succeeded = results.Where(r => r.Result.Success && r.Result.ResultPath is not null).ToList();

        if (succeeded.Count == 0)
        {
            var first = failed.FirstOrDefault();
            CrashDialog.ShowEngineFailure(this, first.Result?.Error ?? "Export failed.", "");
            _vm.StatusText = $"✗ Export failed: {first.Result?.Error}";
            return;
        }

        var last = succeeded[^1].Result;
        _vm.ResultZipPath = last.ResultPath;
        _vm.ShowSuccessScreen = true;
        if (succeeded.Count == 1)
        {
            string sizePart = "";
            try
            {
                if (last.Mode == EngineRunner.OutputMode.SingleZip && File.Exists(last.ResultPath))
                    sizePart = " · " + FormatBytes(new FileInfo(last.ResultPath!).Length);
            }
            catch { /* size cosmetic */ }
            _vm.StatusText = last.Mode switch
            {
                EngineRunner.OutputMode.ServerShared   => $"✓ Merged into {last.ResultPath}",
                EngineRunner.OutputMode.ServerPerAsset => $"✓ '{Path.GetFileName(last.ResultPath)}' ready in server folder",
                _                                      => $"✓ Ready{sizePart} · {Path.GetFileName(last.ResultPath)}",
            };
        }
        else
        {
            _vm.StatusText = $"✓ Exported {succeeded.Count} resource(s): " +
                string.Join(", ", succeeded.Select(r => r.Name));
        }

        var warnings = succeeded.Where(r => r.Result.Warning is not null).ToList();
        if (warnings.Count > 0)
        {
            _vm.StatusText += " · ⚠ see warning";
            AppDialog.Show(string.Join("\n\n", warnings.Select(w => $"{w.Name}: {w.Result.Warning}")),
                "Delivered with warnings",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning, this);
        }

        if (failed.Count > 0)
        {
            AppDialog.Show(string.Join("\n", failed.Select(f => $"{f.Name}: {f.Result.Error}")),
                $"{failed.Count} export(s) failed — their layers stay staged",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning, this);
            // Keep failed layers staged for a retry; drop only what shipped.
            foreach (var job in jobs.Where(j => succeeded.Any(s =>
                         string.Equals(s.Name, j.Name, StringComparison.OrdinalIgnoreCase))))
            {
                foreach (var entry in job.Entries)
                    session.Remove(entry);
            }
            return;
        }

        if (onlyGroup is not null)
        {
            // Exported one group — drop just its layers, keep the rest staged.
            foreach (var entry in included)
                session.Remove(entry);
            _vm.PackSession.UngroupGroup(onlyGroup);
            return;
        }

        // Everything shipped (hidden layers too — they were deliberately
        // excluded, so they don't survive the export either): start clean.
        session.Clear();
        _vm.SelectedPackEntry = null;
        _vm.IsPackMode = false;
    }

    private void OnRemovePackEntry(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement fe && fe.DataContext is Services.PropPackEntry entry)
        {
            if (ReferenceEquals(_vm.SelectedPackEntry, entry))
                _vm.SelectedPackEntry = null;
            _vm.PackSession.Remove(entry);
        }
    }

    private void OnClearPack(object sender, RoutedEventArgs e)
    {
        _vm.SelectedPackEntry = null;
        _vm.PackSession.Clear();
    }

    private void OnPackTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // Legacy single-select path — unified outliner uses OnOutlinerSelectedItemChanged.
        _vm.SelectedPackEntry = e.NewValue is Services.PropPackTreeNode { Entry: { } entry }
            ? entry
            : null;
    }

    private static Services.PropPackTreeNode? FindOutlinerNode(DependencyObject? origin)
    {
        for (var d = origin; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement { DataContext: Services.PropPackTreeNode node })
                return node;
        }
        return null;
    }

    private void OnOutlinerPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var node = FindOutlinerNode(e.OriginalSource as DependencyObject);
        if (node is null)
        {
            // 3D-app outliner: clicking empty panel space deselects.
            _vm.ClearOutlinerSelection();
            return;
        }

        // Any layer can be dragged: staged props and queue rows move
        // between groups; the loaded model's row converts into the group
        // it's dropped on.
        if ((node.Entry is not null || node.QueueItem is not null ||
             node.IsWorking || node.IsPart) &&
            e.OriginalSource is not System.Windows.Controls.TextBox)
        {
            _outlinerDragNode = node;
            _outlinerDragStart = e.GetPosition(OutlinerTree);
            _outlinerDragging = false;
            Services.FosLogger.Info("drag", $"down on '{node.Name}' at {_outlinerDragStart.X:0},{_outlinerDragStart.Y:0} src={e.OriginalSource?.GetType().Name}");
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (ctrl || shift)
        {
            _vm.SetOutlinerSelection(new[] { node }, additive: true);
            e.Handled = true;
        }
        else
        {
            // Assert selection on every plain press — WPF's TreeView only
            // raises SelectedItemChanged when the item CHANGES, so after an
            // empty-space deselect a re-click on the same row would
            // otherwise never re-highlight.
            _vm.SetOutlinerSelection(new[] { node }, additive: false);
        }
    }

    private void OnOutlinerPreviewMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        var node = FindOutlinerNode(e.OriginalSource as DependencyObject);
        if (node is null) return;
        if (!_vm.SelectedOutlinerNodes.Contains(node))
            _vm.SetOutlinerSelection(new[] { node }, additive: false);
    }

    private void OnOutlinerSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not Services.PropPackTreeNode node) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) return;
        _vm.SetOutlinerSelection(new[] { node }, additive: false);
        // NOTE: queued-layer preview intentionally does NOT fire here —
        // selection changes mid-press, and the preview reflows the tree
        // (parked row hides, rows shift), which used to turn an ordinary
        // click into an accidental "drag out of group". The preview runs
        // from OnOutlinerMouseUp once the click completes cleanly.
    }

    private void TryPreviewQueuedNode(Services.PropPackTreeNode? node)
    {
        if (node is null) return;

        // Clicking the ACTIVE model's row (or its parts) puts the gizmo
        // back on it — park/preview swaps can leave it detached.
        if ((node.IsWorking || node.IsPart) && _vm.HasModel)
        {
            _ = AttachGizmoToCurrentAsync();
            return;
        }

        if (node.QueueItem is { IsPending: true } qi &&
            !_vm.IsConverting &&
            File.Exists(qi.SourcePath) &&
            !string.Equals(qi.SourcePath, _vm.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            ActivateQueuedLayer(qi);
        }
        else if (node.QueueItem is { IsPending: true } samePending &&
                 string.Equals(samePending.SourcePath, _vm.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            // Clicking the layer of the model that's ALREADY active —
            // just re-assert the gizmo on it.
            _ = AttachGizmoToCurrentAsync();
        }
    }

    /// <summary>3D-software selection: promote the clicked layer's PARKED
    /// object to active in place (no reload, transform untouched). Only
    /// when no parked object exists (fresh session) does it fall back to
    /// loading the layer's source file.</summary>
    private async void ActivateQueuedLayer(Services.PropPackQueueItem item)
    {
        // Whatever is active right now becomes/stays a layer + parks.
        await ParkActiveModelAsLayerAsync(item.SourcePath);

        if (Viewport?.CoreWebView2 != null)
        {
            var id = System.Text.Json.JsonSerializer.Serialize(item.AssetName);
            string? res = null;
            try
            {
                res = await Viewport.CoreWebView2.ExecuteScriptAsync(
                    $"window.activateParkedModel && window.activateParkedModel({id})");
            }
            catch (Exception ex)
            {
                Services.FosLogger.Warn("viewer", "activateParkedModel: " + ex.Message);
            }
            if (string.Equals(res, "true", StringComparison.OrdinalIgnoreCase))
            {
                _vm.SourcePath = item.SourcePath;
                _vm.PropName = item.AssetName;
                _vm.HasModel = true;
                _vm.IsModelLoading = false;
                // Parts + transform arrive from the viewer; refresh the
                // texture panel for the newly active source.
                _ = LoadBaseTextureGroupAsync(item.SourcePath);
                _vm.StatusText = $"'{item.AssetName}' selected.";
                return;
            }
        }

        // No parked object for this layer (e.g. app restarted) — load it.
        PreviewQueuedLayer(item);
    }

    private async Task AttachGizmoToCurrentAsync()
    {
        if (Viewport?.CoreWebView2 == null) return;
        try
        {
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                "window.attachGizmoToCurrent && window.attachGizmoToCurrent()");
        }
        catch (Exception ex)
        {
            Services.FosLogger.Warn("viewer", "attachGizmoToCurrent: " + ex.Message);
        }
    }

    /// <summary>Saved gizmo state to re-apply once the previewed layer's
    /// model finishes loading (the load pipeline resets transforms).</summary>
    private (double Sx, double Sy, double Sz, double Rx, double Ry, double Rz)? _pendingPreviewTransform;

    private void PreviewQueuedLayer(Services.PropPackQueueItem item)
    {
        _pendingPreviewTransform = null;
        if (item.RequestSnapshot is { } snap)
        {
            var rot = ParseCsvVec3(snap.RotationHint);
            _pendingPreviewTransform = (snap.ScaleHint.X, snap.ScaleHint.Y, snap.ScaleHint.Z, rot.X, rot.Y, rot.Z);
        }
        _vm.StatusText = $"Previewing '{item.AssetName}' — still queued in {(item.GroupName is null ? "the layer list" : $"'{item.GroupName}'")}.";
        TryLoad(item.SourcePath);
    }

    private static (double X, double Y, double Z) ParseCsvVec3(string? csv)
    {
        var parts = (csv ?? "").Split(',');
        double P(int i) => parts.Length > i && double.TryParse(parts[i],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        return (P(0), P(1), P(2));
    }

    private void OnOutlinerPartVisibilityToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var part = fe.Tag switch
        {
            MainViewModel.ModelPart p => p,
            Services.PropPackTreeNode { Part: { } mp } => mp,
            _ => null
        };
        if (part is null) return;
        part.IsVisible = !part.IsVisible;
    }

    private void OnOutlinerContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfContextMenu menu) return;
        menu.Items.Clear();

        var selected = _vm.SelectedOutlinerNodes.ToList();
        if (selected.Count == 0 && menu.PlacementTarget is FrameworkElement fe)
        {
            var n = FindOutlinerNode(fe);
            if (n != null) selected.Add(n);
        }
        if (selected.Count == 0) return;

        bool allParts = selected.All(n => n.IsPart);
        bool allProps = selected.All(n => n.IsProp);
        bool anyPack = selected.Any(n => n.IsPack || n.IsStream);
        bool anyWorking = selected.Any(n => n.IsWorking);

        if (anyWorking && !allParts)
        {
            var add = new WpfMenuItem { Header = "Add files to queue…" };
            add.Click += OnPackQueueAdd;
            menu.Items.Add(add);
            if (!anyPack) return;
        }

        if (allParts && selected.Count >= 1)
        {
            var part = selected[0].Part;
            if (part is null) return;

            void AddPartItem(string header, RoutedEventHandler handler, object? tag = null)
            {
                var mi = new WpfMenuItem { Header = header, Tag = tag ?? part };
                mi.Click += handler;
                menu.Items.Add(mi);
            }

            AddPartItem("Rename…", OnLayerRename);
            var tex = new WpfMenuItem { Header = "Textures" };
            var optTex = new WpfMenuItem { Header = "Optimize", Tag = part };
            optTex.Click += OnLayerOptimizeTextures;
            var chgTex = new WpfMenuItem { Header = "Change textures…", Tag = part };
            chgTex.Click += OnLayerChangeTextures;
            tex.Items.Add(optTex);
            tex.Items.Add(chgTex);
            menu.Items.Add(tex);
            AddPartItem("Optimize Mesh…", OnLayerOptimizeMesh);
            var mat = new WpfMenuItem { Header = "Material" };
            void AddMat(string h, RoutedEventHandler hnd)
            {
                var mi = new WpfMenuItem { Header = h, Tag = part };
                mi.Click += hnd;
                mat.Items.Add(mi);
            }
            AddMat("Standard", OnLayerMaterialStandard);
            AddMat("Glass", OnLayerMaterialGlass);
            AddMat("Emissive", OnLayerMaterialEmissive);
            AddMat("Emissive Strong", OnLayerMaterialEmissiveStrong);
            AddMat("Emissive Night", OnLayerMaterialEmissiveNight);
            AddMat("Metal", OnLayerMaterialMetal);
            AddMat("Cutout", OnLayerMaterialCutout);
            menu.Items.Add(mat);
            menu.Items.Add(new WpfSeparator());
            var split = new WpfMenuItem { Header = "Split into separate YDRs" };
            split.Click += OnSplitLayersToYdrs;
            menu.Items.Add(split);
            if (selected.Count == 1)
            {
                menu.Items.Add(new WpfSeparator());
                AddPartItem("Delete part", OnLayerDelete);
            }
            return;
        }

        if (allProps || selected.All(n => n.IsProp || n.IsQueueItem))
        {
            if (selected.Count == 1 && selected[0].Entry is { } soloEntry)
            {
                var renameLayer = new WpfMenuItem { Header = "Rename layer…" };
                renameLayer.Click += (_, _) => PromptRenameStagedLayer(soloEntry);
                menu.Items.Add(renameLayer);
            }

            var groupItem = new WpfMenuItem { Header = "Group into new pack\tCtrl+G" };
            groupItem.Click += (_, _) =>
            {
                var name = _vm.GroupSelectedOutlinerLayers();
                if (name is not null)
                    _vm.StatusText = $"Grouped into '{name}' — the group exports as one pack.";
            };
            menu.Items.Add(groupItem);

            if (selected.Any(n => n.Entry?.GroupName is not null || n.QueueItem?.GroupName is not null))
            {
                var loose = new WpfMenuItem { Header = "Move out of group" };
                loose.Click += (_, _) =>
                {
                    foreach (var n in selected)
                    {
                        if (n.Entry is { } en) _vm.PackSession.MoveEntryToGroup(en, null);
                        else if (n.QueueItem is { } qi) { qi.GroupName = null; }
                    }
                    _vm.PackSession.RebuildTree();
                };
                menu.Items.Add(loose);
            }

            var exportAll = new WpfMenuItem { Header = "Export all layers" };
            exportAll.Click += OnFinalizePack;
            menu.Items.Add(exportAll);

            menu.Items.Add(new WpfSeparator());
            var remove = new WpfMenuItem { Header = selected.Count == 1 ? "Remove layer" : $"Remove {selected.Count} layers" };
            remove.Click += OnOutlinerRemoveSelected;
            menu.Items.Add(remove);
            return;
        }

        if (anyPack || selected.Any(n => n.IsPack))
        {
            var groupNode = selected.FirstOrDefault(n => n.IsPack);
            var groupKey = groupNode?.GroupKey;

            var add = new WpfMenuItem { Header = groupKey is null ? "Add model(s)…" : $"Add model(s) into '{groupKey}'…" };
            add.Click += OnPackQueueAdd;
            menu.Items.Add(add);

            if (groupKey is not null)
            {
                var exportGroup = new WpfMenuItem
                {
                    Header = $"Export '{groupKey}' as pack",
                    IsEnabled = _vm.PackSession.Entries.Any(en => en.IsIncluded &&
                            string.Equals(en.GroupName, groupKey, StringComparison.OrdinalIgnoreCase))
                        || _vm.PackSession.ConvertQueue.Any(q => q.IsPending &&
                            string.Equals(q.GroupName, groupKey, StringComparison.OrdinalIgnoreCase)),
                };
                exportGroup.Click += async (_, _) => await ExportOutlinerLayers(onlyGroup: groupKey);
                menu.Items.Add(exportGroup);

                var rename = new WpfMenuItem { Header = "Rename group" };
                rename.Click += (_, _) => { if (groupNode is not null) groupNode.IsRenaming = true; };
                menu.Items.Add(rename);

                var ungroup = new WpfMenuItem { Header = "Ungroup (layers export separately)" };
                ungroup.Click += (_, _) => _vm.PackSession.UngroupGroup(groupKey);
                menu.Items.Add(ungroup);
            }

            var fin = new WpfMenuItem
            {
                Header = "Export all layers",
                IsEnabled = _vm.PackSession.HasEntries
            };
            fin.Click += OnFinalizePack;
            menu.Items.Add(fin);

            menu.Items.Add(new WpfSeparator());
            if (groupKey is not null)
            {
                var delGroup = new WpfMenuItem { Header = "Delete group + layers" };
                delGroup.Click += (_, _) => _vm.PackSession.RemoveGroupWithEntries(groupKey);
                menu.Items.Add(delGroup);
            }
            var clear = new WpfMenuItem { Header = "Clear all layers" };
            clear.Click += OnClearPack;
            menu.Items.Add(clear);
        }
    }

    private void OnQueueItemRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Services.PropPackQueueItem item }) return;
        _vm.PackSession.RemoveQueueItem(item);
    }

    private void OnQueueRemoveSelected(object sender, RoutedEventArgs e)
    {
        var items = _vm.SelectedOutlinerNodes
            .Where(n => n.QueueItem is not null)
            .Select(n => n.QueueItem!)
            .ToList();
        foreach (var item in items)
            _vm.PackSession.RemoveQueueItem(item);
    }

    private void OnQueueConvertSelected(object sender, RoutedEventArgs e)
    {
        var wanted = _vm.SelectedOutlinerNodes
            .Where(n => n.QueueItem is { IsPending: true })
            .Select(n => n.QueueItem!)
            .ToHashSet();
        if (wanted.Count == 0)
        {
            _vm.StatusText = "Select pending queue rows to convert.";
            return;
        }
        _packQueueFilter = wanted;
        OnPackQueueRun(sender, e);
    }

    private void OnOutlinerConvertSelectedQueue(object sender, RoutedEventArgs e)
    {
        // Kept for compatibility — queue rows now live inline in the tree.
        OnQueueConvertSelected(sender, e);
    }

    // ── Photoshop-style Layers interactions ─────────────────────────────

    /// <summary>Unified eye click: parts flip viewport visibility, staged
    /// layers flip pack inclusion, group rows toggle every child at once.</summary>
    private void OnOutlinerEyeToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Services.PropPackTreeNode node }) return;

        if (node.Part is { } part)
        {
            part.IsVisible = !part.IsVisible;
            return;
        }
        if (node.Entry is { } entry)
        {
            _vm.PackSession.SetEntryIncluded(entry, !entry.IsIncluded);
            return;
        }
        if (node.QueueItem is { } queued)
        {
            queued.IsIncluded = !queued.IsIncluded;
            _vm.PackSession.RebuildTree();
            return;
        }
        if (node.IsWorking)
        {
            bool on = !node.IsEyeOn;
            foreach (var p in _vm.ModelParts)
                p.IsVisible = on;
            node.IsEyeOn = on;
            return;
        }
        if (node.IsPack && node.GroupKey is { } group)
        {
            // Group eye = hide/show the whole group (export skips hidden
            // groups entirely); member eyes are preserved underneath.
            bool hide = node.IsEyeOn;
            _vm.PackSession.SetGroupHidden(group, hide);
            _vm.StatusText = hide
                ? $"'{group}' hidden — it won't export until you show it again."
                : $"'{group}' visible again.";
        }
    }

    private void OnOutlinerNewGroup(object sender, RoutedEventArgs e)
    {
        var name = _vm.CreateOutlinerGroup();
        BeginGroupRename(name);
        _vm.StatusText = $"Group '{name}' created — drag layers in, or drop model files straight onto it.";
    }

    // ── Pack menu (menu bar) ────────────────────────────────────────────

    /// <summary>Group the outliner selection targets, else the only group
    /// in the session (menu actions shouldn't require a click-first when
    /// there's nothing to disambiguate).</summary>
    private string? ResolvePackMenuGroup()
    {
        var fromSelection = _vm.TargetOutlinerGroup;
        if (fromSelection is not null) return fromSelection;
        return _vm.PackSession.Groups.Count == 1 ? _vm.PackSession.Groups[0] : null;
    }

    private void OnPackMenuOpened(object sender, RoutedEventArgs e)
    {
        bool hasGroup = ResolvePackMenuGroup() is not null;
        bool hasSelection = _vm.SelectedOutlinerNodes.Any(n => n.Entry is not null || n.QueueItem is not null);
        var groupKey = ResolvePackMenuGroup();
        // Pending (drag-dropped, converts-on-export) rows count as content.
        bool groupHasProps = groupKey is not null &&
            (_vm.PackSession.Entries.Any(en => en.IsIncluded &&
                 string.Equals(en.GroupName, groupKey, StringComparison.OrdinalIgnoreCase)) ||
             _vm.PackSession.ConvertQueue.Any(q => q.IsPending &&
                 string.Equals(q.GroupName, groupKey, StringComparison.OrdinalIgnoreCase)));

        PackMenuGroupSelected.IsEnabled = hasSelection;
        PackMenuRename.IsEnabled = hasGroup;
        PackMenuUngroup.IsEnabled = hasGroup;
        PackMenuExportGroup.IsEnabled = groupHasProps;
        PackMenuExportGroup.Header = groupKey is null
            ? "Export selected group"
            : $"Export '{groupKey}' as pack";
        PackMenuExportAll.IsEnabled = _vm.PackSession.Entries.Any(en => en.IsIncluded)
            || _vm.PackSession.ConvertQueue.Any(q => q.IsPending);
        PackMenuClear.IsEnabled = _vm.PackSession.HasOutlinerContent;
    }

    private void OnPackMenuGroupSelected(object sender, RoutedEventArgs e)
    {
        var name = _vm.GroupSelectedOutlinerLayers();
        _vm.StatusText = name is null
            ? "Select staged layers in the Layers panel first — then group them into a pack."
            : $"Grouped into '{name}' — the group exports as one pack.";
    }

    private void OnPackMenuRename(object sender, RoutedEventArgs e)
    {
        var group = ResolvePackMenuGroup();
        if (group is null)
        {
            _vm.StatusText = "Select a group in the Layers panel first.";
            return;
        }
        _vm.IsLayersPanelOpen = true;
        _vm.IsLayersPanelCollapsed = false;
        BeginGroupRename(group);
    }

    private void OnPackMenuUngroup(object sender, RoutedEventArgs e)
    {
        var group = ResolvePackMenuGroup();
        if (group is null)
        {
            _vm.StatusText = "Select a group in the Layers panel first.";
            return;
        }
        _vm.PackSession.UngroupGroup(group);
        _vm.StatusText = $"'{group}' ungrouped — its layers export separately now.";
    }

    private async void OnPackMenuExportGroup(object sender, RoutedEventArgs e)
    {
        var group = ResolvePackMenuGroup();
        if (group is null)
        {
            _vm.StatusText = "Select a group in the Layers panel first.";
            return;
        }
        await ExportOutlinerLayers(onlyGroup: group);
    }

    private void OnOutlinerAddModels(object sender, RoutedEventArgs e) => OnPackQueueAdd(sender, e);

    private void OnOutlinerDeleteSelected(object sender, RoutedEventArgs e)
    {
        var nodes = _vm.SelectedOutlinerNodes.ToList();
        if (nodes.Count == 0)
        {
            _vm.StatusText = "Select layers or groups to delete.";
            return;
        }
        int removed = 0;
        foreach (var node in nodes)
        {
            if (node.Entry is { } entry) { _vm.PackSession.Remove(entry); removed++; }
            else if (node.QueueItem is { } qi) { _vm.PackSession.RemoveQueueItem(qi); removed++; }
            else if (node.IsPack && node.GroupKey is { } group)
            {
                _vm.PackSession.RemoveGroupWithEntries(group);
                removed++;
            }
        }
        _vm.ClearOutlinerSelection();
        if (removed == 0)
            _vm.StatusText = "Nothing deletable selected — mesh parts are removed via right-click → Delete part.";
    }

    /// <summary>Kick the convert queue automatically — Photoshop flow has
    /// no Run button; dropped/added models just start converting.</summary>
    private void TryAutoRunPackQueue()
    {
        if (_vm.PackSession.CanRunConvertQueue && !_vm.IsConverting)
            OnPackQueueRun(this, new RoutedEventArgs());
    }

    // Row drag: move layers between groups / out to the top level.
    private Services.PropPackTreeNode? _outlinerDragNode;
    private Services.PropPackTreeNode? _outlinerDropNode;
    private Point _outlinerDragStart;
    private bool _outlinerDragging;
    /// <summary>True once the pointer actually hovered a different row than
    /// the drag source — a "drag" that never left its own row is a click,
    /// and must never move the layer (guards against tree reflow under a
    /// wobbly click).</summary>
    private bool _outlinerDragLeftSource;

    private void OnOutlinerMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _outlinerDragNode is null) return;

        var pos = e.GetPosition(OutlinerTree);
        if (!_outlinerDragging)
        {
            if (Math.Abs(pos.X - _outlinerDragStart.X) < 4 && Math.Abs(pos.Y - _outlinerDragStart.Y) < 4)
                return;
            _outlinerDragging = true;
            bool captured = Mouse.Capture(OutlinerTree, CaptureMode.SubTree);
            Mouse.OverrideCursor = Cursors.Hand;
            _vm.StatusText = _outlinerDragNode.IsWorking || _outlinerDragNode.IsPart
                ? $"Moving '{_outlinerDragNode.Name}' — drop on a group to add it to that pack."
                : $"Moving '{_outlinerDragNode.Name}' — drop on a group, or on empty space to keep it loose.";
            Services.FosLogger.Info("drag", $"start '{_outlinerDragNode.Name}' captured={captured}");
        }

        var over = FindOutlinerNode(OutlinerTree.InputHitTest(pos) as DependencyObject);
        if (over is not null && !ReferenceEquals(over, _outlinerDragNode))
            _outlinerDragLeftSource = true;
        var target = ResolveDropGroupNode(over);
        if (!ReferenceEquals(target, _outlinerDropNode))
        {
            if (_outlinerDropNode is not null) _outlinerDropNode.IsDropTarget = false;
            _outlinerDropNode = target;
            if (_outlinerDropNode is not null) _outlinerDropNode.IsDropTarget = true;
        }
        e.Handled = true;
    }

    private void OnOutlinerMouseUp(object sender, MouseButtonEventArgs e)
    {
        Services.FosLogger.Info("drag", $"up dragging={_outlinerDragging} leftSource={_outlinerDragLeftSource} node={_outlinerDragNode?.Name ?? "<null>"} target={_outlinerDropNode?.Name ?? "<null>"}");
        if (!_outlinerDragging)
        {
            var clicked = _outlinerDragNode;
            _outlinerDragNode = null;
            _outlinerDragLeftSource = false;
            // Clean click on a queued row → preview its model (safe to
            // reflow the tree now that the press is over).
            TryPreviewQueuedNode(clicked ?? FindOutlinerNode(e.OriginalSource as DependencyObject));
            return;
        }

        // Snapshot + clear the drag state BEFORE releasing capture:
        // Mouse.Capture(null) raises LostMouseCapture synchronously, and
        // the abandon-handler nulls these fields — releasing first made
        // every drop read empty state and silently no-op.
        var dragged = _outlinerDragNode;
        var target = _outlinerDropNode;
        bool leftSource = _outlinerDragLeftSource;
        _outlinerDragNode = null;
        _outlinerDropNode = null;
        _outlinerDragging = false;
        _outlinerDragLeftSource = false;
        if (target is not null) target.IsDropTarget = false;
        Mouse.Capture(null);
        Mouse.OverrideCursor = null;

        // A wobbly click that never hovered another row is NOT a move —
        // treat it as a click (preview if it was a queued row).
        if (!leftSource)
        {
            TryPreviewQueuedNode(dragged);
            e.Handled = true;
            return;
        }

        if (dragged is null) return;
        var group = target?.GroupKey; // null = drop at top level → loose layer

        if (dragged.Entry is { } entry)
        {
            _vm.PackSession.MoveEntryToGroup(entry, group);
            _vm.StatusText = group is null
                ? $"'{entry.SlotName}' is now a loose layer — it exports as its own resource."
                : $"'{entry.SlotName}' moved into '{group}'.";
        }
        else if (dragged.QueueItem is { } qi)
        {
            qi.GroupName = group;
            _vm.PackSession.RebuildTree();
        }
        else if (dragged.IsWorking || dragged.IsPart)
        {
            // The loaded model's row: dropping it on a group converts the
            // model straight into that pack. "Root" = the whole-model row:
            // the Working node, a single-mesh model's only row, or any row
            // sitting at the top level of the outliner.
            bool isRootRow = dragged.IsWorking
                || _vm.ModelParts.Count <= 1
                || _vm.OutlinerRoots.Contains(dragged);
            if (group is null || target is null)
            {
                Services.FosLogger.Info("drag", "model drop: no group target");
                _vm.StatusText = "Drop the model on a group to convert it into that pack.";
            }
            else if (!isRootRow)
            {
                Services.FosLogger.Info("drag", "model drop: child part row");
                _vm.StatusText = "Parts convert together with their model — drag the model's top row, or right-click → Split into separate YDRs.";
            }
            else if (_vm.IsConverting)
            {
                Services.FosLogger.Info("drag", "model drop: already converting");
                _vm.StatusText = "Finish the current convert first, then drop the model on the group.";
            }
            else if (string.IsNullOrEmpty(_vm.SourcePath) || !File.Exists(_vm.SourcePath))
            {
                Services.FosLogger.Info("drag", $"model drop: no source (path='{_vm.SourcePath}')");
                _vm.StatusText = "No source model loaded.";
            }
            else
            {
                // Photoshop feel: the row lands in the group INSTANTLY as a
                // queued layer; the conversion runs in the background on
                // that row (chip: queued → converting…). The request is
                // frozen now so gizmo scale/rotation, hidden parts and
                // materials are exactly what the user sees in the viewport.
                var stem = string.IsNullOrWhiteSpace(_vm.PropName)
                    ? Path.GetFileNameWithoutExtension(_vm.SourcePath)
                    : _vm.PropName.Trim();
                var excludeMeshes = _vm.ModelParts.Where(p => !p.IsVisible).Select(p => p.OriginalName)
                    .Concat(_vm.DeletedPartNames)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var snapshot = BuildConvertRequest(stem, excludeMeshes, routeToPack: true)
                    with { PackGroup = group };
                Services.FosLogger.Info("drag", $"model drop: enqueue '{stem}' into '{group}'");
                _vm.PackSession.EnqueueConvertSnapshot(_vm.SourcePath!, stem, group, snapshot);
                if (!_vm.IsPackMode) _vm.IsPackMode = true;
                // Deliberately NO conversion here — dragging is organizing.
                // The layer MOVES into the group in the panel, but the
                // model stays in the viewport (Photoshop: grouping a layer
                // never clears the canvas). Its settings are re-frozen at
                // export so continued viewport edits still count.
                _vm.RebuildOutlinerWorking();
                _vm.StatusText = $"'{stem}' moved into '{group}' — converts when you export the pack.";
            }
        }
        e.Handled = true;
    }

    /// <summary>Popup/alt-tab stole the capture mid-drag — abandon cleanly
    /// so the cursor and highlight don't stick.</summary>
    private void OnOutlinerLostCapture(object sender, MouseEventArgs e)
    {
        if (!_outlinerDragging) return;
        Mouse.OverrideCursor = null;
        if (_outlinerDropNode is not null) _outlinerDropNode.IsDropTarget = false;
        _outlinerDragNode = null;
        _outlinerDropNode = null;
        _outlinerDragging = false;
        _outlinerDragLeftSource = false;
    }

    /// <summary>Map whatever row the pointer is over to the group it
    /// represents: a group row targets itself; a member row targets its
    /// parent group; anything else (empty space, model rows) = loose.</summary>
    private Services.PropPackTreeNode? ResolveDropGroupNode(Services.PropPackTreeNode? over)
    {
        if (over is null) return null;
        if (over.IsPack) return over;
        var group = over.Entry?.GroupName ?? over.QueueItem?.GroupName;
        if (group is null) return null;
        return _vm.OutlinerRoots.FirstOrDefault(n =>
            n.IsPack && string.Equals(n.GroupKey, group, StringComparison.OrdinalIgnoreCase));
    }

    private void OnOutlinerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var node = FindOutlinerNode(e.OriginalSource as DependencyObject);
        if (node is null) return;
        if (node.IsPack)
        {
            node.IsRenaming = true;
            e.Handled = true;
        }
        else if (node.Entry is { } entry)
        {
            PromptRenameStagedLayer(entry);
            e.Handled = true;
        }
    }

    /// <summary>Rename a staged (converted) layer — renames the outliner
    /// row AND the exported resource/archetype, since finalize derives
    /// stream stems from SlotName and rewrites the YDR internal name.</summary>
    private void PromptRenameStagedLayer(Services.PropPackEntry entry)
    {
        var newName = ShowInputDialog("Rename layer", "New name:", entry.SlotName);
        if (string.IsNullOrWhiteSpace(newName)) return;
        if (_vm.PackSession.RenameEntry(entry, newName))
            _vm.StatusText = $"Renamed to '{entry.SlotName}' — the exported prop uses this name.";
        else
            _vm.StatusText = "Couldn't rename — name is empty or already taken.";
    }

    private void OnOutlinerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.G && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            var name = _vm.GroupSelectedOutlinerLayers();
            _vm.StatusText = name is null
                ? "Select staged layers first, then Ctrl+G groups them into a pack."
                : $"Grouped into '{name}' — the group exports as one pack.";
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            OnOutlinerDeleteSelected(sender, e);
            e.Handled = true;
        }
    }

    private void BeginGroupRename(string groupName)
    {
        var node = _vm.OutlinerRoots.FirstOrDefault(n =>
            n.IsPack && string.Equals(n.GroupKey, groupName, StringComparison.OrdinalIgnoreCase));
        if (node is not null) node.IsRenaming = true;
    }

    private void OnGroupRenameBoxVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb || e.NewValue is not true) return;
        FocusRenameBox(tb);
    }

    /// <summary>Rows materialize asynchronously, so a rename box created
    /// already-visible never fires IsVisibleChanged — hook Loaded too, or
    /// keyboard focus stays on the "New group" button and Enter spawns
    /// another group instead of committing the name.</summary>
    private void OnGroupRenameBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox { IsVisible: true, Tag: Services.PropPackTreeNode { IsRenaming: true } } tb)
            FocusRenameBox(tb);
    }

    private static void FocusRenameBox(System.Windows.Controls.TextBox tb)
    {
        tb.Dispatcher.BeginInvoke(new Action(() =>
        {
            tb.Focus();
            Keyboard.Focus(tb);
            tb.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnGroupRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox { Tag: Services.PropPackTreeNode node } tb) return;
        if (e.Key == Key.Enter)
        {
            CommitGroupRename(tb, node);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            node.IsRenaming = false;
            e.Handled = true;
        }
    }

    private void OnGroupRenameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox { Tag: Services.PropPackTreeNode { IsRenaming: true } node } tb)
            CommitGroupRename(tb, node);
    }

    private void CommitGroupRename(System.Windows.Controls.TextBox tb, Services.PropPackTreeNode node)
    {
        node.IsRenaming = false;
        if (node.GroupKey is not { } oldName) return;
        var newName = tb.Text?.Trim() ?? "";
        if (newName.Length == 0 || string.Equals(newName, oldName, StringComparison.Ordinal)) return;
        if (_vm.PackSession.RenameGroup(oldName, newName) is null)
            _vm.StatusText = $"Couldn't rename '{oldName}' — that name is empty or already taken.";
    }

    // Explorer file drop → queue into the group under the cursor (auto-runs).
    private void OnOutlinerFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnOutlinerFileDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        var over = FindOutlinerNode(OutlinerTree.InputHitTest(e.GetPosition(OutlinerTree)) as DependencyObject);
        var group = ResolveDropGroupNode(over)?.GroupKey;

        int added = _vm.PackSession.EnqueueConvertPaths(files, group);
        _vm.StatusText = added == 0
            ? "Nothing queued (unsupported, missing, or already listed)."
            : group is null
                ? $"Queued {added} file(s) as loose layers — converting…"
                : $"Queued {added} file(s) into '{group}' — converting…";
        e.Handled = true;
        TryAutoRunPackQueue();
    }

    private void OnOutlinerRemoveSelected(object sender, RoutedEventArgs e)
    {
        var nodes = _vm.SelectedOutlinerNodes.ToList();
        foreach (var node in nodes)
        {
            if (node.Entry is { } entry)
                _vm.PackSession.Remove(entry);
            else if (node.QueueItem is { } qi)
                _vm.PackSession.RemoveQueueItem(qi);
        }
        _vm.ClearOutlinerSelection();
    }

    /// <summary>When set, <see cref="OnPackQueueRun"/> only converts these pending items.</summary>
    private HashSet<Services.PropPackQueueItem>? _packQueueFilter;

    private void OnPackTreeRemoveNode(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Services.PropPackTreeNode node }) return;
        if (node.Entry is { } entry)
        {
            if (ReferenceEquals(_vm.SelectedPackEntry, entry))
                _vm.SelectedPackEntry = null;
            _vm.PackSession.Remove(entry);
        }
        else if (node.QueueItem is { } qi)
        {
            _vm.PackSession.RemoveQueueItem(qi);
        }
    }

    private void OnPackQueueAdd(object sender, RoutedEventArgs e)
    {
        if (_vm.PackSession.IsQueueRunning) return;
        if (!_vm.IsPackMode) _vm.IsPackMode = true;

        var dlg = new OpenFileDialog
        {
            Title = "Add props to convert queue",
            Multiselect = true,
            Filter = "3D models (*.obj;*.glb;*.gltf;*.fbx;*.dae;*.ply;*.stl)|" +
                     "*.obj;*.glb;*.gltf;*.fbx;*.dae;*.ply;*.stl|" +
                     "All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true || dlg.FileNames.Length == 0) return;

        // Land in the group selected in the outliner (loose when none) and
        // start converting right away — the Photoshop flow has no Run button.
        var group = _vm.TargetOutlinerGroup;
        int added = _vm.PackSession.EnqueueConvertPaths(dlg.FileNames, group);
        _vm.StatusText = added == 0
            ? "Nothing new queued (unsupported, missing, or already listed)."
            : group is null
                ? $"Queued {added} file(s) as loose layers — converting…"
                : $"Queued {added} file(s) into '{group}' — converting…";
        TryAutoRunPackQueue();
    }

    private async void OnPackQueueRun(object sender, RoutedEventArgs e) => await RunPackQueueAsync();

    private async Task RunPackQueueAsync()
    {
        var session = _vm.PackSession;
        if (!session.CanRunConvertQueue) return;
        if (_vm.IsConverting)
        {
            _vm.StatusText = "Finish the current convert first, then run the pack queue.";
            return;
        }
        if (!_vm.IsPackMode) _vm.IsPackMode = true;

        var filter = _packQueueFilter;
        _packQueueFilter = null;
        var snapshot = session.ConvertQueue
            .Where(q => q.IsPending && (filter is null || filter.Contains(q)))
            .ToList();
        if (snapshot.Count == 0) return;

        session.IsQueueRunning = true;
        _vm.IsConverting = true;
        var runner = new EngineRunner();
        _convertCts = new System.Threading.CancellationTokenSource();
        var token = _convertCts.Token;
        int done = 0, failed = 0;

        try
        {
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (token.IsCancellationRequested) break;

                var item = snapshot[i];
                item.Status = "Converting";
                item.Error = null;
                session.RebuildTree();
                _convertStatusContext = $"{item.AssetName} → {item.GroupName ?? "layers"}: ";
                _vm.StatusText = $"Converting {i + 1}/{snapshot.Count} — {item.SourceName}";

                // Drag-dropped models carry a frozen request (gizmo, parts,
                // materials as seen at drop time); plain file drops build
                // the default one below.
                var req = item.RequestSnapshot is { } snap
                    ? snap with
                      {
                          AssetName = item.AssetName,
                          RouteToPack = true,
                          PackGroup = item.GroupName,
                      }
                    : new EngineRunner.ConvertRequest(
                    SourcePath: item.SourcePath,
                    AssetName: item.AssetName,
                    Up: EngineRunner.UpAxis.Auto,
                    CollisionMaterial: string.IsNullOrWhiteSpace(_vm.CollisionMaterial) ? "CONCRETE" : _vm.CollisionMaterial,
                    IncludeCollision: _vm.IncludeCollision,
                    EmbedCollision: _vm.EmbedCollision,
                    IncludeYtyp: _vm.IncludeYtyp,
                    ExtractTextures: _vm.ExtractTextures,
                    ScaleHint: (1d, 1d, 1d),
                    PositionHint: "0,0,0",
                    RotationHint: "0,0,0",
                    ExcludeMeshes: null,
                    GenerateLods: _vm.GenerateLods,
                    LodDistHigh: _vm.LodDistHigh,
                    LodDistMed: _vm.LodDistMed,
                    LodDistLow: _vm.LodDistLow,
                    LodDistVlow: _vm.LodDistVlow,
                    RouteToPack: true,
                    PackGroup: item.GroupName,
                    BreakableGlass: _vm.BreakableGlass,
                    GlassOpacity: _vm.GlassOpacity);

                EngineRunner.ConvertOutcome outcome;
                try
                {
                    outcome = await runner.RunAsync(req, onLog: OnEngineLog, cancel: token);
                }
                catch (OperationCanceledException)
                {
                    item.Status = "Failed";
                    item.Error = "Cancelled.";
                    session.RebuildTree();
                    break;
                }
                catch (Exception ex)
                {
                    item.Status = "Failed";
                    item.Error = ex.Message;
                    failed++;
                    session.RebuildTree();
                    continue;
                }

                if (outcome.Success)
                {
                    item.Status = "Done";
                    item.Error = null;
                    done++;
                    // Drop finished rows so the outliner stays pack → stream → props.
                    session.ConvertQueue.Remove(item);
                }
                else
                {
                    item.Status = "Failed";
                    item.Error = outcome.Error ?? "Engine reported failure.";
                    failed++;
                    session.RebuildTree();
                }
            }
        }
        finally
        {
            _convertCts?.Dispose();
            _convertCts = null;
            _convertStatusContext = null;
            session.IsQueueRunning = false;
            _vm.IsConverting = false;
            session.NotifyQueueFinished();
        }

        _vm.StatusText = failed == 0
            ? $"✓ {done} layer(s) converted — right-click to export, or keep building."
            : $"Converted {done}, {failed} failed — failed rows stay in the outliner (hover for the error).";

        // Files dropped while this run was busy queue up behind it —
        // keep draining until the outliner has no pending rows left.
        TryAutoRunPackQueue();
    }

    /// <summary>
    /// Drop the currently loaded model and return the 3D-to-Props tab to
    /// its empty drop-zone state. Shared by the success-screen "Convert
    /// another" button and the SOURCE card's X button.
    /// </summary>
    private void ClearLoadedModel()
    {
        // Reset everything that survives a successful conversion. The
        // viewport's Visibility is bound to IsViewportVisible (HasModel &&
        // !ShowSuccessScreen), so flipping these two flags is enough — no
        // need to poke ViewportRoot.Visibility directly (doing so would
        // overwrite the binding and leave us with stale state next time).
        _vm.ShowSuccessScreen = false;
        _vm.HasModel = false;
        _vm.IsModelLoading = false;
        _vm.SourcePath = null;
        _vm.PropName = string.Empty;
        _vm.ResultZipPath = null;
        _vm.Verts = 0;
        _vm.Tris = 0;
        ClearPartDiffuseOverrides();
        ClearModelTexturePreview();
        _ = ClearViewerAsync();
        _vm.StatusText = "Ready — drop a 3D file or use File → Open";
    }

    // ─────────────── Loading models ───────────────

    private void BrowseForModel()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open 3D model",
            Filter = "3D models (*.obj;*.glb;*.gltf;*.fbx;*.dae;*.ply;*.stl)|" +
                     "*.obj;*.glb;*.gltf;*.fbx;*.dae;*.ply;*.stl|" +
                     "All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            TryLoad(dlg.FileName);
    }

    private async void TryLoad(string path, string? displayNameOverride = null)
    {
        Services.FosLogger.Info("load", $"TryLoad start: {path}");
        if (!File.Exists(path))
        {
            Services.FosLogger.Warn("load", $"file not found: {path}");
            _vm.StatusText = $"File not found: {path}";
            return;
        }
        if (!IsSupported(path))
        {
            Services.FosLogger.Warn("load", $"unsupported format: {Path.GetExtension(path)}");
            _vm.StatusText = $"Unsupported format: {Path.GetExtension(path)}";
            return;
        }

        // Record real, user-picked opens for the Welcome screen's Recent list.
        // Skip generated/temp paths (AI, Sketchfab, optimize round-trips) so the
        // list only shows files the user actually chose.
        try
        {
            if (!path.StartsWith(Path.GetTempPath(), System.StringComparison.OrdinalIgnoreCase))
                Services.UserSettings.AddRecentFile(path);
        }
        catch (Exception ex) { Services.FosLogger.Warn("load", "recent-file record failed", ex); }

        var originalNameNoExt = Path.GetFileNameWithoutExtension(path);

        // Multi-model viewport: importing must NEVER delete the model
        // that's already loaded. Whatever is currently active becomes a
        // layer (auto-enqueued loose if the user hadn't grouped it) and
        // its viewer object parks in place. If the model being LOADED is
        // itself a parked layer (preview click), remove its ghost so it
        // doesn't render twice.
        await ParkActiveModelAsLayerAsync(path);
        var pendingSelf = _vm.PackSession.ConvertQueue.FirstOrDefault(q =>
            string.Equals(q.SourcePath, path, StringComparison.OrdinalIgnoreCase));
        if (pendingSelf is not null)
            await RemoveParkedInstanceAsync(pendingSelf.AssetName);

        _vm.SourcePath = path;
        // Sketchfab archives all extract to "scene.gltf" — prefer the real
        // model title when the caller knows it.
        _vm.PropName = SanitizeAssetName(string.IsNullOrWhiteSpace(displayNameOverride)
            ? originalNameNoExt
            : displayNameOverride);
        _vm.HasModel = true;
        _vm.IsModelLoading = true;
        _vm.Verts = 0;
        _vm.Tris = 0;
        ClearPartDiffuseOverrides();
        ClearModelTexturePreview();
        // Reset the optimization-health banner for the new model so a hint
        // dismissed on a previous file doesn't carry over.
        _vm.OptimizationSeverity = Services.MeshThresholds.Severity.Ok;
        _vm.OptimizationHintDismissed = false;

        // Pull source diffuse maps into one "Base Texture" library row.
        _ = LoadBaseTextureGroupAsync(path);

        // Auto-switch Assets → Animated when the file ships animation clips.
        var hasAnim = await Task.Run(() => SourceFileHasAnimation(path));
        if (hasAnim)
        {
            _vm.ExportMode = ExportMode.Prop;
            _vm.AnimatedProp = true;
            _vm.EnsureDefaultPropAnimKeys();
            _vm.ActiveView = AppView.AnimatedProps;
        }

        // Surface the file size in the status so users can mentally judge
        // how long the load + viewer parse will take. "Loading X..." with
        // no other info reads as "stuck" on big files.
        long fileSize = 0;
        try { fileSize = new FileInfo(path).Length; }
        catch (Exception ex) { Services.FosLogger.Warn("load", "size probe failed", ex); }
        _vm.StatusText = fileSize > 0
            ? $"Loading {Path.GetFileName(path)} ({fileSize / (1024.0 * 1024):F1} MB)..."
            : $"Loading {Path.GetFileName(path)}...";

        try
        {
            // First-time model load triggers WebView2 init. Subsequent calls
            // resolve immediately because EnsureWebViewAsync caches the task.
            await EnsureWebViewAsync();
            // Heavy FBX imports drag a sibling directory of textures (often
            // hundreds of MB) into the session dir. Run that copy off the UI
            // thread so the skeleton overlay paints and the window stays
            // responsive instead of going "Not Responding".
            var copiedRel = await Task.Run(() => StageModelInSessionDir(path));
            var url = $"https://viewer.local/{copiedRel.Replace('\\', '/')}";
            if (!_viewerReady)
                _pendingModelUrl = url;
            else
                await LoadInViewerAsync(url);
        }
        catch (Exception ex)
        {
            _vm.HasModel = false;
            _vm.IsModelLoading = false;
            _vm.StatusText = $"Failed to stage model: {ex.Message}";
        }
    }

    /// <summary>True when Assimp finds at least one animation clip — used
    /// to auto-open the Assets → Animated tab on load.</summary>
    private static bool SourceFileHasAnimation(string path)
    {
        try
        {
            using var ctx = new Assimp.AssimpContext();
            var scene = ctx.ImportFile(path, Assimp.PostProcessSteps.None);
            return scene is not null && scene.HasAnimations && scene.AnimationCount > 0;
        }
        catch (Exception ex)
        {
            Services.FosLogger.Warn("load", "animation probe failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Copy the model file (plus any sibling files needed for textures) into
    /// the viewer session dir under a stable subfolder, and return the
    /// relative URL path of the model.
    /// </summary>
    private string StageModelInSessionDir(string path)
    {
        if (string.IsNullOrEmpty(_viewerSessionDir))
            throw new InvalidOperationException("Viewer not initialized.");

        // Wipe the previous model staging.
        var stage = Path.Combine(_viewerSessionDir, "model");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        Directory.CreateDirectory(stage);

        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        var fileName = Path.GetFileName(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // Copy the model file itself.
        File.Copy(path, Path.Combine(stage, fileName), overwrite: true);

        // For formats that may reference external files (OBJ→.mtl + textures,
        // glTF .gltf→.bin + .png/.jpg, FBX/DAE→external textures), copy
        // companion files from the source directory so relative refs
        // resolve. Allowlist to texture + format-specific siblings, flat
        // (no recursion) — otherwise dropping a model on Desktop drags the
        // entire Desktop tree into the session dir before the viewer ever
        // sees the mesh. GLB is fully self-contained and doesn't need this.
        var needsSiblings = ext is ".gltf" or ".obj" or ".dae" or ".fbx";
        if (needsSiblings)
        {
            var companionExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".png", ".jpg", ".jpeg", ".tga", ".dds", ".tif", ".tiff",
                ".bmp", ".ktx", ".ktx2", ".exr", ".hdr", ".webp", ".gif",
            };
            if (ext == ".obj") companionExts.Add(".mtl");
            if (ext == ".gltf") companionExts.Add(".bin");

            foreach (var sib in Directory.EnumerateFiles(sourceDir))
            {
                var sibExt = Path.GetExtension(sib);
                if (!companionExts.Contains(sibExt)) continue;
                var target = Path.Combine(stage, Path.GetFileName(sib));
                if (File.Exists(target)) continue; // model file already copied above
                File.Copy(sib, target, overwrite: true);
            }
        }

        // Artists often put textures in a "textures/" folder next to the
        // model file, or as a sibling of the model directory. Copy both
        // layouts so relative refs and bare filenames resolve in the viewer.
        foreach (var sub in new[] { "textures", "Textures", "tex", "maps" })
        {
            var besideModel = Path.Combine(sourceDir, sub);
            if (Directory.Exists(besideModel))
            {
                var dstFull = Path.GetFullPath(Path.Combine(stage, sub));
                if (dstFull.StartsWith(_viewerSessionDir, StringComparison.OrdinalIgnoreCase))
                    CopyDirectory(besideModel, dstFull, overwrite: true);
            }
        }
        var parent = Directory.GetParent(sourceDir)?.FullName;
        if (parent != null)
        {
            foreach (var sub in new[] { "textures", "Textures", "tex", "maps" })
            {
                var src = Path.Combine(parent, sub);
                if (Directory.Exists(src))
                {
                    var dst = Path.Combine(stage, "..", sub);  // mirror sibling layout
                    var dstFull = Path.GetFullPath(dst);
                    // Keep texture folder INSIDE the session dir for security.
                    if (dstFull.StartsWith(_viewerSessionDir, StringComparison.OrdinalIgnoreCase))
                        CopyDirectory(src, dstFull, overwrite: true);
                }
            }
        }

        return $"model/{fileName}";
    }

    private static void CopyDirectory(string source, string dest, bool overwrite = false)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(dest, Path.GetFileName(file));
            File.Copy(file, target, overwrite);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var target = Path.Combine(dest, Path.GetFileName(dir));
            CopyDirectory(dir, target, overwrite);
        }
    }

    /// <summary>Turn the CURRENT model into a persistent layer before a new
    /// import takes the active slot: parks its viewer object in place and —
    /// when the user hadn't dragged it into a group yet — auto-enqueues it
    /// as a loose layer so nothing the user imported ever silently
    /// disappears.</summary>
    private async Task ParkActiveModelAsLayerAsync(string incomingPath)
    {
        if (Viewport?.CoreWebView2 == null) return;
        if (string.IsNullOrEmpty(_vm.SourcePath) || !_vm.HasModel) return;
        // Re-loading the same file (layer preview / refresh) replaces itself.
        if (string.Equals(_vm.SourcePath, incomingPath, StringComparison.OrdinalIgnoreCase)) return;

        var item = _vm.PackSession.ConvertQueue.FirstOrDefault(q => q.IsPending &&
            string.Equals(q.SourcePath, _vm.SourcePath, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            var stem = string.IsNullOrWhiteSpace(_vm.PropName)
                ? Path.GetFileNameWithoutExtension(_vm.SourcePath)
                : _vm.PropName.Trim();
            var excludeMeshes = _vm.ModelParts.Where(p => !p.IsVisible).Select(p => p.OriginalName)
                .Concat(_vm.DeletedPartNames)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var snapshot = BuildConvertRequest(stem, excludeMeshes, routeToPack: true);
            item = _vm.PackSession.EnqueueConvertSnapshot(_vm.SourcePath!, stem, null, snapshot);
            _vm.StatusText = $"'{stem}' kept as a layer.";
        }

        var id = System.Text.Json.JsonSerializer.Serialize(item.AssetName);
        try
        {
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.parkCurrentModel && window.parkCurrentModel({id})");
        }
        catch (Exception ex)
        {
            Services.FosLogger.Warn("viewer", "parkCurrentModel: " + ex.Message);
        }
    }

    private async Task RemoveParkedInstanceAsync(string assetName)
    {
        if (Viewport?.CoreWebView2 == null) return;
        var id = System.Text.Json.JsonSerializer.Serialize(assetName);
        try
        {
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.removeParkedModel && window.removeParkedModel({id})");
        }
        catch (Exception ex)
        {
            Services.FosLogger.Warn("viewer", "removeParkedModel: " + ex.Message);
        }
    }

    private async Task NudgeViewerResizeAsync()
    {
        if (!_viewerReady || Viewport?.CoreWebView2 == null) return;
        try
        {
            // Let WPF finish the layout pass that opened/closed the pack dock.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                "window.resizeViewer && window.resizeViewer(true)");
        }
        catch { /* viewer may be mid-navigate */ }
    }

    private async Task LoadInViewerAsync(string url)
    {
        try
        {
            // Single-quote the URL inside the JS call to keep escaping simple.
            var safe = url.Replace("\\", "\\\\").Replace("'", "\\'");
            await Viewport.CoreWebView2.ExecuteScriptAsync($"window.loadModel('{safe}')");
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Failed to send model to viewer: {ex.Message}";
        }
    }

    private async Task ClearViewerAsync()
    {
        try
        {
            if (Viewport?.CoreWebView2 != null)
                await Viewport.CoreWebView2.ExecuteScriptAsync("window.clearModel && window.clearModel()");
        }
        catch { /* swallow */ }
    }

    // ─────────────── Reference ped (scale comparison) ───────────────

    /// <summary>
    /// Stage the reference model into &lt;sessionDir&gt;/reference/ and tell
    /// the viewer to (re)load it. Toggles into a clearReference call when
    /// the user has the feature off or no usable file is configured.
    /// Best-effort — failures surface to the status bar but don't throw.
    /// </summary>
    public async Task LoadReferenceAsync()
    {
        if (!_viewerReady || Viewport?.CoreWebView2 == null) return;
        if (_viewerSessionDir == null) return;

        try
        {
            if (!_vm.ShowReferencePed)
            {
                await Viewport.CoreWebView2.ExecuteScriptAsync(
                    "window.clearReference && window.clearReference()");
                return;
            }

            var path = _vm.ReferenceModelPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                await Viewport.CoreWebView2.ExecuteScriptAsync(
                    "window.clearReference && window.clearReference()");
                return;
            }

            var refDir = Path.Combine(_viewerSessionDir, "reference");
            if (Directory.Exists(refDir)) Directory.Delete(refDir, true);
            Directory.CreateDirectory(refDir);

            var fileName = Path.GetFileName(path);
            File.Copy(path, Path.Combine(refDir, fileName), overwrite: true);

            // Mirror a sibling textures/ folder if one is present right next
            // to the reference (common in Sketchfab / extracted-archive
            // layouts). We deliberately don't recurse the parent dir — the
            // user's reference often lives in Downloads, where a generic
            // recursive copy would balloon disk and waste seconds.
            var srcDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            foreach (var sub in new[] { "textures", "Textures", "tex", "maps" })
            {
                var src = Path.Combine(srcDir, sub);
                if (Directory.Exists(src))
                    CopyDirectory(src, Path.Combine(refDir, sub), overwrite: true);
            }

            // For .gltf specifically, the .bin sidecar must come along too.
            if (Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase))
            {
                var binCandidate = Path.ChangeExtension(path, ".bin");
                if (File.Exists(binCandidate))
                    File.Copy(binCandidate, Path.Combine(refDir, Path.GetFileName(binCandidate)), overwrite: true);
            }

            var url = $"https://viewer.local/reference/{fileName.Replace('\\', '/')}";
            var safe = url.Replace("\\", "\\\\").Replace("'", "\\'");
            await Viewport.CoreWebView2.ExecuteScriptAsync($"window.loadReference('{safe}')");
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Reference load failed: {ex.Message}";
        }
    }

    /// <summary>Toggle reference visibility without re-loading. Cheap call —
    /// just flips an object's <c>visible</c> flag in-viewer.</summary>
    public async Task SetReferenceVisibleAsync(bool visible)
    {
        if (Viewport?.CoreWebView2 == null) return;
        try
        {
            var arg = visible ? "true" : "false";
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.setReferenceVisible && window.setReferenceVisible({arg})");
        }
        catch { /* swallow */ }
    }

    /// <summary>Push the glass Appearance slider (0 = see-through, 1 =
    /// reflective) into the viewer so glass parts preview the same look the
    /// export will bake. Safe to call before the viewer is ready — it just
    /// no-ops; the value is re-pushed on the next model load.</summary>
    private async Task SetGlassAppearanceAsync(double value)
    {
        if (!_viewerReady || Viewport?.CoreWebView2 == null) return;
        try
        {
            var v = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.setGlassAppearance && window.setGlassAppearance({v})");
        }
        catch { /* swallow */ }
    }

    // ─────────────── Layers panel (model parts) ───────────────

    /// <summary>
    /// Replace the VM's ModelParts collection from the JSON the viewer
    /// posted after a load. Each item subscribes to its IsVisible so
    /// later toggles flow straight back into the viewer.
    /// </summary>
    private void UpdateModelPartsFromMessage(string json)
    {
        // Tear down old subscriptions before rebuilding.
        foreach (var p in _vm.ModelParts) p.PropertyChanged -= OnModelPartChanged;
        _vm.ModelParts.Clear();
        // Each model load resets the "deleted" tracking — a fresh model has
        // no deleted parts, and stale names from a prior load would leak
        // into the next ExcludeMeshes if we don't clear here.
        _vm.DeletedPartNames.Clear();

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("parts", out var arr) ||
                arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                return;

            foreach (var el in arr.EnumerateArray())
            {
                string name = el.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String
                    ? n.GetString() ?? "(unnamed)"
                    : "(unnamed)";
                bool visible = !el.TryGetProperty("visible", out var v) || v.ValueKind != System.Text.Json.JsonValueKind.False;
                var part = new MainViewModel.ModelPart(name, visible);
                // Leave MaterialPreset on Standard — do not auto-pick Glass
                // from the viewer's looksLikeGlass hint. Preview can still
                // look glassy; export glass is an explicit Layers choice.
                part.PropertyChanged += OnModelPartChanged;
                _vm.ModelParts.Add(part);
            }
        }
        catch
        {
            // Malformed message — leave the panel empty.
        }
    }

    /// <summary>
    /// Incremental merge of a <c>parts-sync</c> message (viewer split a mesh
    /// into its own layer). Unlike <see cref="UpdateModelPartsFromMessage"/>
    /// this preserves the state the user already set on existing rows —
    /// renames, material presets, visibility — and never touches
    /// <see cref="MainViewModel.DeletedPartNames"/>. It adds rows for parts
    /// that are new (the freshly-separated mesh), drops rows whose part no
    /// longer exists in the model (an emptied parent group), and skips any
    /// incoming part the user has already deleted so a re-sync can't
    /// resurrect it.
    /// </summary>
    private void SyncModelPartsFromMessage(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("parts", out var arr) ||
                arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                return;

            // Existing rows keyed by the name the viewer addresses them with.
            var existing = new Dictionary<string, MainViewModel.ModelPart>(StringComparer.Ordinal);
            foreach (var p in _vm.ModelParts) existing[p.OriginalName] = p;

            var incomingNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var el in arr.EnumerateArray())
            {
                string name = el.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String
                    ? n.GetString() ?? "(unnamed)"
                    : "(unnamed)";
                incomingNames.Add(name);

                // Already deleted by the user — a merge must not bring it back.
                if (_vm.DeletedPartNames.Contains(name)) continue;
                // Already shown — keep the row (and all its user edits) as-is.
                if (existing.ContainsKey(name)) continue;

                bool visible = !el.TryGetProperty("visible", out var v) || v.ValueKind != System.Text.Json.JsonValueKind.False;
                var part = new MainViewModel.ModelPart(name, visible);
                // Same as load — Standard by default; user tags Glass in Layers.
                part.PropertyChanged += OnModelPartChanged;
                _vm.ModelParts.Add(part);
            }

            // Prune rows whose part vanished from the model (e.g. a parent
            // group that lost its only mesh to a Separate). Deleted parts are
            // already out of ModelParts, so this won't touch them.
            for (int i = _vm.ModelParts.Count - 1; i >= 0; i--)
            {
                var p = _vm.ModelParts[i];
                if (!incomingNames.Contains(p.OriginalName))
                {
                    p.PropertyChanged -= OnModelPartChanged;
                    _vm.ModelParts.RemoveAt(i);
                }
            }
        }
        catch
        {
            // Malformed message — leave the panel as it was.
        }
    }

    /// <summary>Fired when a per-part IsVisible flips; pushes the new
    /// state into the viewer.</summary>
    private async void OnModelPartChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel.ModelPart part) return;
        if (e.PropertyName == nameof(MainViewModel.ModelPart.IsVisible))
        {
            // Viewer addresses parts by the original (source) name. Display Name
            // may have been renamed via the context menu — that's a UI label only.
            await SetPartVisibleAsync(part.OriginalName, part.IsVisible);
        }
        else if (e.PropertyName == nameof(MainViewModel.ModelPart.MaterialPreset))
        {
            await SetPartMaterialAsync(part.OriginalName, part.MaterialPreset);
        }
    }

    /// <summary>Push a material-preset choice into the viewer for live
    /// preview. The viewer wraps the part's meshes' MeshStandardMaterial
    /// to mimic the exported RAGE shader (translucent + reflective for
    /// glass, emissive for the neon variants).</summary>
    private async Task SetPartMaterialAsync(string partName, MaterialPreset preset)
    {
        if (Viewport?.CoreWebView2 == null) return;
        try
        {
            var safeName = System.Text.Json.JsonSerializer.Serialize(partName);
            var safePreset = System.Text.Json.JsonSerializer.Serialize(preset.ToString());
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.applyPartMaterial && window.applyPartMaterial({safeName}, {safePreset})");
        }
        catch { /* viewer hasn't booted yet — preset still applies at export */ }
    }

    private async Task SetPartVisibleAsync(string partName, bool visible)
    {
        if (Viewport?.CoreWebView2 == null) return;
        try
        {
            // JSON-quote the name to escape backslashes / quotes / unicode.
            var safeName = System.Text.Json.JsonSerializer.Serialize(partName);
            var arg = visible ? "true" : "false";
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.setPartVisible && window.setPartVisible({safeName}, {arg})");
        }
        catch { /* swallow */ }
    }

    /// <summary>Wired up in the layers panel item template — clicking the
    /// eye icon flips the bound part's IsVisible. The button's Tag holds
    /// the ModelPart instance via the DataContext.</summary>
    private void OnLayerVisibilityToggle(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement fe && fe.Tag is MainViewModel.ModelPart part)
            part.IsVisible = !part.IsVisible;
    }

    /// <summary>Click anywhere on the collapsed-state vertical strip to
    /// expand the layers panel back to its full 280-px width. Cinema-4D
    /// docked panels work the same way — collapsing tucks the panel to
    /// a tab on the edge, and the tab itself is the expand affordance.</summary>
    private void OnLayersCollapsedStripClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _vm.IsLayersPanelCollapsed = false;
    }

    /// <summary>TRANSFORM → Snap to Ground: drop the model so its
    /// world-space bounding-box bottom sits at Y=0 (= ground plane
    /// in the viewer's three.js coords, which becomes Z=0 in-game
    /// after the export's Y-up→Z-up swap). The viewer does the bbox
    /// math because it has the post-transform geometry; we just call
    /// the JS hook and let the resulting postTransformToHost message
    /// echo the new Y back into our sidebar + undo stack.</summary>
    private async void OnSnapToGround(object sender, RoutedEventArgs e)
    {
        if (Viewport?.CoreWebView2 == null)
        {
            _vm.StatusText = "Viewer not ready yet — load a model first.";
            return;
        }
        if (!_vm.HasModel)
        {
            _vm.StatusText = "Load a model before snapping to ground.";
            return;
        }
        try
        {
            await Viewport.CoreWebView2.ExecuteScriptAsync("window.snapToGround && window.snapToGround()");
            _vm.StatusText = "Snapped to ground.";
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Snap to ground failed: " + ex.Message;
        }
    }

    private async void OnAddTextureExtras(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasModel) return;
        if (_vm.ModelParts.Count == 0)
        {
            _vm.StatusText = "Load a model first.";
            return;
        }

        var partNames = _vm.ModelParts.Select(p => p.OriginalName).ToList();
        _vm.EnsureTextureVariants(partNames, _vm.PropName);

        // Layers selection decides scope: with part(s) selected the image
        // sticks to just those parts (so two textures can live on two parts
        // and BOTH bake into the export). Nothing selected = whole model.
        var targetParts = _vm.SelectedOutlinerNodes
            .Where(n => n.Part is not null)
            .Select(n => n.Part!.OriginalName)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (targetParts.Count == 0 && _vm.SelectedModelPart is { } soloPart)
            targetParts.Add(soloPart.OriginalName);
        bool partScoped = targetParts.Count > 0 && targetParts.Count < partNames.Count;

        var dlg = new OpenFileDialog
        {
            Title = partScoped
                ? $"Add texture for {DescribeParts(targetParts)}"
                : "Add textures to preview",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.dds;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.dds;*.webp|All files|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true || dlg.FileNames.Length == 0) return;

        ModelTextureItem? last = null;
        int added = 0;
        foreach (var path in dlg.FileNames)
        {
            if (!TextureVariantImport.IsImageFile(path)) continue;

            if (partScoped)
            {
                // Pin to the selected parts: live-swap now + record the
                // per-part override that convert bakes (--part-diffuse).
                var stagedScoped = StageLibraryTextureCopy(path);
                foreach (var p in targetParts)
                    await ApplyOnePartDiffuseAsync(p, stagedScoped);

                var scopedItem = new ModelTextureItem
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    Path = stagedScoped,
                    Detail = $"On {DescribeParts(targetParts)}",
                    CanRemove = true,
                    TargetParts = new List<string>(targetParts),
                };
                _vm.ModelTextures.Add(scopedItem);
                last = scopedItem;
                added++;
                continue;
            }

            var variant = _vm.TextureVariants.AddFullModelImage(path);
            var staged = variant?.PreviewPath ?? StageLibraryTextureCopy(path);
            if (string.IsNullOrEmpty(staged) || !File.Exists(staged))
                staged = StageLibraryTextureCopy(path);

            // Point every part at the same staged file (pack + bake).
            if (variant != null)
            {
                variant.PartTextures.Clear();
                foreach (var p in partNames)
                    variant.PartTextures[p] = staged;
                variant.NotifyTexturesChanged();
            }

            var item = new ModelTextureItem
            {
                Name = Path.GetFileNameWithoutExtension(path),
                Path = staged,
                Detail = "Click to preview",
                CanRemove = true,
                LinkedVariant = variant,
            };
            _vm.ModelTextures.Add(item);
            last = item;
            added++;
        }

        _vm.NotifyTextureListUi();
        if (last == null)
        {
            _vm.StatusText = "No usable images selected.";
        }
        else if (partScoped)
        {
            // Already applied to its parts — no whole-model preview swap.
            _vm.StatusText = added == 1
                ? $"'{last.Name}' applied to {DescribeParts(targetParts)} — bakes on Convert."
                : $"Applied {added} textures to {DescribeParts(targetParts)} (last one wins per part) — bakes on Convert.";
        }
        else
        {
            _suppressTextureSelection = true;
            _vm.SelectedTexture = last;
            _suppressTextureSelection = false;
            await PreviewTextureOnModelAsync(last);
            _vm.StatusText = added == 1
                ? $"Previewing '{last.Name}' on the model."
                : $"Added {added} textures — previewing '{last.Name}'.";
        }
    }

    /// <summary>Friendly "seat, frame" / "seat +2 more" for status text.</summary>
    private string DescribeParts(IReadOnlyList<string> originalNames)
    {
        var display = originalNames
            .Select(n => _vm.ModelParts.FirstOrDefault(p =>
                string.Equals(p.OriginalName, n, StringComparison.Ordinal))?.Name ?? n)
            .ToList();
        return display.Count switch
        {
            0 => "the model",
            1 => $"'{display[0]}'",
            2 => $"'{display[0]}' and '{display[1]}'",
            _ => $"'{display[0]}' +{display.Count - 1} more",
        };
    }

    private string StageLibraryTextureCopy(string imagePath)
    {
        if (string.IsNullOrEmpty(_partTexOverrideDir) || !Directory.Exists(_partTexOverrideDir))
        {
            _partTexOverrideDir = Path.Combine(
                Path.GetTempPath(), "FiveOS", "part-tex", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_partTexOverrideDir);
        }
        var dest = Path.Combine(_partTexOverrideDir,
            Guid.NewGuid().ToString("N")[..8] + "_" + Path.GetFileName(imagePath));
        File.Copy(imagePath, dest, overwrite: true);
        return dest;
    }

    private async void OnRemoveLibraryTexture(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ModelTextureItem item)
            return;
        e.Handled = true;
        if (item.LinkedVariant != null)
            _vm.TextureVariants.Remove(item.LinkedVariant);
        if (ReferenceEquals(_vm.SelectedTexture, item))
            _vm.SelectedTexture = null;
        _vm.ModelTextures.Remove(item);
        _vm.NotifyTextureListUi();

        // Removing a pinned texture releases its parts: drop the bake
        // overrides and put the original maps back in the viewer.
        if (item.TargetParts is { Count: > 0 } released)
        {
            var stillPinned = PinnedPartNames();
            var baseRow = _vm.ModelTextures.FirstOrDefault(t => t.IsBaseGroup);
            foreach (var name in released.Where(n => !stillPinned.Contains(n)))
            {
                _vm.PartDiffuseOverrides.Remove(name);
                var basePath = baseRow != null ? ResolveBasePathForPart(baseRow, name) : null;
                if (basePath == null) continue;
                try { await ApplyOnePartDiffuseAsync(name, basePath, recordForConvert: false); }
                catch (Exception ex)
                {
                    Services.FosLogger.Warn("textures", $"release '{name}': {ex.Message}");
                }
            }
        }
    }

    private async void OnTextureLibrarySelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressTextureSelection) return;
        if (_vm.SelectedTexture is not ModelTextureItem item) return;
        if (item.IsBaseGroup)
        {
            await PreviewTextureOnModelAsync(item);
            return;
        }
        if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path)) return;
        await PreviewTextureOnModelAsync(item);
    }

    /// <summary>Live-swap the selected image onto every visible part — same
    /// feel as picking a material in a DCC viewport.</summary>
    private async Task PreviewTextureOnModelAsync(ModelTextureItem item)
    {
        if (item.IsBaseGroup)
        {
            await RestoreBaseTextureAsync(item);
            return;
        }

        if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path)) return;
        if (_vm.ModelParts.Count == 0) return;

        // Part-scoped textures only ever touch their own parts — clicking
        // them re-asserts the assignment instead of flooding the model.
        // Whole-model previews respect those pins and skip pinned parts.
        var targets = _vm.ModelParts.Where(p => p.IsVisible);
        if (item.TargetParts is { Count: > 0 } scoped)
        {
            targets = targets.Where(p => scoped.Contains(p.OriginalName, StringComparer.Ordinal));
        }
        else
        {
            var pinned = PinnedPartNames();
            if (pinned.Count > 0)
                targets = targets.Where(p => !pinned.Contains(p.OriginalName));
        }

        int noUv = 0;
        foreach (var part in targets)
        {
            try
            {
                if (await ApplyOnePartDiffuseAsync(part.OriginalName, item.Path))
                    noUv++;
            }
            catch (Exception ex)
            {
                Services.FosLogger.Warn("textures", $"preview '{part.OriginalName}': {ex.Message}");
            }
        }

        var activeDetail = item.IsPartScoped
            ? $"On {DescribeParts(item.TargetParts!)}"
            : (noUv > 0 ? "Preview · missing UVs on some parts" : "Previewing on model");
        MarkTextureSelectionDetails(item, activeDetail);
        _vm.StatusText = noUv > 0
            ? $"Previewing '{item.Name}' — {noUv} part(s) have no UVs."
            : item.IsPartScoped
                ? $"'{item.Name}' on {DescribeParts(item.TargetParts!)} — bakes on Convert."
                : $"Previewing '{item.Name}' on the model.";
    }

    /// <summary>Parts currently pinned by a part-scoped library texture.</summary>
    private HashSet<string> PinnedPartNames() => _vm.ModelTextures
        .Where(t => t.IsPartScoped)
        .SelectMany(t => t.TargetParts!)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>Clear bake overrides and restore source maps in the viewer.
    /// Parts pinned by a part-scoped texture keep their assignment — remove
    /// the pinned row (its X) to release them.</summary>
    private async Task RestoreBaseTextureAsync(ModelTextureItem baseItem)
    {
        var pinned = PinnedPartNames();
        if (pinned.Count == 0)
        {
            ClearPartDiffuseOverrides();
        }
        else
        {
            foreach (var key in _vm.PartDiffuseOverrides.Keys
                         .Where(k => !pinned.Contains(k)).ToList())
                _vm.PartDiffuseOverrides.Remove(key);
        }

        bool restored = false;
        if (pinned.Count == 0 && Viewport?.CoreWebView2 != null)
        {
            try
            {
                var res = await Viewport.CoreWebView2.ExecuteScriptAsync(
                    "window.resetAllPartTextures && window.resetAllPartTextures()");
                // ExecuteScriptAsync wraps JSON — true / false / null
                restored = res != null
                    && !res.Equals("false", StringComparison.OrdinalIgnoreCase)
                    && !res.Equals("null", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Services.FosLogger.Warn("textures", "resetAllPartTextures: " + ex.Message);
            }
        }

        // Fallback when the viewer never saw a live swap (no cached orig maps)
        // or part names from Assimp don't match — push staged base maps by
        // matching to current Layers names. Pinned parts are left alone.
        if (!restored && baseItem.BasePartPaths is { Count: > 0 })
        {
            foreach (var part in _vm.ModelParts.Where(p =>
                         p.IsVisible && !pinned.Contains(p.OriginalName)))
            {
                var path = ResolveBasePathForPart(baseItem, part.OriginalName);
                if (path == null) continue;
                try
                {
                    await ApplyOnePartDiffuseAsync(part.OriginalName, path, recordForConvert: false);
                    restored = true;
                }
                catch (Exception ex)
                {
                    Services.FosLogger.Warn("textures", $"restore base '{part.OriginalName}': {ex.Message}");
                }
            }
        }

        var mapCount = baseItem.BasePartPaths?.Values
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;
        MarkTextureSelectionDetails(baseItem, mapCount > 0
            ? $"Original · {mapCount} map{(mapCount == 1 ? "" : "s")}"
            : "Original");
        _vm.StatusText = restored
            ? "Base Texture on the model."
            : "Base Texture selected.";
    }

    private static string? ResolveBasePathForPart(ModelTextureItem baseItem, string partName)
    {
        if (baseItem.BasePartPaths == null || baseItem.BasePartPaths.Count == 0)
            return null;
        if (baseItem.BasePartPaths.TryGetValue(partName, out var exact) && File.Exists(exact))
            return exact;

        // Fuzzy: Assimp node names often differ slightly from viewer display names.
        foreach (var kv in baseItem.BasePartPaths)
        {
            if (!File.Exists(kv.Value)) continue;
            if (string.Equals(kv.Key, partName, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
            if (kv.Key.Contains(partName, StringComparison.OrdinalIgnoreCase)
                || partName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }

        // Single-map models: one texture for every part.
        var unique = baseItem.BasePartPaths.Values
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return unique.Count == 1 ? unique[0] : null;
    }

    /// <summary>Only the active library row shows a live-preview status.</summary>
    private void MarkTextureSelectionDetails(ModelTextureItem active, string activeDetail)
    {
        foreach (var t in _vm.ModelTextures)
        {
            if (ReferenceEquals(t, active))
            {
                t.Detail = activeDetail;
                continue;
            }
            if (t.IsBaseGroup)
            {
                var n = t.BasePartPaths?.Values
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;
                t.Detail = n > 0 ? $"Original · {n} map{(n == 1 ? "" : "s")}" : "Original";
            }
            else if (t.IsPartScoped)
            {
                // Pinned assignments stay live regardless of what previews.
                t.Detail = $"On {DescribeParts(t.TargetParts!)}";
            }
            else if (t.Detail.StartsWith("Preview", StringComparison.OrdinalIgnoreCase)
                     || t.Detail.StartsWith("Previewing", StringComparison.OrdinalIgnoreCase))
            {
                t.Detail = "Click to preview";
            }
        }
    }

    /// <summary>Extract source diffuse maps and show them as one Base Texture row.</summary>
    private async Task LoadBaseTextureGroupAsync(string sourcePath)
    {
        int gen = ++_modelTexRefreshGen;
        _vm.ModelTexturesLoading = true;
        try
        {
            var stageDir = Path.Combine(Path.GetTempPath(), "FiveOS", "model-tex",
                Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(stageDir);

            var group = await Task.Run(() =>
                PartTextureService.ExtractBaseTextureGroup(sourcePath, stageDir));
            if (gen != _modelTexRefreshGen) return;

            if (!string.IsNullOrEmpty(_modelTexPreviewDir))
            {
                try { if (Directory.Exists(_modelTexPreviewDir)) Directory.Delete(_modelTexPreviewDir, true); }
                catch { /* best-effort */ }
            }
            _modelTexPreviewDir = stageDir;

            _suppressTextureSelection = true;
            var extras = _vm.ModelTextures
                .Where(t => t.CanRemove || t.LinkedVariant != null)
                .ToList();
            var selectedWasBase = _vm.SelectedTexture?.IsBaseGroup == true;
            _vm.ModelTextures.Clear();

            var mapCount = group.UniqueMapCount;
            var detail = mapCount > 0
                ? $"Original · {mapCount} map{(mapCount == 1 ? "" : "s")}"
                : "Original · no diffuse maps";
            _vm.ModelTextures.Add(new ModelTextureItem
            {
                Name = "Base Texture",
                Path = group.ThumbPath ?? "",
                Detail = detail,
                CanRemove = false,
                IsBaseGroup = true,
                BasePartPaths = group.PartDiffusePaths.Count > 0
                    ? new Dictionary<string, string>(group.PartDiffusePaths, StringComparer.OrdinalIgnoreCase)
                    : null,
            });
            foreach (var e in extras)
                _vm.ModelTextures.Add(e);

            if (selectedWasBase || _vm.SelectedTexture == null)
                _vm.SelectedTexture = _vm.ModelTextures.FirstOrDefault(t => t.IsBaseGroup);

            Services.FosLogger.Info("textures",
                $"base group: {mapCount} map(s) from {Path.GetFileName(sourcePath)}");
        }
        catch (Exception ex)
        {
            Services.FosLogger.Warn("textures", "base group failed: " + ex.Message);
            if (gen == _modelTexRefreshGen && !_vm.ModelTextures.Any(t => t.IsBaseGroup))
            {
                _vm.ModelTextures.Insert(0, new ModelTextureItem
                {
                    Name = "Base Texture",
                    Detail = "Original",
                    CanRemove = false,
                    IsBaseGroup = true,
                });
            }
        }
        finally
        {
            _suppressTextureSelection = false;
            if (gen == _modelTexRefreshGen)
            {
                _vm.ModelTexturesLoading = false;
                _vm.NotifyTextureListUi();
            }
        }
    }

    private async void OnBuildTexturePack(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.SourcePath) || !File.Exists(_vm.SourcePath))
        {
            _vm.StatusText = "Load a model first.";
            return;
        }
        if (_vm.ModelParts.Count == 0)
        {
            _vm.StatusText = "No model parts — load a model first.";
            return;
        }
        if (!EngineRunner.IsEngineAvailable())
        {
            AppDialog.Show(
                "Conversion engine is missing from the install.\n\nRe-install FiveOS — the conversion engine ships in the same bundle.",
                "Engine not available",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning,
                this);
            return;
        }

        // Library rows only — compiled thumbs are not full source images.
        // Prefer the row path; if the stage dir was wiped, recover from the
        // LinkedVariant or from the part-diffuse copies written during preview.
        var library = new List<(ModelTextureItem Item, string Path)>();
        foreach (var t in _vm.ModelTextures.Where(x => x.CanRemove))
        {
            var path = ResolveLibraryTexturePath(t);
            if (string.IsNullOrEmpty(path)) continue;
            if (!string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))
                t.Path = path;
            library.Add((t, path));
        }
        if (library.Count == 0)
        {
            _vm.StatusText = _vm.ModelTextures.Any(t => t.CanRemove)
                ? "Texture files were cleaned up — add them again."
                : "Add textures to the list first.";
            return;
        }

        var partNames = _vm.ModelParts.Select(p => p.OriginalName).ToList();
        _vm.EnsureTextureVariants(partNames, _vm.PropName);

        // Rebuild variants from the library so every part gets the image
        // (same as live preview) and staged files still exist.
        _vm.TextureVariants.Clear();
        var variants = new List<TextureVariant>();
        foreach (var (item, path) in library)
        {
            var v = _vm.TextureVariants.AddFullModelImage(path, item.Name);
            if (v == null) continue;
            item.LinkedVariant = v;
            variants.Add(v);
        }
        if (variants.Count == 0)
        {
            _vm.StatusText = "Could not stage textures for the pack.";
            return;
        }

        if (!_vm.IsPackMode) _vm.IsPackMode = true;
        if (string.IsNullOrWhiteSpace(_vm.PackSession.PackName))
            _vm.PackSession.PackName = (string.IsNullOrWhiteSpace(_vm.PropName) ? "prop" : _vm.PropName) + "_pack";

        var excludeMeshes = _vm.ModelParts.Where(p => !p.IsVisible).Select(p => p.OriginalName)
            .Concat(_vm.DeletedPartNames)
            .Distinct(System.StringComparer.Ordinal)
            .ToList();
        var baseReq = BuildConvertRequest(_vm.PropName, excludeMeshes, routeToPack: false);

        _vm.IsConverting = true;
        _vm.TextureVariants.IsRunning = true;
        _vm.StatusText = $"Textures: converting base mesh, then {variants.Count} extra(s)…";
        Services.FosLogger.Info("tex-variants",
            $"start: {variants.Count} variants src={Path.GetFileName(_vm.SourcePath)}");

        _convertCts = new System.Threading.CancellationTokenSource();
        TextureVariantPipeline.Result result;
        string? packLog = null;
        try
        {
            var logBuf = new System.Text.StringBuilder();
            result = await TextureVariantPipeline.RunAsync(
                baseReq,
                variants,
                onLog: line =>
                {
                    logBuf.AppendLine(line);
                    OnEngineLog(line);
                },
                onProgress: (cur, total) =>
                {
                    Dispatcher.Invoke(() =>
                        _vm.StatusText = $"Textures pack: {cur}/{total}…");
                },
                cancel: _convertCts.Token);
            packLog = logBuf.ToString();
        }
        catch (Exception ex)
        {
            Services.FosLogger.Err("tex-variants", "failed: " + ex.Message);
            _vm.IsConverting = false;
            _vm.TextureVariants.IsRunning = false;
            _convertCts?.Dispose();
            _convertCts = null;
            _vm.StatusText = "Texture pack failed: " + ex.Message;
            AppDialog.Error("Texture pack failed:\n\n" + ex.Message, "Textures", this);
            return;
        }

        bool wasCancelled = _convertCts?.IsCancellationRequested == true
            || string.Equals(result.Error, "Cancelled.", StringComparison.Ordinal);
        _convertCts?.Dispose();
        _convertCts = null;
        _vm.IsConverting = false;
        _vm.TextureVariants.IsRunning = false;

        if (result.Ok > 0)
        {
            _vm.TextureVariants.Clear();
            _vm.NotifyTextureListUi();
            _vm.StatusText =
                $"✓ Staged {result.Ok} texture prop(s) into pack ({_vm.PackSession.Count} prop{(_vm.PackSession.Count == 1 ? "" : "s")})" +
                (result.Failed > 0 ? $" · {result.Failed} failed" : "") +
                " — Finalize from the Pack panel.";
        }
        else if (wasCancelled)
        {
            _vm.StatusText = "Texture pack cancelled.";
        }
        else
        {
            var detail = result.Error ?? "No textures were staged.";
            if (!string.IsNullOrWhiteSpace(packLog))
            {
                var tail = packLog.Length > 1200 ? packLog[^1200..] : packLog;
                detail += "\n\n" + tail.Trim();
            }
            _vm.StatusText = "✗ Texture pack failed: " + (result.Error ?? "unknown error");
            AppDialog.Error("Texture pack failed:\n\n" + detail, "Textures", this);
        }
    }

    /// <summary>Find a readable image for a library row. Preview stages copies
    /// into PartDiffuseOverrides, so those can rescue a wiped tex-variants dir.</summary>
    private string? ResolveLibraryTexturePath(ModelTextureItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Path) && File.Exists(item.Path))
            return item.Path;

        var linked = item.LinkedVariant?.PreviewPath;
        if (!string.IsNullOrWhiteSpace(linked) && File.Exists(linked))
            return linked;

        if (item.LinkedVariant != null)
        {
            foreach (var p in item.LinkedVariant.PartTextures.Values)
            {
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    return p;
            }
        }

        var stem = Path.GetFileNameWithoutExtension(item.Name ?? "");
        foreach (var p in _vm.PartDiffuseOverrides.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
            if (string.IsNullOrEmpty(stem)) return p;
            var fileStem = Path.GetFileNameWithoutExtension(p);
            // Staged names look like "a1b2c3d4_Screenshot_….png"
            if (fileStem.Contains(stem, StringComparison.OrdinalIgnoreCase)
                || stem.Contains(Path.GetFileNameWithoutExtension(item.Path ?? ""), StringComparison.OrdinalIgnoreCase))
                return p;
        }

        // Single previewed image: any override is that library texture.
        if (_vm.ModelTextures.Count(t => t.CanRemove) == 1)
        {
            foreach (var p in _vm.PartDiffuseOverrides.Values)
            {
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    return p;
            }
        }

        return null;
    }

    /// <summary>
    /// Applied layer textures stay in PartDiffuseOverrides for convert —
    /// they are not listed as separate library rows (Base Texture + user
    /// extras only).
    /// </summary>
    private async Task RefreshModelTextureListAsync()
    {
        await Task.Yield();
        _vm.NotifyTextureListUi();
    }

    private async Task LoadCompiledTexturesFromOutcomeAsync(
        EngineRunner.ConvertOutcome outcome, string assetName)
    {
        if (outcome.ResultPath == null || string.IsNullOrWhiteSpace(assetName))
            return;

        int gen = ++_modelTexRefreshGen;
        _vm.ModelTexturesLoading = true;

        string? extractDir = null;
        try
        {
            var ydrPath = await Task.Run(() =>
                ResolveCompiledYdrPath(outcome, assetName, out extractDir));
            if (gen != _modelTexRefreshGen) return;

            if (string.IsNullOrEmpty(ydrPath) || !File.Exists(ydrPath))
            {
                Services.FosLogger.Warn("textures", "no compiled .ydr found after convert");
                return;
            }

            var infos = await Task.Run(() => TextureGalleryExtractor.Extract(ydrPath));
            if (gen != _modelTexRefreshGen) return;

            var previewDir = Path.Combine(Path.GetTempPath(), "FiveOS", "model-tex",
                Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(previewDir);

            if (!string.IsNullOrEmpty(_modelTexPreviewDir))
            {
                try { if (Directory.Exists(_modelTexPreviewDir)) Directory.Delete(_modelTexPreviewDir, true); }
                catch { /* best-effort */ }
            }
            _modelTexPreviewDir = previewDir;

            // Keep user library rows; refresh the single Base Texture row
            // instead of listing every compiled map separately.
            _suppressTextureSelection = true;
            var keep = _vm.ModelTextures
                .Where(t => t.CanRemove || t.LinkedVariant != null)
                .ToList();
            var baseRow = _vm.ModelTextures.FirstOrDefault(t => t.IsBaseGroup);
            _vm.SelectedTexture = null;
            _vm.ModelTextures.Clear();

            string thumbPath = baseRow?.Path ?? "";
            var first = infos.FirstOrDefault(t => t.ThumbPng is { Length: > 0 });
            if (first?.ThumbPng is { Length: > 0 } png)
            {
                thumbPath = Path.Combine(previewDir, $"base_{SanitizeFileStem(first.Name)}.png");
                await File.WriteAllBytesAsync(thumbPath, png);
            }

            var mapCount = infos.Count;
            _vm.ModelTextures.Add(new ModelTextureItem
            {
                Name = "Base Texture",
                Path = thumbPath,
                Detail = mapCount > 0
                    ? $"Original · {mapCount} map{(mapCount == 1 ? "" : "s")}"
                    : (baseRow?.Detail ?? "Original"),
                CanRemove = false,
                IsBaseGroup = true,
                BasePartPaths = baseRow?.BasePartPaths,
            });
            foreach (var k in keep)
                _vm.ModelTextures.Add(k);

            Services.FosLogger.Info("textures", $"compiled base group: {infos.Count} from {Path.GetFileName(ydrPath)}");
        }
        catch (Exception ex)
        {
            Services.FosLogger.Warn("textures", "compiled list failed: " + ex.Message);
        }
        finally
        {
            _suppressTextureSelection = false;
            if (!string.IsNullOrEmpty(extractDir))
            {
                try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true); }
                catch { /* best-effort — thumbs already copied out */ }
            }
            if (gen == _modelTexRefreshGen)
            {
                _vm.ModelTexturesLoading = false;
                _vm.NotifyTextureListUi();
            }
        }
    }

    /// <summary>Locate the output <c>.ydr</c> for a convert outcome. For zip
    /// delivery, extracts just the drawable into a temp folder (caller deletes
    /// <paramref name="extractDir"/>).</summary>
    private static string? ResolveCompiledYdrPath(
        EngineRunner.ConvertOutcome outcome, string assetName, out string? extractDir)
    {
        extractDir = null;
        var root = outcome.ResultPath;
        if (string.IsNullOrEmpty(root)) return null;
        var ydrName = assetName + ".ydr";

        switch (outcome.Mode)
        {
            case EngineRunner.OutputMode.ServerShared:
                // ResultPath = <server>/stream
                var shared = Path.Combine(root, ydrName);
                return File.Exists(shared) ? shared : null;

            case EngineRunner.OutputMode.ServerPerAsset:
            case EngineRunner.OutputMode.Pack:
                // ResultPath = …/{asset}_resource (or pack slot)
                var per = Path.Combine(root, "stream", ydrName);
                if (File.Exists(per)) return per;
                var loose = Path.Combine(root, ydrName);
                return File.Exists(loose) ? loose : null;

            case EngineRunner.OutputMode.SingleZip:
                if (!File.Exists(root)) return null;
                extractDir = Path.Combine(Path.GetTempPath(), "FiveOS", "ydr-peek",
                    Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(extractDir);
                using (var zip = System.IO.Compression.ZipFile.OpenRead(root))
                {
                    var entry = zip.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith("/" + ydrName, StringComparison.OrdinalIgnoreCase) ||
                        e.FullName.Equals(ydrName, StringComparison.OrdinalIgnoreCase) ||
                        e.Name.Equals(ydrName, StringComparison.OrdinalIgnoreCase));
                    if (entry == null) return null;
                    var dest = Path.Combine(extractDir, ydrName);
                    entry.ExtractToFile(dest, overwrite: true);
                    return dest;
                }

            default:
                return null;
        }
    }

    private static string SanitizeFileStem(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "tex";
        var bad = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(bad, chars[i]) >= 0) chars[i] = '_';
        return new string(chars);
    }

    private void ClearModelTexturePreview()
    {
        _modelTexRefreshGen++;
        _vm.ClearModelTextures();
        _vm.ResetTextureVariants(Array.Empty<string>(), "");
        if (string.IsNullOrEmpty(_modelTexPreviewDir)) return;
        try
        {
            if (Directory.Exists(_modelTexPreviewDir))
                Directory.Delete(_modelTexPreviewDir, recursive: true);
        }
        catch { /* best-effort */ }
        _modelTexPreviewDir = null;
    }

    private async void OnAddMissingTextures(object sender, RoutedEventArgs e)
    {
        if (Viewport?.CoreWebView2 == null)
        {
            _vm.StatusText = "Viewer not ready yet — load a model first.";
            return;
        }
        if (_vm.ModelParts.Count == 0)
        {
            _vm.StatusText = "No model parts to texture — load a model first.";
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = "Pick textures — each image is matched to a part by filename",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.dds)|*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.dds|All files|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true || dlg.FileNames.Length == 0) return;

        int applied = 0;
        int skipped = 0;
        int noUv = 0;
        var parts = _vm.ModelParts.ToList();
        // Track which parts already received a texture so the order-based
        // fallback doesn't stack multiple images onto the same part.
        var usedParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Textures whose filename matched no part — assigned to leftover parts
        // in pick order below. This is the common case for Sims/Blender OBJ:
        // the texture files (e.g. a diffuse map) are named nothing like the
        // mesh objects, so a name-only match left everything white.
        var unmatched = new List<string>();

        foreach (var path in dlg.FileNames)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(stem)) { skipped++; continue; }

            var stemLower = stem.ToLowerInvariant();
            var match = parts.FirstOrDefault(p =>
                string.Equals(p.Name, stem, StringComparison.OrdinalIgnoreCase) && !usedParts.Contains(p.OriginalName));
            if (match == null)
            {
                match = parts.FirstOrDefault(p =>
                    !usedParts.Contains(p.OriginalName) &&
                    (p.Name.ToLowerInvariant().Contains(stemLower) ||
                     stemLower.Contains(p.Name.ToLowerInvariant())));
            }
            if (match == null) { unmatched.Add(path); continue; }

            try { if (await ApplyOnePartDiffuseAsync(match.OriginalName, path)) noUv++; usedParts.Add(match.OriginalName); applied++; }
            catch { skipped++; }
        }

        // Fallback: hand the still-unmatched textures to the still-untextured
        // parts in pick order. A single-mesh Sims model + one diffuse PNG just
        // works; multi-part models fill in order.
        foreach (var path in unmatched)
        {
            var target = parts.FirstOrDefault(p => !usedParts.Contains(p.OriginalName));
            if (target == null) { skipped++; continue; }
            try { if (await ApplyOnePartDiffuseAsync(target.OriginalName, path)) noUv++; usedParts.Add(target.OriginalName); applied++; }
            catch { skipped++; }
        }

        if (noUv > 0)
            _vm.StatusText = $"Textures: {applied} applied, but {noUv} part(s) have NO UV MAP — the OBJ was exported without UVs, so the texture can't show. Re-export from Blender with 'Include UVs' checked.";
        else
            _vm.StatusText = skipped > 0
                ? $"Textures: {applied} applied, {skipped} skipped (decode error). Convert will bake applied ones."
                : $"Textures: {applied} applied — will bake on Convert.";

        if (applied > 0)
            _ = RefreshModelTextureListAsync();
    }

    /// <summary>Stage <paramref name="imagePath"/> for convert and live-swap
    /// the preview diffuse on the named part. Returns true when the part has
    /// NO UV map (texture can't display — the OBJ was exported without UVs).
    /// Pass <paramref name="recordForConvert"/> false when restoring Base
    /// Texture so Convert still bakes the original source maps.</summary>
    private async Task<bool> ApplyOnePartDiffuseAsync(
        string partOriginalName, string imagePath, bool recordForConvert = true)
    {
        if (recordForConvert)
        {
            var staged = StagePartDiffuseOverride(partOriginalName, imagePath);
            _vm.PartDiffuseOverrides[partOriginalName] = staged;
        }

        if (Viewport?.CoreWebView2 == null) return false;

        byte[] bytes;
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
        {
            bytes = await File.ReadAllBytesAsync(imagePath);
        }
        else
        {
            using var img = new ImageMagick.MagickImage(await File.ReadAllBytesAsync(imagePath));
            img.Format = ImageMagick.MagickFormat.Png;
            bytes = img.ToByteArray();
        }
        var mime = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp"            => "image/bmp",
            _                 => "image/png",
        };
        var dataUrl = "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        var safeName = System.Text.Json.JsonSerializer.Serialize(partOriginalName);
        var safeUrl  = System.Text.Json.JsonSerializer.Serialize(dataUrl);
        // setPartTexture returns { ok, hasUv } — no UVs means the map can't map.
        var res = await Viewport.CoreWebView2.ExecuteScriptAsync(
            $"window.setPartTexture && JSON.stringify(window.setPartTexture({safeName}, {safeUrl}))");
        try
        {
            if (!string.IsNullOrEmpty(res))
            {
                var json = System.Text.Json.JsonSerializer.Deserialize<string>(res);
                if (!string.IsNullOrEmpty(json))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("hasUv", out var uvEl) && uvEl.ValueKind == System.Text.Json.JsonValueKind.False)
                        return true;   // applied but no UVs
                }
            }
        }
        catch { /* older viewer returned a bare bool — treat as UV-ok */ }
        return false;
    }

    /// <summary>Copy a picked texture into the per-session override dir so
    /// convert still has a stable absolute path after the dialog closes.</summary>
    private string StagePartDiffuseOverride(string partOriginalName, string imagePath)
    {
        if (string.IsNullOrEmpty(_partTexOverrideDir) || !Directory.Exists(_partTexOverrideDir))
        {
            _partTexOverrideDir = Path.Combine(
                Path.GetTempPath(), "FiveOS", "part-tex", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_partTexOverrideDir);
        }

        var safe = SanitizeAssetName(partOriginalName);
        if (string.IsNullOrEmpty(safe)) safe = "part";
        var ext = Path.GetExtension(imagePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var dest = Path.Combine(_partTexOverrideDir, safe + ext);
        File.Copy(imagePath, dest, overwrite: true);
        return dest;
    }

    private void ClearPartDiffuseOverrides()
    {
        _vm.PartDiffuseOverrides.Clear();
        if (string.IsNullOrEmpty(_partTexOverrideDir)) return;
        try
        {
            if (Directory.Exists(_partTexOverrideDir))
                Directory.Delete(_partTexOverrideDir, recursive: true);
        }
        catch { /* best-effort temp cleanup */ }
        _partTexOverrideDir = null;
    }

    /// <summary>Wired up in the layers panel item template — the trash
    /// icon removes the part from the panel and tells the viewer to hide
    /// it. The name is parked in <see cref="MainViewModel.DeletedPartNames"/>
    /// so the convert path still excludes it from the YDR. Reload the
    /// model to bring it back.</summary>
    private async void OnLayerDelete(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement fe ||
            fe.Tag is not MainViewModel.ModelPart part) return;

        // Drop from the displayed list and from the IsVisible subscription.
        part.PropertyChanged -= OnModelPartChanged;
        _vm.ModelParts.Remove(part);
        _vm.DeletedPartNames.Add(part.OriginalName);

        // Hide in the live preview so the user sees the deletion immediately.
        await SetPartVisibleAsync(part.OriginalName, false);
    }

    // ─────────────── Layer context-menu actions ───────────────

    /// <summary>Material-preset click handlers — one per preset because
    /// IsCheckable MenuItems can't carry CommandParameter through to a
    /// single Click handler reliably (the IsChecked toggle fires before
    /// we can read the parameter). Each just writes the preset; the VM
    /// fires <see cref="MainViewModel.ModelPart.MaterialPresetChanged"/>,
    /// which routes through <see cref="OnModelPartChanged"/> into
    /// <see cref="SetPartMaterialAsync"/> for live viewer preview.</summary>
    private void OnLayerMaterialStandard(object sender, RoutedEventArgs e)
        => SetMaterialPresetFromMenu(sender, MaterialPreset.Standard);
    private void OnLayerMaterialGlass(object sender, RoutedEventArgs e)
        => SetMaterialPresetFromMenu(sender, MaterialPreset.Glass);
    private void OnLayerMaterialEmissive(object sender, RoutedEventArgs e)
        => SetMaterialPresetFromMenu(sender, MaterialPreset.Emissive);
    private void OnLayerMaterialEmissiveStrong(object sender, RoutedEventArgs e)
        => SetMaterialPresetFromMenu(sender, MaterialPreset.EmissiveStrong);
    private void OnLayerMaterialEmissiveNight(object sender, RoutedEventArgs e)
        => SetMaterialPresetFromMenu(sender, MaterialPreset.EmissiveNight);
    private void OnLayerMaterialMetal(object sender, RoutedEventArgs e)
        => SetMaterialPresetFromMenu(sender, MaterialPreset.Metal);
    private void OnLayerMaterialCutout(object sender, RoutedEventArgs e)
        => SetMaterialPresetFromMenu(sender, MaterialPreset.Cutout);

    private void SetMaterialPresetFromMenu(object sender, MaterialPreset preset)
    {
        if (sender is not System.Windows.FrameworkElement fe ||
            fe.Tag is not MainViewModel.ModelPart part) return;
        // IsCheckable on a checked-but-also-Click menu item flips IsChecked
        // BEFORE Click fires. Force the value back to the user's intent
        // (the menu shows one preset checked at a time — radio behavior).
        part.MaterialPreset = preset;
    }

    /// <summary>Rename: tiny modal input dialog. Updates the VM's display
    /// name only — OriginalName stays so engine + viewer keep finding the
    /// part by its source-side name.</summary>
    private void OnLayerRename(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement fe ||
            fe.Tag is not MainViewModel.ModelPart part) return;

        var newName = ShowInputDialog("Rename layer", "New name:", part.Name);
        if (string.IsNullOrWhiteSpace(newName)) return;
        var trimmed = newName.Trim();
        if (string.Equals(trimmed, part.Name, StringComparison.Ordinal)) return;
        part.Name = trimmed;

        // Single-mesh model: the outliner row is labeled with the PROP
        // name, not the part name, so a part-only rename looked like a
        // no-op. One layer IS the prop — rename it end to end (row label
        // + export asset name follow via OnPropNameChanged).
        if (_vm.ModelParts.Count == 1)
            _vm.PropName = trimmed;
    }

    /// <summary>Per-part Optimize Mesh: opens the inline slider on this
    /// row. Uses a quick re-import to count the part's tris up front so
    /// the slider range and "stats" preview are accurate before the user
    /// commits anything.</summary>
    private async void OnLayerOptimizeMesh(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement fe ||
            fe.Tag is not MainViewModel.ModelPart part) return;
        if (string.IsNullOrEmpty(_vm.SourcePath) || !File.Exists(_vm.SourcePath)) return;

        // Close any other slider that may still be open so only one row
        // is in edit mode at a time.
        foreach (var p in _vm.ModelParts)
            if (p != part) p.IsMeshSliderOpen = false;

        // Tri count for this part — needs an Assimp re-read off the UI thread.
        var src = _vm.SourcePath;
        var origName = part.OriginalName;
        int tris = await Task.Run(() => CountTrianglesForPart(src, origName));
        if (tris < 32)
        {
            _vm.StatusText = $"'{part.Name}' has {tris} tris — nothing to decimate.";
            return;
        }

        part.MeshOriginalTris = tris;
        part.MeshSliderMin = Math.Max(64, tris / 20);
        part.MeshSliderMax = Math.Max(part.MeshSliderMin + 64, (int)(tris * 0.95));
        part.MeshSliderTick = Math.Max(50, (part.MeshSliderMax - part.MeshSliderMin) / 100);
        part.MeshTargetTris = Math.Clamp(tris / 2, part.MeshSliderMin, part.MeshSliderMax);
        part.IsMeshSliderOpen = true;
    }

    private void OnLayerCancelOptimizeMesh(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement fe && fe.Tag is MainViewModel.ModelPart part)
            part.IsMeshSliderOpen = false;
    }

    /// <summary>Confirm the per-part decimation: runs SourceMeshOptimizer
    /// scoped to this part's node sub-tree, then re-loads the optimized
    /// GLB into the viewer (same hand-off as the global optimize banner).</summary>
    private async void OnLayerConfirmOptimizeMesh(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement fe ||
            fe.Tag is not MainViewModel.ModelPart part) return;
        if (string.IsNullOrEmpty(_vm.SourcePath) || !File.Exists(_vm.SourcePath)) return;

        var input = _vm.SourcePath!;
        var partName = part.OriginalName;
        var target = part.MeshTargetTris;

        part.IsMeshOptimizing = true;
        _vm.StatusText = $"Optimizing '{part.Name}' — decimating to {target:N0} tris...";

        var result = await Task.Run(() =>
            Services.SourceMeshOptimizer.OptimizePart(
                input, partName, target,
                progress: msg => Dispatcher.Invoke(() => _vm.StatusText = $"Optimizing '{part.Name}' — {msg}")));

        part.IsMeshOptimizing = false;
        part.IsMeshSliderOpen = false;

        if (result.Error != null)
        {
            _vm.StatusText = $"Optimize failed: {result.Error}";
            return;
        }

        _vm.StatusText =
            $"Optimized '{part.Name}': {result.TrianglesBefore:N0} → {result.TrianglesAfter:N0} tris" +
            $" — wireframe enabled, click the viewport and press F to turn off.";

        // Flip wireframe-edge overlay on so the user can immediately spot
        // any holes / dropped tris in the freshly-decimated mesh. Viewer
        // state persists across model reloads, so setting it here and
        // then TryLoad-ing the optimized GLB lands the user on a model
        // with edges already drawn.
        if (Viewport?.CoreWebView2 != null)
            _ = Viewport.CoreWebView2.ExecuteScriptAsync(
                "window.fiveosViewer && window.fiveosViewer.setWireframe(true);");

        TryLoad(result.OutputPath);
    }

    /// <summary>Textures → Optimize: extracts every texture bound to a
    /// material under the part and runs <see cref="PartTextureService"/>
    /// over them. Outputs to a sibling <c>{model}_{part}_textures/</c>
    /// folder. Doesn't mutate the source — produces optimized siblings.</summary>
    private async void OnLayerOptimizeTextures(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement fe ||
            fe.Tag is not MainViewModel.ModelPart part) return;
        if (string.IsNullOrEmpty(_vm.SourcePath) || !File.Exists(_vm.SourcePath)) return;

        var input = _vm.SourcePath!;
        var partName = part.OriginalName;
        _vm.StatusText = $"Optimizing textures bound to '{part.Name}'...";

        var report = await Task.Run(() =>
            Services.PartTextureService.OptimizePartTextures(input, partName));

        if (report.Error != null)
        {
            _vm.StatusText = $"Texture optimize failed: {report.Error}";
            return;
        }

        var savedPct = report.BytesBefore > 0
            ? (int)Math.Round(100.0 * (1 - (double)report.BytesAfter / report.BytesBefore))
            : 0;
        _vm.StatusText = report.OutputDir != null
            ? $"Optimized {report.TexturesOptimized}/{report.TexturesFound} texture(s) for '{part.Name}' " +
              $"({FormatBytesShort(report.BytesBefore)} → {FormatBytesShort(report.BytesAfter)}, {savedPct}% smaller) → {report.OutputDir}"
            : $"Texture optimize: {report.TexturesOptimized} of {report.TexturesFound} processed for '{part.Name}'.";
    }

    /// <summary>Textures → Change textures: file picker for an image,
    /// live-swap in the viewer, and stage for convert bake.</summary>
    private async void OnLayerChangeTextures(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement fe ||
            fe.Tag is not MainViewModel.ModelPart part) return;
        if (Viewport?.CoreWebView2 == null)
        {
            _vm.StatusText = "Viewer not ready yet — load a model first.";
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = $"Pick a new diffuse texture for '{part.Name}'",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.dds)|*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.dds|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            bool noUv = await ApplyOnePartDiffuseAsync(part.OriginalName, dlg.FileName);
            _vm.StatusText = noUv
                ? $"'{part.Name}' has NO UV map — the OBJ was exported without UVs, so the texture can't show. Re-export from Blender with 'Include UVs' checked."
                : $"Swapped diffuse on '{part.Name}' — will bake on Convert.";
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Change textures failed: " + ex.Message;
        }
    }

    /// <summary>Run a quick Assimp pass to count triangles under the
    /// named node's sub-tree. Used to seed the inline slider's range.</summary>
    private static int CountTrianglesForPart(string inputPath, string partName)
    {
        try
        {
            using var importer = new Assimp.AssimpContext();
            var scene = importer.ImportFile(inputPath, Assimp.PostProcessSteps.Triangulate);
            if (scene?.RootNode == null) return 0;

            Assimp.Node? target = null;
            foreach (var c in scene.RootNode.Children)
            {
                if (string.Equals(c.Name, partName, StringComparison.Ordinal))
                {
                    target = c;
                    break;
                }
            }
            if (target == null) return 0;

            int total = 0;
            var stack = new Stack<Assimp.Node>();
            stack.Push(target);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n.HasMeshes)
                    foreach (var i in n.MeshIndices) total += scene.Meshes[i].FaceCount;
                foreach (var c in n.Children) stack.Push(c);
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Bare-bones modal input dialog — title, label, prefilled
    /// TextBox, OK/Cancel. Built programmatically so we don't have to
    /// add yet another XAML window file for a 12-line interaction.</summary>
    /// <summary>Compact themed prompt — same RenameDialog the Vehicles
    /// tab uses, so every rename in the app looks identical.</summary>
    private string? ShowInputDialog(string title, string label, string initial)
    {
        var dlg = new RenameDialog(title, label, initial, isFile: false) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.ResultName : null;
    }

    private static string FormatBytesShort(long b)
    {
        if (b <= 0) return "0 B";
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }

    // ─────────────── Optimization-health banner ───────────────

    /// <summary>
    /// Called from the viewer's "loaded" message after every successful model
    /// load. Computes severity from the tri count + on-disk source size and
    /// builds a friendly title/body for the banner. Banner is purely advisory —
    /// the user can dismiss and convert anyway.
    /// </summary>
    private void EvaluateOptimizationHealth(long tris)
    {
        long sourceBytes = 0;
        try
        {
            if (!string.IsNullOrEmpty(_vm.SourcePath) && File.Exists(_vm.SourcePath))
                sourceBytes = new FileInfo(_vm.SourcePath).Length;
        }
        catch { /* size is informational */ }

        var severity = Services.MeshThresholds.ClassifyImport(tris, sourceBytes);
        _vm.OptimizationSeverity = severity;
        if (severity == Services.MeshThresholds.Severity.Ok)
        {
            _vm.OptimizationHintTitle = "";
            _vm.OptimizationHintBody = "";
            return;
        }

        // Show the dimension(s) that actually tripped the warning. A file
        // can be over on tris alone, on file size alone, or both.
        var triSev = Services.MeshThresholds.ClassifyPropTris(tris);
        var fileSev = Services.MeshThresholds.ClassifySourceFile(sourceBytes);

        // Decimation only makes sense when the tri count itself is the
        // problem. A lean mesh in a huge file (AI exports with 20 MB of
        // embedded textures) gets a banner but no decimate CTA — the
        // slider would be a no-op that can't touch the actual weight.
        _vm.OptimizationHintCanDecimate = triSev != Services.MeshThresholds.Severity.Ok;

        var headline = severity == Services.MeshThresholds.Severity.Fail
            ? "This model is well over the FiveM budget"
            : "Heavier than what optimized props look like";

        var sizeMb = sourceBytes / (1024.0 * 1024);
        var parts = new System.Collections.Generic.List<string>();
        if (triSev != Services.MeshThresholds.Severity.Ok)
            parts.Add($"{tris:N0} tris (target under {Services.MeshThresholds.PropRecommendedTris:N0})");
        if (fileSev != Services.MeshThresholds.Severity.Ok && sourceBytes > 0)
            parts.Add($"{sizeMb:F1} MB on disk (optimized props are 2–10 MB, ~{Services.MeshThresholds.SourceFileSweetSpotBytes / (1024 * 1024)} MB is the sweet spot)");
        // Fall back to the tri count line if neither dimension tripped but
        // severity != Ok somehow (defensive — shouldn't happen).
        if (parts.Count == 0)
            parts.Add($"{tris:N0} tris");

        _vm.OptimizationHintTitle = headline;
        _vm.OptimizationHintBody =
            string.Join(" · ", parts) +
            (_vm.OptimizationHintCanDecimate
                ? ". One-click decimate keeps the visible silhouette."
                : $". The mesh itself is lean ({tris:N0} tris) — the weight is textures. " +
                  $"Optimize shrinks them to {Services.MeshThresholds.PropTextureMaxDim}px now; Convert also compresses whatever you ship.");
    }

    // ── Optimize preview state ─────────────────────────────────────
    //
    // Preview mode: clicking Auto-optimize saves the original SourcePath
    // and immediately kicks off a background decimate at the recommended
    // target. As the slider moves (debounced), additional decimates run
    // serially and the viewer hot-swaps to each result so the user sees
    // the actual mesh they're about to commit to. Confirm locks in the
    // current preview as the new working file; Cancel reloads the
    // original. The original file on disk is never modified.

    private string? _originalSourceBeforePreview;
    private string? _lastPreviewOutputPath;
    private int _previewShownTarget;         // target tris currently visible in viewer
    private int _previewWantedTarget;        // target tris the user most recently asked for
    private bool _previewPumpRunning;        // serializes decimates (one at a time)
    private bool _inPreviewReload;           // suppresses banner re-eval + PropName churn
    private DispatcherTimer? _previewDebounce;
    private const int PreviewDebounceMs = 300;

    /// <summary>Banner action: open the slider AND immediately kick off the
    /// first preview decimate at the recommended target. The original mesh
    /// is preserved for Cancel.</summary>
    private void OnAutoOptimizeBanner(object sender, RoutedEventArgs e)
    {
        if (_vm.IsOptimizing || _vm.IsOptimizePreviewing) return;
        if (string.IsNullOrEmpty(_vm.SourcePath) || !File.Exists(_vm.SourcePath)) return;

        // Texture-weight banner (mesh tris are fine, megabytes are 4K
        // bakes): decimation can't touch the problem, so Optimize shrinks
        // the textures instead. Also route degenerate tri counts here —
        // the slider range math below needs a real mesh.
        if (!_vm.OptimizationHintCanDecimate || _vm.Tris < 200)
        {
            StartTexturePreview();
            return;
        }

        _vm.OptimizeSliderLabel = "Target tris";
        _vm.IsOptimizeSliderSnapping = false;
        _originalSourceBeforePreview = _vm.SourcePath;
        var triBefore = _vm.Tris;
        _vm.OptimizeOriginalTris = triBefore;

        // Slider range: 5%..95% of the input. Below 5% is silhouette-only
        // and almost certainly unwanted; above 95% has nothing to do.
        _vm.OptimizeSliderMin = System.Math.Max(100, triBefore / 20);
        _vm.OptimizeSliderMax = System.Math.Max(_vm.OptimizeSliderMin + 100, (int)(triBefore * 0.95));
        _vm.OptimizeSliderTick = System.Math.Max(50, (_vm.OptimizeSliderMax - _vm.OptimizeSliderMin) / 100);
        var initialTarget = System.Math.Clamp(
            Services.MeshThresholds.SuggestedAutoOptimizeTarget(triBefore),
            _vm.OptimizeSliderMin,
            _vm.OptimizeSliderMax);
        _vm.OptimizeTargetTris = initialTarget;
        _lastPreviewOutputPath = null;
        _previewShownTarget = 0;
        _vm.IsOptimizePreviewing = true;

        RequestPreview(initialTarget);
    }

    /// <summary>True while the preview banner is showing a texture-optimize
    /// result rather than a decimate — the resolution option chips replace
    /// the tri slider.</summary>
    private bool _previewIsTextureMode;
    private int _textureShownDim;    // dimension currently visible in viewer
    private int _textureWantedDim;   // dimension the user selected last
    private bool _texturePumpRunning;
    private long _textureBytesBefore;
    private string? _texturePreviewTempDir;
    // Per-resolution results so switching chips is instant and every chip
    // can display its projected size. Path == "" marks a failed pass so the
    // pump doesn't retry it forever.
    private readonly Dictionary<int, (string Path, long BytesAfter)> _textureResults = new();

    /// <summary>Texture-optimize preview entry: build the 512/1024/2048/4096
    /// option chips and kick off the first pass at the FiveM prop cap. The
    /// pump then fills in the remaining options' sizes in the background.</summary>
    private void StartTexturePreview()
    {
        _originalSourceBeforePreview = _vm.SourcePath;
        _previewIsTextureMode = true;
        _vm.IsTexturePreviewMode = true;
        _vm.OptimizeOriginalTris = 0;   // no tri stats — size stats instead
        _lastPreviewOutputPath = null;
        _previewShownTarget = 0;
        _textureShownDim = 0;
        _textureResults.Clear();
        try { _textureBytesBefore = new FileInfo(_vm.SourcePath!).Length; }
        catch { _textureBytesBefore = 0; }
        _texturePreviewTempDir = Path.Combine(
            Path.GetTempPath(), "FiveOS", "texprev-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_texturePreviewTempDir);

        _vm.TextureDimOptions.Clear();
        foreach (var (dim, hint) in new[]
        {
            (512, "smallest"), (1024, "lean"),
            (2048, "recommended"), (4096, "full res"),
        })
        {
            _vm.TextureDimOptions.Add(new ViewModels.TextureDimOption
            {
                Dim = dim,
                Hint = hint,
                SizeText = hint,
                IsSelected = dim == 2048,
            });
        }

        _vm.OptimizeSliderLabel = "Texture quality";
        _vm.IsOptimizePreviewing = true;

        RequestTexturePass(2048);
    }

    /// <summary>An option chip was selected: show its cached result
    /// instantly if we have it, otherwise let the pump compute it next.</summary>
    private void OnTextureDimChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement fe ||
            fe.DataContext is not ViewModels.TextureDimOption opt) return;
        if (!_vm.IsOptimizePreviewing || !_previewIsTextureMode) return;
        RequestTexturePass(opt.Dim);
    }

    /// <summary>Texture-mode counterpart of RequestPreview — remember the
    /// selected dimension, serve it from cache when possible, and make sure
    /// the pump is running (it also precomputes the other options' sizes).</summary>
    private void RequestTexturePass(int dim)
    {
        _textureWantedDim = dim;
        if (_textureResults.TryGetValue(dim, out var cached) &&
            cached.Path.Length > 0 && File.Exists(cached.Path))
        {
            if (_textureShownDim != dim)
            {
                _textureShownDim = dim;
                _lastPreviewOutputPath = cached.Path;
                _vm.OptimizePreviewCustomStats =
                    $"{FormatBytes(_textureBytesBefore)} → {FormatBytes(cached.BytesAfter)}";
                _ = LoadPreviewInViewerAsync(cached.Path);
            }
        }
        if (_texturePumpRunning) return;
        _ = TexturePumpAsync();
    }

    /// <summary>Run texture-optimize passes one at a time: the selected
    /// resolution first, then the remaining options in the background so
    /// every chip shows its projected size. Mirrors PreviewPumpAsync.</summary>
    private async Task TexturePumpAsync()
    {
        if (_texturePumpRunning) return;
        _texturePumpRunning = true;
        try
        {
            while (_vm.IsOptimizePreviewing && _previewIsTextureMode)
            {
                var input = _originalSourceBeforePreview;
                if (string.IsNullOrEmpty(input) || !File.Exists(input)) break;
                if (_texturePreviewTempDir == null) break;

                // Wanted-but-uncached first; then fill in the other chips.
                int dim = 0;
                if (!_textureResults.ContainsKey(_textureWantedDim)) dim = _textureWantedDim;
                else
                    foreach (var o in _vm.TextureDimOptions)
                        if (!_textureResults.ContainsKey(o.Dim)) { dim = o.Dim; break; }
                if (dim <= 0) break;   // everything computed

                // Only show the spinner/status when the user is actually
                // waiting on this pass, not for background size-fills.
                bool userWaiting = dim == _textureWantedDim && _textureShownDim != dim;
                if (userWaiting)
                {
                    _vm.IsOptimizePreviewDecimating = true;
                    _vm.StatusText = $"Optimizing textures to {dim}px...";
                }

                var outPath = Path.Combine(_texturePreviewTempDir, $"tex{dim}.glb");
                var result = await Task.Run(() =>
                    Services.SourceMeshOptimizer.OptimizeTextures(
                        input, dim,
                        progress: userWaiting
                            ? msg => Dispatcher.Invoke(() =>
                              {
                                  if (_vm.IsOptimizePreviewing)
                                      _vm.StatusText = $"Optimize — {msg}";
                              })
                            : null,
                        outputPath: outPath));

                Services.FosLogger.Info("optimize",
                    $"texture optimize @{dim}px: {result.BytesBefore / 1024:N0}→{result.BytesAfter / 1024:N0} KB, " +
                    $"{result.Elapsed.TotalMilliseconds:F0} ms" +
                    (result.Error != null ? $" — ERROR: {result.Error}" : ""));

                if (!_vm.IsOptimizePreviewing || !_previewIsTextureMode) break;

                if (result.Error != null)
                {
                    _textureResults[dim] = ("", 0);  // don't retry forever
                    if (dim == _textureWantedDim)
                    {
                        _vm.StatusText = $"Optimize failed: {result.Error}";
                        _vm.IsOptimizePreviewDecimating = false;
                    }
                    continue;
                }

                _textureResults[dim] = (result.OutputPath, result.BytesAfter);
                foreach (var o in _vm.TextureDimOptions)
                    if (o.Dim == dim)
                        o.SizeText = FormatBytes(result.BytesAfter) +
                                     (o.Hint.Length > 0 ? $" · {o.Hint}" : "");

                if (dim == _textureWantedDim)
                {
                    _textureShownDim = dim;
                    _lastPreviewOutputPath = result.OutputPath;
                    _vm.OptimizePreviewCustomStats =
                        $"{FormatBytes(result.BytesBefore)} → {FormatBytes(result.BytesAfter)}";
                    _vm.IsOptimizePreviewDecimating = false;
                    await LoadPreviewInViewerAsync(result.OutputPath);
                }
            }
        }
        finally
        {
            _vm.IsOptimizePreviewDecimating = false;
            _texturePumpRunning = false;
        }
    }

    /// <summary>Best-effort removal of the per-resolution preview GLBs.</summary>
    private void CleanupTexturePreviewTemp()
    {
        var dir = _texturePreviewTempDir;
        _texturePreviewTempDir = null;
        _textureResults.Clear();
        _vm.TextureDimOptions.Clear();
        if (dir == null) return;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* temp dir; OS cleanup will get it eventually */ }
    }

    /// <summary>Slider value changed — arm a debounce timer. When the user
    /// pauses for ~300ms, fire a preview decimate at the current target.</summary>
    private void OnOptimizeSliderValueChanged(object sender,
        System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_vm.IsOptimizePreviewing) return;
        if (_previewDebounce == null)
        {
            _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreviewDebounceMs) };
            _previewDebounce.Tick += (_, _) =>
            {
                _previewDebounce!.Stop();
                RequestSliderTarget();
            };
        }
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    /// <summary>Slider release — collapse the debounce and request immediately.</summary>
    private void OnOptimizeSliderDragCompleted(object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (!_vm.IsOptimizePreviewing) return;
        _previewDebounce?.Stop();
        RequestSliderTarget();
    }

    /// <summary>Dispatch the slider's current value to the decimate pump.
    /// The slider is hidden during texture previews (option chips drive
    /// those), so a texture-mode event is stale — ignore it rather than
    /// misread a tri count as a resolution.</summary>
    private void RequestSliderTarget()
    {
        if (_previewIsTextureMode) return;
        RequestPreview(_vm.OptimizeTargetTris);
    }

    /// <summary>Mark <paramref name="target"/> as the most recently requested
    /// preview target. If the pump isn't already running, start it; otherwise
    /// it'll pick up the new target after finishing the current decimate.</summary>
    private void RequestPreview(int target)
    {
        if (target == _previewShownTarget && _lastPreviewOutputPath != null) return;
        _previewWantedTarget = target;
        if (_previewPumpRunning) return;
        _ = PreviewPumpAsync();
    }

    /// <summary>Run preview decimates one at a time. Between each run, check
    /// whether the user has asked for a different target; if so, loop and
    /// decimate again. Exits when caught up (or when preview mode is left).</summary>
    private async Task PreviewPumpAsync()
    {
        if (_previewPumpRunning) return;
        _previewPumpRunning = true;
        try
        {
            while (_vm.IsOptimizePreviewing)
            {
                var target = _previewWantedTarget;
                var input = _originalSourceBeforePreview;
                if (string.IsNullOrEmpty(input) || !File.Exists(input)) break;
                if (target <= 0) break;
                if (target == _previewShownTarget && _lastPreviewOutputPath != null) break;

                _vm.IsOptimizePreviewDecimating = true;
                _vm.StatusText = $"Preview — decimating to {target:N0} tris...";

                var result = await Task.Run(() =>
                    Services.SourceMeshOptimizer.Optimize(
                        input,
                        new Services.SourceMeshOptimizer.Options(TargetTriangles: target),
                        progress: msg => Dispatcher.Invoke(() =>
                        {
                            if (_vm.IsOptimizePreviewing)
                                _vm.StatusText = $"Preview — {msg}";
                        })));

                if (!_vm.IsOptimizePreviewing) break;  // user cancelled while decimating

                Services.FosLogger.Info("optimize",
                    $"preview decimate target={target:N0}: {result.TrianglesBefore:N0}→{result.TrianglesAfter:N0} tris, " +
                    $"{result.BytesBefore / 1024:N0}→{result.BytesAfter / 1024:N0} KB, {result.Elapsed.TotalMilliseconds:F0} ms" +
                    (result.Error != null ? $" — ERROR: {result.Error}" : ""));

                if (result.Error != null)
                {
                    _vm.StatusText = $"Preview failed: {result.Error}";
                    if (_previewWantedTarget == target) break;
                    continue;
                }

                _lastPreviewOutputPath = result.OutputPath;
                _previewShownTarget = target;
                await LoadPreviewInViewerAsync(result.OutputPath);

                if (_previewWantedTarget == target) break;
            }
        }
        finally
        {
            _vm.IsOptimizePreviewDecimating = false;
            _previewPumpRunning = false;
        }
    }

    /// <summary>Hot-swap the viewer to a preview-decimate output without
    /// disturbing banner state, PropName, or OptimizationSeverity. The
    /// "loaded" message handler checks <c>_inPreviewReload</c> and skips
    /// the parts of the post-load flow that would clobber preview UI.</summary>
    private async Task LoadPreviewInViewerAsync(string path)
    {
        if (!File.Exists(path)) return;
        _inPreviewReload = true;
        _vm.IsModelLoading = true;
        try
        {
            await EnsureWebViewAsync();
            var copiedRel = await Task.Run(() => StageModelInSessionDir(path));
            var url = $"https://viewer.local/{copiedRel.Replace('\\', '/')}";
            if (!_viewerReady)
                _pendingModelUrl = url;
            else
                await LoadInViewerAsync(url);
        }
        catch (Exception ex)
        {
            _vm.IsModelLoading = false;
            _inPreviewReload = false;
            _vm.StatusText = $"Preview load failed: {ex.Message}";
        }
    }

    /// <summary>Banner action: lock in the current preview (or run a final
    /// decimate if the pump hasn't caught up yet) as the new working file.</summary>
    private async void OnConfirmOptimizeBanner(object sender, RoutedEventArgs e)
    {
        _previewDebounce?.Stop();

        // Texture-optimize preview: the selected resolution's GLB lives in
        // the preview temp dir — copy it under the canonical sibling name
        // and commit that.
        if (_previewIsTextureMode)
        {
            if (_lastPreviewOutputPath != null && File.Exists(_lastPreviewOutputPath) &&
                !_vm.IsOptimizePreviewDecimating &&
                !string.IsNullOrEmpty(_originalSourceBeforePreview))
            {
                var final = Services.SourceMeshOptimizer.OptimizedOutputPath(_originalSourceBeforePreview);
                try
                {
                    File.Copy(_lastPreviewOutputPath, final, overwrite: true);
                }
                catch (Exception ex)
                {
                    _vm.StatusText = $"Optimize failed: {ex.Message}";
                    return;
                }
                CommitPreview(final);
            }
            return;
        }

        // Fast path: the viewer already shows the preview the user wants.
        // Just commit it without another decimate.
        if (_lastPreviewOutputPath != null && File.Exists(_lastPreviewOutputPath) &&
            _previewShownTarget == _vm.OptimizeTargetTris && !_vm.IsOptimizePreviewDecimating)
        {
            CommitPreview(_lastPreviewOutputPath);
            return;
        }

        // Slow path: pump is mid-flight or slider moved after the last
        // shown preview. Run a fresh decimate at the current target.
        var input = _originalSourceBeforePreview;
        if (string.IsNullOrEmpty(input) || !File.Exists(input))
        {
            _vm.IsOptimizePreviewing = false;
            return;
        }

        var target = _vm.OptimizeTargetTris;
        _vm.IsOptimizing = true;
        _vm.StatusText = $"Optimizing — decimating to {target:N0} tris...";

        var result = await Task.Run(() =>
            Services.SourceMeshOptimizer.Optimize(
                input,
                new Services.SourceMeshOptimizer.Options(TargetTriangles: target),
                progress: msg => Dispatcher.Invoke(() => _vm.StatusText = $"Optimizing — {msg}")));

        _vm.IsOptimizing = false;

        if (result.Error != null)
        {
            _vm.StatusText = $"Optimize failed: {result.Error}";
            return;
        }

        CommitPreview(result.OutputPath);
    }

    /// <summary>Finalize the optimized output as the new working source.
    /// Clears preview state and re-evaluates the optimization-health banner
    /// against the locked-in tri count (which we already have on the VM
    /// from the most recent viewer "loaded" message).</summary>
    private void CommitPreview(string committedPath)
    {
        var wasShown = _previewShownTarget > 0 && _lastPreviewOutputPath == committedPath;

        _vm.SourcePath = committedPath;
        _vm.PropName = SanitizeAssetName(
            Path.GetFileNameWithoutExtension(committedPath)
                .Replace(".fiveos-optimized", "", StringComparison.OrdinalIgnoreCase));
        _vm.IsOptimizePreviewing = false;
        _originalSourceBeforePreview = null;
        _lastPreviewOutputPath = null;
        _previewShownTarget = 0;
        _previewWantedTarget = 0;
        _previewIsTextureMode = false;
        _vm.IsTexturePreviewMode = false;
        _textureShownDim = 0;
        _textureWantedDim = 0;
        _vm.OptimizePreviewCustomStats = "";
        CleanupTexturePreviewTemp();

        if (wasShown)
        {
            // Viewer already shows the result; just re-evaluate the banner.
            _vm.StatusText = $"✓ Optimized · {_vm.Tris:N0} tris";
            EvaluateOptimizationHealth(_vm.Tris);
        }
        else
        {
            // The user clicked Confirm before the preview pump caught up,
            // so we just decimated fresh — reload the viewer normally.
            TryLoad(committedPath);
        }
    }

    /// <summary>Banner action: exit preview mode and restore the original
    /// mesh in the viewer if a preview was shown.</summary>
    private void OnCancelOptimizeBanner(object sender, RoutedEventArgs e)
    {
        _previewDebounce?.Stop();

        var origPath = _originalSourceBeforePreview;
        var hadPreview = _lastPreviewOutputPath != null;

        _vm.IsOptimizePreviewing = false;
        _originalSourceBeforePreview = null;
        _lastPreviewOutputPath = null;
        _previewShownTarget = 0;
        _previewWantedTarget = 0;
        _previewIsTextureMode = false;
        _vm.IsTexturePreviewMode = false;
        _textureShownDim = 0;
        _textureWantedDim = 0;
        _vm.OptimizePreviewCustomStats = "";
        CleanupTexturePreviewTemp();
        // The pumps' loop guards (_vm.IsOptimizePreviewing) handle in-flight
        // exit. Don't clear the pump-running flags — let them finish and
        // self-clear; their results are dropped because preview mode is off.

        if (hadPreview && !string.IsNullOrEmpty(origPath) && File.Exists(origPath))
            TryLoad(origPath);
    }

    /// <summary>Banner action: drill into the Optimize view so the user
    /// can fine-tune decimation settings rather than accept the one-click
    /// default. Banner stays visible (the rule is "user-closes or
    /// optimized-away only"); switching views is just navigation, not
    /// dismissal.</summary>
    private void OnOpenOptimizeTabBanner(object sender, RoutedEventArgs e)
    {
        _vm.ActiveView = AppView.Optimize;
    }

    /// <summary>Banner action: dismiss for the current model. Re-shows on
    /// next file load.</summary>
    private void OnDismissOptimizeBanner(object sender, RoutedEventArgs e)
    {
        _vm.OptimizationHintDismissed = true;
    }

    // ─────────────── Helpers ───────────────

    private static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        foreach (var s in SupportedExtensions)
            if (ext == s) return true;
        return false;
    }

    private static string SanitizeAssetName(string raw)
    {
        var chars = raw.ToLowerInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_')
                chars[i] = '_';
        var s = new string(chars).Trim('_');
        return string.IsNullOrEmpty(s) ? "model" : s;
    }

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "...";

    private static string FormatBytes(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:F1} KB";
        return $"{b / (1024.0 * 1024):F1} MB";
    }

    // ─────────────── Dashboard ───────────────

    /// <summary>
    /// Click handler shared by all four dashboard tiles. The Border's
    /// <c>Tag</c> carries the <see cref="AppView"/> name as
    /// a string; we hand that to the VM's OpenView command so navigation
    /// stays in one place.
    /// </summary>
    private void OnDashboardCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement fe) return;
        if (fe.Tag is not string view) return;
        _vm.OpenViewCommand.Execute(view);
    }

    private void OnRailMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        => _vm.IsRailHovered = true;

    private void OnRailMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        => _vm.IsRailHovered = false;

    /// <summary>Rail item click → drive ActiveView. Each rail row is a
    /// plain Border (no ListBox involvement); its <c>Tag</c> carries the
    /// AppView name as a string, which we hand to OpenViewCommand.</summary>
    private void OnRailItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement fe) return;
        if (fe.Tag is not string view) return;
        Services.FosLogger.Info("nav", $"rail click -> {view}");

        if (string.Equals(view, "Settings", StringComparison.OrdinalIgnoreCase))
        {
            OpenSettingsModal();
            return;
        }

        _vm.IsSettingsOpen = false;   // navigating from the rail closes Settings
        _vm.OpenViewCommand.Execute(view);
    }

    private void OnTogglePinClick(object sender, MouseButtonEventArgs e)
        => _vm.ToggleRailPinCommand.Execute(null);

    /// <summary>Click handler for the Assets segmented toggle (3D Model /
    /// Vehicles) and the Optimize rail entry. Routes through OpenViewCommand,
    /// but gates leaving an in-flight 3D Model session for Optimize behind a
    /// confirmation — Optimize abandons the active 3D session. Switching
    /// between 3D Model and Vehicles (or any switch with no loaded model) is
    /// a no-op for the prompt and falls straight through.</summary>
    private void OnInnerToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement fe) return;
        if (fe.Tag is not string view) return;

        bool leavingPropMode = _vm.IsPropsView && _vm.IsPropMode;
        bool destinationDiscards = view is "Optimize";
        bool somethingToLose = _vm.HasModel || _vm.IsModelLoading || _vm.IsConverting;

        if (leavingPropMode && destinationDiscards && somethingToLose)
        {
            var pick = AppDialog.Show(
                "Switching tabs will cancel your current 3D Model session and clear the loaded model. Continue?",
                "Switch tabs?",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question,
                this);
            if (pick != System.Windows.MessageBoxResult.Yes) return;
        }

        _vm.OpenViewCommand.Execute(view);
    }

    // ── Animated props keys timeline ────────────────────────────────

    private readonly TimelineController _propAnimCtl = new();
    private DispatcherTimer? _propAnimPlayTimer;
    private bool _propAnimScrubbing;
    private bool _propAnimDraggingKey;
    private PropAnimKey? _propAnimDragKey;
    private bool _propAnimTimelineHooked;

    private void EnsurePropAnimTimelineHooks()
    {
        if (_propAnimTimelineHooked) return;
        _propAnimTimelineHooked = true;
        if (PropAnimTimelineLayer != null)
            PropAnimTimelineLayer.RenderCallback = DrawPropAnimTimeline;
        _propAnimCtl.Changed += () => PropAnimTimelineLayer?.InvalidateVisual();
        _vm.PropAnimKeys.CollectionChanged += (_, _) =>
        {
            _vm.NotifyPropAnimKeysChanged();
            PropAnimTimelineLayer?.InvalidateVisual();
        };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.PropAnimPlayhead)
                or nameof(MainViewModel.PropAnimDuration)
                or nameof(MainViewModel.IsAnimatedPropsView)
                or nameof(MainViewModel.PropAnimPlayheadLabel))
                PropAnimTimelineLayer?.InvalidateVisual();
            if (e.PropertyName == nameof(MainViewModel.IsPropAnimPlaying))
                UpdatePropAnimPlayIcon();
        };
    }

    /// <summary>Write timeline keys to a temp JSON sidecar for the engine,
    /// or null when not in Animated mode / fewer than 2 keys.</summary>
    private string? WritePropAnimKeysSidecar()
    {
        if (!_vm.IsAnimatedPropsView) return null;
        _vm.EnsureDefaultPropAnimKeys();
        if (_vm.PropAnimKeys.Count < 2) return null;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "FiveOS");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"anim-keys-{Guid.NewGuid():N}.json");
            var payload = new
            {
                fps = _vm.PropAnimFps,
                duration = _vm.PropAnimDuration,
                keys = _vm.PropAnimKeys
                    .OrderBy(k => k.Time)
                    .Select(k => new { t = k.Time, rx = k.RotX, ry = k.RotY, rz = k.RotZ })
                    .ToList(),
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload));
            return path;
        }
        catch (Exception ex)
        {
            Services.FosLogger.Warn("anim", "anim-keys sidecar failed: " + ex.Message);
            return null;
        }
    }

    private void DrawPropAnimTimeline(System.Windows.Media.DrawingContext dc)
    {
        EnsurePropAnimTimelineHooks();
        var canvas = PropAnimTimelineCanvas;
        if (canvas == null) return;
        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w < 8 || h < 8) return;

        double dur = Math.Max(0.1, _vm.PropAnimDuration);
        _propAnimCtl.ClampScroll(dur);

        var bg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A));
        bg.Freeze();
        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));

        var tickPen = new System.Windows.Media.Pen(
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55)), 1);
        tickPen.Freeze();
        var labelBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0x9A, 0x9A));
        labelBrush.Freeze();

        double rulerH = 22;
        double laneTop = rulerH + 4;
        double laneH = Math.Max(16, h - laneTop - 8);

        // Major ticks every 0.5s (or 1s when zoomed out).
        double step = _propAnimCtl.Zoom < 2 ? 1.0 : 0.5;
        double t0 = Math.Floor(_propAnimCtl.ScrollOffset / step) * step;
        for (double t = t0; t <= _propAnimCtl.ScrollOffset + _propAnimCtl.VisibleDuration(dur) + step; t += step)
        {
            if (t < -1e-6 || t > dur + 1e-6) continue;
            double x = _propAnimCtl.TimeToX(t, dur, w);
            dc.DrawLine(tickPen, new System.Windows.Point(x, 0), new System.Windows.Point(x, rulerH));
            var ft = new System.Windows.Media.FormattedText(
                t.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "s",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface("Segoe UI"),
                10, labelBrush, 1.25);
            dc.DrawText(ft, new System.Windows.Point(x + 3, 3));
        }

        // Key lane background
        var laneBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x25));
        laneBrush.Freeze();
        dc.DrawRoundedRectangle(laneBrush, null, new Rect(8, laneTop, w - 16, laneH), 3, 3);

        var keyFill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5B, 0xBF, 0xB5));
        keyFill.Freeze();
        var keyStroke = new System.Windows.Media.Pen(
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0)), 1);
        keyStroke.Freeze();

        double midY = laneTop + laneH * 0.5;
        foreach (var key in _vm.PropAnimKeys)
        {
            double x = _propAnimCtl.TimeToX(key.Time, dur, w);
            var geo = new System.Windows.Media.StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new System.Windows.Point(x, midY - 7), true, true);
                ctx.LineTo(new System.Windows.Point(x + 6, midY), true, false);
                ctx.LineTo(new System.Windows.Point(x, midY + 7), true, false);
                ctx.LineTo(new System.Windows.Point(x - 6, midY), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(keyFill, keyStroke, geo);
        }

        // Playhead
        double px = _propAnimCtl.TimeToX(_vm.PropAnimPlayhead, dur, w);
        var playPen = new System.Windows.Media.Pen(
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC8, 0x57)), 1.5);
        playPen.Freeze();
        dc.DrawLine(playPen, new System.Windows.Point(px, 0), new System.Windows.Point(px, h));
        var head = new System.Windows.Media.StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(new System.Windows.Point(px - 5, 0), true, true);
            ctx.LineTo(new System.Windows.Point(px + 5, 0), true, false);
            ctx.LineTo(new System.Windows.Point(px, 8), true, false);
        }
        head.Freeze();
        dc.DrawGeometry(playPen.Brush, null, head);
    }

    private void OnPropAnimTimelineSizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsurePropAnimTimelineHooks();
        PropAnimTimelineLayer?.InvalidateVisual();
    }

    private void OnPropAnimTimelineDown(object sender, MouseButtonEventArgs e)
    {
        EnsurePropAnimTimelineHooks();
        var canvas = PropAnimTimelineCanvas;
        if (canvas == null) return;
        canvas.CaptureMouse();
        var pos = e.GetPosition(canvas);
        double dur = Math.Max(0.1, _vm.PropAnimDuration);
        double t = _propAnimCtl.SnapTime(_propAnimCtl.XToTime(pos.X, dur, canvas.ActualWidth), _vm.PropAnimFps);

        // Hit-test nearest key within ~8px
        PropAnimKey? hit = null;
        double best = 10;
        foreach (var key in _vm.PropAnimKeys)
        {
            double kx = _propAnimCtl.TimeToX(key.Time, dur, canvas.ActualWidth);
            double d = Math.Abs(kx - pos.X);
            if (d < best) { best = d; hit = key; }
        }

        if (hit != null && e.ClickCount == 1)
        {
            _propAnimDraggingKey = true;
            _propAnimDragKey = hit;
            _vm.PropAnimPlayhead = hit.Time;
        }
        else
        {
            _propAnimScrubbing = true;
            _vm.PropAnimPlayhead = Math.Clamp(t, 0, dur);
            _vm.ApplyPropAnimPlayheadToTransform();
        }
        PropAnimTimelineLayer?.InvalidateVisual();
        e.Handled = true;
    }

    private void OnPropAnimTimelineMove(object sender, MouseEventArgs e)
    {
        if (!_propAnimScrubbing && !_propAnimDraggingKey) return;
        var canvas = PropAnimTimelineCanvas;
        if (canvas == null) return;
        var pos = e.GetPosition(canvas);
        double dur = Math.Max(0.1, _vm.PropAnimDuration);
        double t = _propAnimCtl.SnapTime(_propAnimCtl.XToTime(pos.X, dur, canvas.ActualWidth), _vm.PropAnimFps);
        t = Math.Clamp(t, 0, dur);
        if (_propAnimDraggingKey && _propAnimDragKey != null)
        {
            _propAnimDragKey.Time = t;
            _vm.PropAnimPlayhead = t;
        }
        else
        {
            _vm.PropAnimPlayhead = t;
            _vm.ApplyPropAnimPlayheadToTransform();
        }
        PropAnimTimelineLayer?.InvalidateVisual();
    }

    private void OnPropAnimTimelineUp(object sender, MouseEventArgs e)
    {
        if (_propAnimDraggingKey)
            _vm.SortPropAnimKeys();
        _propAnimScrubbing = false;
        _propAnimDraggingKey = false;
        _propAnimDragKey = null;
        PropAnimTimelineCanvas?.ReleaseMouseCapture();
        PropAnimTimelineLayer?.InvalidateVisual();
    }

    private void OnPropAnimPlayToggle(object sender, RoutedEventArgs e)
    {
        EnsurePropAnimTimelineHooks();
        if (_vm.IsPropAnimPlaying)
        {
            StopPropAnimPlayback();
            return;
        }
        _vm.EnsureDefaultPropAnimKeys();
        _vm.IsPropAnimPlaying = true;
        _propAnimPlayTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _propAnimPlayTimer.Tick -= OnPropAnimPlayTick;
        _propAnimPlayTimer.Tick += OnPropAnimPlayTick;
        _propAnimPlayTimer.Start();
        UpdatePropAnimPlayIcon();
    }

    private void OnPropAnimPlayTick(object? sender, EventArgs e)
    {
        double dur = Math.Max(0.1, _vm.PropAnimDuration);
        _vm.PropAnimPlayhead += 0.033;
        if (_vm.PropAnimPlayhead >= dur)
            _vm.PropAnimPlayhead = 0;
        _vm.ApplyPropAnimPlayheadToTransform();
        _propAnimCtl.EnsurePlayheadVisible(_vm.PropAnimPlayhead, dur, PropAnimTimelineCanvas?.ActualWidth ?? 400);
        PropAnimTimelineLayer?.InvalidateVisual();
    }

    private void OnPropAnimStop(object sender, RoutedEventArgs e)
    {
        StopPropAnimPlayback();
        _vm.PropAnimPlayhead = 0;
        _vm.ApplyPropAnimPlayheadToTransform();
        PropAnimTimelineLayer?.InvalidateVisual();
    }

    private void StopPropAnimPlayback()
    {
        _propAnimPlayTimer?.Stop();
        _vm.IsPropAnimPlaying = false;
        UpdatePropAnimPlayIcon();
    }

    private void UpdatePropAnimPlayIcon()
    {
        if (PropAnimPlayIcon == null) return;
        PropAnimPlayIcon.Symbol = _vm.IsPropAnimPlaying
            ? Wpf.Ui.Controls.SymbolRegular.Pause24
            : Wpf.Ui.Controls.SymbolRegular.Play24;
    }
}
