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
    }

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

    /// <summary>Total bytes across every file currently staged. Drives the
    /// pack-panel header ("3 props · 4.7 MB"). Recomputed on every mutation
    /// rather than tracked incrementally — cheap and avoids drift.</summary>
    public long TotalBytes => Entries.Sum(e => e.TotalBytes);

    /// <summary>Convenience for UI bindings — number of entries currently
    /// in the pack. Re-raises when Entries changes; the panel binds to it
    /// for the "N props" header.</summary>
    public int Count => Entries.Count;

    /// <summary>True iff at least one prop has been added — gates the
    /// Finalize button and the empty-state placeholder in the pack panel.</summary>
    public bool HasEntries => Entries.Count > 0;

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
    public PropPackEntry? AddFromResourceDir(string resourceDir, string assetName)
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
        try
        {
            if (Directory.Exists(StagingDir))
                Directory.Delete(StagingDir, recursive: true);
        }
        catch { /* swallow */ }
        NotifyAggregateChanged();
    }

    private void NotifyAggregateChanged()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(HasEntries));
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
    public string AssetName { get; init; } = "";
    public string SlotName { get; init; } = "";
    public string SlotDir { get; init; } = "";
    public IReadOnlyList<string> StreamFiles { get; init; } = Array.Empty<string>();
    public long TotalBytes { get; init; }
    public DateTime AddedAt { get; init; }

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
