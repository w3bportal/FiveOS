// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Linq;
using CodeWalker.GameFiles;

namespace FiveOS.Services;

/// <summary>
/// Locates the external <c>.ytd</c> that holds a drawable's textures so the
/// workbench can paint clothing/props whose diffuse isn't embedded. Clothing
/// (.ydd) almost always references a separate texture dictionary by name; this
/// finds the sibling .ytd next to the model and resolves the textures the
/// drawable's shaders actually ask for.
/// </summary>
public static class ClothingTextureResolver
{
    /// <summary>Cheap, filesystem-only guess at the texture dictionary for a
    /// model — the file name to surface in the UI before the user opts in.
    /// Prefers a same-base-name .ytd, then the only .ytd, then the closest
    /// name. Null when the folder has none.</summary>
    public static string? FindCandidateName(string sourcePath)
    {
        var dir = Path.GetDirectoryName(sourcePath);
        if (dir == null) return null;
        string[] ytds;
        try { ytds = Directory.GetFiles(dir, "*.ytd"); }
        catch { return null; }
        if (ytds.Length == 0) return null;

        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var best = ytds[0];
        int bestScore = NameScore(best, baseName);
        for (int i = 1; i < ytds.Length; i++)
        {
            int s = NameScore(ytds[i], baseName);
            if (s > bestScore) { bestScore = s; best = ytds[i]; }
        }
        return Path.GetFileName(best);
    }

    /// <summary>Load the textures a model needs from the .ytd(s) next to it.
    /// Returns the primary .ytd's file name (for display) and a name-hash →
    /// Texture map to hand to <see cref="DrawableMeshExtractor.SetExternalTextures"/>.
    /// Scans same-base-name first and stops once every referenced texture is
    /// covered (capped, so a giant pack folder can't stall). Heavy — call off
    /// the UI thread.</summary>
    public static (string? Name, Dictionary<uint, Texture> Map) Load(string sourcePath, IEnumerable<DrawableBase> drawables)
    {
        var r = LoadDetailed(sourcePath, drawables);
        return (r.Name, r.Map);
    }

    /// <summary>What <see cref="Load"/> found, plus the name hashes that came
    /// out of the model's own <c>+hi</c>/<c>+hifr</c> high-detail set — the
    /// only external textures that should beat an embedded copy.</summary>
    public sealed record Resolved(string? Name, Dictionary<uint, Texture> Map, HashSet<uint> HiHashes);

    public static Resolved LoadDetailed(string sourcePath, IEnumerable<DrawableBase> drawables)
    {
        var map = new Dictionary<uint, Texture>();
        var hi = new HashSet<uint>();
        var dir = Path.GetDirectoryName(sourcePath);
        if (dir == null) return new Resolved(null, map, hi);
        string[] ytds;
        try { ytds = Directory.GetFiles(dir, "*.ytd"); }
        catch { return new Resolved(null, map, hi); }
        if (ytds.Length == 0) return new Resolved(null, map, hi);

        var needed = CollectExternalNames(drawables);
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        // Best-named candidates first: the model's own high-detail set, then
        // its same-name dictionary, so the hi copy is the one that lands in the
        // map (first dictionary per hash wins) and the primary .ytd is usually
        // the only load.
        Array.Sort(ytds, (a, b) => NameScore(b, baseName).CompareTo(NameScore(a, baseName)));

        string? primary = null;
        int scanned = 0;
        int hiPending = ytds.Count(y => IsHiVariant(y, baseName));
        foreach (var ytd in ytds)
        {
            if (scanned++ >= 64) break;   // pathological-folder guard
            bool isHi = IsHiVariant(ytd, baseName);
            if (isHi) hiPending--;
            YtdFile yf;
            try { yf = DrawableOptimizer.LoadResource<YtdFile>(ytd); }
            catch { continue; }
            var items = yf.TextureDict?.Textures?.data_items;
            if (items == null) continue;

            bool contributed = false;
            foreach (var tex in items)
            {
                if (tex == null) continue;
                uint h = (uint)tex.NameHash;
                if (map.TryAdd(h, tex))
                {
                    if (isHi) hi.Add(h);
                    if (needed.Contains(h)) contributed = true;
                }
            }
            if (contributed && primary == null) primary = Path.GetFileName(ytd);

            // Stop early once every referenced texture is in hand — but never
            // before the model's high-detail set has been read.
            if (needed.Count > 0 && hiPending <= 0)
            {
                bool covered = true;
                foreach (var h in needed) if (!map.ContainsKey(h)) { covered = false; break; }
                if (covered) break;
            }
        }

        primary ??= Path.GetFileName(ytds[0]);
        return new Resolved(primary, map, hi);
    }

    private static bool IsHiVariant(string ytdPath, string baseName)
    {
        var n = Path.GetFileNameWithoutExtension(ytdPath);
        return n.Equals(baseName + "+hi", StringComparison.OrdinalIgnoreCase)
            || n.Equals(baseName + "+hifr", StringComparison.OrdinalIgnoreCase)
            || n.Equals(baseName + "+hidr", StringComparison.OrdinalIgnoreCase)
            || n.Equals(baseName + "+hidd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Name hashes of the diffuse/sampler textures the drawables
    /// reference externally (no embedded pixel data). Public so the game-file
    /// importer can decide which RPF dictionaries are worth extracting.</summary>
    public static HashSet<uint> ExternalTextureHashes(IEnumerable<DrawableBase> drawables)
        => CollectExternalNames(drawables);

    private static HashSet<uint> CollectExternalNames(IEnumerable<DrawableBase> drawables)
    {
        var set = new HashSet<uint>();
        foreach (var d in drawables)
        {
            var shaders = d?.ShaderGroup?.Shaders?.data_items;
            if (shaders == null) continue;
            foreach (var sh in shaders)
            {
                var prms = sh?.ParametersList?.Parameters;
                if (prms == null) continue;
                foreach (var p in prms)
                {
                    if (p == null || p.DataType != 0) continue;   // 0 = texture sampler
                    if (p.Data is TextureBase tb && (tb is not Texture t || t.Data == null))
                        set.Add((uint)tb.NameHash);
                }
            }
        }
        return set;
    }

    // Higher = closer match to the model's base name. Exact wins, then a
    // substring either way, then anything.
    private static int NameScore(string ytdPath, string baseName)
    {
        var n = Path.GetFileNameWithoutExtension(ytdPath);
        if (IsHiVariant(ytdPath, baseName)) return 4;
        if (string.Equals(n, baseName, StringComparison.OrdinalIgnoreCase)) return 3;
        if (n.Contains(baseName, StringComparison.OrdinalIgnoreCase) ||
            baseName.Contains(n, StringComparison.OrdinalIgnoreCase)) return 2;
        return 1;
    }
}
