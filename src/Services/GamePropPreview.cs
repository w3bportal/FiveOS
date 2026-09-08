// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodeWalker.GameFiles;

namespace FiveOS.Services;

/// <summary>
/// Pulls a vanilla model out of the GTA V install. Two customers:
/// <list type="bullet">
/// <item>the Emotes viewport, which shows the REAL <c>prop_*</c> mesh in the
/// ped's hand while an emote is authored (<see cref="ExportGlbAsync"/> —
/// geometry only, raw RAGE space);</item>
/// <item>the Props workspace's <b>Game</b> source tab, which searches every
/// model in the install — standalone drawables (<c>.ydr</c>), fragments
/// (<c>.yft</c>: vending machines, fridges, anything breakable) and drawable
/// dictionaries (<c>.ydd</c>) — and extracts one, with the <c>.ytd</c>(s)
/// that carry its textures, to a folder the normal import path then loads
/// like a dropped file (<see cref="Search"/> + <see cref="ExtractToFolderAsync"/>).</item>
/// </list>
/// Model names always carry their extension (<c>prop_bench_01b.ydr</c>,
/// <c>prop_vend_fridge01.yft</c>) so a drawable and a fragment sharing a stem
/// can't collide and the kind is visible in the list.
///
/// Textures are hunted in the same order the game resolves them: the model's
/// own dictionary, its <c>+hi</c>/<c>+hifr</c> high-detail set, the archetype's
/// <c>textureDictionary</c> (from the <c>.ytyp</c>s, scanned lazily), that
/// dictionary's <c>gtxd</c> parents, and finally every dictionary in the
/// same RPF folder.
///
/// Indexes via its own CodeWalker <see cref="RpfManager"/> over the same
/// folder <see cref="AnimRpfIndex"/> and <see cref="GameTextureCache"/> mount
/// (three cold mounts today — sharing one is a future cleanup). The game
/// files are only ever read.
/// </summary>
public static class GamePropPreview
{
    public enum State { NotConfigured, Loading, Ready, Failed }

    /// <summary>What a model name refers to. Fragments and dictionaries load
    /// through the same import path as drawables; the kind only changes what
    /// the preview importer produces (see <c>MainWindow.TryLoad</c>).</summary>
    public enum ModelKind { Drawable, Fragment, Dictionary }

    public sealed record Result(bool Success, string? GlbPath, int Triangles, string? Error);

    /// <summary>Outcome of <see cref="ExtractToFolderAsync"/>: the on-disk
    /// model (RSC7, exactly as OpenIV would export it), how many <c>.ytd</c>
    /// dictionaries were written beside it, and how many of the textures the
    /// drawable references externally those dictionaries actually cover.</summary>
    public sealed record ExtractResult(
        bool Success, string? ModelPath, int TextureDictionaries, string? Error,
        int ExternalTextures = 0, int ResolvedTextures = 0, ModelKind Kind = ModelKind.Drawable);

    public static State Status { get; private set; } = State.NotConfigured;
    public static string? Error { get; private set; }

    /// <summary>True when a GTA V folder is known — the only precondition for
    /// trying. The index itself builds lazily on the first lookup.</summary>
    public static bool IsAvailable => GtaInstall.IsValidFolder(GtaInstall.Resolve());

    /// <summary>How many models (all kinds) the index holds (0 until Ready).</summary>
    public static int ModelCount => _names.Length;

    private const string ExtYdr = ".ydr", ExtYft = ".yft", ExtYdd = ".ydd", ExtYtd = ".ytd", ExtYtyp = ".ytyp";
    private static readonly string[] ModelExtensions = { ExtYdr, ExtYft, ExtYdd };
    private static readonly string[] GtxdNames = { "gtxd.ymt", "gtxd.meta", "mph4_gtxd.ymt" };
    // High-detail dictionary suffixes: generic +hi, and per model kind
    // +hifr (fragment) / +hidr (drawable) / +hidd (dictionary).
    private static readonly string[] HiSuffixes = { "+hi", "+hifr", "+hidr", "+hidd" };
    private static readonly string[] VehshareNames = { "vehshare", "vehshare_truck", "vehshare_army", "vehshare_worn" };

    private static readonly object Lock = new();
    private static Task<bool>? _loadTask;
    // Bumped by Reset(): a Load() started for an older folder must not publish
    // its (stale) tables over the new one when it finally finishes.
    private static int _generation;
    private static RpfManager? _rpf;
    // Keyed by full lower-case file name ("prop_x.ydr"); shortest RPF path wins.
    private static Dictionary<string, RpfFileEntry> _models = new(StringComparer.OrdinalIgnoreCase);
    // Keyed by stem ("prop_x") and by joaat(stem) — archetype/gtxd lookups only know hashes.
    private static Dictionary<string, RpfFileEntry> _ytds = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<uint, RpfFileEntry> _ytdsByHash = new();
    // Every .ytd grouped by the RPF directory it lives in — a prop's shared
    // dictionary (prop_medusas.ytd for prop_medusa_red/blue/white.ydr) sits
    // in the same folder as the models that use it.
    private static Dictionary<RpfDirectoryEntry, List<RpfFileEntry>> _ytdsByDir = new();
    private static List<RpfFileEntry> _ytypEntries = new();
    private static List<RpfFileEntry> _gtxdEntries = new();
    private static string[] _names = Array.Empty<string>();

    // Lazily built: archetype hash → texture dictionary hash (from every .ytyp)
    // and child txd hash → parent txd hash (from gtxd). Only needed when the
    // cheap lookups leave a texture unresolved, and the scan takes seconds.
    private static Task? _archetypeTask;
    private static Dictionary<uint, ArchInfo> _archetypes = new();
    private static Dictionary<uint, uint> _txdParents = new();
    // drawableDictionary hash → textureDictionary hashes of the archetypes in it.
    private static Dictionary<uint, List<uint>> _txdsByDrawableDict = new();

    private readonly record struct ArchInfo(uint TextureDict, uint DrawableDict, int AssetType);

    /// <summary>Kick off (or reuse) the background index. Safe from the UI thread.</summary>
    public static Task<bool> EnsureIndexedAsync(string? gtaFolderOverride = null)
    {
        lock (Lock)
        {
            if (Status == State.Ready) return Task.FromResult(true);
            if (_loadTask is { IsCompleted: false }) return _loadTask;
            // A failed mount stays failed until the user points at another
            // folder (Reset) — retrying would re-scan every RPF per call.
            if (Status == State.Failed) return Task.FromResult(false);

            var folder = gtaFolderOverride is { Length: > 0 } && GtaInstall.IsValidFolder(gtaFolderOverride)
                ? gtaFolderOverride
                : GtaInstall.Resolve();

            if (!GtaInstall.IsValidFolder(folder))
            {
                Status = State.NotConfigured;
                Error = "No GTA V folder found — set it in Settings to preview game props.";
                return Task.FromResult(false);
            }

            Status = State.Loading;
            Error = null;
            int generation = _generation;
            _loadTask = Task.Run(() => Load(folder!, generation));
            return _loadTask;
        }
    }

    private static bool Load(string folder, int generation)
    {
        try
        {
            bool gen9 = GtaInstall.IsEnhanced(folder);
            GTA5Keys.LoadFromPath(folder, gen9, null);

            var rpf = new RpfManager();
            // buildIndex:false required under single-file publish (see GameTextureCache).
            rpf.Init(folder, gen9, _ => { }, _ => { }, rootOnly: false, buildIndex: false);

            var models = new Dictionary<string, RpfFileEntry>(StringComparer.OrdinalIgnoreCase);
            var ytds = new Dictionary<string, RpfFileEntry>(StringComparer.OrdinalIgnoreCase);
            var byDir = new Dictionary<RpfDirectoryEntry, List<RpfFileEntry>>();
            var ytyps = new List<RpfFileEntry>();
            var gtxds = new List<RpfFileEntry>();

            // Which copy of a duplicated file the GAME uses: update.rpf (and the
            // patchday packs under update\) override the base x64*.rpf, DLC
            // packs come next; a shorter path breaks ties. Path length alone
            // would hand out the pre-patch original of every patched prop.
            static bool Shorter(RpfFileEntry a, RpfFileEntry b)
            {
                int pa = Precedence(a.Path), pb = Precedence(b.Path);
                if (pa != pb) return pa > pb;
                return (a.Path?.Length ?? int.MaxValue) < (b.Path?.Length ?? int.MaxValue);
            }

            foreach (var kv in rpf.EntryDict)
            {
                if (kv.Value is not RpfFileEntry fe) continue;
                var nameLower = fe.NameLower ?? "";
                if (nameLower.Length < 5) continue;

                if (nameLower.EndsWith(ExtYtd, StringComparison.Ordinal))
                {
                    var stem = nameLower[..^4];
                    // Shortest path per name: base game beats DLC duplicates.
                    if (!ytds.TryGetValue(stem, out var existing) || Shorter(fe, existing)) ytds[stem] = fe;
                    if (fe.Parent != null)
                    {
                        if (!byDir.TryGetValue(fe.Parent, out var list)) byDir[fe.Parent] = list = new();
                        list.Add(fe);
                    }
                    continue;
                }
                if (nameLower.EndsWith(ExtYtyp, StringComparison.Ordinal)) { ytyps.Add(fe); continue; }
                if (Array.IndexOf(GtxdNames, nameLower) >= 0) { gtxds.Add(fe); continue; }

                foreach (var ext in ModelExtensions)
                {
                    if (!nameLower.EndsWith(ext, StringComparison.Ordinal)) continue;
                    if (!models.TryGetValue(nameLower, out var existing) || Shorter(fe, existing)) models[nameLower] = fe;
                    break;
                }
            }

            if (models.Count == 0)
            {
                lock (Lock)
                {
                    if (generation != _generation) return false;   // superseded by Reset()
                    Status = State.Failed;
                    Error = "GTA folder found but no models were indexed.";
                }
                return false;
            }

            // Hash lookups for archetype/gtxd resolution. Also seed JenkIndex so
            // CodeWalker can print real names for anything that hashes to a file
            // we know about (dictionary drawables, texture names).
            var byHash = new Dictionary<uint, RpfFileEntry>(ytds.Count);
            foreach (var (stem, fe) in ytds)
            {
                byHash.TryAdd(JenkHash.GenHash(stem), fe);
                JenkIndex.Ensure(stem);
            }
            foreach (var name in models.Keys) JenkIndex.Ensure(Path.GetFileNameWithoutExtension(name));

            var names = models.Keys.ToArray();
            Array.Sort(names, CompareNames);

            lock (Lock)
            {
                if (generation != _generation)
                {
                    FosLogger.Info("prop-preview", $"discarding index of {folder} — folder changed while it was building");
                    return false;
                }
                _rpf = rpf;
                _models = models;
                _ytds = ytds;
                _ytdsByHash = byHash;
                _ytdsByDir = byDir;
                _ytypEntries = ytyps;
                _gtxdEntries = gtxds;
                _names = names;
                _archetypes = new();
                _txdParents = new();
                _txdsByDrawableDict = new();
                _archetypeTask = null;
                Status = State.Ready;
            }
            FosLogger.Info("prop-preview",
                $"indexed {models.Count} models ({models.Keys.Count(n => n.EndsWith(ExtYdr))} .ydr, " +
                $"{models.Keys.Count(n => n.EndsWith(ExtYft))} .yft, {models.Keys.Count(n => n.EndsWith(ExtYdd))} .ydd), " +
                $"{ytds.Count} .ytd, {ytyps.Count} .ytyp, {gtxds.Count} gtxd from {folder}");
            return true;
        }
        catch (Exception ex)
        {
            FosLogger.Warn("prop-preview", "model index failed", ex);
            lock (Lock)
            {
                if (generation != _generation) return false;   // superseded by Reset()
                Status = State.Failed;
                Error = "Couldn't read GTA files: " + ex.Message;
            }
            return false;
        }
    }

    /// <summary>3 = under update\ (update.rpf + patchday packs), 2 = other DLC
    /// packs, 1 = base game. Higher wins when the same file exists twice.</summary>
    private static int Precedence(string? rpfPath)
    {
        if (string.IsNullOrEmpty(rpfPath)) return 0;
        var p = rpfPath.Replace('/', '\\').ToLowerInvariant();
        if (p.StartsWith("update\\", StringComparison.Ordinal) || p.Contains("\\update\\", StringComparison.Ordinal)) return 3;
        if (p.Contains("dlcpacks", StringComparison.Ordinal)) return 2;
        return 1;
    }

    // Vehicles have no archetype and pull their generic textures from
    // vehshare — the Vehicles tab handles those; here they would only trigger
    // a pointless .ytyp scan and a walk over every multi-MB car dictionary.
    private static bool IsVehicleEntry(RpfFileEntry fe)
    {
        var p = (fe.Path ?? "").Replace('/', '\\').ToLowerInvariant();
        return p.Contains("\\vehicles.rpf\\", StringComparison.Ordinal)
            || p.Contains("\\vehiclemods\\", StringComparison.Ordinal)
            || p.Contains("\\vehicles\\", StringComparison.Ordinal);
    }

    // Stem first, then drawable → fragment → dictionary, so prop_x.ydr lists
    // right before prop_x.yft and a search tier keeps that order.
    private static int CompareNames(string a, string b)
    {
        int c = string.CompareOrdinal(Stem(a), Stem(b));
        return c != 0 ? c : KindOrder(a).CompareTo(KindOrder(b));
    }

    private static int KindOrder(string name)
        => name.EndsWith(ExtYdr, StringComparison.Ordinal) ? 0
         : name.EndsWith(ExtYft, StringComparison.Ordinal) ? 1
         : 2;

    private static string Stem(string name)
    {
        foreach (var ext in ModelExtensions)
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^4];
        return name;
    }

    /// <summary>Kind of a (normalised) model name, by its extension.</summary>
    public static ModelKind KindOf(string modelName)
    {
        var n = Normalize(modelName);
        return n.EndsWith(ExtYft, StringComparison.Ordinal) ? ModelKind.Fragment
             : n.EndsWith(ExtYdd, StringComparison.Ordinal) ? ModelKind.Dictionary
             : ModelKind.Drawable;
    }

    /// <summary>Whether <paramref name="modelName"/> (e.g. "prop_phone_ing",
    /// "prop_vend_fridge01.yft") is in the indexed install. A bare name tries
    /// .ydr, then .yft, then .ydd.</summary>
    public static bool Contains(string modelName)
        => Status == State.Ready && Resolve(modelName) != null;

    /// <summary>Whether a same-name <c>.ytd</c> exists for the model — the
    /// usual sign its textures are external rather than embedded.</summary>
    public static bool HasTextureDictionary(string modelName)
    {
        if (Status != State.Ready) return false;
        lock (Lock) return _ytds.ContainsKey(Stem(Normalize(modelName)));
    }

    // ─────────────── Search (Props → Game tab) ───────────────

    /// <summary>Models whose name matches <paramref name="query"/>, best first.
    /// Empty until the index is Ready. Cheap enough for a per-keystroke call
    /// off the UI thread.</summary>
    public static IReadOnlyList<string> Search(string? query, int limit = 200)
        => RankSearch(_names, query, limit);

    /// <summary>Pure ranking so it can be unit-tested without game files.
    /// Every whitespace-separated token must appear in the name (extension
    /// included, so "yft" narrows to fragments). Order: exact name/stem, then
    /// names starting with the query, then <c>prop_*</c> (what people are
    /// usually after), then everything else; the input order (stem, then
    /// .ydr/.yft/.ydd) is kept within a tier. An empty query yields nothing —
    /// thousands of unfiltered map pieces aren't a useful list.</summary>
    public static IReadOnlyList<string> RankSearch(IReadOnlyList<string> names, string? query, int limit)
    {
        var q = (query ?? "").Trim().ToLowerInvariant();
        if (q.Length == 0 || limit <= 0) return Array.Empty<string>();
        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return Array.Empty<string>();

        var tiers = new List<string>[4];
        for (int i = 0; i < tiers.Length; i++) tiers[i] = new List<string>();

        foreach (var name in names)
        {
            bool all = true;
            foreach (var t in tokens)
                if (!name.Contains(t, StringComparison.Ordinal)) { all = false; break; }
            if (!all) continue;

            int tier = name == q || Stem(name) == q ? 0
                : name.StartsWith(q, StringComparison.Ordinal) ? 1
                : name.StartsWith("prop_", StringComparison.Ordinal) ? 2
                : 3;
            tiers[tier].Add(name);
        }

        var result = new List<string>(Math.Min(limit, 256));
        foreach (var tier in tiers)
            foreach (var n in tier)
            {
                if (result.Count >= limit) return result;
                result.Add(n);
            }
        return result;
    }

    // ─────────────── Emotes prop carrier (geometry only) ───────────────

    /// <summary>Extract <paramref name="modelName"/>'s High LOD to a .glb in a
    /// temp session folder. Never throws — failures come back in the result.</summary>
    public static async Task<Result> ExportGlbAsync(string modelName)
    {
        var name = Normalize(modelName);
        if (name.Length == 0) return new Result(false, null, 0, "Type a prop model name first.");

        if (!await EnsureIndexedAsync().ConfigureAwait(false))
            return new Result(false, null, 0, Error ?? "Game files aren't available.");

        return await Task.Run(() =>
        {
            try
            {
                RpfManager? rpf;
                lock (Lock) rpf = _rpf;
                var resolved = Resolve(name);
                if (rpf is null || resolved is null)
                    return new Result(false, null, 0, NotFoundMessage(name));

                var (fullName, fe) = resolved.Value;
                if (KindOf(fullName) == ModelKind.Dictionary)
                    return new Result(false, null, 0, $"'{fullName}' is a drawable dictionary — name one model, not a .ydd.");

                DrawableBase? drawable;
                try { drawable = LoadMainDrawable(rpf, fullName, fe); }
                catch (Exception ex) { return new Result(false, null, 0, $"Couldn't extract {fullName}: {ex.Message}"); }
                if (drawable is null) return new Result(false, null, 0, $"{fullName} contains no drawable.");

                var r = YdrPreviewImporter.ImportDrawable(drawable, Stem(fullName));
                return r.Success
                    ? new Result(true, r.GlbPath, r.TriangleCount, null)
                    : new Result(false, null, 0, r.Error);
            }
            catch (Exception ex)
            {
                return new Result(false, null, 0, "Prop preview failed: " + ex.Message);
            }
        }).ConfigureAwait(false);
    }

    private static DrawableBase? LoadMainDrawable(RpfManager rpf, string fullName, RpfFileEntry fe)
    {
        if (KindOf(fullName) != ModelKind.Fragment) return rpf.GetFile<YdrFile>(fe)?.Drawable;
        var frag = rpf.GetFile<YftFile>(fe)?.Fragment;
        return frag?.Drawable ?? frag?.DrawableCloth ?? FirstArrayDrawable(frag);
    }

    // Some fragments (flags, tarps, a few breakables) keep their mesh in
    // DrawableArray with no main drawable at all.
    private static FragDrawable? FirstArrayDrawable(FragType? frag)
    {
        var arr = frag?.DrawableArray?.data_items;
        if (arr == null) return null;
        foreach (var d in arr) if (d != null) return d;
        return null;
    }

    // ─────────────── Props workspace import ───────────────

    /// <summary>Copy <paramref name="modelName"/> out of the game files into a
    /// fresh temp folder, together with every <c>.ytd</c> its shaders need.
    /// The folder is shaped exactly like a prop someone dragged out of OpenIV,
    /// so the ordinary load path — textured preview, convert from it —
    /// takes it from there. Never throws.</summary>
    public static async Task<ExtractResult> ExtractToFolderAsync(string modelName)
    {
        var name = Normalize(modelName);
        if (name.Length == 0) return new ExtractResult(false, null, 0, "Pick a model first.");

        if (!await EnsureIndexedAsync().ConfigureAwait(false))
            return new ExtractResult(false, null, 0, Error ?? "Game files aren't available.");

        return await Task.Run(() => ExtractToFolder(name)).ConfigureAwait(false);
    }

    private static ExtractResult ExtractToFolder(string name)
    {
        try
        {
            RpfManager? rpf;
            lock (Lock) rpf = _rpf;
            var resolved = Resolve(name);
            if (rpf is null || resolved is null)
                return new ExtractResult(false, null, 0, NotFoundMessage(name));

            var (fullName, entry) = resolved.Value;
            var kind = KindOf(fullName);
            var stem = Stem(fullName);

            var folder = Path.Combine(Path.GetTempPath(), "FiveOS",
                "GameProp-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);

            byte[] bytes;
            try { bytes = ExtractRaw(entry); }
            catch (Exception ex) { return new ExtractResult(false, null, 0, $"Couldn't extract {fullName}: {ex.Message}"); }
            var modelPath = Path.Combine(folder, fullName);
            File.WriteAllBytes(modelPath, bytes);

            // Which textures do the drawables reference without carrying them?
            // Those are what the dictionary hunt is for.
            var needed = new HashSet<uint>();
            try
            {
                foreach (var d in LoadDrawables(kind, bytes))
                    needed.UnionWith(ClothingTextureResolver.ExternalTextureHashes(new[] { d }));
            }
            catch (Exception ex) { FosLogger.Warn("prop-preview", $"{fullName}: shader scan failed", ex); }

            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var covered = new HashSet<uint>();

            // Same-name dictionary and its high-detail set always come along
            // when they exist: even with everything embedded, +hi/+hifr is the
            // game's own higher-res version and the preview/export prefer it.
            foreach (var cand in DictionariesNamed(stem))
                WriteDictionary(rpf, cand, folder, needed, covered, written, always: true);

            // Vehicles pull their generic textures (vehicle_generic_*) from the
            // shared vehshare dictionaries rather than anything of their own.
            bool vehicle = IsVehicleEntry(entry);
            if (vehicle && covered.Count < needed.Count)
            {
                foreach (var shared in VehshareNames)
                {
                    if (covered.Count >= needed.Count) break;
                    foreach (var cand in DictionariesNamed(shared))
                        WriteDictionary(rpf, cand, folder, needed, covered, written, always: false);
                }
            }

            // Archetype textureDictionary + gtxd parents, only while something
            // is still missing — the .ytyp scan behind this takes a few seconds
            // the first time.
            if (!vehicle && covered.Count < needed.Count)
            {
                // Pool thread, no sync context: blocking here just pins this
                // worker for the scan. A scan failure only costs this tier.
                try { EnsureArchetypesAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { FosLogger.Warn("prop-preview", "archetype scan unavailable", ex); }
                foreach (var cand in ArchetypeDictionaries(stem, kind))
                {
                    if (covered.Count >= needed.Count) break;
                    WriteDictionary(rpf, cand, folder, needed, covered, written, always: false);
                }
            }

            // Last resort: the other dictionaries in the same RPF folder, the
            // closest names first (one shared .ytd usually covers a prop family).
            if (!vehicle && covered.Count < needed.Count && entry.Parent != null)
            {
                List<RpfFileEntry>? dirYtds;
                lock (Lock) _ytdsByDir.TryGetValue(entry.Parent, out dirYtds);
                if (dirYtds != null)
                {
                    int scanned = 0;
                    long scannedBytes = 0;
                    foreach (var cand in dirYtds
                                 .OrderByDescending(e => NameAffinity(e.NameLower ?? "", stem))
                                 .ThenBy(e => e.NameLower, StringComparer.Ordinal))
                    {
                        if (covered.Count >= needed.Count) break;
                        if (scanned++ >= 64) break;                        // pathological-folder guard
                        scannedBytes += cand.FileSize;
                        if (scannedBytes > 256L * 1024 * 1024) break;      // ...and a byte budget
                        WriteDictionary(rpf, cand, folder, needed, covered, written, always: false);
                    }
                }
            }

            if (covered.Count < needed.Count)
                FosLogger.Info("prop-preview",
                    $"{fullName}: {needed.Count - covered.Count} of {needed.Count} external texture(s) not found (shared/parent dictionary)");

            FosLogger.Info("prop-preview",
                $"extracted {fullName} + {written.Count} .ytd ({covered.Count}/{needed.Count} external textures) → {folder}");
            return new ExtractResult(true, modelPath, written.Count, null, needed.Count, covered.Count, kind);
        }
        catch (Exception ex)
        {
            return new ExtractResult(false, null, 0, "Game model import failed: " + ex.Message);
        }
    }

    /// <summary>Every drawable a model file holds (one for .ydr/.yft, many for .ydd).</summary>
    private static IEnumerable<DrawableBase> LoadDrawables(ModelKind kind, byte[] rsc7Bytes)
    {
        switch (kind)
        {
            case ModelKind.Fragment:
            {
                var yft = new YftFile();
                yft.Load(rsc7Bytes);
                var frag = yft.Fragment;
                var main = frag?.Drawable ?? frag?.DrawableCloth;
                if (main != null) yield return main;
                else
                {
                    var arr = frag?.DrawableArray?.data_items;
                    if (arr != null) foreach (var d in arr) if (d != null) yield return d;
                }
                break;
            }
            case ModelKind.Dictionary:
            {
                var ydd = new YddFile();
                ydd.Load(rsc7Bytes);
                foreach (var d in ydd.Drawables ?? Array.Empty<Drawable>())
                    if (d != null) yield return d;
                break;
            }
            default:
            {
                var ydr = new YdrFile();
                ydr.Load(rsc7Bytes);
                if (ydr.Drawable != null) yield return ydr.Drawable;
                break;
            }
        }
    }

    /// <summary>Load a candidate dictionary and write it beside the model when
    /// it is the model's own (<paramref name="always"/>) or contributes a
    /// texture that is still missing.</summary>
    private static void WriteDictionary(RpfManager rpf, RpfFileEntry cand, string folder,
        HashSet<uint> needed, HashSet<uint> covered, HashSet<string> written, bool always)
    {
        var fileName = cand.Name ?? cand.NameLower ?? "";
        if (fileName.Length == 0 || written.Contains(fileName)) return;

        YtdFile? ytd;
        try { ytd = rpf.GetFile<YtdFile>(cand); }
        catch { return; }
        var items = ytd?.TextureDict?.Textures?.data_items;
        if (items == null) return;

        var contributes = new List<uint>();
        foreach (var t in items)
        {
            if (t == null) continue;
            uint h = (uint)t.NameHash;
            if (needed.Contains(h) && !covered.Contains(h)) contributes.Add(h);
        }
        if (!always && contributes.Count == 0) return;

        try
        {
            File.WriteAllBytes(Path.Combine(folder, fileName), ExtractRaw(cand));
            written.Add(fileName);
            // Only textures that are really on disk count as resolved — a
            // failed write must leave them for the next tier to find.
            foreach (var h in contributes) covered.Add(h);
        }
        catch (Exception ex) { FosLogger.Warn("prop-preview", $"{fileName}: .ytd extract failed", ex); }
    }

    // A dictionary name plus its +hi / +hifr companions, when they exist.
    private static IEnumerable<RpfFileEntry> DictionariesNamed(string txdName)
    {
        var found = new List<RpfFileEntry>(3);
        lock (Lock)
        {
            if (_ytds.TryGetValue(txdName, out var fe)) found.Add(fe);
            foreach (var suffix in HiSuffixes)
                if (_ytds.TryGetValue(txdName + suffix, out fe)) found.Add(fe);
        }
        return found;
    }

    /// <summary>The archetype's textureDictionary (when it isn't just the
    /// model name) and the gtxd parent chain of both, hi variants included.
    /// A dictionary file is no archetype itself — its drawables are, each
    /// with its own textureDictionary — so those are gathered instead.</summary>
    private static IEnumerable<RpfFileEntry> ArchetypeDictionaries(string stem, ModelKind kind)
    {
        var seen = new HashSet<uint>();
        var queue = new Queue<uint>();
        uint self = JenkHash.GenHash(stem);
        seen.Add(self);

        Dictionary<uint, ArchInfo> archetypes;
        Dictionary<uint, uint> parents;
        Dictionary<uint, List<uint>> byDrawableDict;
        lock (Lock) { archetypes = _archetypes; parents = _txdParents; byDrawableDict = _txdsByDrawableDict; }

        if (kind == ModelKind.Dictionary)
        {
            if (byDrawableDict.TryGetValue(self, out var txds))
                foreach (var t in txds) if (t != 0 && seen.Add(t)) queue.Enqueue(t);
        }
        else if (archetypes.TryGetValue(self, out var arch) && arch.TextureDict != 0 && seen.Add(arch.TextureDict))
            queue.Enqueue(arch.TextureDict);
        if (parents.TryGetValue(self, out var p0) && seen.Add(p0)) queue.Enqueue(p0);

        var found = new List<RpfFileEntry>();
        int depth = 0;
        while (queue.Count > 0 && depth++ < 16)
        {
            var h = queue.Dequeue();
            RpfFileEntry? fe;
            lock (Lock) _ytdsByHash.TryGetValue(h, out fe);
            if (fe != null)
            {
                found.Add(fe);
                var stemName = (fe.NameLower ?? "")[..^4];
                found.AddRange(DictionariesNamed(stemName).Where(e => !ReferenceEquals(e, fe)));
            }
            if (parents.TryGetValue(h, out var parent) && seen.Add(parent)) queue.Enqueue(parent);
        }
        return found;
    }

    /// <summary>Scan every .ytyp (archetype → textureDictionary) and gtxd
    /// (child txd → parent txd) once, in the background. Mirrors what
    /// CodeWalker's GameFileCache does at start-up, minus the string index.</summary>
    public static Task EnsureArchetypesAsync()
    {
        lock (Lock)
        {
            if (Status != State.Ready) return Task.CompletedTask;
            if (_archetypeTask != null) return _archetypeTask;
            var rpf = _rpf!;
            var ytyps = _ytypEntries;
            var gtxds = _gtxdEntries;
            int generation = _generation;
            _archetypeTask = Task.Run(() => ScanArchetypes(rpf, ytyps, gtxds, generation));
            return _archetypeTask;
        }
    }

    /// <summary>gtxd parent of a texture dictionary (0 when none). Only
    /// meaningful after <see cref="EnsureArchetypesAsync"/>.</summary>
    public static uint TryGetParentTxd(uint txdHash)
    {
        lock (Lock) return _txdParents.TryGetValue(txdHash, out var p) ? p : 0;
    }

    /// <summary>Archetype textureDictionary hash for a model name (0 when the
    /// archetype is unknown or names no dictionary).</summary>
    public static uint TryGetArchetypeTxd(string modelName)
    {
        var h = JenkHash.GenHash(Stem(Normalize(modelName)));
        lock (Lock) return _archetypes.TryGetValue(h, out var a) ? a.TextureDict : 0;
    }

    public static int ArchetypeCount { get { lock (Lock) return _archetypes.Count; } }
    public static int TxdParentCount { get { lock (Lock) return _txdParents.Count; } }

    private static void ScanArchetypes(RpfManager rpf, List<RpfFileEntry> ytyps, List<RpfFileEntry> gtxds, int generation)
    {
        try { ScanArchetypesCore(rpf, ytyps, gtxds, generation); }
        catch (Exception ex)
        {
            // Best effort: leave the maps empty and let a later import retry.
            FosLogger.Warn("prop-preview", "archetype scan failed", ex);
            lock (Lock) { if (generation == _generation) _archetypeTask = null; }
        }
    }

    private static void ScanArchetypesCore(RpfManager rpf, List<RpfFileEntry> ytyps, List<RpfFileEntry> gtxds, int generation)
    {
        var archetypes = new Dictionary<uint, ArchInfo>();
        var parents = new Dictionary<uint, uint>();
        var byDrawableDict = new Dictionary<uint, List<uint>>();
        int ytypFails = 0;

        // Patched definitions first (update.rpf / DLC), so first-wins below
        // keeps the one the game actually uses.
        var ordered = ytyps.OrderByDescending(e => Precedence(e.Path)).ToList();
        foreach (var fe in ordered)
        {
            YtypFile? ytyp;
            try { ytyp = rpf.GetFile<YtypFile>(fe); }
            catch { ytypFails++; continue; }
            var all = ytyp?.AllArchetypes;
            if (all == null) continue;
            foreach (var a in all)
            {
                if (a == null) continue;
                var info = new ArchInfo(a.TextureDict, a.DrawableDict, (int)a._BaseArchetypeDef.assetType);
                // Keyed by assetName (what the model file is called) AND by the
                // archetype name — usually equal, but not always.
                uint hash = a.Hash;
                uint nameHash = a._BaseArchetypeDef.name;
                if (hash != 0) archetypes.TryAdd(hash, info);
                if (nameHash != 0 && nameHash != hash) archetypes.TryAdd(nameHash, info);
                if (info.DrawableDict != 0 && info.TextureDict != 0)
                {
                    if (!byDrawableDict.TryGetValue(info.DrawableDict, out var list))
                        byDrawableDict[info.DrawableDict] = list = new List<uint>();
                    if (!list.Contains(info.TextureDict)) list.Add(info.TextureDict);
                }
            }
        }

        foreach (var fe in gtxds)
        {
            Dictionary<string, string>? rel = null;
            try
            {
                if ((fe.NameLower ?? "").EndsWith(".ymt", StringComparison.Ordinal))
                    rel = rpf.GetFile<YmtFile>(fe)?.CMapParentTxds;
                if (rel == null || rel.Count == 0)
                    rel = rpf.GetFile<GtxdFile>(fe)?.TxdRelationships;
            }
            catch (Exception ex) { FosLogger.Warn("prop-preview", $"{fe.Name}: gtxd load failed", ex); }
            if (rel == null) continue;
            foreach (var (child, parent) in rel)
            {
                if (string.IsNullOrWhiteSpace(child) || string.IsNullOrWhiteSpace(parent)) continue;
                var c = child.Trim().ToLowerInvariant();
                var p = parent.Trim().ToLowerInvariant();
                JenkIndex.Ensure(c); JenkIndex.Ensure(p);
                parents[JenkHash.GenHash(c)] = JenkHash.GenHash(p);
            }
        }

        lock (Lock)
        {
            if (generation != _generation) return;   // folder changed meanwhile
            _archetypes = archetypes;
            _txdParents = parents;
            _txdsByDrawableDict = byDrawableDict;
        }
        FosLogger.Info("prop-preview",
            $"archetype scan: {archetypes.Count} archetypes from {ytyps.Count - ytypFails}/{ytyps.Count} .ytyp, {byDrawableDict.Count} drawable dictionaries, {parents.Count} gtxd parent links");
    }

    /// <summary>Entry → the bytes OpenIV would write to disk: decompressed
    /// payload re-wrapped in an RSC7 header, so CodeWalker's file loaders
    /// reads it exactly like a loose file.</summary>
    private static byte[] ExtractRaw(RpfFileEntry fe)
    {
        var data = fe.File.ExtractFile(fe)
            ?? throw new InvalidDataException("Extract failed: " + fe.Name);
        if (fe is RpfResourceFileEntry re)
        {
            data = ResourceBuilder.Compress(data);
            data = ResourceBuilder.AddResourceHeader(re, data);
        }
        return data;
    }

    // Higher = the dictionary's name is closer to the model's. A family
    // dictionary is usually the model name minus a colour/number suffix
    // (prop_medusas.ytd for prop_medusa_red.ydr), so score by how many
    // leading characters the two names share — an exact match tops it.
    private static int NameAffinity(string ytdNameLower, string stem)
    {
        var n = ytdNameLower.EndsWith(ExtYtd, StringComparison.Ordinal) ? ytdNameLower[..^4] : ytdNameLower;
        if (n == stem) return int.MaxValue;
        int shared = 0;
        int max = Math.Min(n.Length, stem.Length);
        while (shared < max && n[shared] == stem[shared]) shared++;
        return shared;
    }

    /// <summary>Find the index entry for a normalised name. A bare stem tries
    /// .ydr, then .yft, then .ydd.</summary>
    private static (string FullName, RpfFileEntry Entry)? Resolve(string name)
    {
        var n = Normalize(name);
        if (n.Length == 0) return null;
        lock (Lock)
        {
            if (HasModelExtension(n))
                return _models.TryGetValue(n, out var fe) ? (n, fe) : null;
            foreach (var ext in ModelExtensions)
                if (_models.TryGetValue(n + ext, out var fe)) return (n + ext, fe);
        }
        return null;
    }

    private static bool HasModelExtension(string n)
    {
        foreach (var ext in ModelExtensions)
            if (n.EndsWith(ext, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string NotFoundMessage(string name)
        => $"'{name}' isn't in the game files as a .ydr, .yft or .ydd (check the spelling, or search for part of the name).";

    /// <summary>Re-point at a new folder. Clears the index so the next lookup
    /// rebuilds it.</summary>
    public static void Reset()
    {
        lock (Lock)
        {
            _generation++;
            _rpf = null;
            _models = new(StringComparer.OrdinalIgnoreCase);
            _ytds = new(StringComparer.OrdinalIgnoreCase);
            _ytdsByHash = new();
            _ytdsByDir = new();
            _ytypEntries = new();
            _gtxdEntries = new();
            _names = Array.Empty<string>();
            _archetypes = new();
            _txdParents = new();
            _txdsByDrawableDict = new();
            _archetypeTask = null;
            _loadTask = null;
            Status = State.NotConfigured;
            Error = null;
        }
    }

    /// <summary>Trimmed, lower-case; a trailing ".ydr"/".yft"/".ydd" is kept.</summary>
    private static string Normalize(string? modelName) => (modelName ?? "").Trim().ToLowerInvariant();
}
