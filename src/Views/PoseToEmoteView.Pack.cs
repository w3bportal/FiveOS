// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FiveOS.Services;
using FiveOS.ViewModels;

namespace FiveOS.Views;

/// <summary>
/// Emote Pack: queue several baked emotes, export them as ONE FiveM resource.
///
/// The queue holds finished .ycd bytes rather than a promise to bake later.
/// That isn't a shortcut — the viewer's sampler only works against the rig
/// that's loaded right now, and loading another model destroys the imported
/// clips outright, so "add now, bake at export" would produce empty emotes.
/// </summary>
public partial class PoseToEmoteView
{
    private EmotePackSession Pack => EmotePackSession.Current;

    /// <summary>Guards the bake. Sampling a long clip takes seconds, and two
    /// overlapping bakes would race the _lastYcdXml / _lastBakeHadRootMotion
    /// scratch fields and hand one entry another's metadata.</summary>
    private bool _addToPackBusy;

    private void OnSidebarTabPack(object sender, RoutedEventArgs e)
        => _vm.SidebarTab = PoseToEmoteViewModel.EmoteSidebarTab.Pack;

    /// <summary>
    /// A starting name for the Add dialog, or "" to leave the box empty.
    ///
    /// The emote tab's title first (three tabs then suggest three different
    /// names), then the source file's stem. Deliberately NOT DefaultExportName:
    /// for the built-in presets LoadedModelPath is the label "GTA Male
    /// (synthetic skeleton)", which sanitises to gta_male_synthetic_skeleton
    /// and would become the in-game command. Better an empty box that asks for
    /// a name than a bad one baked into the .ycd.
    /// </summary>
    private string SuggestPackEmoteName()
    {
        var title = _vm.EmoteDocs.ActiveDocument?.Title;
        if (!string.IsNullOrWhiteSpace(title) &&
            !title.Trim().StartsWith("Untitled", StringComparison.OrdinalIgnoreCase))
            return Pack.UniqueCommandName(title);

        var path = _vm.LoadedModelPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(stem)) return Pack.UniqueCommandName(stem);
        }
        return "";
    }

    // ── Add ──────────────────────────────────────────────────────────

    private async void OnAddToEmotePack(object sender, RoutedEventArgs e)
    {
        if (_addToPackBusy) return;

        if (Pack.IsFull)
        {
            AppDialog.Warn(
                $"The pack already holds {EmotePackSession.MaxEntries} emotes — export it or remove a few first.",
                "Add to pack", Window.GetWindow(this));
            return;
        }

        if (!_webViewReady || !_vm.HasRig)
        {
            _vm.StatusText = "Nothing to add — load a rigged model and pose it first.";
            AppDialog.Warn(_vm.StatusText, "Add to pack", Window.GetWindow(this));
            return;
        }

        var dlg = new AddToPackDialog(Pack, SuggestPackEmoteName(), "")
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() != true) return;

        _addToPackBusy = true;
        var prevStatus = _vm.StatusText;
        _vm.StatusText = $"Baking “{dlg.EmoteName}” into the pack…";
        try
        {
            // The clip name is what gets stamped inside the .ycd, so it has to
            // be free of collisions before the bake, not after.
            var clipName = Pack.UniqueClipName(dlg.EmoteName);

            var baked = await BakeCurrentEmoteAsync(clipName);
            if (baked is not { } b)
            {
                // The builders set StatusText with the actual reason (no
                // GTA-mapped bones, viewer not ready, …). Surface it — "Add to
                // Pack" has no other visible effect, so a status line alone
                // would read as a silent no-op.
                var why = string.IsNullOrWhiteSpace(_vm.StatusText) || _vm.StatusText == prevStatus
                    ? "Couldn't bake this emote."
                    : _vm.StatusText;
                AppDialog.Warn(why, "Add to pack", Window.GetWindow(this));
                return;
            }

            // Read the export mode ONE statement after the bake — and downgrade
            // it when root motion was asked for but no mover came out. Flag
            // 786433 over a clip with nothing to extract puts the ped on
            // kinematic physics with no movement to drive it, which reads
            // in-game as floating or sinking.
            var movement = _vm.EffectiveExportMovement;
            bool downgraded = false;
            if (movement == EmoteMovement.RootMotion && !b.HadRootMotion)
            {
                movement = EmoteMovement.InPlace;
                downgraded = true;
            }

            DpemotesPackBuilder.PropInfo? prop = null;
            bool propFailed = false;
            if (_vm.HasProp && !string.IsNullOrWhiteSpace(_vm.PropModelName))
            {
                prop = await GetPropInfoForExportAsync();
                propFailed = prop is null;
            }

            var entry = Pack.Add(new EmotePackEntry
            {
                ClipName = clipName,
                CommandName = Pack.UniqueCommandName(dlg.EmoteName),
                Label = dlg.EmoteLabel,
                YcdBytes = b.Bytes,
                Movement = movement,
                IsAnimated = b.IsAnimated,
                KeyframeCount = b.KeyframeCount,
                DurationSeconds = b.IsAnimated ? _vm.TimelineDuration : 0,
                MappedBones = _vm.LastExportMapped,
                SourceModel = _vm.LoadedModelPath ?? "",
                RootMotionDowngraded = downgraded,
                Prop = prop,
            });

            if (entry is null)
            {
                AppDialog.Warn(
                    $"The pack is full ({EmotePackSession.MaxEntries} emotes).",
                    "Add to pack", Window.GetWindow(this));
                return;
            }

            // Reveal the queue so the add is visibly acknowledged. The live
            // emote is left exactly as it was — you keep working on it.
            _vm.SidebarTab = PoseToEmoteViewModel.EmoteSidebarTab.Pack;
            _vm.StatusText =
                $"Added “{entry.CommandName}” to the pack — {Pack.StatusSummary}.";
            AppendDebug("info", "export", $"emote queued for pack: {entry.ClipName}",
                $"animated={b.IsAnimated} keyframes={b.KeyframeCount} bytes={b.Bytes.Length}");

            if (propFailed)
            {
                AppDialog.Warn(
                    $"Added “{entry.CommandName}”, but the prop's position couldn't be read — "
                    + "this emote will play without its prop.",
                    "Add to pack", Window.GetWindow(this));
            }
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Add to pack failed: " + ex.Message;
            FosLogger.Warn("export", "add to emote pack failed", ex);
            AppendDebug("err", "error", "add to emote pack failed", ex.Message);
            AppDialog.Warn(_vm.StatusText, "Add to pack", Window.GetWindow(this));
        }
        finally
        {
            _addToPackBusy = false;
        }
    }

    // ── Row actions ──────────────────────────────────────────────────

    private static EmotePackEntry? EntryFrom(object sender) =>
        (sender as FrameworkElement)?.Tag as EmotePackEntry;

    private void OnPackEntryRemove(object sender, RoutedEventArgs e)
    {
        if (EntryFrom(sender) is not { } entry) return;
        Pack.Remove(entry);
        _vm.StatusText = Pack.HasEntries
            ? $"Removed “{entry.CommandName}” — {Pack.StatusSummary}."
            : "Emote pack is empty.";
    }

    private void OnPackEntryMoveUp(object sender, RoutedEventArgs e)
    {
        if (EntryFrom(sender) is { } entry) Pack.Move(entry, -1);
    }

    private void OnPackEntryMoveDown(object sender, RoutedEventArgs e)
    {
        if (EntryFrom(sender) is { } entry) Pack.Move(entry, +1);
    }

    /// <summary>Commit an inline command rename. Safe at any time — the
    /// command name is only ever a Lua string; the clip name baked into the
    /// .ycd is a separate, immutable thing. A rejected name snaps back rather
    /// than leaving the box showing something that won't be exported.</summary>
    private void OnPackEntryNameCommitted(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not EmotePackEntry entry) return;
        var wanted = tb.Text?.Trim() ?? "";
        if (!string.Equals(wanted, entry.CommandName, StringComparison.Ordinal))
        {
            if (!Pack.SetCommandName(entry, wanted) && !string.IsNullOrWhiteSpace(wanted))
            {
                var clean = EmotePackSession.Sanitize(wanted);
                if (!string.Equals(clean, entry.CommandName, StringComparison.OrdinalIgnoreCase))
                    _vm.StatusText = string.IsNullOrEmpty(clean)
                        ? "A command name needs at least one letter or number."
                        : $"“{clean}” is already used in this pack.";
            }
        }
        tb.Text = entry.CommandName;   // always show what will actually export
    }

    private void OnPackEntryNameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            // Moving focus fires LostFocus, which commits.
            Keyboard.ClearFocus();
            tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (tb.Tag is EmotePackEntry entry) tb.Text = entry.CommandName;
            Keyboard.ClearFocus();
        }
    }

    // ── Export ───────────────────────────────────────────────────────

    /// <summary>Roll the queue up into one standalone FiveM resource. The user
    /// picks the PARENT folder and we create &lt;parent&gt;\&lt;pack&gt; inside it, so the
    /// folder name, the pack name and the `ensure` line always agree.</summary>
    private async void OnExportEmotePack(object sender, RoutedEventArgs e)
    {
        var included = Pack.Entries.Where(x => x.IsIncluded).ToList();
        if (included.Count == 0)
        {
            _vm.StatusText = "Nothing to export — every emote in the pack is switched off.";
            AppDialog.Warn(_vm.StatusText, "Export pack", Window.GetWindow(this));
            return;
        }

        var packName = EmotePackSession.Sanitize(Pack.PackName);
        if (string.IsNullOrEmpty(packName))
        {
            _vm.StatusText = "The pack name needs at least one letter or number.";
            AppDialog.Warn(_vm.StatusText, "Export pack", Window.GetWindow(this));
            return;
        }

        var parent = await StaFileDialogs.OpenFolderAsync(Window.GetWindow(this), dlg =>
        {
            dlg.Title = $"Where should the “{packName}” resource folder go?";
        });
        if (parent is null) return;

        var emotes = included.Select(x => new EmotePackBuilder.Emote(
            ClipName: x.ClipName,
            CommandName: x.CommandName,
            Label: x.Label,
            YcdBytes: x.YcdBytes,
            Movement: x.Movement,
            // Loop everything, matching the single-emote exports and the
            // preview: a dance that played once and froze on its last frame
            // read as broken.
            IsLooping: true,
            Prop: x.Prop)).ToList();

        var owner = Window.GetWindow(this);
        _vm.StatusText = $"Writing emote pack “{packName}”…";
        try
        {
            var result = await Task.Run(() => EmotePackBuilder.BuildFolder(
                parent, packName, emotes,
                confirmOverwriteForeign: path => AppDialog.Show(
                    $"“{Path.GetFileName(path)}” already exists and wasn't created by FiveOS.\n\n"
                    + "Write the pack into it? fxmanifest.lua, client.lua, README.txt and any "
                    + "stream\\*.ycd files there will be replaced; everything else is left alone.",
                    "Folder already exists",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, owner) == MessageBoxResult.Yes));

            Pack.MarkExported();
            var msg = $"Wrote “{result.PackName}” — {result.EmoteCount} emote"
                    + (result.EmoteCount == 1 ? "" : "s")
                    + $", {EmotePackSession.FormatBytes(result.TotalBytes)}.\n\n"
                    + $"{result.FolderPath}\n\n"
                    + $"Drop the folder into your server's resources/, add `ensure {result.PackName}` "
                    + $"to server.cfg, then /{result.PackName} in game lists every emote.";
            _vm.StatusText = $"Wrote emote pack “{result.PackName}” ({result.EmoteCount} emotes) to {result.FolderPath}";
            AppendDebug("info", "export", $"emote pack written: {result.FolderPath}",
                $"emotes={result.EmoteCount} bytes={result.TotalBytes}");
            AppDialog.Show(msg, "Emote pack", MessageBoxButton.OK, MessageBoxImage.Information, owner);
        }
        catch (OperationCanceledException)
        {
            _vm.StatusText = "Emote pack export cancelled.";
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Emote pack export failed: " + ex.Message;
            FosLogger.Warn("export", "emote pack write failed", ex);
            AppendDebug("err", "error", "emote pack write failed", ex.Message);
            AppDialog.Warn(_vm.StatusText, "Emote pack", owner);
        }
    }

    private void OnClearEmotePack(object sender, RoutedEventArgs e)
    {
        if (!Pack.HasEntries) return;
        var r = AppDialog.Show(
            $"Drop all {Pack.Count} emotes from the pack?\n\n"
            + "The baked animations are discarded — you'd have to pose and add them again.",
            "Clear pack", MessageBoxButton.YesNo, MessageBoxImage.Question, Window.GetWindow(this));
        if (r != MessageBoxResult.Yes) return;
        Pack.Clear();
        _vm.StatusText = "Emote pack cleared.";
    }

    // Menu / keyboard entry points, mirroring the RunExport* block.
    public void RunAddToEmotePack() => OnAddToEmotePack(this, new RoutedEventArgs());
    public void RunExportEmotePack() => OnExportEmotePack(this, new RoutedEventArgs());
}
