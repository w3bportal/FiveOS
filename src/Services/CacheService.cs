// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FiveOS.Services;

/// <summary>
/// Inventory + clear logic for caches the app generates during normal use:
/// <list type="bullet">
///   <item><c>%TEMP%\FiveOS\</c> — WebView2 profiles, viewer bundles, Sketchfab/AI/opt temps</item>
///   <item><c>%TEMP%\ydr-writer\</c> — YDR writer work dirs</item>
///   <item><c>%LOCALAPPDATA%\FiveOS\runtime\</c> — old version extracts (current kept)</item>
/// </list>
/// Secrets under <c>%APPDATA%\FiveOS\secrets\</c> are never touched.
/// </summary>
public static class CacheService
{
    public sealed record Report(long BytesTotal, long BytesFreed, int SkippedDirs);

    /// <summary>How many content-keyed Emotes viewer folders to keep when pruning.</summary>
    private const int KeepViewerPoseFolders = 2;

    private static IEnumerable<string> TempCacheRoots()
    {
        var temp = Path.GetTempPath();
        yield return Path.Combine(temp, "FiveOS");
        yield return Path.Combine(temp, "ydr-writer");
    }

    private static string RuntimeRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FiveOS", "runtime");

    public static long ComputeSize()
    {
        long total = 0;
        foreach (var root in TempCacheRoots())
            total += DirSize(root);
        total += DirSize(RuntimeRoot());
        return total;
    }

    public static Report Clear()
    {
        long total = 0, freed = 0;
        var skipped = 0;

        // Drop stale Emotes content caches first so Clear frees disk even when
        // the active WebView2 profile is locked.
        PruneViewerPoseFolders(ref total, ref freed, ref skipped);

        foreach (var root in TempCacheRoots())
        {
            if (!Directory.Exists(root)) continue;

            foreach (var child in Directory.EnumerateFileSystemEntries(root))
            {
                // ViewerPose folders already handled (kept newest N).
                if (IsViewerPoseDir(child)) continue;

                var size = PathSize(child);
                total += size;
                if (TryDelete(child))
                    freed += size;
                else
                    skipped++;
            }
        }

        // Old published runtime extracts (previous app versions / hash dirs).
        PurgeStaleRuntime(ref total, ref freed, ref skipped);

        return new Report(total, freed, skipped);
    }

    /// <summary>Called on startup / after Clear — keep only the newest
    /// content-keyed Emotes viewer folders so upgrades don't leave GB of
    /// three.js bundles under %TEMP%.</summary>
    public static void PruneStaleViewerCaches()
    {
        long t = 0, f = 0;
        var s = 0;
        PruneViewerPoseFolders(ref t, ref f, ref s);
        PurgeStaleRuntime(ref t, ref f, ref s);
    }

    private static bool IsViewerPoseDir(string path) =>
        Directory.Exists(path)
        && Path.GetFileName(path).StartsWith("ViewerPose-", StringComparison.OrdinalIgnoreCase);

    private static void PruneViewerPoseFolders(ref long total, ref long freed, ref int skipped)
    {
        var root = Path.Combine(Path.GetTempPath(), "FiveOS");
        if (!Directory.Exists(root)) return;

        List<DirectoryInfo> poseDirs;
        try
        {
            poseDirs = new DirectoryInfo(root)
                .EnumerateDirectories("ViewerPose-*")
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .ToList();
        }
        catch { return; }

        for (var i = 0; i < poseDirs.Count; i++)
        {
            var dir = poseDirs[i];
            var size = DirSize(dir.FullName);
            total += size;
            if (i < KeepViewerPoseFolders)
                continue;
            if (TryDelete(dir.FullName))
                freed += size;
            else
                skipped++;
        }
    }

    private static void PurgeStaleRuntime(ref long total, ref long freed, ref int skipped)
    {
        var root = RuntimeRoot();
        if (!Directory.Exists(root)) return;

        var currentVersion = typeof(CacheService).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        foreach (var versionDir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(versionDir);
            if (string.Equals(name, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                // Inside the current version, keep only the newest hash extract
                // per subdir prefix (viewer-*, engine-*).
                PruneOldHashExtracts(versionDir, ref total, ref freed, ref skipped);
                continue;
            }

            var size = DirSize(versionDir);
            total += size;
            if (TryDelete(versionDir))
                freed += size;
            else
                skipped++;
        }
    }

    private static void PruneOldHashExtracts(string versionDir, ref long total, ref long freed, ref int skipped)
    {
        IEnumerable<IGrouping<string, DirectoryInfo>> groups;
        try
        {
            groups = new DirectoryInfo(versionDir)
                .EnumerateDirectories()
                .GroupBy(d =>
                {
                    var n = d.Name;
                    var dash = n.LastIndexOf('-');
                    return dash > 0 ? n[..dash] : n;
                });
        }
        catch { return; }

        foreach (var g in groups)
        {
            var ordered = g.OrderByDescending(d => d.LastWriteTimeUtc).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var size = DirSize(ordered[i].FullName);
                total += size;
                if (i == 0) continue; // keep newest hash for this prefix
                if (TryDelete(ordered[i].FullName))
                    freed += size;
                else
                    skipped++;
            }
        }
    }

    private static long DirSize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => SafeLength(f));
        }
        catch { return 0; }
    }

    private static long PathSize(string path)
    {
        try
        {
            if (Directory.Exists(path)) return DirSize(path);
            if (File.Exists(path)) return new FileInfo(path).Length;
        }
        catch { /* best-effort */ }
        return 0;
    }

    private static long SafeLength(FileInfo f)
    {
        try { return f.Length; } catch { return 0; }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double v = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        var u = -1;
        do { v /= 1024; u++; } while (v >= 1024 && u < units.Length - 1);
        return v.ToString(v < 10 ? "0.0" : "0") + " " + units[u];
    }
}
