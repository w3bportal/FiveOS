// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FiveOS.Services;

/// <summary>
/// Installs an exported emote straight into an rpemotes-reborn resource as an
/// addon: the .ycd goes to <c>stream/[Custom Emotes]/FiveOS/</c> (FiveM streams
/// the stream/ tree recursively) and the emote is registered in
/// <c>client/AnimationListCustom.lua</c> — the file rpemotes-reborn ships
/// precisely for user additions, merged into its animation list at load.
/// After a resource restart the emote shows up in the in-game emote menu.
/// </summary>
internal static class RpEmotesInstaller
{
    /// <summary>Cheap sanity check that a folder is an rpemotes(-reborn)
    /// install with the addon list file we patch.</summary>
    public static bool LooksLikeInstall(string? folder)
        => !string.IsNullOrWhiteSpace(folder)
           && File.Exists(Path.Combine(folder, "fxmanifest.lua"))
           && File.Exists(Path.Combine(folder, "client", "AnimationListCustom.lua"));

    /// <summary>rpemotes command names: lowercase, letters/digits/underscore.</summary>
    public static string SanitizeEmoteName(string? raw)
    {
        var name = (raw ?? "").Trim().ToLowerInvariant();
        name = Regex.Replace(name, "[^a-z0-9_]+", "_").Trim('_');
        if (name.Length > 40) name = name[..40].Trim('_');
        return name.Length == 0 ? "fiveos_emote" : name;
    }

    /// <summary>Writes the .ycd and registers the emote. Returns a user-facing
    /// result message; throws on I/O failure (caller reports).</summary>
    public static string Install(
        string folder, string emoteName, string label, byte[] ycdBytes, bool emoteMoving)
    {
        var streamDir = Path.Combine(folder, "stream", "[Custom Emotes]", "FiveOS");
        Directory.CreateDirectory(streamDir);
        File.WriteAllBytes(Path.Combine(streamDir, emoteName + ".ycd"), ycdBytes);

        var listPath = Path.Combine(folder, "client", "AnimationListCustom.lua");
        var lua = File.ReadAllText(listPath, Encoding.UTF8);

        // Already registered (e.g. re-export of the same emote): the fresh
        // .ycd above is all that's needed — leave their list entry alone.
        if (lua.Contains($"[\"{emoteName}\"]", System.StringComparison.Ordinal))
            return $"Updated '{emoteName}' in RPEmotes (animation replaced, existing menu entry kept). "
                 + "Restart the resource to see it.";

        var m = Regex.Match(lua, @"CustomDP\.Emotes\s*=\s*\{");
        if (!m.Success)
            throw new System.InvalidOperationException(
                "Couldn't find the CustomDP.Emotes table in client/AnimationListCustom.lua — "
                + "is this an rpemotes-reborn install?");

        // One-time safety copy before the first FiveOS edit of their file.
        var bak = listPath + ".fiveos.bak";
        if (!File.Exists(bak)) File.Copy(listPath, bak);

        // The .ycd filename is the animation dictionary; the clip inside is
        // built with the same name (see YcdAnimationBuilder callers).
        var entry =
            "\n    [\"" + emoteName + "\"] = {\n" +
            "        \"" + emoteName + "\",\n" +
            "        \"" + emoteName + "\",\n" +
            "        \"" + LuaEscape(label) + "\",\n" +
            "        {\n" +
            "            EmoteMoving = " + (emoteMoving ? "true" : "false") + ",\n" +
            "            EmoteLoop = true,\n" +
            "        },\n" +
            "    },";

        // Insert right after the opening brace — valid for the shipped empty
        // table ({}) and for tables that already hold other custom emotes.
        int at = m.Index + m.Length;
        lua = lua[..at] + entry + lua[at..];
        File.WriteAllText(listPath, lua, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return $"Added '{emoteName}' to RPEmotes. Restart the resource "
             + $"(restart {Path.GetFileName(folder)}) and find it in the emote menu — /e {emoteName}.";
    }

    /// <summary>Writes the .ycd and registers a SHARED (synced two-player)
    /// emote in CustomDP.Shared. Both players play this clip; the accepting
    /// player is positioned by the sync offsets (rpemotes semantics: Side =
    /// right of the initiator, Front = ahead, Height up, Heading subtracted
    /// from the initiator's heading — 180 = face each other).</summary>
    public static string InstallShared(
        string folder, string emoteName, string label, byte[] ycdBytes,
        double front, double side, double height, double heading)
    {
        var streamDir = Path.Combine(folder, "stream", "[Custom Emotes]", "FiveOS");
        Directory.CreateDirectory(streamDir);
        File.WriteAllBytes(Path.Combine(streamDir, emoteName + ".ycd"), ycdBytes);

        var listPath = Path.Combine(folder, "client", "AnimationListCustom.lua");
        var lua = File.ReadAllText(listPath, Encoding.UTF8);

        var m = Regex.Match(lua, @"CustomDP\.Shared\s*=\s*\{");
        if (!m.Success)
            throw new System.InvalidOperationException(
                "Couldn't find the CustomDP.Shared table in client/AnimationListCustom.lua — "
                + "is this an rpemotes-reborn install?");

        var bak = listPath + ".fiveos.bak";
        if (!File.Exists(bak)) File.Copy(listPath, bak);

        string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("\n    [\"").Append(emoteName).Append("\"] = {\n");
        sb.Append("        \"").Append(emoteName).Append("\",\n");
        sb.Append("        \"").Append(emoteName).Append("\",\n");
        sb.Append("        \"").Append(LuaEscape(label)).Append("\",\n");
        sb.Append("        AnimationOptions = {\n");
        sb.Append("            EmoteLoop = true,\n");
        sb.Append("            SyncOffsetFront = ").Append(F(front)).Append(",\n");
        sb.Append("            SyncOffsetSide = ").Append(F(side)).Append(",\n");
        if (System.Math.Abs(height) > 0.001)
            sb.Append("            SyncOffsetHeight = ").Append(F(height)).Append(",\n");
        if (System.Math.Abs(heading - 180.0) > 0.5)
            sb.Append("            SyncOffsetHeading = ").Append(F(heading)).Append(",\n");
        sb.Append("        },\n");
        sb.Append("    },");

        // Duplicate handling is scoped to the Shared TABLE — the same name in
        // CustomDP.Emotes is a different (solo) emote and must not swallow the
        // Shared registration. An existing Shared entry is REPLACED outright so
        // a re-export updates the sync offsets (they are the point of one).
        var key = $"[\"{emoteName}\"]";
        int tableOpen = m.Index + m.Length - 1;
        int tableEnd = FindTableEnd(lua, tableOpen);
        int entryAt = tableEnd > tableOpen
            ? lua.IndexOf(key, tableOpen, tableEnd - tableOpen, System.StringComparison.Ordinal)
            : -1;
        bool nameElsewhere = ExistsOutsideRange(lua, key, tableOpen, tableEnd);
        var conflictNote = nameElsewhere
            ? $" Note: '{emoteName}' also exists as a regular emote in this file — pick a distinct name if the wrong one plays."
            : "";

        if (entryAt >= 0)
        {
            int entryOpen = lua.IndexOf('{', entryAt);
            int entryClose = entryOpen > 0 ? FindTableEnd(lua, entryOpen) : -1;
            if (entryClose > entryOpen)
            {
                int entryEnd = entryClose + 1;
                if (entryEnd < lua.Length && lua[entryEnd] == ',') entryEnd++;
                lua = lua[..entryAt] + sb.ToString().TrimStart('\n', ' ') + lua[entryEnd..];
                File.WriteAllText(listPath, lua, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return $"Updated synced emote '{emoteName}' in RPEmotes (animation + offsets replaced). "
                     + "Restart the resource to see it." + conflictNote;
            }
        }

        int at = m.Index + m.Length;
        lua = lua[..at] + sb.ToString() + lua[at..];
        File.WriteAllText(listPath, lua, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return $"Added synced emote '{emoteName}' to RPEmotes. Restart the resource — "
             + $"a nearby player gets the accept prompt when you /e {emoteName}." + conflictNote;
    }

    /// <summary>Index of the '}' closing the brace at <paramref name="openBraceIdx"/>,
    /// or -1. Plain brace counting — fine for the entry format we write, naive
    /// about braces inside string literals (matches this installer's approach).</summary>
    private static int FindTableEnd(string lua, int openBraceIdx)
    {
        int depth = 0;
        for (int i = openBraceIdx; i < lua.Length; i++)
        {
            if (lua[i] == '{') depth++;
            else if (lua[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static bool ExistsOutsideRange(string text, string needle, int from, int to)
    {
        for (int i = text.IndexOf(needle, System.StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + 1, System.StringComparison.Ordinal))
            if (i < from || i > to) return true;
        return false;
    }

    private static string LuaEscape(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
