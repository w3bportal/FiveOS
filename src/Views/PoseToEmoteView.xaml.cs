// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FiveOS.Services;
using FiveOS.ViewModels;
using Microsoft.Web.WebView2.Core;

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
    private TimelineStrip? _trimStripPendingSync;
    private DispatcherTimer? _trimSyncTimer;
    private string? _viewerSessionDir;
    private bool _webViewReady;
    private bool _viewerReady;
    private string? _pendingModelUrl;
    private volatile bool _ycdImportBusy;
    private int _importClipRequestId;
    private readonly Dictionary<int, TaskCompletionSource<string?>> _importClipWaiters = new();

    public PoseToEmoteView()
    {
        InitializeComponent();
        DataContext = _vm;
        _timelineCtl.Changed += () => Dispatcher.BeginInvoke(RedrawTimeline, DispatcherPriority.Render);
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

            // Inspector → JS push: secondary-motion toggle + intensity
            // slider both call the same evaluator setter so the spring
            // reacts on next playback tick (or immediately resets if
            // playback is idle).
            if (ev.PropertyName == nameof(PoseToEmoteViewModel.SecondaryMotionEnabled)
                || ev.PropertyName == nameof(PoseToEmoteViewModel.SecondaryMotionIntensity))
            {
                PushSecondaryMotionToViewer();
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

            // Redraw whichever timeline layer the change affects.
            // TimelineTime is the hot path (fires every playback frame);
            // we just shift the playhead instead of redrawing everything.
            switch (ev.PropertyName)
            {
                case nameof(PoseToEmoteViewModel.TimelineTime):
                    if (_vm.TimelinePlaying)
                    {
                        SyncTimelineControllerFromVm();
                        _timelineCtl.EnsurePlayheadVisible(_vm.TimelineTime, _vm.TimelineDuration, TimelineCanvasWidth);
                        PushTimelineControllerToVm();
                    }
                    Dispatcher.BeginInvoke(new Action(DrawTimelinePlayhead), DispatcherPriority.Render);
                    break;
                case nameof(PoseToEmoteViewModel.TimelineDuration):
                case nameof(PoseToEmoteViewModel.TimelineFps):
                case nameof(PoseToEmoteViewModel.TimelineZoom):
                case nameof(PoseToEmoteViewModel.TimelineScrollOffset):
                    Dispatcher.BeginInvoke(new Action(RedrawTimeline), DispatcherPriority.Render);
                    break;
            }
        };
        // Keyframe collection changes (add / move / clear) trigger a full
        // track redraw so the diamonds reflect the live JS-side list.
        _vm.TimelineKeyframes.CollectionChanged += (_, __) =>
        {
            Dispatcher.BeginInvoke(new Action(RedrawTimeline), DispatcherPriority.Render);
        };

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

            // Per-instance virtual host so this WebView2 doesn't clash
            // with the 3D Model tab's viewer (which uses viewer.local).
            // Both serve copies of the same bundle.
            _viewerSessionDir = Path.Combine(Path.GetTempPath(), "FiveOS", "ViewerPose-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_viewerSessionDir);

            _vm.ViewerLoadingCaption = "Unpacking viewer bundle...";
            var bundledViewerDir = FiveOS.Services.RuntimeAssets.ViewerDir;
            CopyDirectory(bundledViewerDir, _viewerSessionDir);

            Viewport.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "pose-viewer.local", _viewerSessionDir, CoreWebView2HostResourceAccessKind.Allow);
            WebViewDialogs.Theme(Viewport.CoreWebView2);

            Viewport.CoreWebView2.WebMessageReceived += OnViewerMessage;
            _vm.ViewerLoadingCaption = "Loading viewer...";
            Viewport.Source = new Uri("https://pose-viewer.local/viewer.html");
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

                case "pose-mode-entered":
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
                        }
                        _vm.HasRig = _vm.Bones.Count > 0;
                        _vm.NotifyBonesChanged();
                        _vm.StatusText = _vm.HasRig
                            ? "Click a joint sphere to pose it. Rotate with the gizmo."
                            : "Loaded, but no skeleton was found.";
                        // Workspace is fully assembled — drop the splash overlay.
                        _vm.IsViewerLoading = false;
                        PushClipTrackVisibilityToViewer();
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

                case "pose-timeline-update":
                    Dispatcher.Invoke(() =>
                    {
                        // Re-sync C# state from the JS source-of-truth.
                        if (doc.RootElement.TryGetProperty("time", out var tEl))
                            _vm.TimelineTime = tEl.GetDouble();
                        if (doc.RootElement.TryGetProperty("duration", out var dEl))
                            _vm.TimelineDuration = dEl.GetDouble();
                        if (doc.RootElement.TryGetProperty("fps", out var fEl))
                            _vm.TimelineFps = fEl.GetInt32();
                        if (doc.RootElement.TryGetProperty("playing", out var pEl))
                            _vm.TimelinePlaying = pEl.GetBoolean();
                        if (doc.RootElement.TryGetProperty("loop", out var lEl))
                            _vm.TimelineLoop = lEl.GetBoolean();
                        if (doc.RootElement.TryGetProperty("keyframes", out var kEl))
                        {
                            RebuildKeyframeMarkers(kEl);
                        }
                        UpdatePlayButtonVisual();
                    });
                    break;

                case "pose-timeline-tick":
                    Dispatcher.Invoke(() =>
                    {
                        if (doc.RootElement.TryGetProperty("time", out var tEl))
                        {
                            _vm.TimelineTime = tEl.GetDouble();
                        }
                        if (doc.RootElement.TryGetProperty("playing", out var pEl))
                            _vm.TimelinePlaying = pEl.GetBoolean();
                        UpdatePlayButtonVisual();
                    });
                    break;

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
                        RedrawStrips();
                        RedrawTimeline();
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

                case "debug-event":
                    {
                        // Forwarded from viewer-side fosTelemetry. Map straight
                        // into the host's debug stream so the floating popup
                        // (and the "X errors" badge on the sidebar) updates.
                        var lev = doc.RootElement.TryGetProperty("level", out var lEl) ? lEl.GetString() ?? "info" : "info";
                        var cat = doc.RootElement.TryGetProperty("category", out var cEl) ? cEl.GetString() ?? "system" : "system";
                        var txt = doc.RootElement.TryGetProperty("text", out var tEl) ? tEl.GetString() ?? "" : "";
                        var payload = doc.RootElement.TryGetProperty("payload", out var pEl) && pEl.ValueKind != JsonValueKind.Null
                            ? pEl.GetRawText() : "";
                        AppendDebug(lev, cat, txt, payload);
                    }
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

    // ── Prop import (emote-with-prop authoring) ─────────────────────

    private async void OnLoadProp(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load Prop Mesh",
            Filter = "3D models (*.glb;*.gltf;*.fbx;*.obj)|*.glb;*.gltf;*.fbx;*.obj|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        if (!_webViewReady || _viewerSessionDir is null) return;

        // Copy the prop file into the session dir so the virtual host
        // can serve it. Re-use the same dir; prop file name is
        // namespaced to avoid clashing with the rig file.
        var ext = Path.GetExtension(dlg.FileName);
        var dest = Path.Combine(_viewerSessionDir, "user-prop" + ext);
        try { File.Copy(dlg.FileName, dest, overwrite: true); }
        catch (Exception ex) { _vm.StatusText = "Couldn't copy prop: " + ex.Message; return; }

        var url = "https://pose-viewer.local/user-prop" + ext;
        var safe = url.Replace("\\", "/").Replace("'", "\\'");
        await Viewport.CoreWebView2.ExecuteScriptAsync($"window.loadProp && window.loadProp('{safe}')");
        // Sync the chosen attach bone over to JS so getPropTransform()
        // decomposes against the right matrix on export.
        await Viewport.CoreWebView2.ExecuteScriptAsync(
            $"window.setPropAttachBone && window.setPropAttachBone('{_vm.PropAttachBoneName}', {_vm.PropBoneId})");
    }

    private async void OnRemoveProp(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        await Viewport.CoreWebView2.ExecuteScriptAsync("window.removeProp && window.removeProp()");
    }

    private async void OnSelectProp(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        await Viewport.CoreWebView2.ExecuteScriptAsync("window.selectProp && window.selectProp()");
    }

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

    private async void OnPropBoneNameChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_webViewReady || !_vm.HasProp) return;
        // Push the new bone-name choice to JS so the prop's local
        // transform decomposes against the right bone on next
        // getPropTransform call. ID stays whatever the user set in the
        // NumberBox.
        await Viewport.CoreWebView2.ExecuteScriptAsync(
            $"window.setPropAttachBone && window.setPropAttachBone('{_vm.PropAttachBoneName}', {_vm.PropBoneId})");
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
        if (!_webViewReady) return;
        var script = _vm.TimelinePlaying ? "window.posePause && window.posePause()" : "window.posePlay && window.posePlay()";
        await Viewport.CoreWebView2.ExecuteScriptAsync(script);
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

    private async void OnTimelineGoToStart(object sender, RoutedEventArgs e) =>
        await SeekTimelineAsync(0);

    private async void OnTimelineGoToEnd(object sender, RoutedEventArgs e) =>
        await SeekTimelineAsync(_vm.TimelineDuration);

    private async void OnTimelinePrevFrame(object sender, RoutedEventArgs e)
    {
        var step = 1.0 / System.Math.Max(1, _vm.TimelineFps);
        await SeekTimelineAsync(_vm.TimelineTime - step);
    }

    private async void OnTimelineNextFrame(object sender, RoutedEventArgs e)
    {
        var step = 1.0 / System.Math.Max(1, _vm.TimelineFps);
        await SeekTimelineAsync(_vm.TimelineTime + step);
    }

    private async void OnTimelineLoopToggle(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        await Viewport.CoreWebView2.ExecuteScriptAsync("window.poseToggleLoop && window.poseToggleLoop()");
    }

    private async void OnAddKeyframe(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        await Viewport.CoreWebView2.ExecuteScriptAsync("window.poseAddKeyframe && window.poseAddKeyframe()");
    }

    private async void OnDeleteKeyframe(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        await Viewport.CoreWebView2.ExecuteScriptAsync("window.poseDeleteCurrentKeyframe && window.poseDeleteCurrentKeyframe()");
    }

    private async void OnClearKeyframes(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        var ok = AppDialog.Show(
            "Clear all keyframes? The current pose stays on the rig.",
            "Clear timeline?",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.Yes) return;
        await Viewport.CoreWebView2.ExecuteScriptAsync("window.poseClearKeyframes && window.poseClearKeyframes()");
    }

    /// <summary>Sync the VM's keyframe collection from the JS-side
    /// timeline-update payload. PixelX is left at 0 — the new
    /// canvas-based timeline computes positions itself at draw time
    /// from Time + the live canvas width, so we don't need to
    /// pre-bake them here. Kept the collection for the animated-chip
    /// + skipped-bones tracking and external consumers.</summary>
    private void RebuildKeyframeMarkers(JsonElement kEl)
    {
        _vm.TimelineKeyframes.Clear();
        foreach (var t in kEl.EnumerateArray())
        {
            _vm.TimelineKeyframes.Add(new KeyframeMarker { Time = t.GetDouble() });
        }
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
    private static readonly Brush TimelineKfFillBrush    = new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x33));
    private static readonly Brush TimelinePlayheadBrush  = new SolidColorBrush(Color.FromRgb(0xE8, 0x45, 0x45));
    private static readonly Brush TimelinePlayheadBadgeBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x45, 0x45));

    // Cascadeur-style sequencer palette
    private static readonly Brush TimelineMajorTickBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));
    private static readonly Brush TimelineMinorTickBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly Brush TimelineLabelBrush     = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
    private static readonly Brush TimelineTrackLineBrush = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38));
    private static readonly Brush TimelineGridBrush      = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly Brush TimelineKfStrokeBrush  = new SolidColorBrush(Color.FromRgb(0xF5, 0xF6, 0xFB));

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
        return _timelineCtl.SnapTime(t, _vm.TimelineFps);
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
        if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
            _timelineCtl.ZoomAt(e.Delta, anchorTime, _vm.TimelineDuration);
        else
            _timelineCtl.ScrollByTime(-(e.Delta / 120.0) * VisibleTimelineDuration * 0.08, _vm.TimelineDuration);
        PushTimelineControllerToVm();
        RedrawTimeline();
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

    private void RedrawTimeline()
    {
        if (TimelineRulerCanvas is null) return;
        DrawTimelineRuler();
        DrawTimelineStrips();
        DrawTimelineTrack();
        DrawTimelinePlayhead();
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
    private static readonly Brush StripFillBrushBaked    = new SolidColorBrush(Color.FromRgb(0x5B, 0xBF, 0xB5));
    private static readonly Brush StripFillBrushImported = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xC4));
    private static readonly Brush StripBorderBrush       = new SolidColorBrush(Color.FromRgb(0x2E, 0x5A, 0x80));
    private static readonly Brush StripTextBrush         = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
    // Semi-opaque dark wash painted over the fade region. Conveys "this
    // region's weight ramps to zero" without competing with the strip
    // colour. Triangular polygon: 0% alpha at the inner edge, full at
    // the strip edge — matches the linear ramp the evaluator uses.
    private static readonly Brush StripFadeBrush         = new SolidColorBrush(Color.FromRgb(0x10, 0x16, 0x28)) { Opacity = 0.65 };

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
            Fill = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
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

        if (_vm.Strips.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "Import an animation",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                IsHitTestVisible = false,
            };
            hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(hint, TIMELINE_PADDING_X + 8);
            Canvas.SetTop(hint, 4 + (lane.Height - hint.DesiredSize.Height) / 2);
            canvas.Children.Add(hint);
            return;
        }

        if (!_vm.TimelineClipTrackVisible)
        {
            var hint = new TextBlock
            {
                Text = "Clip track hidden — click eye to show",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
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
                Stroke = StripBorderBrush,
                StrokeThickness = 1,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = strip,
                ToolTip = $"{strip.ClipName} — {strip.Duration:F2}s @ {strip.Start:F2}s (src {strip.TrimLabel}). Click/drag to scrub. Alt+drag to retime, right-click to trim/split.",
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
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1C)),
                    IsHitTestVisible = false,
                    Padding = new Thickness(4, 1, 4, 1),
                };
                wipText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var wipBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xE6, 0xEC, 0xFA)),
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
                        Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xD5, 0x4A)),
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

    private void DrawTimelineRuler()
    {
        var canvas = TimelineRulerCanvas;
        if (canvas is null) return;
        canvas.Children.Clear();
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 10 || h < 5) return;

        var dur = System.Math.Max(0.001, _vm.TimelineDuration);
        var fps = System.Math.Max(1, _vm.TimelineFps);
        SyncTimelineControllerFromVm();
        var vis = VisibleTimelineDuration;
        var scroll = _timelineCtl.ScrollOffset;
        var usable = TimelineUsableWidth;
        var pxPerFrame = usable / (vis * fps);

        int fStart = (int)System.Math.Floor(scroll * fps);
        int fEnd = (int)System.Math.Ceiling((scroll + vis) * fps);
        fStart = System.Math.Max(0, fStart);
        fEnd = System.Math.Min((int)System.Math.Round(dur * fps), fEnd);

        int labelEveryN = 1;
        while (labelEveryN * pxPerFrame < 50) labelEveryN *= 2;
        if (pxPerFrame >= 8 && labelEveryN > 2) labelEveryN = 2;
        if (pxPerFrame >= 16 && labelEveryN > 1) labelEveryN = 1;

        for (int f = fStart; f <= fEnd; f++)
        {
            double x = TimeToTimelineX(f / (double)fps);
            bool major = f % labelEveryN == 0;
            var tick = new System.Windows.Shapes.Line
            {
                X1 = x, X2 = x,
                Y1 = h - (major ? 10 : 4),
                Y2 = h,
                Stroke = major ? TimelineMajorTickBrush : TimelineMinorTickBrush,
                StrokeThickness = 1,
                SnapsToDevicePixels = true,
            };
            canvas.Children.Add(tick);
            if (major)
            {
                var label = new TextBlock
                {
                    Text = f.ToString(),
                    FontSize = 9,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = TimelineLabelBrush,
                    IsHitTestVisible = false,
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, x - label.DesiredSize.Width / 2);
                Canvas.SetTop(label, 2);
                canvas.Children.Add(label);
            }
        }
    }

    private void DrawTimelineTrack()
    {
        var canvas = TimelineTrackCanvas;
        if (canvas is null) return;
        canvas.Children.Clear();
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 10 || h < 5) return;

        var usable = TimelineUsableWidth;
        var midY = h / 2.0;

        // Subtle track baseline so the row reads as "a timeline".
        var baseline = new System.Windows.Shapes.Line
        {
            X1 = TIMELINE_PADDING_X, X2 = w - TIMELINE_PADDING_X,
            Y1 = midY, Y2 = midY,
            Stroke = TimelineTrackLineBrush,
            StrokeThickness = 1,
            SnapsToDevicePixels = true,
        };
        canvas.Children.Add(baseline);

        // Keyframe diamonds. Each carries its KeyframeMarker model via
        // .Tag so the per-shape MouseDown handlers can find it without
        // a separate hit-test pass.
        foreach (var kf in _vm.TimelineKeyframes)
        {
            var x = TimeToTimelineX(kf.Time);
            var diamond = new System.Windows.Shapes.Polygon
            {
                Points = new PointCollection
                {
                    new System.Windows.Point( 0, -8),
                    new System.Windows.Point( 8,  0),
                    new System.Windows.Point( 0,  8),
                    new System.Windows.Point(-8,  0),
                },
                Fill = TimelineKfFillBrush,
                Stroke = TimelineKfStrokeBrush,
                StrokeThickness = 1,
                Cursor = System.Windows.Input.Cursors.SizeWE,
                Tag = kf,
                ToolTip = $"Keyframe at {kf.Time:F3}s — drag to retime, right-click for options",
            };
            diamond.MouseLeftButtonDown += OnTimelineKfMouseDown;
            diamond.MouseMove           += OnTimelineKfMouseMove;
            diamond.MouseLeftButtonUp   += OnTimelineKfMouseUp;
            diamond.MouseRightButtonUp  += OnTimelineKfRightClick;
            Canvas.SetLeft(diamond, x);
            Canvas.SetTop(diamond, midY);
            canvas.Children.Add(diamond);
        }
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
                FontFamily = new FontFamily("Consolas"),
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

    private void OnTimelineSeekClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        _timelineScrubbing = true;
        _timelineScrubCanvas = canvas;
        SeekFromMouse(e);
        canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnTimelineSeekMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_timelineScrubbing) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { _timelineScrubbing = false; return; }
        SeekFromMouse(e);
    }

    private void OnTimelineSeekUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Canvas canvas) canvas.ReleaseMouseCapture();
        _timelineScrubbing = false;
        _timelineScrubCanvas = null;
    }

    private async void SeekFromMouse(System.Windows.Input.MouseEventArgs e)
    {
        var canvas = _timelineScrubCanvas ?? TimelineStripCanvas ?? TimelineRulerCanvas;
        if (canvas is null) return;
        var pos = e.GetPosition(canvas);
        var t = TimelineXToTime(pos.X);
        _vm.TimelineTime = System.Math.Round(t, 3);
        if (_webViewReady)
        {
            var tArg = t.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            await Viewport.CoreWebView2.ExecuteScriptAsync($"window.poseSetTime && window.poseSetTime({tArg})");
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Keyframe drag — same delta-px math as before, but anchored to the
    // new track canvas instead of the (removed) marker ItemsControl.
    // ────────────────────────────────────────────────────────────────

    private KeyframeMarker? _draggingKf;
    private double _kfDragStartTime;
    private System.Windows.Point _kfDragStartPx;
    private bool _kfDragMoved;

    private void OnTimelineKfMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_vm.TimelineKeyframeTrackLocked) { e.Handled = true; return; }
        if (sender is not FrameworkElement fe || fe.Tag is not KeyframeMarker marker) return;
        _draggingKf = marker;
        _kfDragStartTime = marker.Time;
        _kfDragStartPx = e.GetPosition(TimelineTrackCanvas);
        _kfDragMoved = false;
        fe.CaptureMouse();
        e.Handled = true;
    }

    private void OnTimelineKfMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggingKf is null) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        if (sender is not FrameworkElement fe) return;
        var cur = e.GetPosition(TimelineTrackCanvas);
        var dx = cur.X - _kfDragStartPx.X;
        if (!_kfDragMoved && System.Math.Abs(dx) < 2) return;
        _kfDragMoved = true;
        var usable = TimelineUsableWidth;
        var dt = (dx / usable) * VisibleTimelineDuration;
        var newTime = System.Math.Max(0, System.Math.Min(_vm.TimelineDuration, _kfDragStartTime + dt));
        newTime = _timelineCtl.SnapTime(newTime, _vm.TimelineFps);
        // Hold Alt for fine adjustment (bypass FPS snap).
        if (!System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt))
        {
            var step = 1.0 / System.Math.Max(1, _vm.TimelineFps);
            newTime = System.Math.Round(newTime / step) * step;
        }
        _draggingKf.Time = System.Math.Round(newTime, 3);
        // Move the in-canvas diamond live for tactile feedback before
        // the JS round-trip lands.
        Canvas.SetLeft(fe, TimeToTimelineX(_draggingKf.Time));
        e.Handled = true;
    }

    private async void OnTimelineKfMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_draggingKf is null) return;
        if (sender is FrameworkElement fe) fe.ReleaseMouseCapture();
        var marker = _draggingKf;
        _draggingKf = null;
        e.Handled = true;
        if (!_kfDragMoved) return;
        var fromArg = _kfDragStartTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var toArg   = marker.Time.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        if (_webViewReady)
        {
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.poseMoveKeyframe && window.poseMoveKeyframe({fromArg}, {toArg})");
        }
    }

    private void OnTimelineKfRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not KeyframeMarker marker) return;
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
        fe.ContextMenu = menu;
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

    private void OnStripMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not TimelineStrip strip) return;

        // Alt+drag retimes the clip on the timeline; plain click/drag scrubs.
        if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt))
        {
            if (_vm.TimelineClipTrackLocked) { e.Handled = true; return; }
            _draggingStrip = strip;
            _stripDragStartStart = strip.Start;
            _stripDragStartPx = e.GetPosition(TimelineStripCanvas);
            _stripDragMoved = false;
            fe.CaptureMouse();
            e.Handled = true;
            return;
        }

        _timelineScrubbing = true;
        _timelineScrubCanvas = TimelineStripCanvas;
        SeekFromMouse(e);
        TimelineStripCanvas?.CaptureMouse();
        e.Handled = true;
    }

    private void OnStripMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_timelineScrubbing && _draggingStrip is null)
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            {
                _timelineScrubbing = false;
                TimelineStripCanvas?.ReleaseMouseCapture();
                return;
            }
            SeekFromMouse(e);
            e.Handled = true;
            return;
        }

        if (_draggingStrip is null) return;
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
        // Snap to FPS grid unless Alt held — same convention as KF drag.
        if (!System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt))
        {
            var step = 1.0 / System.Math.Max(1, _vm.TimelineFps);
            newStart = System.Math.Round(newStart / step) * step;
        }
        _draggingStrip.Start = System.Math.Round(newStart, 3);
        // Live-move the rectangle (and its label, which is the next
        // child on the canvas — see DrawTimelineStrips) for tactile
        // feedback before the JS round-trip lands.
        var x0 = TimeToTimelineX(_draggingStrip.Start);
        Canvas.SetLeft(fe, x0);
        // Find the strip's label by walking next siblings until we hit
        // a non-label or the next strip rectangle.
        if (TimelineStripCanvas is not null)
        {
            int idx = TimelineStripCanvas.Children.IndexOf(fe);
            if (idx >= 0 && idx + 1 < TimelineStripCanvas.Children.Count
                && TimelineStripCanvas.Children[idx + 1] is TextBlock lbl)
            {
                Canvas.SetLeft(lbl, x0 + 6);
            }
        }
        e.Handled = true;
    }

    private async void OnStripMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_timelineScrubbing && _draggingStrip is null)
        {
            TimelineStripCanvas?.ReleaseMouseCapture();
            _timelineScrubbing = false;
            _timelineScrubCanvas = null;
            e.Handled = true;
            return;
        }

        if (_draggingStrip is null) return;
        if (sender is FrameworkElement fe) fe.ReleaseMouseCapture();
        var strip = _draggingStrip;
        _draggingStrip = null;
        e.Handled = true;
        if (!_stripDragMoved) return;
        var startArg = strip.Start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        if (_webViewReady)
        {
            await Viewport.CoreWebView2.ExecuteScriptAsync(
                $"window.poseMoveStrip && window.poseMoveStrip({strip.Id}, {startArg})");
        }
    }

    private void OnStripRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not TimelineStrip strip) return;
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

        var remove = new MenuItem { Header = $"Remove '{strip.ClipName}' from timeline" };
        remove.Click += async (_, __) =>
        {
            if (!_webViewReady) return;
            await Viewport.CoreWebView2.ExecuteScriptAsync($"window.poseRemoveStrip && window.poseRemoveStrip({strip.Id})");
        };
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
        if (LoopIcon != null)
            LoopIcon.Symbol = _vm.TimelineLoop
                ? Wpf.Ui.Controls.SymbolRegular.ArrowRepeatAll24
                : Wpf.Ui.Controls.SymbolRegular.ArrowRepeatAllOff24;
    }

    // ─── Prev / next keyframe nav (transport bar) ────────────────────

    private async void OnTimelinePrevKf(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        var target = FindAdjacentKeyframeTime(forward: false);
        if (target is null) return;
        var tArg = target.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        await Viewport.CoreWebView2.ExecuteScriptAsync($"window.poseSetTime && window.poseSetTime({tArg})");
    }

    private async void OnTimelineNextKf(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        var target = FindAdjacentKeyframeTime(forward: true);
        if (target is null) return;
        var tArg = target.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        await Viewport.CoreWebView2.ExecuteScriptAsync($"window.poseSetTime && window.poseSetTime({tArg})");
    }

    /// <summary>Find the keyframe time strictly before / after the
    /// current scrubber position. Returns null when there's no
    /// adjacent KF in that direction (don't wrap — feels surprising
    /// when you're scrubbing manually).</summary>
    private double? FindAdjacentKeyframeTime(bool forward)
    {
        if (_vm.TimelineKeyframes.Count == 0) return null;
        var now = _vm.TimelineTime;
        const double eps = 1e-4;
        if (forward)
        {
            double? best = null;
            foreach (var k in _vm.TimelineKeyframes)
            {
                if (k.Time <= now + eps) continue;
                if (best is null || k.Time < best.Value) best = k.Time;
            }
            return best;
        }
        else
        {
            double? best = null;
            foreach (var k in _vm.TimelineKeyframes)
            {
                if (k.Time >= now - eps) continue;
                if (best is null || k.Time > best.Value) best = k.Time;
            }
            return best;
        }
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

    /// <summary>Mirror of <see cref="OnOutlinerToggleClick"/> for the
    /// right-side Inspector panel — flips the bound IsInspectorOpen
    /// bool, which drives the panel Border's width binding between
    /// 260 (open) and 0 (folded).</summary>
    private void OnInspectorToggleClick(object sender, RoutedEventArgs e)
    {
        _vm.IsInspectorOpen = !_vm.IsInspectorOpen;
    }

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

    private async Task<bool> ImportPayloadAsClipAsync(string payloadJson, string clipName)
    {
        if (!_webViewReady) return false;

        // payloadJson is already a JSON object literal — never inline it in
        // ExecuteScriptAsync (multi-hundred-KB scripts freeze WebView2).
        string? raw = await ImportPayloadAsClipViaMessageAsync(payloadJson, clipName);

        var json = PeelScriptJson(raw);
        if (string.IsNullOrEmpty(json)) { _vm.StatusText = "Clip import failed — empty viewer response."; return false; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean())
                return true;
            var reason = doc.RootElement.TryGetProperty("reason", out var rEl) ? rEl.GetString() : raw;
            var msg = doc.RootElement.TryGetProperty("msg", out var mEl) ? mEl.GetString() : null;
            _vm.StatusText = reason == "no-bones-matched"
                ? "Import failed — bone names in the clip didn't match the loaded rig."
                : $"Clip import failed: {reason}" + (msg is not null ? $" — {msg}" : "");
            AppendDebug("err", "timeline", "importKeyframesAsClip failed", reason ?? raw);
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
    private async Task<string?> ImportPayloadAsClipViaMessageAsync(string payloadJson, string clipName)
    {
        int requestId = Interlocked.Increment(ref _importClipRequestId);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_importClipWaiters) _importClipWaiters[requestId] = tcs;

        var envelope =
            $"{{\"kind\":\"host-import-keyframe-clip\",\"requestId\":{requestId}," +
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
            if (caption is not null) _vm.ViewerLoadingCaption = caption;
            _vm.IsViewerLoading = true;
        }
        else
        {
            _vm.IsViewerLoading = false;
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

    private async void OnTimelineSetInClick(object sender, RoutedEventArgs e) => await TrimActiveStripInAsync();
    private async void OnTimelineSetOutClick(object sender, RoutedEventArgs e) => await TrimActiveStripOutAsync();
    private async void OnTimelineSplitClick(object sender, RoutedEventArgs e) => await SplitActiveStripAsync();

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
                movement: _vm.Movement,
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
                movement: _vm.Movement,
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
            if (!await ImportPayloadAsClipAsync(result.PayloadJson, clipName))
                return;

            await Viewport.CoreWebView2.ExecuteScriptAsync("window.poseSetTime && window.poseSetTime(0)");
            await Viewport.CoreWebView2.ExecuteScriptAsync("window.posePlay && window.posePlay()");

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
            Title = "Import animation (.glb / .gltf / .fbx / .dae / .bvh)",
            Filter = "Animated 3D files (*.glb;*.gltf;*.fbx;*.dae;*.bvh)|*.glb;*.gltf;*.fbx;*.dae;*.bvh|BVH mocap (*.bvh)|*.bvh|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        await ImportAnimationFileAsync(dlg.FileName);
    }

    /// <summary>Core of the animation import — separated from the button
    /// handler so the FIVEOS_DEV_AUTOIMPORT dev hook can drive it too.</summary>
    private async Task ImportAnimationFileAsync(string path)
    {
        SetImportOverlay(true, "Importing animation…");
        _vm.StatusText = "Importing animation…";
        try
        {
            await PumpUiAsync();

            Services.AnimEmoteImporter.Result? res = null;
            try { await Task.Run(() => res = _animImporter.Import(path)); }
            catch (Exception ex) { res = Services.AnimEmoteImporter.Result.Fail("Import failed: " + ex.Message); }

            if (res is not { Success: true })
            {
                _vm.StatusText = res?.Error ?? "Import failed.";
                AppendDebug("err", "error", "Animation import failed", res?.Error ?? "(no result)");
                return;
            }

            var rigBoneNames = _vm.Bones.Select(b => b.Name ?? "").ToList();
            var payloadJson = BuildAnimKeyframePayload(res, rigBoneNames, out var mappedBones);
            if (payloadJson is null || mappedBones == 0)
            {
                _vm.StatusText = "No bones in this clip map onto the loaded rig.";
                AppendDebug("warn", "timeline", "Animation import: 0 bones mapped", Path.GetFileName(path));
                return;
            }

            // Imported clips that carry travel default to Root Motion so the ped
            // physically moves along the source path in-game (mover-extraction flags
            // + baked SKEL_ROOT track). The user can switch to In Place to drop it.
            if (res.RootMotion is { Count: > 0 })
                _vm.MovementIndex = (int)Services.EmoteMovement.RootMotion;

            SetImportOverlay(true, "Pushing animation into timeline…");
            _vm.StatusText = "Pushing animation into timeline…";
            await PumpUiAsync();
            Services.FosLogger.Info("dev", $"import clip payload = {payloadJson.Length:N0} chars, {mappedBones} bones");
            var clipName = res.ClipName ?? Path.GetFileNameWithoutExtension(path);
            if (!await ImportPayloadAsClipAsync(payloadJson, clipName))
                return;

            await Viewport.CoreWebView2.ExecuteScriptAsync("window.poseSetTime && window.poseSetTime(0)");
            await Viewport.CoreWebView2.ExecuteScriptAsync("window.posePlay && window.posePlay()");

            var rigLabel = res.Rig switch
            {
                Services.AnimEmoteImporter.RigKind.GtaRig => "GTA skeleton (exact copy)",
                Services.AnimEmoteImporter.RigKind.Mixamo => "Mixamo rig (retargeted)",
                _ => "generic rig (retargeted)",
            };
            var warnHint = res.Warnings.Count > 0 ? $" · {res.Warnings.Count} warning(s) — see Debug" : "";
            var cascadeurHint = res.Rig == Services.AnimEmoteImporter.RigKind.Generic
                ? " · Cascadeur: export FBX Binary, Animation preset, Bake ON, Y-up, 30fps scene, pelvis-only translation."
                : "";
            var rootHint = "";
            if (res.RootMotion is { Count: > 0 })
            {
                float maxRoot = 0f;
                var o = res.RootMotion[0];
                foreach (var r in res.RootMotion)
                    maxRoot = Math.Max(maxRoot, System.Numerics.Vector3.Distance(r, o));
                if (maxRoot > 0.05f)
                    rootHint = " · Root motion ON — export as FiveM resource (.fxresource), not dpemotes zip (dpemotes can't move the ped).";
                else
                    rootHint = " · Mostly in-place — set Movement to In Place if feet slide in-game.";
            }
            _vm.StatusText = $"Imported {res.ClipName} as clip ({res.Frames} frames @ {res.Fps} fps, {rigLabel}) — re-import if feet slide, then Export.{warnHint}{cascadeurHint}{rootHint}";
            AppendDebug("info", "timeline", "Animation imported",
                $"clip={res.ClipName} rig={res.Rig} frames={res.Frames} fps={res.Fps} mapped={mappedBones} skipped={res.UnmappedBones.Count}");
            foreach (var w in res.Warnings) AppendDebug("warn", "timeline", "Import warning", w);
            if (res.DurationSeconds > 60)
                AppendDebug("warn", "timeline", "Clip longer than the 60s timeline cap", $"{res.DurationSeconds:F1}s — trailing frames are clamped.");
        }
        finally
        {
            SetImportOverlay(false);
        }
    }

    /// <summary>Convert an AnimEmoteImporter result (GTA bone tags + per-frame
    /// quats) into the JSON payload <c>window.setKeyframes</c> accepts — the
    /// same shape <see cref="Services.YcdImporter"/> emits: bones addressed by
    /// the ACTUAL rig node names (resolved tag → rig name, which also covers
    /// GLTFLoader's "_NNN" dedup suffixes), one dense keyframe per frame.</summary>
    private static string? BuildAnimKeyframePayload(
        Services.AnimEmoteImporter.Result res,
        IReadOnlyList<string> rigBoneNames,
        out int mappedBones)
    {
        mappedBones = 0;

        var tagToRigName = new Dictionary<ushort, string>();
        foreach (var rn in rigBoneNames)
        {
            if (Services.GtaBoneTags.TryResolve(rn, out var tag) && !tagToRigName.ContainsKey(tag))
                tagToRigName[tag] = rn;
        }

        // Tag 0 (SKEL_ROOT) stays untouched — the reference glb bakes the
        // Y-up conversion into it, and the export side skips it anyway.
        var tracks = new List<(string RigName, System.Numerics.Quaternion[] PerFrame, string? Src)>();
        foreach (var t in res.Tracks)
        {
            if (t.BoneTag == 0 || t.PerFrame.Length == 0) continue;
            if (!tagToRigName.TryGetValue(t.BoneTag, out var rigName)) continue;
            tracks.Add((rigName, t.PerFrame, t.SourceName));
        }
        if (tracks.Count == 0) return null;
        mappedBones = tracks.Count;

        // Per-frame root translation (the ped's travel / weight-shift / bob),
        // extracted by the retargeter from the source pelvis path. Feeding it as
        // kf.root makes the viewer MOVE the whole ped like the source shows —
        // and the export side (BuildRootMotionTrack) bakes the same track. Clamp
        // so one wild frame can't fling the ped off the grid.
        var rootMotion = res.RootMotion;
        bool hasRoot = rootMotion != null && rootMotion.Count > 0;
        const float rootClamp = 6f;

        int fps = Math.Clamp(res.Fps, 1, 120);
        int frames = Math.Max(1, res.Frames);
        var keyframes = new List<object>(frames);
        for (int f = 0; f < frames; f++)
        {
            var bones = new List<object>(tracks.Count);
            foreach (var (rigName, perFrame, srcName) in tracks)
            {
                var q = perFrame[Math.Min(f, perFrame.Length - 1)];
                var qa = new[]
                {
                    Math.Round((double)q.X, 5), Math.Round((double)q.Y, 5),
                    Math.Round((double)q.Z, 5), Math.Round((double)q.W, 5),
                };
                // src (which SOURCE bone drives this GTA bone) only on frame 0 —
                // the viewer's bone-tree reads it from the first occurrence.
                bones.Add(f == 0 ? (object)new { name = rigName, q = qa, src = srcName }
                                 : new { name = rigName, q = qa });
            }
            var time = Math.Round(f / (double)fps, 3);
            if (hasRoot)
            {
                var r = rootMotion![Math.Min(f, rootMotion.Count - 1)];
                var ra = new[]
                {
                    Math.Round((double)Math.Clamp(r.X, -rootClamp, rootClamp), 4),
                    Math.Round((double)Math.Clamp(r.Y, -rootClamp, rootClamp), 4),
                    Math.Round((double)Math.Clamp(r.Z, -rootClamp, rootClamp), 4),
                };
                keyframes.Add(new { time, bones, root = ra });
            }
            else
            {
                keyframes.Add(new { time, bones });
            }
        }

        var payload = new
        {
            duration = Math.Round(Math.Max(1, frames - 1) / (double)fps, 3),
            fps,
            source = "anim-import",
            boneSpace = "glb",
            keyframes,
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>FIVEOS_DEV_AUTOIMPORT hook — waits for the viewer + default
    /// rig, then runs the same import the Library button triggers.</summary>
    public async Task DevImportAnimationAsync(string path)
    {
        for (int i = 0; i < 100 && !(_webViewReady && _vm.HasRig); i++)
            await Task.Delay(200);
        if (!_webViewReady || !_vm.HasRig)
        {
            Services.FosLogger.Warn("dev", "autoimport: viewer/rig never became ready");
            return;
        }
        // Let the default ped fully settle before importing — HasRig flips on the
        // first 'loaded' signal, but a late re-enter of pose mode (model finishing
        // load) can wipe the just-set keyframes. A user clicking the button never
        // races this; the auto-hook does.
        await Task.Delay(4000);
        await ImportAnimationFileAsync(path);
    }

    /// <summary>FIVEOS_DEV_MODE hook — preselects the playback/movement mode
    /// (0=in place, 1=upper body, 2=walkable).</summary>
    public void DevSetMovement(int index) => _vm.MovementIndex = Math.Clamp(index, 0, 3);

    // ════════════════════════════════════════════════════════════════
    // UNDO / REDO / DEBUG / SKIPPED-BONES handlers
    // ════════════════════════════════════════════════════════════════

    private async void OnUndoPose(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        await Viewport.CoreWebView2.ExecuteScriptAsync("window.poseUndo && window.poseUndo()");
        await RefreshHistoryDepth();
    }

    private async void OnRedoPose(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        await Viewport.CoreWebView2.ExecuteScriptAsync("window.poseRedo && window.poseRedo()");
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

    private void OnToggleDebugPanel(object sender, RoutedEventArgs e)
    {
        _vm.IsDebugPanelOpen = !_vm.IsDebugPanelOpen;
        ShowOrHideDebugPopup();
    }

    private DebugPopup? _debugPopup;
    private void ShowOrHideDebugPopup()
    {
        if (_vm.IsDebugPanelOpen)
        {
            if (_debugPopup is null)
            {
                _debugPopup = new DebugPopup(_vm);
                _debugPopup.Owner = Window.GetWindow(this);
                _debugPopup.Closed += (_, __) =>
                {
                    _vm.IsDebugPanelOpen = false;
                    _debugPopup = null;
                };
            }
            _debugPopup.Show();
            _debugPopup.Activate();
        }
        else
        {
            _debugPopup?.Close();
            _debugPopup = null;
        }
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

    /// <summary>Append a row to the VM's debug log + bump error counters
    /// when relevant. Forwarded from both the viewer's debug-event
    /// messages and from local C# event sites so the log is a unified
    /// timeline of host + viewer activity.</summary>
    private void AppendDebug(string level, string category, string text, string payload = "")
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _vm.DebugEntries.Add(new ViewModels.DebugLogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Category = category,
                Text = text,
                Payload = payload,
            });
            // Cap the collection so a long session doesn't unbounded-grow
            // the WPF binding's working set.
            while (_vm.DebugEntries.Count > ViewModels.PoseToEmoteViewModel.DebugCapacity)
                _vm.DebugEntries.RemoveAt(0);

            if (level == "err") _vm.RecentErrorCount++;
        }));
    }

    // ════════════════════════════════════════════════════════════════
    // MULTI-CHARACTER (Ped A / B) handlers — minimal scope: load a
    // second skeleton offset to the right, swap which one is the
    // active editing target. Both share the timeline for now; full
    // per-slot timelines land later.
    // ════════════════════════════════════════════════════════════════

    private async void OnAddSecondaryPed(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) { _vm.StatusText = "Viewer not ready."; return; }

        // Default Ped B to the bundled female freemode mesh — paired
        // emotes are almost always M/F or F/F authoring, and a one-click
        // "show me the partner ped" is what users expect here. Custom
        // file loading lives on `loadSecondaryPed(url)` for future
        // hookup (a separate "Load custom Ped B…" entry would call that).
        await Viewport.CoreWebView2.ExecuteScriptAsync(
            "window.loadSecondaryGtaPed && window.loadSecondaryGtaPed('female')");
        _vm.HasSecondaryPed = true;
        _vm.StatusText = "Loaded Ped B (female freemode). Use Swap to switch editing focus.";
        AppendDebug("info", "system", "Secondary ped loaded", "freemode_female.glb");
    }

    private async void OnSwapPedFocus(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady || !_vm.HasSecondaryPed) return;
        await Viewport.CoreWebView2.ExecuteScriptAsync("window.swapActivePed && window.swapActivePed()");
        _vm.ActivePedSlot = _vm.ActivePedSlot == "A" ? "B" : "A";
        _vm.StatusText = $"Switched editing focus to Ped {_vm.ActivePedSlot}.";
        AppendDebug("info", "system", $"Swapped active ped -> {_vm.ActivePedSlot}");
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
}

/// <summary>Floating window for the live pose/timeline/export event
/// stream. Bound to the same VM so the row list updates automatically
/// as the host appends entries. Kept free-floating so the user can
/// park it on a second monitor while iterating on a pose.</summary>
internal class DebugPopup : Window
{
    public DebugPopup(ViewModels.PoseToEmoteViewModel vm)
    {
        Title = "FiveOS pose debug log";
        Width = 760;
        Height = 460;
        Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#14161F")!;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DataContext = vm;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = "  Live log — pose / timeline / export events.  Press ` inside the viewport for the in-viewer overlay.",
            Foreground = ColorBrush("#8FB4E6"),
            FontSize = 11,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Padding = new Thickness(10, 8, 8, 8),
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Per-row TextBlock binding to the precomputed FormattedLine.
        // Avoids FrameworkElementFactory + grid template complexity at
        // the cost of column alignment — TimeLabel + level + category
        // are baked into the string with fixed-width padding.
        var lb = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(lb, ScrollBarVisibility.Auto);
        var template = new DataTemplate(typeof(ViewModels.DebugLogEntry));
        var tb = new FrameworkElementFactory(typeof(TextBlock));
        tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ViewModels.DebugLogEntry.FormattedLine)));
        // Color the row by level via a binding/converter would be cleaner
        // but a per-template trigger is enough for three levels.
        var trigger = new DataTrigger
        {
            Binding = new System.Windows.Data.Binding(nameof(ViewModels.DebugLogEntry.Level)),
            Value = "err",
        };
        trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, ColorBrush("#F87171")));
        var warnTrigger = new DataTrigger
        {
            Binding = new System.Windows.Data.Binding(nameof(ViewModels.DebugLogEntry.Level)),
            Value = "warn",
        };
        warnTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, ColorBrush("#FFD23F")));
        template.Triggers.Add(trigger);
        template.Triggers.Add(warnTrigger);
        template.VisualTree = tb;
        lb.ItemTemplate = template;
        lb.SetBinding(ListBox.ItemsSourceProperty, new System.Windows.Data.Binding(nameof(vm.DebugEntries)));
        Grid.SetRow(lb, 1);
        root.Children.Add(lb);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8),
        };
        var clearBtn = new Button { Content = "Clear", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        clearBtn.Click += (_, __) =>
        {
            vm.DebugEntries.Clear();
            vm.RecentErrorCount = 0;
        };
        var copyBtn = new Button { Content = "Copy all", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        copyBtn.Click += (_, __) =>
        {
            var text = string.Join('\n', vm.DebugEntries.Select(e => e.FormattedLine));
            try { System.Windows.Clipboard.SetText(text); }
            catch (Exception ex) { Services.FosLogger.Warn("clipboard", "copy failed", ex); }
        };
        var closeBtn = new Button { Content = "Close", Padding = new Thickness(10, 4, 10, 4) };
        closeBtn.Click += (_, __) => Close();
        footer.Children.Add(clearBtn);
        footer.Children.Add(copyBtn);
        footer.Children.Add(closeBtn);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
    }

    private static System.Windows.Media.SolidColorBrush ColorBrush(string hex)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
        return new System.Windows.Media.SolidColorBrush(c);
    }
}
