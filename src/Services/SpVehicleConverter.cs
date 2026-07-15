// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CodeWalker.GameFiles;

namespace FiveOS.Services;

/// <summary>
/// The REVERSE direction of the RPF tab: take one or MANY singleplayer
/// add-on cars (dlc.rpf files, mod folders containing them, or loose
/// extracted dlc trees) and emit a single FiveM-ready resource:
///
///   &lt;pack&gt;/
///     fxmanifest.lua        files{} + data_file declarations
///     stream/[&lt;car&gt;/]       .yft/.ytd/… (incl. tuning-part rpf contents)
///     data/[&lt;car&gt;/]         vehicles/handling/carcols/carvariations/…
///     audio/[&lt;car&gt;/]        custom engine sounds (.rel + sfx wavepacks)
///     vehicle_names.lua     AddTextEntry display names (from the dlc GXT2s)
///     README.txt            spawn names per pack + install steps
///
/// With ONE source the layout stays flat (stream/x.yft); with several, each
/// source gets its own subfolder (FiveM scans stream/ recursively and
/// data_file paths may point anywhere in the resource), mirroring how the
/// popular hand-built mega carpacks are laid out.
///
/// Reading is CodeWalker's RpfFile scan — nested archives (vehicles.rpf,
/// *_mods.rpf tuning parts) are walked recursively. IMPORTANT: CW's
/// ExtractFile returns resource bytes WITHOUT the RSC7 header (raw
/// decompressed pages); a FiveM server streams loose files in on-disk
/// resource form, so resource entries are re-compressed and re-headered via
/// ResourceBuilder.Compress + AddResourceHeader (verified round-trip).
///
/// SP-only plumbing (content.xml, setup2.xml, dlctext / contentunlocks
/// metas, non-english gxt rpfs) is deliberately dropped — FiveM has its own
/// equivalents (fxmanifest + AddTextEntry).
/// </summary>
public sealed class SpVehicleConverter
{
    public sealed record Options(
        // Resource folder name; null = derive from the dlc device name for a
        // single source, or the common input folder name for a multipack.
        string? ResourceName = null,
        // true: merge every car mod found into one pack (per-car subfolders).
        // false: SINGLE-CAR mode — when the input expands to several mods
        // (e.g. a download shipping Enhanced + Legacy variants), only the
        // largest one is converted and the rest are skipped with a note.
        bool MergeAll = true);

    /// <summary>Per-source outcome so the UI/README can group by car pack.</summary>
    public sealed record SourceInfo(
        string Key,                    // subfolder / grouping key (device name)
        string Label,                  // where it came from (file/folder name)
        IReadOnlyList<string> Models); // spawn names contributed

    public sealed record Result(
        bool Success,
        string? OutputPath,        // <outputRoot>/<resourceName>
        string ResourceName,
        IReadOnlyList<string> Models,          // all spawn names
        IReadOnlyList<SourceInfo> Sources,
        IReadOnlyList<RpfPacker.FileResult> Files,
        IReadOnlyList<string> Warnings,
        string? Error,
        // model → human display name (from the dlc GXT2), where known.
        IReadOnlyDictionary<string, string>? ModelLabels = null);

    // ── Classification tables ────────────────────────────────────────────

    private static readonly HashSet<string> StreamExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".yft", ".ytd", ".ydr", ".ydd", ".ycd", ".ybn",
    };

    /// <summary>meta stem → FiveM data_file type ("" = SP-only, drop the file).
    /// Matched as exact stem, or stem followed by a digit / underscore, so
    /// "handling_sports.meta" and "vehicles2.meta" still map while
    /// "vehicleshopdisplay.meta" does NOT false-match "vehicles".</summary>
    private static readonly (string Stem, string DataFileType)[] MetaTypes =
    {
        ("vehicles",        "VEHICLE_METADATA_FILE"),
        ("handling",        "HANDLING_FILE"),
        ("carcols",         "CARCOLS_FILE"),
        ("carvariations",   "VEHICLE_VARIATION_FILE"),
        ("vehiclelayouts",  "VEHICLE_LAYOUTS_FILE"),
        ("ptfxassetinfo",   "PTFXASSETINFO_FILE"),
        ("contentunlocks",  ""),   // SP progression — meaningless in FiveM
        ("caraddoncontentunlocks", ""),
        ("dlctext",         ""),   // SP text pipeline — replaced by AddTextEntry
    };

    // One in-memory work item, source-agnostic (rpf entry or loose file).
    private sealed record Item(string Name, string ParentDir, Func<byte[]?> GetBytes, string SourceLabel);

    // One car pack to fold into the resource.
    private sealed record Source(string Label, List<Item> Items, long SizeBytes = 0)
    {
        public string Key = "";
    }

    // ── Entry points ─────────────────────────────────────────────────────

    public Result Convert(string inputPath, string outputRoot, Options opts, Action<string> log, CancellationToken cancel = default)
        => Convert(new[] { inputPath }, outputRoot, opts, log, cancel);

    public Result Convert(IReadOnlyList<string> inputPaths, string outputRoot, Options opts, Action<string> log, CancellationToken cancel = default)
    {
        var warnings = new List<string>();
        var files = new List<RpfPacker.FileResult>();

        void Ok(Item it, string rel, long bytes, bool isResource)
            => files.Add(new RpfPacker.FileResult(it.SourceLabel, rel, bytes, isResource, true, null, null));
        void Skip(Item it, string why)
            => files.Add(new RpfPacker.FileResult(it.SourceLabel, it.Name, 0, false, false, why, null));
        void Fault(Item it, string why)
            => files.Add(new RpfPacker.FileResult(it.SourceLabel, it.Name, 0, false, false, null, why));
        Result Fail(string msg) => new(false, null, "", Array.Empty<string>(),
            Array.Empty<SourceInfo>(), files, warnings, msg);

        try
        {
            // ── Expand every input into car-pack sources ─────────────────
            var sources = new List<Source>();
            foreach (var input in inputPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancel.ThrowIfCancellationRequested();
                if (File.Exists(input) && input.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                {
                    var s = SourceFromRpf(input, log, warnings);
                    if (s != null) sources.Add(s);
                }
                else if (Directory.Exists(input))
                {
                    // A folder can hold several independent car mods (each
                    // with its own dlc.rpf) — that's the multipack case. A
                    // folder with no dlc.rpf falls back to any .rpf(s) found,
                    // and no rpfs at all means an extracted dlc tree.
                    var dlcRpfs = Directory.EnumerateFiles(input, "dlc.rpf", SearchOption.AllDirectories).ToList();
                    if (dlcRpfs.Count > 0)
                    {
                        foreach (var rp in dlcRpfs.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                        {
                            var s = SourceFromRpf(rp, log, warnings);
                            if (s != null) sources.Add(s);
                        }
                    }
                    else
                    {
                        var rpfs = Directory.EnumerateFiles(input, "*.rpf", SearchOption.AllDirectories).ToList();
                        if (rpfs.Count == 1)
                        {
                            var s = SourceFromRpf(rpfs[0], log, warnings);
                            if (s != null) sources.Add(s);
                        }
                        else if (rpfs.Count > 1)
                        {
                            foreach (var rp in rpfs.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                            {
                                var s = SourceFromRpf(rp, log, warnings);
                                if (s != null) sources.Add(s);
                            }
                        }
                        else
                        {
                            log("Scanning extracted dlc folder…");
                            sources.Add(new Source(Path.GetFileName(input.TrimEnd('\\', '/')),
                                CollectFromFolder(input)));
                        }
                    }
                }
                else
                    warnings.Add("Input not found (skipped): " + input);
            }
            if (sources.Count == 0)
                return Fail("No readable car mod found in the selected input(s).");

            if (!opts.MergeAll && sources.Count > 1)
            {
                // Single-car mode: keep the biggest variant, drop the rest.
                var keep = sources.OrderByDescending(s => s.SizeBytes).First();
                var dropped = sources.Where(s => s != keep).Select(s => s.Label).ToList();
                warnings.Add($"Single-car mode: found {sources.Count} car mods — converted '{keep.Label}' "
                    + $"(largest) and skipped {string.Join(", ", dropped)}. Switch to Car pack to merge them all.");
                sources = new List<Source> { keep };
            }

            // ── Keys: device name per source, deduped ────────────────────
            var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sources)
            {
                var key = Sanitize(FindDeviceName(s.Items) is { Length: > 0 } dev ? dev : s.Label);
                var baseKey = key;
                for (int n = 2; !usedKeys.Add(key); n++) key = $"{baseKey}_{n}";
                s.Key = key;
            }

            bool multi = sources.Count > 1;
            var resourceName = Sanitize(opts.ResourceName
                ?? (multi ? DefaultPackName(inputPaths) : sources[0].Key));
            var outDir = Path.Combine(outputRoot, resourceName);
            Directory.CreateDirectory(outDir);
            log(multi
                ? $"Building FiveM car pack '{resourceName}' from {sources.Count} mods…"
                : $"Building FiveM resource '{resourceName}'…");

            // ── Route every source's items ───────────────────────────────
            var globalStreamNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // name → key
            var dataFiles = new List<(string RelPath, string Type)>();
            var manifestFiles = new List<string>();
            var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var modelLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sourceInfos = new List<SourceInfo>();
            var allModels = new List<string>();

            foreach (var src in sources)
            {
                cancel.ThrowIfCancellationRequested();
                var pfx = multi ? src.Key + "/" : "";          // fxmanifest paths
                string Sub(string top, string name) => multi
                    ? Path.Combine(top, src.Key, name)
                    : Path.Combine(top, name);

                var streamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var wavePacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var gxtCandidates = new List<Item>();
                var metaTexts = new List<(string NameLower, string Text)>();
                var localManifest = new List<string>();

                foreach (var it in src.Items)
                {
                    var lower = it.Name.ToLowerInvariant();
                    var ext = Path.GetExtension(lower);

                    if (lower is "content.xml" or "setup2.xml") { Skip(it, "SP dlc plumbing"); continue; }
                    if (lower.EndsWith(".nametable")) { Skip(it, "dev-side nametable"); continue; }
                    if (Regex.IsMatch(lower, @"\.dat\d+$")) { Skip(it, "duplicate of the .rel copy"); continue; }

                    if (StreamExts.Contains(ext))
                    {
                        if (!streamNames.Add(it.Name)) { Skip(it, "duplicate stream name — first copy wins"); continue; }
                        if (globalStreamNames.TryGetValue(it.Name, out var otherKey) && otherKey != src.Key)
                            warnings.Add($"{it.Name} exists in both '{otherKey}' and '{src.Key}' — RAGE streams by file name, so one will win in-game. Rename one car if both matter.");
                        else
                            globalStreamNames[it.Name] = src.Key;

                        var bytes = it.GetBytes();
                        if (bytes == null) { Fault(it, "extract failed"); continue; }
                        var rel = Sub("stream", it.Name);
                        WriteOut(outDir, rel, bytes);
                        Ok(it, rel, bytes.Length, isResource: true);
                        continue;
                    }

                    if (ext == ".meta" || ext == ".xml")
                    {
                        var stem = Path.GetFileNameWithoutExtension(lower);
                        var map = MetaTypes.FirstOrDefault(m => StemMatches(stem, m.Stem));
                        if (map.Stem != null && map.DataFileType == "")
                        { Skip(it, "SP-only meta (FiveM equivalent is generated)"); continue; }

                        var bytes = it.GetBytes();
                        if (bytes == null) { Fault(it, "extract failed"); continue; }

                        var outName = it.Name;
                        for (int n = 2; localManifest.Contains($"data/{pfx}{outName}", StringComparer.OrdinalIgnoreCase); n++)
                            outName = $"{Path.GetFileNameWithoutExtension(it.Name)}_{n}{ext}";

                        var rel = Sub("data", outName);
                        WriteOut(outDir, rel, bytes);
                        var relLua = $"data/{pfx}{outName}";
                        localManifest.Add(relLua);
                        if (map.Stem != null)
                            dataFiles.Add((relLua, map.DataFileType));
                        else
                            warnings.Add($"{it.Name} ({src.Key}): unrecognized meta — copied but NOT declared as a data_file.");
                        metaTexts.Add((stem, Encoding.UTF8.GetString(bytes)));
                        Ok(it, rel, bytes.Length, isResource: false);
                        continue;
                    }

                    if (ext == ".rel")
                    {
                        var bytes = it.GetBytes();
                        if (bytes == null) { Fault(it, "extract failed"); continue; }
                        var rel = Sub("audio", it.Name);
                        WriteOut(outDir, rel, bytes);
                        localManifest.Add($"audio/{pfx}{it.Name}");
                        var type = AudioRelType(lower);
                        if (type != null)
                            dataFiles.Add(($"audio/{pfx}" + Regex.Replace(it.Name, @"\.dat\d+\.rel$", ".dat", RegexOptions.IgnoreCase), type));
                        else
                            warnings.Add($"{it.Name} ({src.Key}): unknown audio .rel flavor — shipped but not declared.");
                        Ok(it, rel, bytes.Length, isResource: false);
                        continue;
                    }

                    if (ext == ".awc")
                    {
                        var bytes = it.GetBytes();
                        if (bytes == null) { Fault(it, "extract failed"); continue; }
                        var pack = string.IsNullOrEmpty(it.ParentDir) || it.ParentDir.Equals("audio", StringComparison.OrdinalIgnoreCase)
                            ? "dlc_" + src.Key
                            : it.ParentDir;
                        var rel = Sub("audio", Path.Combine("sfx", pack, it.Name));
                        WriteOut(outDir, rel, bytes);
                        localManifest.Add($"audio/{pfx}sfx/{pack}/{it.Name}");
                        wavePacks.Add(pack);
                        Ok(it, rel, bytes.Length, isResource: false);
                        continue;
                    }

                    if (ext == ".gxt2")
                    {
                        gxtCandidates.Add(it);
                        Skip(it, "gxt2 → converted to vehicle_names.lua");
                        continue;
                    }

                    Skip(it, "not needed in a FiveM resource");
                }

                foreach (var pack in wavePacks)
                    dataFiles.Add(($"audio/{pfx}sfx/{pack}", "AUDIO_WAVEPACK"));
                manifestFiles.AddRange(localManifest);

                // vehicles.meta → spawn names + gameNames for this source.
                var models = new List<string>();
                var gameNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, text) in metaTexts.Where(m => StemMatches(m.NameLower, "vehicles")))
                {
                    foreach (Match item in Regex.Matches(text, @"<Item>.*?</Item>", RegexOptions.Singleline))
                    {
                        var model = Regex.Match(item.Value, @"<modelName>([^<]+)</modelName>").Groups[1].Value.Trim();
                        var game = Regex.Match(item.Value, @"<gameName>([^<]+)</gameName>").Groups[1].Value.Trim();
                        if (model.Length == 0) continue;
                        var keyName = model.ToLowerInvariant();
                        if (!models.Contains(keyName)) models.Add(keyName);
                        if (game.Length > 0) gameNames[model] = game;
                    }
                }
                // REPLACE-style dlc (e.g. "Improved <vanilla car>" mods): no
                // add-on metas at all — the streamed files override the
                // vanilla assets that share their names. Derive the car list
                // from the yft stems so Preview still has something to show.
                if (models.Count == 0)
                {
                    var stems = src.Items
                        .Where(i => i.Name.EndsWith(".yft", StringComparison.OrdinalIgnoreCase))
                        .Select(i => Regex.Replace(
                            Path.GetFileNameWithoutExtension(i.Name).ToLowerInvariant(), "_hi$", ""))
                        .Distinct()
                        .ToList();
                    if (stems.Count > 0)
                    {
                        models.AddRange(stems);
                        warnings.Add($"'{src.Label}': no vehicles.meta — this is a REPLACE-style mod. "
                            + $"It overrides the vanilla {string.Join(", ", stems)} for everyone on the server; "
                            + "spawn the vanilla name in-game (no new handling entry to edit).");
                    }
                }

                allModels.AddRange(models.Where(m => !allModels.Contains(m)));
                sourceInfos.Add(new SourceInfo(src.Key, src.Label, models));

                // GXT2 → display names keyed by JOAAT(gameName); american first.
                foreach (var it in gxtCandidates.OrderByDescending(g =>
                             g.SourceLabel.Contains("american", StringComparison.OrdinalIgnoreCase)))
                {
                    var g = ParseGxt2(it.GetBytes());
                    if (g?.TextEntries == null) continue;
                    bool any = false;
                    foreach (var t in g.TextEntries)
                    {
                        if (string.IsNullOrWhiteSpace(t.Text)) continue;
                        foreach (var gn in gameNames.Values)
                            if (JenkHash.GenHash(gn.ToLowerInvariant()) == t.Hash && !displayNames.ContainsKey(gn))
                            { displayNames[gn] = t.Text; any = true; }
                    }
                    if (any) break;
                }
                foreach (var (model, gn) in gameNames)
                    if (displayNames.TryGetValue(gn, out var lbl) && !modelLabels.ContainsKey(model))
                        modelLabels[model.ToLowerInvariant()] = lbl;

                if (models.Count == 0)
                    warnings.Add($"'{src.Label}': no vehicle models found at all — is it actually a vehicle dlc?");
            }

            // ── fxmanifest.lua ───────────────────────────────────────────
            var fx = new StringBuilder();
            fx.AppendLine("-- Generated by FiveOS (RPF · SP car → FiveM resource)");
            fx.AppendLine(multi
                ? $"-- Car pack from {sources.Count} SP mods: {string.Join(", ", sources.Select(s => s.Key))}"
                : $"-- Source: {sources[0].Label}");
            fx.AppendLine();
            fx.AppendLine("fx_version 'cerulean'");
            fx.AppendLine("game 'gta5'");
            fx.AppendLine();
            fx.AppendLine("files {");
            foreach (var f in manifestFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                fx.AppendLine($"    '{LuaStr(f)}',");
            fx.AppendLine("}");
            fx.AppendLine();
            foreach (var (rel, type) in dataFiles.Distinct())
                fx.AppendLine($"data_file '{LuaStr(type)}' '{LuaStr(rel)}'");
            if (displayNames.Count > 0)
            {
                fx.AppendLine();
                fx.AppendLine("client_script 'vehicle_names.lua'");
            }
            File.WriteAllText(Path.Combine(outDir, "fxmanifest.lua"), fx.ToString());

            if (displayNames.Count > 0)
            {
                var lua = new StringBuilder();
                lua.AppendLine("-- Vehicle display names (from the dlcs' GXT2 text tables).");
                lua.AppendLine("Citizen.CreateThread(function()");
                foreach (var (game, label) in displayNames)
                    lua.AppendLine($"    AddTextEntry('{LuaStr(game)}', '{LuaStr(label)}')");
                lua.AppendLine("end)");
                File.WriteAllText(Path.Combine(outDir, "vehicle_names.lua"), lua.ToString());
            }

            // ── README ───────────────────────────────────────────────────
            var rd = new StringBuilder();
            rd.AppendLine($"{resourceName} — FiveM add-on vehicle {(multi ? "pack" : "resource")}");
            rd.AppendLine(new string('=', 50));
            rd.AppendLine();
            rd.AppendLine("Install:");
            rd.AppendLine($"  1. Copy this '{resourceName}' folder into your server's resources/ directory.");
            rd.AppendLine($"  2. Add:  ensure {resourceName}   to your server.cfg.");
            rd.AppendLine("  3. Restart the server (or `refresh` + `ensure` from the console).");
            rd.AppendLine();
            rd.AppendLine("Spawn names:");
            foreach (var si in sourceInfos)
            {
                if (multi) rd.AppendLine($"  [{si.Key}]");
                foreach (var m in si.Models)
                    rd.AppendLine($"  {(multi ? "  " : "")}{m}");
            }
            rd.AppendLine();
            rd.AppendLine("Spawn with vMenu, /car <name>, or any trainer/spawner.");
            File.WriteAllText(Path.Combine(outDir, "README.txt"), rd.ToString());

            log($"Done — {allModels.Count} vehicle(s) from {sources.Count} source(s), {files.Count(f => f.Packed)} file(s) → {outDir}");
            return new Result(true, outDir, resourceName, allModels, sourceInfos, files, warnings, null, modelLabels);
        }
        catch (OperationCanceledException)
        {
            // Let cancellation propagate to the caller instead of being masked
            // as a generic "failed" Result by the catch below.
            throw;
        }
        catch (Exception ex)
        {
            return new Result(false, null, "", Array.Empty<string>(),
                Array.Empty<SourceInfo>(), files, warnings, ex.Message);
        }
    }

    // ── Source walkers ───────────────────────────────────────────────────

    private static Source? SourceFromRpf(string rpfPath, Action<string> log, List<string> warnings)
    {
        log($"Reading {Path.GetFileName(rpfPath)}…");
        var rpf = new RpfFile(rpfPath, Path.GetFileName(rpfPath));
        string? scanError = null;
        rpf.ScanStructure(_ => { }, e => scanError ??= e);
        if (rpf.AllEntries == null || rpf.AllEntries.Count == 0)
        {
            warnings.Add($"{Path.GetFileName(rpfPath)}: " + (scanError != null
                ? $"couldn't read the archive ({scanError})"
                : "archive is empty or encrypted — extract it with OpenIV and point me at the folder."));
            return null;
        }
        if (rpf.Encryption is not (RpfEncryption.OPEN or RpfEncryption.NONE))
            warnings.Add($"{Path.GetFileName(rpfPath)}: encryption is {rpf.Encryption} — extraction may fail without game keys.");

        // Label: the mod folder containing the rpf beats the generic "dlc.rpf".
        // Label: "dlc.rpf" says nothing — use the mod folder, and include the
        // grandparent so variant pairs read as "Enhanced/sp_x" vs "Legacy/sp_x".
        var stem = Path.GetFileNameWithoutExtension(rpfPath);
        var label = stem;
        if (stem.Equals("dlc", StringComparison.OrdinalIgnoreCase))
        {
            var parent = new DirectoryInfo(Path.GetDirectoryName(rpfPath)!);
            label = parent.Parent != null ? $"{parent.Parent.Name}/{parent.Name}" : parent.Name;
        }
        return new Source(label, CollectFromRpf(rpf), new FileInfo(rpfPath).Length);
    }

    /// <summary>Flatten an rpf (incl. nested archives) into work items.
    /// Resource entries get their RSC7 header rebuilt on extract.</summary>
    private static List<Item> CollectFromRpf(RpfFile root)
    {
        var items = new List<Item>();
        void Walk(RpfFile r)
        {
            // Per-language text rpfs: only american carries the names we use.
            var n = r.NameLower ?? "";
            if (n.EndsWith("dlc.rpf") && n != "dlc.rpf" && !n.StartsWith("american"))
                return;

            foreach (var e in r.AllEntries ?? new())
            {
                if (e is not RpfFileEntry fe) continue;
                if (fe.NameLower?.EndsWith(".rpf") == true) continue;  // walked via Children
                var parent = fe.Parent?.Name ?? "";
                items.Add(new Item(fe.Name, parent, () =>
                {
                    var data = fe.File.ExtractFile(fe);
                    if (data == null) return null;
                    if (fe is RpfResourceFileEntry re)
                    {
                        // Rebuild the on-disk resource form FiveM streams.
                        data = ResourceBuilder.Compress(data);
                        data = ResourceBuilder.AddResourceHeader(re, data);
                    }
                    return data;
                }, fe.Path));
            }
            foreach (var c in r.Children ?? new())
                Walk(c);
        }
        Walk(root);
        return items;
    }

    /// <summary>Loose extracted dlc tree — files already carry their RSC7
    /// headers on disk, so bytes pass through untouched.</summary>
    private static List<Item> CollectFromFolder(string rootDir)
    {
        var items = new List<Item>();
        foreach (var f in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
        {
            var parent = new DirectoryInfo(Path.GetDirectoryName(f)!).Name;
            items.Add(new Item(Path.GetFileName(f), parent, () => File.ReadAllBytes(f), f));
        }
        return items;
    }

    // ── Parsers / helpers ────────────────────────────────────────────────

    /// <summary>GXT2 arrives as a plain binary blob from most dlcs, but a
    /// loose-extracted one may carry an RSC7 header — handle both.</summary>
    private static Gxt2File? ParseGxt2(byte[]? data)
    {
        if (data is not { Length: > 8 }) return null;
        try
        {
            if (BitConverter.ToUInt32(data, 0) == 0x37435352) // 'RSC7'
            {
                var entry = RpfFile.CreateResourceFileEntry(ref data, 0);
                data = ResourceBuilder.Decompress(data);
                var g = new Gxt2File();
                g.Load(data, entry);
                return g;
            }
            var g2 = new Gxt2File();
            g2.Load(data, new RpfResourceFileEntry { Name = "global.gxt2", NameLower = "global.gxt2" });
            return g2;
        }
        catch { return null; }
    }

    /// <summary>"dlc_spchumclassics:/data/…" prefixes inside content.xml name
    /// the dlc device — the natural per-source key.</summary>
    private static string FindDeviceName(List<Item> items)
    {
        var content = items.FirstOrDefault(i => i.Name.Equals("content.xml", StringComparison.OrdinalIgnoreCase));
        if (content == null) return "";
        try
        {
            var text = Encoding.UTF8.GetString(content.GetBytes() ?? Array.Empty<byte>());
            var m = Regex.Match(text, @"dlc_([A-Za-z0-9_]+):");
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : "";
        }
        catch { return ""; }
    }

    private static bool StemMatches(string stem, string prefix)
        => stem.Equals(prefix, StringComparison.OrdinalIgnoreCase)
           || (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && stem.Length > prefix.Length
               && (stem[prefix.Length] == '_' || char.IsDigit(stem[prefix.Length])));

    private static string? AudioRelType(string nameLower) => nameLower switch
    {
        _ when nameLower.EndsWith(".dat151.rel") => "AUDIO_GAMEDATA",
        _ when nameLower.EndsWith(".dat54.rel") => "AUDIO_SOUNDDATA",
        _ when nameLower.EndsWith(".dat10.rel") => "AUDIO_SYNTHDATA",
        _ => null,
    };

    private static string DefaultPackName(IReadOnlyList<string> inputs)
    {
        // Several inputs usually share a parent folder — name the pack after it.
        var dir = Path.GetDirectoryName(inputs[0].TrimEnd('\\', '/'));
        var name = dir != null ? new DirectoryInfo(dir).Name : "";
        return string.IsNullOrWhiteSpace(name) ? "carpack" : name + "_pack";
    }

    private static string Sanitize(string name)
    {
        var s = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9_\-]+", "_").Trim('_');
        return s.Length == 0 ? "sp_vehicle" : s;
    }

    /// <summary>Escape a value for a single-quoted Lua string. Source RPF
    /// filenames and dlc gameName/label text are third-party input; an
    /// unescaped quote/backslash/newline would break the generated
    /// fxmanifest.lua or vehicle_names.lua and make the whole resource fail to
    /// load on the server. Order matters: backslash first.</summary>
    private static string LuaStr(string? s) =>
        (s ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\r", "")
            .Replace("\n", " ");

    private static void WriteOut(string outDir, string rel, byte[] bytes)
    {
        var full = Path.Combine(outDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }
}
