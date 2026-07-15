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
/// Indexes every <c>.ycd</c> in a GTA V install via CodeWalker's keyed
/// <see cref="RpfManager"/> (same mount path as <see cref="GameTextureCache"/>).
/// Entries stay as RPF references — bytes are extracted lazily when a dict
/// is selected for clip listing / preview.
/// </summary>
public static class AnimRpfIndex
{
    public enum State { NotConfigured, Loading, Ready, Failed }

    public static State Status { get; private set; } = State.NotConfigured;
    public static string? Error { get; private set; }
    public static string? GtaFolder { get; private set; }

    /// <summary>Sorted dictionary entries (name without extension).</summary>
    public static IReadOnlyList<AnimDictEntry> Dictionaries => _dicts;

    private static List<AnimDictEntry> _dicts = new();
    private static readonly object Lock = new();
    private static Task<bool>? _loadTask;

    /// <summary>Kick off (or reuse) the background index. Safe from the UI thread.</summary>
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
                Error = "No GTA V folder found — set it to browse game animations.";
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
            GTA5Keys.LoadFromPath(folder, gen9, null);

            var rpf = new RpfManager();
            // buildIndex:false required under single-file publish (see GameTextureCache).
            rpf.Init(folder, gen9, _ => { }, _ => { }, rootOnly: false, buildIndex: false);

            // Prefer the shortest path per file name (base-game over DLC duplicates).
            var best = new Dictionary<string, RpfFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in rpf.EntryDict)
            {
                if (kv.Value is not RpfFileEntry fe) continue;
                var nameLower = fe.NameLower ?? "";
                if (!nameLower.EndsWith(".ycd", StringComparison.Ordinal)) continue;

                if (!best.TryGetValue(nameLower, out var existing)
                    || (fe.Path?.Length ?? int.MaxValue) < (existing.Path?.Length ?? int.MaxValue))
                {
                    best[nameLower] = fe;
                }
            }

            var list = best.Values
                .Select(fe =>
                {
                    var name = Path.GetFileNameWithoutExtension(fe.Name);
                    return new AnimDictEntry(
                        name,
                        fe.Name,
                        fe.Path ?? fe.Name,
                        AnimDictCategories.Classify(name),
                        fe);
                })
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (list.Count == 0)
            {
                Status = State.Failed;
                Error = "GTA folder found but no .ycd animation dictionaries were indexed.";
                return false;
            }

            lock (Lock)
            {
                _dicts = list;
                Status = State.Ready;
                GtaFolder = folder;
            }
            FosLogger.Info("anim-library", $"indexed {list.Count} .ycd dictionaries from {folder}");
            return true;
        }
        catch (Exception ex)
        {
            Status = State.Failed;
            Error = "Couldn't read GTA animation files: " + ex.Message;
            FosLogger.Warn("anim-library", "anim index failed", ex);
            return false;
        }
    }

    /// <summary>Extract a dictionary as on-disk RSC7 bytes for
    /// <see cref="YcdImporter"/>.</summary>
    public static byte[] ExtractBytes(AnimDictEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var fe = entry.FileEntry
            ?? throw new InvalidOperationException("Dictionary has no RPF entry.");
        var data = fe.File.ExtractFile(fe)
            ?? throw new InvalidDataException("Extract failed: " + entry.FileName);
        if (fe is RpfResourceFileEntry re)
        {
            data = ResourceBuilder.Compress(data);
            data = ResourceBuilder.AddResourceHeader(re, data);
        }
        return data;
    }

    /// <summary>Re-point at a new folder. Clears the index so the next
    /// <see cref="EnsureLoadedAsync"/> rebuilds it.</summary>
    public static void Reset(string? newFolder)
    {
        lock (Lock)
        {
            _dicts = new();
            _loadTask = null;
            Status = State.NotConfigured;
            Error = null;
            GtaFolder = newFolder;
        }
    }
}

/// <summary>One vanilla animation dictionary (.ycd) from the game install.</summary>
public sealed class AnimDictEntry
{
    public AnimDictEntry(string name, string fileName, string path, string category, RpfFileEntry fileEntry)
    {
        Name = name;
        FileName = fileName;
        Path = path;
        Category = category;
        FileEntry = fileEntry;
    }

    /// <summary>Dictionary name used by RequestAnimDict (no extension).</summary>
    public string Name { get; }

    /// <summary>File name including .ycd.</summary>
    public string FileName { get; }

    /// <summary>Archive-relative path for display / search.</summary>
    public string Path { get; }

    /// <summary>Browse category from <see cref="AnimDictCategories"/>.</summary>
    public string Category { get; }

    internal RpfFileEntry FileEntry { get; }
}
