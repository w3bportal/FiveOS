// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FiveOS.Services;
using FiveOS.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace FiveOS.Views;

/// <summary>
/// Pose -> Emote workspace. Hosts its own WebView2 (separate from the
/// main 3D viewport, both serve the same viewer.html bundle) so the
/// rigged model and the prop converter don't fight over scene state.
/// The viewer enters pose mode automatically once a model finishes
/// loading; the host C# side never has to "switch modes" — it just
/// triggers loads and reads pose data back via window.getPose().
/// </summary>
public partial class PoseToEmoteView : UserControl
{
    private readonly PoseToEmoteViewModel _vm = new();
    private readonly TimelineController _timelineCtl = new();
    private readonly TimelineSelectionModel _timelineSelection = new();
    private readonly TimelineCommandHistory<string> _timelineHistory = new();
    private TimelineInteractionState _timelineInteraction = TimelineInteractionState.Idle;
    private readonly List<TimelineItemRef> _timelineClipboard = new();
    private CancellationTokenSource? _operationCts;
    private TimelineStrip? _trimStripPendingSync;
    private DispatcherTimer? _trimSyncTimer;
    private string? _viewerSessionDir;
    private bool _webViewReady;
    private bool _viewerReady;
    private string? _pendingModelUrl;
    private volatile bool _ycdImportBusy;
    private int _importClipRequestId;
    private readonly Dictionary<int, TaskCompletionSource<string?>> _importClipWaiters = new();
    // Animation Library — cached extract of the selected dictionary +
    // generation counter so rapid clip clicks don't apply stale imports.
    private byte[]? _animLibDictBytes;
    private string? _animLibDictFileName;
    private int _animLibPreviewGen;
    private bool _animLibBusy;
    // Latest clip click while a preview is still importing — applied in finally
    // so rapid browsing doesn't drop selections (and leave the ped on A-pose).
    private string? _animLibPendingClip;
    private bool _animLibPendingPlay;
    // Preview → GIF capture: viewer buffers frames; host pulls after done.
    private int _gifRecordFps = 12;
    private TaskCompletionSource<bool>? _gifRecordTcs;
    // Scrub coalescing: WPF fires MouseMove far faster than WebView2 can
    // evaluate a full pose. Keep the latest target time and only issue the
    // next poseSetTime when the previous script call has finished.
    private double? _pendingScrubTime;
    private bool _scrubScriptInFlight;
    private bool _lastTickPlaying;
    // Echo ordering: the viewer stamps pose-timeline-update / snapshot /
    // tick messages with a monotonic rev. Deferred lambdas run at mixed
    // dispatcher priorities, so without this a STALE echo (previous clip's
    // duration) could apply after a newer one — the browsing "flicker" and
    // the axis stuck at an old clip's length while bars sat in its left
    // third. State (updates+snapshots) and ticks are guarded separately:
    // a tick only carries time and must never suppress a full state echo.
    private long _timelineStateRevApplied;
    private long _timelineTickRevApplied;
    // Import supersession: rapid library browsing overlaps imports; only the
    // NEWEST one may run post-import actions (auto-fit + snapshot request).
    private int _timelineImportGen;
    // Bar drag/trim (2026-07-17 "select and drag these"): grabbing a lane
    // bar slides the whole animation in time; grabbing an endpoint dot
    // trims that edge. Preview span overrides the renderer while dragging
    // and stays up until the committed echo rebuilds the markers, so the
    // bars never blink back to the pre-drag position.
    private enum BarDragMode { None, Move, Trim }
    private BarDragMode _barDragMode;
    private bool _barDragTrimEnd;                              // Trim: dragging the END dot
    private double _barDragOrigStart, _barDragOrigEnd;         // span at mouse-down (sec)
    private double _barPreviewStart = -1, _barPreviewEnd = -1; // live span; <0 = none
    // Cached dope-sheet row list — rebuilt when tracks/filter change, not
    // on every hover hit-test / render pass.
    private List<TimelineTrackRow>? _dopeVisibleTracksCache;
    private int _dopeVisibleTracksVersion = -1;

    public PoseToEmoteView()
    {
        InitializeComponent();
        DataContext = _vm;
        Focusable = true;
#if FIVEOS_MOTION
        InitializeMotionHub();
#endif
        PreviewKeyDown += OnTimelinePreviewKeyDown;
        // Animation Library is open by default — kick off the GTA index once
        // the control is live (non-blocking).
        Loaded += (_, _) =>
        {
            if (_vm.IsAnimLibraryOpen)
                _ = EnsureAnimLibraryLoadedAsync();
        };
        _timelineSelection.Changed += () =>
        {
            _vm.TimelineSelectionCount = _timelineSelection.Count;
            foreach (var strip in _vm.Strips)
                strip.IsSelected = _timelineSelection.Contains(
                    new TimelineItemRef(TimelineItemKind.Strip, strip.StableId));
            foreach (var row in _vm.TimelineTracks)
                foreach (var key in row.Keys)
                    key.IsSelected = _timelineSelection.Contains(
                        new TimelineItemRef(TimelineItemKind.Keyframe, key.Id));
            ScheduleRedrawTimeline();
        };
        _timelineCtl.Changed += ScheduleRedrawTimeline;
        _trimSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _trimSyncTimer.Tick += async (_, _) =>
        {
            _trimSyncTimer!.Stop();
            if (_trimStripPendingSync != null)
                await SyncStripRangeToViewerAsync(_trimStripPendingSync);
            _trimStripPendingSync = null;
        };
        // Push duration/fps changes from the C# NumberBoxes down into JS
        // so the timeline truly reflects user input. Pose-timeline-update
        // messages flow back to keep the UI in sync (one round-trip per
        // edit; cheap enough).
        _vm.PropertyChanged += (s, ev) =>
        {
            // Body-calibration sliders re-solve the cached retarget live.
            if (ev.PropertyName is nameof(PoseToEmoteViewModel.CalibrationEnabled)
                                or nameof(PoseToEmoteViewModel.CalibrationClearance)
                                or nameof(PoseToEmoteViewModel.CalibrationBodyWidth)
                                or nameof(PoseToEmoteViewModel.CalibrationArmSpread))
            {
                OnCalibrationChanged();
                return;
            }

            // Outliner selection state. The viewer-driven and
            // user-click paths both funnel through _vm.SelectedBone; we
            // mirror it onto each entry's IsSelected so the tree row
            // can highlight, and auto-expand the group containing the
            // newly-selected bone (otherwise clicking on the viewport
            // marker would highlight a row that's hidden behind a
            // collapsed region).
            if (ev.PropertyName == nameof(PoseToEmoteViewModel.SelectedBone))
            {
                var sel = _vm.SelectedBone;
                foreach (var b in _vm.Bones)
                {
                    var should = ReferenceEquals(b, sel);
                    if (b.IsSelected != should) b.IsSelected = should;
                }
                if (sel != null)
                {
                    var grp = _vm.BoneGroups.FirstOrDefault(g => g.Name == sel.Group);
                    if (grp != null && !grp.IsExpanded) grp.IsExpanded = true;
                }
            }

            // Push duration/fps edits from the NumberBoxes down to the JS
            // timeline state so it stays the source of truth for playback.
            if (_webViewReady && ev.PropertyName == nameof(PoseToEmoteViewModel.TimelineDuration))
            {
                var d = _vm.TimelineDuration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                _ = Viewport.CoreWebView2.ExecuteScriptAsync($"window.poseSetDuration && window.poseSetDuration({d})");
            }
            else if (_webViewReady && ev.PropertyName == nameof(PoseToEmoteViewModel.TimelineClipTrackVisible))
            {
                PushClipTrackVisibilityToViewer();
                RedrawTimeline();
            }
            else if (ev.PropertyName == nameof(PoseToEmoteViewModel.TimelineKeyframeTrackVisible))
            {
                RedrawTimeline();
            }
            else if (_webViewReady && ev.PropertyName == nameof(PoseToEmoteViewModel.TimelineFps))
            {
                _ = Viewport.CoreWebView2.ExecuteScriptAsync($"window.poseSetFps && window.poseSetFps({_vm.TimelineFps})");
            }
            // Movement mode → preview travel. In Place / Upper-body hold the ped
            // centred; Root Motion slides it along the clip's baked mover so the
            // user can preview/verify the travel before exporting a .fxresource.
            else if (_webViewReady && ev.PropertyName == nameof(PoseToEmoteViewModel.MovementIndex))
            {
                bool travel = _vm.Movement == Services.EmoteMovement.RootMotion;
                _ = Viewport.CoreWebView2.ExecuteScriptAsync(
                    $"window.poseSetRootMotionPreview && window.poseSetRootMotionPreview({(travel ? "true" : "false")})");
            }

            // Inspector → JS push: secondary-motion toggle + intensity
            // slider both call the same evaluator setter so the spring
            // reacts on next playback tick (or immediately resets if
            // playback is idle).
            if (ev.PropertyName == nameof(PoseToEmoteViewModel.SecondaryMotionEnabled)
                || ev.PropertyName == nameof(PoseToEmoteViewModel.SecondaryMotionIntensity))
            {
                PushSecondaryMotionToViewer();
            }
            if (ev.PropertyName == nameof(PoseToEmoteViewModel.AnimLibraryWorkingOnly)
                && _vm.SelectedAnimDict is { } workingDict)
            {
                _ = LoadAnimLibraryDictAsync(workingDict);
            }
            // Onion-skin toggle pushes a boolean down to the viewer; the
            // viewer hides the ghost LineSegments and skips per-frame
            // updates when off, so this has zero cost when disabled.
            if (ev.PropertyName == nameof(PoseToEmoteViewModel.OnionSkinEnabled))
            {
                if (_webViewReady)
                {
                    var en = _vm.OnionSkinEnabled ? "true" : "false";
                    _ = Viewport.CoreWebView2.ExecuteScriptAsync(
                        $"window.poseSetOnionSkin && window.poseSetOnionSkin({en})");
                }
            }
            if (ev.PropertyName == nameof(PoseToEmoteViewModel.JointMarkersEnabled))
            {
                if (_webViewReady)
                {
                    var en = _vm.JointMarkersEnabled ? "true" : "false";
                    _ = Viewport.CoreWebView2.ExecuteScriptAsync(
                        $"window.poseSetJointDots && window.poseSetJointDots({en})");
                }
            }
            if (ev.PropertyName == nameof(PoseToEmoteViewModel.RigDisplayMode))
            {
                if (_webViewReady)
                {
                    var mode = (_vm.RigDisplayMode ?? "control").Replace("\\", "\\\\").Replace("'", "\\'");
                    _ = Viewport.CoreWebView2.ExecuteScriptAsync(
                        $"window.poseSetRigDisplay && window.poseSetRigDisplay('{mode}')");
                }
                // Keep the legacy bool in sync for any code still reading it.
                var showMarkers = _vm.RigDisplayMode is "fiveos" or "markers";
                if (_vm.JointMarkersEnabled != showMarkers)
                    _vm.JointMarkersEnabled = showMarkers;
            }
            if (ev.PropertyName == nameof(PoseToEmoteViewModel.PoseIkMode))
            {
                if (_webViewReady)
                {
                    var on = _vm.PoseIkMode ? "true" : "false";
                    _ = Viewport.CoreWebView2.ExecuteScriptAsync(
                        $"window.poseSetIkMode && window.poseSetIkMode({on})");
                }
                _vm.PoseModeLabel = _vm.PoseIkMode ? "IK" : "FK";
            }

            // Redraw whichever timeline layer the change affects.
            // TimelineTime is the hot path (fires every playback frame);
            // we just shift the playhead instead of redrawing everything.
            switch (ev.PropertyName)
            {
                case nameof(PoseToEmoteViewModel.TimelineTime):
                    if (_vm.TimelinePlaying)
                    {
                        SyncTimelineControllerFromVm();
                        var prevScroll = _timelineCtl.ScrollOffset;
                        _timelineCtl.EnsurePlayheadVisible(_vm.TimelineTime, _vm.TimelineDuration, TimelineCanvasWidth);
                        // Only push (and thus full-redraw via ScrollOffset
                        // PropertyChanged) when the viewport actually moved.
                        if (System.Math.Abs(_timelineCtl.ScrollOffset - prevScroll) > 1e-9)
                            PushTimelineControllerToVm();
                    }
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // Playhead-only — a full DrawDopeSheet here used to
                        // rebuild every grid line and key diamond on every
                        // ~20 Hz playback tick / scrub move. The unified layers
                        // timeline shows BOTH sections, so move both playheads.
                        DrawTimelinePlayhead();
                        UpdateDopePlayhead();
                    }), DispatcherPriority.Render);
                    break;
                case nameof(PoseToEmoteViewModel.TimelineDuration):
                case nameof(PoseToEmoteViewModel.TimelineFps):
                case nameof(PoseToEmoteViewModel.TimelineZoom):
                case nameof(PoseToEmoteViewModel.TimelineScrollOffset):
                    ScheduleRedrawTimeline();
                    break;
            }
        };
        // Keyframe collection changes (add / move / clear) trigger a full
        // track redraw so the diamonds reflect the live JS-side list.
        // Coalesced: a snapshot rebuild fires one event PER KEY.
        _vm.TimelineKeyframes.CollectionChanged += (_, __) => ScheduleRedrawTimeline();

        // Pose library hydration now happens via RefreshCustomPoses on
        // every IsVisibleChanged tab activation (further down in the
        // ctor), reading PoseLibraryService directly. The duplicate
        // PosePresetService boot-load that used to live here was
        // removed when the two save-pose paths were consolidated.
        // Lazy WebView2 init: don't spawn the ~40 MB Edge process tree
        // until the Pose tab actually becomes visible. Without this the
        // viewer eagerly inits on app startup just because the
        // collapsed Grid container instantiates this UserControl.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                if (!_webViewReady) _ = InitWebViewAsync();
                // Refresh the saved-pose list every time the tab opens —
                // pose files may have been added/removed by another tool
                // (or by a previous FiveOS session) since we last looked.
                try { RefreshCustomPoses(); }
                catch (Exception ex) { Services.FosLogger.Warn("pose", "RefreshCustomPoses failed", ex); }
            }
        };
    }

    /// <summary>
    /// Warm the Emotes workspace ahead of first open: spin up the WebView2, unpack
    /// the viewer bundle, load viewer.html + the freemode rig and enter pose mode —
    /// all in the background while the tab is still collapsed. WebView2 runs the
    /// page even when hidden, so by the time the user clicks Emotes the rig is
    /// already painted and the tab opens instantly instead of stalling on the
    /// ~40 MB Edge spin-up + three.js + rig load. Idempotent (InitWebViewAsync
    /// guards on _webViewReady); a no-op once the tab has been opened normally.
    /// Called from MainWindow shortly after startup.
    /// </summary>
    /// <summary>Raised ONCE, the first time the pose viewer finishes loading the
    /// rig (pose-mode-entered). MainWindow uses it to hand the view back to the
    /// default after warming Emotes off-screen during startup.</summary>
    public event Action? ViewerFirstReady;
    private bool _firstReadyFired;

    public void PreloadViewer()
    {
        if (_webViewReady) return;
        Services.FosLogger.Info("pose", "PreloadViewer: warming Emotes WebView2 + rig in background");
        try { _ = InitWebViewAsync(); }
        catch (Exception ex) { Services.FosLogger.Warn("pose", "PreloadViewer failed", ex); }
    }

    private async Task InitWebViewAsync()
    {
        if (_webViewReady) return;
        try
        {
            _vm.ViewerLoadingCaption = "Starting WebView2 host...";
            var userDataDir = Path.Combine(Path.GetTempPath(), "FiveOS", "WebView2-Pose");
            Directory.CreateDirectory(userDataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await Viewport.EnsureCoreWebView2Async(env);

            Viewport.CoreWebView2.Settings.AreDevToolsEnabled = true;

            // Serve the viewer from a STABLE, content-keyed session folder and KEEP
            // WebView2's disk cache between launches. The old code used a fresh guid
            // folder + wiped the cache (ClearBrowsingData) + a random cache-bust on
            // every launch — so the ~MB three.js bundle re-downloaded and re-parsed
            // each time and the Emotes tab took seconds to open. Keying the folder
            // and the cache-bust on the viewer CONTENT (mtime; the bundle dir is
            // already content-hashed in release) means: same content → cache HIT →
            // fast open; changed content (dev edit / release update) → new key →
            // clean reload, no staleness. Per-content folders coexist (no delete),
            // so it's safe across concurrent instances.
            var bundledViewerDir = FiveOS.Services.RuntimeAssets.ViewerDir;
            var viewerKey = Path.GetFileName(bundledViewerDir.TrimEnd('\\', '/'));
            var srcHtml = Path.Combine(bundledViewerDir, "viewer.html");
            long stamp = File.Exists(srcHtml) ? File.GetLastWriteTimeUtc(srcHtml).Ticks : 0L;
            _viewerSessionDir = Path.Combine(Path.GetTempPath(), "FiveOS", $"ViewerPose-{viewerKey}-{stamp:x}");
            if (!File.Exists(Path.Combine(_viewerSessionDir, "viewer.html")))
            {
                _vm.ViewerLoadingCaption = "Unpacking viewer bundle...";
                Directory.CreateDirectory(_viewerSessionDir);
                CopyDirectory(bundledViewerDir, _viewerSessionDir);
            }

            Viewport.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "pose-viewer.local", _viewerSessionDir, CoreWebView2HostResourceAccessKind.Allow);
            WebViewDialogs.Theme(Viewport.CoreWebView2);

            Viewport.CoreWebView2.WebMessageReceived += OnViewerMessage;
            _vm.ViewerLoadingCaption = "Loading viewer...";
            // Stable per-content cache-bust: identical across launches (cache HIT →
            // fast), changes only when viewer.html changes.
            Viewport.Source = new Uri($"https://pose-viewer.local/viewer.html?v={stamp:x}");
            _webViewReady = true;

            // Failsafe: if the viewer never sends pose-mode-entered (broken
            // bundle, JS error swallowed before reportReady) drop the splash
            // after 15s so the user can at least see the underlying UI and
            // any status text the viewer managed to surface.
            var failsafe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            failsafe.Tick += (_, _) =>
            {
                failsafe.Stop();
                if (_vm.IsViewerLoading) _vm.IsViewerLoading = false;
            };
            failsafe.Start();
        }
        catch (Exception ex)
        {
            // Drop the overlay so the user can read the error in the
            // underlying status text rather than being stuck on a spinner.
            _vm.IsViewerLoading = false;
            _vm.StatusText = "Viewer failed to start: " + ex.Message;
        }
    }

    private void PushAccentToViewer()
    {
        if (!_webViewReady) return;
        if (System.Windows.Application.Current?.TryFindResource("SystemAccentColorBrush") is not SolidColorBrush brush) return;
        var c = brush.Color;
        var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        _ = Viewport.CoreWebView2.ExecuteScriptAsync(
            $"document.documentElement.style.setProperty('--pose-accent','{hex}')");
    }

    // ─── Boot-splash drop ─────────────────────────────────────────────
    // The overlay stays up until the viewer paints the rig (kind:
    // 'pose-painted'), then fades out so the workspace never appears
    // half-drawn. FadeOutViewerSplash is idempotent; a backstop timer
    // guarantees the splash still clears if the paint signal is missed.
    private bool _splashFaded;
    private DispatcherTimer? _splashBackstop;

    private void StartSplashDropBackstop()
    {
        _splashBackstop?.Stop();
        _splashBackstop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        _splashBackstop.Tick += (_, _) => { _splashBackstop?.Stop(); FadeOutViewerSplash(); };
        _splashBackstop.Start();
    }

    private void FadeOutViewerSplash()
    {
        if (_splashFaded) return;
        _splashFaded = true;
        _splashBackstop?.Stop();
        if (!_vm.IsViewerLoading) return;   // already hidden (error/failsafe path)
        // Hold the splash a beat AFTER the first paint so the rig is
        // unmistakably on screen (mesh + textures + controls settled) before
        // the overlay starts dissolving — otherwise it still reads as cutting
        // off onto a workspace mid-assembly. Then fade out.
        var hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        hold.Tick += (_, _) =>
        {
            hold.Stop();
            var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(400))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            fade.Completed += (_, _) =>
            {
                _vm.IsViewerLoading = false;                 // collapse the overlay
                ViewerLoadingOverlay.BeginAnimation(OpacityProperty, null);
                ViewerLoadingOverlay.Opacity = 1.0;          // reset for any re-show
            };
            ViewerLoadingOverlay.BeginAnimation(OpacityProperty, fade);
        };
        hold.Start();
    }

    private void OnViewerMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("kind", out var kindEl)) return;
            var kind = kindEl.GetString();

            switch (kind)
            {
                case "ready":
                    _viewerReady = true;
                    // Mirror the host app's accent into the viewer so the
                    // pose-HUD slider thumbs + title match whatever
                    // SystemAccentColorBrush resolves to in Wpf.Ui's dark
                    // theme (CSS can't bind to a WPF DynamicResource).
                    PushAccentToViewer();
                    if (_pendingModelUrl is { } url)
                    {
                        _pendingModelUrl = null;
                        _vm.ViewerLoadingCaption = "Loading model...";
                        if (url.StartsWith("gta:", StringComparison.Ordinal))
                        {
                            var variant = url.Substring("gta:".Length);
                            _ = Viewport.CoreWebView2.ExecuteScriptAsync(
                                $"window.loadGtaSkeleton && window.loadGtaSkeleton('{variant}')");
                        }
                        else
                        {
                            _ = LoadInViewerAsync(url);
                        }
                    }
                    else if (!_vm.HasRig)
                    {
                        // Default-load the synthetic male freemode rig on
                        // first viewer ready, so the Pose tab opens onto a
                        // posable model instead of the empty state. Skipped
                        // if the user already kicked off their own load
                        // before the ready message arrived (handled above).
                        _vm.LoadedModelPath = "GTA Male (synthetic skeleton)";
                        _vm.StatusText = "Loading synthetic GTA male skeleton...";
                        _vm.ViewerLoadingCaption = "Loading default skeleton...";
                        _ = Viewport.CoreWebView2.ExecuteScriptAsync(
                            "window.loadGtaSkeleton && window.loadGtaSkeleton('male')");
                    }
                    else
                    {
                        // Re-ready (e.g. viewer hot-reloaded with the rig
                        // already populated): nothing left to wait on.
                        _vm.IsViewerLoading = false;
                    }
                    break;

                case "loaded":
                    // Model finished loading -> automatically enter pose mode.
                    _vm.ViewerLoadingCaption = "Building joint markers...";
                    _ = Viewport.CoreWebView2.ExecuteScriptAsync("window.enterPoseMode && window.enterPoseMode()");
                    break;

                case "pose-painted":
                    // Viewer has rendered the rig to screen — now it's safe to
                    // fade the boot splash out onto a fully-drawn workspace.
                    Dispatcher.Invoke(FadeOutViewerSplash);
                    break;

                case "pose-mode-entered":
                    Services.FosLogger.Info("pose", "viewer: pose-mode-entered (rig ready) — Emotes tab now opens instantly");
                    if (!_firstReadyFired)
                    {
                        _firstReadyFired = true;
                        Dispatcher.BeginInvoke(new Action(() => { try { ViewerFirstReady?.Invoke(); } catch { } }));
                    }
                    Dispatcher.Invoke(() =>
                    {
                        _vm.Bones.Clear();
                        // Pull the rig-id map from the message (added with the
                        // multi-rig outliner). Older viewer builds don't send
                        // `rigs` / `rigId` — bones default to 'primary' in that
                        // case so the outliner still renders a single tree.
                        var rigOrder = new List<(string Id, string Name)>();
                        if (doc.RootElement.TryGetProperty("rigs", out var rigsEl))
                        {
                            foreach (var r in rigsEl.EnumerateArray())
                            {
                                rigOrder.Add((
                                    r.TryGetProperty("id", out var ridEl) ? ridEl.GetString() ?? "primary" : "primary",
                                    r.TryGetProperty("name", out var rnmEl) ? rnmEl.GetString() ?? "Rig" : "Rig"
                                ));
                            }
                        }
                        if (rigOrder.Count == 0) rigOrder.Add(("primary", "Primary"));

                        if (doc.RootElement.TryGetProperty("bones", out var bonesEl))
                        {
                            var list = new List<(PoseBoneEntry Entry, string RigId)>();
                            foreach (var b in bonesEl.EnumerateArray())
                            {
                                var nm = b.TryGetProperty("name", out var nmEl) ? (nmEl.GetString() ?? "") : "";
                                var (group, sort) = BoneGroupClassifier.Classify(nm);
                                var rid = b.TryGetProperty("rigId", out var riEl) ? riEl.GetString() ?? "primary" : "primary";
                                list.Add((new PoseBoneEntry
                                {
                                    Index = b.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0,
                                    Name = nm,
                                    ParentName = b.TryGetProperty("parent", out var pn) ? (pn.GetString() ?? "") : "",
                                    Group = group,
                                    SortKey = sort,
                                }, rid));
                            }
                            // Stable sort that keeps rig order from the message
                            // (so primary's tree always appears first), then
                            // groups within a rig, then bones within a group.
                            var rigIndex = new Dictionary<string, int>();
                            for (int i = 0; i < rigOrder.Count; i++) rigIndex[rigOrder[i].Id] = i;
                            list.Sort((a, c) =>
                            {
                                int ra = rigIndex.TryGetValue(a.RigId, out var ri) ? ri : int.MaxValue;
                                int rc = rigIndex.TryGetValue(c.RigId, out var rj) ? rj : int.MaxValue;
                                if (ra != rc) return ra.CompareTo(rc);
                                var g = string.Compare(a.Entry.Group, c.Entry.Group, StringComparison.Ordinal);
                                if (g != 0) return g;
                                if (a.Entry.SortKey != c.Entry.SortKey) return a.Entry.SortKey.CompareTo(c.Entry.SortKey);
                                return string.Compare(a.Entry.Name, c.Entry.Name, StringComparison.OrdinalIgnoreCase);
                            });
                            foreach (var entry in list) _vm.Bones.Add(entry.Entry);

                            // Flat BoneGroups (legacy consumers — search /
                            // drag / mirror code paths still walk this).
                            _vm.BoneGroups.Clear();
                            PoseBoneGroup? currentGroup = null;
                            foreach (var entry in _vm.Bones)
                            {
                                if (currentGroup is null || currentGroup.Name != entry.Group)
                                {
                                    currentGroup = new PoseBoneGroup
                                    {
                                        Name = entry.Group,
                                        IsExpanded = true,
                                        IsVisible = true,
                                    };
                                    _vm.BoneGroups.Add(currentGroup);
                                }
                                currentGroup.Bones.Add(entry);
                            }
                            foreach (var g in _vm.BoneGroups) g.NotifyCountChanged();

                            // Hierarchical Rigs (the new outliner). Build
                            // one PoseRig per rig id, each with its own
                            // BoneGroups split by region. Walk the sorted
                            // bone list once: every (rigId, group) flip
                            // starts a new bucket.
                            _vm.Rigs.Clear();
                            PoseRig? currentRig = null;
                            PoseBoneGroup? currentRigGroup = null;
                            foreach (var pair in list)
                            {
                                if (currentRig is null || currentRig.Id != pair.RigId)
                                {
                                    var rigName = rigOrder.FirstOrDefault(r => r.Id == pair.RigId).Name ?? pair.RigId;
                                    currentRig = new PoseRig { Id = pair.RigId, Name = string.IsNullOrEmpty(rigName) ? pair.RigId : rigName };
                                    _vm.Rigs.Add(currentRig);
                                    currentRigGroup = null;
                                }
                                if (currentRigGroup is null || currentRigGroup.Name != pair.Entry.Group)
                                {
                                    currentRigGroup = new PoseBoneGroup
                                    {
                                        Name = pair.Entry.Group,
                                        IsExpanded = true,
                                        IsVisible = true,
                                    };
                                    currentRig.BoneGroups.Add(currentRigGroup);
                                }
                                currentRigGroup.Bones.Add(pair.Entry);
                            }
                            foreach (var rig in _vm.Rigs)
                                foreach (var g in rig.BoneGroups) g.NotifyCountChanged();

                            // Prime the Dope Sheet immediately from the rig.
                            // The viewer snapshot fills the actual key arrays.
                            _vm.TimelineTracks.Clear();
                            _vm.TimelineTracks.Add(new TimelineTrackRow
                            {
                                Id = "summary", Name = "Summary", Depth = 0,
                            });
                            _vm.TimelineTracks.Add(new TimelineTrackRow
                            {
                                Id = "root-motion", Name = "Root Motion", Depth = 0,
                            });
                            var byName = _vm.Bones
                                .GroupBy(x => x.Name, StringComparer.Ordinal)
                                .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
                            foreach (var bone in _vm.Bones)
                            {
                                var depth = 1;
                                var parent = bone.ParentName;
                                var guard = 0;
                                while (parent.Length > 0 && byName.TryGetValue(parent, out var parentBone) && guard++ < 64)
                                {
                                    depth++;
                                    parent = parentBone.ParentName;
                                }
                                var (group, _) = BoneGroupClassifier.Classify(bone.Name);
                                _vm.TimelineTracks.Add(new TimelineTrackRow
                                {
                                    Id = $"bone:{bone.Index}",
                                    Name = bone.Name,
                                    DisplayName = TimelineTrackRow.FriendlyName(bone.Name),
                                    ParentId = bone.ParentName,
                                    Group = group,
                                    Depth = depth,
                                });
                            }
                            RebuildDopeDisplayTracks();
                        }
                        _vm.HasRig = _vm.Bones.Count > 0;
                        _vm.NotifyBonesChanged();
                        // Keep the status line clean on load (user "keep only
                        // essential"); the FK/IK how-to lives in the ? shortcuts
                        // tooltip and Pose Settings. Status still shows real
                        // feedback (imports, warnings) as it happens.
                        _vm.StatusText = _vm.HasRig
                            ? ""
                            : "Loaded, but no skeleton was found.";
                        // Workspace is assembled, but the WebView2 hasn't
                        // PAINTED the rig yet — keep the splash until the viewer
                        // reports 'pose-painted' (first real frame on screen), so
                        // it never cuts off onto a half-drawn workspace. Backstop
                        // fades it out anyway if that signal is somehow missed.
                        StartSplashDropBackstop();
                        _ = SendTimelineCommandAsync("requestSnapshot");
                        PushClipTrackVisibilityToViewer();
                        // Sync rig display / onion toggles into the freshly-booted viewer.
                        var mode = (_vm.RigDisplayMode ?? "fiveos").Replace("\\", "\\\\").Replace("'", "\\'");
                        var onion = _vm.OnionSkinEnabled ? "true" : "false";
                        var ikOn = _vm.PoseIkMode ? "true" : "false";
                        _ = Viewport.CoreWebView2.ExecuteScriptAsync(
                            $"window.poseSetRigDisplay && window.poseSetRigDisplay('{mode}');" +
                            $"window.poseSetOnionSkin && window.poseSetOnionSkin({onion});" +
                            $"window.poseSetIkMode && window.poseSetIkMode({ikOn})");
                        // Clear any prior clip drive dots until a new map arrives.
                        foreach (var bone in _vm.Bones)
                        {
                            bone.HasDriveMap = false;
                            bone.IsDriven = false;
                            bone.DriveSource = "";
                        }
                        _vm.DrivenBoneCount = 0;
                    });
                    break;

                case "pose-joint-dots":
                    // Viewer auto-disabled markers on animation load — mirror
                    // that into the settings toggle so the switch matches.
                    Dispatcher.Invoke(() =>
                    {
                        if (!doc.RootElement.TryGetProperty("enabled", out var enEl)) return;
                        try { _vm.JointMarkersEnabled = enEl.GetBoolean(); }
                        catch { /* ignore malformed payloads */ }
                    });
                    break;

                case "bone-drive-map":
                    // Clip → bone drive status for the Outliner (replaces the
                    // floating SKELETON panel). Green = driven, grey = rest.
                    Dispatcher.Invoke(() =>
                    {
                        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (doc.RootElement.TryGetProperty("driven", out var drivenEl)
                            && drivenEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            foreach (var prop in drivenEl.EnumerateObject())
                                map[prop.Name] = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                                    ? (prop.Value.GetString() ?? "")
                                    : "";
                        }
                        var active = map.Count > 0;
                        var drivenN = 0;
                        foreach (var bone in _vm.Bones)
                        {
                            bone.HasDriveMap = active;
                            if (active && map.TryGetValue(bone.Name, out var src))
                            {
                                bone.IsDriven = true;
                                bone.DriveSource = src;
                                drivenN++;
                            }
                            else
                            {
                                bone.IsDriven = false;
                                bone.DriveSource = "";
                            }
                        }
                        _vm.DrivenBoneCount = drivenN;
                        _vm.NotifyBonesChanged();
                        if (active)
                            _vm.StatusText = $"Clip drives {drivenN} / {_vm.Bones.Count} bones — see Outliner dots.";
                    });
                    break;

                case "pose-rig-display":
                    Dispatcher.Invoke(() =>
                    {
                        if (!doc.RootElement.TryGetProperty("mode", out var modeEl)) return;
                        var mode = modeEl.GetString();
                        if (string.IsNullOrWhiteSpace(mode)) return;
                        if (_vm.RigDisplayMode != mode)
                            _vm.RigDisplayMode = mode!;
                    });
                    break;

                case "rig-selected-for-transform":
                    Dispatcher.Invoke(() =>
                    {
                        var rid = doc.RootElement.TryGetProperty("rigId", out var ridEl)
                            ? ridEl.GetString() : null;
                        foreach (var rig in _vm.Rigs)
                            rig.IsModelSelected = rig.Id == rid;
                    });
                    break;

                case "pose-error":
                    Dispatcher.Invoke(() =>
                    {
                        var msg = doc.RootElement.TryGetProperty("message", out var mEl)
                            ? mEl.GetString() ?? "Pose error."
                            : "Pose error.";
                        _vm.StatusText = msg;
                        // Same reasoning as InitWebViewAsync's catch — keep
                        // the user from being stranded on the splash if the
                        // viewer reports a load failure.
                        _vm.IsViewerLoading = false;
                    });
                    break;

                case "pose-bone-selected":
                    Dispatcher.Invoke(() =>
                    {
                        if (!doc.RootElement.TryGetProperty("index", out var iEl)) return;
                        var i = iEl.GetInt32();
                        if (i >= 0 && i < _vm.Bones.Count)
                        {
                            // Hard-suppress every SelectionChanged that
                            // fires synchronously off the SelectedBone
                            // assignment below. Belt-and-braces alongside
                            // _lastPushedBoneIndex: even if a CollectionView
                            // refresh sneaks in multiple SelectionChanged
                            // events (each with a different .Index than
                            // _lastPushedBoneIndex), the flag drops them
                            // all on the floor and prevents echoing
                            // selectPoseBone back into the viewer.
                            _suppressSelectionEcho++;
                            try
                            {
                                _lastPushedBoneIndex = i;
                                _vm.SelectedBone = _vm.Bones[i];
                            }
                            finally
                            {
                                _suppressSelectionEcho--;
                            }
                        }
                    });
                    break;

                case "pose-bone-changed":
                    Dispatcher.Invoke(() =>
                    {
                        if (!doc.RootElement.TryGetProperty("index", out var iEl)) return;
                        var i = iEl.GetInt32();
                        if (i < 0 || i >= _vm.Bones.Count) return;
                        // Per-frame guard: only flip IsModified the first
                        // time the bone moves. Re-firing NotifyBonesChanged
                        // every frame triggers CollectionView refresh
                        // (IsLiveGroupingRequested=True), which in turn
                        // re-fires ListBox.SelectionChanged — which would
                        // bounce a selectPoseBone call back into the JS
                        // viewer mid-drag and yank the gizmo around. The
                        // "gizmo glitches between bones" symptom.
                        if (!_vm.Bones[i].IsModified)
                        {
                            _vm.Bones[i].IsModified = true;
                            _vm.NotifyBonesChanged();
                        }
                    });
                    break;

                case "pose-reset":
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var b in _vm.Bones) b.IsModified = false;
                        _vm.NotifyBonesChanged();
                        _vm.StatusText = "Pose reset to bind pose.";
                    });
                    break;

                // Shift+Del in the viewer. The confirm dialog is host-side, so
                // the hotkey asks rather than clearing on its own.
                case "pose-clear-all-requested":
                    Dispatcher.Invoke(() => _ = ClearAllAsync());
                    break;

                case "pose-timeline-update":
                {
                    // BeginInvoke: never stall the WebView2 message pump on
                    // a full keyframe rebuild (Invoke used to serialize every
                    // mutation behind the UI thread).
                    // CLONE the root first: the lambda runs AFTER this handler
                    // returns, when `using var doc` is already disposed — every
                    // deferred TryGetProperty threw ObjectDisposedException,
                    // which silently ate keyframe echoes / duration updates
                    // (record & import looked like no-ops; crashes.log 22:17).
                    var rootUpd = doc.RootElement.Clone();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // Drop out-of-order echoes (see _timelineStateRevApplied).
                        if (rootUpd.TryGetProperty("rev", out var revEl) && revEl.TryGetInt64(out var rev))
                        {
                            if (rev <= _timelineStateRevApplied) return;
                            _timelineStateRevApplied = rev;
                        }
                        // Re-sync C# state from the JS source-of-truth.
                        if (rootUpd.TryGetProperty("time", out var tEl))
                            _vm.TimelineTime = tEl.GetDouble();
                        if (rootUpd.TryGetProperty("duration", out var dEl))
                            _vm.TimelineDuration = dEl.GetDouble();
                        if (rootUpd.TryGetProperty("fps", out var fEl))
                            _vm.TimelineFps = fEl.GetInt32();
                        // Keep the transport's Start/End range boxes in sync
                        // with the clip length (End follows a new duration).
                        var totalF = (int)System.Math.Round(_vm.TimelineDuration * System.Math.Max(1, _vm.TimelineFps));
                        if (_vm.TrimEndFrame <= 0 || _vm.TrimEndFrame > totalF) _vm.TrimEndFrame = totalF;
                        if (_vm.TrimStartFrame < 0 || _vm.TrimStartFrame >= totalF) _vm.TrimStartFrame = 0;
                        if (rootUpd.TryGetProperty("playing", out var pEl))
                            _vm.TimelinePlaying = pEl.GetBoolean();
                        if (rootUpd.TryGetProperty("loop", out var lEl))
                            _vm.TimelineLoop = lEl.GetBoolean();
                        if (rootUpd.TryGetProperty("keyframes", out var kEl))
                        {
                            RebuildKeyframeMarkers(kEl);
                        }
                        UpdatePlayButtonVisual();
                        ScheduleRedrawTimeline();
                    }), DispatcherPriority.Background);
                    break;
                }

                case "pose-timeline-tick":
                {
                    // Hottest path (~20 Hz playback + scrub echoes). Never
                    // Dispatcher.Invoke — that blocks the WebView2 thread
                    // until the UI catches up and stacks latency.
                    // Clone: `doc` is disposed before the deferred lambda runs.
                    var rootTick = doc.RootElement.Clone();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // While the user is scrubbing we already drive
                        // TimelineTime from the mouse; ignore echo ticks so
                        // we don't fight the coalescer or redraw twice.
                        if (_timelineScrubbing || _scrubScriptInFlight) return;
                        // Ticks run at Render priority and overtake Background
                        // state lambdas — drop any tick older than the last
                        // tick OR the last full state echo (shared rev space).
                        if (rootTick.TryGetProperty("rev", out var revEl) && revEl.TryGetInt64(out var rev))
                        {
                            if (rev <= _timelineTickRevApplied || rev <= _timelineStateRevApplied) return;
                            _timelineTickRevApplied = rev;
                        }
                        if (rootTick.TryGetProperty("time", out var tEl))
                            _vm.TimelineTime = tEl.GetDouble();
                        if (rootTick.TryGetProperty("playing", out var pEl))
                        {
                            var playing = pEl.GetBoolean();
                            if (playing != _lastTickPlaying || playing != _vm.TimelinePlaying)
                            {
                                _vm.TimelinePlaying = playing;
                                _lastTickPlaying = playing;
                                UpdatePlayButtonVisual();
                            }
                        }
                    }), DispatcherPriority.Render);
                    break;
                }

                case "timeline-document-snapshot":
                {
                    // Clone: `doc` is disposed before the deferred lambda runs.
                    var rootSnap = doc.RootElement.Clone();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // Snapshots carry full state — same ordering family as
                        // pose-timeline-update; drop stale ones.
                        if (rootSnap.TryGetProperty("rev", out var revEl) && revEl.TryGetInt64(out var rev))
                        {
                            if (rev <= _timelineStateRevApplied) return;
                            _timelineStateRevApplied = rev;
                        }
                        ApplyTimelineDocumentSnapshot(rootSnap);
                    }),
                        DispatcherPriority.Background);
                    break;
                }

                case "js-error":
                    Dispatcher.Invoke(() =>
                    {
                        var msg = doc.RootElement.TryGetProperty("message", out var mEl) ? mEl.GetString() : "";
                        var src = doc.RootElement.TryGetProperty("source", out var sEl) ? sEl.GetString() : "";
                        var ln  = doc.RootElement.TryGetProperty("line", out var lEl) ? lEl.GetInt32() : 0;
                        _vm.StatusText = $"JS error: {msg} ({System.IO.Path.GetFileName(src)}:{ln})";
                    });
                    break;

                case "pose-timeline-warn":
                    Dispatcher.Invoke(() =>
                    {
                        if (doc.RootElement.TryGetProperty("message", out var mEl))
                            _vm.StatusText = mEl.GetString() ?? "";
                    });
                    break;

                case "import-keyframe-clip-result":
                    if (doc.RootElement.TryGetProperty("requestId", out var reqEl))
                    {
                        int reqId = reqEl.GetInt32();
                        lock (_importClipWaiters)
                        {
                            if (_importClipWaiters.Remove(reqId, out var tcs))
                                tcs.TrySetResult(json);
                        }
                    }
                    break;

                // NOTE (all gif-record-* cases): clone the root before
                // BeginInvoke — `using var doc` is disposed before the deferred
                // lambda runs (same ObjectDisposedException as the timeline).
                case "gif-record-start":
                {
                    var rootGs = doc.RootElement.Clone();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (rootGs.TryGetProperty("fps", out var fpsEl)
                            && fpsEl.TryGetInt32(out var fps))
                            _gifRecordFps = fps;
                        var n = rootGs.TryGetProperty("frames", out var nEl)
                            && nEl.TryGetInt32(out var nf) ? nf : 0;
                        _vm.StatusText = n > 0 ? $"Recording GIF… 0/{n}" : "Recording GIF…";
                    }), DispatcherPriority.Background);
                    break;
                }

                case "gif-record-progress":
                {
                    var rootGp = doc.RootElement.Clone();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var i = rootGp.TryGetProperty("i", out var iEl) && iEl.TryGetInt32(out var ii) ? ii + 1 : 0;
                        var n = rootGp.TryGetProperty("n", out var nEl) && nEl.TryGetInt32(out var nn) ? nn : 0;
                        if (n > 0) _vm.StatusText = $"Recording GIF… {i}/{n}";
                    }), DispatcherPriority.Background);
                    break;
                }

                // Legacy: older viewers streamed frame payloads here. Ignore data;
                // host pulls frames via gifTakeFrame after gif-record-done.
                case "gif-record-frame":
                {
                    var rootGf = doc.RootElement.Clone();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var i = rootGf.TryGetProperty("i", out var iEl) && iEl.TryGetInt32(out var ii) ? ii + 1 : 0;
                        var n = rootGf.TryGetProperty("n", out var nEl) && nEl.TryGetInt32(out var nn) ? nn : 0;
                        if (n > 0) _vm.StatusText = $"Recording GIF… {i}/{n}";
                    }), DispatcherPriority.Background);
                    break;
                }

                case "gif-record-done":
                {
                    var rootGd = doc.RootElement.Clone();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (rootGd.TryGetProperty("fps", out var fpsEl)
                            && fpsEl.TryGetInt32(out var fps))
                            _gifRecordFps = fps;
                        _gifRecordTcs?.TrySetResult(true);
                    }), DispatcherPriority.Background);
                    break;
                }

                case "gif-record-error":
                {
                    var rootGe = doc.RootElement.Clone();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var msg = rootGe.TryGetProperty("message", out var mEl)
                            ? mEl.GetString() : null;
                        _vm.StatusText = string.IsNullOrEmpty(msg)
                            ? "GIF recording failed."
                            : msg;
                        _gifRecordTcs?.TrySetResult(false);
                    }), DispatcherPriority.Background);
                    break;
                }

                case "strip-list-update":
                    Dispatcher.Invoke(() =>
                    {
                        if (!doc.RootElement.TryGetProperty("strips", out var arr)) return;
                        // Reconcile by Id, mirroring the clip-library
                        // approach so any selection or hover state on
                        // a strip survives sibling mutations.
                        var live = new System.Collections.Generic.HashSet<int>();
                        foreach (var el in arr.EnumerateArray())
                        {
                            if (!el.TryGetProperty("id", out var idEl)) continue;
                            int id = idEl.GetInt32();
                            live.Add(id);
                            var existing = _vm.Strips.FirstOrDefault(s => s.Id == id);
                            int clipId = el.TryGetProperty("clipId", out var ciEl) ? ciEl.GetInt32() : 0;
                            string name = el.TryGetProperty("clipName", out var nEl) ? nEl.GetString() ?? "" : "";
                            string kind = el.TryGetProperty("kind", out var kEl) ? kEl.GetString() ?? "pose" : "pose";
                            double start = el.TryGetProperty("start", out var sEl) ? sEl.GetDouble() : 0.0;
                            double dur = el.TryGetProperty("duration", out var dEl) ? dEl.GetDouble() : 0.0;
                            double fIn  = el.TryGetProperty("fadeIn", out var fiEl) ? fiEl.GetDouble() : 0.0;
                            double fOut = el.TryGetProperty("fadeOut", out var foEl) ? foEl.GetDouble() : 0.0;
                            string fInEase = el.TryGetProperty("fadeInEase", out var fieEl) ? fieEl.GetString() ?? "linear" : "linear";
                            string fOutEase = el.TryGetProperty("fadeOutEase", out var foeEl) ? foeEl.GetString() ?? "linear" : "linear";
                            double srcStart = el.TryGetProperty("sourceStart", out var ssEl) ? ssEl.GetDouble() : 0.0;
                            double srcEnd = el.TryGetProperty("sourceEnd", out var seEl) ? seEl.GetDouble() : dur;
                            if (existing == null)
                            {
                                _vm.Strips.Add(new TimelineStrip
                                {
                                    Id = id, ClipId = clipId, ClipName = name,
                                    Kind = kind, Start = start, Duration = dur,
                                    FadeIn = fIn, FadeOut = fOut,
                                    FadeInEase = fInEase, FadeOutEase = fOutEase,
                                    SourceStart = srcStart, SourceEnd = srcEnd,
                                });
                            }
                            else
                            {
                                existing.ClipId = clipId;
                                existing.ClipName = name;
                                existing.Kind = kind;
                                existing.Start = start;
                                existing.Duration = dur;
                                existing.FadeIn = fIn;
                                existing.FadeOut = fOut;
                                existing.FadeInEase = fInEase;
                                existing.FadeOutEase = fOutEase;
                                existing.SourceStart = srcStart;
                                existing.SourceEnd = srcEnd;
                            }
                        }
                        for (int i = _vm.Strips.Count - 1; i >= 0; i--)
                            if (!live.Contains(_vm.Strips[i].Id))
                                _vm.Strips.RemoveAt(i);
                        InvalidateDopeVisibleTracks();
                        RedrawStrips();
                        ScheduleRedrawTimeline();
                        _vm.NotifyStripsChanged();
                    });
                    break;

                case "clip-library-update":
                    Dispatcher.Invoke(() =>
                    {
                        // Reconcile in place rather than clear-and-rebuild so
                        // the ListBox selection survives mutations (rename,
                        // a sibling clip arriving from a freshly-imported
                        // model). Match by Id; new ids append; missing ids
                        // are removed; existing ids get fields refreshed.
                        if (!doc.RootElement.TryGetProperty("clips", out var arr)) return;
                        var live = new System.Collections.Generic.HashSet<int>();
                        foreach (var el in arr.EnumerateArray())
                        {
                            if (!el.TryGetProperty("id", out var idEl)) continue;
                            int id = idEl.GetInt32();
                            live.Add(id);
                            var existing = _vm.ClipLibrary.FirstOrDefault(c => c.Id == id);
                            string name = el.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                            string kind = el.TryGetProperty("kind", out var kEl) ? kEl.GetString() ?? "pose" : "pose";
                            double dur = el.TryGetProperty("duration", out var dEl) ? dEl.GetDouble() : 0.0;
                            int tracks = el.TryGetProperty("trackCount", out var tEl) ? tEl.GetInt32() : 0;
                            if (existing == null)
                            {
                                _vm.ClipLibrary.Add(new ClipLibraryEntry
                                {
                                    Id = id, Name = name, Kind = kind,
                                    Duration = dur, TrackCount = tracks,
                                });
                            }
                            else
                            {
                                existing.Name = name;
                                existing.Kind = kind;
                                existing.Duration = dur;
                                existing.TrackCount = tracks;
                            }
                        }
                        for (int i = _vm.ClipLibrary.Count - 1; i >= 0; i--)
                            if (!live.Contains(_vm.ClipLibrary[i].Id))
                                _vm.ClipLibrary.RemoveAt(i);
                    });
                    break;

case "prop-loaded":
                    Dispatcher.Invoke(() => { _vm.HasProp = true; _vm.StatusText = "Prop loaded. Move with the gizmo, then export."; });
                    break;

                case "prop-removed":
                    Dispatcher.Invoke(() => { _vm.HasProp = false; _vm.StatusText = "Prop removed."; });
                    break;

                case "prop-error":
                    Dispatcher.Invoke(() =>
                    {
                        var msg = doc.RootElement.TryGetProperty("message", out var mEl) ? mEl.GetString() ?? "Prop error." : "Prop error.";
                        _vm.StatusText = msg;
                    });
                    break;

                case "pose-history":
                    Dispatcher.Invoke(() =>
                    {
                        // Viewer broadcasts the new stack depth after every
                        // undo/redo so the buttons can flip their enabled
                        // state without us polling.
                        if (doc.RootElement.TryGetProperty("depth", out var dEl)) _vm.UndoDepth = dEl.GetInt32();
                        if (doc.RootElement.TryGetProperty("redoDepth", out var rEl)) _vm.RedoDepth = rEl.GetInt32();
                    });
                    break;

                case "pose-applied":
                    Dispatcher.Invoke(() =>
                    {
                        // applyPose finished — mark every bone as modified so
                        // the sidebar's dots reflect that the rig moved.
                        var applied = doc.RootElement.TryGetProperty("applied", out var aEl) ? aEl.GetInt32() : 0;
                        var missing = doc.RootElement.TryGetProperty("missing", out var mEl) ? mEl.GetInt32() : 0;
                        if (applied > 0)
                        {
                            foreach (var b in _vm.Bones) b.IsModified = true;
                            _vm.NotifyBonesChanged();
                        }
                        _vm.StatusText = missing > 0
                            ? $"Pose applied: {applied} bones (rig is missing {missing} from the saved pose)."
                            : $"Pose applied: {applied} bones.";
                    });
                    break;

                case "pose-mirrored":
                    Dispatcher.Invoke(() =>
                    {
                        var pairs = doc.RootElement.TryGetProperty("pairs", out var pEl) ? pEl.GetInt32() : 0;
                        // Flag every L/R-marked bone as modified — easier (and
                        // accurate enough) than tracking which side each
                        // quaternion came from. A future improvement could
                        // diff against rest_xyzw to mark only actually-changed
                        // bones.
                        foreach (var b in _vm.Bones)
                        {
                            if (HasSideMarker(b.Name)) b.IsModified = true;
                        }
                        _vm.NotifyBonesChanged();
                        _vm.StatusText = pairs == 0
                            ? "No L/R bone pairs detected — mirror did nothing."
                            : $"Mirrored {pairs} bone pair{(pairs == 1 ? "" : "s")} from left to right.";
                    });
                    break;
            }
        }
        catch { /* ignore malformed messages */ }
    }

    private void ApplyTimelineDocumentSnapshot(JsonElement message)
    {
        var snapshot = message.TryGetProperty("snapshot", out var nested) ? nested : message;
        if (snapshot.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number)
            _vm.TimelineDuration = duration.GetDouble();
        if (snapshot.TryGetProperty("fps", out var fps) && fps.ValueKind == JsonValueKind.Number)
            _vm.TimelineFps = fps.GetInt32();
        if (!snapshot.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            return;

        // Id→object lookups: a baked clip carries ~100+ tracks × ~hundreds
        // of keys, and the old per-item FirstOrDefault scans made every
        // snapshot echo O(K²) (tens of millions of comparisons — the UI
        // visibly froze after each edit).
        var tracksById = new Dictionary<string, TimelineTrackRow>(StringComparer.Ordinal);
        foreach (var existing in _vm.TimelineTracks) tracksById.TryAdd(existing.Id, existing);

        var liveTracks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in tracks.EnumerateArray())
        {
            var id = JsonElementId(element, "id");
            if (id.Length == 0) continue;
            liveTracks.Add(id);
            if (!tracksById.TryGetValue(id, out var track))
            {
                track = new TimelineTrackRow { Id = id };
                _vm.TimelineTracks.Add(track);
                tracksById[id] = track;
            }
            track.Name = element.TryGetProperty("name", out var name) ? name.GetString() ?? id : id;
            track.DisplayName = TimelineTrackRow.IsSpecialTrack(track)
                ? track.Name
                : TimelineTrackRow.FriendlyName(track.Name);
            if (!TimelineTrackRow.IsSpecialTrack(track))
            {
                var (group, _) = BoneGroupClassifier.Classify(track.Name);
                track.Group = group;
            }
            else
            {
                track.Group = "";
            }
            track.ParentId = JsonElementId(element, "parentId");
            track.Depth = element.TryGetProperty("depth", out var depth) && depth.ValueKind == JsonValueKind.Number
                ? depth.GetInt32() : 0;
            track.IsLocked = element.TryGetProperty("locked", out var locked) &&
                             locked.ValueKind == JsonValueKind.True;
            track.IsMuted = element.TryGetProperty("muted", out var muted) &&
                            muted.ValueKind == JsonValueKind.True;
            track.IsGroupHeader = false;

            if (!element.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array)
                continue;
            var keysById = new Dictionary<string, KeyframeMarker>(track.Keys.Count, StringComparer.Ordinal);
            foreach (var existingKey in track.Keys) keysById.TryAdd(existingKey.Id, existingKey);
            var liveKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var keyElement in keys.EnumerateArray())
            {
                var keyId = JsonElementId(keyElement, "id");
                if (keyId.Length == 0) continue;
                liveKeys.Add(keyId);
                if (!keysById.TryGetValue(keyId, out var key))
                {
                    key = new KeyframeMarker { Id = keyId };
                    track.Keys.Add(key);
                    keysById[keyId] = key;
                }
                key.BoneName = track.Name;
                key.Time = keyElement.TryGetProperty("time", out var time) ? time.GetDouble() : 0;
                key.Ease = keyElement.TryGetProperty("ease", out var ease)
                    ? ease.GetString() ?? "auto" : "auto";
                key.IsSelected = _timelineSelection.Contains(
                    new TimelineItemRef(TimelineItemKind.Keyframe, keyId));
            }
            for (var i = track.Keys.Count - 1; i >= 0; i--)
                if (!liveKeys.Contains(track.Keys[i].Id)) track.Keys.RemoveAt(i);
            track.HasKeys = track.Keys.Count > 0;
        }
        for (var i = _vm.TimelineTracks.Count - 1; i >= 0; i--)
        {
            var row = _vm.TimelineTracks[i];
            if (row.IsGroupHeader || !liveTracks.Contains(row.Id))
                _vm.TimelineTracks.RemoveAt(i);
        }

        RebuildDopeDisplayTracks();
        ScheduleRedrawTimeline();
    }

    private static string JsonElementId(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        };
    }

    private async void OnOpenRiggedModel(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open Rigged Model",
            Filter = "Rigged 3D models (*.glb;*.gltf;*.fbx)|*.glb;*.gltf;*.fbx|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        // Defensive: IsVisibleChanged usually wins this race, but if the
        // user clicks fast enough that the WebView2 hasn't started its
        // init yet, kick it off here so the load isn't dropped.
        if (!_webViewReady) await InitWebViewAsync();
        await LoadModelAsync(dlg.FileName);
    }

    private async Task LoadModelAsync(string path)
    {
        if (!_webViewReady || _viewerSessionDir is null)
        {
            _vm.StatusText = "Viewer not ready yet.";
            return;
        }

        // Copy the model into the session dir so the virtual host can serve it.
        // Re-uses the same dir as the viewer bundle — co-locating user files
        // here keeps the host mapping single-rooted.
        var ext = Path.GetExtension(path);
        var dest = Path.Combine(_viewerSessionDir, "user-pose-model" + ext);
        try { File.Copy(path, dest, overwrite: true); }
        catch (Exception ex)
        {
            _vm.StatusText = "Couldn't copy model: " + ex.Message;
            return;
        }

        _vm.LoadedModelPath = path;
        _vm.HasRig = false;
        _vm.Bones.Clear();
        _vm.BoneGroups.Clear();
        _vm.NotifyBonesChanged();
        _vm.StatusText = "Loading " + Path.GetFileName(path) + "...";

        var url = "https://pose-viewer.local/user-pose-model" + ext;
        if (_viewerReady) await LoadInViewerAsync(url);
        else _pendingModelUrl = url;
    }

    private async Task LoadInViewerAsync(string url)
    {
        var safe = url.Replace("\\", "/").Replace("'", "\\'");
        await Viewport.CoreWebView2.ExecuteScriptAsync($"window.exitPoseMode && window.exitPoseMode()");
        await Viewport.CoreWebView2.ExecuteScriptAsync($"window.loadModel('{safe}')");
    }

    private async void OnResetPose(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        await Viewport.CoreWebView2.ExecuteScriptAsync("window.resetPose && window.resetPose()");
    }

    private async void OnLoadGtaMale(object sender, RoutedEventArgs e) => await LoadGtaPresetAsync("male");
    private async void OnLoadGtaFemale(object sender, RoutedEventArgs e) => await LoadGtaPresetAsync("female");

    // Pose-preset handlers were removed in v10 -- the hardcoded
    // axis-angle quaternions didn't match the freemode bind orientation
    // and the resulting poses were unusable. "Save current pose as
    // template" replaces them when the .ycd export pipeline is solid.

    // ── Prop export plumbing ─────────────────────────────────────────
    // The prop-authoring UI was removed from the sidebar (2026-07-16),
    // but the export path below stays: it is dormant while HasProp is
    // false and keeps prop-emote exports possible if the feature returns.

    /// <summary>Fetch the prop's transform relative to its attach bone
    /// (in-viewport bone, since that's what TransformControls operates
    /// in) and package as a PropInfo for the dpemotes snippet. Returns
    /// null on any failure -- export still proceeds without the prop
    /// block in that case.</summary>
    private async Task<Services.DpemotesPackBuilder.PropInfo?> GetPropInfoForExportAsync()
    {
        try
        {
            var raw = await Viewport.CoreWebView2.ExecuteScriptAsync("window.getPropTransform && window.getPropTransform()");
            if (string.IsNullOrEmpty(raw) || raw == "null") return null;
            string json;
            try { json = JsonSerializer.Deserialize<string>(raw) ?? ""; }
            catch { json = raw; }
            using var doc = JsonDocument.Parse(json);
            var posEl = doc.RootElement.GetProperty("pos");
            var rotEl = doc.RootElement.GetProperty("rot_deg");
            var placement = new[]
            {
                (float)posEl[0].GetDouble(), (float)posEl[1].GetDouble(), (float)posEl[2].GetDouble(),
                (float)rotEl[0].GetDouble(), (float)rotEl[1].GetDouble(), (float)rotEl[2].GetDouble(),
            };
            return new Services.DpemotesPackBuilder.PropInfo(_vm.PropModelName, _vm.PropBoneId, placement);
        }
        catch { return null; }
    }

    private async Task LoadGtaPresetAsync(string variant)
    {
        if (!_webViewReady) await InitWebViewAsync();
        // The viewer's "ready" message may not have arrived yet on the very
        // first visit. Defer the preset call until the host has heard back
        // by buffering it in the same _pendingModelUrl slot — repurposed
        // as a sentinel for "do something on ready".
        _vm.LoadedModelPath = $"GTA {char.ToUpper(variant[0]) + variant[1..]} (synthetic skeleton)";
        _vm.HasRig = false;
        _vm.Bones.Clear();
        _vm.BoneGroups.Clear();
        _vm.NotifyBonesChanged();
        _vm.StatusText = "Loading synthetic GTA " + variant + " skeleton...";

        if (_viewerReady)
        {
            await Viewport.CoreWebView2.ExecuteScriptAsync($"window.loadGtaSkeleton && window.loadGtaSkeleton('{variant}')");
        }
        else
        {
            // Latch onto the next ready-fires-once event by re-driving in OnViewerMessage.
            _pendingModelUrl = "gta:" + variant;
        }
    }

    private async void OnMirrorPose(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        await Viewport.CoreWebView2.ExecuteScriptAsync("window.mirrorPose && window.mirrorPose()");
    }

    // ── Timeline event handlers ─────────────────────────────────────
    //
    // The `_suppressScrubberPush` re-entrancy guard that used to live
    // here was dead — written in four places but never read. Today
    // the TimelineTime PropertyChanged handler only triggers a
    // playhead redraw, not a JS push, so there's no echo loop to
    // suppress. If a future change introduces one, fix it with a
    // mediator, not a bool flag.

    private async void OnTimelinePlayPause(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) { _vm.StatusText = "Viewer not ready."; return; }
        var script = _vm.TimelinePlaying ? "window.posePause && window.posePause()" : "window.posePlay && window.posePlay()";
        await Viewport.CoreWebView2.ExecuteScriptAsync(script);
        // Status reflects the action just requested; VM syncs from the viewer next.
        _vm.StatusText = _vm.TimelinePlaying ? "Paused." : "Playing…";
    }

    private async Task SeekTimelineAsync(double t)
    {
        t = System.Math.Max(0, System.Math.Min(_vm.TimelineDuration, t));
        _vm.TimelineTime = System.Math.Round(t, 3);
        if (_webViewReady)
        {
            var tArg = t.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            await Viewport.CoreWebView2.ExecuteScriptAsync($"window.poseSetTime && window.poseSetTime({tArg})");
        }
    }

    private async void OnTimelineGoToStart(object sender, RoutedEventArgs e)
    {
        await SeekTimelineAsync(0);
        _vm.StatusText = "Playhead → start.";
    }

    private async void OnTimelineGoToEnd(object sender, RoutedEventArgs e)
    {
        await SeekTimelineAsync(_vm.TimelineDuration);
        _vm.StatusText = "Playhead → end.";
    }

    private async void OnTimelinePrevFrame(object sender, RoutedEventArgs e)
    {
        var step = 1.0 / System.Math.Max(1, _vm.TimelineFps);
        await SeekTimelineAsync(_vm.TimelineTime - step);
        _vm.StatusText = $"Frame {_vm.TimelineCurrentFrame}.";
    }

    private async void OnTimelineNextFrame(object sender, RoutedEventArgs e)
    {
        var step = 1.0 / System.Math.Max(1, _vm.TimelineFps);
        await SeekTimelineAsync(_vm.TimelineTime + step);
        _vm.StatusText = $"Frame {_vm.TimelineCurrentFrame}.";
    }

    private async void OnTimelineLoopToggle(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) { _vm.StatusText = "Viewer not ready."; return; }
        await Viewport.CoreWebView2.ExecuteScriptAsync("window.poseToggleLoop && window.poseToggleLoop()");
        // Viewer echoes the new loop flag via pose-timeline-update; hint now.
        _vm.StatusText = _vm.TimelineLoop ? "Loop off…" : "Loop on…";
    }

    private async void OnAddKeyframe(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) { _vm.StatusText = "Viewer not ready."; return; }
        var selected = _vm.TimelineTracks.Where(x => x.IsSelected).Select(x => x.Id).ToArray();
        if (selected.Length > 0)
        {
            // Bone rows selected → key just those tracks.
            await SendTimelineCommandAsync("addKey", selected,
                new { time = _vm.TimelineTime, fullPose = false });
            _vm.StatusText = $"Keyframe set on {selected.Length} track(s) at {_vm.TimelineTimecodeLabel}.";
        }
        else
        {
            // Full-pose record: ALWAYS the proven poseAddKeyframe path — it
            // snapshots the pose, stores the key AND echoes the timeline
            // update that makes the diamond appear. (The unified layers
            // timeline briefly routed this through the 'addKey fullPose'
            // dope op, which records silently with no visible result.)
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                "window.poseAddKeyframe && window.poseAddKeyframe()");
            _vm.StatusText = $"Keyframe set at {_vm.TimelineTimecodeLabel}.";
        }
        // Redraw only — the poseAddKeyframe echo already updates the summary
        // lane, and a full document snapshot here (47 tracks × N keys) was a
        // measurable hitch on every record press.
        ScheduleRedrawTimeline();
    }

    private async void OnClearAll(object sender, RoutedEventArgs e) => await ClearAllAsync();

    /// <summary>Transport Frame box: Enter seeks to the typed frame.</summary>
    private async void OnFrameBoxCommit(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if (sender is System.Windows.Controls.TextBox tb && int.TryParse(tb.Text.Trim(), out var f))
        {
            await SeekTimelineAsync(System.Math.Max(0, f) / (double)System.Math.Max(1, _vm.TimelineFps));
            System.Windows.Input.Keyboard.ClearFocus();
        }
    }

    /// <summary>Transport ✂ Trim: cut the clip to the Start/End frame range
    /// (viewer-side trimRange op — undoable, rebases to frame 0).</summary>
    private async void OnTrimRangeClick(object sender, RoutedEventArgs e)
    {
        var fps = System.Math.Max(1, _vm.TimelineFps);
        double s = System.Math.Max(0, _vm.TrimStartFrame) / (double)fps;
        double en = System.Math.Max(_vm.TrimStartFrame + 1, _vm.TrimEndFrame) / (double)fps;
        await SendTimelineCommandAsync("trimRange", null, new { start = s, end = en });
        // Range boxes re-sync from the echoed (shorter) duration.
        _vm.TrimStartFrame = 0;
        _vm.TrimEndFrame = 0;
        _ = SendTimelineCommandAsync("requestSnapshot");
        _vm.StatusText = "Trimmed to range.";
    }

    private void OnTimelineSnapClick(object sender, RoutedEventArgs e)
    {
        // TwoWay binding already flipped IsChecked; just confirm to the user.
        _vm.StatusText = _vm.TimelineSnapEnabled
            ? "Snap on — edits lock to whole frames (hold Shift to bypass)."
            : "Snap off — free scrubbing.";
    }

    /// <summary>Wipe the workspace: strips, clip library, keyframes, pose,
    /// playhead. Shared by the Clear all button and the Shift+Del hotkey, so
    /// both go through the same confirmation — this throws away imported
    /// clips, and getting one back costs a re-import and another retarget.</summary>
    private async Task ClearAllAsync()
    {
        if (!_webViewReady) { _vm.StatusText = "Viewer not ready."; return; }
        if (!_vm.HasAnythingToClear)
        {
            _vm.StatusText = "Nothing to clear — timeline and library are empty.";
            return;
        }

        var what = new List<string>();
        if (_vm.Strips.Count > 0) what.Add($"{_vm.Strips.Count} clip(s) on the timeline");
        if (_vm.ClipLibrary.Count > 0) what.Add($"{_vm.ClipLibrary.Count} clip(s) in the library");
        if (_vm.TimelineKeyframes.Count > 0) what.Add($"{_vm.TimelineKeyframes.Count} keyframe(s)");

        var ok = AppDialog.Show(
            "This clears " + string.Join(", ", what) + ", and resets the pose to bind.\n\n"
                + "Imported clips have to be re-imported. The loaded model stays.",
            "Clear everything?",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (ok != System.Windows.MessageBoxResult.Yes) return;

        await Viewport.CoreWebView2.ExecuteScriptAsync("window.poseClearAll && window.poseClearAll()");
        _vm.StatusText = "Workspace cleared.";
    }

    /// <summary>Sync the VM's keyframe collection from the JS-side
    /// timeline-update payload. PixelX is left at 0 — the new
    /// canvas-based timeline computes positions itself at draw time
    /// from Time + the live canvas width, so we don't need to
    /// pre-bake them here. Kept the collection for the animated-chip
    /// + skipped-bones tracking and external consumers.</summary>
    private void RebuildKeyframeMarkers(JsonElement kEl)
    {
        // Fresh marker state supersedes any bar-drag preview span.
        _barPreviewStart = _barPreviewEnd = -1;
        // Reconcile in place — Clear()+Add used to fire CollectionChanged
        // (and a coalesced redraw) once per key on every pose-timeline-update.
        var byId = new Dictionary<string, KeyframeMarker>(StringComparer.Ordinal);
        foreach (var existing in _vm.TimelineKeyframes)
            byId.TryAdd(existing.Id, existing);
        var live = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<KeyframeMarker>();
        foreach (var t in kEl.EnumerateArray())
        {
            string id;
            double time;
            string ease = "auto";
            if (t.ValueKind == JsonValueKind.Number)
            {
                time = t.GetDouble();
                id = $"summary:{time:0.######}";
            }
            else if (t.ValueKind == JsonValueKind.Object)
            {
                time = t.TryGetProperty("time", out var timeEl) ? timeEl.GetDouble() : 0;
                id = JsonElementId(t, "id") is { Length: > 0 } parsed
                    ? parsed : $"summary:{time:0.######}";
                ease = t.TryGetProperty("ease", out var easeEl)
                    ? easeEl.GetString() ?? "auto" : "auto";
            }
            else continue;
            live.Add(id);
            if (!byId.TryGetValue(id, out var marker))
            {
                marker = new KeyframeMarker { Id = id };
                byId[id] = marker;
            }
            marker.Time = time;
            marker.Ease = ease;
            order.Add(marker);
        }
        // Replace contents only when membership/order actually changed.
        bool same = _vm.TimelineKeyframes.Count == order.Count;
        if (same)
        {
            for (int i = 0; i < order.Count; i++)
            {
                if (!ReferenceEquals(_vm.TimelineKeyframes[i], order[i]))
                { same = false; break; }
            }
        }
        if (!same)
        {
            _vm.TimelineKeyframes.Clear();
            foreach (var marker in order) _vm.TimelineKeyframes.Add(marker);
        }
        for (int i = _vm.TimelineKeyframes.Count - 1; i >= 0; i--)
            if (!live.Contains(_vm.TimelineKeyframes[i].Id))
                _vm.TimelineKeyframes.RemoveAt(i);
        InvalidateDopeVisibleTracks();
        _vm.NotifyAnimatedChipChanged();
    }

    // ════════════════════════════════════════════════════════════════
    // CANVAS-BASED TIMELINE — ruler + track + playhead, all drawn from
    // scratch each redraw. Three layers stacked in the XAML:
    //   * TimelineRulerCanvas    — frame ticks + numeric labels
    //   * TimelineTrackCanvas    — KF diamonds + interaction surface
    //   * TimelinePlayheadCanvas — current-time line + handle on top
    //
    // The canvases are sized via the Border Grid in XAML; we just
    // sample ActualWidth at draw time. RedrawTimeline() rebuilds all
    // three; DrawTimelinePlayhead() runs the hot per-frame path.
    // ════════════════════════════════════════════════════════════════

    private const double TIMELINE_PADDING_X = TimelineController.PaddingX;

    // Brand accents — these aren't theme brushes because they're the
    // FiveOS orange used app-wide (matches the IsAnimatedExport chip,
    // the selected-joint highlight, the SectionHeader accent rail).
    // Everything is frozen: these are drawn thousands of times per frame
    // by the retained render layers, and unfrozen Freezables carry
    // per-use change-tracking cost.
    private static Brush Rgb(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }
    private static Brush Argb(byte a, byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromArgb(a, r, g, b)); br.Freeze(); return br; }
    private static Pen FrozenPen(Brush stroke, double thickness) { var p = new Pen(stroke, thickness); p.Freeze(); return p; }

    // Reference-style dope lanes (2026-07-17, matched to the user's mock):
    // FAT saturated bars filling most of the row — alternating blue/orange —
    // with uniform orange endpoint dots and NO baselines behind them. Dense
    // per-frame runs get the bar; sparse hand-set keys stay as diamonds.
    private static readonly Brush DopeBarOrange = Rgb(0xC8, 0x86, 0x0F);
    private static readonly Brush DopeBarBlue = Rgb(0x3D, 0x6E, 0xC8);
    private static readonly Brush DopeDot = Rgb(0xFF, 0xB6, 0x27);

    private static readonly Brush TimelineKfFillBrush    = Rgb(0xFF, 0xAA, 0x33);
    private static readonly Brush TimelinePlayheadBrush  = Rgb(0xE8, 0x45, 0x45);
    private static readonly Brush TimelinePlayheadBadgeBrush = Rgb(0xE8, 0x45, 0x45);

    // Cascadeur-style sequencer palette
    private static readonly Brush TimelineMajorTickBrush = Rgb(0xC8, 0xC8, 0xC8);
    private static readonly Brush TimelineMinorTickBrush = Rgb(0x55, 0x55, 0x55);
    private static readonly Brush TimelineLabelBrush     = Rgb(0xD0, 0xD0, 0xD0);
    private static readonly Brush TimelineTrackLineBrush = Rgb(0x38, 0x38, 0x38);
    private static readonly Brush TimelineGridBrush      = Rgb(0x33, 0x33, 0x33);
    private static readonly Brush TimelineKfStrokeBrush  = Rgb(0xF5, 0xF6, 0xFB);

    // Pens + text resources for the DrawingContext-rendered layers.
    // Alpha is baked into the grid pens (the old per-element Opacity).
    private static readonly Pen TimelineMajorTickPen   = FrozenPen(TimelineMajorTickBrush, 1);
    private static readonly Pen TimelineMinorTickPen   = FrozenPen(TimelineMinorTickBrush, 1);
    private static readonly Pen DopeMajorGridPen       = FrozenPen(Argb(115, 0xC8, 0xC8, 0xC8), 1);    // 0.45 opacity
    private static readonly Pen DopeMinorGridPen       = FrozenPen(Argb(51, 0x33, 0x33, 0x33), 0.5);   // 0.20 opacity
    private static readonly Pen DopeRowBaselinePen     = FrozenPen(Rgb(0x34, 0x34, 0x34), 1);
    private static readonly Pen TimelineTrackLinePen   = FrozenPen(TimelineTrackLineBrush, 1);
    private static readonly Pen TimelineKfStrokePen    = FrozenPen(TimelineKfStrokeBrush, 1);
    private static readonly Pen TimelineKfSelectedPen  = FrozenPen(TimelinePlayheadBrush, 2);
    private static readonly FontFamily TimelineFontFamily = new("Consolas");
    private static readonly Typeface TimelineTypeface = new(TimelineFontFamily,
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private System.Windows.Shapes.Line? _playheadLine;
    private Border? _playheadBadge;
    private TextBlock? _playheadFrameLabel;

    private double TimelineCanvasWidth
    {
        get
        {
            // Never measure the keyframe lane — it's collapsed in simple mode
            // (ActualWidth = 0), which was squashing the clip bar to a sliver.
            double w = TimelineStripCanvas?.ActualWidth ?? 0;
            if (w < 10) w = TimelineRulerCanvas?.ActualWidth ?? 0;
            if (w < 10) w = TimelinePlayheadCanvas?.ActualWidth ?? 0;
            if (w < 10) w = DopeSheetCanvas?.ActualWidth ?? 0;
            return w;
        }
    }

    private void SyncTimelineControllerFromVm()
    {
        _timelineCtl.Zoom = _vm.TimelineZoom;
        _timelineCtl.ScrollOffset = _vm.TimelineScrollOffset;
        _timelineCtl.ClampScroll(_vm.TimelineDuration);
    }

    private void PushTimelineControllerToVm()
    {
        _vm.TimelineZoom = _timelineCtl.Zoom;
        _vm.TimelineScrollOffset = _timelineCtl.ScrollOffset;
    }

    private double TimelineUsableWidth =>
        _timelineCtl.UsableWidth(TimelineCanvasWidth);

    private double TimeToTimelineX(double timeSec)
    {
        SyncTimelineControllerFromVm();
        return _timelineCtl.TimeToX(timeSec, _vm.TimelineDuration, TimelineCanvasWidth);
    }

    private double TimelineXToTime(double x)
    {
        SyncTimelineControllerFromVm();
        var t = _timelineCtl.XToTime(x, _vm.TimelineDuration, TimelineCanvasWidth);
        return _vm.TimelineSnapEnabled &&
               !System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)
            ? _timelineCtl.SnapTime(t, _vm.TimelineFps)
            : t;
    }

    private double VisibleTimelineDuration =>
        _timelineCtl.VisibleDuration(System.Math.Max(0.001, _vm.TimelineDuration));

    private void OnTimelineMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        SyncTimelineControllerFromVm();
        var canvas = sender as FrameworkElement ?? TimelineStripCanvas;
        if (canvas is null) return;
        var pos = e.GetPosition(canvas);
        var anchorTime = _timelineCtl.XToTime(pos.X, _vm.TimelineDuration, canvas.ActualWidth);
        // Standard 3D-app convention (user directive 2026-07-17): plain wheel
        // ZOOMS at the cursor (works over the layer bars AND the dope-sheet
        // lanes); Shift/Ctrl+wheel pans the timeline horizontally.
        var mods = System.Windows.Input.Keyboard.Modifiers;
        bool pan = mods.HasFlag(System.Windows.Input.ModifierKeys.Shift)
                || mods.HasFlag(System.Windows.Input.ModifierKeys.Control);
        if (!pan)
            _timelineCtl.ZoomAt(e.Delta, anchorTime, _vm.TimelineDuration);
        else
            _timelineCtl.ScrollByTime(-(e.Delta / 120.0) * VisibleTimelineDuration * 0.08, _vm.TimelineDuration);
        PushTimelineControllerToVm();
        ScheduleRedrawTimeline();
        e.Handled = true;
    }

    private async Task SyncStripRangeToViewerAsync(TimelineStrip strip)
    {
        if (!_webViewReady) return;
        var startArg = strip.Start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var durArg = strip.Duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var srcStartArg = strip.SourceStart.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var srcEndArg = strip.SourceEnd.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        await Viewport.CoreWebView2.ExecuteScriptAsync(
            $"window.poseSetStripRange && window.poseSetStripRange({strip.Id}, {startArg}, {durArg}, {srcStartArg}, {srcEndArg})");
    }

    private void PushClipTrackVisibilityToViewer()
    {
        if (!_webViewReady) return;
        var on = _vm.TimelineClipTrackVisible ? "true" : "false";
        _ = Viewport.CoreWebView2.ExecuteScriptAsync(
            $"window.poseSetClipTrackEnabled && window.poseSetClipTrackEnabled({on})");
    }

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e) => RedrawTimeline();

    /// <summary>Coalescing wrapper for event-driven redraws: N triggers in
    /// one dispatcher pump = ONE RedrawTimeline. The keyframe snapshot path
    /// used to queue a separate full redraw per CollectionChanged event —
    /// 700 keys meant 700 queued rebuilds.</summary>
    private bool _redrawTimelineQueued;
    private void ScheduleRedrawTimeline()
    {
        if (_redrawTimelineQueued) return;
        _redrawTimelineQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _redrawTimelineQueued = false;
            RedrawTimeline();
        }), DispatcherPriority.Render);
    }

    private void RedrawTimeline()
    {
        // Unified layers timeline: layer bars AND per-bone lanes are both
        // always visible, so redraw both sections.
        if (TimelineRulerCanvas is null) return;
        DrawTimelineRuler();
        DrawTimelineStrips();
        DrawTimelineTrack();
        DrawTimelinePlayhead();
        DrawDopeSheet();
        UpdateTimelineNavBar();
    }

    // ── Zoom navigator (bottom bar): thumb = the visible window. ──────────
    private bool _navDragging;
    private double _navDragStartX, _navDragStartOffset;

    private void UpdateTimelineNavBar()
    {
        if (TimelineNavBar is null || TimelineNavThumb is null) return;
        var trackW = System.Math.Max(0, TimelineNavBar.ActualWidth - 2);
        if (trackW < 10) return;
        var dur = System.Math.Max(0.001, _vm.TimelineDuration);
        var vis = System.Math.Min(dur, VisibleTimelineDuration);
        var frac = vis / dur;
        var w = System.Math.Min(trackW, System.Math.Max(24, trackW * frac));
        var range = System.Math.Max(0.0001, dur - vis);
        var x = trackW <= w ? 0 : System.Math.Clamp(_timelineCtl.ScrollOffset / range, 0, 1) * (trackW - w);
        System.Windows.Controls.Canvas.SetLeft(TimelineNavThumb, x);
        TimelineNavThumb.Width = w;
        var zoomedIn = frac < 0.999;
        TimelineNavThumb.Opacity = zoomedIn ? 0.85 : 0.3;
        TimelineNavBar.Cursor = zoomedIn ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
    }

    private void OnNavBarMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TimelineNavBar is null || TimelineNavThumb is null) return;
        SyncTimelineControllerFromVm();
        var dur = System.Math.Max(0.001, _vm.TimelineDuration);
        var vis = System.Math.Min(dur, VisibleTimelineDuration);
        if (vis / dur >= 0.999) return;   // zoomed out — inert
        var trackW = System.Math.Max(1, TimelineNavBar.ActualWidth - 2);
        var thumbW = TimelineNavThumb.Width;
        var pos = e.GetPosition(TimelineNavBar).X;
        var thumbX = System.Windows.Controls.Canvas.GetLeft(TimelineNavThumb);
        if (pos < thumbX || pos > thumbX + thumbW)
        {
            // Jump: center the window at the clicked position.
            var frac = System.Math.Clamp((pos - thumbW / 2) / System.Math.Max(1, trackW - thumbW), 0, 1);
            _vm.TimelineScrollOffset = frac * (dur - vis);
            SyncTimelineControllerFromVm();
            ScheduleRedrawTimeline();
        }
        _navDragging = true;
        _navDragStartX = e.GetPosition(TimelineNavBar).X;
        _navDragStartOffset = _vm.TimelineScrollOffset;
        TimelineNavBar.CaptureMouse();
        e.Handled = true;
    }

    private void OnNavBarMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_navDragging || TimelineNavBar is null || TimelineNavThumb is null) return;
        var dur = System.Math.Max(0.001, _vm.TimelineDuration);
        var vis = System.Math.Min(dur, VisibleTimelineDuration);
        var trackW = System.Math.Max(1, TimelineNavBar.ActualWidth - 2);
        var thumbW = TimelineNavThumb.Width;
        var usable = System.Math.Max(1, trackW - thumbW);
        var dx = e.GetPosition(TimelineNavBar).X - _navDragStartX;
        _vm.TimelineScrollOffset = System.Math.Clamp(
            _navDragStartOffset + dx / usable * (dur - vis), 0, System.Math.Max(0, dur - vis));
        SyncTimelineControllerFromVm();
        ScheduleRedrawTimeline();
    }

    private void OnNavBarMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_navDragging) return;
        _navDragging = false;
        TimelineNavBar?.ReleaseMouseCapture();
    }

    private void OnSequencerModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle)
            toggle.IsChecked = true;
        _vm.TimelineMode = TimelineEditorMode.Sequencer;
        _vm.StatusText = "Sequencer — edit whole animation clips.";
        ScheduleRedrawTimeline();
    }

    private async void OnDopeSheetModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle)
            toggle.IsChecked = true;
        _vm.TimelineMode = TimelineEditorMode.DopeSheet;
        _vm.StatusText = "Dope Sheet — edit per-bone keys.";
        await SendTimelineCommandAsync("requestSnapshot");
        ScheduleRedrawTimeline();
    }

    private async Task SendTimelineCommandAsync(
        string command,
        IEnumerable<string>? ids = null,
        object? payload = null)
    {
        if (!_webViewReady) return;
        var message = JsonSerializer.Serialize(new
        {
            kind = "host-timeline-command",
            command,
            ids = ids?.ToArray() ?? Array.Empty<string>(),
            payload,
        });
        Viewport.CoreWebView2.PostWebMessageAsJson(message);
        await Task.CompletedTask;
    }

    private async void OnTimelineDeleteSelection(object sender, RoutedEventArgs e) =>
        await DeleteTimelineSelectionAsync();

    private async Task DeleteTimelineSelectionAsync()
    {
        var selected = _timelineSelection.Items.ToArray();
        if (selected.Length == 0)
        {
            _vm.StatusText = "Select a clip or key before deleting.";
            return;
        }
        var strips = selected.Where(x => x.Kind == TimelineItemKind.Strip).Select(x => x.Id).ToArray();
        var keys = selected.Where(x => x.Kind == TimelineItemKind.Keyframe).Select(x => x.Id).ToArray();
        if (strips.Length > 0) await SendTimelineCommandAsync("deleteStrips", strips);
        if (keys.Length > 0) await SendTimelineCommandAsync("deleteKeys", keys);
        _timelineSelection.Clear();
        _vm.StatusText = $"Deleted {selected.Length} timeline item(s).";
    }

    private async void OnTimelineDuplicateSelection(object sender, RoutedEventArgs e) =>
        await DuplicateTimelineSelectionAsync();

    private async Task DuplicateTimelineSelectionAsync()
    {
        var ids = _timelineSelection.Items.Select(x => x.Id).ToArray();
        if (ids.Length == 0)
        {
            _vm.StatusText = "Select a clip or key before duplicating.";
            return;
        }
        await SendTimelineCommandAsync("duplicate", ids);
        _vm.StatusText = $"Duplicated {ids.Length} timeline item(s).";
    }

    private async Task CopyTimelineSelectionAsync()
    {
        _timelineClipboard.Clear();
        _timelineClipboard.AddRange(_timelineSelection.Items);
        await SendTimelineCommandAsync("copy", _timelineClipboard.Select(x => x.Id));
        _vm.StatusText = $"Copied {_timelineClipboard.Count} timeline item(s).";
    }

    private Task PasteTimelineSelectionAsync() =>
        SendTimelineCommandAsync("paste", payload: new { time = _vm.TimelineTime });

    private async void OnTimelinePreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_vm.HasRig || e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;
        var mods = System.Windows.Input.Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(System.Windows.Input.ModifierKeys.Control);
        switch (e.Key)
        {
            case System.Windows.Input.Key.Delete:
            case System.Windows.Input.Key.Back:
                await DeleteTimelineSelectionAsync();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Space:
                await ToggleTimelinePlaybackAsync();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.C when ctrl:
                await CopyTimelineSelectionAsync();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.V when ctrl:
                await PasteTimelineSelectionAsync();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.D when ctrl:
                await DuplicateTimelineSelectionAsync();
                e.Handled = true;
                break;
            // Unified undo: hostUndo/hostRedo pop the newest action across
            // BOTH viewer stacks (pose-bone edits + timeline ops). The old
            // routing sent these to the timeline stack only, so Ctrl+Z
            // after a pose edit was a silent no-op whenever any WPF
            // control had focus.
            case System.Windows.Input.Key.Z when ctrl:
                if (_webViewReady)
                    await Viewport.CoreWebView2.ExecuteScriptAsync(
                        mods.HasFlag(System.Windows.Input.ModifierKeys.Shift)
                            ? "window.hostRedo && window.hostRedo()"
                            : "window.hostUndo && window.hostUndo()");
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Y when ctrl:
                if (_webViewReady)
                    await Viewport.CoreWebView2.ExecuteScriptAsync("window.hostRedo && window.hostRedo()");
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Home:
                await SetTimelineTimeAsync(0);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.End:
                await SetTimelineTimeAsync(_vm.TimelineDuration);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Left:
            case System.Windows.Input.Key.OemComma:
                await SetTimelineTimeAsync(_vm.TimelineTime -
                    (mods.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? 10 : 1) /
                    (double)System.Math.Max(1, _vm.TimelineFps));
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Right:
            case System.Windows.Input.Key.OemPeriod:
                await SetTimelineTimeAsync(_vm.TimelineTime +
                    (mods.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? 10 : 1) /
                    (double)System.Math.Max(1, _vm.TimelineFps));
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Up:
            case System.Windows.Input.Key.Down:
                var allTimes = _vm.TimelineTracks.SelectMany(x => x.Keys)
                    .Select(x => x.Time).Distinct().OrderBy(x => x).ToArray();
                var target = e.Key == System.Windows.Input.Key.Down
                    ? allTimes.FirstOrDefault(x => x > _vm.TimelineTime + 1e-4, _vm.TimelineTime)
                    : allTimes.LastOrDefault(x => x < _vm.TimelineTime - 1e-4, _vm.TimelineTime);
                await SetTimelineTimeAsync(target);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.I:
                await TrimSelectedStripsAsync("in");
                e.Handled = true;
                break;
            case System.Windows.Input.Key.O:
                await TrimSelectedStripsAsync("out");
                e.Handled = true;
                break;
            case System.Windows.Input.Key.K:
                OnAddKeyframe(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    // Same pair the transport Play button uses — window.poseTogglePlayback
    // never existed in the viewer, so the old call made Space a silent no-op.
    private Task ToggleTimelinePlaybackAsync() =>
        Viewport.CoreWebView2.ExecuteScriptAsync(_vm.TimelinePlaying
            ? "window.posePause && window.posePause()"
            : "window.posePlay && window.posePlay()");

    private async Task SetTimelineTimeAsync(double time)
    {
        _vm.TimelineTime = System.Math.Clamp(time, 0, _vm.TimelineDuration);
        if (_webViewReady)
        {
            var arg = _vm.TimelineTime.ToString(
                "0.###", System.Globalization.CultureInfo.InvariantCulture);
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.poseSetTime && window.poseSetTime({arg})");
        }
    }

    /// <summary>Called when only the strip collection changed — skips
    /// re-drawing the ruler and keyframe diamonds, which are unaffected.</summary>
    private void RedrawStrips()
    {
        if (TimelineStripCanvas is null) return;
        DrawTimelineStrips();
    }

    // Clip fill palette tuned for the dark-navy take-sequencer surface:
    // imported clips get a salmon-pink fill (matches T1 in the reference
    // mockup), baked clips get a teal — both fully opaque so they read
    // sharply on the #161A26 lane background.
    private static readonly Brush StripFillBrushBaked    = Rgb(0x5B, 0xBF, 0xB5);
    private static readonly Brush StripFillBrushImported = Rgb(0x4A, 0x90, 0xC4);
    private static readonly Brush StripBorderBrush       = Rgb(0x2E, 0x5A, 0x80);
    private static readonly Brush StripTextBrush         = Rgb(0xFA, 0xFA, 0xFA);
    // Semi-opaque dark wash painted over the fade region. Conveys "this
    // region's weight ramps to zero" without competing with the strip
    // colour. Triangular polygon: 0% alpha at the inner edge, full at
    // the strip edge — matches the linear ramp the evaluator uses.
    // (0.65 opacity baked into the alpha channel so the brush can freeze.)
    private static readonly Brush StripFadeBrush         = Argb(0xA6, 0x10, 0x16, 0x28);
    // Per-redraw brushes the strip lane used to allocate inline.
    private static readonly Brush StripLaneFillBrush     = Rgb(0x28, 0x28, 0x28);
    private static readonly Brush StripLaneStrokeBrush   = Rgb(0x3A, 0x3A, 0x3A);
    private static readonly Brush StripHintTextBrush     = Rgb(0x66, 0x66, 0x66);
    private static readonly Brush StripWipTextBrush      = Rgb(0x12, 0x14, 0x1C);
    private static readonly Brush StripWipBadgeBrush     = Argb(0xE6, 0xE6, 0xEC, 0xFA);
    private static readonly Brush StripTrimHandleBrush   = Argb(0xCC, 0xFF, 0xD5, 0x4A);

    private void DrawTimelineStrips()
    {
        var canvas = TimelineStripCanvas;
        if (canvas is null) return;
        canvas.Children.Clear();
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 10 || h < 5) return;

        var usable = TimelineUsableWidth;
        var dur = System.Math.Max(0.001, _vm.TimelineDuration);
        SyncTimelineControllerFromVm();
        var vis = VisibleTimelineDuration;
        var scroll = _timelineCtl.ScrollOffset;

        // Empty lane guide
        var lane = new System.Windows.Shapes.Rectangle
        {
            Width = usable,
            Height = System.Math.Max(26, h - 8),
            RadiusX = 0, RadiusY = 0,
            Fill = StripLaneFillBrush,
            Stroke = StripLaneStrokeBrush,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(lane, TIMELINE_PADDING_X);
        Canvas.SetTop(lane, 4);
        canvas.Children.Add(lane);

        // Vertical frame grid (Cascadeur-style) — visible window only when zoomed
        var fps = System.Math.Max(1, _vm.TimelineFps);
        var totalFrames = System.Math.Max(1, (int)System.Math.Round(dur * fps));
        int fStart = (int)System.Math.Floor(scroll * fps);
        int fEnd = (int)System.Math.Ceiling((scroll + vis) * fps);
        fStart = System.Math.Clamp(fStart, 0, totalFrames);
        fEnd = System.Math.Clamp(fEnd, 0, totalFrames);
        int spanFrames = System.Math.Max(1, fEnd - fStart);
        var pxPerFrame = usable / (vis * fps);
        int gridEvery = 1;
        while (gridEvery * pxPerFrame < 6) gridEvery *= 2;
        for (int f = fStart; f <= fEnd; f += gridEvery)
        {
            double gx = TimeToTimelineX(f / (double)fps);
            var gridLine = new System.Windows.Shapes.Line
            {
                X1 = gx, X2 = gx,
                Y1 = 4, Y2 = 4 + lane.Height,
                Stroke = TimelineGridBrush,
                StrokeThickness = 1,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
            };
            canvas.Children.Add(gridLine);
        }

        // No strips: the whole clip track — gutter row and this lane — is
        // collapsed via ShowClipTrack, so there is nothing to draw here.
        if (_vm.Strips.Count == 0) return;

        if (!_vm.TimelineClipTrackVisible)
        {
            var hint = new TextBlock
            {
                Text = "Clip track hidden — click eye to show",
                FontSize = 10,
                Foreground = StripHintTextBrush,
                IsHitTestVisible = false,
            };
            hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(hint, TIMELINE_PADDING_X + 8);
            Canvas.SetTop(hint, 4 + (lane.Height - hint.DesiredSize.Height) / 2);
            canvas.Children.Add(hint);
            return;
        }

        foreach (var strip in _vm.Strips)
        {
            double x0 = TimeToTimelineX(strip.Start);
            double x1 = TimeToTimelineX(strip.Start + strip.Duration);
            double width = System.Math.Max(24, x1 - x0);

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = width,
                Height = System.Math.Max(26, h - 8),
                RadiusX = 0, RadiusY = 0,
                Fill = strip.Kind == "imported" ? StripFillBrushImported : StripFillBrushBaked,
                Stroke = strip.IsSelected ? TimelinePlayheadBrush : StripBorderBrush,
                StrokeThickness = strip.IsSelected ? 2.5 : 1,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = strip,
                ToolTip = $"{strip.ClipName} — {strip.Duration:F2}s @ {strip.Start:F2}s (src {strip.TrimLabel}). Click to select, drag to move, double-click for Dope Sheet.",
            };
            rect.MouseLeftButtonDown += OnStripMouseDown;
            rect.MouseMove           += OnStripMouseMove;
            rect.MouseLeftButtonUp   += OnStripMouseUp;
            rect.MouseRightButtonUp  += OnStripRightClick;
            Canvas.SetLeft(rect, x0);
            Canvas.SetTop(rect, 4);
            canvas.Children.Add(rect);

            // Fade-in / fade-out triangles. Each triangle is filled
            // with a semi-opaque dark wash so the user reads weight=0
            // at the outer edge and weight=1 at the inner edge of the
            // ramp. IsHitTestVisible off so drags fall through to the
            // strip body rectangle below.
            double rectH = h - 6;
            if (strip.FadeIn > 0)
            {
                double fadeW = System.Math.Min(width, (strip.FadeIn / System.Math.Max(0.001, strip.Duration)) * width);
                var tri = new System.Windows.Shapes.Polygon
                {
                    Points = new PointCollection
                    {
                        new System.Windows.Point(0,      0),
                        new System.Windows.Point(fadeW,  0),
                        new System.Windows.Point(0,      rectH),
                    },
                    Fill = StripFadeBrush,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(tri, x0);
                Canvas.SetTop(tri, 3);
                canvas.Children.Add(tri);
            }
            if (strip.FadeOut > 0)
            {
                double fadeW = System.Math.Min(width, (strip.FadeOut / System.Math.Max(0.001, strip.Duration)) * width);
                var tri = new System.Windows.Shapes.Polygon
                {
                    Points = new PointCollection
                    {
                        new System.Windows.Point(width,         0),
                        new System.Windows.Point(width,         rectH),
                        new System.Windows.Point(width - fadeW, rectH),
                    },
                    Fill = StripFadeBrush,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(tri, x0);
                Canvas.SetTop(tri, 3);
                canvas.Children.Add(tri);
            }

            // Strip label: clip name truncated to fit. IsHitTestVisible
            // off so drag/click events fall through to the rectangle.
            var label = new TextBlock
            {
                Text = strip.ClipName,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = StripTextBrush,
                IsHitTestVisible = false,
                MaxWidth = System.Math.Max(0, width - 38),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, x0 + 8);
            Canvas.SetTop(label, 3 + ((h - 6) - label.DesiredSize.Height) / 2);
            canvas.Children.Add(label);

            // "WIP" badge — only on manually baked clips, not imports.
            if (width > 64 && strip.Kind != "imported")
            {
                var wipText = new TextBlock
                {
                    Text = "WIP",
                    FontSize = 8,
                    FontWeight = FontWeights.Bold,
                    FontFamily = TimelineFontFamily,
                    Foreground = StripWipTextBrush,
                    IsHitTestVisible = false,
                    Padding = new Thickness(4, 1, 4, 1),
                };
                wipText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var wipBadge = new Border
                {
                    Background = StripWipBadgeBrush,
                    CornerRadius = new CornerRadius(2),
                    Child = wipText,
                    IsHitTestVisible = false,
                    Height = 14,
                };
                Canvas.SetLeft(wipBadge, x0 + width - wipText.DesiredSize.Width - 12);
                Canvas.SetTop(wipBadge, 5);
                canvas.Children.Add(wipBadge);
            }
            // Left / right trim handles — drag to set in/out visually.
            const double handleVisualW = 7;
            const double handleHitW = 14;
            if (!_vm.TimelineClipTrackLocked && width > handleHitW * 3)
            {
                foreach (var (isLeft, hx, hw) in new[] {
                    (true, x0 - (handleHitW - handleVisualW) / 2, handleHitW),
                    (false, x1 - handleVisualW - (handleHitW - handleVisualW) / 2, handleHitW) })
                {
                    var hit = new System.Windows.Shapes.Rectangle
                    {
                        Width = hw,
                        Height = h - 6,
                        Fill = System.Windows.Media.Brushes.Transparent,
                        Cursor = System.Windows.Input.Cursors.SizeWE,
                        Tag = (strip, isLeft),
                        ToolTip = isLeft ? "Drag to trim clip start" : "Drag to trim clip end",
                    };
                    hit.MouseLeftButtonDown += OnStripTrimMouseDown;
                    hit.MouseMove += OnStripTrimMouseMove;
                    hit.MouseLeftButtonUp += OnStripTrimMouseUp;
                    Canvas.SetLeft(hit, hx);
                    Canvas.SetTop(hit, 3);
                    canvas.Children.Add(hit);

                    var handle = new System.Windows.Shapes.Rectangle
                    {
                        Width = handleVisualW,
                        Height = h - 6,
                        RadiusX = 2, RadiusY = 2,
                        Fill = StripTrimHandleBrush,
                        Stroke = StripBorderBrush,
                        StrokeThickness = 1,
                        IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(handle, isLeft ? x0 : x1 - handleVisualW);
                    Canvas.SetTop(handle, 3);
                    canvas.Children.Add(handle);
                }
            }
        }
    }

    private TimelineStrip? _trimStrip;
    private bool _trimLeft;
    private double _trimStartX;
    private double _trimOrigStart;
    private double _trimOrigDur;
    private double _trimOrigSrcStart;
    private double _trimOrigSrcEnd;

    private void OnStripTrimMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_vm.TimelineClipTrackLocked) { e.Handled = true; return; }
        if (sender is not FrameworkElement fe || fe.Tag is not ValueTuple<TimelineStrip, bool> tag) return;
        _trimStrip = tag.Item1;
        _trimLeft = tag.Item2;
        _trimStartX = e.GetPosition(TimelineStripCanvas).X;
        _trimOrigStart = _trimStrip.Start;
        _trimOrigDur = _trimStrip.Duration;
        _trimOrigSrcStart = _trimStrip.SourceStart;
        _trimOrigSrcEnd = _trimStrip.SourceEnd;
        fe.CaptureMouse();
        e.Handled = true;
    }

    private void OnStripTrimMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_trimStrip is null) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        var dx = e.GetPosition(TimelineStripCanvas).X - _trimStartX;
        var usable = TimelineUsableWidth;
        var dt = (dx / usable) * VisibleTimelineDuration;
        var frame = 1.0 / System.Math.Max(1, _vm.TimelineFps);
        dt = System.Math.Round(dt / frame) * frame;
        const double minDur = TimelineController.MinClipDurationSec;
        if (_trimLeft)
        {
            var maxTrim = _trimOrigDur - minDur;
            dt = System.Math.Clamp(dt, -_trimOrigStart, maxTrim);
            _trimStrip.Start = System.Math.Round(_trimOrigStart + dt, 3);
            _trimStrip.Duration = System.Math.Round(_trimOrigDur - dt, 3);
            _trimStrip.SourceStart = System.Math.Round(_trimOrigSrcStart + dt, 3);
        }
        else
        {
            dt = System.Math.Clamp(dt, minDur - _trimOrigDur, _vm.TimelineDuration - _trimOrigStart - _trimOrigDur);
            _trimStrip.Duration = System.Math.Round(_trimOrigDur + dt, 3);
            _trimStrip.SourceEnd = System.Math.Round(_trimOrigSrcStart + _trimStrip.Duration, 3);
        }
        RedrawStrips();
        _trimStripPendingSync = _trimStrip;
        _trimSyncTimer?.Stop();
        _trimSyncTimer?.Start();
        e.Handled = true;
    }

    private async void OnStripTrimMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_trimStrip is null) return;
        if (sender is FrameworkElement fe) fe.ReleaseMouseCapture();
        var strip = _trimStrip;
        _trimStrip = null;
        _trimSyncTimer?.Stop();
        _trimStripPendingSync = null;
        e.Handled = true;
        await SyncStripRangeToViewerAsync(strip);
    }

    private Controls.TimelineRenderLayer? _seqRulerLayer;
    private Controls.TimelineRenderLayer? _seqKfLaneLayer;

    private void DrawTimelineRuler()
    {
        var canvas = TimelineRulerCanvas;
        if (canvas is null) return;
        if (_seqRulerLayer is null)
        {
            _seqRulerLayer = new Controls.TimelineRenderLayer { RenderCallback = RenderTimelineRulerLayer };
            canvas.Children.Add(_seqRulerLayer);
        }
        SyncTimelineControllerFromVm();
        _seqRulerLayer.InvalidateVisual();
    }

    private void RenderTimelineRulerLayer(DrawingContext dc)
    {
        var canvas = TimelineRulerCanvas;
        if (canvas is null) return;
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 10 || h < 5) return;

        var dur = System.Math.Max(0.001, _vm.TimelineDuration);
        var fps = System.Math.Max(1, _vm.TimelineFps);
        var vis = VisibleTimelineDuration;
        var scroll = _timelineCtl.ScrollOffset;
        var usable = TimelineUsableWidth;
        var pxPerFrame = usable / (vis * fps);

        int fStart = (int)System.Math.Floor(scroll * fps);
        int fEnd = (int)System.Math.Ceiling((scroll + vis) * fps);
        fStart = System.Math.Max(0, fStart);
        fEnd = System.Math.Min((int)System.Math.Round(dur * fps), fEnd);

        int labelEveryN = NiceFrameStep(pxPerFrame, 42);
        if (pxPerFrame >= 16) labelEveryN = 1;
        else if (pxPerFrame >= 9 && labelEveryN > 5) labelEveryN = 5;

        // Minor ticks stride so they never pack tighter than ~3 px — the
        // old code drew one Line element per frame, which at zoom-out was
        // hundreds of overlapping ticks rendered as a solid smear.
        int tickEveryN = 1;
        while (tickEveryN * pxPerFrame < 3) tickEveryN *= 2;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var majorTicks = new StreamGeometry();
        var minorTicks = new StreamGeometry();
        using (var majorCtx = majorTicks.Open())
        using (var minorCtx = minorTicks.Open())
        {
            for (int f = fStart; f <= fEnd; f++)
            {
                bool major = f % labelEveryN == 0;
                if (!major && f % tickEveryN != 0) continue;
                double x = TimeToTimelineX(f / (double)fps);
                var ctx = major ? majorCtx : minorCtx;
                ctx.BeginFigure(new Point(x, h - (major ? 10 : 4)), false, false);
                ctx.LineTo(new Point(x, h), true, false);
                if (major)
                {
                    var label = new FormattedText(f.ToString(),
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, TimelineTypeface, 9, TimelineLabelBrush, dpi);
                    dc.DrawText(label, new Point(x - label.Width / 2, 2));
                }
            }
        }
        majorTicks.Freeze();
        minorTicks.Freeze();
        dc.DrawGeometry(null, TimelineMinorTickPen, minorTicks);
        dc.DrawGeometry(null, TimelineMajorTickPen, majorTicks);
    }

    private void DrawTimelineTrack()
    {
        var canvas = TimelineTrackCanvas;
        if (canvas is null) return;
        if (_seqKfLaneLayer is null)
        {
            _seqKfLaneLayer = new Controls.TimelineRenderLayer { RenderCallback = RenderTimelineKfLaneLayer };
            canvas.Children.Add(_seqKfLaneLayer);
        }
        SyncTimelineControllerFromVm();
        _seqKfLaneLayer.InvalidateVisual();
    }

    /// <summary>Sequencer summary-keyframe lane: baseline + one diamond per
    /// visible keyframe, batched into a single geometry (the old code made a
    /// Polygon with four mouse handlers per key — interaction now lives on
    /// the canvas via HitTestSummaryKf).</summary>
    private void RenderTimelineKfLaneLayer(DrawingContext dc)
    {
        var canvas = TimelineTrackCanvas;
        if (canvas is null) return;
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 10 || h < 5) return;
        var midY = h / 2.0;

        // MANUAL keys only (user directive 2026-07-17): imported animations are
        // baked per-frame keys (ease=linear) and must NEVER render as dot spam —
        // they read as the LAYER bar below instead. Diamonds here are reserved
        // for keys the user set by hand (◉/K → ease=auto).
        var baseline = new StreamGeometry();
        var diamonds = new StreamGeometry();
        var drawnColumns = new HashSet<int>();
        using (var baseCtx = baseline.Open())
        using (var kfCtx = diamonds.Open())
        {
            baseCtx.BeginFigure(new Point(TIMELINE_PADDING_X, midY), false, false);
            baseCtx.LineTo(new Point(w - TIMELINE_PADDING_X, midY), true, false);
            foreach (var kf in _vm.TimelineKeyframes)
            {
                if (string.Equals(kf.Ease, "linear", StringComparison.OrdinalIgnoreCase)) continue;
                var x = TimeToTimelineXRaw(kf.Time);
                if (x < -8 || x > w + 8) continue;
                if (!drawnColumns.Add((int)System.Math.Round(x))) continue;
                kfCtx.BeginFigure(new Point(x, midY - 8), true, true);
                kfCtx.LineTo(new Point(x + 8, midY), true, false);
                kfCtx.LineTo(new Point(x, midY + 8), true, false);
                kfCtx.LineTo(new Point(x - 8, midY), true, false);
            }
        }
        baseline.Freeze();
        diamonds.Freeze();
        dc.DrawGeometry(null, TimelineTrackLinePen, baseline);
        dc.DrawGeometry(TimelineKfFillBrush, TimelineKfStrokePen, diamonds);
    }

    /// <summary>Unclamped time→x for the sequencer lane (see DopeTimeToXRaw).</summary>
    private double TimeToTimelineXRaw(double timeSec)
    {
        var vis = VisibleTimelineDuration;
        return TIMELINE_PADDING_X + (timeSec - _timelineCtl.ScrollOffset) / vis * TimelineUsableWidth;
    }

    /// <summary>Nearest summary keyframe within the old diamond's hit size
    /// (±10 px around the lane's midline), or null.</summary>
    private KeyframeMarker? HitTestSummaryKf(Point p)
    {
        var canvas = TimelineTrackCanvas;
        if (canvas is null || canvas.ActualWidth < 10) return null;
        SyncTimelineControllerFromVm();
        if (System.Math.Abs(p.Y - canvas.ActualHeight / 2.0) > 10) return null;
        KeyframeMarker? best = null;
        double bestDistance = 10;
        foreach (var kf in _vm.TimelineKeyframes)
        {
            var distance = System.Math.Abs(TimeToTimelineXRaw(kf.Time) - p.X);
            if (distance <= bestDistance) { bestDistance = distance; best = kf; }
        }
        return best;
    }

    private double DopeTimeToX(double time) =>
        _timelineCtl.TimeToX(time, _vm.TimelineDuration, DopeSheetCanvas?.ActualWidth ?? 0);

    /// <summary>Unclamped time→x for the dope sheet: unlike
    /// <see cref="TimelineController.TimeToX"/> (which pins off-window times
    /// to the viewport edges), off-screen keys map to their true x so the
    /// renderer can CULL them instead of stacking them at the edges.</summary>
    private double DopeTimeToXRaw(double time)
    {
        var vis = VisibleTimelineDuration;
        var usable = _timelineCtl.UsableWidth(DopeSheetCanvas?.ActualWidth ?? 0);
        return TimelineController.PaddingX + (time - _timelineCtl.ScrollOffset) / vis * usable;
    }

    private double DopeXToTime(double x)
    {
        var t = _timelineCtl.XToTime(x, _vm.TimelineDuration, DopeSheetCanvas?.ActualWidth ?? 0);
        if (_vm.TimelineSnapEnabled &&
            !System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
            t = _timelineCtl.SnapTime(t, _vm.TimelineFps);
        return System.Math.Clamp(t, 0, _vm.TimelineDuration);
    }

    private const double DopeRowHeight = 22;   // tight AE-style rows (was 26)

    /// <summary>Frame stride for ruler labels/grid: 1,5,10,20,50,100… — the
    /// reference labels every 5 frames, not power-of-two strides.</summary>
    private static int NiceFrameStep(double pxPerFrame, double minPx)
    {
        foreach (var step in new[] { 1, 5, 10, 20, 50, 100, 200, 500, 1000 })
            if (step * pxPerFrame >= minPx) return step;
        return 2000;
    }
    private Controls.TimelineRenderLayer? _dopeSheetLayer;   // grid + baselines + key diamonds
    private Controls.TimelineRenderLayer? _dopeRulerLayer;   // frame-number labels
    private System.Windows.Shapes.Line? _dopePlayheadLine;   // cached, X-mutated per tick

    private void InvalidateDopeVisibleTracks()
    {
        _dopeVisibleTracksVersion++;
        _dopeVisibleTracksCache = null;
    }

    /// <summary>ScrollViewer lives inside the ItemsControl template (for
    /// virtualization), so x:Name is not a generated field — resolve it.</summary>
    private ScrollViewer? DopeSheetTrackScroller
    {
        get
        {
            if (DopeSheetTrackList is null) return null;
            if (DopeSheetTrackList.Template?.FindName("DopeSheetTrackScroller", DopeSheetTrackList)
                is ScrollViewer named) return named;
            return VisualTreeHelper.GetChildrenCount(DopeSheetTrackList) > 0
                ? VisualTreeHelper.GetChild(DopeSheetTrackList, 0) as ScrollViewer
                : null;
        }
    }

    /// <summary>The rows the dope sheet draws + hit-tests, in display order
    /// (body-part groups + bones from <see cref="PoseToEmoteViewModel.DopeDisplayTracks"/>).</summary>
    private List<TimelineTrackRow> GetDopeVisibleTracks()
    {
        if (_dopeVisibleTracksCache is not null) return _dopeVisibleTracksCache;
        if (_vm.DopeDisplayTracks.Count == 0 && _vm.TimelineTracks.Count > 0)
            RebuildDopeDisplayTracks();
        var visibleTracks = new List<TimelineTrackRow>(_vm.DopeDisplayTracks.Count);
        foreach (var t in _vm.DopeDisplayTracks)
            if (!t.IsFilteredOut) visibleTracks.Add(t);
        if (visibleTracks.Count == 0 && _vm.TimelineKeyframes.Count > 0)
        {
            var summary = new TimelineTrackRow
            {
                Id = "summary",
                Name = "Summary",
                DisplayName = "Summary",
            };
            foreach (var key in _vm.TimelineKeyframes) summary.Keys.Add(key);
            visibleTracks.Add(summary);
        }
        _dopeVisibleTracksCache = visibleTracks;
        return visibleTracks;
    }

    /// <summary>
    /// Rebuild the Dope Sheet gutter: Summary / Root Motion, then body-part
    /// groups (collapsed defaults for Fingers/Face/Accessory), hiding empty
    /// bone tracks unless ShowEmptyTracks is on.
    /// </summary>
    private void RebuildDopeDisplayTracks()
    {
        var filter = _vm.TimelineTrackFilter.Trim();
        var showEmpty = _vm.ShowEmptyTracks;
        var source = _vm.TimelineTracks.Where(t => !t.IsGroupHeader).ToList();

        foreach (var track in source)
        {
            track.HasKeys = track.Keys.Count > 0;
            if (string.IsNullOrEmpty(track.DisplayName) || track.DisplayName == track.Name
                || !TimelineTrackRow.IsSpecialTrack(track))
            {
                track.DisplayName = TimelineTrackRow.IsSpecialTrack(track)
                    ? track.Name
                    : TimelineTrackRow.FriendlyName(track.Name);
            }
            if (!TimelineTrackRow.IsSpecialTrack(track) && string.IsNullOrEmpty(track.Group))
            {
                var (group, _) = BoneGroupClassifier.Classify(track.Name);
                track.Group = group;
            }
            track.IsFilteredOut = false;
        }

        static bool MatchesFilter(TimelineTrackRow t, string f) =>
            f.Length == 0
            || t.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
            || (t.DisplayName?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
            || (t.Group?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);

        // Reference timeline (final form, 2026-07-17): a LAYER header row plus
        // one row per DRIVEN bone — every row rendering as a fat span bar (the
        // user's mock). No per-frame diamonds anywhere; manual keys live on the
        // Keyframes lane above (ease != linear only). Bone rows are synthesized
        // from the rig's drive-map, so stale clip-track duplicates can't appear.
        var display = new List<TimelineTrackRow>(64);
        if (_vm.TimelineKeyframes.Count > 0 || _vm.Strips.Count > 0)
        {
            const string LayerKey = "__layer";
            if (!_vm.DopeGroupExpanded.TryGetValue(LayerKey, out var layerOpen))
            {
                layerOpen = true;   // reference look: rows visible by default
                _vm.DopeGroupExpanded[LayerKey] = layerOpen;
            }
            var layerName = !string.IsNullOrWhiteSpace(_vm.PrimaryTrackLabel) ? _vm.PrimaryTrackLabel
                : !string.IsNullOrWhiteSpace(_uncalibratedName) ? _uncalibratedName
                : "Animation";
            display.Add(new TimelineTrackRow
            {
                Id = "layer:main",
                Name = layerName,
                DisplayName = layerName,
                Group = LayerKey,
                IsGroupHeader = true,
                IsExpanded = layerOpen,
                HasKeys = true,
                Depth = 0,
            });
            if (layerOpen)
            {
                foreach (var bone in _vm.Bones)
                {
                    if (bone is null || !bone.IsDriven) continue;
                    var friendly = TimelineTrackRow.FriendlyName(bone.Name);
                    if (filter.Length > 0
                        && !friendly.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !(bone.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                        continue;
                    display.Add(new TimelineTrackRow
                    {
                        Id = "bone:" + bone.Name,
                        Name = bone.Name ?? "",
                        DisplayName = friendly,
                        Group = LayerKey,
                        Depth = 1,
                        HasKeys = true,
                    });
                }
            }
        }

        _vm.DopeDisplayTracks.Clear();
        foreach (var row in display)
            _vm.DopeDisplayTracks.Add(row);

        InvalidateDopeVisibleTracks();
    }

    private void EnsureDopeSheetLayers()
    {
        if (_dopeSheetLayer is null && DopeSheetCanvas is not null)
        {
            _dopeSheetLayer = new Controls.TimelineRenderLayer { RenderCallback = RenderDopeSheetLayer };
            DopeSheetCanvas.Children.Add(_dopeSheetLayer);
        }
        if (_dopeRulerLayer is null && DopeSheetRulerCanvas is not null)
        {
            _dopeRulerLayer = new Controls.TimelineRenderLayer { RenderCallback = RenderDopeRulerLayer };
            DopeSheetRulerCanvas.Children.Add(_dopeRulerLayer);
        }
        if (_dopePlayheadLine is null && DopeSheetPlayheadCanvas is not null)
        {
            _dopePlayheadLine = new System.Windows.Shapes.Line
            {
                Stroke = TimelinePlayheadBrush,
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            };
            DopeSheetPlayheadCanvas.Children.Add(_dopePlayheadLine);
        }
    }

    /// <summary>Full dope-sheet refresh: one InvalidateVisual per render
    /// layer (a single batched DrawingContext pass each) + the playhead
    /// line move. No UIElement churn — the per-key Polygons this used to
    /// rebuild are gone; hit-testing is mathematical (HitTestDopeKey).</summary>
    private void DrawDopeSheet()
    {
        var canvas = DopeSheetCanvas;
        if (canvas is null || DopeSheetRulerCanvas is null || DopeSheetPlayheadCanvas is null) return;
        EnsureDopeSheetLayers();
        if (canvas.ActualWidth < 10) return;
        SyncTimelineControllerFromVm();
        _dopeSheetLayer?.InvalidateVisual();
        _dopeRulerLayer?.InvalidateVisual();
        UpdateDopePlayhead();
    }

    /// <summary>Playback/scrub hot path: reposition the cached playhead
    /// line only — the grid/key layers are untouched (the old code rebuilt
    /// all three canvases on every ~20 Hz tick).</summary>
    private void UpdateDopePlayhead()
    {
        var playheadCanvas = DopeSheetPlayheadCanvas;
        if (playheadCanvas is null) return;
        EnsureDopeSheetLayers();
        if (_dopePlayheadLine is null) return;
        SyncTimelineControllerFromVm();
        var x = DopeTimeToX(_vm.TimelineTime);
        _dopePlayheadLine.X1 = x;
        _dopePlayheadLine.X2 = x;
        _dopePlayheadLine.Y1 = 0;
        _dopePlayheadLine.Y2 = playheadCanvas.ActualHeight;
    }

    /// <summary>DrawingContext pass for the dope-sheet body: frame grid,
    /// row baselines, and every VISIBLE key diamond. Keys are culled
    /// horizontally (true-x, ±8 px margin) and vertically (row viewport),
    /// then deduped per pixel column so a fully-baked 30 fps clip zoomed
    /// out doesn't tessellate thousands of overlapping diamonds.</summary>
    private void RenderDopeSheetLayer(DrawingContext dc)
    {
        var canvas = DopeSheetCanvas;
        if (canvas is null || canvas.ActualWidth < 10) return;
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;

        var fps = System.Math.Max(1, _vm.TimelineFps);
        var visible = VisibleTimelineDuration;
        var first = (int)System.Math.Floor(_timelineCtl.ScrollOffset * fps);
        var last = (int)System.Math.Ceiling((_timelineCtl.ScrollOffset + visible) * fps);
        var pxPerFrame = _timelineCtl.UsableWidth(w) / (visible * fps);
        int majorEvery = NiceFrameStep(pxPerFrame, 48);

        var majorGrid = new StreamGeometry();
        var minorGrid = new StreamGeometry();
        using (var majorCtx = majorGrid.Open())
        using (var minorCtx = minorGrid.Open())
        {
            for (int frame = System.Math.Max(0, first); frame <= last; frame++)
            {
                bool major = frame % majorEvery == 0;
                if (!major && pxPerFrame < 6) continue;
                var x = DopeTimeToX(frame / (double)fps);
                var ctx = major ? majorCtx : minorCtx;
                ctx.BeginFigure(new Point(x, 0), false, false);
                ctx.LineTo(new Point(x, h), true, false);
            }
        }
        majorGrid.Freeze();
        minorGrid.Freeze();
        dc.DrawGeometry(null, DopeMinorGridPen, minorGrid);
        dc.DrawGeometry(null, DopeMajorGridPen, majorGrid);

        var visibleTracks = GetDopeVisibleTracks();
        var verticalOffset = DopeSheetTrackScroller?.VerticalOffset ?? 0;

        // Reference-style lanes (user's mock, 2026-07-17): dense keyed spans →
        // FAT alternating blue/orange bars with orange endpoint dots, no
        // baselines. Sparse hand-set keys → diamonds (still grabbable). One
        // bar per row ≈ 50 shapes total, so redraws stay instant.
        const double barH = 14;
        var normalKeys = new StreamGeometry();
        var selectedKeys = new StreamGeometry();
        var drawnColumns = new HashSet<int>();
        using (var normalCtx = normalKeys.Open())
        using (var selectedCtx = selectedKeys.Open())
        {
            for (int row = 0; row < visibleTracks.Count; row++)
            {
                var track = visibleTracks[row];
                var y = row * DopeRowHeight + DopeRowHeight / 2 - verticalOffset;
                if (y < -DopeRowHeight || y > h + DopeRowHeight) continue;

                if (track.IsGroupHeader || track.Keys.Count == 0)
                {
                    // Reference bars: every layer/bone row spans the animation's
                    // keyed range — header blue, bone rows alternating.
                    double lMin = double.MaxValue, lMax = double.MinValue;
                    if (_barPreviewStart >= 0)
                    {
                        // Mid-drag: all bars follow the previewed span.
                        lMin = DopeTimeToXRaw(_barPreviewStart);
                        lMax = DopeTimeToXRaw(_barPreviewEnd);
                    }
                    else foreach (var kf in _vm.TimelineKeyframes)
                    {
                        var x = DopeTimeToXRaw(kf.Time);
                        if (x < lMin) lMin = x;
                        if (x > lMax) lMax = x;
                    }
                    if (lMax > lMin)
                    {
                        var bx0 = System.Math.Max(-6, lMin);
                        var bx1 = System.Math.Min(w + 6, lMax);
                        if (bx1 > bx0)
                        {
                            var barBrush = track.IsGroupHeader ? DopeBarBlue
                                : (row & 1) == 0 ? DopeBarOrange : DopeBarBlue;
                            dc.DrawRoundedRectangle(barBrush, null,
                                new Rect(bx0, y - barH / 2, System.Math.Max(4, bx1 - bx0), barH), 3, 3);
                            if (lMin >= -8) dc.DrawEllipse(DopeDot, null, new Point(lMin, y), 3.2, 3.2);
                            if (lMax <= w + 8) dc.DrawEllipse(DopeDot, null, new Point(lMax, y), 3.2, 3.2);
                        }
                    }
                    continue;
                }

                double rMinX = double.MaxValue, rMaxX = double.MinValue;
                int rVis = 0;
                foreach (var key in track.Keys)
                {
                    var x = DopeTimeToXRaw(key.Time);
                    if (x < -10 || x > w + 10) continue;
                    rVis++;
                    if (x < rMinX) rMinX = x;
                    if (x > rMaxX) rMaxX = x;
                }
                bool rowDense = rVis > 2 && (rMaxX - rMinX) / System.Math.Max(1, rVis - 1) < 6;
                if (rowDense)
                {
                    var barBrush = (row & 1) == 0 ? DopeBarBlue : DopeBarOrange;
                    dc.DrawRoundedRectangle(barBrush, null,
                        new Rect(rMinX, y - barH / 2, System.Math.Max(4, rMaxX - rMinX), barH), 3, 3);
                    dc.DrawEllipse(DopeDot, null, new Point(rMinX, y), 3.2, 3.2);
                    dc.DrawEllipse(DopeDot, null, new Point(rMaxX, y), 3.2, 3.2);
                    // Selected keys still surface as white diamonds on the bar.
                    foreach (var key in track.Keys)
                    {
                        if (!key.IsSelected) continue;
                        var x = DopeTimeToXRaw(key.Time);
                        if (x < -8 || x > w + 8) continue;
                        selectedCtx.BeginFigure(new Point(x, y - 4.5), true, true);
                        selectedCtx.LineTo(new Point(x + 4.5, y), true, false);
                        selectedCtx.LineTo(new Point(x, y + 4.5), true, false);
                        selectedCtx.LineTo(new Point(x - 4.5, y), true, false);
                    }
                    continue;
                }

                drawnColumns.Clear();
                foreach (var key in track.Keys)
                {
                    var x = DopeTimeToXRaw(key.Time);
                    if (x < -8 || x > w + 8) continue;
                    var column = ((int)System.Math.Round(x) << 1) | (key.IsSelected ? 1 : 0);
                    if (!drawnColumns.Add(column)) continue;
                    var ctx = key.IsSelected ? selectedCtx : normalCtx;
                    ctx.BeginFigure(new Point(x, y - 4.5), true, true);
                    ctx.LineTo(new Point(x + 4.5, y), true, false);
                    ctx.LineTo(new Point(x, y + 4.5), true, false);
                    ctx.LineTo(new Point(x - 4.5, y), true, false);
                }
            }
        }
        normalKeys.Freeze();
        selectedKeys.Freeze();
        dc.DrawGeometry(TimelineKfFillBrush, TimelineKfStrokePen, normalKeys);
        dc.DrawGeometry(Brushes.White, TimelineKfSelectedPen, selectedKeys);
    }

    /// <summary>DrawingContext pass for the dope-sheet ruler: frame numbers
    /// at the major grid columns (same stride math as the body grid).</summary>
    private void RenderDopeRulerLayer(DrawingContext dc)
    {
        var canvas = DopeSheetCanvas;
        if (canvas is null || canvas.ActualWidth < 10) return;
        var fps = System.Math.Max(1, _vm.TimelineFps);
        var visible = VisibleTimelineDuration;
        var first = (int)System.Math.Floor(_timelineCtl.ScrollOffset * fps);
        var last = (int)System.Math.Ceiling((_timelineCtl.ScrollOffset + visible) * fps);
        var pxPerFrame = _timelineCtl.UsableWidth(canvas.ActualWidth) / (visible * fps);
        int majorEvery = NiceFrameStep(pxPerFrame, 48);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int frame = System.Math.Max(0, first); frame <= last; frame++)
        {
            if (frame % majorEvery != 0) continue;
            var x = DopeTimeToX(frame / (double)fps);
            var text = new FormattedText(frame.ToString(),
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, TimelineTypeface, 9, TimelineLabelBrush, dpi);
            dc.DrawText(text, new Point(x + 2, 2));
        }
    }

    private Point _dopePointerStart;
    private bool _dopePointerMoved;
    private System.Windows.Shapes.Rectangle? _dopeMarquee;
    private KeyframeMarker? _dopeDragKey;
    private Dictionary<string, double>? _dopeDragOriginalTimes;
    private KeyframeMarker? _dopeHoverKey;

    /// <summary>Mathematical key hit-test against the same layout the render
    /// layer draws (row = y band, key = nearest x within ±8 px). Replaces the
    /// per-diamond Polygon.Tag dispatch — there are no per-key UIElements
    /// anymore.</summary>
    private (TimelineTrackRow Track, KeyframeMarker Key)? HitTestDopeKey(Point p)
    {
        var canvas = DopeSheetCanvas;
        if (canvas is null || canvas.ActualWidth < 10) return null;
        SyncTimelineControllerFromVm();
        var visibleTracks = GetDopeVisibleTracks();
        var verticalOffset = DopeSheetTrackScroller?.VerticalOffset ?? 0;
        var row = (int)System.Math.Floor((p.Y + verticalOffset) / DopeRowHeight);
        if (row < 0 || row >= visibleTracks.Count) return null;
        var centerY = row * DopeRowHeight + DopeRowHeight / 2 - verticalOffset;
        if (System.Math.Abs(p.Y - centerY) > 8) return null;

        var track = visibleTracks[row];
        if (track.IsGroupHeader) return null;
        KeyframeMarker? best = null;
        double bestDistance = 8;
        foreach (var key in track.Keys)
        {
            var distance = System.Math.Abs(DopeTimeToXRaw(key.Time) - p.X);
            if (distance <= bestDistance) { bestDistance = distance; best = key; }
        }
        return best is null ? null : (track, best);
    }

    /// <summary>Keyed span (first..last keyframe, seconds) of the loaded
    /// animation, or null when fewer than 2 keys — the source of the lane
    /// bars, and the drag/trim hit target.</summary>
    private (double Start, double End)? DopeKeyframeSpan()
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var kf in _vm.TimelineKeyframes)
        {
            if (kf.Time < min) min = kf.Time;
            if (kf.Time > max) max = kf.Time;
        }
        return max > min ? (min, max) : null;
    }

    private void OnDopeSheetMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        canvas.Focus();
        var position = e.GetPosition(canvas);

        // A press on a key starts a key drag (with the old per-diamond
        // selection semantics); anywhere else starts the marquee.
        if (HitTestDopeKey(position) is { } hit && !hit.Track.IsLocked)
        {
            if (!System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
                foreach (var track in _vm.TimelineTracks) track.IsSelected = ReferenceEquals(track, hit.Track);
            else
                hit.Track.IsSelected = !hit.Track.IsSelected;
            var item = new TimelineItemRef(TimelineItemKind.Keyframe, hit.Key.Id);
            var mods = System.Windows.Input.Keyboard.Modifiers;
            if (mods.HasFlag(System.Windows.Input.ModifierKeys.Control)) _timelineSelection.Toggle(item);
            else if (mods.HasFlag(System.Windows.Input.ModifierKeys.Shift)) _timelineSelection.Add(item);
            else if (!_timelineSelection.Contains(item)) _timelineSelection.SelectOnly(item);
            _dopeDragKey = hit.Key;
            _dopePointerStart = position;
            _dopePointerMoved = false;
            _dopeDragOriginalTimes = _vm.TimelineTracks
                .SelectMany(t => t.Keys)
                .Where(k => _timelineSelection.Contains(new TimelineItemRef(TimelineItemKind.Keyframe, k.Id)))
                .ToDictionary(k => k.Id, k => k.Time);
            _timelineInteraction = TimelineInteractionState.DraggingKeys;
            canvas.CaptureMouse();
            e.Handled = true;
            return;
        }

        // A press on a lane BAR slides the whole animation in time; a press
        // on an endpoint dot trims that edge (2026-07-17 user request).
        SyncTimelineControllerFromVm();
        if (DopeKeyframeSpan() is { } span)
        {
            var vOff = DopeSheetTrackScroller?.VerticalOffset ?? 0;
            var rowIdx = (int)((position.Y + vOff) / DopeRowHeight);
            var rows = GetDopeVisibleTracks();
            if (rowIdx >= 0 && rowIdx < rows.Count &&
                (rows[rowIdx].IsGroupHeader || rows[rowIdx].Keys.Count == 0))
            {
                var x0 = DopeTimeToXRaw(span.Start);
                var x1 = DopeTimeToXRaw(span.End);
                var mode = BarDragMode.None;
                var trimEnd = false;
                if (System.Math.Abs(position.X - x0) <= 7) { mode = BarDragMode.Trim; }
                else if (System.Math.Abs(position.X - x1) <= 7) { mode = BarDragMode.Trim; trimEnd = true; }
                else if (position.X > x0 && position.X < x1) mode = BarDragMode.Move;
                if (mode != BarDragMode.None)
                {
                    // Clicking a bone-row bar highlights that bone in the
                    // viewport — same select path as its gutter row.
                    if (rows[rowIdx].Id.StartsWith("bone:", StringComparison.Ordinal) && _webViewReady)
                    {
                        var bone = _vm.Bones.FirstOrDefault(b =>
                            string.Equals(b.Name, rows[rowIdx].Name, StringComparison.Ordinal));
                        if (bone != null)
                        {
                            _vm.SelectedBone = bone;
                            if (bone.Index != _lastPushedBoneIndex)
                            {
                                _lastPushedBoneIndex = bone.Index;
                                _ = Viewport.CoreWebView2.ExecuteScriptAsync(
                                    $"window.selectPoseBone && window.selectPoseBone({bone.Index})");
                            }
                        }
                    }
                    _barDragMode = mode;
                    _barDragTrimEnd = trimEnd;
                    _barDragOrigStart = span.Start;
                    _barDragOrigEnd = span.End;
                    _barPreviewStart = span.Start;
                    _barPreviewEnd = span.End;
                    _dopePointerStart = position;
                    _dopePointerMoved = false;
                    _timelineInteraction = TimelineInteractionState.DraggingStrips;
                    canvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }
        }

        // Sweep any stale marquee first — redraws no longer clear the
        // canvas children, so a capture lost mid-drag must not leak one.
        if (_dopeMarquee is not null) canvas.Children.Remove(_dopeMarquee);
        _dopePointerStart = position;
        _dopePointerMoved = false;
        _timelineInteraction = TimelineInteractionState.Marquee;
        _dopeMarquee = new System.Windows.Shapes.Rectangle
        {
            Stroke = TimelinePlayheadBrush,
            StrokeThickness = 1,
            Fill = Argb(0x25, 0x4A, 0x9E, 0xFF),
            IsHitTestVisible = false,
        };
        canvas.Children.Add(_dopeMarquee);
        canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnDopeSheetMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Canvas canvas) return;

        if (_timelineInteraction == TimelineInteractionState.DraggingKeys)
        {
            if (_dopeDragKey is null || _dopeDragOriginalTimes is null ||
                e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            var dx = e.GetPosition(canvas).X - _dopePointerStart.X;
            if (!_dopePointerMoved && System.Math.Abs(dx) < 2) return;
            _dopePointerMoved = true;
            var dt = dx / System.Math.Max(1, _timelineCtl.UsableWidth(canvas.ActualWidth)) *
                     VisibleTimelineDuration;
            var anchor = _dopeDragOriginalTimes[_dopeDragKey.Id] + dt;
            if (_vm.TimelineSnapEnabled &&
                !System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
                anchor = _timelineCtl.SnapTime(anchor, _vm.TimelineFps);
            dt = anchor - _dopeDragOriginalTimes[_dopeDragKey.Id];
            foreach (var key in _vm.TimelineTracks.SelectMany(t => t.Keys))
                if (_dopeDragOriginalTimes.TryGetValue(key.Id, out var original))
                    key.Time = System.Math.Clamp(original + dt, 0, _vm.TimelineDuration);
            _dopeSheetLayer?.InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_timelineInteraction == TimelineInteractionState.DraggingStrips && _barDragMode != BarDragMode.None)
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            var dx = e.GetPosition(canvas).X - _dopePointerStart.X;
            if (!_dopePointerMoved && System.Math.Abs(dx) < 2) return;
            _dopePointerMoved = true;
            var dt = dx / System.Math.Max(1, _timelineCtl.UsableWidth(canvas.ActualWidth)) *
                     VisibleTimelineDuration;
            var fps = System.Math.Max(1, _vm.TimelineFps);
            var snap = _vm.TimelineSnapEnabled &&
                !System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);
            if (_barDragMode == BarDragMode.Move)
            {
                var start = _barDragOrigStart + dt;
                if (snap) start = _timelineCtl.SnapTime(start, fps);
                start = System.Math.Max(0, start);
                _barPreviewStart = start;
                _barPreviewEnd = start + (_barDragOrigEnd - _barDragOrigStart);
            }
            else if (!_barDragTrimEnd)
            {
                var s = _barDragOrigStart + dt;
                if (snap) s = _timelineCtl.SnapTime(s, fps);
                _barPreviewStart = System.Math.Clamp(s, 0, _barDragOrigEnd - 1.0 / fps);
            }
            else
            {
                var en = _barDragOrigEnd + dt;
                if (snap) en = _timelineCtl.SnapTime(en, fps);
                _barPreviewEnd = System.Math.Clamp(en, _barDragOrigStart + 1.0 / fps, _vm.TimelineDuration);
            }
            _dopeSheetLayer?.InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_timelineInteraction == TimelineInteractionState.Marquee && _dopeMarquee is not null)
        {
            var current = e.GetPosition(canvas);
            var rect = TimelineHitTesting.Normalize(_dopePointerStart, current);
            _dopePointerMoved |= rect.Width > 3 || rect.Height > 3;
            _dopeMarquee.Width = rect.Width;
            _dopeMarquee.Height = rect.Height;
            Canvas.SetLeft(_dopeMarquee, rect.Left);
            Canvas.SetTop(_dopeMarquee, rect.Top);
            return;
        }

        // Idle hover: keep the old per-diamond affordances (SizeWE cursor +
        // "bone · frame · ease" tooltip) via the canvas-level hit-test.
        if (_timelineInteraction == TimelineInteractionState.Idle)
        {
            var pos = e.GetPosition(canvas);
            var hit = HitTestDopeKey(pos);
            if (!ReferenceEquals(hit?.Key, _dopeHoverKey))
            {
                _dopeHoverKey = hit?.Key;
                if (hit is { } hover)
                {
                    var fps = System.Math.Max(1, _vm.TimelineFps);
                    canvas.ToolTip = $"{hover.Track.Name} · frame {System.Math.Round(hover.Key.Time * fps)} · {hover.Key.Ease}";
                    canvas.Cursor = hover.Track.IsLocked
                        ? System.Windows.Input.Cursors.Arrow
                        : System.Windows.Input.Cursors.SizeWE;
                }
                else
                {
                    canvas.ToolTip = null;
                    canvas.Cursor = null;
                }
            }
            // Bar affordances: SizeWE over an endpoint dot (trim), SizeAll
            // over the bar body (slide in time). Recomputed per move — the
            // hover-key change guard above can't see bar transitions.
            if (hit is null && DopeKeyframeSpan() is { } hoverSpan)
            {
                System.Windows.Input.Cursor? barCursor = null;
                var vOff = DopeSheetTrackScroller?.VerticalOffset ?? 0;
                var rowIdx = (int)((pos.Y + vOff) / DopeRowHeight);
                var rows = GetDopeVisibleTracks();
                if (rowIdx >= 0 && rowIdx < rows.Count &&
                    (rows[rowIdx].IsGroupHeader || rows[rowIdx].Keys.Count == 0))
                {
                    var x0 = DopeTimeToXRaw(hoverSpan.Start);
                    var x1 = DopeTimeToXRaw(hoverSpan.End);
                    if (System.Math.Abs(pos.X - x0) <= 7 || System.Math.Abs(pos.X - x1) <= 7)
                        barCursor = System.Windows.Input.Cursors.SizeWE;
                    else if (pos.X > x0 && pos.X < x1)
                        barCursor = System.Windows.Input.Cursors.SizeAll;
                }
                canvas.Cursor = barCursor;
            }
        }
    }

    private async void OnDopeSheetMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        canvas.ReleaseMouseCapture();

        if (_timelineInteraction == TimelineInteractionState.DraggingKeys)
        {
            if (_dopePointerMoved && _dopeDragOriginalTimes is not null)
            {
                var moves = _vm.TimelineTracks.SelectMany(t => t.Keys)
                    .Where(k => _dopeDragOriginalTimes.ContainsKey(k.Id))
                    .Select(k => new { id = k.Id, time = k.Time })
                    .ToArray();
                await SendTimelineCommandAsync("moveKeys", moves.Select(x => x.id), new { moves });
            }
            _dopeDragKey = null;
            _dopeDragOriginalTimes = null;
            _timelineInteraction = TimelineInteractionState.Idle;
            RedrawTimeline();
            e.Handled = true;
            return;
        }

        if (_timelineInteraction == TimelineInteractionState.DraggingStrips && _barDragMode != BarDragMode.None)
        {
            var mode = _barDragMode;
            _barDragMode = BarDragMode.None;
            _timelineInteraction = TimelineInteractionState.Idle;
            if (_dopePointerMoved)
            {
                // Preview span stays visible until the committed echo rebuilds
                // the markers (RebuildKeyframeMarkers clears it) — no blink
                // back to the pre-drag position.
                if (mode == BarDragMode.Move)
                {
                    var delta = _barPreviewStart - _barDragOrigStart;
                    if (System.Math.Abs(delta) > 1e-4)
                    {
                        await SendTimelineCommandAsync("shiftKeys", null, new { delta });
                        _ = SendTimelineCommandAsync("requestSnapshot");
                        _vm.StatusText = $"Animation moved to frame {System.Math.Round(_barPreviewStart * System.Math.Max(1, _vm.TimelineFps))}.";
                    }
                    else { _barPreviewStart = _barPreviewEnd = -1; RedrawTimeline(); }
                }
                else
                {
                    var fps = System.Math.Max(1, _vm.TimelineFps);
                    var s = System.Math.Max(0, _barPreviewStart);
                    var en = System.Math.Max(s + 1.0 / fps, _barPreviewEnd);
                    await SendTimelineCommandAsync("trimRange", null, new { start = s, end = en });
                    _ = SendTimelineCommandAsync("requestSnapshot");
                    _vm.StatusText = _barDragTrimEnd ? "Trimmed the tail." : "Trimmed the lead-in.";
                }
            }
            else
            {
                _barPreviewStart = _barPreviewEnd = -1;
                RedrawTimeline();
            }
            e.Handled = true;
            return;
        }

        var end = e.GetPosition(canvas);
        if (_dopeMarquee is not null) canvas.Children.Remove(_dopeMarquee);
        if (_dopePointerMoved)
        {
            // Marquee select via the same row/column math the renderer uses
            // (the old code walked canvas children — there are none now).
            var selection = TimelineHitTesting.Normalize(_dopePointerStart, end);
            var hits = new List<TimelineItemRef>();
            SyncTimelineControllerFromVm();
            var visibleTracks = GetDopeVisibleTracks();
            var verticalOffset = DopeSheetTrackScroller?.VerticalOffset ?? 0;
            for (int row = 0; row < visibleTracks.Count; row++)
            {
                var y = row * DopeRowHeight + DopeRowHeight / 2 - verticalOffset;
                if (y < -DopeRowHeight || y > canvas.ActualHeight + DopeRowHeight) continue;
                foreach (var key in visibleTracks[row].Keys)
                {
                    var x = DopeTimeToXRaw(key.Time);
                    if (selection.IntersectsWith(new Rect(x - 7, y - 7, 14, 14)))
                        hits.Add(new TimelineItemRef(TimelineItemKind.Keyframe, key.Id));
                }
            }
            if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
                foreach (var hit in hits) _timelineSelection.Toggle(hit);
            else
                _timelineSelection.Replace(hits);
        }
        else
        {
            _timelineSelection.Clear();
            await SetTimelineTimeAsync(DopeXToTime(end.X));
        }
        _timelineInteraction = TimelineInteractionState.Idle;
        _dopeMarquee = null;
        RedrawTimeline();
    }

    private void OnDopeSheetRightUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        if (HitTestDopeKey(e.GetPosition(canvas)) is not { } hit) return;
        var item = new TimelineItemRef(TimelineItemKind.Keyframe, hit.Key.Id);
        if (!_timelineSelection.Contains(item)) _timelineSelection.SelectOnly(item);
        var menu = new ContextMenu();
        foreach (var (id, label) in new[]
        {
            ("auto", "Auto"), ("linear", "Linear"), ("in", "Ease In"),
            ("out", "Ease Out"), ("hold", "Hold"),
        })
        {
            var option = new MenuItem { Header = label, IsChecked = hit.Key.Ease == id };
            option.Click += async (_, _) =>
                await SendTimelineCommandAsync("setEase",
                    _timelineSelection.Items.Select(x => x.Id), new { ease = id });
            menu.Items.Add(option);
        }
        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "Delete selected keys" };
        delete.Click += async (_, _) => await DeleteTimelineSelectionAsync();
        menu.Items.Add(delete);
        menu.PlacementTarget = canvas;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnDopeSheetTrackScroll(object sender, ScrollChangedEventArgs e)
    {
        // Vertical row scroll only — ignore no-op / horizontal noise, and
        // coalesce so a finger-flick doesn't queue dozens of full rebuilds.
        if (e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;
        ScheduleRedrawTimeline();
    }

    private void OnTimelineTrackFilterChanged(object sender, TextChangedEventArgs e)
    {
        RebuildDopeDisplayTracks();
        ScheduleRedrawTimeline();
    }

    private void OnShowEmptyTracksChanged(object sender, RoutedEventArgs e)
    {
        RebuildDopeDisplayTracks();
        ScheduleRedrawTimeline();
    }

    private void OnTimelineMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ContextMenu is null) return;
        fe.ContextMenu.PlacementTarget = fe;
        fe.ContextMenu.IsOpen = true;
    }

    private void OnAnimLibrarySettingsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ContextMenu is null) return;
        fe.ContextMenu.DataContext = _vm;
        fe.ContextMenu.PlacementTarget = fe;
        fe.ContextMenu.IsOpen = true;
    }

    /// <summary>Toolbar profile chip → account dropdown (refresh credits / test
    /// credits / open panel / sign out). Left-click opens the ContextMenu below
    /// the chip — same pattern as the library settings button.</summary>
    private void OnAccountMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ContextMenu is null) return;
        fe.ContextMenu.DataContext = _vm;
        fe.ContextMenu.PlacementTarget = fe;
        fe.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        fe.ContextMenu.IsOpen = true;
    }

    private void OnTimelineTrackExpandClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not TimelineTrackRow track) return;
        if (track.IsGroupHeader)
        {
            _vm.DopeGroupExpanded[track.Group] = track.IsExpanded;
            RebuildDopeDisplayTracks();
            ScheduleRedrawTimeline();
            return;
        }
        var descendants = _vm.TimelineTracks.Where(candidate => IsTimelineDescendant(candidate, track)).ToList();
        foreach (var descendant in descendants)
            descendant.IsFilteredOut = !track.IsExpanded;
        InvalidateDopeVisibleTracks();
        ScheduleRedrawTimeline();
    }

    private void OnTimelineTrackRowMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not TimelineTrackRow selected) return;
        if (selected.IsGroupHeader)
        {
            selected.IsExpanded = !selected.IsExpanded;
            _vm.DopeGroupExpanded[selected.Group] = selected.IsExpanded;
            RebuildDopeDisplayTracks();
            ScheduleRedrawTimeline();
            e.Handled = true;
            return;
        }
        // Timeline bone row → highlight that bone in the viewport (same select
        // path as the outliner rows). 2026-07-17 user request.
        if (selected.Id.StartsWith("bone:", StringComparison.Ordinal)
            && _webViewReady && _suppressSelectionEcho == 0)
        {
            var bone = _vm.Bones.FirstOrDefault(b => string.Equals(b.Name, selected.Name, StringComparison.Ordinal));
            if (bone != null)
            {
                _vm.SelectedBone = bone;
                if (bone.Index != _lastPushedBoneIndex)
                {
                    _lastPushedBoneIndex = bone.Index;
                    _ = Viewport.CoreWebView2.ExecuteScriptAsync(
                        $"window.selectPoseBone && window.selectPoseBone({bone.Index})");
                }
            }
        }
        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) ||
            modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
            selected.IsSelected = !selected.IsSelected;
        else
        {
            foreach (var track in _vm.TimelineTracks)
                track.IsSelected = ReferenceEquals(track, selected);
            foreach (var track in _vm.DopeDisplayTracks)
                if (!track.IsGroupHeader)
                    track.IsSelected = ReferenceEquals(track, selected);
        }
        e.Handled = true;
    }

    private bool IsTimelineDescendant(TimelineTrackRow candidate, TimelineTrackRow ancestor)
    {
        var parent = candidate.ParentId;
        var guard = 0;
        while (parent.Length > 0 && guard++ < 64)
        {
            if (parent == ancestor.Id || parent == ancestor.Name) return true;
            var parentTrack = _vm.TimelineTracks.FirstOrDefault(
                x => x.Id == parent || x.Name == parent);
            if (parentTrack is null) break;
            parent = parentTrack.ParentId;
        }
        return false;
    }

    private async void OnTimelineTrackStateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not TimelineTrackRow track) return;
        if (track.IsGroupHeader) return;
        await SendTimelineCommandAsync("setTrackState", new[] { track.Id },
            new { locked = track.IsLocked, muted = track.IsMuted });
    }

    private void OnTimelineFitAll(object sender, RoutedEventArgs e)
    {
        _vm.TimelineZoom = 1;
        _vm.TimelineScrollOffset = 0;
        RedrawTimeline();
        _vm.StatusText = "Timeline fitted to full duration.";
    }

    private void OnTimelineFrameSelected(object sender, RoutedEventArgs e)
    {
        var times = _vm.TimelineTracks.SelectMany(t => t.Keys)
            .Where(k => _timelineSelection.Contains(
                new TimelineItemRef(TimelineItemKind.Keyframe, k.Id)))
            .Select(k => k.Time)
            .ToList();
        if (times.Count == 0)
        {
            _vm.StatusText = "Select one or more keys in the Dope Sheet, then Frame.";
            return;
        }
        var min = times.Min();
        var max = times.Max();
        var span = System.Math.Max(1.0 / System.Math.Max(1, _vm.TimelineFps), max - min);
        _vm.TimelineZoom = System.Math.Clamp(
            _vm.TimelineDuration / (span * 1.4), TimelineController.MinZoom, TimelineController.MaxZoom);
        var visible = _vm.TimelineDuration / _vm.TimelineZoom;
        _vm.TimelineScrollOffset = System.Math.Clamp(
            (min + max) / 2 - visible / 2, 0, System.Math.Max(0, _vm.TimelineDuration - visible));
        RedrawTimeline();
        _vm.StatusText = $"Framed {times.Count} selected key(s).";
    }

    private void DrawTimelinePlayhead()
    {
        var canvas = TimelinePlayheadCanvas;
        if (canvas is null) return;
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 10 || h < 5) return;
        var x = TimeToTimelineX(_vm.TimelineTime);
        var frame = _vm.TimelineCurrentFrame;

        if (_playheadLine is null)
        {
            _playheadLine = new System.Windows.Shapes.Line
            {
                Stroke = TimelinePlayheadBrush,
                StrokeThickness = 1.5,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false,
            };
            canvas.Children.Add(_playheadLine);
        }
        if (_playheadBadge is null)
        {
            _playheadFrameLabel = new TextBlock
            {
                FontFamily = TimelineFontFamily,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            _playheadBadge = new Border
            {
                Background = TimelinePlayheadBadgeBrush,
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 1, 4, 1),
                Child = _playheadFrameLabel,
                IsHitTestVisible = false,
            };
            canvas.Children.Add(_playheadBadge);
        }

        _playheadLine.X1 = x;
        _playheadLine.X2 = x;
        _playheadLine.Y1 = 0;
        _playheadLine.Y2 = h;
        if (_playheadFrameLabel is not null)
            _playheadFrameLabel.Text = frame.ToString();
        if (_playheadBadge is not null)
        {
            _playheadBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var badgeW = System.Math.Max(18, _playheadBadge.DesiredSize.Width);
            Canvas.SetLeft(_playheadBadge, x - badgeW / 2);
            Canvas.SetTop(_playheadBadge, 1);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Seek interaction — click or drag anywhere on the ruler / track.
    // ────────────────────────────────────────────────────────────────

    private bool _timelineScrubbing;
    private Canvas? _timelineScrubCanvas;
    private Point _sequencerPointerStart;
    private bool _sequencerMarqueeMoved;
    private System.Windows.Shapes.Rectangle? _sequencerMarquee;

    private void OnTimelineSeekClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        canvas.Focus();
        // Summary-keyframe lane: a press on a diamond starts a key drag
        // (the diamonds are rendered geometry now, not Polygons with their
        // own handlers). A press on a LOCKED diamond eats the click like
        // the old per-element handler did — no scrub.
        if (ReferenceEquals(canvas, TimelineTrackCanvas) &&
            HitTestSummaryKf(e.GetPosition(canvas)) is { } summaryKf)
        {
            if (_vm.TimelineKeyframeTrackLocked) { e.Handled = true; return; }
            _draggingKf = summaryKf;
            _kfDragStartTime = summaryKf.Time;
            _kfDragStartPx = e.GetPosition(TimelineTrackCanvas);
            _kfDragMoved = false;
            canvas.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (ReferenceEquals(canvas, TimelineStripCanvas))
        {
            _sequencerPointerStart = e.GetPosition(canvas);
            _sequencerMarqueeMoved = false;
            _timelineInteraction = TimelineInteractionState.Marquee;
            _sequencerMarquee = new System.Windows.Shapes.Rectangle
            {
                Stroke = TimelinePlayheadBrush,
                StrokeThickness = 1,
                Fill = Argb(0x25, 0x4A, 0x9E, 0xFF),
                IsHitTestVisible = false,
            };
            canvas.Children.Add(_sequencerMarquee);
            canvas.CaptureMouse();
            e.Handled = true;
            return;
        }
        _timelineScrubbing = true;
        _timelineInteraction = TimelineInteractionState.Scrubbing;
        _timelineScrubCanvas = canvas;
        SeekFromMouse(e);
        canvas.CaptureMouse();
        e.Handled = true;
    }

    private KeyframeMarker? _summaryHoverKf;

    private void OnTimelineSeekMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Live summary-keyframe drag (canvas-captured).
        if (_draggingKf is not null && ReferenceEquals(sender, TimelineTrackCanvas))
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            var cur = e.GetPosition(TimelineTrackCanvas);
            var dx = cur.X - _kfDragStartPx.X;
            if (!_kfDragMoved && System.Math.Abs(dx) < 2) return;
            _kfDragMoved = true;
            var dt = (dx / TimelineUsableWidth) * VisibleTimelineDuration;
            var newTime = System.Math.Max(0, System.Math.Min(_vm.TimelineDuration, _kfDragStartTime + dt));
            newTime = _timelineCtl.SnapTime(newTime, _vm.TimelineFps);
            // Hold Alt for fine adjustment (bypass FPS snap).
            if (!System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt))
            {
                var step = 1.0 / System.Math.Max(1, _vm.TimelineFps);
                newTime = System.Math.Round(newTime / step) * step;
            }
            _draggingKf.Time = System.Math.Round(newTime, 3);
            // Redraw the lane live for tactile feedback before the JS
            // round-trip lands (replaces the old Canvas.SetLeft on the shape).
            _seqKfLaneLayer?.InvalidateVisual();
            e.Handled = true;
            return;
        }

        // Idle hover over the summary lane: tooltip + resize cursor, same
        // affordances the per-diamond elements used to carry.
        if (ReferenceEquals(sender, TimelineTrackCanvas) &&
            _timelineInteraction == TimelineInteractionState.Idle &&
            !_timelineScrubbing && sender is Canvas laneCanvas)
        {
            var hover = HitTestSummaryKf(e.GetPosition(laneCanvas));
            if (!ReferenceEquals(hover, _summaryHoverKf))
            {
                _summaryHoverKf = hover;
                laneCanvas.ToolTip = hover is null
                    ? null
                    : $"Keyframe at {hover.Time:F3}s — drag to retime, right-click for options";
                laneCanvas.Cursor = hover is null ? null : System.Windows.Input.Cursors.SizeWE;
            }
        }

        if (_timelineInteraction == TimelineInteractionState.Marquee &&
            ReferenceEquals(sender, TimelineStripCanvas) &&
            _sequencerMarquee is not null)
        {
            var current = e.GetPosition(TimelineStripCanvas);
            var rect = TimelineHitTesting.Normalize(_sequencerPointerStart, current);
            _sequencerMarqueeMoved |= rect.Width > 3 || rect.Height > 3;
            _sequencerMarquee.Width = rect.Width;
            _sequencerMarquee.Height = rect.Height;
            Canvas.SetLeft(_sequencerMarquee, rect.Left);
            Canvas.SetTop(_sequencerMarquee, rect.Top);
            e.Handled = true;
            return;
        }
        if (!_timelineScrubbing) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { _timelineScrubbing = false; return; }
        SeekFromMouse(e);
    }

    private async void OnTimelineSeekUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Summary-keyframe drag commit.
        if (_draggingKf is not null && ReferenceEquals(sender, TimelineTrackCanvas))
        {
            (sender as Canvas)?.ReleaseMouseCapture();
            var marker = _draggingKf;
            _draggingKf = null;
            e.Handled = true;
            if (!_kfDragMoved) return;
            var fromArg = _kfDragStartTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var toArg = marker.Time.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            if (_webViewReady)
            {
                await Viewport.CoreWebView2.ExecuteScriptAsync(
                    $"window.poseMoveKeyframe && window.poseMoveKeyframe({fromArg}, {toArg})");
            }
            return;
        }
        if (sender is Canvas canvas)
        {
            canvas.ReleaseMouseCapture();
            if (ReferenceEquals(canvas, TimelineStripCanvas) &&
                _timelineInteraction == TimelineInteractionState.Marquee)
            {
                var end = e.GetPosition(canvas);
                if (_sequencerMarqueeMoved)
                {
                    var rect = TimelineHitTesting.Normalize(_sequencerPointerStart, end);
                    var hits = _vm.Strips
                        .Where(s =>
                        {
                            var left = TimeToTimelineX(s.Start);
                            var right = TimeToTimelineX(s.Start + s.Duration);
                            return rect.IntersectsWith(new Rect(left, 4, System.Math.Max(24, right - left), 26));
                        })
                        .Select(s => new TimelineItemRef(TimelineItemKind.Strip, s.StableId));
                    _timelineSelection.Replace(hits);
                }
                else
                {
                    _timelineSelection.Clear();
                    await SetTimelineTimeAsync(TimelineXToTime(end.X));
                }
                _sequencerMarquee = null;
                _timelineInteraction = TimelineInteractionState.Idle;
                RedrawStrips();
                e.Handled = true;
                return;
            }
        }
        _timelineScrubbing = false;
        _timelineInteraction = TimelineInteractionState.Idle;
        _timelineScrubCanvas = null;
        // Flush a non-scrub poseSetTime so onion skin catches the final frame.
        if (_webViewReady && !_scrubScriptInFlight)
        {
            var finalArg = _vm.TimelineTime.ToString(
                "0.###", System.Globalization.CultureInfo.InvariantCulture);
            _ = Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.poseSetTime && window.poseSetTime({finalArg}, false)");
        }
    }

    private async void SeekFromMouse(System.Windows.Input.MouseEventArgs e)
    {
        var canvas = _timelineScrubCanvas ?? TimelineStripCanvas ?? TimelineRulerCanvas
            ?? DopeSheetRulerCanvas ?? DopeSheetCanvas;
        if (canvas is null) return;
        var pos = e.GetPosition(canvas);
        // Dope-sheet ruler uses the dope time mapping; sequencer uses the
        // shared lane mapping. Pick by which canvas owns the scrub.
        var t = ReferenceEquals(canvas, DopeSheetRulerCanvas) || ReferenceEquals(canvas, DopeSheetCanvas)
            ? DopeXToTime(pos.X)
            : TimelineXToTime(pos.X);
        t = System.Math.Round(t, 3);
        _vm.TimelineTime = t;
        // Playhead moves immediately; viewer catches up via latest-wins queue.
        // Unified layers timeline: both sections track the scrub.
        DrawTimelinePlayhead();
        UpdateDopePlayhead();
        if (!_webViewReady) return;
        _pendingScrubTime = t;
        if (_scrubScriptInFlight) return;
        _scrubScriptInFlight = true;
        try
        {
            while (_pendingScrubTime is double pending)
            {
                _pendingScrubTime = null;
                var tArg = pending.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                // Second arg true = scrub: viewer skips onion skin + throttles host ticks.
                await Viewport.CoreWebView2.ExecuteScriptAsync(
                    $"window.poseSetTime && window.poseSetTime({tArg}, true)");
            }
        }
        finally
        {
            _scrubScriptInFlight = false;
            // Final non-scrub seek so onion skin / host tick land on the
            // resting frame after the mouse settles.
            if (!_timelineScrubbing && _webViewReady)
            {
                var finalArg = _vm.TimelineTime.ToString(
                    "0.###", System.Globalization.CultureInfo.InvariantCulture);
                _ = Viewport.CoreWebView2.ExecuteScriptAsync(
                    $"window.poseSetTime && window.poseSetTime({finalArg}, false)");
            }
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Keyframe drag — same delta-px math as before, driven by the lane
    // canvas + HitTestSummaryKf (the per-diamond shapes are gone).
    // Down/move/up live inside OnTimelineSeekClick/Move/Up.
    // ────────────────────────────────────────────────────────────────

    private KeyframeMarker? _draggingKf;
    private double _kfDragStartTime;
    private System.Windows.Point _kfDragStartPx;
    private bool _kfDragMoved;

    private void OnTimelineTrackRightUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        if (HitTestSummaryKf(e.GetPosition(canvas)) is not { } marker) return;
        var menu = new ContextMenu();
        var del = new MenuItem { Header = $"Delete keyframe ({marker.Time:F3}s)" };
        del.Click += async (_, __) =>
        {
            if (!_webViewReady) return;
            var t = marker.Time.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.poseDeleteKeyframeAtTime && window.poseDeleteKeyframeAtTime({t})");
        };
        var seekTo = new MenuItem { Header = $"Jump playhead to {marker.Time:F3}s" };
        seekTo.Click += async (_, __) =>
        {
            if (!_webViewReady) return;
            var t = marker.Time.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            await Viewport.CoreWebView2.ExecuteScriptAsync($"window.poseSetTime && window.poseSetTime({t})");
        };
        var capHere = new MenuItem { Header = "Capture current pose as new keyframe at scrubber" };
        capHere.Click += async (_, __) =>
        {
            if (!_webViewReady) return;
            await Viewport.CoreWebView2.ExecuteScriptAsync("window.poseAddKeyframe && window.poseAddKeyframe()");
        };

        // Easing submenu — controls the SQUAD/slerp curve out of this key.
        // 'Auto' (smoothstep) is the default soft natural feel;
        // 'Linear' opts out of cubic smoothing entirely; In/Out shape the
        // ease asymmetrically; Hold snaps to the next key with no
        // interpolation, useful for blocking out cuts.
        var ease = new MenuItem { Header = "Easing" };
        async void SetEase(string mode)
        {
            if (!_webViewReady) return;
            var t = marker.Time.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.poseSetKeyframeEase && window.poseSetKeyframeEase({t}, '{mode}')");
        }
        var easeAuto   = new MenuItem { Header = "Auto smooth (default)" }; easeAuto.Click   += (_, __) => SetEase("auto");
        var easeLin    = new MenuItem { Header = "Linear" };               easeLin.Click    += (_, __) => SetEase("linear");
        var easeIn     = new MenuItem { Header = "Ease in" };              easeIn.Click     += (_, __) => SetEase("in");
        var easeOut    = new MenuItem { Header = "Ease out" };             easeOut.Click    += (_, __) => SetEase("out");
        var easeHold   = new MenuItem { Header = "Hold (no interp)" };     easeHold.Click   += (_, __) => SetEase("hold");
        ease.Items.Add(easeAuto);
        ease.Items.Add(easeLin);
        ease.Items.Add(easeIn);
        ease.Items.Add(easeOut);
        ease.Items.Add(new Separator());
        ease.Items.Add(easeHold);

        menu.Items.Add(seekTo);
        menu.Items.Add(capHere);
        menu.Items.Add(ease);
        menu.Items.Add(new Separator());
        menu.Items.Add(del);
        menu.PlacementTarget = canvas;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ────────────────────────────────────────────────────────────────
    // Strip drag — drag the body of a clip-strip horizontally to retime
    // its start. Same delta-px math as keyframe drag, but operates on
    // Start (not Time) and moves the whole rectangle live.
    // ────────────────────────────────────────────────────────────────

    private TimelineStrip? _draggingStrip;
    private double _stripDragStartStart;
    private System.Windows.Point _stripDragStartPx;
    private bool _stripDragMoved;
    private Dictionary<int, double>? _stripDragOriginalStarts;

    private void OnStripMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not TimelineStrip strip) return;
        TimelineStripCanvas?.Focus();
        var item = new TimelineItemRef(TimelineItemKind.Strip, strip.StableId);
        var mods = System.Windows.Input.Keyboard.Modifiers;
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Control)) _timelineSelection.Toggle(item);
        else if (mods.HasFlag(System.Windows.Input.ModifierKeys.Shift)) _timelineSelection.Add(item);
        else if (!_timelineSelection.Contains(item)) _timelineSelection.SelectOnly(item);

        if (e.ClickCount == 2)
        {
            _vm.TimelineMode = TimelineEditorMode.DopeSheet;
            _ = SendTimelineCommandAsync("openClip", new[] { strip.StableId });
            ScheduleRedrawTimeline();
            e.Handled = true;
            return;
        }
        if (_vm.TimelineClipTrackLocked) { e.Handled = true; return; }
        _draggingStrip = strip;
        _stripDragStartStart = strip.Start;
        _stripDragStartPx = e.GetPosition(TimelineStripCanvas);
        _stripDragMoved = false;
        _stripDragOriginalStarts = _vm.Strips
            .Where(s => _timelineSelection.Contains(new TimelineItemRef(TimelineItemKind.Strip, s.StableId)))
            .ToDictionary(s => s.Id, s => s.Start);
        _timelineInteraction = TimelineInteractionState.DraggingStrips;
        fe.CaptureMouse();
        RedrawStrips();
        e.Handled = true;
    }

    private void OnStripMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggingStrip is null || _stripDragOriginalStarts is null) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        if (sender is not FrameworkElement fe) return;
        var cur = e.GetPosition(TimelineStripCanvas);
        var dx = cur.X - _stripDragStartPx.X;
        if (!_stripDragMoved && System.Math.Abs(dx) < 2) return;
        _stripDragMoved = true;
        var usable = TimelineUsableWidth;
        var dt = (dx / usable) * VisibleTimelineDuration;
        // Clamp left edge to 0; right edge can extend past the timeline
        // duration (poseAddStrip already grew duration on add, but
        // dragging late is allowed — the user can grow duration in the
        // numbox).
        var newStart = System.Math.Max(0, _stripDragStartStart + dt);
        if (_vm.TimelineSnapEnabled &&
            !System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
        {
            var step = 1.0 / System.Math.Max(1, _vm.TimelineFps);
            newStart = System.Math.Round(newStart / step) * step;
        }
        dt = newStart - _stripDragStartStart;
        foreach (var selected in _vm.Strips)
            if (_stripDragOriginalStarts.TryGetValue(selected.Id, out var original))
                selected.Start = System.Math.Round(System.Math.Max(0, original + dt), 3);
        RedrawStrips();
        e.Handled = true;
    }

    private async void OnStripMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_draggingStrip is null) return;
        if (sender is FrameworkElement fe) fe.ReleaseMouseCapture();
        var strip = _draggingStrip;
        _draggingStrip = null;
        var moved = _stripDragOriginalStarts;
        _stripDragOriginalStarts = null;
        _timelineInteraction = TimelineInteractionState.Idle;
        e.Handled = true;
        if (!_stripDragMoved || moved is null) return;
        var moves = _vm.Strips.Where(s => moved.ContainsKey(s.Id))
            .Select(s => new { id = s.StableId, start = s.Start }).ToArray();
        await SendTimelineCommandAsync("moveStrips", moves.Select(x => x.id), new { moves });
    }

    private void OnStripRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not TimelineStrip strip) return;
        var selected = new TimelineItemRef(TimelineItemKind.Strip, strip.StableId);
        if (!_timelineSelection.Contains(selected)) _timelineSelection.SelectOnly(selected);
        var menu = new ContextMenu();
        var jumpToStart = new MenuItem { Header = $"Jump playhead to strip start ({strip.Start:F2}s)" };
        jumpToStart.Click += async (_, __) =>
        {
            if (!_webViewReady) return;
            var t = strip.Start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            await Viewport.CoreWebView2.ExecuteScriptAsync($"window.poseSetTime && window.poseSetTime({t})");
        };
        menu.Items.Add(jumpToStart);
        menu.Items.Add(new Separator());

        var locked = _vm.TimelineClipTrackLocked;
        var setIn = new MenuItem { Header = "Set In Point (trim start to playhead)", IsEnabled = !locked };
        setIn.Click += async (_, __) =>
        {
            if (!_webViewReady) return;
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.poseSetStripIn && window.poseSetStripIn({strip.Id})");
            _vm.StatusText = $"Trimmed start of '{strip.ClipName}' to playhead.";
        };
        var setOut = new MenuItem { Header = "Set Out Point (trim end to playhead)", IsEnabled = !locked };
        setOut.Click += async (_, __) =>
        {
            if (!_webViewReady) return;
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.poseSetStripOut && window.poseSetStripOut({strip.Id})");
            _vm.StatusText = $"Trimmed end of '{strip.ClipName}' to playhead.";
        };
        var split = new MenuItem { Header = "Split at playhead", IsEnabled = !locked };
        split.Click += async (_, __) =>
        {
            if (!_webViewReady) return;
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.poseSplitStrip && window.poseSplitStrip({strip.Id})");
            _vm.StatusText = $"Split '{strip.ClipName}' at playhead.";
        };
        menu.Items.Add(setIn);
        menu.Items.Add(setOut);
        menu.Items.Add(split);
        menu.Items.Add(new Separator());

        // Fade presets. Capped at the strip's own duration since the
        // engine clamps anyway; presets exist only up to half the
        // strip so the two ramps can both be set without one eating
        // the other entirely.
        var fadePresets = new[] { 0.0, 0.1, 0.25, 0.5, 1.0, 2.0 };
        var fadeMax = strip.Duration / 2.0;
        var fadeInMenu = new MenuItem { Header = $"Fade in ({strip.FadeIn:F2}s)" };
        foreach (var v in fadePresets)
        {
            if (v > fadeMax && v > 0) continue;
            var item = new MenuItem
            {
                Header = v == 0 ? "Off" : $"{v:F2}s",
                IsChecked = System.Math.Abs(strip.FadeIn - v) < 0.005,
                IsCheckable = false,
            };
            var captured = v;
            item.Click += async (_, __) =>
            {
                if (!_webViewReady) return;
                var arg = captured.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                await Viewport.CoreWebView2.ExecuteScriptAsync(
                    $"window.poseSetStripFade && window.poseSetStripFade({strip.Id}, {arg}, -1)");
            };
            fadeInMenu.Items.Add(item);
        }
        var fadeOutMenu = new MenuItem { Header = $"Fade out ({strip.FadeOut:F2}s)" };
        foreach (var v in fadePresets)
        {
            if (v > fadeMax && v > 0) continue;
            var item = new MenuItem
            {
                Header = v == 0 ? "Off" : $"{v:F2}s",
                IsChecked = System.Math.Abs(strip.FadeOut - v) < 0.005,
                IsCheckable = false,
            };
            var captured = v;
            item.Click += async (_, __) =>
            {
                if (!_webViewReady) return;
                var arg = captured.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                await Viewport.CoreWebView2.ExecuteScriptAsync(
                    $"window.poseSetStripFade && window.poseSetStripFade({strip.Id}, -1, {arg})");
            };
            fadeOutMenu.Items.Add(item);
        }
        menu.Items.Add(fadeInMenu);
        menu.Items.Add(fadeOutMenu);
        var easeModes = new[] { ("linear", "Linear"), ("in", "Ease in"), ("out", "Ease out"), ("easeInOut", "Ease in-out") };
        var easeInModeMenu = new MenuItem { Header = "Fade in curve" };
        foreach (var (mode, label) in easeModes)
        {
            var item = new MenuItem { Header = label, IsCheckable = false, IsChecked = strip.FadeInEase == mode };
            var captured = mode;
            item.Click += async (_, __) =>
            {
                if (!_webViewReady) return;
                await Viewport.CoreWebView2.ExecuteScriptAsync(
                    $"window.poseSetStripFadeEase && window.poseSetStripFadeEase({strip.Id}, '{captured}', null)");
            };
            easeInModeMenu.Items.Add(item);
        }
        var easeOutModeMenu = new MenuItem { Header = "Fade out curve" };
        foreach (var (mode, label) in easeModes)
        {
            var item = new MenuItem { Header = label, IsCheckable = false, IsChecked = strip.FadeOutEase == mode };
            var captured = mode;
            item.Click += async (_, __) =>
            {
                if (!_webViewReady) return;
                await Viewport.CoreWebView2.ExecuteScriptAsync(
                    $"window.poseSetStripFadeEase && window.poseSetStripFadeEase({strip.Id}, null, '{captured}')");
            };
            easeOutModeMenu.Items.Add(item);
        }
        menu.Items.Add(easeInModeMenu);
        menu.Items.Add(easeOutModeMenu);
        menu.Items.Add(new Separator());

        var copy = new MenuItem { Header = "Copy selected clips    Ctrl+C" };
        copy.Click += async (_, __) => await CopyTimelineSelectionAsync();
        var duplicate = new MenuItem { Header = "Duplicate selected clips    Ctrl+D", IsEnabled = !locked };
        duplicate.Click += async (_, __) => await DuplicateTimelineSelectionAsync();
        var remove = new MenuItem { Header = "Delete selected clips    Delete", IsEnabled = !locked };
        remove.Click += async (_, __) => await DeleteTimelineSelectionAsync();
        menu.Items.Add(copy);
        menu.Items.Add(duplicate);
        menu.Items.Add(remove);
        fe.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Place a library clip on the timeline at the current
    /// playhead position. The id comes from the button's Tag, set by
    /// the ListBox.ItemTemplate in the Outliner Clips section.</summary>
    private async void OnAddClipToTimelineClick(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        if (sender is not FrameworkElement fe || fe.Tag is not int clipId) return;
        try
        {
            await Viewport.CoreWebView2.ExecuteScriptAsync($"window.poseAddStrip({clipId})");
            _vm.StatusText = "Added clip to timeline.";
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Add strip failed: {ex.Message}";
        }
    }

    private void UpdatePlayButtonVisual()
    {
        if (PlayIcon != null)
            PlayIcon.Symbol = _vm.TimelinePlaying
                ? Wpf.Ui.Controls.SymbolRegular.Pause24
                : Wpf.Ui.Controls.SymbolRegular.Play24;
    }

    /// <summary>The last bone index we pushed to (or accepted from) the
    /// JS viewer. Used to de-dupe ListBox.SelectionChanged events so a
    /// CollectionView refresh mid-drag doesn't re-issue the same
    /// selectPoseBone call and stomp the gizmo's drag state.</summary>
    private int _lastPushedBoneIndex = -1;

    /// <summary>Refcount of in-flight "we are updating SelectedBone in
    /// response to a viewer message; ignore any SelectionChanged that
    /// fires synchronously" scopes. Bumped + reset around the
    /// pose-bone-selected handler. Refcount (not bool) so nested
    /// updates can't accidentally clear the flag mid-update.</summary>
    private int _suppressSelectionEcho = 0;

    /// <summary>Outliner row click. Sets <see cref="PoseToEmoteViewModel.SelectedBone"/>
    /// (which fans out to IsSelected + group auto-expand via the
    /// PropertyChanged subscription in the constructor) and pushes the
    /// selection down to the viewer so the gizmo attaches.</summary>
    private async void OnBoneRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not PoseBoneEntry bone) return;
        // _suppressSelectionEcho is still bumped by the viewer-driven
        // pose-bone-selected path; honor it here too so a viewer ping
        // can't bounce right back as a user-initiated selection.
        if (_suppressSelectionEcho > 0) return;
        _vm.SelectedBone = bone;
        if (!_webViewReady) return;
        var idx = bone.Index;
        if (idx == _lastPushedBoneIndex) return;
        _lastPushedBoneIndex = idx;
        await Viewport.CoreWebView2.ExecuteScriptAsync($"window.selectPoseBone && window.selectPoseBone({idx})");
    }

    /// <summary>Outliner search filter. Mirrors Blender's outliner
    /// search box but fuzzy: typing "spineRoot" still surfaces
    /// "SK_Spine_Root". Direct substring hits always win; otherwise we
    /// fall back to FuzzySharp's partial-ratio score so transposed
    /// characters, missing underscores, and dropped prefixes still
    /// match. Flips PoseBoneEntry.IsHidden + PoseBoneGroup.IsFilteredOut
    /// so the tree collapses non-matches instead of just dimming them.
    /// Empty filter resets every flag back to false. Auto-expands any
    /// group that has visible children so a match deep in a collapsed
    /// region surfaces immediately.</summary>
    private void OnOutlinerFilterChanged(object sender, TextChangedEventArgs e)
    {
        var raw = _vm.OutlinerFilter ?? "";
        var needle = raw.Trim().ToLowerInvariant();
        bool hasFilter = needle.Length > 0;
        // Tuned by trial: 70 keeps "spineRoot"→"SK_Spine_Root" but
        // rejects a 3-letter needle accidentally fuzzy-hitting every
        // bone in the rig. Skip fuzzy scoring for needles ≤2 chars
        // (too short to be meaningful — fall back to substring only).
        const int kFuzzyCutoff = 70;
        bool useFuzzy = needle.Length >= 3;
        foreach (var grp in _vm.BoneGroups)
        {
            int visibleCount = 0;
            foreach (var bone in grp.Bones)
            {
                bool match;
                if (!hasFilter)
                {
                    match = true;
                }
                else
                {
                    var hay = bone.Name.ToLowerInvariant();
                    match = hay.Contains(needle);
                    if (!match && useFuzzy)
                    {
                        match = FuzzySharp.Fuzz.PartialRatio(needle, hay) >= kFuzzyCutoff;
                    }
                }
                if (bone.IsHidden == match) bone.IsHidden = !match;
                if (match) visibleCount++;
            }
            var filteredOut = hasFilter && visibleCount == 0;
            if (grp.IsFilteredOut != filteredOut) grp.IsFilteredOut = filteredOut;
            // While filtering, expand any group with a match so the user
            // sees the hit immediately. When the search clears, leave
            // expansion as-is — the user's manual collapses survive.
            if (hasFilter && visibleCount > 0 && !grp.IsExpanded) grp.IsExpanded = true;
        }
    }

    /// <summary>Outliner tab click. Flips
    /// <see cref="PoseToEmoteViewModel.IsOutlinerOpen"/>, which drives
    /// the panel Border's bound Width between 280 (open) and 0 (folded).
    /// The viewport column is `*`-sized so the freed width snaps back
    /// to the viewport when the panel folds away, Blender N-panel style.</summary>
    private void OnOutlinerToggleClick(object sender, RoutedEventArgs e)
    {
        _vm.IsOutlinerOpen = !_vm.IsOutlinerOpen;
    }

    // Unified sidebar tabs — swap which content the single right panel shows.
    private void OnSidebarTabEmotes(object sender, RoutedEventArgs e)
    {
        _vm.SidebarTab = PoseToEmoteViewModel.EmoteSidebarTab.Emotes;
        _vm.IsMotionPanelSelected = false;
    }

#if !FIVEOS_MOTION
    private void OnSidebarTabMotion(object sender, RoutedEventArgs e) { }

    private void OnVideoToEmote(object sender, RoutedEventArgs e) { }

    public void RunVideoToEmote() { }

    public void OpenMotionMode() { }
#endif

    private void OnSidebarTabOutliner(object sender, RoutedEventArgs e)
        => _vm.SidebarTab = PoseToEmoteViewModel.EmoteSidebarTab.Outliner;

    /// <summary>Open the settings dialog that replaced the Inspector rail.
    /// It shares this view's VM, so its bindings drive the same properties
    /// the PropertyChanged subscription above pushes to the viewer — the
    /// preview keeps updating live while the dialog is open. Modeless on
    /// purpose: ShowDialog would block scrubbing, which is exactly what you
    /// need to be doing while tuning calibration.</summary>
    private void OnOpenPoseSettings(object sender, RoutedEventArgs e)
    {
        if (_poseSettingsWindow is { IsLoaded: true })
        {
            _poseSettingsWindow.Activate();
            return;
        }
        _poseSettingsWindow = new PoseSettingsWindow(_vm)
        {
            Owner = System.Windows.Window.GetWindow(this),
        };
        _poseSettingsWindow.Closed += (_, __) => _poseSettingsWindow = null;
        _poseSettingsWindow.Show();
    }

    /// <summary>Live settings dialog, if open. Tracked so the gear re-focuses
    /// the existing window instead of stacking duplicates that would fight
    /// over the same VM.</summary>
    private PoseSettingsWindow? _poseSettingsWindow;

    /// <summary>Pushes the current Secondary-Motion toggle + intensity
    /// down to the WebView2 evaluator. Called by the VM's property-
    /// changed handler so both the switch flip and slider drag stay
    /// in sync with the JS side without per-control event wiring in
    /// the XAML.</summary>
    private async void PushSecondaryMotionToViewer()
    {
        if (!_webViewReady) return;
        var enabled = _vm.SecondaryMotionEnabled ? "true" : "false";
        var intensity = _vm.SecondaryMotionIntensity
            .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        await Viewport.CoreWebView2.ExecuteScriptAsync(
            $"window.poseSetSecondaryMotion && window.poseSetSecondaryMotion({enabled}, {intensity})");
    }

    private async Task<bool> HasAnimatedTimelineAsync()
    {
        if (!_webViewReady) return false;
        try
        {
            var raw = await Viewport.CoreWebView2.ExecuteScriptAsync(
                "window.poseHasAnimatedContent && window.poseHasAnimatedContent()");
            return raw == "true";
        }
        catch { return false; }
    }

    private async Task<bool> ImportPayloadAsClipAsync(string payloadJson, string clipName, bool editable = false)
    {
        if (!_webViewReady) return false;

        // Rapid library browsing overlaps these calls; only the newest import
        // may run the post-import auto-fit + snapshot request below, or an
        // older clip's continuation re-fits the timeline AFTER the newer one.
        var gen = ++_timelineImportGen;

        // payloadJson is already a JSON object literal — never inline it in
        // ExecuteScriptAsync (multi-hundred-KB scripts freeze WebView2).
        // `editable` routes bone-tracks payloads (.ycd / library adds) through
        // the editable-keyframes conversion instead of a sealed clip strip.
        string? raw = await ImportPayloadAsClipViaMessageAsync(payloadJson, clipName, editable);

        if (gen != _timelineImportGen) return false;

        var json = PeelScriptJson(raw);
        if (string.IsNullOrEmpty(json)) { _vm.StatusText = "Clip import failed — empty viewer response."; return false; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean())
            {
                // Auto-fit: an opened animation spans the ENTIRE timeline (user
                // directive 2026-07-16). Zoom 1 = the full clip duration across
                // the full lane width — stale zoom/scroll from a previous clip
                // used to leave the new strip tiny or off-screen. Duration/fps
                // arrive from the viewer's pose-timeline-update echo; zoom is
                // duration-relative, so fitting here is race-free. The frame-
                // numbered ruler then covers exactly the clip's frame range.
                _vm.TimelineZoom = 1;
                _vm.TimelineScrollOffset = 0;
                RedrawTimeline();
                // Refresh the per-bone lanes of the unified layers timeline —
                // the snapshot otherwise only arrives at rig load.
                _ = SendTimelineCommandAsync("requestSnapshot");
                return true;
            }
            var reason = doc.RootElement.TryGetProperty("reason", out var rEl) ? rEl.GetString() : raw;
            var msg = doc.RootElement.TryGetProperty("msg", out var mEl) ? mEl.GetString() : null;
            _vm.StatusText = reason == "no-bones-matched"
                ? "Import failed — bone names in the clip didn't match the loaded rig."
                : $"Clip import failed: {reason}" + (msg is not null ? $" — {msg}" : "");
            AppendDebug("err", "timeline", "importKeyframesAsClip failed", reason ?? raw ?? "");
            return false;
        }
        catch
        {
            _vm.StatusText = "Clip import failed — viewer returned: " + (raw ?? "(null)");
            return false;
        }
    }

    /// <summary>Large clip payloads go through PostWebMessage instead of
    /// ExecuteScriptAsync, which chokes on multi-megabyte inline scripts.</summary>
    private async Task<string?> ImportPayloadAsClipViaMessageAsync(string payloadJson, string clipName, bool editable = false)
    {
        int requestId = Interlocked.Increment(ref _importClipRequestId);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_importClipWaiters) _importClipWaiters[requestId] = tcs;

        var envelope =
            $"{{\"kind\":\"host-import-keyframe-clip\",\"requestId\":{requestId}," +
            (editable ? "\"editable\":true," : "") +
            $"\"clipName\":{JsonSerializer.Serialize(clipName)},\"payload\":{payloadJson}}}";
        Viewport.CoreWebView2.PostWebMessageAsJson(envelope);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        await using var reg = cts.Token.Register(() => tcs.TrySetResult(null));
        return await tcs.Task.ConfigureAwait(true);
    }

    private static bool LooksLikeHashName(string? name)
        => !string.IsNullOrEmpty(name) && name.All(char.IsDigit);

    private void SetImportOverlay(bool on, string? caption = null)
    {
        if (on)
        {
            if (caption is not null) _vm.OperationCaption = caption;
            _vm.IsOperationRunning = true;
            _vm.OperationProgress = 0;
        }
        else
        {
            _vm.IsOperationRunning = false;
            _vm.OperationCanCancel = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async Task PumpUiAsync()
    {
        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
    }

    private async Task BakeTimelineToClipAsync(string clipName)
    {
        if (!_webViewReady) return;
        var encoded = JsonSerializer.Serialize(clipName);
        await Viewport.CoreWebView2.ExecuteScriptAsync(
            $"window.poseBakeToClip && window.poseBakeToClip({encoded})");
    }

    private async Task TrimActiveStripInAsync()
    {
        if (_vm.TimelineClipTrackLocked) { _vm.StatusText = "Clip track is locked."; return; }
        if (!_webViewReady) return;
        var ok = await Viewport.CoreWebView2.ExecuteScriptAsync(
            "window.poseActiveStripId && window.poseSetStripIn && window.poseActiveStripId() != null && window.poseSetStripIn(window.poseActiveStripId())");
        if (ok == "true") _vm.StatusText = "Set In — trimmed clip start to playhead.";
        else _vm.StatusText = "Move playhead inside a clip to set In point.";
    }

    private async Task TrimActiveStripOutAsync()
    {
        if (_vm.TimelineClipTrackLocked) { _vm.StatusText = "Clip track is locked."; return; }
        if (!_webViewReady) return;
        var ok = await Viewport.CoreWebView2.ExecuteScriptAsync(
            "window.poseActiveStripId && window.poseSetStripOut && window.poseActiveStripId() != null && window.poseSetStripOut(window.poseActiveStripId())");
        if (ok == "true") _vm.StatusText = "Set Out — trimmed clip end to playhead.";
        else _vm.StatusText = "Move playhead inside a clip to set Out point.";
    }

    private async Task SplitActiveStripAsync()
    {
        if (_vm.TimelineClipTrackLocked) { _vm.StatusText = "Clip track is locked."; return; }
        if (!_webViewReady) return;
        var raw = await Viewport.CoreWebView2.ExecuteScriptAsync(
            "window.poseActiveStripId && window.poseSplitStrip && window.poseActiveStripId() != null ? window.poseSplitStrip(window.poseActiveStripId()) : null");
        if (!string.IsNullOrEmpty(raw) && raw != "null")
            _vm.StatusText = "Split clip at playhead.";
        else
            _vm.StatusText = "Move playhead inside a clip to split it.";
    }

    private async void OnTimelineSetInClick(object sender, RoutedEventArgs e) =>
        await TrimSelectedStripsAsync("in");
    private async void OnTimelineSetOutClick(object sender, RoutedEventArgs e) =>
        await TrimSelectedStripsAsync("out");
    private async void OnTimelineSplitClick(object sender, RoutedEventArgs e)
    {
        var ids = _timelineSelection.Items
            .Where(x => x.Kind == TimelineItemKind.Strip)
            .Select(x => x.Id).ToArray();
        if (ids.Length == 0) { await SplitActiveStripAsync(); return; }
        await SendTimelineCommandAsync("split", ids,
            new { atTime = _vm.TimelineTime });
        _vm.StatusText = $"Split {ids.Length} clip(s) at playhead.";
    }

    private async Task TrimSelectedStripsAsync(string edge)
    {
        var ids = _timelineSelection.Items
            .Where(x => x.Kind == TimelineItemKind.Strip)
            .Select(x => x.Id).ToArray();
        if (ids.Length == 0)
        {
            if (edge == "in") await TrimActiveStripInAsync();
            else await TrimActiveStripOutAsync();
            return;
        }
        foreach (var id in ids)
            await SendTimelineCommandAsync("trim", new[] { id },
                new { id, edge, time = _vm.TimelineTime });
        _vm.StatusText = edge == "in"
            ? $"Set In on {ids.Length} clip(s)."
            : $"Set Out on {ids.Length} clip(s).";
    }

    /// <summary>Bake the current keyframe sequence into a reusable
    /// THREE.AnimationClip on the viewer side and add it to the clip
    /// library. The button is gated on IsAnimatedExport so this should
    /// only fire when there are 2+ keyframes; the JS baker still throws
    /// defensively, but the host-side catch keeps the WebView2 stable.</summary>
    private async void OnBakeAsClipClick(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        try
        {
            // Empty name => JS auto-generates a timestamped slug. M2's
            // strip editor (or a right-click rename) can adjust later.
            await Viewport.CoreWebView2.ExecuteScriptAsync("window.poseBakeToClip('')");
            _vm.StatusText = "Baked current keyframes into clip library.";
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Bake failed: {ex.Message}";
        }
    }

    /// <summary>Remove a clip from the viewer-side library. The Id is
    /// stashed in the button's Tag by the ListBox.ItemTemplate so we
    /// can address the right entry regardless of selection state.</summary>
    private async void OnDeleteClipClick(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        if (sender is not FrameworkElement fe || fe.Tag is not int id) return;
        try
        {
            await Viewport.CoreWebView2.ExecuteScriptAsync($"window.poseDeleteClip({id})");
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Clip delete failed: {ex.Message}";
        }
    }

    /// <summary>Click on a group row in the outliner. Toggles
    /// IsExpanded so the chevron + folder icon flip via DataTrigger
    /// and the bone children show/hide. The eye toggle is a sibling
    /// element rather than a child of the row Button, so its own click
    /// hit-tests first and never bubbles up to this handler.</summary>
    private void OnGroupRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is PoseBoneGroup grp)
        {
            grp.IsExpanded = !grp.IsExpanded;
        }
    }

    /// <summary>Click on a rig row at the top of the outliner. Two
    /// behaviours coexist:
    ///   * If the rig isn't currently the model-transform target,
    ///     select it — gizmo attaches to the model root with the
    ///     translate / rotate / scale toolbar visible.
    ///   * If it's already selected, toggle the expand chevron so
    ///     you can collapse it to a one-line row when working on
    ///     the other rig.
    /// The viewer broadcasts rig-selected-for-transform back so the
    /// IsModelSelected highlight stays in sync if the user picks a
    /// bone afterwards (which auto-clears selection).</summary>
    private async void OnRigRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not PoseRig rig) return;
        if (rig.IsModelSelected)
        {
            rig.IsExpanded = !rig.IsExpanded;
            return;
        }
        if (!_webViewReady) return;
        try
        {
            var idArg = System.Text.Json.JsonSerializer.Serialize(rig.Id);
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.selectRigForTransform && window.selectRigForTransform({idArg})");
            _vm.StatusText = $"{rig.Name} selected — use the gizmo to move / rotate / scale the whole model.";
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Rig select failed: {ex.Message}";
        }
    }

    /// <summary>Eye toggle in the outliner group header. Pushes the
    /// region's bone-index set + new visibility to the viewer, which
    /// flips the joint-sphere meshes off so they're not pickable in the
    /// viewport. Visibility lives on the [[pose_bone_group]] VM object
    /// (TwoWay-bound), so the icon already updated by the time this
    /// handler runs — we just propagate.</summary>
    private async void OnGroupVisibilityToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not PoseBoneGroup grp) return;
        if (!_webViewReady) return;
        var indices = grp.Bones.Select(b => b.Index).ToArray();
        // JSON-serialize the int array so it's safe to drop into a JS
        // string literal — digits + commas only, no escape needed.
        var indicesJson = JsonSerializer.Serialize(indices);
        var visibleArg = grp.IsVisible ? "true" : "false";
        await Viewport.CoreWebView2.ExecuteScriptAsync(
            $"window.setPoseBoneVisibility && window.setPoseBoneVisibility('{indicesJson}', {visibleArg})");
    }

    private async Task<string?> GetPoseJsonForExportAsync()
    {
        if (!_webViewReady) return null;
        var raw = await Viewport.CoreWebView2.ExecuteScriptAsync("window.getPose && window.getPose()");
        if (string.IsNullOrEmpty(raw) || raw == "null") return null;
        try { return JsonSerializer.Deserialize<string>(raw); }
        catch { return raw; }
    }

    private async void OnExportStationaryEmote(object sender, RoutedEventArgs e) =>
        await ExportEmoteAsync(stationary: true);

    private async void OnExportWalkingEmote(object sender, RoutedEventArgs e) =>
        await ExportEmoteAsync(stationary: false);

    private async Task ExportEmoteAsync(bool stationary)
    {
        var poseJson = await GetPoseJsonForExportAsync();
        if (string.IsNullOrEmpty(poseJson))
        {
            _vm.StatusText = "No pose to export — load a rigged model first.";
            return;
        }

        if (stationary && _vm.SuggestFxResourceForExport)
        {
            var r = AppDialog.Show(
                "This animation uses root motion — dpemotes won't move the ped.\n\nExport as a FiveM resource (.fxresource) instead?",
                "Use walking export?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
            {
                await ExportEmoteAsync(stationary: false);
                return;
            }
        }

        var sourceName = Path.GetFileNameWithoutExtension(_vm.LoadedModelPath);
        var defaultName = string.IsNullOrEmpty(sourceName) ? "pose" : sourceName;

        if (stationary)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export stationary emote (dpemotes)",
                Filter = "dpemotes pack (*.dpemotes.zip)|*.dpemotes.zip",
                FileName = defaultName + ".dpemotes.zip",
                DefaultExt = ".dpemotes.zip",
            };
            if (dlg.ShowDialog() != true) return;
            try { await WriteDpemotesPackAsync(dlg.FileName, poseJson); }
            catch (Exception ex) { _vm.StatusText = "Save failed: " + ex.Message; }
        }
        else
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export walking emote (FiveM resource)",
                Filter = "FiveM resource folder (*.fxresource)|*.fxresource",
                FileName = defaultName + ".fxresource",
                DefaultExt = ".fxresource",
            };
            if (dlg.ShowDialog() != true) return;
            try { await WriteFiveMResourceFolderAsync(dlg.FileName, poseJson); }
            catch (Exception ex) { _vm.StatusText = "Save failed: " + ex.Message; }
        }
    }

    private async void OnExportPose(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady)
        {
            _vm.StatusText = "Viewer not ready.";
            return;
        }

        var raw = await Viewport.CoreWebView2.ExecuteScriptAsync("window.getPose && window.getPose()");
        // ExecuteScriptAsync returns the JS value JSON-encoded. Our getPose
        // already returns a JSON STRING, so the result here is a
        // double-encoded JSON string. Peel one layer.
        string? poseJson = null;
        if (!string.IsNullOrEmpty(raw) && raw != "null")
        {
            try { poseJson = JsonSerializer.Deserialize<string>(raw); }
            catch { poseJson = raw; }
        }

        if (string.IsNullOrEmpty(poseJson))
        {
            _vm.StatusText = "No pose to export — load a rigged model first.";
            return;
        }

        var sourceName = Path.GetFileNameWithoutExtension(_vm.LoadedModelPath);
        var defaultName = string.IsNullOrEmpty(sourceName) ? "pose" : sourceName;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Pose",
            // Order top-to-bottom = most-to-least drop-in:
            //   1. FiveM resource folder = self-contained, drop in resources/ + ensure.
            //   2. dpemotes pack = merge into an existing dpemotes install.
            //   3. Raw .ycd = for users wiring it into another anim pipeline.
            //   4. FiveOS JSON = sharing the pose between FiveOS users without a baked .ycd.
            // The resource-folder filter uses a sentinel .fxresource "extension"
            // that we strip + treat as the folder name when the user picks it.
            Filter =
                  "FiveM resource folder (*.fxresource)|*.fxresource"
                + "|dpemotes pack (*.dpemotes.zip)|*.dpemotes.zip"
                + "|FiveM emote (*.ycd)|*.ycd"
                + "|CodeWalker XML (*.ycd.xml)|*.ycd.xml"
                + "|FiveOS pose JSON (*.fivepose.json)|*.fivepose.json",
            FileName = defaultName + ".fxresource",
            DefaultExt = ".fxresource",
        };
        if (dlg.ShowDialog() != true) return;

        var name = Path.GetFileName(dlg.FileName).ToLowerInvariant();
        try
        {
            if (name.EndsWith(".fxresource"))
                await WriteFiveMResourceFolderAsync(dlg.FileName, poseJson);
            else if (name.EndsWith(".dpemotes.zip"))
                await WriteDpemotesPackAsync(dlg.FileName, poseJson);
            else if (name.EndsWith(".ycd.xml"))
                await WriteYcdXmlAsync(dlg.FileName, poseJson);
            else if (name.EndsWith(".ycd"))
                await WriteYcdAsync(dlg.FileName, poseJson);
            else
                WriteJsonPose(dlg.FileName, poseJson);
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Save failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Build a self-contained FiveM resource folder. The SaveFileDialog
    /// gave us a file path ending in <c>.fxresource</c> — we strip that
    /// suffix and use the rest as the resource folder path. The folder
    /// gets fxmanifest.lua + client.lua + stream/&lt;name&gt;.ycd.
    /// </summary>
    private async Task WriteFiveMResourceFolderAsync(string sentinelPath, string poseJson)
    {
        // sentinelPath looks like C:\path\my_emote.fxresource — strip
        // the sentinel "extension" to get the resource folder location.
        var folderPath = sentinelPath;
        if (folderPath.EndsWith(".fxresource", StringComparison.OrdinalIgnoreCase))
            folderPath = folderPath[..^".fxresource".Length];

        var emoteName = Path.GetFileName(folderPath);
        if (string.IsNullOrWhiteSpace(emoteName)) { _vm.StatusText = "Pick a name for the resource folder."; return; }

        // Same single-pose-vs-animated branching as the dpemotes path.
        string? kfJson = null;
        try
        {
            var raw = await Viewport.CoreWebView2.ExecuteScriptAsync("window.getKeyframes && window.getKeyframes()");
            if (!string.IsNullOrEmpty(raw) && raw != "null")
            {
                try { kfJson = JsonSerializer.Deserialize<string>(raw); }
                catch { kfJson = raw; }
            }
        }
        catch { /* fall through */ }

        int kfCount = 0;
        if (!string.IsNullOrEmpty(kfJson))
        {
            try { using var kdoc = JsonDocument.Parse(kfJson); if (kdoc.RootElement.TryGetProperty("keyframes", out var kfEl)) kfCount = kfEl.GetArrayLength(); }
            catch (Exception ex) { Services.FosLogger.Warn("pose", "keyframe count parse failed", ex); }
        }

        byte[]? ycdBytes;
        bool isAnimated;
        if (await HasAnimatedTimelineAsync())
        {
            ycdBytes = await BuildAnimatedYcdBytesFromViewerAsync(emoteName, kfJson ?? "{}");
            isAnimated = true;
        }
        else
        {
            ycdBytes = BuildSinglePoseYcdBytes(emoteName, poseJson);
            isAnimated = false;
        }

        if (ycdBytes is null || ycdBytes.Length == 0)
        {
            // Status text already set by the inner build helpers on bail.
            return;
        }

        var pretty = char.ToUpper(emoteName[0]) + emoteName[1..].Replace('_', ' ');

        // Pull prop info if loaded (same as dpemotes path).
        Services.DpemotesPackBuilder.PropInfo? propInfo = null;
        if (_vm.HasProp)
        {
            propInfo = await GetPropInfoForExportAsync();
        }

        try
        {
            Services.FiveMResourceBuilder.BuildFolder(
                folderPath: folderPath,
                emoteName: emoteName,
                displayName: pretty,
                ycdBytes: ycdBytes,
                isLooping: !isAnimated,
                movement: _vm.EffectiveExportMovement,
                prop: propInfo,
                ycdXml: _lastYcdXml);

            _vm.StatusText = isAnimated
                ? $"Wrote resource to {folderPath} (animated, {kfCount} keyframes). Drop folder into resources/ and `ensure {emoteName}`."
                : $"Wrote resource to {folderPath} (single pose). Drop folder into resources/ and `ensure {emoteName}`.";
            AppendDebug("info", "export", $"FiveM resource folder written: {folderPath}",
                $"animated={isAnimated} keyframes={kfCount}");
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Resource write failed: " + ex.Message;
            AppendDebug("err", "error", "FiveM resource write failed", ex.Message);
        }
    }

    /// <summary>
    /// Bake to .ycd in-memory, then zip it together with a paste-ready
    /// Lua snippet + a README of drop-in instructions. The .ycd is the
    /// same artifact WriteYcd would have produced (single-pose or
    /// animated, depending on keyframe count); we just package it
    /// dpemotes-style instead of writing it to disk directly.
    /// </summary>
    private async Task WriteDpemotesPackAsync(string zipPath, string poseJson)
    {
        // Strip the .dpemotes.zip suffix to get the emote slug. Lower-case
        // + safe-chars happens inside DpemotesPackBuilder, but slug-only
        // here keeps the filename and the in-game /e command aligned.
        var emoteName = Path.GetFileName(zipPath);
        if (emoteName.EndsWith(".dpemotes.zip", StringComparison.OrdinalIgnoreCase))
            emoteName = emoteName[..^".dpemotes.zip".Length];
        else
            emoteName = Path.GetFileNameWithoutExtension(emoteName);

        // Use a temp file path for the .ycd bake -- we read it back as
        // bytes for the zip. Doing both in memory would require dragging
        // the writer's MemoryStream out of WriteYcd, which is more
        // intrusive than just round-tripping through %TEMP%.
        var tempYcd = Path.Combine(Path.GetTempPath(), $"fiveos_{Guid.NewGuid():N}.ycd");
        try
        {
            // The async ycd writer needs a path. Have it write to the
            // temp file and we'll pick up the bytes after.
            await Task.Run(() =>
            {
                // Use the same single-pose-vs-animated branching as a
                // direct .ycd export would. WriteYcd is async-void; we
                // call its core paths directly to control the path.
            });

            string? kfJson = null;
            try
            {
                var raw = await Viewport.CoreWebView2.ExecuteScriptAsync("window.getKeyframes && window.getKeyframes()");
                if (!string.IsNullOrEmpty(raw) && raw != "null")
                {
                    try { kfJson = JsonSerializer.Deserialize<string>(raw); }
                    catch { kfJson = raw; }
                }
            }
            catch { /* fall through */ }

            int kfCount = 0;
            if (!string.IsNullOrEmpty(kfJson))
            {
                try { using var kdoc = JsonDocument.Parse(kfJson); if (kdoc.RootElement.TryGetProperty("keyframes", out var kfEl)) kfCount = kfEl.GetArrayLength(); }
                catch (Exception ex) { Services.FosLogger.Warn("pose", "keyframe count parse failed", ex); }
            }

            byte[]? ycdBytes = null;
            bool isAnimated = false;
            if (await HasAnimatedTimelineAsync())
            {
                ycdBytes = await BuildAnimatedYcdBytesFromViewerAsync(emoteName, kfJson ?? "{}");
                isAnimated = true;
            }
            else
            {
                ycdBytes = BuildSinglePoseYcdBytes(emoteName, poseJson);
            }

            if (ycdBytes is null || ycdBytes.Length == 0)
            {
                // Status text was already set by the inner build helpers
                // when they bail (e.g. no GTA-mapped bones).
                return;
            }

            // Loop=true for held-pose emotes, false for animated clips
            // that play once. Sensible defaults; user can edit the
            // snippet before pasting.
            var pretty = char.ToUpper(emoteName[0]) + emoteName[1..].Replace('_', ' ');

            // If a prop is loaded, fetch its transform relative to the
            // chosen bone and pack as a PropEmotes entry.
            Services.DpemotesPackBuilder.PropInfo? propInfo = null;
            if (_vm.HasProp)
            {
                propInfo = await GetPropInfoForExportAsync();
            }

            var zipBytes = Services.DpemotesPackBuilder.Build(
                emoteName, pretty, ycdBytes,
                isLooping: !isAnimated,
                movement: _vm.EffectiveExportMovement,
                prop: propInfo);
            File.WriteAllBytes(zipPath, zipBytes);

            _vm.StatusText = isAnimated
                ? $"Saved {Path.GetFileName(zipPath)} (animated, {kfCount} keyframes). Unzip into dpemotes/, paste the .lua snippet."
                : $"Saved {Path.GetFileName(zipPath)} (single pose). Unzip into dpemotes/, paste the .lua snippet.";
        }
        finally
        {
            try { if (File.Exists(tempYcd)) File.Delete(tempYcd); }
            catch (Exception ex) { Services.FosLogger.Warn("pose", "temp ycd cleanup failed", ex); }
        }
    }

    /// <summary>Peel one layer of JSON string encoding from ExecuteScriptAsync.</summary>
    private static string? PeelScriptJson(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "null") return null;
        try { return JsonSerializer.Deserialize<string>(raw) ?? raw; }
        catch { return raw; }
    }

    /// <summary>Parse a <c>getPose()</c> JSON payload into posed bones — the
    /// proven single-pose export path (raw glTF locals, SKEL_ROOT skipped).</summary>
    private static List<Services.PosedBone> ParsePoseBonesFromJson(string poseJson)
    {
        var posed = new List<Services.PosedBone>();
        using var doc = JsonDocument.Parse(poseJson);
        if (!doc.RootElement.TryGetProperty("bones", out var bonesEl)) return posed;
        foreach (var b in bonesEl.EnumerateArray())
        {
            var name = b.TryGetProperty("name", out var nm) ? (nm.GetString() ?? "") : "";
            if (!Services.GtaBoneTags.TryResolve(name, out var tag) || tag == 0) continue;
            if (!b.TryGetProperty("rot_xyzw", out var rEl) || rEl.GetArrayLength() < 4) continue;
            var q = new System.Numerics.Quaternion(
                (float)rEl[0].GetDouble(), (float)rEl[1].GetDouble(),
                (float)rEl[2].GetDouble(), (float)rEl[3].GetDouble());
            posed.Add(new Services.PosedBone(tag, System.Numerics.Vector3.Zero, q));
        }
        return posed;
    }

    /// <summary>Same logic as WriteSinglePoseYcd but returns bytes
    /// instead of writing to disk. Used by the dpemotes packer.</summary>
    private byte[]? BuildSinglePoseYcdBytes(string clipName, string poseJson)
    {
        var posed = ParsePoseBonesFromJson(poseJson);
        if (posed.Count == 0)
        {
            _vm.StatusText = "No bones from this rig matched the GTA player skeleton.";
            return null;
        }
        _lastYcdXml = Services.YcdPoseBuilder.BuildXml(clipName, posed);
        return Services.YcdPoseBuilder.Build(clipName, posed);
    }

    /// <summary>Animated export: sample each frame through the viewer's timeline
    /// evaluator + <c>getPose()</c>, then bake — identical bone logic to the
    /// proven single-pose path.</summary>
    private async Task<byte[]?> BuildAnimatedYcdBytesFromViewerAsync(string clipName, string? kfJsonForRootMotion)
    {
        if (!_webViewReady)
        {
            _vm.StatusText = "Viewer not ready.";
            return null;
        }

        string? sampleJson;
        try
        {
            var raw = await Viewport.CoreWebView2.ExecuteScriptAsync(
                "window.samplePoseClipForExport && window.samplePoseClipForExport()");
            sampleJson = PeelScriptJson(raw);
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Couldn't sample the timeline: " + ex.Message;
            return null;
        }

        if (string.IsNullOrEmpty(sampleJson))
        {
            _vm.StatusText = "Need 2+ keyframes for an animated .ycd.";
            return null;
        }

        using var doc = JsonDocument.Parse(sampleJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("poses", out var posesEl) || posesEl.GetArrayLength() < 2)
        {
            _vm.StatusText = "Need 2+ keyframes for an animated .ycd.";
            return null;
        }

        int fps = root.TryGetProperty("fps", out var fEl) ? fEl.GetInt32() : 30;
        int frames = root.TryGetProperty("frames", out var frEl) ? frEl.GetInt32() : posesEl.GetArrayLength();
        fps = Math.Clamp(fps, 1, 120);
        frames = Math.Max(2, frames);

        var perTag = new Dictionary<ushort, System.Numerics.Quaternion[]>();
        int frameIdx = 0;
        foreach (var pose in posesEl.EnumerateArray())
        {
            if (frameIdx >= frames) break;
            if (!pose.TryGetProperty("bones", out var bonesEl)) { frameIdx++; continue; }
            foreach (var b in bonesEl.EnumerateArray())
            {
                var name = b.TryGetProperty("name", out var nm) ? (nm.GetString() ?? "") : "";
                if (!Services.GtaBoneTags.TryResolve(name, out var tag) || tag == 0) continue;
                if (!b.TryGetProperty("rot_xyzw", out var rEl) || rEl.GetArrayLength() < 4) continue;
                if (!perTag.TryGetValue(tag, out var arr))
                {
                    arr = new System.Numerics.Quaternion[frames];
                    for (int i = 0; i < frames; i++) arr[i] = System.Numerics.Quaternion.Identity;
                    perTag[tag] = arr;
                }
                arr[frameIdx] = new System.Numerics.Quaternion(
                    (float)rEl[0].GetDouble(), (float)rEl[1].GetDouble(),
                    (float)rEl[2].GetDouble(), (float)rEl[3].GetDouble());
            }
            frameIdx++;
        }

        if (perTag.Count == 0)
        {
            _vm.StatusText = "No bones from this rig matched the GTA player skeleton.";
            return null;
        }

        var tracks = perTag
            .OrderBy(kv => kv.Key)
            .Select(kv => new Services.PosedBoneTrack(kv.Key, kv.Value))
            .ToList();

        Services.PosedPositionTrack? rootMotion = null;
        if (_vm.MovementIndex == (int)Services.EmoteMovement.RootMotion)
        {
            var exportSource = root.TryGetProperty("source", out var srcEl) ? (srcEl.GetString() ?? "") : "";
            if (root.TryGetProperty("roots", out var rootsEl) && rootsEl.GetArrayLength() >= 2)
                rootMotion = BuildRootMotionTrackFromSamples(rootsEl, frames, fps, exportSource);
            else if (!string.IsNullOrEmpty(kfJsonForRootMotion))
            {
                using var kdoc = JsonDocument.Parse(kfJsonForRootMotion);
                rootMotion = BuildRootMotionTrack(kdoc.RootElement, frames, fps);
            }
            if (rootMotion != null && root.TryGetProperty("floorOffsetY", out var foEl))
            {
                float bias = (float)foEl.GetDouble();
                if (Math.Abs(bias) > 0.001f)
                {
                    bool animImport = string.Equals(exportSource, "anim-import", StringComparison.OrdinalIgnoreCase);
                    var pf = rootMotion.PerFrame;
                    for (int f = 0; f < pf.Length; f++)
                    {
                        pf[f] = animImport
                            ? new System.Numerics.Vector3(pf[f].X, pf[f].Y + bias, pf[f].Z)
                            : new System.Numerics.Vector3(pf[f].X, pf[f].Y, pf[f].Z + bias);
                    }
                }
            }
        }
        var positions = rootMotion is null ? null : new[] { rootMotion };
        _lastYcdXml = Services.YcdAnimationBuilder.BuildXml(clipName, tracks, frames, fps, positions);
        _vm.LastExportMapped = perTag.Count;
        _vm.LastExportSkipped = 0;
        _vm.LastExportSkippedNames = "";
        return Services.YcdAnimationBuilder.Build(clipName, tracks, frames, fps, positions);
    }

    // Source XML of the last .ycd we built. Captured by the Build*YcdBytes
    // helpers and stamped onto the exported resource folder so the user
    // can compile via CodeWalker.exe as a fallback diagnostic when our
    // in-process binary writer produces bytes RAGE doesn't fully consume.
    private string? _lastYcdXml;

    private void WriteJsonPose(string path, string poseJson)
    {
        using var doc = JsonDocument.Parse(poseJson);
        var pretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, pretty);
        _vm.StatusText = "Saved " + Path.GetFileName(path);
    }

    private async Task WriteYcdAsync(string path, string poseJson)
    {
        string? kfJson = null;
        try
        {
            var raw = await Viewport.CoreWebView2.ExecuteScriptAsync("window.getKeyframes && window.getKeyframes()");
            kfJson = PeelScriptJson(raw);
        }
        catch { /* fall through to single-pose */ }

        int kfCount = 0;
        bool hasStrips = false;
        if (!string.IsNullOrEmpty(kfJson))
        {
            try
            {
                using var kdoc = JsonDocument.Parse(kfJson);
                if (kdoc.RootElement.TryGetProperty("keyframes", out var kfEl))
                    kfCount = kfEl.GetArrayLength();
            }
            catch { /* malformed -> single-pose */ }
        }
        try
        {
            var stripRaw = await Viewport.CoreWebView2.ExecuteScriptAsync(
                "window.poseGetStrips && window.poseGetStrips().length");
            if (int.TryParse(stripRaw, out var sc)) hasStrips = sc > 0;
        }
        catch { /* ignore */ }

        if ((kfCount >= 2 || hasStrips) && _webViewReady)
            await WriteAnimatedYcdAsync(path, kfJson ?? "{}");
        else
            WriteSinglePoseYcd(path, poseJson);
    }

    /// <summary>Export the CodeWalker .ycd.xml (the human-readable / compilable
    /// XML form of the clip dict) instead of the packed binary — same
    /// single-vs-animated branching as WriteYcdAsync. The XML is what our
    /// binary writer round-trips internally (_lastYcdXml), so this just
    /// surfaces it as its own download for users who compile via CodeWalker
    /// or want to inspect/tweak the clip.</summary>
    private async Task WriteYcdXmlAsync(string path, string poseJson)
    {
        // clip name = file name minus the ".ycd.xml" suffix
        var clipName = Path.GetFileName(path);
        clipName = clipName.EndsWith(".ycd.xml", StringComparison.OrdinalIgnoreCase)
            ? clipName[..^8]
            : Path.GetFileNameWithoutExtension(clipName);

        // Same single-vs-animated decision as WriteYcdAsync.
        string? kfJson = null;
        try { kfJson = PeelScriptJson(await Viewport.CoreWebView2.ExecuteScriptAsync("window.getKeyframes && window.getKeyframes()")); }
        catch { /* fall through to single-pose */ }

        int kfCount = 0;
        if (!string.IsNullOrEmpty(kfJson))
        {
            try
            {
                using var kdoc = JsonDocument.Parse(kfJson);
                if (kdoc.RootElement.TryGetProperty("keyframes", out var kfEl)) kfCount = kfEl.GetArrayLength();
            }
            catch { /* malformed -> single-pose */ }
        }
        bool hasStrips = false;
        try
        {
            var stripRaw = await Viewport.CoreWebView2.ExecuteScriptAsync("window.poseGetStrips && window.poseGetStrips().length");
            if (int.TryParse(stripRaw, out var sc)) hasStrips = sc > 0;
        }
        catch { /* ignore */ }

        string? xml;
        if ((kfCount >= 2 || hasStrips) && _webViewReady)
        {
            // Building the animated .ycd populates _lastYcdXml as a side effect.
            await BuildAnimatedYcdBytesFromViewerAsync(clipName, kfJson ?? "{}");
            xml = _lastYcdXml;
        }
        else
        {
            var posed = ParsePoseBonesFromJson(poseJson);
            if (posed.Count == 0)
            {
                _vm.StatusText = "Pose JSON has no bones[] — nothing to bake.";
                return;
            }
            xml = Services.YcdPoseBuilder.BuildXml(clipName, posed);
            _lastYcdXml = xml;
        }

        if (string.IsNullOrEmpty(xml))
        {
            _vm.StatusText = "Could not build .ycd.xml.";
            return;
        }
        File.WriteAllText(path, xml);
        _vm.StatusText = "Saved " + Path.GetFileName(path);
        AppendDebug("info", "export", $".ycd.xml saved: {Path.GetFileName(path)}", $"len={xml.Length}");
    }

    private void WriteSinglePoseYcd(string path, string poseJson)
    {
        var posed = ParsePoseBonesFromJson(poseJson);
        if (posed.Count == 0)
        {
            _vm.StatusText = "Pose JSON has no bones[] — nothing to bake.";
            return;
        }

        var skippedNames = new List<string>();
        int mapped = posed.Count, skipped = 0;
        using (var doc = JsonDocument.Parse(poseJson))
        {
            if (doc.RootElement.TryGetProperty("bones", out var bonesEl))
            {
                foreach (var b in bonesEl.EnumerateArray())
                {
                    var name = b.TryGetProperty("name", out var nm) ? (nm.GetString() ?? "") : "";
                    if (!Services.GtaBoneTags.TryResolve(name, out _))
                    {
                        skipped++;
                        if (skippedNames.Count < 64) skippedNames.Add(name);
                    }
                }
            }
        }

        var clipName = Path.GetFileNameWithoutExtension(path);
        var bytes = Services.YcdPoseBuilder.Build(clipName, posed);
        File.WriteAllBytes(path, bytes);

        _vm.LastExportMapped = mapped;
        _vm.LastExportSkipped = skipped;
        _vm.LastExportSkippedNames = string.Join(", ", skippedNames);

        _vm.StatusText = $"Saved {Path.GetFileName(path)} — {mapped} bone{(mapped == 1 ? "" : "s")} baked (single pose)" +
                         (skipped > 0 ? $", {skipped} unmapped." : ".");
        AppendDebug("info", "export", $"Single-pose .ycd saved: {Path.GetFileName(path)}",
            $"mapped={mapped} skipped={skipped}");
    }

    private async Task WriteAnimatedYcdAsync(string path, string? kfJsonForRootMotion)
    {
        var clipName = Path.GetFileNameWithoutExtension(path);
        var bytes = await BuildAnimatedYcdBytesFromViewerAsync(clipName, kfJsonForRootMotion);
        if (bytes is null || bytes.Length == 0) return;

        File.WriteAllBytes(path, bytes);
        _vm.StatusText = $"Saved {Path.GetFileName(path)} — {_vm.LastExportMapped} bones (animated, viewer-sampled).";
        AppendDebug("info", "export", $"Animated .ycd saved: {Path.GetFileName(path)}",
            $"bones={_vm.LastExportMapped}");
    }

    /// <summary>Find the bracketing keyframes for time t and slerp the
    /// bone's quaternion at fractional position. Returns identity if
    /// the bone is absent from all keyframes (RAGE applies the bind
    /// pose for absent rotation tracks).</summary>
    // Pelvis/spine orientation on export: the single-pose path writes SKEL_Pelvis
    // and the spine straight from the retarget (SKEL_ROOT skipped) and is verified
    // correct in-game against a shipped clip — so the convention needs NO root
    // correction. The animated path uses the identical convention + encoding, so it
    // needs none either. The in-game "bent forward" the user saw was the root-motion
    // MOVER we used to bake into SKEL_ROOT (RAGE contorts the ped when a normal .ycd
    // carries a mover); that is now reverted (root motion off by default), so the
    // animated export should stand upright with NO correction.
    //
    // Kept OFF by default. Only a genuine residual tip would warrant a correction,
    // tunable in one session via env FIVEOS_ANIM_ROOTFIX (x±/y±/z± = ±90° on that
    // axis, applied to SKEL_Pelvis + SKEL_Spine_Root so every descendant inherits
    // it). If a value is ever needed, report it and it becomes the hardcoded default.
    private static readonly (bool On, System.Numerics.Quaternion Q) AnimRootFix = ParseAnimRootFix();
    private static (bool, System.Numerics.Quaternion) ParseAnimRootFix()
    {
        var v = (System.Environment.GetEnvironmentVariable("FIVEOS_ANIM_ROOTFIX") ?? "off").Trim().ToLowerInvariant();
        if (v is "off" or "0" or "none") return (false, System.Numerics.Quaternion.Identity);
        var axis = v.StartsWith("y") ? System.Numerics.Vector3.UnitY
                 : v.StartsWith("z") ? System.Numerics.Vector3.UnitZ
                 : System.Numerics.Vector3.UnitX;
        float sign = v.Contains('-') ? -1f : 1f;
        return (true, System.Numerics.Quaternion.CreateFromAxisAngle(axis, sign * (float)(System.Math.PI / 2)));
    }
    private static System.Numerics.Quaternion FixAnimRootFrame(ushort tag, System.Numerics.Quaternion q)
    {
        if (!AnimRootFix.On) return q;
        if (tag == Services.GtaBoneTags.ByGtaName["SKEL_Pelvis"]
            || tag == Services.GtaBoneTags.ByGtaName["SKEL_Spine_Root"])
            return System.Numerics.Quaternion.Normalize(AnimRootFix.Q * q);
        return q;
    }

    private static System.Numerics.Quaternion SamplePoseAtTime(
        List<(double Time, Dictionary<int, System.Numerics.Quaternion> Bones)> kfs,
        int boneIndex,
        double t)
    {
        // Pre-clip: clamp to first keyframe.
        if (t <= kfs[0].Time)
            return kfs[0].Bones.TryGetValue(boneIndex, out var q0)
                ? q0 : System.Numerics.Quaternion.Identity;
        // Post-clip: clamp to last keyframe.
        if (t >= kfs[^1].Time)
            return kfs[^1].Bones.TryGetValue(boneIndex, out var qN)
                ? qN : System.Numerics.Quaternion.Identity;
        // Mid-clip: find bracketing pair, slerp.
        for (int i = 0; i < kfs.Count - 1; i++)
        {
            if (t >= kfs[i].Time && t <= kfs[i + 1].Time)
            {
                var a = kfs[i].Bones.TryGetValue(boneIndex, out var qa) ? qa : System.Numerics.Quaternion.Identity;
                var b = kfs[i + 1].Bones.TryGetValue(boneIndex, out var qb) ? qb : System.Numerics.Quaternion.Identity;
                var span = kfs[i + 1].Time - kfs[i].Time;
                var alpha = span > 1e-6 ? (t - kfs[i].Time) / span : 0;
                return System.Numerics.Quaternion.Slerp(a, b, (float)alpha);
            }
        }
        return System.Numerics.Quaternion.Identity;
    }

    /// <summary>Build the SKEL_ROOT mover (position) track from the per-keyframe
    /// <c>root</c> offsets. Samples the offset at every clip frame (linear) and
    /// converts viewer glTF Y-up to RAGE Z-up. Video imports use
    /// <c>[x, screen-up, 0]</c> → RAGE <c>[x, 0, screen-up]</c>; animation
    /// imports use <c>[x, 0, fwd]</c> → RAGE <c>[x, fwd, 0]</c>. Returns null
    /// when there's no root data or the travel is negligible (&lt; 3 cm).</summary>
    private static Services.PosedPositionTrack? BuildRootMotionTrack(JsonElement docRoot, int frames, int fps)
    {
        if (!docRoot.TryGetProperty("keyframes", out var kfEl) || kfEl.ValueKind != JsonValueKind.Array)
            return null;

        var source = docRoot.TryGetProperty("source", out var sEl) ? (sEl.GetString() ?? "") : "";
        bool animImport = string.Equals(source, "anim-import", StringComparison.OrdinalIgnoreCase);

        var samples = new List<(double Time, System.Numerics.Vector3 Pos)>();
        foreach (var kf in kfEl.EnumerateArray())
        {
            if (!kf.TryGetProperty("root", out var rEl) || rEl.ValueKind != JsonValueKind.Array || rEl.GetArrayLength() < 3)
                continue;
            double t = kf.TryGetProperty("time", out var tEl) ? tEl.GetDouble() : 0;
            samples.Add((t, new System.Numerics.Vector3(
                (float)rEl[0].GetDouble(), (float)rEl[1].GetDouble(), (float)rEl[2].GetDouble())));
        }
        if (samples.Count < 2) return null;
        samples.Sort((a, b) => a.Time.CompareTo(b.Time));

        // Skip a mover track when the ped barely travels — a static position
        // track would just add weight and risk a jitter the emote didn't have.
        float maxD = 0f;
        var origin = samples[0].Pos;
        foreach (var s in samples) maxD = Math.Max(maxD, System.Numerics.Vector3.Distance(s.Pos, origin));
        if (maxD < 0.03f) return null;

        var perFrame = new System.Numerics.Vector3[frames];
        for (int f = 0; f < frames; f++)
        {
            var v = SampleVec3AtTime(samples, f / (double)fps);
            // Animation retarget roots are glTF ground-plane [x, 0, fwd].
            // Video roots are [x, screen-up, 0]. Never feed glTF Y into RAGE Z
            // (up) — that was the floating bug.
            perFrame[f] = animImport
                ? new System.Numerics.Vector3(v.X, v.Z, 0f)
                : new System.Numerics.Vector3(v.X, 0f, v.Y);
        }
        return new Services.PosedPositionTrack(0 /* SKEL_ROOT */, perFrame);
    }

    /// <summary>Build SKEL_ROOT mover from per-frame root offsets sampled
    /// during <c>samplePoseClipForExport</c> (clip-strip workflow).</summary>
    private static Services.PosedPositionTrack? BuildRootMotionTrackFromSamples(
        JsonElement rootsEl, int frames, int fps, string? source)
    {
        if (rootsEl.GetArrayLength() < 2) return null;
        var perFrame = new System.Numerics.Vector3[frames];
        bool animImport = string.Equals(source, "anim-import", StringComparison.OrdinalIgnoreCase);
        for (int f = 0; f < frames; f++)
        {
            var idx = Math.Min(f, rootsEl.GetArrayLength() - 1);
            var rEl = rootsEl[idx];
            if (rEl.ValueKind != JsonValueKind.Array || rEl.GetArrayLength() < 3) continue;
            float x = (float)rEl[0].GetDouble();
            float y = (float)rEl[1].GetDouble();
            float z = (float)rEl[2].GetDouble();
            perFrame[f] = animImport
                ? new System.Numerics.Vector3(x, z, 0f)
                : new System.Numerics.Vector3(x, 0f, y);
        }
        float maxD = 0f;
        var origin = perFrame[0];
        foreach (var p in perFrame) maxD = Math.Max(maxD, System.Numerics.Vector3.Distance(p, origin));
        if (maxD < 0.03f) return null;
        return new Services.PosedPositionTrack(0, perFrame);
    }

    /// <summary>Apply constant snap-to-ground offset from viewer export sample.</summary>
    private static void ApplyFloorOffsetBias(System.Numerics.Vector3[] perFrame, JsonElement root, bool animImport)
    {
        if (!root.TryGetProperty("floorOffsetY", out var foEl)) return;
        float bias = (float)foEl.GetDouble();
        if (Math.Abs(bias) < 0.001f) return;
        for (int f = 0; f < perFrame.Length; f++)
        {
            var p = perFrame[f];
            perFrame[f] = animImport
                ? new System.Numerics.Vector3(p.X, p.Y + bias, p.Z)
                : new System.Numerics.Vector3(p.X, p.Y, p.Z + bias);
        }
    }

    private static System.Numerics.Vector3 SampleVec3AtTime(
        List<(double Time, System.Numerics.Vector3 Pos)> s, double t)
    {
        if (s.Count == 0) return default;
        if (t <= s[0].Time) return s[0].Pos;
        if (t >= s[^1].Time) return s[^1].Pos;
        for (int i = 0; i < s.Count - 1; i++)
        {
            if (t >= s[i].Time && t <= s[i + 1].Time)
            {
                var span = s[i + 1].Time - s[i].Time;
                var alpha = span > 1e-6 ? (t - s[i].Time) / span : 0;
                return System.Numerics.Vector3.Lerp(s[i].Pos, s[i + 1].Pos, (float)alpha);
            }
        }
        return s[^1].Pos;
    }

    private static bool HasSideMarker(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        // Same detection rules as the JS-side poseFindMirrorName. Kept in
        // sync deliberately; if you tweak the JS heuristics, mirror them
        // here too so the "X modified" label tracks reality.
        if (name.Contains("_L_") || name.Contains("_R_")) return true;
        if (name.Contains("_Left_") || name.Contains("_Right_")) return true;
        if (name.Contains("Left") || name.Contains("Right")) return true;
        if (name.EndsWith(".L") || name.EndsWith(".R")) return true;
        if (name.EndsWith("_L") || name.EndsWith("_R")) return true;
        return false;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest), overwrite: true);
    }

    // ════════════════════════════════════════════════════════════════
    // CUSTOM POSE LIBRARY — save / apply / delete handlers
    // ════════════════════════════════════════════════════════════════

    private async void OnSaveCurrentPose(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) { _vm.StatusText = "Viewer not ready."; return; }

        // Pull the current pose blob and pass it to the library service.
        var raw = await Viewport.CoreWebView2.ExecuteScriptAsync("window.getPose && window.getPose()");
        if (string.IsNullOrEmpty(raw) || raw == "null")
        {
            _vm.StatusText = "No pose to save — load a rigged model first.";
            return;
        }
        string? poseJson;
        try { poseJson = JsonSerializer.Deserialize<string>(raw); }
        catch { poseJson = raw; }
        if (string.IsNullOrWhiteSpace(poseJson)) { _vm.StatusText = "Pose blob was empty."; return; }

        // Prompt for a label. WPF's InputBox lives in VB-runtime land; we
        // use a tiny inline modal instead so the pose-tab UX stays
        // self-contained.
        var label = PromptText("Save pose as",
            "Give this pose a memorable name. It'll show up in the library list and can be re-applied later.",
            DefaultPoseLabel());
        if (string.IsNullOrWhiteSpace(label)) return;

        try
        {
            var sourceRig = string.IsNullOrEmpty(_vm.LoadedModelPath)
                ? null
                : Path.GetFileNameWithoutExtension(_vm.LoadedModelPath);
            var saved = Services.PoseLibraryService.Save(label!, poseJson!, sourceRig);
            RefreshCustomPoses();
            _vm.StatusText = $"Saved \"{saved.DisplayName}\" to library ({_vm.CustomPoses.Count} total).";
            AppendDebug("info", "pose", $"Saved pose '{saved.DisplayName}'",
                $"slug={saved.Slug} bones={saved.BoneCount}");
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Couldn't save pose: " + ex.Message;
            AppendDebug("err", "error", "Save pose failed", ex.Message);
        }
    }

    private async void OnApplyCustomPose(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        if (sender is not FrameworkElement fe || fe.Tag is not string slug) return;
        try
        {
            var json = Services.PoseLibraryService.Load(slug);
            // ExecuteScriptAsync stringifies its arg via JS literal — we
            // pass the JSON as a single-arg string, then unwrap on the
            // JS side. window.applyPose accepts either a string or an
            // object, so JS.parse round-trip is unnecessary.
            var encoded = JsonSerializer.Serialize(json);
            await Viewport.CoreWebView2.ExecuteScriptAsync($"window.applyPose && window.applyPose({encoded})");
            _vm.StatusText = $"Applied saved pose: {slug}";
            AppendDebug("info", "pose", $"Applied pose '{slug}'");
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Couldn't apply pose: " + ex.Message;
            AppendDebug("err", "error", "Apply pose failed", ex.Message);
        }
    }

    private void OnDeleteCustomPose(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string slug) return;
        var ok = AppDialog.Show(
            $"Delete saved pose \"{slug}\"? This cannot be undone.",
            "Delete pose",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.Yes) return;
        try
        {
            Services.PoseLibraryService.Delete(slug);
            RefreshCustomPoses();
            _vm.StatusText = $"Deleted saved pose: {slug}";
            AppendDebug("info", "pose", $"Deleted pose '{slug}'");
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Delete failed: " + ex.Message;
            AppendDebug("err", "error", "Delete pose failed", ex.Message);
        }
    }

    private void RefreshCustomPoses()
    {
        var entries = Services.PoseLibraryService.List();
        _vm.CustomPoses.Clear();
        foreach (var e in entries)
        {
            _vm.CustomPoses.Add(new ViewModels.SavedPoseEntry
            {
                Slug = e.Slug,
                DisplayName = e.DisplayName,
                SavedAt = e.SavedAt,
                SourceRig = e.SourceRig ?? "",
                BoneCount = e.BoneCount,
                FilePath = e.FilePath,
            });
        }
        _vm.NotifyCustomPosesChanged();
    }

    private string DefaultPoseLabel()
    {
        // Suggest a deterministic name based on the loaded model + a
        // timestamp suffix — the user usually wants to tweak it but a
        // sensible default beats a blank box.
        var stem = string.IsNullOrEmpty(_vm.LoadedModelPath)
            ? "pose"
            : Path.GetFileNameWithoutExtension(_vm.LoadedModelPath);
        return $"{stem} {DateTime.Now:HH:mm}";
    }

    // ════════════════════════════════════════════════════════════════
    // YCD IMPORT — load existing .ycd into the timeline
    // ════════════════════════════════════════════════════════════════

    private async void OnImportYcd(object sender, RoutedEventArgs e)
    {
        if (_ycdImportBusy) { _vm.StatusText = "Import already in progress…"; return; }
        if (!_webViewReady) { _vm.StatusText = "Viewer not ready."; return; }
        if (!_vm.HasRig)
        {
            _vm.StatusText = "Load a rigged model before importing a .ycd.";
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import animation (.ycd)",
            Filter = "RAGE animation (*.ycd)|*.ycd|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        await ImportYcdFileAsync(dlg.FileName);
    }

    /// <summary>Parse a .ycd off the UI thread, push it into the viewer,
    /// and auto-play — mirrors <see cref="ImportAnimationFileAsync"/>.</summary>
    private async Task ImportYcdFileAsync(string path)
    {
        if (_ycdImportBusy) return;
        _ycdImportBusy = true;
        try
        {
            _vm.StatusText = "Loading animation from .ycd…";
            Services.FosLogger.Info("dev", "YCD import starting: " + Path.GetFileName(path));
            await PumpUiAsync();

            var rigBoneNames = _vm.Bones.Select(b => b.Name ?? "").ToList();
            // Aux twist/corrective bones (RB_/MH_/PH_/IK_) are hidden from
            // the pose sidebar but real clips animate them — RB_* rolls
            // carry the limb twist; dropping those tracks tears the thigh
            // and arm bands. Offer them to the importer; the viewer binds
            // them through its aux-bone registry.
            rigBoneNames.AddRange(Services.GtaBoneTags.FreemodeAuxBoneNames);
            List<string> clips;
            Services.YcdImporter.ImportResult result;
            try
            {
                (clips, result) = await Task.Run(() =>
                    Services.YcdImporter.ImportFirstBundled(path, rigBoneNames));
            }
            catch (Exception ex)
            {
                _vm.StatusText = "Import failed: " + ex.Message;
                AppendDebug("err", "error", "YCD import failed", ex.Message);
                Services.FosLogger.Warn("dev", "YCD import failed", ex);
                return;
            }

            _vm.StatusText = $"Importing {Path.GetFileName(path)} into timeline…";
            await PumpUiAsync();
            Services.FosLogger.Info("dev",
                $"ycd import payload = {result.PayloadJson.Length:N0} chars, {result.MappedBones} bones, {result.KeyframeCount} preview frames @ {result.Fps}fps");

            if (result.MappedBones == 0)
            {
                _vm.StatusText = "YCD loaded but 0 bones matched your rig — use GTA Male/Female skeleton, then re-import.";
                AppendDebug("warn", "timeline", "YCD import: 0 bones mapped",
                    $"clip={result.ClipName} keyframes={result.KeyframeCount} unmappedTracks={result.UnmappedTracks}");
                return;
            }

            var clipName = LooksLikeHashName(result.ClipName)
                ? Path.GetFileNameWithoutExtension(path)
                : (result.ClipName ?? Path.GetFileNameWithoutExtension(path));
            if (!await ImportPayloadAsClipAsync(result.PayloadJson, clipName, editable: true))
                return;

            // Imported animations travel by default — only static poses stay in-place.
            _vm.MovementIndex = (int)Services.EmoteMovement.RootMotion;
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                "window.poseRestartPlay ? window.poseRestartPlay() : (window.poseSetTime(0), window.posePlay())");
            _vm.TimelinePlaying = true;
            _vm.TimelineTime = 0;
            UpdatePlayButtonVisual();

            var clipHint = clips.Count > 1 ? $" (first of {clips.Count} clips: {result.ClipName})" : "";
            _vm.StatusText = $"Imported {clipName} — playing.{clipHint}";
            AppendDebug("info", "timeline", "YCD imported",
                $"clip={result.ClipName} keyframes={result.KeyframeCount} mapped={result.MappedBones} unmapped={result.UnmappedTracks}");
        }
        finally
        {
            _ycdImportBusy = false;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // IMPORT ANIMATION (.glb/.gltf/.fbx/.dae) — absorbed from the retired
    // Animation → Emote tab. AnimEmoteImporter reads the clip and (for
    // foreign rigs) retargets it onto the GTA skeleton; the result ships
    // to the viewer through the same window.setKeyframes payload the .ycd
    // importer uses, so playback, tweaking and every export path behave
    // identically to an imported .ycd.
    // ════════════════════════════════════════════════════════════════

    private readonly Services.AnimEmoteImporter _animImporter = new();

    private async void OnImportAnimation(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) { _vm.StatusText = "Viewer not ready."; return; }
        if (!_vm.HasRig)
        {
            _vm.StatusText = "Load a rigged model before importing an animation.";
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import animation (.glb / .gltf / .fbx / .blend / .dae / .bvh / .package / .ycd / .json)",
            Filter = "Animations (*.glb;*.gltf;*.fbx;*.blend;*.dae;*.bvh;*.package;*.ycd;*.json)|*.glb;*.gltf;*.fbx;*.blend;*.dae;*.bvh;*.package;*.ycd;*.json|GTA animation (*.ycd)|*.ycd|Sims 4 package (*.package)|*.package|BVH mocap (*.bvh)|*.bvh|Physics mocap (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        // One entry point for every animation source: .ycd takes the
        // RAGE-native import path, everything else goes through retarget.
        if (Path.GetExtension(dlg.FileName).Equals(".ycd", StringComparison.OrdinalIgnoreCase))
            await ImportYcdFileAsync(dlg.FileName);
        else
            await ImportAnimationFileAsync(dlg.FileName);
    }

    // ════════════════════════════════════════════════════════════════
    // VIDEO → EMOTE / IMAGE → POSE (MediaPipe Pose Landmarker)
    // ════════════════════════════════════════════════════════════════

    // The retarget result BEFORE body calibration. Kept so the calibration
    // sliders can re-solve live: re-importing would mean re-running the import.
    private Services.AnimEmoteImporter.Result? _uncalibrated;
    private string _uncalibratedName = "";
    private int _calibrationGen;

    private Services.AnimEmoteImporter.Result ApplyCalibration(Services.AnimEmoteImporter.Result res)
    {
        var warnings = new List<string>();
        var tracks = Services.BodyCalibration.Apply(res.Tracks, _vm.Calibration, warnings);
        foreach (var w in warnings) AppendDebug("warn", "timeline", "Body calibration", w);
        return res with { Tracks = tracks };
    }

    /// <summary>Re-solve the calibration against the cached raw retarget and push
    /// it to the viewer. Debounced: the sliders fire continuously while dragged
    /// and each solve FKs the whole clip.</summary>
    private async void OnCalibrationChanged()
    {
        if (_uncalibrated is null || !_webViewReady) return;
        var gen = ++_calibrationGen;
        await Task.Delay(120);
        if (gen != _calibrationGen) return;      // superseded by a newer drag
        try
        {
            var res = ApplyCalibration(_uncalibrated);
            var rigBoneNames = _vm.Bones.Select(b => b.Name ?? "").ToList();
            var payload = BuildAnimKeyframePayload(res, rigBoneNames, out var mapped);
            if (payload is null || mapped == 0) return;
            await ApplyImportedAnimAsync(res, payload, mapped, _uncalibratedName);
        }
        catch (Exception ex)
        {
            AppendDebug("err", "error", "Calibration re-solve failed", ex.ToString());
        }
    }

    /// <summary>Import a local animation file (.fbx/.glb/…) onto the timeline.
    /// Returns true only when the clip is on the timeline and ready to preview.</summary>
    public async Task<bool> ImportAnimationFileAsync(string path)
    {
        SetImportOverlay(on: true, "Importing animation.");
        _vm.StatusText = "Importing animation.";
        try
        {
            await PumpUiAsync();
            int simsClipIndex;
            if (path.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
            {
                List<Services.Sims.SimsClipDecoder.ClipInfo> list;
                try
                {
                    list = await Task.Run(() => AnimationContainerResolver.ListSimsClips(path).ToList());
                }
                catch (Exception ex)
                {
                    _vm.StatusText = "Couldn't read Sims package: " + ex.Message;
                    AppendDebug("err", "error", "Sims package list failed", ex.Message);
                    FosLogger.Warn("import", "Sims package list failed: " + ex.Message);
                    return false;
                }
                if (list.Count == 0)
                {
                    _vm.StatusText = "No CLIP animations found in this Sims 4 .package.";
                    return false;
                }
                simsClipIndex = 0;
                if (list.Count > 1)
                {
                    SetImportOverlay(on: false);
                    SimsClipPickerWindow simsClipPickerWindow = new SimsClipPickerWindow(list)
                    {
                        Owner = Window.GetWindow((DependencyObject)this)
                    };
                    if (((Window)simsClipPickerWindow).ShowDialog() == true)
                    {
                        int? selectedIndex = simsClipPickerWindow.SelectedIndex;
                        if (selectedIndex.HasValue)
                        {
                            int valueOrDefault = selectedIndex.GetValueOrDefault();
                            simsClipIndex = valueOrDefault;
                            SetImportOverlay(on: true, "Importing Sims clip.");
                            await PumpUiAsync();
                            goto IL_0363;
                        }
                    }
                    _vm.StatusText = "Import cancelled.";
                    return false;
                }
                SetImportOverlay(on: true, "Importing Sims clip.");
                await PumpUiAsync();
                goto IL_0363;
            }
            AnimEmoteImporter.Result res = null;
            if (Services.PhysicsMocapImporter.LooksLikePhysicsMocap(path))
            {
                try
                {
                    await Task.Run(() => res = Services.PhysicsMocapImporter.Import(path));
                }
                catch (Exception exPhys)
                {
                    res = AnimEmoteImporter.Result.Fail("Physics mocap import failed: " + exPhys.Message);
                }
                if (res == null || !res.Success)
                {
                    _vm.StatusText = res?.Error ?? "Physics mocap import failed.";
                    AppendDebug("err", "error", "Physics mocap import failed", res?.Error ?? "(no result)");
                    FosLogger.Warn("import", _vm.StatusText);
                    return false;
                }
                foreach (string warning in res.Warnings)
                    AppendDebug("warn", "timeline", "Physics mocap", warning);
                _uncalibrated = res;
                _uncalibratedName = System.IO.Path.GetFileName(path);
                res = ApplyCalibration(res);
                List<string> physBones = _vm.Bones.Select((PoseBoneEntry b) => b.Name ?? "").ToList();
                int physMapped;
                string physPayload = BuildAnimKeyframePayload(res, physBones, out physMapped);
                if (physPayload == null || physMapped == 0)
                {
                    _vm.StatusText = "No bones in this physics mocap map onto the loaded rig.";
                    FosLogger.Warn("import", _vm.StatusText);
                    return false;
                }
                return await ApplyImportedAnimAsync(res, physPayload, physMapped, System.IO.Path.GetFileName(path));
            }
            AnimationContainerResolver.ResolveResult resolveResult = await Task.Run(() => AnimationContainerResolver.Resolve(path));
            if (!resolveResult.Success)
            {
                _vm.StatusText = resolveResult.Error ?? "Import failed.";
                AppendDebug("err", "error", "Animation container resolve failed", resolveResult.Error ?? "");
                FosLogger.Warn("import", _vm.StatusText);
                return false;
            }
            if (!string.IsNullOrWhiteSpace(resolveResult.Note))
            {
                AppendDebug("info", "timeline", "Container resolved", resolveResult.Note);
            }
            path = resolveResult.Path;
            try
            {
                await Task.Run(() => res = _animImporter.Import(path));
            }
            catch (Exception ex2)
            {
                res = AnimEmoteImporter.Result.Fail("Import failed: " + ex2.Message);
            }
            if (res == null || !res.Success)
            {
                _vm.StatusText = res?.Error ?? "Import failed.";
                AppendDebug("err", "error", "Animation import failed", res?.Error ?? "(no result)");
                FosLogger.Warn("import", "Animation import failed: " + (_vm.StatusText));
                return false;
            }
            _uncalibrated = res;
            _uncalibratedName = System.IO.Path.GetFileName(path);
            res = ApplyCalibration(res);
            List<string> rigBoneNames = _vm.Bones.Select((PoseBoneEntry b) => b.Name ?? "").ToList();
            int mappedBones;
            string text = BuildAnimKeyframePayload(res, rigBoneNames, out mappedBones);
            if (text == null || mappedBones == 0)
            {
                _vm.StatusText = "No bones in this clip map onto the loaded rig.";
                AppendDebug("warn", "timeline", "Animation import: 0 bones mapped", System.IO.Path.GetFileName(path));
                FosLogger.Warn("import", _vm.StatusText + " — " + System.IO.Path.GetFileName(path));
                return false;
            }
            // A travelling clip still imports its mover, but MOVEMENT no longer
            // offers root motion, so there is no mode to switch to — the export
            // plays it in place. Flag it rather than let the ped moonwalk with
            // no explanation.
            IReadOnlyList<System.Numerics.Vector3> rootMotion = res.RootMotion;
            if (rootMotion != null && rootMotion.Count > 0)
            {
                AppendDebug("warn", "timeline", "Clip carries root motion",
                    "The ped will play this in place — root motion export is not available.");
            }
            return await ApplyImportedAnimAsync(res, text, mappedBones, System.IO.Path.GetFileName(path));
            IL_0363:
            AnimEmoteImporter.Result simsRes = null;
            try
            {
                await Task.Run(() => simsRes = Services.Sims.SimsEmoteImporter.Import(path, simsClipIndex));
            }
            catch (Exception ex3)
            {
                simsRes = AnimEmoteImporter.Result.Fail("Sims import failed: " + ex3.Message);
            }
            if (simsRes == null || !simsRes.Success)
            {
                _vm.StatusText = simsRes?.Error ?? "Sims import failed.";
                AppendDebug("err", "error", "Sims import failed", simsRes?.Error ?? "(no result)");
                FosLogger.Warn("import", _vm.StatusText);
                return false;
            }
            AppendDebug("info", "timeline", "Sims CLIP imported", $"{simsRes.ClipName} · {simsRes.MappedBones.Count} bones · {simsRes.Frames}f @{simsRes.Fps}fps");
            foreach (string warning in simsRes.Warnings)
            {
                AppendDebug("warn", "timeline", "Sims import", warning);
            }
            List<string> rigBoneNames2 = _vm.Bones.Select((PoseBoneEntry b) => b.Name ?? "").ToList();
            int mappedBones2;
            string text2 = BuildAnimKeyframePayload(simsRes, rigBoneNames2, out mappedBones2);
            if (text2 == null || mappedBones2 == 0)
            {
                _vm.StatusText = "No bones in this Sims clip map onto the loaded rig.";
                FosLogger.Warn("import", _vm.StatusText);
                return false;
            }
            rootMotion = simsRes.RootMotion;
            return await ApplyImportedAnimAsync(simsRes, text2, mappedBones2, System.IO.Path.GetFileName(path));
        }
        catch (Exception ex4)
        {
            _vm.StatusText = "Import failed: " + ex4.Message;
            AppendDebug("err", "error", "Animation import exception", ex4.ToString());
            FosLogger.Warn("import", "Animation import exception", ex4);
            return false;
        }
        finally
        {
            SetImportOverlay(on: false);
        }
    }

    private async Task<bool> ApplyImportedAnimAsync(AnimEmoteImporter.Result res, string payloadJson, int mappedBones, string displayName)
    {
        SetImportOverlay(on: true, "Pushing animation into timeline.");
        _vm.StatusText = "Pushing animation into timeline.";
        await PumpUiAsync();
        FosLogger.Info("dev", $"import clip payload = {payloadJson.Length:N0} chars, {mappedBones} bones");
        string clipName = res.ClipName ?? System.IO.Path.GetFileNameWithoutExtension(displayName);
        if (!(await ImportPayloadAsClipAsync(payloadJson, clipName)))
        {
            return false;
        }
        // Imported animations default to Root motion (ped travels): the baked
        // mover is applied so the ped physically moves. Switch Movement → In Place
        // to keep it centred (preview + export).
        _vm.MovementIndex = (int)Services.EmoteMovement.RootMotion;
        // Land on frame 0, PAUSED. Imported clips — Sims pose packs especially
        // — are multi-second animations that move THROUGH poses; the old
        // auto-play dropped the user on an arbitrary mid-motion frame that read
        // as a broken/contorted pose (the ped isn't broken, the frame just
        // isn't the pose). Frame 0 is the clean start; poseSetTime evaluates the
        // strip so the ped is actually posed there, not left at bind. The user
        // presses Space to preview or scrubs to the frame they want.
        await Viewport.CoreWebView2.ExecuteScriptAsync("window.posePause && window.posePause()");
        await Viewport.CoreWebView2.ExecuteScriptAsync("window.poseSetTime && window.poseSetTime(0)");
        string value = res.Rig switch
        {
            AnimEmoteImporter.RigKind.GtaRig => "GTA skeleton (exact copy)", 
            AnimEmoteImporter.RigKind.Mixamo => "Mixamo rig (retargeted)", 
            _ => "generic / Sims rig (retargeted)", 
        };
        string value2 = ((res.Warnings.Count > 0) ? $" � {res.Warnings.Count} warning(s) - see Debug" : "");
        string value3 = "";
        // Pull the input-health line out of the warnings so it shows in the status
        // bar itself, not just the Debug panel — it's the one the user most needs
        // to see: was this a good file to import, and if not, how to fix the export.
        string? inputMsg = res.Warnings.FirstOrDefault(w => w.StartsWith("Input check:", StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(inputMsg)) inputMsg = " — " + inputMsg.Substring("Input check:".Length).Trim();
        IReadOnlyList<System.Numerics.Vector3> rootMotion = res.RootMotion;
        if (rootMotion != null && rootMotion.Count > 0)
        {
            float num = 0f;
            System.Numerics.Vector3 value4 = res.RootMotion[0];
            foreach (System.Numerics.Vector3 item in res.RootMotion)
            {
                num = Math.Max(num, System.Numerics.Vector3.Distance(item, value4));
            }
            value3 = ((!(num > 0.15f)) ? " — mostly in place (switch Movement → In Place if the feet slide in-game)." : $" — Root motion ON (~{num:F1}m travel): export as a FiveM resource (.fxresource), not a dpemotes zip.");
        }
        double clipSeconds = res.Fps > 0 ? (double)res.Frames / res.Fps : 0;
        _vm.StatusText = $"Imported {res.ClipName} — {clipSeconds:F1}s animation ({res.Frames} frames @ {res.Fps} fps, {value}), paused at the start. Press Space to play or scrub to a pose.{inputMsg}{value2}{value3}";
        AppendDebug("info", "timeline", "Animation imported", $"clip={res.ClipName} rig={res.Rig} frames={res.Frames} fps={res.Fps} mapped={mappedBones} skipped={res.UnmappedBones.Count}");
        foreach (string warning in res.Warnings)
        {
            AppendDebug("warn", "timeline", "Import warning", warning);
        }
        if (res.DurationSeconds > 60.0)
        {
            AppendDebug("warn", "timeline", "Clip longer than the 60s timeline cap", $"{res.DurationSeconds:F1}s - trailing frames are clamped.");
        }
        return true;
    }

    private static string? BuildAnimKeyframePayload(AnimEmoteImporter.Result res, IReadOnlyList<string> rigBoneNames, out int mappedBones)
    {
        mappedBones = 0;
        Dictionary<ushort, string> tagToName = new Dictionary<ushort, string>();
        foreach (string rigBoneName in rigBoneNames)
        {
            if (GtaBoneTags.TryResolve(rigBoneName, out var tag) && !tagToName.ContainsKey(tag))
                tagToName[tag] = rigBoneName;
        }

        // Retarget emits Assimp GAME_RIG parent-locals. THREE.js freemode
        // bone.quaternion rest ≠ Assimp RestRot for the same glb (see
        // YcdBoneSpaceConverter), so shipping raw quats as boneSpace=glb melts
        // the ped. Emit bone-tracks + Assimp binds; the viewer rebases with
        // live poseInitialQuats: qThree = restThree * inv(assimpBind) * qAssimp.
        var boneTracks = new List<(string Name, List<double[]> Q)>();
        var assimpBind = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (PosedBoneTrack track in res.Tracks)
        {
            if (track.BoneTag == 0 || track.PerFrame.Length == 0) continue;
            if (!tagToName.TryGetValue(track.BoneTag, out var name)) continue;

            var qList = new List<double[]>(track.PerFrame.Length);
            foreach (var q in track.PerFrame)
            {
                qList.Add(new[]
                {
                    Math.Round(q.X, 5),
                    Math.Round(q.Y, 5),
                    Math.Round(q.Z, 5),
                    Math.Round(q.W, 5),
                });
            }
            boneTracks.Add((name, qList));

            if (YcdBoneSpaceConverter.TryGetGlbBind(track.BoneTag) is { } bind)
            {
                assimpBind[name] = new[]
                {
                    Math.Round(bind.X, 6),
                    Math.Round(bind.Y, 6),
                    Math.Round(bind.Z, 6),
                    Math.Round(bind.W, 6),
                };
            }
        }

        if (boneTracks.Count == 0) return null;
        mappedBones = boneTracks.Count;

        int fps = Math.Clamp(res.Fps, 1, 120);
        int frames = Math.Max(1, res.Frames);
        double duration = Math.Round((double)Math.Max(1, frames - 1) / fps, 3);

        List<double[]>? root = null;
        if (res.RootMotion is { Count: > 0 } rm)
        {
            root = new List<double[]>(frames);
            for (int i = 0; i < frames; i++)
            {
                var v = rm[Math.Min(i, rm.Count - 1)];
                root.Add(new[]
                {
                    Math.Round(Math.Clamp(v.X, -6f, 6f), 4),
                    Math.Round(Math.Clamp(v.Y, -6f, 6f), 4),
                    Math.Round(Math.Clamp(v.Z, -6f, 6f), 4),
                });
            }
        }

        // EDITABLE keyframes (2026-07-16): every frame becomes a real pose
        // keyframe via window.setKeyframes, so the imported animation is
        // draggable / deletable / recordable-over / undoable on the layers
        // timeline. The old 'bone-tracks' clip-strip format made imports a
        // sealed player that stomped edits and showed zero keys.
        var keyframes = new List<object>(frames);
        for (int f = 0; f < frames; f++)
        {
            var bones = new List<object>(boneTracks.Count);
            foreach (var (name, q) in boneTracks)
                bones.Add(new { name, q = q[Math.Min(f, q.Count - 1)] });
            double[]? rootAt = root != null ? root[Math.Min(f, root.Count - 1)] : null;
            // ease=linear: per-frame mocap keys need plain slerp between
            // neighbours (what the old clip player did). The default 'auto'
            // ease runs SQUAD spline interpolation, which overshoots on dense
            // noisy data — visibly mangling fingers between keys.
            keyframes.Add(new { time = Math.Round((double)f / fps, 3), bones, root = rootAt, ease = "linear" });
        }

        return JsonSerializer.Serialize(new
        {
            duration,
            fps,
            source = "anim-import",
            // Assimp GAME_RIG locals — viewer rebases onto THREE rest before play.
            boneSpace = "assimp",
            format = "keyframes-editable",
            // Preview travels by default (matches the Root motion default). The
            // Movement dropdown → In Place holds the ped centred; the vertical
            // hop/crouch always shows regardless.
            rootPreview = true,
            assimpBind,
            keyframes,
        }, new JsonSerializerOptions { WriteIndented = false });
    }

    public async Task DevImportAnimationAsync(string path)
    {
        for (int i = 0; i < 100; i++)
        {
            if (_webViewReady && _vm.HasRig)
            {
                break;
            }
            await Task.Delay(200);
        }
        if (!_webViewReady || !_vm.HasRig)
        {
            FosLogger.Warn("dev", "autoimport: viewer/rig never became ready");
            return;
        }
        await Task.Delay(4000);
        await ImportAnimationFileAsync(path);
    }

    public void DevSetMovement(int index)
    {
        _vm.MovementIndex = Math.Clamp(index, 0, (int)Services.EmoteMovement.RootMotion);
    }

    private async void OnUndoPose(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        await Viewport.CoreWebView2.ExecuteScriptAsync("window.hostUndo && window.hostUndo()");
        await RefreshHistoryDepth();
    }

    private async void OnRedoPose(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        await Viewport.CoreWebView2.ExecuteScriptAsync("window.hostRedo && window.hostRedo()");
        await RefreshHistoryDepth();
    }

    /// <summary>Pull the viewer's current history depth and stamp it on
    /// the VM so the Undo/Redo buttons can disable themselves at the
    /// stack boundaries.</summary>
    private async Task RefreshHistoryDepth()
    {
        try
        {
            var raw = await Viewport.CoreWebView2.ExecuteScriptAsync("window.poseHistoryDepth && JSON.stringify(window.poseHistoryDepth())");
            if (string.IsNullOrEmpty(raw) || raw == "null") return;
            string? json;
            try { json = JsonSerializer.Deserialize<string>(raw); }
            catch { json = raw; }
            if (string.IsNullOrEmpty(json)) return;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("undo", out var uEl)) _vm.UndoDepth = uEl.GetInt32();
            if (doc.RootElement.TryGetProperty("redo", out var rEl)) _vm.RedoDepth = rEl.GetInt32();
        }
        catch { /* silent — depth display is best-effort */ }
    }


    private void OnShowSkippedReport(object sender, RoutedEventArgs e)
    {
        if (_vm.LastExportSkipped == 0)
        {
            AppDialog.Show("No skipped bones in the most recent export — everything mapped cleanly.",
                "Export report", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var body =
            $"Mapped bones: {_vm.LastExportMapped}\n" +
            $"Unmapped bones: {_vm.LastExportSkipped}\n\n" +
            "Unmapped names (these stayed at bind pose in the .ycd):\n" +
            (string.IsNullOrEmpty(_vm.LastExportSkippedNames) ? "(none recorded)" : _vm.LastExportSkippedNames);
        AppDialog.Show(body, "Last export report", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Report a host-side event into the viewer's corner ticker.
    ///
    /// This used to append to a VM collection that only the floating debug
    /// popup rendered; with that popup gone, the ticker is the one debug
    /// surface, so C# events are pushed into the same ring buffer the
    /// viewer's own telemetry uses. Viewer-originated entries must NOT come
    /// back through here — they are already in that buffer, and re-injecting
    /// them would both double every line and feed itself.
    ///
    /// Fire-and-forget: a dropped debug line is never worth throwing over,
    /// and anything logged before the viewer is up has nowhere to go.</summary>
    private void AppendDebug(string level, string category, string text, string payload = "")
    {
        if (!_webViewReady) return;
        var fn = level switch { "err" => "err", "warn" => "warn", _ => "info" };
        var catJs = JsonSerializer.Serialize(category);
        var textJs = JsonSerializer.Serialize(text);
        var payloadJs = string.IsNullOrEmpty(payload) ? "undefined" : JsonSerializer.Serialize(payload);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _ = Viewport.CoreWebView2.ExecuteScriptAsync(
                    $"window.fosTelemetry && window.fosTelemetry.{fn}({catJs}, {textJs}, {payloadJs})");
            }
            catch { /* viewer torn down mid-flight */ }
        }));
    }

    // ════════════════════════════════════════════════════════════════
    // Inline modal text prompt — used by Save Pose. WPF doesn't ship
    // an InputBox; this is a tiny purpose-built one rather than dragging
    // in Microsoft.VisualBasic just for one dialog.
    // ════════════════════════════════════════════════════════════════
    private string? PromptText(string title, string description, string defaultText)
    {
        var win = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = Window.GetWindow(this),
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            FontSize = 12,
            Opacity = 0.75,
        });
        var input = new TextBox { Text = defaultText, MinWidth = 320 };
        panel.Children.Add(input);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new Button { Content = "Save", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        win.Content = panel;

        bool accepted = false;
        ok.Click += (_, __) => { accepted = true; win.Close(); };
        input.Focus();
        input.SelectAll();
        win.ShowDialog();
        return accepted ? input.Text : null;
    }

    // ── File menu entry points (MainWindow → Emote workspace) ────────

    public void RunOpenRiggedModel() => OnOpenRiggedModel(this, new RoutedEventArgs());
    public void RunLoadGtaMale() => OnLoadGtaMale(this, new RoutedEventArgs());
    public void RunLoadGtaFemale() => OnLoadGtaFemale(this, new RoutedEventArgs());

    /// <summary>Deep-link: open the right-side Animation Library and index.</summary>
    public void OpenAnimLibraryMode()
    {
        _vm.IsAnimLibraryOpen = true;
        _vm.IsMotionPanelSelected = false;
        _ = EnsureAnimLibraryLoadedAsync();
    }

    private void OnAnimLibraryToggleClick(object sender, RoutedEventArgs e)
    {
        _vm.IsAnimLibraryOpen = !_vm.IsAnimLibraryOpen;
        if (_vm.IsAnimLibraryOpen)
            _ = EnsureAnimLibraryLoadedAsync();
    }

    private void OnAnimLibraryResize(object sender, DragDeltaEventArgs e)
    {
        // Grip is on the left edge — drag left (negative) grows the panel.
        double next = _vm.AnimLibraryOpenWidth - e.HorizontalChange;
        _vm.AnimLibraryOpenWidth = Math.Clamp(next, 280, 640);
    }

    private void OnAnimLibrarySearchChanged(object sender, TextChangedEventArgs e)
        => _vm.ApplyAnimLibraryFilter();

    private void OnAnimLibraryLoadMore(object sender, RoutedEventArgs e)
        => _vm.LoadMoreAnimLibrary();

    private async Task EnsureAnimLibraryLoadedAsync()
    {
        if (_vm.AnimLibraryTotalCount > 0 && AnimRpfIndex.Status == AnimRpfIndex.State.Ready)
            return;

        _vm.AnimLibraryLoading = true;
        _vm.AnimLibraryStatus = "Scanning GTA V for animation dictionaries…";
        RefreshAnimLibraryGtaLabel();

        bool ok = await AnimRpfIndex.EnsureLoadedAsync();
        _vm.AnimLibraryLoading = false;

        if (!ok)
        {
            _vm.AnimLibraryStatus = AnimRpfIndex.Error
                ?? "No GTA V folder found — set it to browse game animations.";
            _vm.AnimLibraryTotalCount = 0;
            _vm.AnimLibraryAll.Clear();
            _vm.AnimLibraryFiltered.Clear();
            _vm.AnimLibraryCategories.Clear();
            RefreshAnimLibraryGtaLabel();
            return;
        }

        _vm.AnimLibraryAll.Clear();
        foreach (var e in AnimRpfIndex.Dictionaries)
            _vm.AnimLibraryAll.Add(e);
        _vm.AnimLibraryTotalCount = _vm.AnimLibraryAll.Count;
        _vm.RebuildAnimLibraryCategories();
        _vm.ApplyAnimLibraryFilter();
        _vm.AnimLibraryStatus = $"{_vm.AnimLibraryTotalCount:N0} dictionaries indexed.";
        RefreshAnimLibraryGtaLabel();
        _vm.StatusText = "Animation Library ready — pick a dictionary and clip.";
    }

    private void RefreshAnimLibraryGtaLabel()
    {
        var folder = AnimRpfIndex.GtaFolder ?? GtaInstall.Resolve();
        _vm.AnimLibraryGtaFolderLabel = GtaInstall.IsValidFolder(folder)
            ? folder!
            : "GTA V folder not set";
    }

    private async void OnAnimLibrarySetGtaFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Pick your GTA V folder (the one with GTA5.exe)",
            InitialDirectory = AnimRpfIndex.GtaFolder
                ?? GtaInstall.Resolve()
                ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        if (dlg.ShowDialog() != true) return;
        if (!GtaInstall.IsValidFolder(dlg.FolderName))
        {
            _vm.AnimLibraryStatus = "That folder has no GTA5.exe — pick the game's install folder.";
            return;
        }

        UserSettings.SaveGtaFolder(dlg.FolderName);
        AnimRpfIndex.Reset(dlg.FolderName);
        GameTextureCache.Reset(dlg.FolderName);
        _vm.AnimLibraryTotalCount = 0;
        _vm.AnimLibraryAll.Clear();
        _vm.AnimLibraryFiltered.Clear();
        _vm.AnimLibraryClips.Clear();
        _vm.NotifyAnimLibraryClipsChanged();
        _animLibDictBytes = null;
        _animLibDictFileName = null;
        await EnsureAnimLibraryLoadedAsync();
    }

    private async void OnAnimLibraryDictSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_vm.SelectedAnimDict is not { } dict) return;
        await LoadAnimLibraryDictAsync(dict);
    }

    private CancellationTokenSource? _animLibScanCts;

    private async void OnAnimLibraryScanWorking(object sender, RoutedEventArgs e)
    {
        if (_vm.AnimLibraryScanning || _vm.AnimLibraryAll.Count == 0) return;

        _animLibScanCts?.Cancel();
        _animLibScanCts = new CancellationTokenSource();
        var ct = _animLibScanCts.Token;

        _vm.AnimLibraryScanning = true;
        _vm.NotifyAnimLibraryScanChanged();
        _vm.AnimLibraryStatus = "Scanning for playable clips…";

        var dicts = _vm.AnimLibraryAll.ToList();
        var working = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int ok = 0, bad = 0;

        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < dicts.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var d = dicts[i];
                    try
                    {
                        var bytes = AnimRpfIndex.ExtractBytes(d);
                        if (YcdImporter.HasPlayableClip(bytes, d.FileName))
                        {
                            lock (working) working.Add(d.Name);
                            Interlocked.Increment(ref ok);
                        }
                        else Interlocked.Increment(ref bad);
                    }
                    catch
                    {
                        Interlocked.Increment(ref bad);
                    }

                    if (i % 20 == 0 || i == dicts.Count - 1)
                    {
                        var n = i + 1;
                        var status = $"Scanning… {n:N0}/{dicts.Count:N0}  ({ok:N0} working)";
                        Dispatcher.Invoke(() => _vm.AnimLibraryStatus = status);
                    }
                }
            }, ct);

            _vm.AnimLibraryWorkingDicts = working;
            _vm.AnimLibraryWorkingOnly = true;
            _vm.RebuildAnimLibraryCategories();
            _vm.ApplyAnimLibraryFilter();
            _vm.AnimLibraryStatus =
                $"Scan done — {working.Count:N0} working dictionaries, {bad:N0} skipped.";
            _vm.StatusText = _vm.AnimLibraryStatus;

            if (_vm.SelectedAnimDict is { } sel)
                await LoadAnimLibraryDictAsync(sel);
        }
        catch (OperationCanceledException)
        {
            _vm.AnimLibraryStatus = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            _vm.AnimLibraryStatus = "Scan failed: " + ex.Message;
            FosLogger.Warn("anim-library", "working scan failed", ex);
        }
        finally
        {
            _vm.AnimLibraryScanning = false;
            _vm.NotifyAnimLibraryScanChanged();
        }
    }

    private async Task LoadAnimLibraryDictAsync(AnimDictEntry dict)
    {
        _vm.AnimLibraryClips.Clear();
        _vm.NotifyAnimLibraryClipsChanged();
        _vm.SelectedAnimClip = null;
        _vm.AnimLibraryClipReady = false;
        _vm.AnimLibraryDurationLabel = "Max duration: —";
        _vm.AnimLibraryTracksLabel = "Total tracks: —";
        _animLibDictBytes = null;
        _animLibDictFileName = null;

        try
        {
            _vm.StatusText = $"Loading {dict.FileName}…";
            var bytes = await Task.Run(() => AnimRpfIndex.ExtractBytes(dict));
            var workingOnly = _vm.AnimLibraryWorkingOnly;
            var clips = await Task.Run(() => workingOnly
                ? YcdImporter.ListPlayableClips(bytes, dict.FileName)
                : YcdImporter.ListClips(bytes, dict.FileName));
            _animLibDictBytes = bytes;
            _animLibDictFileName = dict.FileName;

            foreach (var c in clips.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
                _vm.AnimLibraryClips.Add(c);
            _vm.NotifyAnimLibraryClipsChanged();

            if (clips.Count == 0)
            {
                _vm.StatusText = workingOnly
                    ? $"{dict.Name} — no playable clips (broken / unresolved)."
                    : $"{dict.Name} — no clips found.";
            }
            else
            {
                _vm.StatusText = workingOnly
                    ? $"{dict.Name} — {clips.Count} playable clip(s)."
                    : $"{dict.Name} — {clips.Count} clip(s).";
            }

            if (clips.Count == 1)
                _vm.SelectedAnimClip = clips[0];
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Couldn't load dictionary: " + ex.Message;
            FosLogger.Warn("anim-library", "dict load failed: " + dict.FileName, ex);
        }
    }

    private async void OnAnimLibraryClipSelected(object sender, SelectionChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.SelectedAnimClip)) return;
        await PreviewAnimLibraryClipAsync(play: true);
    }

    /// <summary>
    /// Record one loop of the library preview from the WebGL viewport and
    /// save it as an animated GIF (Magick.NET). Frames stay in the viewer
    /// and are pulled one-by-one after capture (postMessage can't carry
    /// multi-MB JPEG payloads reliably).
    /// </summary>
    private async void OnAnimLibraryRecordGif(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady)
        {
            _vm.StatusText = "Viewer not ready.";
            return;
        }
        if (_vm.IsRecordingPreviewGif) return;
        if (_vm.SelectedAnimDict is null || string.IsNullOrEmpty(_vm.SelectedAnimClip))
        {
            _vm.StatusText = "Select a clip first.";
            return;
        }

        _vm.IsRecordingPreviewGif = true;
        _gifRecordTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            if (!_vm.AnimLibraryClipReady)
                await PreviewAnimLibraryClipAsync(play: false);

            if (!_vm.AnimLibraryClipReady)
            {
                _vm.StatusText = "Preview the clip before recording a GIF.";
                return;
            }

            await Viewport.CoreWebView2.ExecuteScriptAsync("window.posePause && window.posePause()");
            _vm.TimelinePlaying = false;
            UpdatePlayButtonVisual();

            _vm.StatusText = "Recording GIF…";
            _ = Viewport.CoreWebView2.ExecuteScriptAsync(
                "window.recordPreviewGif && window.recordPreviewGif({fps:12,width:480,maxSeconds:8})");

            bool ok;
            try
            {
                ok = await _gifRecordTcs.Task.WaitAsync(TimeSpan.FromMinutes(2));
            }
            catch (TimeoutException)
            {
                try
                {
                    await Viewport.CoreWebView2.ExecuteScriptAsync(
                        "window.cancelPreviewGif && window.cancelPreviewGif()");
                }
                catch { /* ignore */ }
                _vm.StatusText = "GIF recording timed out.";
                return;
            }

            if (!ok)
            {
                if (string.IsNullOrEmpty(_vm.StatusText)
                    || _vm.StatusText.StartsWith("Recording GIF", StringComparison.Ordinal))
                    _vm.StatusText = "GIF recording failed.";
                return;
            }

            _vm.StatusText = "Collecting GIF frames…";
            var frames = await PullGifFramesFromViewerAsync();
            try
            {
                await Viewport.CoreWebView2.ExecuteScriptAsync(
                    "window.gifClearFrames && window.gifClearFrames()");
            }
            catch { /* ignore */ }

            if (frames.Count == 0)
            {
                _vm.StatusText = "GIF recording failed — no frames captured.";
                return;
            }

            var safeDict = SanitizeFileToken(_vm.SelectedAnimDict.Name);
            var safeClip = SanitizeFileToken(_vm.SelectedAnimClip);
            var dlg = new SaveFileDialog
            {
                Title = "Save preview GIF",
                Filter = "Animated GIF (*.gif)|*.gif",
                FileName = $"{safeDict}_{safeClip}.gif",
                DefaultExt = ".gif",
            };
            if (dlg.ShowDialog() != true)
            {
                _vm.StatusText = "GIF save cancelled.";
                return;
            }

            _vm.StatusText = $"Encoding GIF… ({frames.Count} frames)";
            var fps = _gifRecordFps;
            var outPath = dlg.FileName;
            await Task.Run(() => PreviewGifEncoder.WriteAnimatedGif(frames, fps, outPath));
            _vm.StatusText = $"Saved GIF: {Path.GetFileName(outPath)} ({frames.Count} frames)";
            try { Process.Start(new ProcessStartInfo(outPath) { UseShellExecute = true }); }
            catch { /* ignore — file is still saved */ }
        }
        catch (Exception ex)
        {
            _vm.StatusText = "GIF failed: " + ex.Message;
            FosLogger.Warn("gif", "preview GIF failed", ex);
        }
        finally
        {
            _vm.IsRecordingPreviewGif = false;
            _gifRecordTcs = null;
        }
    }

    /// <summary>Pull JPEG frames buffered in the viewer after a successful
    /// gif-record-done. One frame per script call keeps results under
    /// WebView2 size limits.</summary>
    private async Task<List<byte[]>> PullGifFramesFromViewerAsync()
    {
        var frames = new List<byte[]>();
        if (!_webViewReady) return frames;

        var countRaw = await Viewport.CoreWebView2.ExecuteScriptAsync(
            "window.gifFrameCount ? window.gifFrameCount() : 0");
        if (!int.TryParse(countRaw?.Trim('"'), out var count) || count <= 0)
            return frames;

        for (int i = 0; i < count; i++)
        {
            if ((i % 8) == 0 || i == count - 1)
                _vm.StatusText = $"Collecting GIF frames… {i + 1}/{count}";

            var raw = await Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.gifTakeFrame ? window.gifTakeFrame({i}) : null");
            if (string.IsNullOrWhiteSpace(raw) || raw == "null")
                continue;

            string? b64;
            try { b64 = System.Text.Json.JsonSerializer.Deserialize<string>(raw); }
            catch { b64 = null; }
            if (string.IsNullOrEmpty(b64)) continue;

            try { frames.Add(Convert.FromBase64String(b64)); }
            catch { /* skip corrupt frame */ }
        }

        return frames;
    }

    private static string SanitizeFileToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "preview";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars);
        return string.IsNullOrWhiteSpace(s) ? "preview" : s;
    }

    private async Task PreviewAnimLibraryClipAsync(bool play)
    {
        if (_vm.SelectedAnimDict is null || string.IsNullOrEmpty(_vm.SelectedAnimClip)) return;

        // Already importing — queue the latest click and cancel applying the
        // in-flight result so rapid browsing always lands on the last clip.
        if (_animLibBusy)
        {
            _animLibPendingClip = _vm.SelectedAnimClip;
            _animLibPendingPlay = play;
            Interlocked.Increment(ref _animLibPreviewGen);
            return;
        }

        if (_animLibDictBytes is null || string.IsNullOrEmpty(_animLibDictFileName))
        {
            await LoadAnimLibraryDictAsync(_vm.SelectedAnimDict);
            if (_animLibDictBytes is null) return;
        }

        int gen = Interlocked.Increment(ref _animLibPreviewGen);
        _animLibBusy = true;
        _animLibPendingClip = null;
        try
        {
            if (!_webViewReady) await InitWebViewAsync();
            if (!_vm.HasRig)
            {
                _vm.StatusText = "Loading GTA Male for preview…";
                await LoadGtaPresetAsync("male");
                for (int i = 0; i < 80 && !_vm.HasRig; i++)
                    await Task.Delay(100);
                if (!_vm.HasRig)
                {
                    _vm.StatusText = "Rig didn't load — try GTA Male from Pose / Import first.";
                    return;
                }
            }

            var clipName = _vm.SelectedAnimClip!;
            var bytes = _animLibDictBytes!;
            var fileName = _animLibDictFileName!;
            var rigBoneNames = _vm.Bones.Select(b => b.Name ?? "").ToList();
            rigBoneNames.AddRange(GtaBoneTags.FreemodeAuxBoneNames);

            _vm.StatusText = $"Previewing {clipName}…";
            YcdImporter.ImportResult result;
            try
            {
                result = await Task.Run(() =>
                    YcdImporter.ImportNamed(bytes, fileName, clipName, rigBoneNames));
            }
            catch (Exception ex)
            {
                if (gen != _animLibPreviewGen) return;
                _vm.AnimLibraryClipReady = false;
                _vm.StatusText = "Preview failed: " + ex.Message;
                FosLogger.Warn("anim-library", $"preview failed: {fileName} / {clipName}", ex);
                return;
            }

            if (gen != _animLibPreviewGen) return;

            _vm.AnimLibraryDurationLabel = $"Max duration: {result.Duration:F3}s";
            _vm.AnimLibraryTracksLabel =
                $"Total tracks: {result.MappedBones + result.UnmappedTracks}";

            var displayName = LooksLikeHashName(result.ClipName) ? clipName : result.ClipName;
            if (result.MappedBones == 0)
            {
                _vm.AnimLibraryClipReady = false;
                _vm.StatusText = "Clip loaded but 0 bones matched — use GTA Male/Female.";
                return;
            }

            if (!await ImportPayloadAsClipAsync(result.PayloadJson, displayName, editable: true))
            {
                if (gen != _animLibPreviewGen) return;
                _vm.AnimLibraryClipReady = false;
                _vm.StatusText = "Preview failed to load onto the ped — try again.";
                return;
            }

            if (gen != _animLibPreviewGen) return;

            _vm.AnimLibraryClipReady = true;
            // Library clips are real game anims — always root-motion / travelling.
            _vm.MovementIndex = (int)Services.EmoteMovement.RootMotion;
            // Atomic seek+play — two separate ExecuteScriptAsync calls let a
            // deferred import timeline-update (playing:false) land between
            // them and leave the transport lit but frozen at 0:00.
            if (play)
            {
                await Viewport.CoreWebView2.ExecuteScriptAsync(
                    "window.poseRestartPlay ? window.poseRestartPlay() : (window.poseSetTime(0), window.posePlay())");
                _vm.TimelinePlaying = true;
                _vm.TimelineTime = 0;
                UpdatePlayButtonVisual();
            }
            else
            {
                await Viewport.CoreWebView2.ExecuteScriptAsync(
                    "window.poseSetTime && window.poseSetTime(0); window.posePause && window.posePause()");
                _vm.TimelinePlaying = false;
                _vm.TimelineTime = 0;
                UpdatePlayButtonVisual();
            }

            _vm.StatusText = play
                ? $"Playing {_vm.SelectedAnimDict.Name} / {clipName}"
                : $"Loaded {_vm.SelectedAnimDict.Name} / {clipName} for editing.";
        }
        finally
        {
            _animLibBusy = false;
            var pending = _animLibPendingClip;
            var pendingPlay = _animLibPendingPlay;
            _animLibPendingClip = null;
            if (!string.IsNullOrEmpty(pending) && _vm.SelectedAnimDict is not null)
            {
                // SelectionChanged will re-enter preview if we change the clip;
                // if it's already selected, call preview directly.
                if (!string.Equals(_vm.SelectedAnimClip, pending, StringComparison.Ordinal))
                    _vm.SelectedAnimClip = pending;
                else
                    await PreviewAnimLibraryClipAsync(pendingPlay);
            }
        }
    }
}
