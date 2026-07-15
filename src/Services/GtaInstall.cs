// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FiveOS.Services;

/// <summary>
/// Locates a GTA V install folder (the one containing GTA5.exe) so the
/// Vehicles tab can load shared vehicle textures (vehshare.ytd) from it.
/// Order: saved override → Rockstar/Steam registry → Steam library folders →
/// common hard-coded paths. Returns null when nothing valid is found.
/// </summary>
public static class GtaInstall
{
    /// <summary>A folder is a GTA V install if it has the game exe.</summary>
    public static bool IsValidFolder(string? folder)
        => !string.IsNullOrWhiteSpace(folder)
           && (File.Exists(Path.Combine(folder, "GTA5.exe"))
               || File.Exists(Path.Combine(folder, "GTA5_Enhanced.exe"))
               || File.Exists(Path.Combine(folder, "PlayGTAV.exe")));

    /// <summary>True when the install is the Enhanced (gen9) edition — the
    /// RPF/keys loader needs to know which format to expect.</summary>
    public static bool IsEnhanced(string folder)
        => File.Exists(Path.Combine(folder, "GTA5_Enhanced.exe"));

    /// <summary>Resolve the GTA folder: saved setting first, else auto-detect.
    /// The result is NOT persisted here (the caller decides).</summary>
    public static string? Resolve()
    {
        var saved = UserSettings.LoadGtaFolder();
        if (IsValidFolder(saved)) return saved;
        return AutoDetect();
    }

    public static string? AutoDetect()
    {
        foreach (var c in Candidates())
            if (IsValidFolder(c))
                return c;
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        // Rockstar launcher registry.
        foreach (var key in new[]
                 {
                     @"SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V",
                     @"SOFTWARE\Rockstar Games\Grand Theft Auto V",
                     @"SOFTWARE\WOW6432Node\Rockstar Games\GTAV",
                 })
        {
            var v = ReadHklm(key, "InstallFolder") ?? ReadHklm(key, "InstallFolderSteam");
            if (v != null) yield return v;
        }

        // Steam: SteamPath + every library folder.
        foreach (var lib in SteamLibraries())
            yield return Path.Combine(lib, "steamapps", "common", "Grand Theft Auto V");

        // Common hard-coded installs.
        foreach (var root in new[]
                 {
                     @"C:\Program Files\Rockstar Games\Grand Theft Auto V",
                     @"C:\Program Files (x86)\Rockstar Games\Grand Theft Auto V",
                     @"C:\Program Files\Epic Games\GTAV",
                     @"C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V",
                     @"C:\Program Files\Steam\steamapps\common\Grand Theft Auto V",
                 })
            yield return root;
    }

    private static string? ReadHklm(string subkey, string value)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(subkey);
            return k?.GetValue(value) as string;
        }
        catch { return null; }
    }

    /// <summary>Every Steam library root (each contains steamapps\common\…).</summary>
    private static IEnumerable<string> SteamLibraries()
    {
        string? steam = null;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            steam = k?.GetValue("SteamPath") as string;
        }
        catch { }
        steam ??= @"C:\Program Files (x86)\Steam";
        steam = steam.Replace('/', '\\');
        yield return steam;   // the default library

        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); } catch { yield break; }

        // Each library entry has a "path"   "D:\\SteamLibrary" line.
        foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
        {
            var p = m.Groups[1].Value.Replace("\\\\", "\\");
            if (Directory.Exists(p)) yield return p;
        }
    }
}
