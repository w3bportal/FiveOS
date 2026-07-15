// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FiveOS.Services;

/// <summary>
/// Packages a baked .ycd into a dpemotes-ready ZIP. End-user workflow:
/// 1. Unzip into their dpemotes resource folder (merges with existing
///    structure: stream/ for the .ycd, the snippet for the registry).
/// 2. Open dpemotes/client/AnimationListCustom.lua, paste the snippet
///    block into the CustomDP.Emotes table.
/// 3. Restart dpemotes (or the whole server). New emote is in the menu.
///
/// The auto-edit-the-lua-file route is deliberately avoided -- silently
/// rewriting a user's config is the kind of thing that destroys an
/// install when AnimationListCustom.lua has prior customisations.
/// Paste-by-hand is one extra step that keeps their file under their
/// control.
/// </summary>
public static class DpemotesPackBuilder
{
    /// <summary>
    /// Build a .zip containing:
    /// - stream/&lt;name&gt;.ycd
    /// - &lt;name&gt;.lua    (paste-ready registry entry)
    /// - README.txt
    /// </summary>
    /// <param name="emoteName">Short emote slug used as both filename
    /// and as the dictionary/clip name embedded in the .ycd.</param>
    /// <param name="displayName">Pretty name shown in the dpemotes menu.</param>
    /// <param name="ycdBytes">The baked .ycd file contents.</param>
    /// <param name="isLooping">True for held-pose emotes; false for
    /// one-shot animations.</param>
    /// <param name="isMoving">True if the player can walk while
    /// emoting; false locks them in place.</param>
    /// <summary>Optional prop info -- when non-null, the snippet
    /// emits to dpemotes' PropEmotes table instead of Emotes, with
    /// PropPlacement set to the prop's transform relative to PropBone.</summary>
    public record PropInfo(string ModelName, int BoneTag, float[] Placement);

    public static byte[] Build(
        string emoteName,
        string displayName,
        byte[] ycdBytes,
        bool isLooping = true,
        EmoteMovement movement = EmoteMovement.InPlace,
        PropInfo? prop = null)
    {
        if (string.IsNullOrWhiteSpace(emoteName))
            throw new ArgumentException("emoteName must be non-empty.", nameof(emoteName));
        if (ycdBytes is null || ycdBytes.Length == 0)
            throw new ArgumentException("ycdBytes must be non-empty.", nameof(ycdBytes));

        var safe = SanitizeName(emoteName);
        var pretty = string.IsNullOrWhiteSpace(displayName) ? safe : displayName;

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // 1. The .ycd file -- goes under stream/ so a straight
            //    unzip-over-dpemotes merges it into the right place.
            var ycdEntry = zip.CreateEntry($"stream/{safe}.ycd", CompressionLevel.Fastest);
            using (var s = ycdEntry.Open()) s.Write(ycdBytes, 0, ycdBytes.Length);

            // 2. Snippet to paste into AnimationListCustom.lua. If the
            //    emote carries a prop, this is a PropEmotes entry with
            //    Prop / PropBone / PropPlacement; otherwise a plain
            //    Emotes entry.
            // Root-motion emotes: also emit rpemotes-reborn's `onFootFlag` with
            // the mover-extraction flag so THAT menu travels the ped. dpemotes
            // itself ignores the field (and can't extract movers) — those users
            // need the standalone resource export instead.
            int? onFootFlag = movement == EmoteMovement.RootMotion
                ? movement.ToAnimFlag(isLooping)
                : null;
            var lua = BuildSnippet(safe, pretty, isLooping, movement.ToEmoteMoving(), onFootFlag, prop);
            var luaEntry = zip.CreateEntry($"{safe}.lua", CompressionLevel.Fastest);
            using (var s = luaEntry.Open())
            using (var w = new StreamWriter(s, new UTF8Encoding(false))) w.Write(lua);

            // 3. README.
            var readme = BuildReadme(safe, pretty, ycdBytes.Length);
            var readmeEntry = zip.CreateEntry("README.txt", CompressionLevel.Fastest);
            using (var s = readmeEntry.Open())
            using (var w = new StreamWriter(s, new UTF8Encoding(false))) w.Write(readme);
        }
        return ms.ToArray();
    }

    private static string BuildSnippet(string name, string displayName, bool loop, bool moving, int? onFootFlag, PropInfo? prop)
    {
        var escDisplay = displayName.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var targetTable = prop is null ? "CustomDP.Emotes" : "CustomDP.PropEmotes";
        var sb = new StringBuilder();
        sb.AppendLine($"-- Paste this block inside the {targetTable} table in");
        sb.AppendLine("-- dpemotes/client/AnimationListCustom.lua, then restart dpemotes.");
        sb.AppendLine();
        sb.AppendLine($"    [\"{name}\"] = {{");
        sb.AppendLine($"        \"{name}\",");
        sb.AppendLine($"        \"{name}\",");
        sb.AppendLine($"        \"{escDisplay}\",");
        sb.AppendLine("        AnimationOptions = {");
        if (prop is not null)
        {
            // Lua escape on prop model name (typically lowercase
            // alphanumeric + underscore, but defensive escape anyway).
            var escProp = prop.ModelName.Replace("\\", "\\\\").Replace("\"", "\\\"");
            sb.AppendLine($"            Prop = \"{escProp}\",");
            sb.AppendLine($"            PropBone = {prop.BoneTag},");
            // PropPlacement is 6 floats: x, y, z (metres), then xRot,
            // yRot, zRot (degrees). dpemotes formats each on its own
            // line in their source -- match the style so it diffs
            // cleanly.
            var p = prop.Placement;
            sb.AppendLine("            PropPlacement = {");
            for (int i = 0; i < Math.Min(6, p.Length); i++)
                sb.AppendLine($"                {F(p[i])},");
            sb.AppendLine("            },");
        }
        sb.AppendLine($"            EmoteMoving = {(moving ? "true" : "false")},");
        sb.AppendLine($"            EmoteLoop = {(loop ? "true" : "false")},");
        if (onFootFlag is int flag)
        {
            // rpemotes-reborn honours a raw TaskPlayAnim flag here; this one
            // extracts the clip's SKEL_ROOT mover so the ped physically travels
            // (imported-clip root motion). dpemotes ignores the field.
            sb.AppendLine($"            onFootFlag = {flag}, -- root motion (mover extraction); rpemotes-reborn only");
        }
        sb.AppendLine("        }");
        sb.AppendLine("    },");
        return sb.ToString();
    }

    private static string F(float v) =>
        v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string BuildReadme(string name, string displayName, int ycdBytes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FiveOS emote pack: {displayName} ({name})");
        sb.AppendLine($"  Built {DateTime.Now:yyyy-MM-dd HH:mm}, {name}.ycd is {ycdBytes:N0} bytes.");
        sb.AppendLine();
        sb.AppendLine("Drop-in instructions:");
        sb.AppendLine();
        sb.AppendLine("1. Locate your dpemotes resource folder, typically:");
        sb.AppendLine("   <server>/resources/[addons]/dpemotes/");
        sb.AppendLine();
        sb.AppendLine($"2. Copy stream/{name}.ycd from this zip into dpemotes/stream/");
        sb.AppendLine($"   (Or unzip this whole archive over the dpemotes folder; the");
        sb.AppendLine($"   stream/ folder structure matches.)");
        sb.AppendLine();
        sb.AppendLine($"3. Open dpemotes/client/AnimationListCustom.lua and paste the");
        sb.AppendLine($"   contents of {name}.lua inside the CustomDP.Emotes table:");
        sb.AppendLine();
        sb.AppendLine("       CustomDP.Emotes = {");
        sb.AppendLine($"           -- paste {name}.lua here");
        sb.AppendLine("       }");
        sb.AppendLine();
        sb.AppendLine("4. Restart the dpemotes resource (or restart the server).");
        sb.AppendLine();
        sb.AppendLine($"5. In-game: /e {name}");
        sb.AppendLine();
        sb.AppendLine("Troubleshooting:");
        sb.AppendLine("  * If the emote plays but bones are wrong: bone-name remap mismatch.");
        sb.AppendLine("    Check the source pose was authored on a SKEL_* GTA rig.");
        sb.AppendLine("  * If the menu shows the emote but nothing happens in-game:");
        sb.AppendLine("    the .ycd may not have streamed in. Use /restart dpemotes once");
        sb.AppendLine("    after copying the file, or check resmon for stream errors.");
        return sb.ToString();
    }

    private static string SanitizeName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ' || ch == '-') sb.Append('_');
        }
        var s = sb.ToString();
        if (s.Length == 0) s = "fiveos_emote";
        if (char.IsDigit(s[0])) s = "p_" + s;
        return s;
    }
}
