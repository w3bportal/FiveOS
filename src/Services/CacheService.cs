// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FiveOS.Services;

/// <summary>
/// Inventory + clear logic for the temp-folder caches the app generates
/// during normal use:
/// <list type="bullet">
///   <item><c>%TEMP%\FiveOS\WebView2\</c> — WebView2 user data (locked while app runs)</item>
///   <item><c>%TEMP%\FiveOS\Viewer-*\</c> — per-session viewer bundle (active one locked)</item>
///   <item><c>%TEMP%\FiveOS\ytd-opt-*\</c>, <c>sketchfab-*\</c>, plain GUIDs — per-operation work dirs</item>
///   <item><c>%TEMP%\ydr-writer\*\</c> — YDR writer work dirs</item>
/// </list>
///
/// Saved API keys live under <c>%APPDATA%\FiveOS\secrets\</c> (DPAPI), and
/// extracted runtime assets live under <c>%LOCALAPPDATA%\FiveOS\runtime\</c>;
/// neither path is touched here.
/// </summary>
public static class CacheService
{
    public sealed record Report(long BytesTotal, long BytesFreed, int SkippedDirs);

    private static IEnumerable<string> CacheRoots()
    {
        var temp = Path.GetTempPath();
        yield return Path.Combine(temp, "FiveOS");
        yield return Path.Combine(temp, "ydr-writer");
    }

    public static long ComputeSize()
    {
        long total = 0;
        foreach (var root in CacheRoots())
            total += DirSize(root);
        return total;
    }

    public static Report Clear()
    {
        long total = 0, freed = 0;
        var skipped = 0;

        foreach (var root in CacheRoots())
        {
            if (!Directory.Exists(root)) continue;

            // Try every immediate child individually so a single locked dir
            // (active WebView2 / Viewer session) doesn't abort the rest.
            foreach (var child in Directory.EnumerateFileSystemEntries(root))
            {
                var size = PathSize(child);
                total += size;
                if (TryDelete(child))
                    freed += size;
                else
                    skipped++;
            }
        }

        return new Report(total, freed, skipped);
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
