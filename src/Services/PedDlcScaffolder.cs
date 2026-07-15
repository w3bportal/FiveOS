// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CodeWalker.GameFiles;

namespace FiveOS.Services;

/// <summary>
/// Phase 2 of the RPF converter: build a GTA V SINGLEPLAYER add-on
/// <c>dlc.rpf</c> from a FiveM add-on ped resource folder.
///
/// Scope (deliberately narrow, per the verified format spec):
/// • Converts STANDALONE streamed peds — a ped model (<c>&lt;name&gt;.yft</c>/
///   <c>.ydr</c> + <c>.ytd</c>, optional <c>_p</c> props and a per-ped
///   component folder) registered via <c>peds.meta</c>.
/// • DETECTS freemode/EUP component clothing and refuses to convert it
///   (returns <see cref="Classification.FreemodeClothing"/> with reasons) —
///   clothing uses a different SP mechanism and can't be auto-scaffolded.
///
/// Output layout (Rockstar/Albo1125 canonical form):
/// <code>
/// &lt;out&gt;/&lt;NAME&gt;/dlc.rpf
///   ├─ setup2.xml
///   ├─ content.xml
///   ├─ common/data/peds.meta            (+ pedpersonality.meta if shipped)
///   └─ x64/models/cdimages/
///        ├─ componentpeds.rpf           (PEDSTREAM_FILE — model + textures)
///        └─ componentpeds_p.rpf         (RPF_FILE — props, if any)
/// </code>
/// The two manifests, the AUTOGEN changeset, the deviceName and the nameHash
/// are all generated from one set of variables so they can never drift —
/// a mismatch silently disables the pack or crashes the game on launch.
/// </summary>
public sealed class PedDlcScaffolder
{
    public enum Classification { StandalonePed, FreemodeClothing, NoPedFound }

    public sealed record Options(string? DlcNameOverride = null, int Order = 100, bool FiveMModsLayout = false);

    public sealed record Result(
        bool Success,
        Classification Classification,
        string? DlcRpfPath,
        string DlcName,
        IReadOnlyList<string> PedNames,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> ManualReviewReasons,
        IReadOnlyList<RpfPacker.FileResult> Files,
        string? Error);

    // Inside componentpeds.rpf: model carriers vs the per-ped component folder.
    private static readonly string[] ModelExts = { ".yft", ".ydr" };
    private static readonly string[] ModelExtraExts = { ".ytd", ".ydd", ".ymt", ".yld" };
    private static readonly string[] PropExts = { ".yft", ".ytd", ".ydd" };

    // peds.meta path stays in ONE variable shared by content.xml dataFiles,
    // filesToEnable and the physical write — see spec §7.4.
    private const string PedsMetaPath = "common/data/peds.meta";
    private const string PersonalityPath = "common/data/pedpersonality.meta";
    private const string ModelRpfPath = "%PLATFORM%/models/cdimages/componentpeds.rpf";
    private const string PropsRpfPath = "%PLATFORM%/models/cdimages/componentpeds_p.rpf";

    // data_file directives that, on their own, mean freemode/EUP variation
    // content rather than a streamed ped (spec §6 signal 1).
    private static readonly HashSet<string> ClothingDataFileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SHOP_PED_APPAREL_META_FILE", "ALTERNATE_VARIATIONS_FILE",
        "PED_COMPONENT_SETS_FILE", "PED_OVERLAY_FILE",
        "PED_FIRST_PERSON_ALTERNATE_DATA", "PED_FIRST_PERSON_ASSET_DATA",
    };

    private static readonly Regex FreemodeNameRx =
        new(@"^mp_[mf]_freemode_01", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FreemodeFileRx =
        new(@"mp_[mf]_freemode_01", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ShopApparelRootRx =
        new(@"<\s*ShopPedApparel|<\s*pedName\s*>\s*mp_[mf]_freemode_01",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Spec §6 signal 3: bare-named freemode component/prop files (the freemode
    // prefix is optional). Only meaningful when there's no peds.meta — a
    // standalone folder-ped's own <PEDNAME>/ folder uses these same names but
    // always ships a peds.meta, so the !hasPedMeta gate keeps it convertible.
    private static readonly Regex ComponentDrawableRx = new(
        @"^(?:mp_[mf]_freemode_01_)?(head|berd|hair|uppr|lowr|hand|feet|teef|accs|task|decl|jbib)_\d+_(u|r)\.ydd$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ComponentTextureRx = new(
        @"^(?:mp_[mf]_freemode_01_)?(head|berd|hair|uppr|lowr|hand|feet|teef|accs|task|decl|jbib)_diff_\d+_[a-z]_(uni|whi)\.ytd$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PropFileRx = new(
        @"^(?:mp_[mf]_freemode_01_)?p_(head|eyes|ears|mouth|lhand|rhand|lwrist|rwrist|hip|lfoot|rfoot)_\d+(?:_diff_\d+_[a-z])?\.(ydd|ytd)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Build the dlc.rpf under <paramref name="outputRootDir"/> (a
    /// <c>&lt;NAME&gt;/dlc.rpf</c> subfolder is created). Never throws for
    /// content problems — those surface as warnings / manual-review reasons;
    /// only a hard I/O failure sets <see cref="Result.Error"/>.
    /// </summary>
    public Result Scaffold(string inputFolder, string outputRootDir, Options opts, Action<string>? log = null)
    {
        var warnings = new List<string>();
        var reasons = new List<string>();
        var files = new List<RpfPacker.FileResult>();
        var dlcName = SanitizeDlcName(opts.DlcNameOverride
            ?? (string.IsNullOrWhiteSpace(inputFolder) ? "addonped" : new DirectoryInfo(inputFolder.TrimEnd('\\', '/')).Name));

        try
        {
            if (string.IsNullOrWhiteSpace(inputFolder) || !Directory.Exists(inputFolder))
                return Fail(Classification.NoPedFound, dlcName, $"Input folder not found: {inputFolder}");

            var manifest = FxManifestParser.Load(inputFolder);
            var allFiles = Directory.EnumerateFiles(inputFolder, "*", SearchOption.AllDirectories).ToList();
            var allDirs = Directory.EnumerateDirectories(inputFolder, "*", SearchOption.AllDirectories).ToList();

            // ── Classify ────────────────────────────────────────────────
            DetectClothing(manifest, allFiles, inputFolder, reasons);

            var metaPaths = FindPedsMetaFiles(manifest, allFiles, inputFolder);
            var pedNames = ExtractPedNames(metaPaths, warnings);
            var peds = FindStandalonePeds(pedNames, allFiles, allDirs, warnings, metaPaths.Count > 0);

            if (peds.Count == 0)
            {
                // No convertible standalone ped. If clothing signals fired,
                // it's the freemode/EUP case; otherwise we just couldn't find
                // a ped model to scaffold around.
                var cls = reasons.Count > 0 ? Classification.FreemodeClothing : Classification.NoPedFound;
                if (cls == Classification.NoPedFound)
                    reasons.Add("No standalone ped model (.yft/.ydr) matching a peds.meta <Name> was found — nothing to scaffold a dlc.rpf around.");
                log?.Invoke($"Not convertible ({cls}): {string.Join(" | ", reasons)}");
                return new Result(false, cls, null, dlcName, pedNames, warnings, reasons, files, null);
            }

            if (reasons.Count > 0)
                warnings.Add("Freemode/EUP clothing signals were also detected — only the standalone ped(s) were converted; the clothing portion needs manual review: " + string.Join("; ", reasons));

            // ── Generate manifests ──────────────────────────────────────
            var hasProps = peds.Any(p => p.PropSrcs.Count > 0);
            var personalityPath = FindPersonalityFile(manifest, allFiles, inputFolder);
            var hasPersonality = personalityPath != null;

            var changeSet = dlcName + "_AUTOGEN";
            var contentXml = BuildContentXml(dlcName, changeSet, hasPersonality, hasProps);
            var setup2Xml = BuildSetup2Xml(dlcName, changeSet, opts.Order);

            var componentPedNames = peds.Where(p => p.ComponentFolderFiles.Count > 0)
                                        .Select(p => p.PedName).ToList();
            var pedsMetaBytes = BuildPedsMeta(metaPaths, componentPedNames, warnings);

            // ── Write the dlc.rpf ───────────────────────────────────────
            // SP layout:    <out>/<NAME>/dlc.rpf
            // FiveM layout: <out>/mods/update/x64/dlcpacks/<NAME>/dlc.rpf
            //   (drop the `mods` folder into FiveM.app for client-side use).
            var dlcDir = opts.FiveMModsLayout
                ? Path.Combine(Path.GetFullPath(outputRootDir), "mods", "update", "x64", "dlcpacks", dlcName)
                : Path.Combine(Path.GetFullPath(outputRootDir), dlcName);
            Directory.CreateDirectory(dlcDir);
            var dlcRpfPath = Path.Combine(dlcDir, "dlc.rpf");
            if (File.Exists(dlcRpfPath)) File.Delete(dlcRpfPath);

            log?.Invoke($"Creating {dlcName}/dlc.rpf (OPEN)…");
            var utf8 = new UTF8Encoding(false);
            var dlc = RpfFile.CreateNew(dlcDir, "dlc.rpf", RpfEncryption.OPEN);

            void AddRoot(string path, byte[] data)
            {
                RpfPacker.AddFileAtPath(dlc.Root, path.Replace('/', '\\'), data);
                files.Add(new RpfPacker.FileResult("", path, data.Length, RpfPacker.IsResource(data), true, null, null));
                log?.Invoke($"  + {path}");
            }

            AddRoot("setup2.xml", utf8.GetBytes(setup2Xml));
            AddRoot("content.xml", utf8.GetBytes(contentXml));
            AddRoot(PedsMetaPath, pedsMetaBytes);
            if (hasPersonality) AddRoot(PersonalityPath, File.ReadAllBytes(personalityPath!));

            // Nested model archive: componentpeds.rpf under x64/models/cdimages.
            var cdimages = RpfPacker.GetOrCreateDirChain(dlc.Root, "x64\\models\\cdimages");
            var modelRpf = RpfFile.CreateNew(cdimages, "componentpeds.rpf", RpfEncryption.OPEN);
            var modelLeaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ped in peds)
            {
                var modelLeaf = $"{ped.PedName}{Path.GetExtension(ped.ModelSrc)}";
                modelLeaves.Add(modelLeaf);
                AddNested(modelRpf, modelLeaf, ped.ModelSrc, "componentpeds.rpf", files, log);
                foreach (var extra in ped.ExtraSrcs)
                {
                    var leaf = Path.GetFileName(extra);
                    if (!modelLeaves.Add(leaf))
                        warnings.Add($"Two source files map to '{leaf}' in componentpeds.rpf — only the last ('{extra}') is kept.");
                    AddNested(modelRpf, leaf, extra, "componentpeds.rpf", files, log);
                }
                foreach (var (rel, src) in ped.ComponentFolderFiles)
                    AddNested(modelRpf, rel, src, "componentpeds.rpf", files, log);
            }

            // Optional props archive.
            if (hasProps)
            {
                var propRpf = RpfFile.CreateNew(cdimages, "componentpeds_p.rpf", RpfEncryption.OPEN);
                var propLeaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ped in peds)
                    foreach (var prop in ped.PropSrcs)
                    {
                        var leaf = Path.GetFileName(prop);
                        if (!propLeaves.Add(leaf))
                            warnings.Add($"Two source files map to '{leaf}' in componentpeds_p.rpf — only the last ('{prop}') is kept.");
                        AddNested(propRpf, leaf, prop, "componentpeds_p.rpf", files, log);
                    }
            }

            // Install helper next to the pack (outside <NAME> so the folder
            // stays clean to drop into dlcpacks).
            WriteInstallReadme(Path.GetFullPath(outputRootDir), dlcName, peds, warnings, reasons, opts.FiveMModsLayout);

            log?.Invoke($"Done. {dlcName}/dlc.rpf built with {peds.Count} ped(s): {string.Join(", ", peds.Select(p => p.PedName))}.");
            return new Result(true, Classification.StandalonePed, dlcRpfPath, dlcName,
                peds.Select(p => p.PedName).ToList(), warnings, reasons, files, null);
        }
        catch (Exception ex)
        {
            return new Result(false, Classification.NoPedFound, null, dlcName,
                Array.Empty<string>(), warnings, reasons, files, ex.Message);
        }
    }

    private static void AddNested(RpfFile nested, string relInNested, string src, string nestedLabel,
                                  List<RpfPacker.FileResult> files, Action<string>? log)
    {
        var data = File.ReadAllBytes(src);
        RpfPacker.AddFileAtPath(nested.Root, relInNested, data);
        var display = $"x64/models/cdimages/{nestedLabel}/{relInNested.Replace('\\', '/')}";
        files.Add(new RpfPacker.FileResult(src, display, data.Length, RpfPacker.IsResource(data), true, null, null));
        log?.Invoke($"  + {display}");
    }

    private Result Fail(Classification c, string dlcName, string error) =>
        new(false, c, null, dlcName, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<RpfPacker.FileResult>(), error);

    // ── Classification ──────────────────────────────────────────────────

    private static void DetectClothing(FxManifestParser.FxManifest manifest, List<string> allFiles,
                                       string root, List<string> reasons)
    {
        bool hasPedMeta = manifest.DataFiles.Any(d => d.Type.Equals("PED_METADATA_FILE", StringComparison.OrdinalIgnoreCase));

        foreach (var df in manifest.DataFiles)
        {
            if (df.Type.Equals("SHOP_PED_APPAREL_META_FILE", StringComparison.OrdinalIgnoreCase))
                reasons.Add($"manifest declares SHOP_PED_APPAREL_META_FILE ('{df.Path}') — freemode shop clothing.");
            else if (ClothingDataFileTypes.Contains(df.Type) && !hasPedMeta)
                reasons.Add($"manifest declares {df.Type} without PED_METADATA_FILE — ped-variation/clothing content.");

            // PED_METADATA_FILE pointing at the freemode base ped = component
            // faces/heads (e.g. mp_m_freemode_01.ymt), not a standalone ped.
            if (df.Type.Equals("PED_METADATA_FILE", StringComparison.OrdinalIgnoreCase)
                && FreemodeNameRx.IsMatch(Path.GetFileNameWithoutExtension(df.Path)))
                reasons.Add($"PED_METADATA_FILE targets the freemode base ped ('{df.Path}') — freemode component content.");
        }

        // stream filenames targeting the freemode base ped (handles FiveM's
        // 'mp_m_freemode_01^teef_003_u.ydd' caret naming too).
        if (allFiles.Any(f => FreemodeFileRx.IsMatch(Path.GetFileName(f))
                              && (f.EndsWith(".ydd", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase))))
            reasons.Add("stream contains drawables targeting mp_m/f_freemode_01 — freemode component clothing.");

        // Signal 3: bare freemode component/prop naming (no freemode prefix).
        // Gated on no PED_METADATA_FILE so a standalone folder-ped's own
        // <PEDNAME>/ folder (same names, but it ships a peds.meta) still converts.
        if (!hasPedMeta && allFiles.Any(f =>
            {
                var n = Path.GetFileName(f);
                return ComponentDrawableRx.IsMatch(n) || ComponentTextureRx.IsMatch(n) || PropFileRx.IsMatch(n);
            }))
            reasons.Add("stream files use freemode component/prop naming (e.g. uppr_000_u.ydd) with no peds.meta — freemode component clothing.");

        // EUP markers — match the RESOURCE-relative path only, so an ancestor
        // folder on the user's disk named e.g. 'eup_tools' can't false-trigger.
        if (allFiles.Any(f => Regex.IsMatch(Path.GetRelativePath(root, f), @"(^|[\\/])eup($|[\\/_])", RegexOptions.IgnoreCase)))
            reasons.Add("paths contain EUP markers — EUP uniforms use their own SP framework.");

        // apparel meta content (root element / freemode pedName).
        foreach (var meta in allFiles.Where(f => f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                                              || f.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var head = ReadHead(meta, 4096);
                if (ShopApparelRootRx.IsMatch(head))
                    reasons.Add($"'{Path.GetFileName(meta)}' is freemode apparel metadata (<ShopPedApparel>/freemode pedName).");
            }
            catch { /* unreadable meta — ignore for classification */ }
        }

        // de-dup reasons, keep order
        var seen = new HashSet<string>();
        reasons.RemoveAll(r => !seen.Add(r));
    }

    // ── peds.meta discovery / parsing ───────────────────────────────────

    private static List<string> FindPedsMetaFiles(FxManifestParser.FxManifest manifest, List<string> allFiles, string root)
    {
        var paths = manifest.DataFiles
            .Where(d => d.Type.Equals("PED_METADATA_FILE", StringComparison.OrdinalIgnoreCase))
            .Select(d => Resolve(root, d.Path))
            .Where(File.Exists)
            .ToList();
        paths.AddRange(allFiles.Where(f => Path.GetFileName(f).Equals("peds.meta", StringComparison.OrdinalIgnoreCase)));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FindPersonalityFile(FxManifestParser.FxManifest manifest, List<string> allFiles, string root)
    {
        var declared = manifest.DataFiles
            .Where(d => d.Type.Equals("PED_PERSONALITY_FILE", StringComparison.OrdinalIgnoreCase))
            .Select(d => Resolve(root, d.Path)).FirstOrDefault(File.Exists);
        if (declared != null) return declared;
        return allFiles.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).Equals("pedpersonality", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> ExtractPedNames(List<string> metaPaths, List<string> warnings)
    {
        var names = new List<string>();
        foreach (var p in metaPaths)
        {
            try
            {
                var doc = XDocument.Load(p);
                foreach (var item in doc.Descendants("Item").Where(i => i.Parent?.Name.LocalName == "InitDatas"))
                {
                    var n = (string?)item.Element("Name");
                    if (!string.IsNullOrWhiteSpace(n)) names.Add(n.Trim());
                }
            }
            catch (Exception ex) { warnings.Add($"Couldn't parse {Path.GetFileName(p)} for ped names: {ex.Message}"); }
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── ped asset gathering ─────────────────────────────────────────────

    private sealed record PedAsset(
        string PedName,
        string ModelSrc,
        List<string> ExtraSrcs,
        List<(string rel, string src)> ComponentFolderFiles,
        List<string> PropSrcs);

    private static List<PedAsset> FindStandalonePeds(List<string> pedNames, List<string> allFiles,
                                                     List<string> allDirs, List<string> warnings, bool hasPedsMeta)
    {
        var peds = new List<PedAsset>();
        var candidates = pedNames.Where(n => !FreemodeNameRx.IsMatch(n)).ToList();
        // Fall back to model-file discovery ONLY when a peds.meta exists but we
        // couldn't parse any name from it (pedNames empty). Without a peds.meta
        // there's no ped registration possible — so a prop/vehicle pack (which
        // also has .yft/.ydr files) must NOT be mistaken for a ped here.
        if (candidates.Count == 0 && pedNames.Count == 0 && hasPedsMeta)
        {
            candidates = allFiles
                .Where(f => ModelExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Where(n => !n.EndsWith("_p", StringComparison.OrdinalIgnoreCase) && !FreemodeNameRx.IsMatch(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidates.Count > 0)
                warnings.Add("peds.meta present but no <Name> parsed — derived ped(s) from model filenames: " + string.Join(", ", candidates));
        }

        foreach (var ped in candidates)
        {
            var model = allFiles.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).Equals(ped, StringComparison.OrdinalIgnoreCase)
                && ModelExts.Contains(Path.GetExtension(f).ToLowerInvariant()));
            if (model == null)
            {
                warnings.Add($"peds.meta lists '{ped}' but no model file ({string.Join("/", ModelExts)}) was found — skipped.");
                continue;
            }

            var extras = allFiles.Where(f =>
                Path.GetFileNameWithoutExtension(f).Equals(ped, StringComparison.OrdinalIgnoreCase)
                && ModelExtraExts.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();

            var compFolder = allDirs.FirstOrDefault(d =>
                Path.GetFileName(d.TrimEnd('\\', '/')).Equals(ped, StringComparison.OrdinalIgnoreCase));
            var compFiles = new List<(string, string)>();
            if (compFolder != null)
            {
                foreach (var f in Directory.EnumerateFiles(compFolder, "*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    // Keep only RAGE drawables/textures/LODs/meta — match the
                    // packer's RAGE-only policy so OS/VCS cruft (Thumbs.db,
                    // stray .png/.txt) doesn't bloat componentpeds.rpf.
                    if (!ModelExts.Contains(ext) && !ModelExtraExts.Contains(ext)) continue;
                    var rel = ped + "\\" + Path.GetRelativePath(compFolder, f).Replace('/', '\\');
                    compFiles.Add((rel, f));
                }
            }

            var props = allFiles.Where(f =>
                Path.GetFileNameWithoutExtension(f).Equals(ped + "_p", StringComparison.OrdinalIgnoreCase)
                && PropExts.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();

            peds.Add(new PedAsset(ped, model, extras, compFiles, props));
        }
        return peds;
    }

    // ── peds.meta carry / merge / IsStreamedGfx edit (spec §7.8/§7.9) ────

    private static byte[] BuildPedsMeta(List<string> metaPaths, List<string> componentPedNames, List<string> warnings)
    {
        // Single meta, no folder-ped edits → carry the exact bytes (zero risk).
        if (metaPaths.Count == 1 && componentPedNames.Count == 0)
            return File.ReadAllBytes(metaPaths[0]);

        try
        {
            var merged = XDocument.Load(metaPaths[0]);
            var initDatas = merged.Descendants("InitDatas").FirstOrDefault()
                            ?? throw new InvalidDataException("no <InitDatas> in peds.meta");

            for (int i = 1; i < metaPaths.Count; i++)
            {
                var d = XDocument.Load(metaPaths[i]);
                foreach (var item in d.Descendants("Item").Where(x => x.Parent?.Name.LocalName == "InitDatas"))
                    initDatas.Add(new XElement(item));
                var srcTxd = d.Descendants("txdRelationships").FirstOrDefault();
                if (srcTxd != null)
                {
                    var dstTxd = merged.Descendants("txdRelationships").FirstOrDefault();
                    if (dstTxd != null) foreach (var c in srcTxd.Elements()) dstTxd.Add(new XElement(c));
                    else merged.Root!.Add(new XElement(srcTxd));
                }
            }
            if (metaPaths.Count > 1)
                warnings.Add($"Merged {metaPaths.Count} peds.meta files into one.");

            // Flip IsStreamedGfx -> true for folder-based (component) peds.
            foreach (var ped in componentPedNames)
            {
                var item = initDatas.Elements("Item").FirstOrDefault(it =>
                    string.Equals((string?)it.Element("Name"), ped, StringComparison.OrdinalIgnoreCase));
                if (item == null) continue;
                var gfx = item.Element("IsStreamedGfx");
                if (gfx != null) gfx.SetAttributeValue("value", "true");
                else item.Add(new XElement("IsStreamedGfx", new XAttribute("value", "true")));
                warnings.Add($"Set IsStreamedGfx=true for component-folder ped '{ped}'.");
            }

            return Serialize(merged);
        }
        catch (Exception ex)
        {
            warnings.Add($"peds.meta merge/edit FAILED ({ex.Message}); carried only '{Path.GetFileName(metaPaths[0])}' verbatim.");
            if (metaPaths.Count > 1)
                warnings.Add($"WARNING: {metaPaths.Count - 1} additional peds.meta file(s) were DROPPED — their ped registrations are missing even though their models were still packed (orphaned, unspawnable). Merge by hand: {string.Join(", ", metaPaths.Skip(1).Select(Path.GetFileName))}.");
            if (componentPedNames.Count > 0)
                warnings.Add($"WARNING: IsStreamedGfx=true was NOT applied to component-folder ped(s): {string.Join(", ", componentPedNames)}. They may render broken until you set <IsStreamedGfx value=\"true\"/> by hand.");
            return File.ReadAllBytes(metaPaths[0]);
        }
    }

    // ── manifest builders ───────────────────────────────────────────────

    private static string BuildContentXml(string name, string changeSet, bool hasPersonality, bool hasProps)
    {
        // (path, fileType, overlay, persistent) — disabled is always true.
        var items = new List<(string path, string type, bool overlay, bool persistent)>
        {
            ($"dlc_{name}:/{PedsMetaPath}", "PED_METADATA_FILE", false, false),
        };
        if (hasPersonality) items.Add(($"dlc_{name}:/{PersonalityPath}", "PED_PERSONALITY_FILE", false, false));
        items.Add(($"dlc_{name}:/{ModelRpfPath}", "PEDSTREAM_FILE", false, true));
        if (hasProps) items.Add(($"dlc_{name}:/{PropsRpfPath}", "RPF_FILE", false, true));

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<CDataFileMgr__ContentsOfDataFileXml>");
        sb.AppendLine("  <disabledFiles />");
        sb.AppendLine("  <includedXmlFiles />");
        sb.AppendLine("  <includedDataFiles />");
        sb.AppendLine("  <dataFiles>");
        foreach (var it in items)
        {
            sb.AppendLine("    <Item>");
            sb.AppendLine($"      <filename>{it.path}</filename>");
            sb.AppendLine($"      <fileType>{it.type}</fileType>");
            sb.AppendLine($"      <overlay value=\"{Bool(it.overlay)}\" />");
            sb.AppendLine("      <disabled value=\"true\" />");
            sb.AppendLine($"      <persistent value=\"{Bool(it.persistent)}\" />");
            sb.AppendLine("    </Item>");
        }
        sb.AppendLine("  </dataFiles>");
        sb.AppendLine("  <contentChangeSets>");
        sb.AppendLine("    <Item>");
        sb.AppendLine($"      <changeSetName>{changeSet}</changeSetName>");
        sb.AppendLine("      <mapChangeSetData />");
        sb.AppendLine("      <filesToInvalidate />");
        sb.AppendLine("      <filesToDisable />");
        sb.AppendLine("      <filesToEnable>");
        foreach (var it in items)
            sb.AppendLine($"        <Item>{it.path}</Item>");
        sb.AppendLine("      </filesToEnable>");
        sb.AppendLine("      <txdToLoad />");
        sb.AppendLine("      <txdToUnload />");
        sb.AppendLine("      <residentResources />");
        sb.AppendLine("      <unregisterResources />");
        sb.AppendLine("      <requiresLoadingScreen value=\"false\" />");
        sb.AppendLine("    </Item>");
        sb.AppendLine("  </contentChangeSets>");
        sb.AppendLine("  <patchFiles />");
        sb.AppendLine("</CDataFileMgr__ContentsOfDataFileXml>");
        return sb.ToString();
    }

    private static string BuildSetup2Xml(string name, string changeSet, int order)
    {
        var ts = DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<SSetupData>");
        sb.AppendLine($"  <deviceName>dlc_{name}</deviceName>");
        sb.AppendLine("  <datFile>content.xml</datFile>");
        sb.AppendLine($"  <timeStamp>{ts}</timeStamp>");
        sb.AppendLine($"  <nameHash>{name}</nameHash>");
        sb.AppendLine("  <contentChangeSets />");
        sb.AppendLine("  <contentChangeSetGroups>");
        sb.AppendLine("    <Item>");
        sb.AppendLine("      <NameHash>GROUP_STARTUP</NameHash>");
        sb.AppendLine("      <ContentChangeSets>");
        sb.AppendLine($"        <Item>{changeSet}</Item>");
        sb.AppendLine("      </ContentChangeSets>");
        sb.AppendLine("    </Item>");
        sb.AppendLine("  </contentChangeSetGroups>");
        sb.AppendLine("  <startupScript />");
        sb.AppendLine("  <scriptCallstackSize value=\"0\" />");
        sb.AppendLine("  <type>EXTRACONTENT_COMPAT_PACK</type>");
        sb.AppendLine($"  <order value=\"{order}\" />");
        sb.AppendLine("  <minorOrder value=\"0\" />");
        sb.AppendLine("  <isLevelPack value=\"false\" />");
        sb.AppendLine("  <dependencyPackHash />");
        sb.AppendLine("  <requiredVersion />");
        sb.AppendLine("  <subPackCount value=\"0\" />");
        sb.AppendLine("</SSetupData>");
        return sb.ToString();
    }

    private static void WriteInstallReadme(string outRoot, string name, List<PedAsset> peds,
                                           List<string> warnings, List<string> reasons, bool fivemMods)
    {
        var sb = new StringBuilder();
        sb.AppendLine(fivemMods
            ? $"# {name} — FiveM CLIENT-SIDE add-on ped"
            : $"# {name} — singleplayer add-on ped");
        sb.AppendLine();
        sb.AppendLine(fivemMods
            ? "Built by FiveOS (Mods -> RPF -> FiveM client mods folder). Client-side only — the server is NOT affected."
            : "Built by FiveOS (Mods -> RPF -> Singleplayer ped dlc.rpf).");
        sb.AppendLine();
        sb.AppendLine("Peds registered: " + string.Join(", ", peds.Select(p => p.PedName)));
        sb.AppendLine();
        if (fivemMods)
        {
            sb.AppendLine("## Install (GTA V singleplayer — OpenIV mods folder)");
            sb.AppendLine(@"1. Enable OpenIV's mods folder, then copy the 'mods' folder produced beside this");
            sb.AppendLine(@"   readme into your GTA V install root, so the pack ends up at:");
            sb.AppendLine($@"     <GTA V>\mods\update\x64\dlcpacks\{name}\dlc.rpf");
            sb.AppendLine(@"2. Open mods\update\update.rpf\common\data\dlclist.xml in OpenIV and add inside <Paths>:");
            sb.AppendLine($"     <Item>dlcpacks:/{name}/</Item>");
            sb.AppendLine("3. Launch the game and spawn the ped by model name.");
            sb.AppendLine("   NOTE: FiveM's CLIENT loader does NOT read this dlcpacks layout (it needs an");
            sb.AppendLine("   assembly.xml overlay). For FiveM, use the converter's Replace modes instead.");
            sb.AppendLine($"     (ped models: {string.Join(", ", peds.Select(p => p.PedName))})");
        }
        else
        {
            sb.AppendLine("## Install (OpenIV, mods folder)");
            sb.AppendLine($"1. Copy the '{name}' folder into:");
            sb.AppendLine(@"     mods\update\x64\dlcpacks\");
            sb.AppendLine(@"2. Open mods\update\update.rpf\common\data\dlclist.xml and add this line inside <Paths>:");
            sb.AppendLine($"     <Item>dlcpacks:/{name}/</Item>");
            sb.AppendLine("3. Launch the game. Spawn a ped with its model name, e.g. in a trainer or:");
            sb.AppendLine($"     (ped models: {string.Join(", ", peds.Select(p => p.PedName))})");
        }
        if (warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Warnings");
            foreach (var w in warnings) sb.AppendLine(" - " + w);
        }
        if (reasons.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Not converted (needs manual review)");
            foreach (var r in reasons) sb.AppendLine(" - " + r);
        }
        try { File.WriteAllText(Path.Combine(outRoot, $"{name}_INSTALL_README.txt"), sb.ToString()); }
        catch { /* readme is best-effort */ }
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static string Bool(bool b) => b ? "true" : "false";

    private static string Resolve(string root, string rel) =>
        Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)
                                              .Replace('\\', Path.DirectorySeparatorChar)));

    private static string ReadHead(string path, int maxBytes)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[Math.Min(maxBytes, (int)Math.Min(fs.Length, int.MaxValue))];
        var read = fs.Read(buf, 0, buf.Length);
        return Encoding.UTF8.GetString(buf, 0, read);
    }

    private static byte[] Serialize(XDocument doc)
    {
        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = false,
        };
        using (var w = XmlWriter.Create(ms, settings)) doc.Save(w);
        return ms.ToArray();
    }

    /// <summary>DLC pack name: letters/digits/underscore only (RAGE device
    /// name + dlclist entry must agree and stay simple).</summary>
    private static string SanitizeDlcName(string raw)
    {
        var s = new string((raw ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        return string.IsNullOrWhiteSpace(s) ? "addonped" : s;
    }
}
