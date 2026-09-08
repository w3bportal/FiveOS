// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FiveOS.Services;
using FiveOS.ViewModels;

namespace FiveOS.Views;

/// <summary>
/// Vehicles stage — Animate mode. The same car-preview WebView2 flips into
/// the Vehicle Animation editor (viewer.html <c>fiveosVehicleAnim</c>): the
/// loaded .yft's bones become a live skeleton, the gizmo poses them, keys
/// land on a timeline under the viewport, and Export bakes a .ycd resource.
/// This partial owns the host side: mode switching, the viewer-state echo
/// into <see cref="BoneAnimViewModel"/>, sessions and export.
/// </summary>
public partial class VehiclesView
{
    private BoneAnimViewModel? AnimVm => Vm?.Anim;
    private BoneAnimViewModel? _animHooked;

    // Last built car — re-sent to whichever loader the stage mode wants.
    private string? _lastPayloadJson;
    private List<VehicleMeshBuilder.BoneInfo>? _lastSkeleton;
    private List<VehicleMeshBuilder.GroupInfo>? _lastGroups;
    private List<VehicleMeshBuilder.PieceInfo>? _lastPieces;
    // Keys survive Preview↔Animate flips and the textures-landed reload.
    private string? _pendingAnimSession;
    private string? _lastSelName;
    private string _lastKeyedSig = "";

    // ═══════════════ Mode ═══════════════

    /// <summary>Put the viewer into the editor (the stage's only mode now) and
    /// re-send the current car. Keys are stashed across a re-send.</summary>
    private async Task ApplyStageModeAsync()
    {
        var vm = Vm; if (vm == null) return;
        if (!_carWebReady) await InitCarPreviewAsync();
        if (!_carViewerReady) return;   // the ready handler re-applies

        if (vm.IsAnimateMode)
        {
            if (AnimVm != null && string.IsNullOrEmpty(AnimVm.OutputFolder))
                AnimVm.OutputFolder = UserSettings.LoadVehicleAnimOutputFolder();
            PushAnimSettings();
            if (PaintCol.Width.Value < 240) PaintCol.Width = new GridLength(250);
            if (_lastPayloadJson != null)
            {
                await CarExecAsync($"window.fiveosVehicleAnim.load({KeepCamera(_lastPayloadJson)})");
                PopulateBones(_lastSkeleton);
                await ApplyPendingSessionAsync();
                AnimVm?.SetStatus("Click a door, bonnet or wheel on the model, drag the gizmo, and the key lands on the timeline.");
            }
            else
            {
                await CarExecAsync("window.fiveosVehicleAnim.activate()");
                AnimVm?.SetStatus("Pick a car on the left or drop a .yft on the stage to start animating.");
            }
        }
        else
        {
            if (_lastPayloadJson != null && AnimVm != null && AnimVm.TotalKeys > 0)
            {
                var stash = await CarExecReturnAsync("window.fiveosVehicleAnim.getSession()");
                if (!string.IsNullOrEmpty(stash)) _pendingAnimSession = stash;
            }
            await CarExecAsync("window.fiveosVehicleAnim.deactivate()");
            if (_lastPayloadJson != null)
            {
                await CarExecAsync($"window.fiveosWorkbench.loadLod({KeepCamera(_lastPayloadJson)})");
                if (_lastGroups != null) PopulateParts(_lastGroups);
                DoorsToggle.IsChecked = false;
            }
        }
        CarPreview.Focus();
    }

    /// <summary>The payload as built asks the viewer to re-frame the camera;
    /// a mode flip must keep the user's orbit.</summary>
    private static string KeepCamera(string payload)
    {
        const string key = "\"frame\":true";
        int i = payload.IndexOf(key, StringComparison.Ordinal);
        return i < 0 || i > 200 ? payload : payload.Substring(0, i) + "\"frame\":false" + payload.Substring(i + key.Length);
    }

    private void HookAnimVm()
    {
        var vm = AnimVm;
        if (ReferenceEquals(_animHooked, vm)) return;
        if (_animHooked != null) _animHooked.PropertyChanged -= OnAnimVmPropertyChanged;
        _animHooked = vm;
        if (_animHooked != null) _animHooked.PropertyChanged += OnAnimVmPropertyChanged;
    }

    private void PushAnimSettings()
    {
        var vm = AnimVm; if (vm == null) return;
        _ = CarExecAsync($"window.fiveosVehicleAnim.setFps({vm.Fps});"
            + $"window.fiveosVehicleAnim.setDuration({F(vm.DurationSec)});"
            + $"window.fiveosVehicleAnim.setLoop({Js(vm.Loop)});"
            + $"window.fiveosVehicleAnim.setAutoKey({Js(vm.AutoKey)});"
            + $"window.fiveosVehicleAnim.setEase({JsStr(vm.Ease)});"
            + $"window.fiveosVehicleAnim.setShowDots({Js(vm.ShowDots)});");
    }

    /// <summary>Called by LoadModelAsync once the car is built — sends it to
    /// the editor (Animate) and refreshes the bone list.</summary>
    private async Task ShowCarInEditorAsync(VehicleMeshBuilder.BuildResult r, string yftPath)
    {
        var vm = AnimVm; if (vm == null) return;
        HookAnimVm();
        FosLogger.Info("vehanim", $"editor loaded {yftPath}: {r.Skeleton.Count} bones, {r.TotalParts} parts, geometry on [{string.Join(", ", r.Skeleton.Where(b => b.HasGeometry).Select(b => b.Name))}]");
        vm.ModelPath = yftPath;
        vm.ModelName = ModelBaseName(yftPath);
        if (string.IsNullOrWhiteSpace(vm.ClipName) || vm.ClipName.EndsWith("_anim", StringComparison.OrdinalIgnoreCase))
            vm.ClipName = VehicleAnimResourceBuilder.SanitizeName(vm.ModelName + "_anim");
        PushAnimSettings();
        await CarExecAsync($"window.fiveosVehicleAnim.load({r.PayloadJson})");
        PopulateBones(r.Skeleton);
        await ApplyPendingSessionAsync();
        int animatable = r.Skeleton.Count(b => b.HasGeometry || b.Kind != null);
        vm.ModelInfo = $"{Path.GetFileName(yftPath)} · {r.Skeleton.Count} bones ({animatable} animatable)";
        vm.SetStatus(r.Skeleton.Count == 0
            ? "This .yft has no skeleton — nothing to animate. Try the model's main or _hi .yft."
            : "Click a part to select its bone, drag the gizmo, and the key lands on the timeline.");
        CarPreview.Focus();
    }

    private async Task ApplyPendingSessionAsync()
    {
        if (_pendingAnimSession == null) return;
        var json = _pendingAnimSession; _pendingAnimSession = null;
        var res = await CarExecReturnAsync($"JSON.stringify(window.fiveosVehicleAnim.setSession({json}))");
        if (!string.IsNullOrEmpty(res) && res.Contains("\"missing\":[\"", StringComparison.Ordinal))
            AnimVm?.SetStatus("Keys restored — some keyed bones don't exist on this model and were skipped.");
    }

    private void PopulateBones(List<VehicleMeshBuilder.BoneInfo>? skeleton)
    {
        var vm = AnimVm; if (vm == null) return;
        vm.SyncingFromViewer = true;
        try
        {
            vm.Bones.Clear();
            if (skeleton != null)
                foreach (var b in skeleton)
                {
                    var row = new BoneRow { Name = b.Name, Tag = b.Tag, Kind = b.Kind, Depth = b.Depth, HasGeometry = b.HasGeometry };
                    // Eye checkbox → viewport. Rows die with the model, so no unhook needed.
                    row.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(BoneRow.IsVisible) && s is BoneRow r)
                            _ = CarExecAsync($"window.fiveosVehicleAnim.setBoneVisible({JsStr(r.Name)}, {Js(r.IsVisible)})");
                    };
                    vm.Bones.Add(row);
                }
            vm.SelectedBone = null;
            _lastSelName = null; _lastKeyedSig = "";
            vm.RefilterBones();
        }
        finally { vm.SyncingFromViewer = false; }
    }

    private static string ModelBaseName(string yftPath)
    {
        var stem = Path.GetFileNameWithoutExtension(yftPath);
        return stem.EndsWith("_hi", StringComparison.OrdinalIgnoreCase) ? stem[..^3] : stem;
    }

    /// <summary>Prefer the <c>_hi.yft</c> sibling: same skeleton, full meshes.</summary>
    private static string PreferHiLod(string yftPath)
    {
        try
        {
            var stem = Path.GetFileNameWithoutExtension(yftPath);
            if (stem.EndsWith("_hi", StringComparison.OrdinalIgnoreCase)) return yftPath;
            var hi = Path.Combine(Path.GetDirectoryName(yftPath) ?? "", stem + "_hi.yft");
            return File.Exists(hi) ? hi : yftPath;
        }
        catch { return yftPath; }
    }

    // ═══════════════ Viewer → VM ═══════════════

    /// <summary>Messages from the viewer other than "ready" (see
    /// OnCarViewerMessage). Runs on the dispatcher with a cloned element.</summary>
    private void HandleAnimViewerMessage(string kind, JsonElement root)
    {
        switch (kind)
        {
            case "vanim-state": ApplyViewerState(root); break;
            case "vanim-request-save": OnAnimSaveSession(this, new RoutedEventArgs()); break;
            case "vanim-split-request": OnSplitRequested(root); break;
        }
    }

    private void ApplyViewerState(JsonElement s)
    {
        var vm = AnimVm; if (vm == null) return;
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
            vm.TotalKeys = GetI(s, "totalKeys");

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
                vm.SelTag = (ushort)GetI(sel, "tag");
                vm.SelKeys = GetI(sel, "keys");
                vm.SelKeyHere = GetB(sel, "keyHere");
                if (sel.TryGetProperty("rot", out var r) && r.ValueKind == JsonValueKind.Array && r.GetArrayLength() >= 3)
                { vm.RotX = Math.Round(r[0].GetDouble(), 2); vm.RotY = Math.Round(r[1].GetDouble(), 2); vm.RotZ = Math.Round(r[2].GetDouble(), 2); }
                if (sel.TryGetProperty("pos", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 3)
                { vm.PosX = Math.Round(p[0].GetDouble(), 3); vm.PosY = Math.Round(p[1].GetDouble(), 3); vm.PosZ = Math.Round(p[2].GetDouble(), 3); }
            }
            else
            {
                vm.SelName = null; vm.SelTag = 0; vm.SelKeys = 0; vm.SelKeyHere = false;
                vm.RotX = vm.RotY = vm.RotZ = 0; vm.PosX = vm.PosY = vm.PosZ = 0;
            }

            bool selChanged = !string.Equals(selName, _lastSelName, StringComparison.Ordinal);
            bool keyedChanged = sig != _lastKeyedSig;
            _lastSelName = selName; _lastKeyedSig = sig;
            if (selChanged)
                foreach (var row in vm.Bones) row.IsSelected = selName != null && string.Equals(row.Name, selName, StringComparison.OrdinalIgnoreCase);
            // Refilter BEFORE picking the row: rebuilding the ListBox's items
            // nulls its bound SelectedItem.
            if (selChanged || keyedChanged)
            {
                vm.RefilterBones();
                var row = selName == null ? null
                    : vm.Bones.FirstOrDefault(b => string.Equals(b.Name, selName, StringComparison.OrdinalIgnoreCase));
                if (!ReferenceEquals(vm.SelectedBone, row)) vm.SelectedBone = row;
                if (row != null && vm.VisibleBones.Contains(row)) AnimBonesList.ScrollIntoView(row);
            }
        }
        finally { vm.SyncingFromViewer = false; }
    }

    // ═══════════════ VM → viewer ═══════════════

    private void OnAnimVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var vm = AnimVm; if (vm == null || vm.SyncingFromViewer || !_carViewerReady || Vm?.IsAnimateMode != true) return;
        switch (e.PropertyName)
        {
            case nameof(BoneAnimViewModel.Fps): _ = CarExecAsync($"window.fiveosVehicleAnim.setFps({vm.Fps})"); break;
            case nameof(BoneAnimViewModel.DurationSec): _ = CarExecAsync($"window.fiveosVehicleAnim.setDuration({F(vm.DurationSec)})"); break;
            case nameof(BoneAnimViewModel.Loop): _ = CarExecAsync($"window.fiveosVehicleAnim.setLoop({Js(vm.Loop)})"); break;
            case nameof(BoneAnimViewModel.AutoKey): _ = CarExecAsync($"window.fiveosVehicleAnim.setAutoKey({Js(vm.AutoKey)})"); break;
            case nameof(BoneAnimViewModel.Ease): _ = CarExecAsync($"window.fiveosVehicleAnim.setEase({JsStr(vm.Ease)})"); break;
            case nameof(BoneAnimViewModel.ShowDots): _ = CarExecAsync($"window.fiveosVehicleAnim.setShowDots({Js(vm.ShowDots)})"); break;
            case nameof(BoneAnimViewModel.Wireframe): _ = CarExecAsync($"window.fiveosVehicleAnim.setWireframe({Js(vm.Wireframe)})"); break;
            case nameof(BoneAnimViewModel.RotX):
            case nameof(BoneAnimViewModel.RotY):
            case nameof(BoneAnimViewModel.RotZ):
            case nameof(BoneAnimViewModel.PosX):
            case nameof(BoneAnimViewModel.PosY):
            case nameof(BoneAnimViewModel.PosZ):
                if (vm.HasSelection)
                    _ = CarExecAsync("window.fiveosVehicleAnim.setBoneOffset(null,"
                        + $"[{F(vm.RotX)},{F(vm.RotY)},{F(vm.RotZ)}],[{F(vm.PosX)},{F(vm.PosY)},{F(vm.PosZ)}])");
                break;
            case nameof(BoneAnimViewModel.SelectedBone):
                if (vm.SelectedBone != null && !string.Equals(vm.SelectedBone.Name, vm.SelName, StringComparison.OrdinalIgnoreCase))
                    _ = CarExecAsync($"window.fiveosVehicleAnim.select({JsStr(vm.SelectedBone.Name)})");
                break;
        }
    }

    // ═══════════════ Toolbar ═══════════════

    private async void OnAnimOpenModel(object sender, RoutedEventArgs e)
    {
        var path = await StaFileDialogs.OpenAsync(Window.GetWindow(this), d =>
        {
            d.Title = "Open a vehicle .yft to animate";
            d.Filter = "Vehicle model (*.yft)|*.yft|All files|*.*";
        });
        if (path != null) await OpenAnimFileAsync(path);
    }

    /// <summary>A .yft (any car, even outside the pack) or a .fvanim session,
    /// dropped on the tab or picked from the toolbar.</summary>
    private async Task OpenAnimFileAsync(string path)
    {
        var vm = Vm; if (vm == null) return;
        if (path.EndsWith(".fvanim", StringComparison.OrdinalIgnoreCase)) { await LoadAnimSessionFileAsync(path); return; }
        if (!vm.IsAnimateMode)
        {
            vm.IsAnimateMode = true;
            HookAnimVm();
            await ApplyStageModeAsync();
        }
        await LoadModelAsync(path);
    }

    /// <summary>Editor shortcuts while keyboard focus is on the WPF side (bone
    /// list, checkboxes, toolbar). Inside the 3D view the page handles them
    /// itself; this covers everywhere else on the tab.</summary>
    private void OnViewPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var vm = AnimVm; if (vm == null || !vm.HasModel) return;
        var focused = System.Windows.Input.Keyboard.FocusedElement;
        if (focused is System.Windows.Controls.Primitives.TextBoxBase || focused is PasswordBox || focused is ComboBox) return;   // typing
        var mods = System.Windows.Input.Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(System.Windows.Input.ModifierKeys.Control);
        bool shift = mods.HasFlag(System.Windows.Input.ModifierKeys.Shift);
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        string? js = null;
        if (ctrl && key == System.Windows.Input.Key.Z && !shift) js = "undo()";
        else if (ctrl && (key == System.Windows.Input.Key.Y || (key == System.Windows.Input.Key.Z && shift))) js = "redo()";
        else if (ctrl && key == System.Windows.Input.Key.S) { OnAnimSaveSession(this, new RoutedEventArgs()); e.Handled = true; return; }
        else if (!ctrl && focused is not System.Windows.Controls.Primitives.ButtonBase)
        {
            // Space / K only when nothing that uses those keys has focus.
            if (key == System.Windows.Input.Key.Space) js = "togglePlay()";
            else if (key == System.Windows.Input.Key.K) js = "addKey()";
        }
        if (js == null) return;
        _ = CarExecAsync("window.fiveosVehicleAnim." + js);
        e.Handled = true;
    }

    // ═══════════════ Split pieces onto a new bone ═══════════════
    // The picking happens in the viewport (hover → highlight → click); the
    // page posts the chosen vertices back and we only ask for a name.

    private void OnAnimSplitBone(object sender, RoutedEventArgs e)
        => FireAnim("window.fiveosVehicleAnim.setSplitMode(true)");

    private sealed record SplitPart(string Bone, VehicleBoneSplitter.Selection Selection);

    private async void OnSplitRequested(JsonElement req)
    {
        var vm = AnimVm; if (vm == null || !vm.HasModel || _lastSkeleton == null) return;
        int pieces = GetI(req, "pieces"), tris = GetI(req, "tris");

        // One entry per source bone: a part on a different bone can't share a
        // single new bone, so each gets its own — but it's one action.
        var groups = new List<SplitPart>();
        if (req.TryGetProperty("parts", out var partsEl) && partsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in partsEl.EnumerateArray())
            {
                var boneName = part.TryGetProperty("bone", out var bn) ? bn.GetString() : null;
                if (string.IsNullOrEmpty(boneName)) continue;
                var positions = new List<SharpDX.Vector3>();
                if (part.TryGetProperty("positions", out var ps) && ps.ValueKind == JsonValueKind.Array)
                    foreach (var p in ps.EnumerateArray())
                        if (p.GetArrayLength() >= 3)
                            positions.Add(new SharpDX.Vector3((float)p[0].GetDouble(), (float)p[1].GetDouble(), (float)p[2].GetDouble()));
                if (positions.Count == 0) continue;
                var bx = part.GetProperty("box"); var mn = bx.GetProperty("min"); var mx = bx.GetProperty("max");
                var box = new VehicleBoneSplitter.Box(
                    new SharpDX.Vector3((float)mn[0].GetDouble(), (float)mn[1].GetDouble(), (float)mn[2].GetDouble()),
                    new SharpDX.Vector3((float)mx[0].GetDouble(), (float)mx[1].GetDouble(), (float)mx[2].GetDouble()));
                groups.Add(new SplitPart(boneName, VehicleBoneSplitter.Selection.FromPositions(positions, box)));
            }
        }
        if (groups.Count == 0) { vm.SetStatus("Nothing selected to split."); return; }

        var existing = new HashSet<string>(_lastSkeleton.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
        string baseFrom = groups.Count == 1 ? groups[0].Bone : "part";
        string suggested = baseFrom + "_anim";
        for (int i = 2; existing.Contains(suggested); i++) suggested = baseFrom + "_anim" + i;
        var prompt = groups.Count == 1
            ? $"Name for the {pieces} piece{(pieces == 1 ? "" : "s")} ({tris:N0} tris) split off '{groups[0].Bone}':"
            : $"Name for the {pieces} pieces ({tris:N0} tris) split off {groups.Count} parts (each part gets its own bone, numbered):";
        var dlg = new RenameDialog("New bone", prompt, suggested, isFile: false,
            hint: "Letters, digits and underscores. Written into the car's .yft and _hi.yft (a .bak is kept next to each).")
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        var baseName = VehicleBoneSplitter.SanitizeBoneName(dlg.ResultName);
        if (baseName.Length == 0) return;

        // Unique names: one part → the name as-is; several → name, name2, name3…
        var used = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        for (int i = 0; i < groups.Count; i++)
        {
            string nm = groups.Count == 1 ? baseName : baseName + (i + 1);
            for (int j = 2; used.Contains(nm); j++) nm = baseName + (i + 1) + "_" + j;
            used.Add(nm); names.Add(nm);
        }

        var modelPath = vm.ModelPath!;
        vm.IsBusy = true;
        vm.SetStatus($"Splitting {pieces} piece(s) into {groups.Count} new bone(s)…");
        int totalMoved = 0, fileCount = 0; string? firstName = names.FirstOrDefault(); string? err = null;
        try
        {
            await Task.Run(() =>
            {
                var log = new List<string>();
                for (int i = 0; i < groups.Count; i++)
                {
                    var res = VehicleBoneSplitter.SplitVehicle(modelPath, groups[i].Bone, names[i], groups[i].Selection, log.Add);
                    if (!res.Ok) { err = res.Error; break; }
                    totalMoved += res.Files.Sum(f => f.VerticesMoved);
                    fileCount = res.Files.Count;
                }
                foreach (var l in log) FosLogger.Info("vehanim", "split: " + l);
            });
        }
        finally { vm.IsBusy = false; }

        if (err != null) { vm.SetStatus("Split failed: " + err); return; }
        vm.SetStatus(groups.Count == 1
            ? $"'{names[0]}' added — {totalMoved:N0} vertices moved in {fileCount} file(s); backups saved as .bak."
            : $"Added {groups.Count} bones ({string.Join(", ", names)}) — {totalMoved:N0} vertices moved; backups saved as .bak.");

        await LoadModelAsync(_loadedModelPath ?? modelPath);
        if (firstName != null) await CarExecAsync($"window.fiveosVehicleAnim.select({JsStr(firstName)})");
    }

    private void OnAnimUndo(object sender, RoutedEventArgs e) => FireAnim("window.fiveosVehicleAnim.undo()");
    private void OnAnimRedo(object sender, RoutedEventArgs e) => FireAnim("window.fiveosVehicleAnim.redo()");
    private void OnAnimAddKey(object sender, RoutedEventArgs e) => FireAnim("window.fiveosVehicleAnim.addKey()");
    private void OnAnimDeleteKey(object sender, RoutedEventArgs e) => FireAnim("window.fiveosVehicleAnim.deleteKeys()");
    private void OnAnimResetBone(object sender, RoutedEventArgs e) => FireAnim("window.fiveosVehicleAnim.resetBone()");
    private void OnAnimResetAll(object sender, RoutedEventArgs e) => FireAnim("window.fiveosVehicleAnim.resetAll()");
    private void OnAnimClearBoneKeys(object sender, RoutedEventArgs e) => FireAnim("window.fiveosVehicleAnim.clearBoneKeys(null)");

    private void FireAnim(string js)
    {
        _ = CarExecAsync(js);
        CarPreview.Focus();
    }

    private void OnAnimRenameClip(object sender, RoutedEventArgs e)
    {
        var vm = AnimVm; if (vm == null) return;
        var dlg = new RenameDialog("Clip name", "Clip / resource name:", vm.ClipName, isFile: false,
            hint: "Letters, digits and underscores. This names the .ycd, the FiveM resource and the /command.");
        dlg.Owner = Window.GetWindow(this);
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.ResultName))
            vm.ClipName = VehicleAnimResourceBuilder.SanitizeName(dlg.ResultName);
    }

    private void OnAnimExportMenu(object sender, RoutedEventArgs e)
    {
        if (AnimExportBtn.ContextMenu == null) return;
        AnimExportBtn.ContextMenu.PlacementTarget = AnimExportBtn;
        AnimExportBtn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        AnimExportBtn.ContextMenu.IsOpen = true;
    }

    // ═══════════════ Sessions (.fvanim) ═══════════════

    private async void OnAnimSaveSession(object sender, RoutedEventArgs e)
    {
        var vm = AnimVm; if (vm == null || !vm.HasModel) return;
        var session = await CarExecReturnAsync("window.fiveosVehicleAnim.getSession()");
        if (string.IsNullOrEmpty(session)) { vm.SetStatus("Nothing to save yet."); return; }

        var path = vm.SessionPath;
        if (string.IsNullOrEmpty(path))
        {
            path = await StaFileDialogs.SaveAsync(Window.GetWindow(this), d =>
            {
                d.Title = "Save vehicle animation session";
                d.Filter = "FiveOS vehicle animation (*.fvanim)|*.fvanim";
                d.FileName = VehicleAnimResourceBuilder.SanitizeName(vm.ClipName) + ".fvanim";
                d.InitialDirectory = Path.GetDirectoryName(vm.ModelPath!) ?? "";
            });
            if (path == null) return;
        }
        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("version", 1);
                w.WriteString("app", "FiveOS Vehicle Anim");
                w.WriteString("model", vm.ModelPath);
                w.WriteString("clipName", vm.ClipName);
                w.WritePropertyName("session");
                using (var sdoc = JsonDocument.Parse(session)) sdoc.RootElement.WriteTo(w);
                w.WriteEndObject();
            }
            File.WriteAllBytes(path, ms.ToArray());
            vm.SessionPath = path;
            vm.SetStatus("Session saved: " + path);
        }
        catch (Exception ex) { vm.SetStatus("Couldn't save the session: " + ex.Message); }
    }

    private async void OnAnimLoadSession(object sender, RoutedEventArgs e)
    {
        var path = await StaFileDialogs.OpenAsync(Window.GetWindow(this), d =>
        {
            d.Title = "Open a vehicle animation session";
            d.Filter = "FiveOS vehicle animation (*.fvanim)|*.fvanim|All files|*.*";
        });
        if (path != null) await LoadAnimSessionFileAsync(path);
    }

    private async Task LoadAnimSessionFileAsync(string path)
    {
        var vm = AnimVm; if (vm == null) return;
        string model, clip, session;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            model = root.TryGetProperty("model", out var m) ? (m.GetString() ?? "") : "";
            clip = root.TryGetProperty("clipName", out var c) ? (c.GetString() ?? "") : "";
            session = root.TryGetProperty("session", out var s) ? s.GetRawText() : "";
        }
        catch (Exception ex) { vm.SetStatus("Not a FiveOS session file: " + ex.Message); return; }
        if (string.IsNullOrEmpty(session)) { vm.SetStatus("That session has no keys in it."); return; }

        if (!File.Exists(model))
        {
            AppDialog.Info($"The session's vehicle isn't where it was:\n{model}\n\nPick the .yft it belongs to.", "Locate the vehicle", Window.GetWindow(this));
            var picked = await StaFileDialogs.OpenAsync(Window.GetWindow(this), d =>
            {
                d.Title = "Locate the session's vehicle .yft";
                d.Filter = "Vehicle model (*.yft)|*.yft";
            });
            if (picked == null) return;
            model = picked;
        }
        _pendingAnimSession = session;
        vm.SessionPath = path;
        if (!string.IsNullOrWhiteSpace(clip)) vm.ClipName = clip;
        await OpenAnimFileAsync(model);
    }

    // ═══════════════ Export ═══════════════

    private readonly record struct BakedClip(byte[] Bytes, string Xml, int Bones, int Frames, int Fps, bool Loop, string Clip);

    /// <summary>Sample the timeline at the clip FPS and bake it. Rotation
    /// tracks for every keyed bone; position tracks only where a bone leaves
    /// its rest position. No entity mover — bone 0 is the chassis.</summary>
    private async Task<BakedClip?> BakeClipAsync()
    {
        var vm = AnimVm; if (vm == null) return null;
        if (!vm.HasModel) { vm.SetStatus("Open a vehicle first."); return null; }
        var raw = await CarExecReturnAsync("window.fiveosVehicleAnim.sample()");
        if (string.IsNullOrEmpty(raw)) { vm.SetStatus("Couldn't read the timeline from the viewer."); return null; }

        int fps, frames; bool loop;
        var tracks = new List<PosedBoneTrack>();
        var positions = new List<PosedPositionTrack>();
        var seen = new HashSet<ushort>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            fps = Math.Clamp(GetI(root, "fps", 30), 1, 120);
            frames = Math.Max(2, GetI(root, "frames", 2));
            loop = GetB(root, "loop", true);
            if (root.TryGetProperty("bones", out var bones) && bones.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in bones.EnumerateArray())
                {
                    var tag = (ushort)GetI(b, "tag");
                    var name = b.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!seen.Add(tag)) { FosLogger.Warn("vehanim", $"duplicate bone tag {tag} ({name}) — second track skipped"); continue; }
                    var rot = b.GetProperty("rot"); var pos = b.GetProperty("pos");
                    var quats = new System.Numerics.Quaternion[frames];
                    var vecs = new System.Numerics.Vector3[frames];
                    int rf = Math.Max(1, Math.Min(frames, rot.GetArrayLength()));
                    int pf = Math.Max(1, Math.Min(frames, pos.GetArrayLength()));
                    for (int f = 0; f < frames; f++)
                    {
                        var q = rot[Math.Min(f, rf - 1)];
                        quats[f] = System.Numerics.Quaternion.Normalize(new System.Numerics.Quaternion(
                            (float)q[0].GetDouble(), (float)q[1].GetDouble(), (float)q[2].GetDouble(), (float)q[3].GetDouble()));
                        var p = pos[Math.Min(f, pf - 1)];
                        vecs[f] = new System.Numerics.Vector3((float)p[0].GetDouble(), (float)p[1].GetDouble(), (float)p[2].GetDouble());
                    }
                    tracks.Add(new PosedBoneTrack(tag, quats, name));

                    var rest = b.TryGetProperty("restPos", out var rp) && rp.GetArrayLength() >= 3
                        ? new System.Numerics.Vector3((float)rp[0].GetDouble(), (float)rp[1].GetDouble(), (float)rp[2].GetDouble())
                        : vecs[0];
                    float dev = 0f;
                    foreach (var v in vecs) dev = Math.Max(dev, System.Numerics.Vector3.Distance(v, rest));
                    if (dev > 0.0005f) positions.Add(new PosedPositionTrack(tag, vecs));
                }
            }
        }
        catch (Exception ex) { vm.SetStatus("Couldn't parse the timeline sample: " + ex.Message); return null; }

        if (tracks.Count == 0) { vm.SetStatus("Key at least one bone first (drag the gizmo with auto-key on, or press K)."); return null; }

        var clip = VehicleAnimResourceBuilder.SanitizeName(vm.ClipName);
        try
        {
            var xml = YcdAnimationBuilder.BuildXml(clip, tracks, frames, fps, positions.Count > 0 ? positions : null, emitMover: false);
            var bytes = YcdAnimationBuilder.Build(clip, tracks, frames, fps, positions.Count > 0 ? positions : null, emitMover: false);
            return new BakedClip(bytes, xml, tracks.Count, frames, fps, loop, clip);
        }
        catch (Exception ex)
        {
            vm.SetStatus("Baking the .ycd failed: " + ex.Message);
            FosLogger.Warn("vehanim", "ycd bake failed", ex);
            return null;
        }
    }

    private string AnimOutputRoot()
    {
        var root = AnimVm?.OutputFolder;
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FiveOS", "VehicleAnims");
        Directory.CreateDirectory(root);
        return root;
    }

    private async void OnAnimExportFiveM(object sender, RoutedEventArgs e)
    {
        var vm = AnimVm; if (vm == null) return;
        var baked = await BakeClipAsync(); if (baked == null) return;
        var b = baked.Value;
        vm.IsBusy = true;
        try
        {
            var folder = Path.Combine(AnimOutputRoot(), b.Clip);
            await Task.Run(() => VehicleAnimResourceBuilder.BuildFolder(folder, b.Clip, b.Bytes, b.Loop, b.Xml, vm.ModelName));
            vm.LastExportPath = folder;
            vm.SetStatus($"Exported {b.Bones} bone{(b.Bones == 1 ? "" : "s")}, {b.Frames} frames @ {b.Fps} fps → {folder}   (ensure {b.Clip}, then /{b.Clip} in game)");
            OpenFolderInExplorer(folder);
        }
        catch (Exception ex) { vm.SetStatus("Export failed: " + ex.Message); FosLogger.Warn("vehanim", "fivem export failed", ex); }
        finally { vm.IsBusy = false; }
    }

    private async void OnAnimExportDlc(object sender, RoutedEventArgs e)
    {
        var vm = AnimVm; if (vm == null) return;
        var baked = await BakeClipAsync(); if (baked == null) return;
        var b = baked.Value;
        vm.IsBusy = true;
        try
        {
            var rpf = await Task.Run(() => VehicleAnimResourceBuilder.BuildDlcRpf(AnimOutputRoot(), b.Clip, b.Bytes));
            vm.LastExportPath = Path.GetDirectoryName(rpf);
            vm.SetStatus($"Built {rpf} — {b.Bones} bone{(b.Bones == 1 ? "" : "s")}, {b.Frames} frames @ {b.Fps} fps");
            OpenFolderInExplorer(Path.GetDirectoryName(rpf)!);
        }
        catch (Exception ex) { vm.SetStatus("DLC export failed: " + ex.Message); FosLogger.Warn("vehanim", "dlc export failed", ex); }
        finally { vm.IsBusy = false; }
    }

    private async void OnAnimExportYcd(object sender, RoutedEventArgs e)
    {
        var vm = AnimVm; if (vm == null) return;
        var baked = await BakeClipAsync(); if (baked == null) return;
        var b = baked.Value;
        var dict = VehicleAnimResourceBuilder.DictFor(b.Clip);
        var path = await StaFileDialogs.SaveAsync(Window.GetWindow(this), d =>
        {
            d.Title = "Save clip dictionary";
            d.Filter = "RAGE clip dictionary (*.ycd)|*.ycd";
            d.FileName = dict + ".ycd";
            d.InitialDirectory = AnimOutputRoot();
        });
        if (path == null) return;
        try
        {
            File.WriteAllBytes(path, b.Bytes);
            File.WriteAllText(Path.ChangeExtension(path, ".ycd.xml"), b.Xml, new UTF8Encoding(false));
            vm.LastExportPath = Path.GetDirectoryName(path);
            vm.SetStatus($"Saved {path} — RequestAnimDict('{Path.GetFileNameWithoutExtension(path)}'), clip '{b.Clip}'");
        }
        catch (Exception ex) { vm.SetStatus("Couldn't write the .ycd: " + ex.Message); }
    }

    private async void OnAnimPickOutputFolder(object sender, RoutedEventArgs e)
    {
        var folder = await StaFileDialogs.OpenFolderAsync(Window.GetWindow(this), d =>
        {
            d.Title = "Where should exported vehicle animations go?";
            d.InitialDirectory = AnimOutputRoot();
        });
        if (folder == null || AnimVm == null) return;
        AnimVm.OutputFolder = folder;
        UserSettings.SaveVehicleAnimOutputFolder(folder);
        AnimVm.SetStatus("Exports go to " + folder);
    }

    private void OnAnimOpenOutput(object sender, RoutedEventArgs e)
    {
        var target = AnimVm?.LastExportPath;
        if (string.IsNullOrEmpty(target) || !Directory.Exists(target)) target = AnimOutputRoot();
        OpenFolderInExplorer(target);
    }

    private static void OpenFolderInExplorer(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch { /* explorer failure is non-fatal */ }
    }

    // ═══════════════ Drop helpers ═══════════════

    /// <summary>A single dropped .yft / .fvanim — handled by the editor
    /// rather than the pack queue.</summary>
    private static string? DroppedAnimFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length != 1) return null;
        var p = paths[0];
        return File.Exists(p) && (p.EndsWith(".yft", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".fvanim", StringComparison.OrdinalIgnoreCase))
            ? p : null;
    }

    // ═══════════════ JS plumbing ═══════════════

    /// <summary>Run JS and return its string result (or the raw JSON when the
    /// script returns an object). Null on failure / undefined.</summary>
    private async Task<string?> CarExecReturnAsync(string js)
    {
        if (!_carWebReady || CarPreview.CoreWebView2 == null) return null;
        try
        {
            var raw = await CarPreview.CoreWebView2.ExecuteScriptAsync(js);
            if (string.IsNullOrEmpty(raw) || raw == "null" || raw == "undefined") return null;
            if (raw.StartsWith('"')) return JsonSerializer.Deserialize<string>(raw);
            return raw;
        }
        catch { return null; }
    }

    private static string Js(bool b) => b ? "true" : "false";
    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    private static double GetD(JsonElement e, string name, double def = 0)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;
    private static int GetI(JsonElement e, string name, int def = 0)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble()) : def;
    private static bool GetB(JsonElement e, string name, bool def = false)
        => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && def) : def;
}
