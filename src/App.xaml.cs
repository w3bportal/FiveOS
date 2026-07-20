// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using FiveOS.Services;
using FiveOS.ViewModels;
using FiveOS.Views;

namespace FiveOS;

public partial class App : Application
{
    /// <summary>Web portal URL opened from Help menu.</summary>
    public const string WebPortalUrl = "https://github.com/w3bportal/FiveOS";

    /// <summary>Community Discord — surfaced in the About dialog.</summary>
    public const string DiscordInviteUrl = "https://discord.gg/GEXUCC6TJ6";

    /// <summary>Author / org displayed in About dialog.</summary>
    public const string AuthorName = "Web Portal (@w3bportal)";

    // Single-instance guard. WebView2 only allows one process tree per
    // user-data folder (%TEMP%\FiveOS\WebView2), so a second launch
    // otherwise fails with ERROR_BUSY (0x800700AA) and a misleading
    // "Install the WebView2 runtime" toast. The "Local\" prefix scopes
    // the mutex to the current session — multiple users on the same
    // machine each get their own instance.
    private const string SingletonMutexName = @"Local\FiveOS-3f7c8e2a-b6d9-4c1f-9a2e-7d4b5e6f8a91";

    private Mutex? _singletonMutex;
    private bool _ownsMutex;

    /// <summary>Subscriber so we can detach in OnExit before tearing down
    /// DiscordPresenceService — avoids a final stray Publish() against a
    /// disposed RPC client.</summary>
    private PropertyChangedEventHandler? _discordVmHandler;
    private MainViewModel? _discordVm;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singletonMutex = new Mutex(initiallyOwned: true, SingletonMutexName, out bool createdNew);
        if (!createdNew)
        {
            _singletonMutex.Dispose();
            _singletonMutex = null;
            Services.FosLogger.Info("boot", "second instance detected — focusing existing window");
            FocusExistingInstance();
            Shutdown();
            return;
        }
        _ownsMutex = true;
        var ver = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";
        Services.FosLogger.Info("boot", $"FiveOS {ver} starting (PID {Environment.ProcessId})");

        // AssimpNet can't find assimp.dll inside single-file extract dirs
        // (raw LoadLibrary ignores AddDllDirectory). Preload by absolute
        // path so Motion "Add to timeline" / anim import works.
        Services.NativeAssimpLoader.Preload();

        // Bring up Sentry before the exception handlers below so any
        // exception in early startup still has a chance to be reported.
        // No-op when FIVEOS_SENTRY_DSN is unset (forks / dev runs / OSS
        // builds without our DSN), so this costs nothing in those cases.
        Services.SentryReporter.Init();

        // ─── Global exception safety net ──────────────────────────────
        // The View layer has 15+ `async void` event handlers (Click,
        // PropertyChanged, etc.) — any escaping exception kills the
        // dispatcher with no stack trace and no chance for the user
        // to recover their work. These three hooks route every
        // unhandled exception path through CrashLog (a rolling 256 KB
        // file at %AppData%\FiveOS\crashes.log) and, for the recoverable
        // dispatcher path, mark the exception handled so the app stays
        // alive instead of dying silently on a single bad event handler.
        //
        // Fatal exceptions (StackOverflow, OutOfMemory, AccessViolation,
        // ExecutionEngine) are NOT swallowed — the CLR will still tear
        // down because the process state is no longer trustworthy.
        DispatcherUnhandledException += (s, ev) =>
        {
            Services.CrashLog.Record("DispatcherUnhandledException", ev.Exception);
            Services.SentryReporter.Capture("DispatcherUnhandledException", ev.Exception);
            if (IsFatal(ev.Exception)) return;   // let the CLR handle it
            ev.Handled = true;
            try
            {
                Services.FosLogger.Warn("crash", $"dispatcher swallow: {ev.Exception.GetType().Name}: {ev.Exception.Message}");
            }
            catch { }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ev) =>
        {
            Services.CrashLog.Record("UnobservedTaskException", ev.Exception);
            Services.SentryReporter.Capture("UnobservedTaskException", ev.Exception);
            ev.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            var ex = ev.ExceptionObject as Exception;
            var note = ev.IsTerminating ? "process is terminating" : null;
            Services.CrashLog.Record("AppDomain.UnhandledException", ex, extra: note);
            Services.SentryReporter.Capture("AppDomain.UnhandledException", ex, extra: note);
        };

        // Apply the saved UI language before any window is constructed so
        // the very first XAML parse (SplashWindow / MainWindow) already
        // sees the right strings — switching at runtime works too via the
        // Item[] notification, but starting in the right language avoids
        // a perceptible flicker between en and the user's locale.
        LocalizationService.Instance.SetLanguage(
            UserSettings.LoadLanguage() ?? LocalizationService.ResolveDefaultLanguage());

        base.OnStartup(e);

        // ─── Drag-drop under elevation (UIPI) ─────────────────────────
        // FiveM modders routinely launch tools "Run as administrator". Our
        // manifest is asInvoker, so an elevated launch puts this process at
        // High integrity while Explorer stays at Medium — and Windows then
        // silently drops every OLE drag-drop message Explorer sends us, so
        // nothing can be dragged onto any window. Re-allow the drag-drop
        // message trio through the UIPI filter. No-op when not elevated, so
        // it's safe to call unconditionally.
        EnableDragDropFromLowerIntegrity();

        // Show splash at 0%, then advance the bar in lockstep with real
        // init phases:
        //   10% → splash visible
        //   35% → MainWindow constructed (XAML parse + control init done)
        //  100% → MainWindow.Loaded — splash fades out, main takes focus
        //
        // WebView2 is no longer part of startup: it inits lazily on first
        // model load, so the splash doesn't wait for it.
        //
        // MainWindow must be Show()n so its Loaded event fires, but it must
        // stay invisible until the splash hands off. WPF Opacity=0 does NOT
        // achieve that here: MainWindow is a FluentWindow whose DWM backdrop —
        // and any native WebView2 surface — is composited OUTSIDE WPF ("airspace")
        // and renders opaque (white) regardless of Opacity, which is exactly the
        // flash that showed behind the splash. So park the whole window fully
        // OFF-SCREEN while it loads; that hides every surface type. The XAML
        // opens Maximized (Windows ignores Left/Top for maximized windows), so
        // force Normal first, park it off-screen, then restore to Maximized in
        // doFinish when the splash closes.
        var splash = new SplashWindow();
        splash.Show();
        splash.SetProgress(10, "Starting up...");

        var main = new MainWindow();
        MainWindow = main;
        main.WindowState = WindowState.Normal;
        main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.Left = -32000;
        main.Top = -32000;
        // Keep the XAML restore size (1520×940) — do NOT shrink to 200×200 here.
        // Windows uses this Normal size/position as the RESTORE rect behind the
        // maximized window; 200×200 clamped to the 720×480 minimum, so dragging or
        // un-maximizing collapsed the whole app to a tiny window. doFinish sets a
        // centred, full-size restore rect just before it maximizes on reveal.
        main.ShowInTaskbar = false;
        splash.SetProgress(35, "Loading window...");

        // Minimum on-screen time for the splash. Even if MainWindow.Loaded
        // fires sooner (small repos / warm caches), keep the splash visible
        // so the brand mark and version aren't a flicker.
        var splashShownAt = DateTime.UtcNow;
        var minSplashDuration = TimeSpan.FromSeconds(3);

        bool finishedOnce = false;
        bool mainReady = false;
        bool updateCheckDone = false;
        Action doFinish = () =>
        {
            if (finishedOnce) return;
            finishedOnce = true;
            // Reveal the main window (parked off-screen during init so the
            // splash had the screen to itself). Restore Maximized — the XAML
            // default and the product launch behaviour. Parking had to use
            // Normal + off-screen coords (maximized windows ignore Left/Top).
            main.ShowInTaskbar = true;
            main.Opacity = 1;
            // Establish a proper RESTORE rect before maximizing, so un-maximizing —
            // by drag OR the restore button — lands a LARGE centred window instead
            // of the off-screen parking rect or a tiny fixed size (1520 looked
            // postage-stamp-small on a 3440px ultrawide). Scale it to ~80% of the
            // work area. Set synchronously: WPF batches these onto one render frame
            // so the intermediate Normal state never paints (no flash).
            var wa = SystemParameters.WorkArea;
            double rw = Math.Max(1200, wa.Width * 0.80);
            double rh = Math.Max(760, wa.Height * 0.85);
            main.WindowState = WindowState.Normal;
            main.Width = rw;
            main.Height = rh;
            main.Left = wa.Left + (wa.Width - rw) / 2;
            main.Top = wa.Top + (wa.Height - rh) / 2;
            main.WindowState = WindowState.Maximized;
            splash.FinishAndClose(() =>
            {
                main.Activate();
                // Float the first-run dialogs (level picker, then the
                // Blender-style Welcome screen) over the revealed main window,
                // just AFTER the boot splash has finished closing — never over
                // the still-hidden window (both are CenterOwner). A short
                // DispatcherTimer fires reliably; ApplicationIdle can starve
                // under the WebView2 render loop, and an inline modal call would
                // hang mid-close.
                var welcomeTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(200),
                };
                welcomeTimer.Tick += (_, _) =>
                {
                    welcomeTimer.Stop();
                    main.ShowStartupDialogs();
                };
                welcomeTimer.Start();
            });
        };

        // Wraps doFinish with the minimum-duration gate. If the main window
        // is ready before 3s have elapsed, schedule the close; otherwise
        // close immediately.
        Action finish = () =>
        {
            if (finishedOnce) return;
            var elapsed = DateTime.UtcNow - splashShownAt;
            if (elapsed >= minSplashDuration)
            {
                doFinish();
            }
            else
            {
                var remaining = minSplashDuration - elapsed;
                var holdTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = remaining,
                };
                holdTimer.Tick += (_, _) =>
                {
                    holdTimer.Stop();
                    doFinish();
                };
                holdTimer.Start();
            }
        };

        // Two gates must clear before the splash hands off: the window must be
        // constructed (mainReady) AND the on-splash update check must have
        // settled or hit its cap (updateCheckDone). tryFinish is their join.
        Action tryFinish = () =>
        {
            if (mainReady && updateCheckDone) finish();
        };

        main.Loaded += (_, _) =>
        {
            mainReady = true;
            tryFinish();
        };

        // Failsafe: if Loaded never fires (or the update gate wedges), don't
        // trap the user behind the splash forever — reveal unconditionally
        // after 8s (doFinish is idempotent via finishedOnce).
        var failsafe = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(8),
        };
        failsafe.Tick += (_, _) =>
        {
            failsafe.Stop();
            doFinish();
        };
        failsafe.Start();

        main.Show();

        // ─── On-splash update check ──────────────────────────────────
        // Run the GitHub-releases check WHILE the splash is up ("Checking for
        // updates...") so a newer build is known before the app is even
        // revealed. Bounded by a short cap: a slow/offline network must never
        // trap the user behind the splash — if the check outlives the cap the
        // app proceeds and the result still lands (footer badge + themed
        // offer) via MainWindow.SetPendingUpdate once it finally completes.
        // Skipped entirely when Settings → Global update is off (manual only).
        if (Services.UserSettings.LoadGlobalUpdate())
        {
            _ = RunSplashUpdateCheckAsync(splash, main, TimeSpan.FromSeconds(5),
                onSettled: () =>
                {
                    updateCheckDone = true;
                    tryFinish();
                });
        }
        else
        {
            updateCheckDone = true;
            tryFinish();
        }

        // ─── Plugin host wiring ──────────────────────────────────────
        // The PluginManager exposes two callback hooks the host owns:
        //  - Toaster: how plugins surface user-facing messages.
        //  - RequestDllTrust: the consent gate before a DLL plugin runs.
        // Wiring them once at startup keeps PluginManager free of UI deps.
        FiveOS.Plugins.PluginManager.Toaster = msg =>
        {
            // Marshal back to the UI thread — plugins might call from any
            // thread. StatusText is the existing global status bar.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (main.DataContext is MainViewModel mvm) mvm.StatusText = msg;
            }));
        };
        FiveOS.Plugins.PluginManager.RequestDllTrust = async record =>
        {
            // Synchronous prompt on the UI thread; PluginManager invokes
            // this from CreateView which is already UI-bound. Async signature
            // exists for future MVVM dialogs.
            return await Dispatcher.InvokeAsync(() =>
            {
                var msg =
                    $"\"{record.Name}\" is a .NET DLL plugin loaded from:\n\n" +
                    $"{record.EntryPath}\n\n" +
                    "DLL plugins run with full app permissions: filesystem, network, registry. " +
                    "Only trust plugins from sources you recognise.\n\n" +
                    "Trust this plugin and load it?";
                var result = FiveOS.Views.AppDialog.Show(msg,
                    "FiveOS — confirm plugin trust",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, main);
                return result == MessageBoxResult.Yes;
            });
        };

        // ─── Discord Rich Presence ───────────────────────────────────
        // Off when the user has flipped the toggle in Settings; otherwise
        // bring up the IPC client and start mirroring VM state. Wired here
        // (rather than from MainWindow) so OnExit teardown is symmetrical.
        DiscordPresenceService.Initialize();
        if (main.DataContext is MainViewModel vm)
        {
            _discordVm = vm;
            _discordVmHandler = (_, ev) =>
            {
                switch (ev.PropertyName)
                {
                    case nameof(MainViewModel.ActiveView):
                        DiscordPresenceService.SetTab(ResolveActiveViewLabel(vm.ActiveView));
                        break;
                    case nameof(MainViewModel.SourcePath):
                        DiscordPresenceService.SetLoadedModelFromPath(vm.SourcePath);
                        break;
                    case nameof(MainViewModel.IsConverting):
                        DiscordPresenceService.SetConverting(vm.IsConverting);
                        break;
                }
            };
            vm.PropertyChanged += _discordVmHandler;

            main.Loaded += (_, _) =>
            {
                DiscordPresenceService.SetTab(ResolveActiveViewLabel(vm.ActiveView));
                DiscordPresenceService.SetLoadedModelFromPath(vm.SourcePath);
                DiscordPresenceService.SetConverting(vm.IsConverting);
            };
        }
    }

    /// <summary>
    /// Drive the update check while the boot splash is up, then release the
    /// splash gate. Bounded by <paramref name="cap"/>: the moment the check
    /// finishes OR the cap elapses (whichever first) the gate is released via
    /// <paramref name="onSettled"/>, so a slow/black-hole network can never
    /// hold the user behind the splash. If the cap wins the race the check
    /// keeps running in the background and its result still lands via
    /// <see cref="MainWindow.SetPendingUpdate"/>. <paramref name="onSettled"/>
    /// is invoked exactly once.
    /// </summary>
    private async Task RunSplashUpdateCheckAsync(
        SplashWindow splash, MainWindow main, TimeSpan cap, Action onSettled)
    {
        var settled = false;
        void Settle()
        {
            if (settled) return;
            settled = true;
            try { onSettled(); } catch { /* gate best-effort */ }
        }

        try
        {
            var check = PerformSplashUpdateCheckAsync(splash, main);
            await Task.WhenAny(check, Task.Delay(cap));
            Settle();                     // release the splash now
            try { await check; } catch { /* already surfaced/silent */ }
        }
        catch { /* never let a splash-time check crash startup */ }
        finally { Settle(); }             // guarantee the gate is released
    }

    /// <summary>
    /// The actual on-splash check: sets the "Checking for updates..." caption,
    /// queries GitHub Releases, then reflects the outcome in the splash caption
    /// and (when newer) hands the result to the main window to light the badge
    /// and arm the offer. Silent on transient errors — a launch-time network
    /// hiccup shouldn't alarm the user; Help → Check for updates still works.
    /// Resumes on the UI thread after the await (WPF sync-context), so the
    /// caption writes are thread-safe.
    /// </summary>
    private static async Task PerformSplashUpdateCheckAsync(SplashWindow splash, MainWindow main)
    {
        try
        {
            splash.SetProgress(60, "Checking for updates...");
            var result = await Services.UpdateChecker.CheckAsync();

            if (result.Status is Services.UpdateChecker.Status.UpToDate
                              or Services.UpdateChecker.Status.UpdateAvailable)
                Services.UserSettings.SaveLastUpdateCheck(DateTime.UtcNow);

            switch (result.Status)
            {
                case Services.UpdateChecker.Status.UpdateAvailable when result.Latest != null:
                    splash.SetProgress(90,
                        $"Update available: v{result.Latest.Major}.{result.Latest.Minor}.{result.Latest.Build}");
                    main.SetPendingUpdate(result);
                    break;
                case Services.UpdateChecker.Status.UpToDate:
                    splash.SetProgress(90, "You're up to date.");
                    break;
                default:
                    // NoReleases / Error — stay on the neutral caption rather
                    // than flash a scary message during a 3-second splash.
                    break;
            }
        }
        catch { /* silent — Help → Check for updates surfaces errors on demand */ }
    }

    /// <summary>
    /// Friendly label for the dashboard / feature view, used as the
    /// "what's happening" line on the Discord profile. Dashboard maps
    /// to null so DiscordPresenceService falls back to its idle subtitle.
    /// </summary>
    private static string? ResolveActiveViewLabel(FiveOS.ViewModels.AppView v) => v switch
    {
        FiveOS.ViewModels.AppView.Props       => "3D Model",
        FiveOS.ViewModels.AppView.Optimize    => "Optimize",
        FiveOS.ViewModels.AppView.Rpf         => "RPF",
        FiveOS.ViewModels.AppView.Vehicles    => "Vehicles",
        FiveOS.ViewModels.AppView.Emotes      => "Emotes",
        _                                     => null,  // Dashboard/ImageTo3D → idle subtitle
    };

    /// <summary>Returns true for exceptions where the process state is
    /// no longer trustworthy and the CLR should be allowed to tear
    /// the app down. We don't try to swallow these — silently
    /// continuing past a corrupted heap or blown stack is worse than
    /// crashing.</summary>
    private static bool IsFatal(Exception ex)
    {
        return ex is StackOverflowException
            or OutOfMemoryException
            or AccessViolationException
            or System.Runtime.InteropServices.SEHException;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.FosLogger.Info("boot", $"FiveOS exiting (code {e.ApplicationExitCode})");
        if (_discordVm != null && _discordVmHandler != null)
        {
            _discordVm.PropertyChanged -= _discordVmHandler;
        }
        _discordVm = null;
        _discordVmHandler = null;
        DiscordPresenceService.Shutdown();
        Services.SentryReporter.Shutdown();

        if (_singletonMutex != null)
        {
            if (_ownsMutex)
            {
                try { _singletonMutex.ReleaseMutex(); } catch { /* not held */ }
            }
            _singletonMutex.Dispose();
            _singletonMutex = null;
        }
        base.OnExit(e);
    }

    private static void FocusExistingInstance()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName(current.ProcessName))
            {
                using (other)
                {
                    if (other.Id == current.Id) continue;
                    var hwnd = other.MainWindowHandle;
                    if (hwnd == IntPtr.Zero) continue;
                    if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                    return;
                }
            }
        }
        catch
        {
            // Best-effort: if we can't focus the existing window, exiting
            // silently is still better than the misleading WebView2 toast.
        }
    }

    /// <summary>Re-allow the OLE drag-drop message trio through the UIPI
    /// filter so a lower-integrity Explorer (Medium) can drop onto our
    /// window when we're launched elevated (High). Process-wide — one call
    /// covers MainWindow, the embedded views, and BatchConvertWindow with
    /// no per-HWND plumbing. Best-effort and a no-op when not elevated, so
    /// it never throws and costs nothing in the normal-launch case.</summary>
    private static void EnableDragDropFromLowerIntegrity()
    {
        foreach (var msg in new[] { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA })
        {
            try { ChangeWindowMessageFilter(msg, MSGFLT_ADD); }
            catch { /* best-effort: pre-Vista / blocked — drag-drop just stays as-is */ }
        }

        // Log-only: tells us from crashes.log whether a "drops don't work"
        // report came from an elevated session. Doesn't gate the filter.
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var elevated = new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            if (elevated)
                Services.FosLogger.Info("boot", "elevated — drag-drop UIPI filter applied");
        }
        catch { /* identity lookup is best-effort */ }
    }

    /// <summary>Per-window variant of the UIPI drag-drop unblock. Called once
    /// per top-level window after its HWND exists (OnSourceInitialized).
    /// ChangeWindowMessageFilterEx is the API Microsoft recommends over the
    /// deprecated process-wide ChangeWindowMessageFilter; we apply both so the
    /// fix holds even on builds/configs where the process-wide call is ignored.
    /// Best-effort and a no-op when not elevated.</summary>
    public static void EnableDragDropForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        foreach (var msg in new[] { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA })
        {
            try { ChangeWindowMessageFilterEx(hwnd, msg, MSGFLT_ALLOW, IntPtr.Zero); }
            catch { /* best-effort */ }
        }
    }

    private const int SW_RESTORE = 9;

    // UIPI message-filter (drag-drop while elevated). ChangeWindowMessageFilter
    // is process-wide (MSGFLT_ADD); ChangeWindowMessageFilterEx is per-window
    // (MSGFLT_ALLOW). Both re-admit the OLE drag-drop message trio that Windows
    // otherwise blocks from a lower-integrity Explorer.
    private const uint MSGFLT_ADD = 1;
    private const uint MSGFLT_ALLOW = 1;
    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_COPYGLOBALDATA = 0x0049;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr pChangeFilterStruct);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
}
