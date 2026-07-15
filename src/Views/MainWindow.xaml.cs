// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Diagnostics;
using System.IO;
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

        // Locked to the Fluent DARK theme (user preference — dark only). Mica
        // backdrop comes from WindowBackdropType="Mica" on the FluentWindow, and
        // the accent follows the Windows system accent. We deliberately do NOT
        // use SystemThemeWatcher so the app stays dark even when Windows is set
        // to Light.
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);

        DataContext = _vm;
        Title = BuildVersionTitle();
        // WebView2 is initialized lazily on first model load (see EnsureWebViewAsync)
        // so the Edge process tree (manager + GPU + storage utility, ~40 MB)
        // doesn't spawn for users who never open the 3D viewer this session.

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
            else if (IsTransformProperty(e.PropertyName) && !_suppressTransformPush)
                await PushTransformToViewerAsync();
            else if (e.PropertyName == nameof(MainViewModel.ActiveView)
                     && _vm.ActiveView == AppView.Emotes
                     && _vm.OpenEmotesAsAnimLibrary)
            {
                _vm.OpenEmotesAsAnimLibrary = false;
                EmotesWorkspace.OpenAnimLibraryMode();
            }
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
        // Dispose the WebView2 (frees the Edge process tree) and delete this
        // session's viewer temp dir — it holds the full viewer bundle plus the
        // staged model + all sibling textures (often hundreds of MB). Without
        // this every run leaks a Viewer-<guid> dir under %TEMP%\FiveOS forever.
        try { Viewport?.Dispose(); } catch { /* already gone */ }
        if (!string.IsNullOrEmpty(_viewerSessionDir))
        {
            try { if (Directory.Exists(_viewerSessionDir)) Directory.Delete(_viewerSessionDir, true); }
            catch { /* locked/best-effort — CacheService.Clear sweeps leftovers */ }
        }
        base.OnClosed(e);
    }

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

    private static string BuildVersionTitle()
    {
        var asm = typeof(MainWindow).Assembly;
        var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
        string version;
        if (info.Length > 0)
        {
            var raw = ((System.Reflection.AssemblyInformationalVersionAttribute)info[0]).InformationalVersion;
            // Strip "+commit-sha" suffix that SourceLink/CI sometimes appends.
            var plus = raw.IndexOf('+');
            version = plus > 0 ? raw[..plus] : raw;
        }
        else
        {
            var v = asm.GetName().Version;
            version = v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        const string slogan = "All in One FiveM Modding Tool.";
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
        if (System.Windows.Application.Current?.TryFindResource("SystemAccentColorBrush") is not System.Windows.Media.SolidColorBrush brush) return;
        var c = brush.Color;
        var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        _ = Viewport.CoreWebView2.ExecuteScriptAsync(
            $"document.documentElement.style.setProperty('--pose-accent','{hex}')");
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

    private void EnsureEmotesView() => _vm.ActiveView = AppView.Emotes;

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
            TryLoad(dlg.ResultModelPath);
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

    private SettingsView? _settingsPage;

    /// <summary>
    /// Lazily build the Settings page on first open. Done this way so the
    /// SettingsView (and its on-load cache-size scan) doesn't run during
    /// MainWindow construction — that was making the splash feel broken.
    /// </summary>
    private SettingsView EnsureSettingsPage()
    {
        if (_settingsPage == null)
        {
            _settingsPage = new SettingsView();
            SettingsHost.Content = _settingsPage;
        }
        return _settingsPage;
    }

    private void OnSettingsOpen(object sender, RoutedEventArgs e)
    {
        Services.FosLogger.Info("settings", "settings overlay opened");
        EnsureSettingsPage();
        _vm.IsSettingsOpen = true;
    }

    private void OnSettingsClose(object sender, RoutedEventArgs e)
    {
        Services.FosLogger.Info("settings", "settings overlay closed");
        _vm.IsSettingsOpen = false;
    }

    /// <summary>Click on the dimmed backdrop (outside the modal card) closes the
    /// Settings dialog. Clicks inside the card bubble up with a different
    /// OriginalSource, so they're ignored.</summary>
    private void OnSettingsBackdropClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender))
            _vm.IsSettingsOpen = false;
    }

    /// <summary>
    /// Open the Settings overlay, optionally focusing a specific provider
    /// card. Called by child views (MeshOptimizeView) that need to send the
    /// user to Settings when a key is missing.
    /// </summary>
    public void NavigateToSettings(SettingsView.FocusSection section = SettingsView.FocusSection.None)
    {
        var page = EnsureSettingsPage();
        _vm.IsSettingsOpen = true;
        // Defer the focus call until after the SettingsView's first layout
        // pass — Focus() pokes named child controls that aren't yet
        // realised on a freshly-built UserControl.
        Dispatcher.BeginInvoke(new Action(() => page.Focus(section)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Open Settings and scroll to the API-key card for the given AI
    /// provider id (meshy, tripo3d, rodin, replicate, stability). Used by
    /// ImageTo3DView when the user-selected provider has no key saved.
    /// </summary>
    public void NavigateToAiProviderSettings(string providerId)
    {
        var page = EnsureSettingsPage();
        _vm.IsSettingsOpen = true;
        Dispatcher.BeginInvoke(new Action(() => page.FocusAiProvider(providerId)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

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
                    $"You're running the latest release.\n\nCurrent: v{result.Current.Major}.{result.Current.Minor}.{result.Current.Build}",
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
            OnConvert(this, new RoutedEventArgs());
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
        if (ctrl && e.Key == Key.OemComma)
        {
            if (_vm.IsSettingsOpen)
            {
                _vm.IsSettingsOpen = false;
            }
            else
            {
                EnsureSettingsPage();
                _vm.IsSettingsOpen = true;
            }
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
        if (string.IsNullOrEmpty(_vm.SourcePath) || string.IsNullOrWhiteSpace(_vm.PropName))
            return;

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
            PartMaterials:  BuildPartMaterialMap(),
            BreakableGlass: _vm.BreakableGlass,
            GlassOpacity:   _vm.GlassOpacity,
            PartDiffuseTextures: _vm.PartDiffuseOverrides.Count > 0
                ? new Dictionary<string, string>(_vm.PartDiffuseOverrides, StringComparer.OrdinalIgnoreCase)
                : null,
            AnimatedProp: _vm.AnimatedProp,
            // Gate AutoSpin on AnimatedProp: the auto-spin controls only SHOW
            // under Animated prop, so a user who enabled auto-spin then turned
            // Animated prop back off must not still ship a spinning prop.
            AutoSpin: _vm.AnimatedProp && _vm.AutoSpin,
            SpinAxis: _vm.SpinAxisIndex switch { 0 => "X", 1 => "Y", _ => "Z" },
            SpinSeconds: _vm.SpinSeconds,
            SpinReverse: _vm.SpinReverse);
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
            _vm.StatusText = "FiveOS · " + truncated;

            if (!EngineStepHeader.IsMatch(line))
                return;

            _engineStepBaseStatus = "FiveOS · " + truncated;
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

    /// <summary>
    /// Compile the running prop-pack into a single FiveM resource.
    /// Reuses the same success-screen overlay the per-prop convert flow
    /// uses, then clears the staging directory so the next pack starts
    /// fresh.
    /// </summary>
    private void OnFinalizePack(object sender, RoutedEventArgs e)
    {
        var session = _vm.PackSession;
        if (session.Entries.Count == 0)
        {
            _vm.StatusText = "Pack is empty — add at least one prop before finalising.";
            return;
        }

        _vm.StatusText = $"Finalising pack '{session.PackName}' ({session.Count} props)…";
        FiveOS.Services.PropPackBuilder.BuildResult result;
        try
        {
            result = FiveOS.Services.PropPackBuilder.Build(session, _vm.LodDistVlow);
        }
        catch (Exception ex)
        {
            CrashDialog.ShowEngineFailure(this, $"Pack finalize failed: {ex.Message}", ex.ToString());
            _vm.StatusText = $"✗ Pack finalize failed: {ex.Message}";
            return;
        }

        if (!result.Success || result.ResultPath is null)
        {
            CrashDialog.ShowEngineFailure(this, result.Error ?? "Pack finalize failed.", "");
            _vm.StatusText = $"✗ Pack finalize failed: {result.Error}";
            return;
        }

        _vm.ResultZipPath = result.ResultPath;
        _vm.ShowSuccessScreen = true;
        string sizePart = "";
        try
        {
            if (result.Mode == EngineRunner.OutputMode.SingleZip && File.Exists(result.ResultPath))
                sizePart = " · " + FormatBytes(new FileInfo(result.ResultPath).Length);
        }
        catch { /* size cosmetic */ }
        _vm.StatusText = result.Mode switch
        {
            EngineRunner.OutputMode.ServerShared   => $"✓ Pack merged into {result.ResultPath}",
            EngineRunner.OutputMode.ServerPerAsset => $"✓ Pack '{Path.GetFileName(result.ResultPath)}' ready in server folder",
            _                                      => $"✓ Pack ready{sizePart} · {Path.GetFileName(result.ResultPath)}",
        };
        if (result.Warning is not null)
        {
            _vm.StatusText += " · ⚠ see warning";
            AppDialog.Show(result.Warning, "Pack delivered with a warning",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning, this);
        }

        // Drop the staging tree + clear entries so the next pack starts
        // clean. The success overlay still references ResultZipPath for
        // the "Open folder" action, so the on-disk pack zip / folder is
        // untouched — only the in-progress staging goes away.
        session.Clear();
        _vm.IsPackMode = false;
    }

    private void OnRemovePackEntry(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement fe && fe.DataContext is Services.PropPackEntry entry)
            _vm.PackSession.Remove(entry);
    }

    private void OnClearPack(object sender, RoutedEventArgs e) => _vm.PackSession.Clear();

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

    private async void TryLoad(string path)
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

        _vm.SourcePath = path;
        _vm.PropName = SanitizeAssetName(originalNameNoExt);
        _vm.HasModel = true;
        _vm.IsModelLoading = true;
        _vm.Verts = 0;
        _vm.Tris = 0;
        ClearPartDiffuseOverrides();
        // Reset the optimization-health banner for the new model so a hint
        // dismissed on a previous file doesn't carry over.
        _vm.OptimizationSeverity = Services.MeshThresholds.Severity.Ok;
        _vm.OptimizationHintDismissed = false;
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
                // Auto-tag glass parts. The viewer flags a part `glass:true`
                // when any mesh under it uses a glass material (its own
                // looksLikeGlass heuristic — one source of truth). Defaulting
                // the preset to Glass here makes the export write glass.sps +
                // GLASS_SHOOT_THROUGH collision without the user hand-tagging
                // each window. Set BEFORE subscribing so it doesn't fire a
                // redundant live-preview round-trip (the viewer already renders
                // it reflective on load); the user can still override to
                // Standard in the layers panel.
                if (el.TryGetProperty("glass", out var gl) && gl.ValueKind == System.Text.Json.JsonValueKind.True)
                    part.MaterialPreset = MaterialPreset.Glass;
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
                // Auto-tag glass the same way the load path does — the viewer
                // already rendered it reflective, so set the preset BEFORE
                // subscribing to avoid a redundant live-preview round-trip.
                if (el.TryGetProperty("glass", out var gl) && gl.ValueKind == System.Text.Json.JsonValueKind.True)
                    part.MaterialPreset = MaterialPreset.Glass;
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

    /// <summary>ASSETS → Add Missing Textures: multi-image picker that
    /// fuzzy-matches each picked file to a part by filename stem, live-
    /// swaps the preview, and stages the image for convert
    /// (<c>--part-diffuse</c>). Useful when the source file ships without
    /// bundled textures (.obj is the common case).</summary>
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
    }

    /// <summary>Stage <paramref name="imagePath"/> for convert and live-swap
    /// the preview diffuse on the named part. Returns true when the part has
    /// NO UV map (texture can't display — the OBJ was exported without UVs).</summary>
    private async Task<bool> ApplyOnePartDiffuseAsync(string partOriginalName, string imagePath)
    {
        var staged = StagePartDiffuseOverride(partOriginalName, imagePath);
        _vm.PartDiffuseOverrides[partOriginalName] = staged;

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
    private string? ShowInputDialog(string title, string label, string initial)
    {
        var win = new Window
        {
            Title = title,
            Owner = this,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)FindResource("ApplicationBackgroundBrush"),
        };

        var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new System.Windows.Controls.TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
        var box = new System.Windows.Controls.TextBox { Text = initial, FontSize = 13 };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        stack.Children.Add(box);

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new System.Windows.Controls.Button { Content = "OK", Width = 72, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", Width = 72, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);
        win.Content = stack;

        string? result = null;
        ok.Click += (_, _) => { result = box.Text; win.DialogResult = true; };

        return win.ShowDialog() == true ? result : null;
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

    // ─────────────── Activity rail ───────────────

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
        _vm.IsSettingsOpen = false;   // navigating from the rail closes the Settings modal
        _vm.OpenViewCommand.Execute(view);
    }

    private void OnTogglePinClick(object sender, MouseButtonEventArgs e)
        => _vm.ToggleRailPinCommand.Execute(null);

    /// <summary>Click handler for the 3D Model / Weapons / Optimize segmented
    /// toggle. Routes through OpenViewCommand like the dashboard tiles do, but
    /// gates the 3D Model → Weapons / Optimize transition behind a confirmation
    /// when work is in flight: Weapons clears the loaded model outright (via
    /// the ExportMode-change subscriber), and switching to Optimize abandons
    /// the active 3D session, so prompt before either when there's something
    /// to lose. Going TO 3D Model, or any switch with no loaded model, is a
    /// no-op for the prompt and falls straight through.</summary>
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
}
