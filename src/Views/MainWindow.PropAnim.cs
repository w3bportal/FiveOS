// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FiveOS.Services;
using FiveOS.ViewModels;

namespace FiveOS.Views;

/// <summary>
/// Props stage — Animated mode. The same viewer that previews the prop flips
/// into the bone editor (viewer.html <c>fiveosBoneAnim.loadObject()</c>):
/// every part of the model becomes a bone, the gizmo poses it, keys land on
/// the in-page timeline, and Convert bakes the timeline into the prop's
/// <c>.ycd</c> (+ skeleton in the .ydr, auto-start ytyp) through the engine.
/// This partial owns the host side: the Props | Animated switch, the
/// viewer-state echo into <see cref="MainViewModel.PropAnim"/>, the bones
/// outliner, keyboard routing and the keys sidecar the converter reads.
/// </summary>
public partial class MainWindow
{
    private BoneAnimViewModel PropAnimVm => _vm.PropAnim;
    private bool _propAnimHooked;
    private bool _propAnimEditorLive;          // loadObject() succeeded on the current model
    private string? _propAnimPendingSession;   // keys stashed across a Props ↔ Animated flip
    private string? _propAnimSelName;
    private string _propAnimKeyedSig = "";
    private string? _propAnimImportSig;        // last clip-import summary reported by the viewer
    private string? _propAnimKeysSidecar;      // last sampled timeline, for the converter
    private int _propAnimModeGen;              // bumps per mode change so a stale enter/leave bails
    private int _propAnimKeysRev = -1;         // viewer key revision the sidecar was sampled at
    private bool _propAnimSidecarQueued;
    // Keys per model path: a layer parked from Animated mode keeps its keys
    // until it is previewed again.
    private readonly Dictionary<string, string> _propAnimSessions = new(StringComparer.OrdinalIgnoreCase);

    // ═══════════════ Mode ═══════════════

    /// <summary>Entering Animated: hand the loaded prop to the editor and fill
    /// the bones list. Leaving: stash the keys, give the stage back to the
    /// previewer. Safe to call before the viewer or a model is ready — the
    /// "loaded" handler calls again once there is something to animate.</summary>
    private async Task ApplyPropStageModeAsync()
    {
        HookPropAnimVm();
        if (_vm.IsAnimatedPropsView)
        {
            if (!_viewerReady || Viewport?.CoreWebView2 == null || !_vm.HasModel || _vm.IsModelLoading) return;
            // Bump only once we know this call will really do something: a
            // no-op invocation that bumped here used to invalidate a real
            // one already in flight, whose continuation then tore the
            // editor down — taking the imported clip with it.
            int gen = ++_propAnimModeGen;
            var skel = await PropExecReturnAsync("window.fiveosBoneAnim.loadObject()");
            if (gen != _propAnimModeGen || !_vm.IsAnimatedPropsView)
            {
                // Superseded. Only tear the editor down if the stage genuinely
                // left Animated — another enter is allowed to take over.
                if (!_vm.IsAnimatedPropsView) await PropExecAsync("window.fiveosBoneAnim.deactivate()");
                return;
            }
            if (string.IsNullOrEmpty(skel))
            {
                _propAnimEditorLive = false;
                PropAnimVm.SetStatus("Nothing to animate yet — load a model first.");
                return;
            }
            // What the model's own animation brought in (queried directly —
            // the same return path the bone list above came back on).
            var importDiag = await PropExecReturnAsync(
                "JSON.stringify(JSON.parse(window.fiveosBoneAnim.getState()).imported || null)");
            FosLogger.Info("propanim", "clip import: " + (importDiag ?? "(none)"));

            _propAnimEditorLive = true;
            PropAnimVm.ModelPath = _vm.SourcePath;
            PropAnimVm.ModelName = _vm.PropName ?? "";
            PopulatePropBones(skel);
            await PropExecAsync("window.fiveosBoneAnim.setShowDots(true)");
            var session = _propAnimPendingSession;
            if (session == null && _vm.SourcePath != null && _propAnimSessions.TryGetValue(_vm.SourcePath, out var kept))
                session = kept;
            _propAnimPendingSession = null;
            if (session != null)
            {
                var applied = await PropExecReturnAsync($"JSON.stringify(window.fiveosBoneAnim.setSession({session}))");
                FosLogger.Info("propanim", $"setSession replayed ({session.Length} bytes) -> {applied}");
            }
            int parts = PropAnimVm.Bones.Count(b => b.HasGeometry);
            PropAnimVm.ModelInfo = $"{Path.GetFileName(_vm.SourcePath ?? "")} · {parts} part{(parts == 1 ? "" : "s")}";
            PropAnimVm.SetStatus(parts <= 1
                ? "Drag the gizmo (E rotate, W move), then Convert — the motion ships inside the prop."
                : "Click a part, drag the gizmo, and the key lands on the timeline. Convert bakes it into the prop.");
            Viewport.Focus();
        }
        else
        {
            _propAnimModeGen++;   // cancel any enter still in flight
            if (_viewerReady && Viewport?.CoreWebView2 != null)
            {
                await StashPropAnimSessionAsync();
                // Always tell the viewer, even when the host thinks the editor
                // is idle: an enter that was still in flight may have activated it.
                await PropExecAsync("window.fiveosBoneAnim.deactivate()");
            }
            _propAnimEditorLive = false;
            _propAnimKeysSidecar = null;
            ClearPropBones();
        }
    }

    /// <summary>Keep the current keys for the next re-entry on this model
    /// (mode flip, decimate preview reload, park + re-preview).</summary>
    private async Task StashPropAnimSessionAsync()
    {
        if (!_propAnimEditorLive || PropAnimVm.TotalKeys <= 0) return;
        var stash = await PropExecReturnAsync("window.fiveosBoneAnim.getSession()");
        if (string.IsNullOrEmpty(stash) || !SessionHasKeys(stash))
        {
            // The viewer had already emptied itself (it stops posting state on
            // deactivate, so TotalKeys above can be stale). Storing this would
            // replay an empty session over the next import and wipe it.
            if (_vm.SourcePath != null) _propAnimSessions.Remove(_vm.SourcePath);
            return;
        }
        _propAnimPendingSession = stash;
        if (_vm.SourcePath != null) _propAnimSessions[_vm.SourcePath] = stash;
    }

    /// <summary>True when a session JSON actually carries keyed bones.</summary>
    private static bool SessionHasKeys(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("bones", out var b)
                && b.ValueKind == JsonValueKind.Array && b.GetArrayLength() > 0;
        }
        catch { return false; }
    }

    /// <summary>A new model is being loaded — keys belonged to the old one.</summary>
    private void ResetPropAnimForNewModel()
    {
        _propAnimPendingSession = null;
        _propAnimKeysSidecar = null;
        _propAnimKeysRev = -1;
        _propAnimEditorLive = false;
        ClearPropBones();
    }

    /// <summary>The model on the stage is about to be swapped for a parked
    /// layer (or parked itself): keep its keys, forget the editor state, and
    /// re-enter Animated on whatever becomes active.</summary>
    private async Task PropAnimBeforeParkAsync()
    {
        if (!_vm.IsAnimatedPropsView || !_propAnimEditorLive) return;
        await StashPropAnimSessionAsync();
        _propAnimPendingSession = null;   // the stash is per path; the next model gets its own
        _propAnimEditorLive = false;
        _propAnimKeysSidecar = null;
        ClearPropBones();
    }

    private void HookPropAnimVm()
    {
        if (_propAnimHooked) return;
        _propAnimHooked = true;
        PropAnimVm.PropertyChanged += OnPropAnimVmPropertyChanged;
        _vm.SelectedOutlinerNodes.CollectionChanged += (_, _) => PushPropAnimSelection();
    }

    /// <summary>Hand the viewer everything picked in the LAYERS tree, so one
    /// gizmo drag moves the whole selection instead of the last part clicked.</summary>
    private bool _propAnimSelectionQueued;
    private void PushPropAnimSelection()
    {
        // Building a selection raises one change per row, so settle first and
        // send the finished list once.
        if (_propAnimSelectionQueued) return;
        _propAnimSelectionQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _propAnimSelectionQueued = false;
            var vm = PropAnimVm;
            if (vm.SyncingFromViewer || !_propAnimEditorLive || !_vm.IsAnimatedPropsView) return;
            var names = PropAnimSelectedBones().Select(b => b.Name).ToList();
            if (names.Count == 0) return;
            _ = PropExecAsync($"window.fiveosBoneAnim.selectMany({JsonSerializer.Serialize(names)})");
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ═══════════════ Bones outliner ═══════════════

    private void PopulatePropBones(string skeletonJson)
    {
        var vm = PropAnimVm;
        vm.SyncingFromViewer = true;
        try
        {
            vm.Bones.Clear();
            using var doc = JsonDocument.Parse(skeletonJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                // Parents come through as indices into this same array.
                var byIndex = new Dictionary<int, string>();
                foreach (var b in doc.RootElement.EnumerateArray())
                    byIndex[GetI(b, "index", -1)] = b.TryGetProperty("name", out var bn) ? bn.GetString() ?? "" : "";

                foreach (var b in doc.RootElement.EnumerateArray())
                {
                    var name = b.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(name) || name == "root") continue;   // the synthetic root is never animated
                    var pi = GetI(b, "parent", -1);
                    var parentName = byIndex.TryGetValue(pi, out var pn) && pn != "root" ? pn : null;
                    var row = new BoneRow
                    {
                        Name = name,
                        Depth = Math.Max(0, GetI(b, "depth") - 1),
                        HasGeometry = GetB(b, "hasGeometry"),
                        Kind = GetB(b, "hasGeometry") ? "part" : null,
                        ParentName = string.IsNullOrEmpty(parentName) ? null : parentName,
                    };
                    row.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(BoneRow.IsVisible) && s is BoneRow r)
                            OnBoneRowVisibilityChanged(r);
                    };
                    vm.Bones.Add(row);
                }
            }
            vm.SelectedBone = null;
            _propAnimSelName = null; _propAnimKeyedSig = "";
            vm.RefilterBones();
            _vm.ShowBoneOutliner(vm.VisibleBones);
            _vm.IsLayersPanelOpen = true;
        }
        catch (Exception ex) { FosLogger.Warn("propanim", "bone list parse failed: " + ex.Message); }
        finally { vm.SyncingFromViewer = false; }
    }

    private void ClearPropBones()
    {
        var vm = PropAnimVm;
        vm.SyncingFromViewer = true;
        try
        {
            vm.Bones.Clear(); vm.SelectedBone = null; vm.RefilterBones();
            _vm.HideBoneOutliner();
            vm.TotalKeys = 0; vm.KeyedBoneCount = 0; vm.CanUndo = vm.CanRedo = false;
            vm.SelName = null; vm.ModelInfo = "";
            _propAnimSelName = null; _propAnimKeyedSig = "";
        }
        finally { vm.SyncingFromViewer = false; }
    }

    // ═══════════════ Viewer → VM ═══════════════

    /// <summary>Editor messages (<c>vanim-*</c>) from the Props viewer. Runs on
    /// the dispatcher with a cloned element.</summary>
    private void HandlePropAnimMessage(string kind, JsonElement root)
    {
        if (!_vm.IsAnimatedPropsView) return;
        switch (kind)
        {
            case "vanim-state": ApplyPropAnimViewerState(root); break;
            case "vanim-request-save": _vm.StatusText = "Keys live with the model here — Convert to bake them into the prop."; break;
        }
    }

    private void ApplyPropAnimViewerState(JsonElement s)
    {
        var vm = PropAnimVm;
        vm.SyncingFromViewer = true;
        try
        {
            double time = GetD(s, "time"), duration = GetD(s, "duration", vm.DurationSec);
            int frame = GetI(s, "frame"), frames = GetI(s, "frames", vm.TotalFrames);
            vm.CurrentFrame = frame; vm.TotalFrames = frames;
            vm.TimeText = $"{frame} / {frames} · {time.ToString("0.00", CultureInfo.InvariantCulture)}s";
            vm.IsPlaying = GetB(s, "playing");
            vm.Loop = GetB(s, "loop", vm.Loop);
            vm.AutoKey = GetB(s, "autoKey", vm.AutoKey);
            vm.Fps = GetI(s, "fps", vm.Fps);
            vm.DurationSec = duration;
            if (s.TryGetProperty("ease", out var ez) && ez.ValueKind == JsonValueKind.String) vm.Ease = ez.GetString() ?? vm.Ease;
            vm.CanUndo = GetB(s, "canUndo"); vm.CanRedo = GetB(s, "canRedo");
            int prevKeys = vm.TotalKeys;
            vm.TotalKeys = GetI(s, "totalKeys");
            if (prevKeys > 0 && vm.TotalKeys == 0)
                FosLogger.Warn("propanim", $"timeline emptied: {prevKeys} -> 0 keys (duration {GetD(s, "duration"):0.###})");
            if (vm.TotalKeys == 0) _propAnimKeysSidecar = null;
            int rev = GetI(s, "keysRev", -1);
            vm.KeysRev = rev;
            if (rev != _propAnimKeysRev && !_propAnimSidecarQueued)
            {
                // Re-sample shortly after the keys settle so every snapshot path
                // (including the synchronous drag-to-group one) sees fresh keys.
                _propAnimSidecarQueued = true;
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(300);
                    _propAnimSidecarQueued = false;
                    if (_vm.IsAnimatedPropsView && _propAnimEditorLive) await EnsurePropAnimKeysSidecarAsync();
                });
            }

            if (s.TryGetProperty("imported", out var imp) && imp.ValueKind == JsonValueKind.Object)
            {
                var importSig = imp.GetRawText();
                if (importSig != _propAnimImportSig)
                {
                    _propAnimImportSig = importSig;
                    int ikeys = GetI(imp, "keys"), ibones = GetI(imp, "bones"), cand = GetI(imp, "candidates");
                    double idur = GetD(imp, "duration");
                    FosLogger.Info("propanim", $"clip import: {importSig}");
                    vm.SetStatus(ikeys > 0
                        ? $"Imported this model's own animation — {ikeys} key{(ikeys == 1 ? "" : "s")} on {ibones} part{(ibones == 1 ? "" : "s")}, {idur:0.##}s. Edit it here, or Convert to ship it."
                        : cand > 0
                            ? "This model ships an animation, but none of its parts move relative to their parent — nothing to key. Pose the parts yourself, or Convert to ship the file's own clip."
                            : vm.StatusText);
                }
            }

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (s.TryGetProperty("keyedBones", out var kb) && kb.ValueKind == JsonValueKind.Array)
                foreach (var b in kb.EnumerateArray())
                    if (b.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                        counts[n.GetString() ?? ""] = GetI(b, "count");
            vm.KeyedBoneCount = counts.Count;
            var sig = string.Join("|", counts.Keys.OrderBy(x => x));
            foreach (var row in vm.Bones)
            {
                int c = counts.TryGetValue(row.Name, out var v) ? v : 0;
                if (row.KeyCount != c) row.KeyCount = c;
            }

            string? selName = null;
            if (s.TryGetProperty("selected", out var sel) && sel.ValueKind == JsonValueKind.Object)
            {
                selName = sel.TryGetProperty("name", out var sn) ? sn.GetString() : null;
                vm.SelName = selName;
                vm.SelKeys = GetI(sel, "keys");
                vm.SelKeyHere = GetB(sel, "keyHere");
            }
            else { vm.SelName = null; vm.SelKeys = 0; vm.SelKeyHere = false; }

            bool selChanged = !string.Equals(selName, _propAnimSelName, StringComparison.Ordinal);
            bool keyedChanged = sig != _propAnimKeyedSig;
            _propAnimSelName = selName; _propAnimKeyedSig = sig;
            if (selChanged)
                foreach (var row in vm.Bones) row.IsSelected = selName != null && string.Equals(row.Name, selName, StringComparison.OrdinalIgnoreCase);
            if (selChanged || keyedChanged)
            {
                var before = vm.VisibleBones.ToList();
                vm.RefilterBones();
                if (!before.SequenceEqual(vm.VisibleBones))
                    _vm.ShowBoneOutliner(vm.VisibleBones);
                var row = selName == null ? null
                    : vm.Bones.FirstOrDefault(b => string.Equals(b.Name, selName, StringComparison.OrdinalIgnoreCase));
                // A pick made on the stage highlights that one part; a pick that
                // is already inside the user's outliner selection leaves the
                // whole group alone.
                var picked = PropAnimSelectedBones();
                bool keepMulti = picked.Count > 1 && row != null && picked.Contains(row);
                if (!keepMulti)
                {
                    if (!ReferenceEquals(vm.SelectedBone, row)) vm.SelectedBone = row;
                    if (row != null) SelectBoneInOutliner(row);
                }
            }
        }
        finally { vm.SyncingFromViewer = false; }
    }

    // ═══════════════ VM → viewer ═══════════════

    private void OnPropAnimVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var vm = PropAnimVm;
        if (vm.SyncingFromViewer || !_propAnimEditorLive || !_vm.IsAnimatedPropsView) return;
        if (e.PropertyName == nameof(BoneAnimViewModel.SelectedBone)
            && vm.SelectedBone != null
            && !string.Equals(vm.SelectedBone.Name, vm.SelName, StringComparison.OrdinalIgnoreCase))
            _ = PropExecAsync($"window.fiveosBoneAnim.select({JsStr(vm.SelectedBone.Name)})");
    }

    // ═══════════════ Stage bar / panel actions ═══════════════

    /// <summary>The Props | Animated tabs above the viewport.</summary>
    private void OnPropStageModeClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        _vm.OpenViewCommand.Execute(tag);
    }

    private void OnPropAnimUndo(object sender, RoutedEventArgs e) => FirePropAnim("window.fiveosBoneAnim.undo()");
    private void OnPropAnimRedo(object sender, RoutedEventArgs e) => FirePropAnim("window.fiveosBoneAnim.redo()");
    private void OnPropAnimResetAll(object sender, RoutedEventArgs e) => FirePropAnim("window.fiveosBoneAnim.resetAll()");
    private void OnPropAnimClearKeys(object sender, RoutedEventArgs e) => FirePropAnim("window.fiveosBoneAnim.clearAllKeys()");

    /// <summary>One-click turntable on the selected part (or the only part).</summary>
    private void OnPropAnimSpin(object sender, RoutedEventArgs e)
    {
        // Labels are in game terms: Vertical = game Z (viewer Y), X = X, Y = game forward (viewer -Z).
        var axis = PropAnimSpinAxis?.SelectedIndex switch { 1 => "x", 2 => "gy", _ => "y" };
        bool reverse = PropAnimSpinReverse?.IsChecked == true;
        var targets = PropAnimActionTargets();
        if (targets.Count == 0) { PropAnimVm.SetStatus("Pick a part first — click one in BONES, or Shift+click a range."); return; }
        foreach (var t in targets)
            FirePropAnim($"window.fiveosBoneAnim.spin({JsStr(t.Name)}, {JsStr(axis)}, {Js(reverse)})");
        PropAnimVm.SetStatus(targets.Count == 1
            ? $"'{targets[0].Name}' now turns a full circle over the clip length. Convert to ship it."
            : $"{targets.Count} parts now turn a full circle over the clip length. Convert to ship it.");
    }

    private void FirePropAnim(string js)
    {
        if (!_propAnimEditorLive) return;
        _ = PropExecAsync(js);
        Viewport.Focus();
    }

    /// <summary>Editor shortcuts while keyboard focus is on the WPF side. Inside
    /// the viewer the page handles them itself. Returns true when consumed.</summary>
    private bool HandlePropAnimKey(KeyEventArgs e, bool ctrl, bool shift)
    {
        if (!_propAnimEditorLive) return false;
        var focused = Keyboard.FocusedElement;
        if (focused is System.Windows.Controls.Primitives.TextBoxBase || focused is PasswordBox || focused is ComboBox) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        string? js = null;
        if (ctrl && key == Key.Z && !shift) js = "undo()";
        else if (ctrl && (key == Key.Y || (key == Key.Z && shift))) js = "redo()";
        else if (!ctrl && focused is not System.Windows.Controls.Primitives.ButtonBase)
        {
            if (key == Key.Space) js = "togglePlay()";
            else if (key == Key.K) js = "addKey()";
        }
        if (js == null) return false;
        _ = PropExecAsync("window.fiveosBoneAnim." + js);
        e.Handled = true;
        return true;
    }

    // ═══════════════ Convert hand-off ═══════════════

    /// <summary>Sample the editor's timeline into the keys sidecar the engine
    /// reads (<c>--anim-keys</c>). Null when there is nothing keyed — the
    /// converter then falls back to the source file's own clip, if any.</summary>
    private async Task EnsurePropAnimKeysSidecarAsync()
    {
        if (!_vm.IsAnimatedPropsView || !_propAnimEditorLive || PropAnimVm.TotalKeys <= 0) { _propAnimKeysSidecar = null; return; }
        var sample = await PropExecReturnAsync("window.fiveosBoneAnim.sample()");
        if (string.IsNullOrEmpty(sample) || !sample.Contains("\"skeleton\"", StringComparison.Ordinal)) { _propAnimKeysSidecar = null; return; }
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "FiveOS");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"anim-keys-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, sample);
            _propAnimKeysSidecar = path;
            _propAnimKeysRev = PropAnimVm.KeysRev;
        }
        catch (Exception ex) { FosLogger.Warn("propanim", "keys sidecar failed: " + ex.Message); }
    }

    // ═══════════════ JS plumbing ═══════════════

    private async Task PropExecAsync(string js)
    {
        if (!_viewerReady || Viewport?.CoreWebView2 == null) return;
        try { await Viewport.CoreWebView2.ExecuteScriptAsync(js); } catch { /* best-effort */ }
    }

    /// <summary>Run JS and return its string result (or the raw JSON when the
    /// script returns an object). Null on failure / undefined.</summary>
    private async Task<string?> PropExecReturnAsync(string js)
    {
        if (!_viewerReady || Viewport?.CoreWebView2 == null) return null;
        try
        {
            var raw = await Viewport.CoreWebView2.ExecuteScriptAsync(js);
            if (string.IsNullOrEmpty(raw) || raw == "null" || raw == "undefined") return null;
            if (raw.StartsWith('"')) return JsonSerializer.Deserialize<string>(raw);
            return raw;
        }
        catch { return null; }
    }

    /// <summary>Push a part's visibility to the viewer, and carry the change
    /// across the rest of the selection when several parts are selected.</summary>
    private bool _propAnimBulkVisibility;

    /// <summary>The parts highlighted in the LAYERS tree.</summary>
    private List<BoneRow> PropAnimSelectedBones()
        => _vm.SelectedOutlinerNodes.Select(n => n.Bone).Where(b => b is not null).Select(b => b!).ToList();

    private void SelectBoneInOutliner(BoneRow row)
    {
        var node = _vm.FindOutlinerBoneNode(row);
        if (node is not null) _vm.SetOutlinerSelection(new[] { node }, additive: false);
    }

    private void OnBoneRowVisibilityChanged(BoneRow r)
    {
        _ = PropExecAsync($"window.fiveosBoneAnim.setBoneVisible({JsStr(r.Name)}, {Js(r.IsVisible)})");
        if (_propAnimBulkVisibility) return;                 // already inside a bulk apply
        var picked = PropAnimSelectedBones();
        if (picked.Count < 2 || !picked.Contains(r)) return;
        _propAnimBulkVisibility = true;
        try
        {
            foreach (var other in picked)
                if (!ReferenceEquals(other, r)) other.IsVisible = r.IsVisible;
        }
        finally { _propAnimBulkVisibility = false; }
    }

    /// <summary>The parts an action should apply to: everything selected in the
    /// LAYERS tree, or the single selected part, or the only part there is.</summary>
    private List<BoneRow> PropAnimActionTargets()
    {
        var picked = PropAnimSelectedBones();
        if (picked.Count > 0) return picked;
        if (PropAnimVm.SelectedBone != null) return new List<BoneRow> { PropAnimVm.SelectedBone };
        var only = PropAnimVm.Bones.Where(b => b.HasGeometry).ToList();
        return only.Count == 1 ? only : new List<BoneRow>();
    }

    private static string Js(bool b) => b ? "true" : "false";
    private static string JsStr(string s) => JsonSerializer.Serialize(s ?? "");
    private static double GetD(JsonElement e, string name, double def = 0)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;
    private static int GetI(JsonElement e, string name, int def = 0)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble()) : def;
    private static bool GetB(JsonElement e, string name, bool def = false)
        => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && def) : def;
}
