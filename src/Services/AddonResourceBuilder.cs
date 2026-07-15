// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FiveOS.Services;

/// <summary>
/// "Add-on" mode of the RPF converter: turn the user's model into a NEW
/// named FiveM asset — as opposed to <see cref="ReplaceBuilder"/>, which
/// overrides a vanilla asset by name. Nothing in the base game is touched;
/// the prop is spawnable by its own archetype name (CreateObject, .ymap
/// placement, housing/furniture scripts).
///
/// Output is a conventional streamed-asset resource:
///
///   &lt;pack&gt;/
///     fxmanifest.lua          (this_is_a_map; files{}; DLC_ITYP_REQUEST)
///     README.txt
///     stream/
///       &lt;name&gt;.ydr  (+ same-stem .ytd/.ybn/.ycd siblings)
///       &lt;pack&gt;.ytyp  (archetype dictionary, one entry per .ydr)
///
/// The .ytyp is built by the engine's <c>merge-pack</c> subcommand from
/// each .ydr's embedded Drawable bounds — the same path the prop-pack
/// finalizer uses, so add-on archetypes behave identically to pack ones.
///
/// v1 scope mirrors Replace v1: props (.ydr) + textures (.ytd). Peds
/// (.ydd/.yft) are flagged — those go through the SP ped dlc.rpf modes.
/// </summary>
public sealed class AddonResourceBuilder
{
    /// <param name="NewAssetName">Optional new archetype name. Only applied
    /// when the input holds exactly ONE .ydr; empty keeps the model's own
    /// filename. Multi-model folders always keep their original names.</param>
    public sealed record Options(string? NewAssetName = null, string? PackName = null, double FallbackLodDist = 500d);

    public sealed record Result(
        bool Success,
        string? OutputPath,
        IReadOnlyList<string> ArchetypeNames,
        IReadOnlyList<string> ProducedFiles,
        IReadOnlyList<string> Warnings,
        string? Error);

    /// <summary>Stream-asset extensions copied into the add-on. Per-prop
    /// .ytyp files from earlier converts are dropped — merge-pack rebuilds
    /// one pack-wide dictionary so DLC_ITYP_REQUEST stays a single line.</summary>
    private static readonly string[] StreamExts = { ".ydr", ".ytd", ".ybn", ".ycd" };

    public Result Build(string inputFolder, string outputRootDir, Options opts, Action<string>? log = null)
    {
        var warnings = new List<string>();
        var produced = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(inputFolder) || !Directory.Exists(inputFolder))
                return Fail($"Input folder not found: {inputFolder}");

            var all = Directory.EnumerateFiles(inputFolder, "*", SearchOption.AllDirectories).ToList();
            var ydrs = all.Where(f => f.EndsWith(".ydr", StringComparison.OrdinalIgnoreCase)).ToList();

            if (all.Any(f => f.EndsWith(".yft", StringComparison.OrdinalIgnoreCase)))
                warnings.Add("A .yft (vehicle/ped fragment) was found — Add-on v1 only handles props (.ydr); the .yft was NOT used. Vehicles have their own tab.");
            if (all.Any(f => f.EndsWith(".ydd", StringComparison.OrdinalIgnoreCase)))
                warnings.Add("A .ydd (ped/clothing drawable dictionary) was found — Add-on v1 only handles props (.ydr). For add-on peds use the Singleplayer ped dlc.rpf modes.");
            if (ydrs.Count == 0)
                return Fail("No .ydr (prop model) found — an add-on needs a model to declare a new archetype. " +
                            "For texture-only overrides use a Replace mode; to just bundle files use Raw packed .rpf.");

            // New-name rename only makes sense for a single model; with
            // several, silently renaming one of them would be a trap.
            var newName = ReplaceBuilder.SanitizeAssetName(opts.NewAssetName);
            if (ydrs.Count > 1 && !string.IsNullOrEmpty(newName))
            {
                warnings.Add($"NEW ASSET NAME '{newName}' ignored — {ydrs.Count} models found, original names kept.");
                newName = "";
            }

            var firstStem = Path.GetFileNameWithoutExtension(ydrs[0]);
            var primaryName = string.IsNullOrEmpty(newName)
                ? ReplaceBuilder.SanitizeAssetName(firstStem)
                : newName;
            if (string.IsNullOrEmpty(primaryName)) primaryName = "prop";

            var pack = string.IsNullOrWhiteSpace(opts.PackName)
                ? primaryName + "_addon"
                : ReplaceBuilder.SanitizeAssetName(opts.PackName!);
            if (string.IsNullOrEmpty(pack)) pack = "addon";

            var resDir = Path.Combine(Path.GetFullPath(outputRootDir), pack);
            var streamDir = Path.Combine(resDir, "stream");
            Directory.CreateDirectory(streamDir);

            // Copy stream assets. When a single model is being renamed, its
            // same-stem siblings (.ytd/.ybn/.ycd) rename in lockstep so
            // RAGE's texture/physics lookups still resolve by stem.
            var renameFrom = ydrs.Count == 1 && !string.IsNullOrEmpty(newName) ? firstStem : null;
            var archetypes = new List<string>();
            foreach (var src in all)
            {
                var ext = Path.GetExtension(src);
                if (!StreamExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;

                var stem = Path.GetFileNameWithoutExtension(src);
                var outStem = renameFrom != null && stem.Equals(renameFrom, StringComparison.OrdinalIgnoreCase)
                    ? newName
                    : stem;
                var dstName = outStem + ext.ToLowerInvariant();
                var dst = Path.Combine(streamDir, dstName);
                File.Copy(src, dst, overwrite: true);
                produced.Add("stream/" + dstName);
                log?.Invoke($"  + stream/{dstName}");

                if (ext.Equals(".ydr", StringComparison.OrdinalIgnoreCase))
                {
                    archetypes.Add(outStem);
                    if (!outStem.Equals(stem, StringComparison.OrdinalIgnoreCase))
                    {
                        PropPackBuilder.RewriteYdrInternalName(dst, outStem);
                        log?.Invoke($"    internal name rewritten → {outStem}");
                    }
                }
            }

            // One pack-wide archetype dictionary from the .ydrs' embedded bounds.
            var ytypName = pack + ".ytyp";
            log?.Invoke($"Building stream/{ytypName} (archetypes from drawable bounds)…");
            var merge = PropPackBuilder.RunMergePack(pack, streamDir, Path.Combine(streamDir, ytypName), opts.FallbackLodDist);
            if (!merge.Success)
                return Fail($"ytyp build failed: {merge.Error}");
            produced.Add("stream/" + ytypName);

            File.WriteAllText(Path.Combine(resDir, "fxmanifest.lua"),
                PropPackBuilder.BuildManifest(pack, pack, ytypName), Utf8NoBom);
            produced.Add("fxmanifest.lua");

            File.WriteAllText(Path.Combine(resDir, "README.txt"),
                BuildReadme(pack, archetypes), Utf8NoBom);
            produced.Add("README.txt");

            log?.Invoke($"Done. Add-on resource '{pack}' — {archetypes.Count} archetype(s): {string.Join(", ", archetypes)}.");
            return new Result(true, resDir, archetypes, produced, warnings, null);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        Result Fail(string error) => new(false, null, Array.Empty<string>(), produced, warnings, error);
    }

    private static string BuildReadme(string pack, IReadOnlyList<string> archetypes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {pack} — FiveM add-on resource");
        sb.AppendLine();
        sb.AppendLine("Adds NEW spawnable asset(s) — no vanilla game file is replaced.");
        sb.AppendLine();
        sb.AppendLine("## Install");
        sb.AppendLine($"1. Copy the '{pack}' folder into your server's resources/ folder.");
        sb.AppendLine($"2. Add  ensure {pack}  to server.cfg (or start {pack}).");
        sb.AppendLine("3. Restart the resource / server.");
        sb.AppendLine();
        sb.AppendLine("## Spawn");
        sb.AppendLine("Each model is registered as its own archetype:");
        foreach (var a in archetypes)
            sb.AppendLine($"  - {a}");
        sb.AppendLine();
        sb.AppendLine("Spawn by name via CreateObject / a props spawner menu, place it in a");
        sb.AppendLine(".ymap, or register it with a housing/furniture script.");
        return sb.ToString();
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);
}
