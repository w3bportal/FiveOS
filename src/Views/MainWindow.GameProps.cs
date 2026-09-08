// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FiveOS.Services;

namespace FiveOS.Views;

/// <summary>
/// Props → SOURCE → <b>Game</b> tab: search the GTA V install for a
/// standalone <c>.ydr</c> and import it like a dropped file.
///
/// The heavy lifting is <see cref="GamePropPreview"/> (RPF index, search,
/// extract-with-textures); this partial only drives the sidebar and hands
/// the extracted <c>.ydr</c> to <c>TryLoad</c>, so the textured preview,
/// layers, gizmo and both game targets behave exactly as they do for a
/// <c>.ydr</c> the user dragged out of OpenIV.
/// </summary>
public partial class MainWindow
{
    private CancellationTokenSource? _gamePropSearchCts;
    private bool _gamePropIndexKicked;

    /// <summary>First time the tab is shown: start the (one-off, background)
    /// RPF index so results are instant by the time the user has typed.</summary>
    private void OnGamePropTabLoaded(object sender, RoutedEventArgs e)
        => _ = EnsureGamePropIndexAsync();

    private async Task<bool> EnsureGamePropIndexAsync()
    {
        if (GamePropPreview.Status == GamePropPreview.State.Ready)
        {
            _vm.GamePropNeedsFolder = false;
            return true;
        }

        if (!GamePropPreview.IsAvailable)
        {
            _vm.GamePropNeedsFolder = true;
            _vm.GamePropStatus = "No GTA V install found. Point FiveOS at the folder that contains GTA5.exe to search its models.";
            return false;
        }

        _vm.GamePropNeedsFolder = false;
        bool first = !_gamePropIndexKicked;
        _gamePropIndexKicked = true;
        if (first) _vm.GamePropStatus = "Indexing game models (first time only)…";

        bool ok;
        try { ok = await GamePropPreview.EnsureIndexedAsync(); }
        catch (Exception ex)
        {
            FosLogger.Warn("game-props", "index failed", ex);
            ok = false;
        }

        if (!ok)
        {
            _vm.GamePropStatus = GamePropPreview.Error ?? "Couldn't read the game files.";
            _vm.GamePropNeedsFolder = GamePropPreview.Status == GamePropPreview.State.NotConfigured;
            return false;
        }

        if (string.IsNullOrWhiteSpace(_vm.GamePropSearch))
            _vm.GamePropStatus = $"{GamePropPreview.ModelCount:N0} models indexed (.ydr, .yft, .ydd). Type to search — e.g. prop_bench, fridge, chair.";
        else
            await RunGamePropSearchAsync();

        // Warm the archetype/gtxd tables now (background, one-off) so an
        // import whose textures need them doesn't stall for the .ytyp scan.
        _ = GamePropPreview.EnsureArchetypesAsync();
        return true;
    }

    private void OnGamePropSearchChanged(object sender, TextChangedEventArgs e)
        => _ = RunGamePropSearchAsync(debounceMs: 120);

    /// <summary>Enter imports the top match for what is typed RIGHT NOW — the
    /// fast path for someone who already knows the model name. The list may
    /// still be showing the previous keystroke's results (120 ms debounce), so
    /// the search is re-run synchronously first and the selection is taken
    /// from that, not from whatever was highlighted before.</summary>
    private async void OnGamePropSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var typed = _vm.GamePropSearch?.Trim() ?? "";
        await RunGamePropSearchAsync();
        if (!string.Equals(_vm.GamePropSearch?.Trim() ?? "", typed, StringComparison.Ordinal)) return;
        var first = _vm.GamePropResults.FirstOrDefault();
        if (first is null) return;
        // An explicit exact match (name or stem) wins; otherwise the top hit.
        var exact = _vm.GamePropResults.FirstOrDefault(r =>
            string.Equals(r, typed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension(r), typed, StringComparison.OrdinalIgnoreCase));
        _vm.GamePropSelected = exact ?? first;
        await ImportSelectedGamePropAsync();
    }

    private async void OnGamePropListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.GamePropSelected)) return;
        await ImportSelectedGamePropAsync();
    }

    /// <summary>Re-run the search for the current text. Debounced so a fast
    /// typist doesn't queue a scan per keystroke; a stale run's results are
    /// dropped rather than flashing past.</summary>
    private async Task RunGamePropSearchAsync(int debounceMs = 0)
    {
        _gamePropSearchCts?.Cancel();
        var cts = _gamePropSearchCts = new CancellationTokenSource();
        var token = cts.Token;

        try
        {
            if (debounceMs > 0) await Task.Delay(debounceMs, token);
        }
        catch (TaskCanceledException) { return; }

        var query = _vm.GamePropSearch?.Trim() ?? "";
        if (query.Length == 0)
        {
            _vm.GamePropResults.Clear();
            _vm.GamePropHasResults = false;
            _vm.GamePropSelected = null;
            if (GamePropPreview.Status == GamePropPreview.State.Ready)
                _vm.GamePropStatus = $"{GamePropPreview.ModelCount:N0} models indexed (.ydr, .yft, .ydd). Type to search — e.g. prop_bench, fridge, chair.";
            return;
        }

        if (GamePropPreview.Status != GamePropPreview.State.Ready)
        {
            // Kick the index if nothing has tried yet; it re-runs the search
            // itself once it lands. A failed mount or a missing folder is
            // terminal until the user picks a folder — retrying per keystroke
            // would re-mount every RPF on each key.
            if (!_vm.GamePropNeedsFolder && GamePropPreview.Status != GamePropPreview.State.Failed)
                _ = EnsureGamePropIndexAsync();
            return;
        }

        const int Limit = 200;
        IReadOnlyList<string> hits;
        try { hits = await Task.Run(() => GamePropPreview.Search(query, Limit), token); }
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested) return;

        var keep = _vm.GamePropSelected;
        _vm.GamePropResults.Clear();
        foreach (var h in hits) _vm.GamePropResults.Add(h);
        _vm.GamePropHasResults = hits.Count > 0;
        _vm.GamePropSelected = keep != null && hits.Contains(keep) ? keep : hits.FirstOrDefault();

        _vm.GamePropStatus = hits.Count == 0
            ? $"No model matches \"{query}\" — try part of the name (models are .ydr, .yft or .ydd)."
            : hits.Count >= Limit
                ? $"Showing the first {Limit} matches — add another word to narrow it down."
                : $"{hits.Count} match{(hits.Count == 1 ? "" : "es")}.";
    }

    private async void OnGamePropImport(object sender, RoutedEventArgs e)
        => await ImportSelectedGamePropAsync();

    /// <summary>Extract the selected model (+ its .ytd textures) to a temp
    /// folder and load it through the ordinary game-file path. Busy until the
    /// viewer has the model, so a double-click can't start a second import
    /// while the first is still decoding textures.</summary>
    private async Task ImportSelectedGamePropAsync()
    {
        var name = _vm.GamePropSelected?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            _vm.GamePropStatus = "Search for a model and pick one from the list first.";
            return;
        }
        if (_vm.GamePropBusy) return;

        _vm.GamePropBusy = true;
        _vm.GamePropStatus = $"Reading {name} from the game files…";
        _vm.StatusText = $"Reading {name} from the game files…";
        try
        {
            var res = await GamePropPreview.ExtractToFolderAsync(name);
            if (!res.Success || res.ModelPath is null)
            {
                _vm.GamePropStatus = res.Error ?? "Couldn't read that model.";
                _vm.StatusText = _vm.GamePropStatus;
                FosLogger.Warn("game-props", $"import {name} failed: {res.Error}");
                return;
            }

            FosLogger.Info("game-props",
                $"import {name}: {res.ModelPath} (+{res.TextureDictionaries} .ytd, {res.ResolvedTextures}/{res.ExternalTextures} external textures)");
            int missing = res.ExternalTextures - res.ResolvedTextures;
            var kindNote = res.Kind switch
            {
                GamePropPreview.ModelKind.Fragment => " Fragment: the main model is imported; its breakable pieces aren't.",
                GamePropPreview.ModelKind.Dictionary => " Dictionary: every model in it is loaded as its own mesh group — use the layers panel to separate them.",
                _ => "",
            };
            var rename = " Rename it below before converting unless you mean to replace the game's own prop.";
            var summary = missing > 0
                ? $"Imported {name}, but {missing} of its {res.ExternalTextures} textures couldn't be found in any dictionary the game links to it — the preview may be partly untextured. Use Add textures… if you need them in the export.{kindNote}{rename}"
                : res.TextureDictionaries > 0
                    ? $"Imported {name} with {res.TextureDictionaries} texture dictionar{(res.TextureDictionaries == 1 ? "y" : "ies")}.{kindNote}{rename}"
                    : $"Imported {name} (textures embedded).{kindNote}{rename}";

            // Same path as a dragged file: textured .glb preview, recent-files
            // skipped (temp path).
            _vm.GamePropStatus = $"Building the preview for {name}…";
            bool loaded = await TryLoadAsync(res.ModelPath, Path.GetFileNameWithoutExtension(res.ModelPath));
            _vm.GamePropStatus = loaded
                ? summary
                : $"Couldn't load {name}: {_vm.StatusText}";
        }
        catch (Exception ex)
        {
            FosLogger.Warn("game-props", $"import {name} threw", ex);
            _vm.GamePropStatus = "Game prop import failed: " + ex.Message;
        }
        finally
        {
            _vm.GamePropBusy = false;
        }
    }

    /// <summary>Same folder setting the Vehicles and Emotes tabs use — so
    /// every game-file mount is re-pointed, not just this tab's.</summary>
    private async void OnGamePropSetFolder(object sender, RoutedEventArgs e)
    {
        // Shell dialogs on this window's UI thread stall for seconds (WebView2
        // contends the pump) — StaFileDialogs runs them on their own thread.
        var initial = GtaInstall.Resolve()
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var folder = await StaFileDialogs.OpenFolderAsync(this, dlg =>
        {
            dlg.Title = "Pick your GTA V folder (the one with GTA5.exe)";
            dlg.InitialDirectory = initial;
        });
        if (folder is null) return;
        if (!GtaInstall.IsValidFolder(folder))
        {
            _vm.GamePropStatus = "That folder has no GTA5.exe — pick the game's install folder.";
            return;
        }

        UserSettings.SaveGtaFolder(folder);
        GamePropPreview.Reset();
        AnimRpfIndex.Reset(folder);
        GameTextureCache.Reset(folder);
        _gamePropIndexKicked = false;
        _vm.GamePropNeedsFolder = false;
        await EnsureGamePropIndexAsync();
    }
}
