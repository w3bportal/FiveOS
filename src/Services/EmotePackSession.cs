// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FiveOS.Services;

/// <summary>
/// Accumulator for the "Emote Pack" workflow: instead of every emote export
/// producing its own resource folder (ten emotes = ten folders, ten `ensure`
/// lines), the user hits "Add to Pack" after each one and finally exports the
/// whole queue as ONE standalone FiveM resource — see
/// <see cref="EmotePackBuilder"/>.
///
/// State lives in memory and dies with the app. That matches the "session,
/// not save file" intent the props pack already established: a pack is
/// something you build up in a sitting and finalise at the end.
///
/// WHY THE BYTES ARE HERE AND NOT A PROMISE TO BAKE LATER: the viewer's
/// sampler (window.samplePoseClipForExport) only works against the rig that
/// is loaded RIGHT NOW — it needs pose mode, live bones, and real clip
/// objects. Loading another model runs clearImportedClips(), which destroys
/// imported clips outright. So an entry captures its finished .ycd at Add
/// time; there is no such thing as a deferred bake.
/// </summary>
public sealed partial class EmotePackSession : ObservableObject
{
    /// <summary>Above this the FiveM clip-dictionary store starts to matter
    /// (every streamed .ycd takes a slot whether or not it's ever played),
    /// and the panel stops being scannable. Split into themed packs.</summary>
    public const int MaxEntries = 50;

    public static EmotePackSession Current { get; } = new();

    private EmotePackSession()
    {
        Entries.CollectionChanged += OnEntriesChanged;
    }

    /// <summary>Resource name used at export — drives the output folder, the
    /// `ensure` line, the /&lt;pack&gt; command, the streamed dictionary prefix,
    /// and the fxmanifest description. Sanitised at export, not here, so the
    /// textbox stays typeable.</summary>
    [ObservableProperty]
    private string _packName = "my_emotes";

    /// <summary>Queued emotes, in the order they'll appear in the exported
    /// resource's table, README and list command.</summary>
    public ObservableCollection<EmotePackEntry> Entries { get; } = new();

    /// <summary>True once the queue has changed since the last successful
    /// export — drives the "you have unexported emotes" prompt on close.</summary>
    public bool HasUnexportedChanges { get; private set; }

    public void MarkExported() => HasUnexportedChanges = false;

    // ── aggregates (panel header / footer / tab caption) ──────────────

    public int Count => Entries.Count;
    public bool HasEntries => Entries.Count > 0;
    public bool IsEmpty => Entries.Count == 0;
    public bool IsFull => Entries.Count >= MaxEntries;
    public int IncludedCount => Entries.Count(e => e.IsIncluded);

    /// <summary>Gates the Export button — a pack of nothing but muted rows
    /// would write a resource with an empty EMOTES table.</summary>
    public bool CanExport => Entries.Any(e => e.IsIncluded);

    /// <summary>Bytes that will actually ship (muted rows excluded).</summary>
    public long TotalBytes => Entries.Where(e => e.IsIncluded).Sum(e => (long)e.YcdBytes.Length);

    public string TotalSizeDisplay => FormatBytes(TotalBytes);

    /// <summary>"3 emotes · 107 KB", plus a muted count when some rows are
    /// switched off so the size figure never looks wrong.</summary>
    public string StatusSummary
    {
        get
        {
            if (Entries.Count == 0) return "";
            var parts = new List<string>
            {
                Entries.Count == 1 ? "1 emote" : $"{Entries.Count} emotes",
            };
            int muted = Entries.Count - IncludedCount;
            if (muted > 0) parts.Add($"{muted} muted");
            var size = TotalSizeDisplay;
            if (!string.IsNullOrEmpty(size)) parts.Add(size);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Vertical sidebar tab caption — carries the count so a queued
    /// pack is visible without opening the panel.</summary>
    public string TabLabel => Count > 0 ? $"Pack ({Count})" : "Pack";

    // ── mutations ────────────────────────────────────────────────────

    /// <summary>Append a freshly baked emote. Returns null when the pack is
    /// full (the caller reports that to the user — silently dropping a bake
    /// the user waited seconds for would be worse than refusing it).</summary>
    public EmotePackEntry? Add(EmotePackEntry entry)
    {
        if (entry is null || IsFull) return null;
        Entries.Add(entry);
        return entry;
    }

    public void Remove(EmotePackEntry entry)
    {
        if (entry is null) return;
        Entries.Remove(entry);
    }

    /// <summary>Shuffle a row by one. Order is cosmetic — it drives the Lua
    /// table order, the list command and the README, never behaviour.</summary>
    public void Move(EmotePackEntry entry, int delta)
    {
        if (entry is null || delta == 0) return;
        int from = Entries.IndexOf(entry);
        if (from < 0) return;
        int to = Math.Clamp(from + delta, 0, Entries.Count - 1);
        if (to == from) return;
        Entries.Move(from, to);
    }

    /// <summary>Rename the in-game command. SAFE to do after the bake: the
    /// command name only ever appears as a Lua string. The clip name inside
    /// the .ycd (<see cref="EmotePackEntry.ClipName"/>) is a different,
    /// immutable thing — see the remarks on that property.
    /// Returns false when the name is empty or already used in this pack.</summary>
    public bool SetCommandName(EmotePackEntry entry, string? requested)
    {
        if (entry is null) return false;
        var clean = Sanitize(requested);
        if (string.IsNullOrEmpty(clean)) return false;
        if (string.Equals(clean, entry.CommandName, StringComparison.OrdinalIgnoreCase)) return false;
        if (IsCommandTaken(clean, ignore: entry)) return false;
        entry.CommandName = clean;
        return true;
    }

    public bool IsCommandTaken(string? name, EmotePackEntry? ignore = null)
    {
        var clean = Sanitize(name);
        if (string.IsNullOrEmpty(clean)) return false;
        return Entries.Any(e => !ReferenceEquals(e, ignore)
            && string.Equals(e.CommandName, clean, StringComparison.OrdinalIgnoreCase));
    }

    public void Clear()
    {
        Entries.Clear();
    }

    /// <summary>A clip name free within this session. Collisions get _2, _3…
    /// and then keep that name forever: the returned value is baked into the
    /// .ycd, so it can never be renumbered when a neighbour is removed.</summary>
    public string UniqueClipName(string? requested)
    {
        var stem = Sanitize(requested);
        if (string.IsNullOrEmpty(stem)) stem = "emote";
        var taken = new HashSet<string>(Entries.Select(e => e.ClipName), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(stem)) return stem;
        for (int n = 2; n < 1000; n++)
        {
            var cand = Truncate($"{stem}_{n}");
            if (!taken.Contains(cand)) return cand;
        }
        return Truncate(stem + "_" + Guid.NewGuid().ToString("N")[..4]);
    }

    /// <summary>A command name free within this session, seeded from a clip
    /// name. Same shape as <see cref="UniqueClipName"/> but scoped to the
    /// command namespace, which is independently editable.</summary>
    public string UniqueCommandName(string? requested)
    {
        var stem = Sanitize(requested);
        if (string.IsNullOrEmpty(stem)) stem = "emote";
        if (!IsCommandTaken(stem)) return stem;
        for (int n = 2; n < 1000; n++)
        {
            var cand = Truncate($"{stem}_{n}");
            if (!IsCommandTaken(cand)) return cand;
        }
        return Truncate(stem + "_" + Guid.NewGuid().ToString("N")[..4]);
    }

    // ── naming ───────────────────────────────────────────────────────

    /// <summary>
    /// The ONE sanitizer for everything this feature names: clip names,
    /// command names, the pack name. Lowercase, [a-z0-9_] only, spaces and
    /// dashes folded to underscores, runs collapsed, 40-char cap, and a `p_`
    /// prefix when the result would start with a digit.
    ///
    /// That last rule is not cosmetic. The .ycd writers run their own
    /// sanitizer over whatever clip name they're handed, and theirs prefixes
    /// leading digits too — so a name that is already in this shape passes
    /// through them unchanged, which is exactly what keeps the Lua clip
    /// string and the hash baked inside the file in agreement.
    /// </summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        bool lastWasSep = false;
        foreach (var ch in raw.Trim())
        {
            if (char.IsLetterOrDigit(ch) && ch < 128)
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSep = false;
            }
            else if (ch == '_' || ch == ' ' || ch == '-')
            {
                // Collapse runs so "my  cool-emote" doesn't become "my__cool_emote".
                if (sb.Length > 0 && !lastWasSep) { sb.Append('_'); lastWasSep = true; }
            }
            // Everything else (punctuation, accents, emoji) is dropped.
        }
        var s = sb.ToString().Trim('_');
        if (s.Length == 0) return "";
        if (char.IsDigit(s[0])) s = "p_" + s;
        return Truncate(s);
    }

    private static string Truncate(string s) =>
        s.Length <= 40 ? s : s[..40].Trim('_');

    internal static string FormatBytes(long bytes)
    {
        double b = bytes;
        if (b <= 0) return "";
        if (b < 1024) return $"{b:0} B";
        if (b < 1024 * 1024) return $"{b / 1024:0.#} KB";
        return $"{b / (1024 * 1024):0.##} MB";
    }

    // ── change plumbing ──────────────────────────────────────────────

    /// <summary>Watch every row so the eye toggle can bind straight through
    /// (IsChecked TwoWay) with no code-behind handler and the aggregates
    /// still refresh.</summary>
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (EmotePackEntry old in e.OldItems)
                old.PropertyChanged -= OnEntryPropertyChanged;
        if (e.NewItems is not null)
            foreach (EmotePackEntry added in e.NewItems)
                added.PropertyChanged += OnEntryPropertyChanged;

        HasUnexportedChanges = Entries.Count > 0;
        NotifyAggregateChanged();
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EmotePackEntry.IsIncluded))
        {
            HasUnexportedChanges = true;
            NotifyAggregateChanged();
        }
        else if (e.PropertyName is nameof(EmotePackEntry.CommandName) or nameof(EmotePackEntry.Label))
        {
            HasUnexportedChanges = true;
        }
    }

    private void NotifyAggregateChanged()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFull));
        OnPropertyChanged(nameof(IncludedCount));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(TotalSizeDisplay));
        OnPropertyChanged(nameof(StatusSummary));
        OnPropertyChanged(nameof(TabLabel));
    }
}

/// <summary>
/// One baked emote waiting in the pack. The bytes ARE the shipped artifact —
/// nothing is re-baked at export time.
/// </summary>
public sealed partial class EmotePackEntry : ObservableObject
{
    /// <summary>
    /// The clip name baked INSIDE the .ycd, and therefore immutable.
    ///
    /// The writers stamp it into three fields of the file (Clips/Item/Hash,
    /// Clips/Item/AnimationHash, Animations/Item/Hash), and the first of those
    /// becomes the ClipMap key that TaskPlayAnim(ped, dict, clip) hashes and
    /// looks up. Change it after the bake and the emote fails SILENTLY: the
    /// dictionary loads, the call returns, nothing plays.
    ///
    /// The dictionary name is a different matter — it never appears in the
    /// file at all (FiveM registers a streamed asset under its file stem), so
    /// the export is free to name the file whatever it likes. That's why
    /// <see cref="CommandName"/> and <see cref="Label"/> can be edited freely
    /// and this cannot.
    /// </summary>
    public string ClipName { get; init; } = "";

    /// <summary>In-game command (/&lt;name&gt;). Lua string only — safe to rename.</summary>
    [ObservableProperty]
    private string _commandName = "";

    /// <summary>Pretty name shown by the list command and the README. Free text.</summary>
    [ObservableProperty]
    private string _label = "";

    /// <summary>Layer eye. Off = left out of both the Lua table AND stream/.</summary>
    [ObservableProperty]
    private bool _isIncluded = true;

    public byte[] YcdBytes { get; init; } = Array.Empty<byte>();

    /// <summary>Playback mode captured at bake time — the whole point of a
    /// pack. A wave (upper body, flag 49) and a moonwalk (root motion,
    /// 786433) have to coexist; reading the live combo at export would stamp
    /// the last emote's flag onto every entry.</summary>
    public EmoteMovement Movement { get; init; }

    public bool IsAnimated { get; init; }
    public int KeyframeCount { get; init; }
    public double DurationSeconds { get; init; }
    public int MappedBones { get; init; }
    public string SourceModel { get; init; } = "";

    /// <summary>True when the user asked for root motion but the bake carried
    /// no SKEL_ROOT mover, so <see cref="Movement"/> was downgraded to
    /// in-place. Surfaced in the row detail so the change isn't invisible.</summary>
    public bool RootMotionDowngraded { get; init; }

    public DpemotesPackBuilder.PropInfo? Prop { get; init; }

    public DateTime AddedAt { get; init; } = DateTime.Now;

    public string SizeDisplay => EmotePackSession.FormatBytes(YcdBytes.Length);

    public string BadgeText => IsAnimated ? "ANIM" : "POSE";

    /// <summary>Row subtitle: what this entry is and how it will play.</summary>
    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (IsAnimated)
            {
                parts.Add(KeyframeCount == 1 ? "1 key" : $"{KeyframeCount} keys");
                if (DurationSeconds > 0.01)
                    parts.Add($"{DurationSeconds:0.#}s");
            }
            else
            {
                parts.Add("held pose");
            }
            parts.Add(Movement.Label());
            if (RootMotionDowngraded) parts.Add("no mover — in place");
            if (Prop is not null) parts.Add("prop");
            parts.Add(SizeDisplay);
            return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
        }
    }

    /// <summary>Tooltip — where this emote came from and when it was baked.</summary>
    public string SourceSummary
    {
        get
        {
            var src = string.IsNullOrEmpty(SourceModel)
                ? "unknown source"
                : System.IO.Path.GetFileName(SourceModel);
            return $"clip \"{ClipName}\" · from {src} · {MappedBones} bones · baked {AddedAt:HH:mm}";
        }
    }
}
