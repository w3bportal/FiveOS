// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace FiveOS.Services;

/// <summary>Which carcols identifier space a conflict lives in. The three
/// numeric spaces are engine-global: two resources declaring the same modkit
/// / siren / lightSettings id silently break each other in-game. KitName is
/// the string linkage between carcols &lt;kitName&gt; and carvariations
/// &lt;kits&gt;&lt;Item&gt; — duplicated names (the classic
/// "0_default_modkit") make the engine bind every vehicle to whichever kit
/// happened to load last.</summary>
public enum CarcolsIdKind { ModKit, Siren, LightSettings, KitName }

/// <summary>
/// Scans a FiveM resources tree for carcols.meta / carvariations.meta id
/// collisions (modkit / siren / lightSettings ids + duplicate kit names) and
/// auto-remaps the losers to unused high-range ids, updating the matching
/// carvariations cross-references (kit name strings, sirenSettings /
/// lightSettings values) in the same resource. Mirrors the repo's service
/// conventions: plain instance class, no DI, synchronous methods the
/// view-model wraps in Task.Run.
///
/// Scan → preview (the plan rows) → Apply is the same in-place-overwrite +
/// timestamped-Downloads-backup model as the txAdmin optimizer.
/// </summary>
public sealed class CarcolsFixerService
{
    // Remap allocation walks DOWN from the top of each id space so fresh ids
    // land far away from both vanilla GTA ids (all low-range) and typical
    // hand-numbered addon ids. Sirens/lights are hard 8-bit engine spaces;
    // modkits are 16-bit.
    private const long ModKitMax = 65_535, ModKitMin = 1;
    private const long SirenMax = 254, SirenMin = 1;
    private const long LightMax = 255, LightMin = 1;

    // ─── Scan result model ───────────────────────────────────────────────

    /// <summary>One planned fix (or unfixable finding) from a scan.</summary>
    public sealed class PlanItem
    {
        public CarcolsIdKind Kind { get; init; }
        /// <summary>Kit name / siren name when the meta declares one.</summary>
        public string EntryName { get; init; } = "";
        public long OldId { get; init; }
        /// <summary>Remap target; -1 when the item is name-only or unfixable.</summary>
        public long NewId { get; init; } = -1;
        public string? OldName { get; init; }
        public string? NewName { get; init; }
        public string ResourceName { get; init; } = "";
        public string FilePath { get; init; } = "";
        /// <summary>What the id clashes with, for the row tooltip.</summary>
        public string ConflictsWith { get; init; } = "";
        /// <summary>Count of carvariations cross-references this fix rewrites.</summary>
        public int RefUpdates { get; internal set; }
        public bool CanFix { get; init; } = true;
        public string Note { get; init; } = "";

        internal List<Action> Mutations { get; } = new();
        internal List<MetaDoc> Touches { get; } = new();
    }

    public sealed class ScanResult
    {
        public int CarcolsFileCount { get; init; }
        public int CarvariationsFileCount { get; init; }
        public int KitCount { get; init; }
        public int SirenCount { get; init; }
        public int LightCount { get; init; }
        public List<PlanItem> Items { get; init; } = new();
        internal List<MetaDoc> Docs { get; init; } = new();
    }

    public sealed class ApplyResult
    {
        public int EntriesFixed;
        public int FilesChanged;
        public string? BackupRoot;
        public List<string> Errors { get; } = new();
    }

    // ─── Internal per-file model ─────────────────────────────────────────

    internal sealed class MetaDoc
    {
        public required string Path;
        public required string Resource;
        public required XDocument Doc;
        public bool IsCarcols;
        public bool Dirty;
    }

    private sealed class IdEntry
    {
        public required MetaDoc File;
        public required XElement IdElement;   // the <id value="…"/> to rewrite
        public CarcolsIdKind Kind;
        public long Id;
        public string Name = "";              // kitName / siren <name>, "" if absent
        public XElement? NameElement;         // kit's <kitName> for renames
    }

    // ─── Scan ────────────────────────────────────────────────────────────

    public ScanResult Scan(string root, CancellationToken ct, Action<double>? progress = null)
    {
        var docs = LoadMetaDocs(root, ct, progress);

        var kits = new List<IdEntry>();
        var sirens = new List<IdEntry>();
        var lights = new List<IdEntry>();

        foreach (var doc in docs.Where(d => d.IsCarcols))
        {
            ct.ThrowIfCancellationRequested();
            var rootEl = doc.Doc.Root!;
            CollectIdEntries(doc, rootEl, "Kits", CarcolsIdKind.ModKit, "kitName", kits);
            CollectIdEntries(doc, rootEl, "Sirens", CarcolsIdKind.Siren, "name", sirens);
            CollectIdEntries(doc, rootEl, "Lights", CarcolsIdKind.LightSettings, null, lights);
        }

        var items = new List<PlanItem>();
        var usedKitIds = new HashSet<long>(kits.Select(k => k.Id));
        var usedSirenIds = new HashSet<long>(sirens.Select(s => s.Id));
        var usedLightIds = new HashSet<long>(lights.Select(l => l.Id));
        var usedKitNames = new HashSet<string>(
            kits.Select(k => k.Name).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);

        var renamedKits = new HashSet<IdEntry>();
        PlanIdConflicts(kits, docs, usedKitIds, usedKitNames, ModKitMax, ModKitMin, items, renamedKits);
        PlanIdConflicts(sirens, docs, usedSirenIds, null, SirenMax, SirenMin, items, null);
        PlanIdConflicts(lights, docs, usedLightIds, null, LightMax, LightMin, items, null);
        PlanKitNameDuplicates(kits, docs, usedKitNames, items, renamedKits);

        return new ScanResult
        {
            CarcolsFileCount = docs.Count(d => d.IsCarcols),
            CarvariationsFileCount = docs.Count(d => !d.IsCarcols),
            KitCount = kits.Count,
            SirenCount = sirens.Count,
            LightCount = lights.Count,
            Items = items,
            Docs = docs,
        };
    }

    /// <summary>Find every carcols / carvariations meta under <paramref name="root"/>
    /// by sniffing the root element name — data_file entries can name the
    /// files anything, so filename matching alone misses real ones.</summary>
    private static List<MetaDoc> LoadMetaDocs(string root, CancellationToken ct, Action<double>? progress)
    {
        var docs = new List<MetaDoc>();
        var files = Directory
            .EnumerateFiles(root, "*.meta", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{System.IO.Path.DirectorySeparatorChar}cache{System.IO.Path.DirectorySeparatorChar}",
                                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke((i + 1) / (double)files.Count);
            var path = files[i];
            bool? isCarcols = SniffRootElement(path);
            if (isCarcols == null) continue;

            try
            {
                var doc = XDocument.Load(path);
                docs.Add(new MetaDoc
                {
                    Path = path,
                    Resource = ResourceNameFor(path, root),
                    Doc = doc,
                    IsCarcols = isCarcols.Value,
                });
            }
            catch (Exception ex)
            {
                FosLogger.Warn("carcols", $"unparseable meta skipped: {path}", ex);
            }
        }
        return docs;
    }

    /// <summary>true = carcols, false = carvariations, null = neither.
    /// Reads only the first ~2 KB so sweeping thousands of metas stays fast.</summary>
    private static bool? SniffRootElement(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> buf = stackalloc byte[2048];
            int n = fs.Read(buf);
            var head = Encoding.UTF8.GetString(buf[..n]);
            if (head.Contains("<CVehicleModelInfoVarGlobal", StringComparison.OrdinalIgnoreCase)) return true;
            if (head.Contains("<CVehicleModelInfoVariation", StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch { /* unreadable → not ours */ }
        return null;
    }

    /// <summary>Resource = nearest ancestor folder holding an fxmanifest.lua /
    /// __resource.lua, else the file's parent folder name.</summary>
    private static string ResourceNameFor(string filePath, string scanRoot)
    {
        var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(filePath)!);
        var rootFull = System.IO.Path.GetFullPath(scanRoot);
        while (dir != null && dir.FullName.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "fxmanifest.lua")) ||
                File.Exists(System.IO.Path.Combine(dir.FullName, "__resource.lua")))
                return dir.Name;
            dir = dir.Parent;
        }
        return new DirectoryInfo(System.IO.Path.GetDirectoryName(filePath)!).Name;
    }

    private static void CollectIdEntries(
        MetaDoc doc, XElement rootEl, string sectionName, CarcolsIdKind kind,
        string? nameElementName, List<IdEntry> into)
    {
        // Sections sit directly under CVehicleModelInfoVarGlobal; match by
        // LocalName so the (namespace-free) metas parse the same either way.
        foreach (var section in rootEl.Elements().Where(e =>
                     string.Equals(e.Name.LocalName, sectionName, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var item in section.Elements().Where(e =>
                         string.Equals(e.Name.LocalName, "Item", StringComparison.OrdinalIgnoreCase)))
            {
                var idEl = item.Elements().FirstOrDefault(e =>
                    string.Equals(e.Name.LocalName, "id", StringComparison.OrdinalIgnoreCase));
                if (idEl == null || !TryReadValue(idEl, out long id)) continue;

                XElement? nameEl = null;
                string name = "";
                if (nameElementName != null)
                {
                    nameEl = item.Elements().FirstOrDefault(e =>
                        string.Equals(e.Name.LocalName, nameElementName, StringComparison.OrdinalIgnoreCase));
                    name = nameEl?.Value.Trim() ?? "";
                }

                into.Add(new IdEntry
                {
                    File = doc, IdElement = idEl, Kind = kind, Id = id,
                    Name = name, NameElement = kind == CarcolsIdKind.ModKit ? nameEl : null,
                });
            }
        }
    }

    /// <summary>Reads &lt;id value="123"/&gt; (canonical) or &lt;id&gt;123&lt;/id&gt;
    /// (seen in hand-edited packs); accepts 0x-prefixed hex.</summary>
    private static bool TryReadValue(XElement el, out long value)
    {
        var raw = (el.Attribute("value")?.Value ?? el.Value).Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        return long.TryParse(raw, out value);
    }

    private static void WriteValue(XElement el, long value)
    {
        if (el.Attribute("value") != null) el.SetAttributeValue("value", value);
        else el.Value = value.ToString();
    }

    // ─── Conflict planning ───────────────────────────────────────────────

    private static void PlanIdConflicts(
        List<IdEntry> entries, List<MetaDoc> docs,
        HashSet<long> usedIds, HashSet<string>? usedKitNames,
        long idMax, long idMin, List<PlanItem> items, HashSet<IdEntry>? renamedKits)
    {
        foreach (var group in entries.GroupBy(e => e.Id).Where(g => g.Count() > 1))
        {
            // Deterministic keeper: first by resource/path order (files were
            // enumerated sorted). Everyone after it gets remapped.
            var ordered = group.ToList();
            var keeper = ordered[0];

            foreach (var loser in ordered.Skip(1))
            {
                long newId = AllocateId(usedIds, idMax, idMin);
                var item = new PlanItem
                {
                    Kind = loser.Kind,
                    EntryName = loser.Name.Length > 0 ? loser.Name : $"(unnamed {loser.Kind})",
                    OldId = loser.Id,
                    NewId = newId,
                    ResourceName = loser.File.Resource,
                    FilePath = loser.File.Path,
                    ConflictsWith = $"{keeper.File.Resource} ({(keeper.Name.Length > 0 ? keeper.Name : System.IO.Path.GetFileName(keeper.File.Path))})",
                    CanFix = newId > 0,
                    Note = newId > 0 ? "" : "no free ids left in range",
                };
                if (newId <= 0) { items.Add(item); continue; }

                var idEl = loser.IdElement;
                item.Mutations.Add(() => WriteValue(idEl, newId));
                item.Touches.Add(loser.File);

                // Modkit: keep the "id_name" convention in sync and rewrite
                // the carvariations <kits><Item> linkage to the renamed kit.
                // When the keeper sits in the SAME resource under the SAME
                // name, the resource's refs are inherently ambiguous — leave
                // them bound to the keeper rather than stealing them all.
                if (loser.Kind == CarcolsIdKind.ModKit && loser.NameElement != null && loser.Name.Length > 0)
                {
                    string newName = RenamedKit(loser.Name, newId, usedKitNames!);
                    var nameEl = loser.NameElement;
                    string oldName = loser.Name;
                    item.Mutations.Add(() => nameEl.Value = newName);
                    renamedKits?.Add(loser);
                    bool keeperOwnsRefs = keeper.File.Resource == loser.File.Resource
                        && string.Equals(keeper.Name, oldName, StringComparison.OrdinalIgnoreCase);
                    if (!keeperOwnsRefs)
                        AddKitNameRefUpdates(item, docs, loser.File.Resource, oldName, newName);
                }

                // Siren / lightSettings: rewrite same-resource carvariations
                // references — unless the keeper lives in the SAME resource,
                // in which case existing refs already point at a surviving
                // definition and must stay.
                if (loser.Kind is CarcolsIdKind.Siren or CarcolsIdKind.LightSettings
                    && keeper.File.Resource != loser.File.Resource)
                {
                    string refName = loser.Kind == CarcolsIdKind.Siren ? "sirenSettings" : "lightSettings";
                    AddNumericRefUpdates(item, docs, loser.File.Resource, refName, loser.Id, newId);
                }

                items.Add(item);
            }
        }
    }

    /// <summary>Duplicate kitName strings whose ids differ (same-id duplicates
    /// were already renamed by the modkit id pass above).</summary>
    private static void PlanKitNameDuplicates(
        List<IdEntry> kits, List<MetaDoc> docs, HashSet<string> usedKitNames,
        List<PlanItem> items, HashSet<IdEntry> renamedKits)
    {
        // Kits already renamed by the modkit-id pass are resolved — running
        // them through the name pass too would stack a second rename.
        var named = kits.Where(k => k.Name.Length > 0 && k.NameElement != null && !renamedKits.Contains(k)).ToList();
        foreach (var group in named
                     .GroupBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Select(k => k.Id).Distinct().Count() > 1))
        {
            var ordered = group.ToList();
            var keeper = ordered[0];
            foreach (var loser in ordered.Skip(1))
            {
                string newName = RenamedKit(loser.Name, loser.Id, usedKitNames);
                var item = new PlanItem
                {
                    Kind = CarcolsIdKind.KitName,
                    EntryName = loser.Name,
                    OldId = loser.Id,
                    NewId = loser.Id,          // id unchanged — name-only fix
                    OldName = loser.Name,
                    NewName = newName,
                    ResourceName = loser.File.Resource,
                    FilePath = loser.File.Path,
                    ConflictsWith = $"{keeper.File.Resource} (id {keeper.Id})",
                };
                var nameEl = loser.NameElement!;
                item.Mutations.Add(() => nameEl.Value = newName);
                item.Touches.Add(loser.File);
                // Same-resource keeper with the same name → refs are ambiguous;
                // leave them bound to the keeper (see PlanIdConflicts note).
                if (keeper.File.Resource != loser.File.Resource)
                    AddKitNameRefUpdates(item, docs, loser.File.Resource, loser.Name, newName);
                items.Add(item);
            }
        }
    }

    private static void AddKitNameRefUpdates(
        PlanItem item, List<MetaDoc> docs, string resource, string oldName, string newName)
    {
        foreach (var vDoc in docs.Where(d => !d.IsCarcols && d.Resource == resource))
        {
            var refs = vDoc.Doc.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "kits", StringComparison.OrdinalIgnoreCase))
                .SelectMany(k => k.Elements())
                .Where(i => string.Equals(i.Value.Trim(), oldName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (refs.Count == 0) continue;
            item.RefUpdates += refs.Count;
            item.Touches.Add(vDoc);
            item.Mutations.Add(() => { foreach (var r in refs) r.Value = newName; });
        }
    }

    private static void AddNumericRefUpdates(
        PlanItem item, List<MetaDoc> docs, string resource, string elementName, long oldId, long newId)
    {
        foreach (var vDoc in docs.Where(d => !d.IsCarcols && d.Resource == resource))
        {
            var refs = vDoc.Doc.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, elementName, StringComparison.OrdinalIgnoreCase)
                            && TryReadValue(e, out long v) && v == oldId)
                .ToList();
            if (refs.Count == 0) continue;
            item.RefUpdates += refs.Count;
            item.Touches.Add(vDoc);
            item.Mutations.Add(() => { foreach (var r in refs) WriteValue(r, newId); });
        }
    }

    private static long AllocateId(HashSet<long> used, long max, long min)
    {
        for (long id = max; id >= min; id--)
            if (used.Add(id)) return id;
        return -1;
    }

    /// <summary>"123_police_modkit" + id 65530 → "65530_police_modkit";
    /// non-prefixed names get the id prepended. Uniquified against every
    /// kit name seen this scan.</summary>
    private static string RenamedKit(string oldName, long newId, HashSet<string> usedNames)
    {
        var m = Regex.Match(oldName, @"^\d+(_.+)$");
        string candidate = m.Success ? $"{newId}{m.Groups[1].Value}" : $"{newId}_{oldName}";
        string result = candidate;
        int suffix = 2;
        while (!usedNames.Add(result)) result = $"{candidate}_{suffix++}";
        return result;
    }

    // ─── Apply ───────────────────────────────────────────────────────────

    public ApplyResult Apply(ScanResult scan, bool backup, CancellationToken ct,
                             Action<PlanItem, bool, string?>? perItem = null)
    {
        var result = new ApplyResult();
        string? backupRoot = backup
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "FiveOS_Carcols_Backup_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"))
            : null;

        // Back up every file any fixable item touches, once, BEFORE mutating.
        var toTouch = scan.Items.Where(i => i.CanFix).SelectMany(i => i.Touches).Distinct().ToList();
        if (backupRoot != null)
        {
            foreach (var doc in toTouch)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var dst = System.IO.Path.Combine(backupRoot, doc.Resource, System.IO.Path.GetFileName(doc.Path));
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
                    // Same-named metas in one resource (subfolders) — suffix
                    // rather than silently overwriting the first backup.
                    int n = 2;
                    while (File.Exists(dst))
                        dst = System.IO.Path.Combine(backupRoot, doc.Resource,
                            $"{System.IO.Path.GetFileNameWithoutExtension(doc.Path)}_{n++}{System.IO.Path.GetExtension(doc.Path)}");
                    File.Copy(doc.Path, dst);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"backup failed: {doc.Path} — {ex.Message}");
                    FosLogger.Warn("carcols", $"backup failed for {doc.Path}", ex);
                }
            }
        }

        // Mutate the in-memory documents.
        foreach (var item in scan.Items.Where(i => i.CanFix))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var m in item.Mutations) m();
                foreach (var d in item.Touches) d.Dirty = true;
                result.EntriesFixed++;
                perItem?.Invoke(item, true, null);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{item.EntryName}: {ex.Message}");
                perItem?.Invoke(item, false, ex.Message);
            }
        }

        // Persist every dirty doc with the repo's canonical writer settings.
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = false,
        };
        foreach (var doc in scan.Docs.Where(d => d.Dirty))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var w = XmlWriter.Create(doc.Path, settings);
                doc.Doc.Save(w);
                doc.Dirty = false;
                result.FilesChanged++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"write failed: {doc.Path} — {ex.Message}");
                FosLogger.Warn("carcols", $"write failed for {doc.Path}", ex);
            }
        }

        result.BackupRoot = backupRoot != null && Directory.Exists(backupRoot) ? backupRoot : null;
        return result;
    }
}
