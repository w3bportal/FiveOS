// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodeWalker.GameFiles;

namespace FiveOS.Services;

/// <summary>
/// Loads GTA V's SHARED vehicle textures (vehshare.ytd &amp; friends) so add-on
/// car bodies render with their real game textures in the Vehicles preview.
///
/// Add-on vehicle shaders point their diffuse at generic textures — e.g.
/// <c>vehicle_generic_smallspecmap</c>, <c>vehicle_generic_detail2</c> — that
/// the game keeps in <c>vehshare.ytd</c> and mods never ship. CodeWalker
/// renders them by loading those shared dictionaries from the game files;
/// this does the same, using CodeWalker.Core's key derivation + RPF manager
/// on the user's install.
///
/// The full RPF scan (~5-10 s, ~400k entries) runs ONCE per session on a
/// background thread; the resolved name-hash → Texture map is then cached and
/// handed to <see cref="DrawableMeshExtractor"/> as an external fallback.
/// Everything degrades gracefully: no game folder ⇒ Status.NotConfigured and
/// the preview falls back to painted-metal bodywork.
/// </summary>
public static class GameTextureCache
{
    public enum State { NotConfigured, Loading, Ready, Failed }

    public static State Status { get; private set; } = State.NotConfigured;
    public static string? Error { get; private set; }
    public static string? GtaFolder { get; private set; }

    /// <summary>name-hash → shared Texture (empty until Ready).</summary>
    public static IReadOnlyDictionary<uint, Texture> SharedTextures => _shared;

    private static Dictionary<uint, Texture> _shared = new();
    private static readonly object Lock = new();
    private static Task<bool>? _loadTask;

    // The shared dictionaries every add-on car may reference. Base-game
    // vehshare covers the vast majority; the variants fill trucks/army/worn.
    private static readonly string[] VehshareNames =
    {
        "vehshare.ytd", "vehshare_truck.ytd", "vehshare_army.ytd", "vehshare_worn.ytd",
    };

    /// <summary>Kick off (or reuse) the background load. Returns true when the
    /// shared map is populated. Safe to call repeatedly / from the UI thread —
    /// the heavy work happens once on a worker thread.</summary>
    public static Task<bool> EnsureLoadedAsync(string? gtaFolderOverride = null)
    {
        lock (Lock)
        {
            if (Status == State.Ready) return Task.FromResult(true);
            if (_loadTask is { IsCompleted: false }) return _loadTask;

            var folder = gtaFolderOverride is { Length: > 0 } && GtaInstall.IsValidFolder(gtaFolderOverride)
                ? gtaFolderOverride
                : GtaInstall.Resolve();

            if (!GtaInstall.IsValidFolder(folder))
            {
                Status = State.NotConfigured;
                Error = "No GTA V folder found — set it to load shared car textures.";
                return Task.FromResult(false);
            }

            GtaFolder = folder;
            Status = State.Loading;
            Error = null;
            _loadTask = Task.Run(() => Load(folder!));
            return _loadTask;
        }
    }

    private static bool Load(string folder)
    {
        try
        {
            bool gen9 = GtaInstall.IsEnhanced(folder);
            // Derive the game's RPF/resource keys from the install.
            GTA5Keys.LoadFromPath(folder, gen9, null);

            var rpf = new RpfManager();
            // buildIndex:false is REQUIRED under single-file publish — the
            // index build reads a "strings.txt" via Path.GetDirectoryName(
            // Assembly.Location), and Location is "" in a single-file exe so
            // GetDirectoryName returns null → Path.Combine(null) throws. We
            // match vehshare by the entry's own name (from each RPF's TOC,
            // populated regardless), so the Jenkins string index isn't needed.
            rpf.Init(folder, gen9, _ => { }, _ => { }, rootOnly: false, buildIndex: false);

            var map = new Dictionary<uint, Texture>();
            // Prefer base-game vehshare entries; only one path per name is
            // needed, so take the first match (shortest path = base x64e).
            foreach (var name in VehshareNames)
            {
                RpfFileEntry? best = null;
                foreach (var kv in rpf.EntryDict)
                {
                    if (kv.Value is not RpfFileEntry fe) continue;
                    if (!string.Equals(fe.NameLower, name, StringComparison.Ordinal)) continue;
                    if (best == null || (fe.Path?.Length ?? int.MaxValue) < (best.Path?.Length ?? int.MaxValue))
                        best = fe;
                }
                if (best == null) continue;

                YtdFile? ytd;
                try { ytd = rpf.GetFile<YtdFile>(best); } catch { continue; }
                var items = ytd?.TextureDict?.Textures?.data_items;
                if (items == null) continue;
                foreach (var t in items)
                    if (t != null) map.TryAdd((uint)t.NameHash, t);   // base vehshare wins
            }

            if (map.Count == 0)
            {
                Status = State.Failed;
                Error = "GTA folder found but vehshare.ytd yielded no textures.";
                return false;
            }

            lock (Lock)
            {
                _shared = map;
                Status = State.Ready;
                GtaFolder = folder;
            }
            FosLogger.Info("vehicles", $"shared vehicle textures loaded: {map.Count} from {folder}");
            return true;
        }
        catch (Exception ex)
        {
            Status = State.Failed;
            Error = "Couldn't read GTA files: " + ex.Message;
            FosLogger.Warn("vehicles", "shared texture load failed", ex);
            return false;
        }
    }

    /// <summary>Re-point at a new folder (user picked one). Clears the cache so
    /// the next EnsureLoadedAsync reloads from there.</summary>
    public static void Reset(string? newFolder)
    {
        lock (Lock)
        {
            _shared = new();
            _loadTask = null;
            Status = State.NotConfigured;
            Error = null;
            GtaFolder = newFolder;
        }
    }
}
