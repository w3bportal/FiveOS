// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FiveOS.Services;

/// <summary>
/// Accumulator for the "Prop Pack" workflow: instead of every conversion
/// producing its own .zip / server drop, the user can flip into Pack mode
/// and have each subsequent conversion append its stream/* outputs into
/// a single staging directory. When the user clicks Finalize, the pack
/// is rolled up into one self-contained FiveM resource by
/// <see cref="PropPackBuilder"/>.
///
/// State lives in-memory (the staging directory is a transient
/// %TEMP%\FiveOS\pack folder) — quitting the app drops the pack. This
/// matches the "session, not save file" intent of the feature: a pack is
/// something you build up in a sitting and finalise at the end.
/// </summary>
public sealed partial class PropPackSession : ObservableObject
{
    public static PropPackSession Current { get; } = new();

    private PropPackSession()
    {
        StagingDir = Path.Combine(Path.GetTempPath(), "FiveOS", "pack");
        _packName = "props_pack";
        _defaultCategory = HousingCategory.Decorations;
        _defaultPrice = 250;
        _defaultType = FurnitureType.None;

        Entries.CollectionChanged += (_, _) => RebuildTree();
        ConvertQueue.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasConvertQueue));
            OnPropertyChanged(nameof(CanRunConvertQueue));
            OnPropertyChanged(nameof(PendingQueueCount));
            RebuildTree();
        };
        RebuildTree();
    }

    partial void OnPackNameChanged(string value) => RebuildTree();

    /// <summary>Root staging directory. Each entry's bytes live under
    /// <c>StagingDir\&lt;asset_name&gt;\stream\*</c>. Built lazily on first
    /// <see cref="AddFromResourceDir"/>; cleared by <see cref="Clear"/>.</summary>
    public string StagingDir { get; }

    /// <summary>Resource name used when the pack is finalised — drives
    /// the output folder name, the fxmanifest description, and the .zip
    /// filename. Sanitised at finalize time, not here.</summary>
    [ObservableProperty]
    private string _packName;

    /// <summary>Default housing category applied to new entries. The
    /// pack panel's category dropdown writes into this; <see cref="AddFromResourceDir"/>
    /// stamps it onto each entry so finalize-time catalog emission knows
    /// where each prop lives in the housing-script UI.</summary>
    [ObservableProperty]
    private string _defaultCategory;

    /// <summary>Default in-game price applied to new entries. Per-entry
    /// price overrides this; this just seeds the value so users don't
    /// have to set the same number on every prop in a category-uniform
    /// pack.</summary>
    [ObservableProperty]
    private int _defaultPrice;

    /// <summary>Default furniture type (Storage / Wardrobe / None). Some
    /// housing scripts treat storage and wardrobe props specially —
    /// ps-housing wires them to ox_inventory stashes / clothing storage
    /// when <c>type</c> is set. Default is None (decorative).</summary>
    [ObservableProperty]
    private FurnitureType _defaultType;

    /// <summary>Live list of entries — bound directly to the pack panel
    /// so adds/removes update the UI without manual refresh calls.</summary>
    public ObservableCollection<PropPackEntry> Entries { get; } = new();

    /// <summary>Source meshes waiting to convert into <see cref="Entries"/>
    /// one-by-one (C4D-style queue under the pack tree).</summary>
    public ObservableCollection<PropPackQueueItem> ConvertQueue { get; } = new();

    /// <summary>Outliner roots, Photoshop style: one Pack node per group
    /// (props + pending queue rows directly inside), then loose Prop rows,
    /// then loose queue rows. No stream/queue wrapper levels.</summary>
    public ObservableCollection<PropPackTreeNode> TreeRoots { get; } = new();

    /// <summary>Registered group names, in creation order. Groups can be
    /// empty (freshly created, waiting for a drag-in), so the registry is
    /// authoritative — entry GroupNames are unioned in defensively at
    /// rebuild time.</summary>
    public ObservableCollection<string> Groups { get; } = new();

    /// <summary>Expansion memory across tree rebuilds, keyed by group name.</summary>
    private readonly Dictionary<string, bool> _groupExpanded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Photoshop-style group eye: a hidden group is skipped at
    /// export wholesale (staged members AND pending rows), while each
    /// member keeps its own eye state for when the group returns.</summary>
    private readonly HashSet<string> _hiddenGroups = new(StringComparer.OrdinalIgnoreCase);

    public bool IsGroupHidden(string group) =>
        !string.IsNullOrEmpty(group) && _hiddenGroups.Contains(group);

    public void SetGroupHidden(string group, bool hidden)
    {
        if (string.IsNullOrWhiteSpace(group)) return;
        if (hidden) _hiddenGroups.Add(group);
        else _hiddenGroups.Remove(group);
        RebuildTree();
    }

    /// <summary>True while MainWindow is draining <see cref="ConvertQueue"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunConvertQueue))]
    [NotifyPropertyChangedFor(nameof(HasConvertQueue))]
    private bool _isQueueRunning;

    public bool HasConvertQueue => ConvertQueue.Count > 0;
    public bool CanRunConvertQueue => !IsQueueRunning && ConvertQueue.Any(q => q.IsPending);
    public int PendingQueueCount => ConvertQueue.Count(q => q.IsPending);

    /// <summary>Total bytes across every file currently staged. Drives the
    /// pack-panel header ("3 props · 4.7 MB"). Recomputed on every mutation
    /// rather than tracked incrementally — cheap and avoids drift.</summary>
    public long TotalBytes => Entries.Sum(e => e.TotalBytes);

    /// <summary>Human-readable pack size for the panel header.</summary>
    public string TotalSizeDisplay
    {
        get
        {
            double b = TotalBytes;
            if (b <= 0) return "";
            if (b < 1024) return $"{b:0} B";
            if (b < 1024 * 1024) return $"{b / 1024:0.#} KB";
            return $"{b / (1024 * 1024):0.##} MB";
        }
    }

    /// <summary>Convenience for UI bindings — number of entries currently
    /// in the pack. Re-raises when Entries changes; the panel binds to it
    /// for the "N props" header.</summary>
    public int Count => Entries.Count;

    /// <summary>True iff at least one prop has been added — gates the
    /// Finalize button and the empty-state placeholder in the pack panel.</summary>
    public bool HasEntries => Entries.Count > 0;

    /// <summary>Header subtitle: "3 props · 12.4 MB" or queue hint.</summary>
    public string StatusSummary
    {
        get
        {
            var parts = new List<string>();
            if (Entries.Count > 0)
            {
                var size = TotalSizeDisplay;
                var props = Entries.Count == 1 ? "1 prop" : $"{Entries.Count} props";
                parts.Add(string.IsNullOrEmpty(size) ? props : $"{props} · {size}");
            }
            var pending = PendingQueueCount;
            if (pending > 0)
                parts.Add($"{pending} queued");
            return parts.Count == 0 ? "" : string.Join(" · ", parts);
        }
    }

    /// <summary>Enqueue source meshes for one-by-one convert. Converted
    /// props land in <paramref name="groupName"/> (null = loose layer).</summary>
    public int EnqueueConvertPaths(IEnumerable<string> paths, string? groupName = null)
    {
        var group = string.IsNullOrWhiteSpace(groupName) ? null : groupName!.Trim();
        if (group is not null && !Groups.Contains(group, StringComparer.OrdinalIgnoreCase))
            Groups.Add(group);

        int added = 0;
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw) || !File.Exists(raw)) continue;
            if (!IsSupportedMesh(raw)) continue;
            if (ConvertQueue.Any(q => string.Equals(q.SourcePath, raw, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (ConvertQueue.Count >= 30) break;

            var stem = Path.GetFileNameWithoutExtension(raw) ?? "prop";
            var asset = UniqueQueueAssetName(Sanitize(stem));
            ConvertQueue.Add(new PropPackQueueItem(raw, asset) { GroupName = group });
            added++;
        }
        NotifyAggregateChanged();
        return added;
    }

    /// <summary>Enqueue the LOADED model with a frozen convert request
    /// (drag-drop onto a group): the row appears in the outliner
    /// immediately and converts in the background. Re-dropping the same
    /// source while still pending just retargets its group.</summary>
    public PropPackQueueItem EnqueueConvertSnapshot(
        string sourcePath, string baseName, string? groupName,
        EngineRunner.ConvertRequest snapshot)
    {
        var group = string.IsNullOrWhiteSpace(groupName) ? null : groupName!.Trim();
        if (group is not null && !Groups.Contains(group, StringComparer.OrdinalIgnoreCase))
            Groups.Add(group);

        var pending = ConvertQueue.FirstOrDefault(q => q.IsPending &&
            string.Equals(q.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
        if (pending is not null)
        {
            pending.GroupName = group;
            pending.RequestSnapshot = snapshot;
            RebuildTree();
            NotifyAggregateChanged();
            return pending;
        }

        var asset = UniqueQueueAssetName(Sanitize(baseName));
        var item = new PropPackQueueItem(sourcePath, asset)
        {
            GroupName = group,
            RequestSnapshot = snapshot,
        };
        ConvertQueue.Add(item);
        NotifyAggregateChanged();
        return item;
    }

    public void RemoveQueueItem(PropPackQueueItem item)
    {
        if (item is null || IsQueueRunning) return;
        ConvertQueue.Remove(item);
        NotifyAggregateChanged();
    }

    public void ClearConvertQueue()
    {
        if (IsQueueRunning) return;
        ConvertQueue.Clear();
        NotifyAggregateChanged();
    }

    /// <summary>Refresh aggregate UI flags after a queue run finishes.</summary>
    public void NotifyQueueFinished()
    {
        NotifyAggregateChanged();
        RebuildTree();
    }

    public void RebuildTree()
    {
        // Remember expansion before the rebuild throws the nodes away.
        foreach (var root in TreeRoots)
        {
            if (root.IsPack && root.GroupKey is { } key)
                _groupExpanded[key] = root.IsExpanded;
        }
        TreeRoots.Clear();

        // Registry first (keeps creation order, includes empty groups),
        // then any group names that only exist on entries/queue rows.
        var groupNames = new List<string>(Groups);
        foreach (var g in Entries.Select(e => e.GroupName)
                     .Concat(ConvertQueue.Select(q => q.GroupName)))
        {
            if (!string.IsNullOrWhiteSpace(g) &&
                !groupNames.Contains(g!, StringComparer.OrdinalIgnoreCase))
                groupNames.Add(g!);
        }

        foreach (var group in groupNames)
        {
            var members = Entries.Where(e =>
                string.Equals(e.GroupName, group, StringComparison.OrdinalIgnoreCase)).ToList();
            var pending = ConvertQueue.Where(q =>
                string.Equals(q.GroupName, group, StringComparison.OrdinalIgnoreCase)).ToList();

            bool groupHidden = _hiddenGroups.Contains(group);
            var node = new PropPackTreeNode
            {
                Kind = PropPackTreeNode.NodeKind.Pack,
                Name = group,
                GroupKey = group,
                Detail = GroupDetail(members, pending),
                IsExpanded = !_groupExpanded.TryGetValue(group, out var exp) || exp,
                IsEyeOn = !groupHidden,
            };
            foreach (var entry in members)
                node.Children.Add(MakePropNode(entry));
            foreach (var item in pending)
                node.Children.Add(MakeQueueNode(item));
            // Hidden group dims every child row; member IsIncluded states
            // are untouched underneath and return when the group is shown.
            if (groupHidden)
                foreach (var c in node.Children)
                    c.IsEyeOn = false;
            TreeRoots.Add(node);
        }

        // Loose layers — export separately, one resource each.
        foreach (var entry in Entries.Where(e => string.IsNullOrWhiteSpace(e.GroupName)))
            TreeRoots.Add(MakePropNode(entry));
        foreach (var item in ConvertQueue.Where(q => string.IsNullOrWhiteSpace(q.GroupName)))
            TreeRoots.Add(MakeQueueNode(item));

        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static PropPackTreeNode MakePropNode(PropPackEntry entry) => new()
    {
        Kind = PropPackTreeNode.NodeKind.Prop,
        Name = entry.SlotName,
        Detail = entry.SizeDisplay,
        Entry = entry,
        IsExpanded = false,
        IsEyeOn = entry.IsIncluded,
    };

    private static PropPackTreeNode MakeQueueNode(PropPackQueueItem item) => new()
    {
        Kind = PropPackTreeNode.NodeKind.QueueItem,
        Name = item.AssetName,
        QueueItem = item,
        IsExpanded = false,
        IsEyeOn = item.IsIncluded,
    };

    private static string GroupDetail(List<PropPackEntry> members, List<PropPackQueueItem> pending)
    {
        var parts = new List<string>();
        if (members.Count > 0)
        {
            double b = members.Sum(m => m.TotalBytes);
            var size = b <= 0 ? "" :
                b < 1024 * 1024 ? $"{b / 1024:0.#} KB" : $"{b / (1024 * 1024):0.##} MB";
            var props = members.Count == 1 ? "1 prop" : $"{members.Count} props";
            parts.Add(string.IsNullOrEmpty(size) ? props : $"{props} · {size}");
        }
        if (pending.Count > 0)
            parts.Add($"{pending.Count} queued");
        return parts.Count == 0 ? "empty" : string.Join(" · ", parts);
    }

    /// <summary>Create + register a new empty group with a unique name.
    /// Returns the name so callers can start an inline rename on it.</summary>
    public string AddGroup(string? baseName = null)
    {
        var stem = Sanitize(string.IsNullOrWhiteSpace(baseName) ? "pack" : baseName!);
        var name = stem;
        int n = 2;
        while (Groups.Contains(name, StringComparer.OrdinalIgnoreCase) ||
               Entries.Any(e => string.Equals(e.GroupName, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{stem}_{n}"; n++;
        }
        Groups.Add(name);
        RebuildTree();
        return name;
    }

    /// <summary>Rename a group everywhere: registry, member entries, queued
    /// rows, expansion memory. Returns the sanitized name actually applied,
    /// or null when the target name is empty/taken.</summary>
    public string? RenameGroup(string oldName, string newName)
    {
        var clean = Sanitize(newName);
        if (string.IsNullOrEmpty(clean) ||
            string.Equals(clean, oldName, StringComparison.OrdinalIgnoreCase))
            return null;
        if (Groups.Contains(clean, StringComparer.OrdinalIgnoreCase))
            return null;

        var idx = Groups.ToList().FindIndex(g => string.Equals(g, oldName, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) Groups[idx] = clean;
        foreach (var e in Entries.Where(e => string.Equals(e.GroupName, oldName, StringComparison.OrdinalIgnoreCase)))
            e.GroupName = clean;
        foreach (var q in ConvertQueue.Where(q => string.Equals(q.GroupName, oldName, StringComparison.OrdinalIgnoreCase)))
            q.GroupName = clean;
        if (_groupExpanded.Remove(oldName, out var exp))
            _groupExpanded[clean] = exp;
        if (_hiddenGroups.Remove(oldName))
            _hiddenGroups.Add(clean);
        RebuildTree();
        return clean;
    }

    /// <summary>Dissolve a group. Members become loose layers (they export
    /// separately again); nothing is deleted from disk.</summary>
    public void UngroupGroup(string name)
    {
        foreach (var e in Entries.Where(e => string.Equals(e.GroupName, name, StringComparison.OrdinalIgnoreCase)))
            e.GroupName = null;
        foreach (var q in ConvertQueue.Where(q => string.Equals(q.GroupName, name, StringComparison.OrdinalIgnoreCase)))
            q.GroupName = null;
        for (int i = Groups.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Groups[i], name, StringComparison.OrdinalIgnoreCase))
                Groups.RemoveAt(i);
        }
        _groupExpanded.Remove(name);
        _hiddenGroups.Remove(name);
        RebuildTree();
    }

    /// <summary>Delete a group AND its member entries (staged bytes gone).</summary>
    public void RemoveGroupWithEntries(string name)
    {
        foreach (var e in Entries.Where(e =>
                     string.Equals(e.GroupName, name, StringComparison.OrdinalIgnoreCase)).ToList())
            Remove(e);
        UngroupGroup(name);
    }

    /// <summary>Move a layer into a group (null = out to the top level).
    /// The target group is auto-registered so drags can create it.</summary>
    public void MoveEntryToGroup(PropPackEntry entry, string? group)
    {
        if (entry is null) return;
        var clean = string.IsNullOrWhiteSpace(group) ? null : group!.Trim();
        if (string.Equals(entry.GroupName, clean, StringComparison.OrdinalIgnoreCase)) return;
        if (clean is not null && !Groups.Contains(clean, StringComparer.OrdinalIgnoreCase))
            Groups.Add(clean);
        entry.GroupName = clean;
        RebuildTree();
    }

    /// <summary>Rename a staged layer. Updates the outliner row, the
    /// housing label, and — because finalize derives every stream stem
    /// from <see cref="PropPackEntry.SlotName"/> and rewrites the YDR's
    /// internal name to match — the exported resource/archetype name.
    /// Returns false when the name is empty or already taken.</summary>
    public bool RenameEntry(PropPackEntry entry, string newName)
    {
        if (entry is null) return false;
        var clean = Sanitize(newName);
        if (string.IsNullOrEmpty(clean)) return false;
        if (string.Equals(clean, entry.SlotName, StringComparison.OrdinalIgnoreCase)) return false;
        if (Entries.Any(e => !ReferenceEquals(e, entry) &&
                (string.Equals(e.SlotName, clean, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(e.AssetName, clean, StringComparison.OrdinalIgnoreCase))))
            return false;

        entry.SlotName = clean;
        entry.AssetName = clean;
        entry.Label = Humanize(clean);
        RebuildTree();
        return true;
    }

    /// <summary>Flip a layer's eye and refresh the tree so group aggregate
    /// eyes + row dimming follow.</summary>
    public void SetEntryIncluded(PropPackEntry entry, bool included)
    {
        if (entry is null || entry.IsIncluded == included) return;
        entry.IsIncluded = included;
        RebuildTree();
    }

    /// <summary>Footer count — staged layers + rows still converting.</summary>
    public string ItemCountDisplay
    {
        get
        {
            int n = Entries.Count + ConvertQueue.Count;
            return n == 1 ? "1 item" : $"{n} items";
        }
    }

    /// <summary>True when the outliner has any pack content to show
    /// (groups — even empty ones — staged layers, or queue rows).</summary>
    public bool HasOutlinerContent => Entries.Count > 0 || Groups.Count > 0 || ConvertQueue.Count > 0;

    /// <summary>Fires after <see cref="RebuildTree"/> so the unified
    /// Layers outliner can refresh Working children / selection.</summary>
    public event EventHandler? TreeChanged;

    private static bool IsSupportedMesh(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".obj" or ".glb" or ".gltf" or ".fbx" or ".dae" or ".ply" or ".stl";
    }

    private string UniqueQueueAssetName(string baseName)
    {
        var name = string.IsNullOrEmpty(baseName) ? "prop" : baseName;
        var taken = new HashSet<string>(
            ConvertQueue.Select(q => q.AssetName)
                .Concat(Entries.Select(e => e.AssetName)),
            StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int n = 2; n < 1000; n++)
        {
            var cand = $"{name}_{n}";
            if (!taken.Contains(cand)) return cand;
        }
        return $"{name}_{Guid.NewGuid():N}"[..16];
    }

    /// <summary>
    /// Snapshot the engine's resource folder (the per-asset
    /// <c>&lt;workdir&gt;\&lt;asset&gt;_resource\stream\*</c> tree) into the
    /// pack staging directory and record an entry. Called from
    /// <see cref="EngineRunner"/>'s pack delivery path before the work
    /// directory gets cleaned up — by the time this returns the bytes are
    /// in a stable location under <see cref="StagingDir"/>.
    /// </summary>
    /// <returns>The created entry, or null when the source resource dir
    /// has no stream/ tree (engine likely failed before writing).</returns>
    public PropPackEntry? AddFromResourceDir(string resourceDir, string assetName, string? groupName = null)
    {
        if (string.IsNullOrWhiteSpace(resourceDir) || !Directory.Exists(resourceDir))
            return null;

        var srcStream = Path.Combine(resourceDir, "stream");
        if (!Directory.Exists(srcStream))
            return null;

        // Each entry gets its own subfolder so re-adding an asset with the
        // same name doesn't silently clobber the previous one — Remove()
        // is the explicit way to drop a prior version.
        var slot = UniqueSlot(assetName);
        var dstStream = Path.Combine(StagingDir, slot, "stream");
        Directory.CreateDirectory(dstStream);

        var files = new List<string>();
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(srcStream))
        {
            var dst = Path.Combine(dstStream, Path.GetFileName(f));
            File.Copy(f, dst, overwrite: true);
            files.Add(dst);
            total += new FileInfo(dst).Length;
        }

        // Carry the resource-level fxmanifest.lua + any weapon metas, etc.
        // into the slot so we can re-read them at finalize time if we ever
        // need their data_file declarations. Current builder regenerates
        // the manifest from scratch, but capturing the source manifest
        // means we don't lose information if that ever changes.
        foreach (var f in Directory.EnumerateFiles(resourceDir))
        {
            var dst = Path.Combine(StagingDir, slot, Path.GetFileName(f));
            File.Copy(f, dst, overwrite: true);
            total += new FileInfo(dst).Length;
        }

        var group = string.IsNullOrWhiteSpace(groupName) ? null : groupName!.Trim();
        if (group is not null && !Groups.Contains(group, StringComparer.OrdinalIgnoreCase))
            Groups.Add(group);

        var entry = new PropPackEntry
        {
            AssetName = assetName,
            SlotName = slot,
            SlotDir = Path.Combine(StagingDir, slot),
            StreamFiles = files.Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToList(),
            TotalBytes = total,
            AddedAt = DateTime.Now,
            Label = Humanize(assetName),
            Category = DefaultCategory,
            Price = DefaultPrice,
            Type = DefaultType,
            GroupName = group,
        };
        Entries.Add(entry);
        NotifyAggregateChanged();
        return entry;
    }

    /// <summary>Drop a single entry from the pack — removes its staged
    /// files on disk and the row from the panel. No-op if the entry has
    /// already been removed (e.g. double-click on the X).</summary>
    public void Remove(PropPackEntry entry)
    {
        if (entry is null) return;
        if (!Entries.Remove(entry)) return;
        try
        {
            if (Directory.Exists(entry.SlotDir))
                Directory.Delete(entry.SlotDir, recursive: true);
        }
        catch { /* staging cleanup is best-effort */ }
        NotifyAggregateChanged();
    }

    /// <summary>Wipe every entry + delete the staging tree. Called when
    /// the user clicks "Clear pack" or after a successful finalize.</summary>
    public void Clear()
    {
        Entries.Clear();
        ConvertQueue.Clear();
        Groups.Clear();
        _groupExpanded.Clear();
        _hiddenGroups.Clear();
        try
        {
            if (Directory.Exists(StagingDir))
                Directory.Delete(StagingDir, recursive: true);
        }
        catch { /* swallow */ }
        NotifyAggregateChanged();
        RebuildTree();
    }

    private void NotifyAggregateChanged()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(TotalSizeDisplay));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(StatusSummary));
        OnPropertyChanged(nameof(HasConvertQueue));
        OnPropertyChanged(nameof(CanRunConvertQueue));
        OnPropertyChanged(nameof(PendingQueueCount));
        OnPropertyChanged(nameof(ItemCountDisplay));
        OnPropertyChanged(nameof(HasOutlinerContent));
    }

    /// <summary>Compute a free subfolder name for a new entry. Same asset
    /// added twice gets <c>name</c>, <c>name-2</c>, <c>name-3</c>, etc.</summary>
    private string UniqueSlot(string assetName)
    {
        var baseSlot = Sanitize(assetName);
        var cand = baseSlot;
        int n = 2;
        while (Directory.Exists(Path.Combine(StagingDir, cand)) ||
               Entries.Any(e => string.Equals(e.SlotName, cand, StringComparison.OrdinalIgnoreCase)))
        {
            cand = $"{baseSlot}-{n}";
            n++;
        }
        return cand;
    }

    private static string Sanitize(string raw)
    {
        var chars = (raw ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray();
        var s = new string(chars).ToLowerInvariant();
        return string.IsNullOrEmpty(s) ? "prop" : s;
    }

    /// <summary>Best-effort label from an asset name — strips common prefixes
    /// (prop_, v_res_, etc.), splits on underscores, title-cases each word.
    /// Result is what housing scripts will show in their furniture menu.</summary>
    private static string Humanize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Prop";
        var s = raw.Trim().ToLowerInvariant();
        // Strip common GTA prop name prefixes — the user's seeing "Old Couch"
        // in the menu, not "v_res_r_old_couch".
        foreach (var pfx in new[] { "prop_", "v_res_", "v_ilev_", "v_corp_", "p_" })
        {
            if (s.StartsWith(pfx)) { s = s[pfx.Length..]; break; }
        }
        var parts = s.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(p =>
            p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }
}

/// <summary>One row in the pack panel. Mutable so the user can edit
/// label/category/price/type inline before finalising — finalize-time
/// catalog emission reads the latest values straight off the entry.
/// <see cref="SlotName"/> is the on-disk subfolder under
/// <see cref="PropPackSession.StagingDir"/>; it can diverge from
/// <see cref="AssetName"/> when the user adds the same asset twice
/// (slot gets a "-2" suffix).</summary>
public sealed partial class PropPackEntry : ObservableObject
{
    /// <summary>Mutable so a staged layer can be renamed after convert —
    /// the export pipeline derives resource + archetype names from these
    /// at build time (stems are re-copied + the YDR's internal name is
    /// rewritten), so a rename here renames the shipped prop. Go through
    /// <see cref="PropPackSession.RenameEntry"/> for uniqueness.</summary>
    public string AssetName { get; set; } = "";
    public string SlotName { get; set; } = "";
    public string SlotDir { get; init; } = "";
    public IReadOnlyList<string> StreamFiles { get; init; } = Array.Empty<string>();
    public long TotalBytes { get; init; }
    public DateTime AddedAt { get; init; }

    /// <summary>Photoshop-style group membership. Null = loose layer at the
    /// top level of the outliner — exports as its own standalone resource.
    /// Non-null = member of that group; the group finalises into one pack
    /// resource. Mutate via <see cref="PropPackSession.MoveEntryToGroup"/>
    /// so the tree rebuilds.</summary>
    [ObservableProperty]
    private string? _groupName;

    /// <summary>Layer eye. Off = skipped when its group (or the loose
    /// layer itself) is exported. Mutate via
    /// <see cref="PropPackSession.SetEntryIncluded"/> so group aggregate
    /// eyes refresh.</summary>
    [ObservableProperty]
    private bool _isIncluded = true;

    /// <summary>Display name shown in the housing script's furniture menu
    /// (the <c>label</c> field in every script's catalog). Defaults to a
    /// title-cased version of the asset name; user can edit inline.</summary>
    [ObservableProperty]
    private string _label = "";

    /// <summary>One of the eight canonical FiveOS categories (see
    /// <see cref="HousingCategory"/>). Mapped to each script's native
    /// category name at emit time.</summary>
    [ObservableProperty]
    private string _category = HousingCategory.Decorations;

    /// <summary>In-game purchase price. Some scripts (qbx_properties) ignore
    /// this — the field is still written to catalog.json for consistency.</summary>
    [ObservableProperty]
    private int _price = 250;

    /// <summary>Storage / Wardrobe flag. Causes ps-housing and nolag to wire
    /// the prop to an inventory stash or clothing storage. Default None.</summary>
    [ObservableProperty]
    private FurnitureType _type = FurnitureType.None;

    /// <summary>Comma-joined list of file basenames for the row's tooltip
    /// (e.g. "prop1.ydr · prop1.ytyp").</summary>
    public string FileSummary => string.Join(" · ", StreamFiles);

    public string SizeDisplay
    {
        get
        {
            double b = TotalBytes;
            if (b < 1024) return $"{b:0} B";
            if (b < 1024 * 1024) return $"{b / 1024:0.#} KB";
            return $"{b / (1024 * 1024):0.##} MB";
        }
    }
}

/// <summary>Canonical FiveOS furniture categories. Eight buckets covering
/// what every popular housing script (ps-housing, qbx_properties,
/// loaf_housing, qs-housing, nolag_properties) ships with by default.
/// Mapped to per-script category names at catalog-emit time — see
/// <see cref="HousingCatalogEmitter"/>.</summary>
public static class HousingCategory
{
    public const string Couches     = "Couches";
    public const string Chairs      = "Chairs";
    public const string Tables      = "Tables";
    public const string Beds        = "Beds";
    public const string Storage     = "Storage";
    public const string Electronics = "Electronics";
    public const string Lighting    = "Lighting";
    public const string Decorations = "Decorations";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Couches, Chairs, Tables, Beds, Storage, Electronics, Lighting, Decorations,
    };
}

/// <summary>Special-behaviour furniture types recognised by ps-housing,
/// nolag_properties, and bcs-housing. Plain decorative props use
/// <see cref="None"/>.</summary>
public enum FurnitureType
{
    None,
    Storage,
    Wardrobe,
}
