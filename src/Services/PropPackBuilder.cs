// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using CodeWalker.GameFiles;

namespace FiveOS.Services;

/// <summary>
/// Compiles a populated <see cref="PropPackSession"/> into a single
/// FiveM resource matching the conventional housing-pack layout:
///
///   &lt;packname&gt;/
///     fxmanifest.lua          (this_is_a_map; files{}; DLC_ITYP_REQUEST)
///     README.md
///     stream/
///       prop1.ydr
///       prop2.ydr
///       ...
///       &lt;packname&gt;.ytyp     (merged archetype dictionary)
///
/// Per-prop .ytyps coming out of single-asset conversion are collapsed
/// into one big &lt;packname&gt;.ytyp by shelling out to the engine's
/// <c>merge-pack</c> subcommand, which loads each .ydr's embedded
/// Drawable bounding info and writes a single CMapTypes node containing
/// every archetype. Housing scripts and map editors expect this shape
/// (one ytyp per resource), so leaving the per-prop ytyps would break
/// drag-and-drop into existing housing-script furniture flows.
///
/// Honours the user's output settings the same way single-prop convert
/// does — single-zip drops a .zip in the configured downloads folder,
/// server modes copy the resource into the configured server folder.
/// </summary>
public static class PropPackBuilder
{
    public sealed record BuildResult(
        bool Success, string? ResultPath, string? Error, EngineRunner.OutputMode Mode,
        string? Warning = null);

    /// <param name="fallbackLodDist">Archetype cull distance used only for a
    /// .ydr that carries no LodDistVlow of its own — merge-pack prefers each
    /// drawable's embedded value so props keep the draw distance they were
    /// converted with.</param>
    public static BuildResult Build(PropPackSession session, double fallbackLodDist = 500d)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        if (session.Entries.Count == 0)
            return new BuildResult(false, null, "Pack is empty — convert at least one prop first.", EngineRunner.OutputMode.SingleZip);

        var packSafeName = Sanitize(session.PackName);
        if (string.IsNullOrEmpty(packSafeName)) packSafeName = "props_pack";

        // Stage the merged resource under a fresh temp dir, then deliver
        // it via whatever the user's output settings dictate. Same shape
        // as EngineRunner so the success-screen handling on the view
        // side stays one code path.
        var workDir = Path.Combine(Path.GetTempPath(), "FiveOS", "pack-build-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        var resourceDir = Path.Combine(workDir, packSafeName);
        var streamDir = Path.Combine(resourceDir, "stream");
        Directory.CreateDirectory(streamDir);

        var copied = new List<string>();
        // Maps each session entry to the archetype name actually written
        // to disk after the stem-disambiguation pass below. Housing-catalog
        // emission needs the on-disk archetype name (= ytyp archetype hash
        // source), not the original asset name — those can diverge when
        // the same asset is added twice and gets a "-2" suffix.
        var catalogMappings = new List<HousingCatalogEmitter.MappedEntry>();
        try
        {
            // Roll up every entry's stream/* into the merged stream dir,
            // EXCEPT the per-prop .ytyp files — those get replaced by a
            // single merged <packname>.ytyp built from each ydr's embedded
            // Drawable below.
            //
            // We process one slot at a time and pick a single unique stem
            // per slot, then rename every file in that slot to share it
            // (ydr + ybn + ycd). This guarantees:
            //   • every YDR file basename is unique → RAGE's streaming
            //     module gets a distinct asset name hash per archetype
            //   • paired sibling files (.ybn, .ycd) keep the same stem
            //     as their .ydr, so RAGE's physicsDictionary lookup
            //     still resolves to the right collision data
            //   • the YDR's INTERNAL Drawable.Name is rewritten to match
            //     the new filename. Without that, RAGE's resource cache
            //     keys two renamed copies of the same prop to the same
            //     in-memory drawable (the Name baked at convert time),
            //     and the second copy renders the first one's geometry.
            var stemSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in session.Entries)
            {
                var srcStream = Path.Combine(entry.SlotDir, "stream");
                if (!Directory.Exists(srcStream)) continue;

                // Original stem from the slot's .ydr (falls back to first
                // stream file if for some reason there is no .ydr — should
                // never happen for prop entries, but cheap to guard).
                string? originalStem = Directory.EnumerateFiles(srcStream, "*.ydr")
                    .Select(Path.GetFileNameWithoutExtension)
                    .FirstOrDefault();
                if (string.IsNullOrEmpty(originalStem))
                {
                    originalStem = Directory.EnumerateFiles(srcStream)
                        .Select(Path.GetFileNameWithoutExtension)
                        .FirstOrDefault();
                }
                if (string.IsNullOrEmpty(originalStem)) continue;

                // Slot name is already disambiguated by PropPackSession
                // (prop1, prop1-2, prop1-3, ...), so it's our preferred
                // canonical stem. Still re-check against stemSeen in case
                // the user assembled a session that re-uses a slot name
                // across re-adds — unlikely but cheap to defend against.
                var baseStem = Sanitize(entry.SlotName);
                if (string.IsNullOrEmpty(baseStem)) baseStem = Sanitize(entry.AssetName);
                if (string.IsNullOrEmpty(baseStem)) baseStem = "prop";
                var newStem = baseStem;
                int n = 2;
                while (!stemSeen.Add(newStem))
                {
                    newStem = $"{baseStem}-{n}"; n++;
                }

                // Stash the final archetype name so the housing-catalog
                // emitter below references what RAGE actually streams,
                // not what the user typed in the pack panel.
                catalogMappings.Add(new HousingCatalogEmitter.MappedEntry(entry, newStem));

                foreach (var src in Directory.EnumerateFiles(srcStream))
                {
                    var basename = Path.GetFileName(src);
                    // Per-prop ytyps get dropped here. The merge-pack step
                    // below rebuilds a single <pack>.ytyp from the .ydrs.
                    if (basename.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var ext = Path.GetExtension(basename);
                    var stem = Path.GetFileNameWithoutExtension(basename);
                    // Files whose stem matches the slot's dominant stem
                    // get renamed in lockstep (.ydr + .ybn + .ycd). Files
                    // with an unrelated stem (shared txd, weapon metas if
                    // they ever land here) are copied as-is.
                    var newName = stem.Equals(originalStem, StringComparison.OrdinalIgnoreCase)
                        ? newStem + ext
                        : basename;
                    var dst = Path.Combine(streamDir, newName);
                    File.Copy(src, dst, overwrite: true);
                    copied.Add(newName);

                    if (ext.Equals(".ydr", StringComparison.OrdinalIgnoreCase) &&
                        !newStem.Equals(originalStem, StringComparison.OrdinalIgnoreCase))
                    {
                        RewriteYdrInternalName(dst, newStem);
                    }
                }
            }

            // Build the merged ytyp by shelling out to the engine. The
            // engine reads every .ydr in streamDir, extracts its embedded
            // Drawable bbox/sphere, and writes one CMapTypes node listing
            // every archetype under the pack name's hash.
            var mergedYtypName = packSafeName + ".ytyp";
            var mergedYtypPath = Path.Combine(streamDir, mergedYtypName);
            var mergeResult = RunMergePack(packSafeName, streamDir, mergedYtypPath, fallbackLodDist);
            if (!mergeResult.Success)
            {
                return new BuildResult(false, null,
                    $"merge-pack failed: {mergeResult.Error}",
                    EngineRunner.OutputMode.SingleZip);
            }
            copied.Add(mergedYtypName);

            File.WriteAllText(
                Path.Combine(resourceDir, "fxmanifest.lua"),
                BuildManifest(packSafeName, session.PackName, mergedYtypName),
                new UTF8Encoding(false));

            // Drop the per-script housing catalog snippets next to the
            // streamed assets. Three of the five target scripts use live
            // in-game preview (no thumbnails needed); the other two ship
            // their own greenscreen generators. So this writes Lua/JSON
            // only — see HousingCatalogEmitter for the per-script formats.
            HousingCatalogEmitter.Emit(
                resourceDir,
                packDisplayName: session.PackName,
                packSafeName: packSafeName,
                catalogMappings);

            File.WriteAllText(
                Path.Combine(resourceDir, "README.md"),
                BuildReadme(packSafeName, session, copied, catalogMappings),
                new UTF8Encoding(false));

            // Deliver via the same strategies EngineRunner uses for a
            // single-prop conversion — single-zip default, server modes
            // when configured.
            if (UserSettings.IsServerModeActive())
            {
                var serverFolder = UserSettings.LoadServerResourceFolder()!;
                var layout = UserSettings.LoadServerLayout();
                if (layout == ServerLayout.PerAsset)
                    return DeliverToServerPerAsset(resourceDir, serverFolder, packSafeName);
                return DeliverToServerShared(resourceDir, serverFolder);
            }
            return DeliverAsZip(resourceDir, packSafeName);
        }
        catch (Exception ex)
        {
            return new BuildResult(false, null, $"Pack build failed: {ex.Message}", EngineRunner.OutputMode.SingleZip);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* swallow */ }
        }
    }

    // ───────────────────────── delivery ─────────────────────────

    private static BuildResult DeliverAsZip(string resourceDir, string packName)
    {
        var dest = UserSettings.ResolveSingleOutputFolder();
        Directory.CreateDirectory(dest);
        var zipPath = UniquePath(Path.Combine(dest, $"{packName}.zip"));
        ZipFile.CreateFromDirectory(resourceDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        return new BuildResult(true, zipPath, null, EngineRunner.OutputMode.SingleZip);
    }

    private static BuildResult DeliverToServerPerAsset(string resourceDir, string serverFolder, string packName)
    {
        Directory.CreateDirectory(serverFolder);
        var dest = UniquePath(Path.Combine(serverFolder, packName));
        CopyDirectory(resourceDir, dest);
        return new BuildResult(true, dest, null, EngineRunner.OutputMode.ServerPerAsset);
    }

    private static BuildResult DeliverToServerShared(string resourceDir, string serverFolder)
    {
        Directory.CreateDirectory(serverFolder);
        var srcStream = Path.Combine(resourceDir, "stream");
        var dstStream = Path.Combine(serverFolder, "stream");
        Directory.CreateDirectory(dstStream);

        // The shared folder accumulates every pack's stream/ and loads all
        // ytyps via the wildcard DLC_ITYP_REQUEST. If this pack overwrites a
        // same-named asset from an EARLIER pack, that pack's .ytyp still
        // declares the same archetype hash with the OLD drawable's bounds —
        // stem disambiguation only runs within one pack build, so cross-pack
        // collisions must at least be surfaced, not silently absorbed.
        var overwritten = new List<string>();
        if (Directory.Exists(srcStream))
        {
            foreach (var f in Directory.EnumerateFiles(srcStream))
            {
                var name = Path.GetFileName(f);
                var dst = Path.Combine(dstStream, name);
                if (File.Exists(dst) && !name.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase))
                    overwritten.Add(name);
                File.Copy(f, dst, overwrite: true);
            }
        }
        // Re-use EngineRunner's shared-manifest writer via a tiny shim:
        // we generate our own manifest by enumerating stream/ contents.
        RewriteSharedFxManifest(serverFolder);

        string? warning = null;
        if (overwritten.Count > 0 && Directory.Exists(srcStream))
        {
            var thisYtyp = Directory.EnumerateFiles(srcStream, "*.ytyp")
                .Select(Path.GetFileName).FirstOrDefault();
            var otherYtyps = Directory.EnumerateFiles(dstStream, "*.ytyp")
                .Select(Path.GetFileName)
                .Where(n => !string.Equals(n, thisYtyp, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (otherYtyps.Count > 0)
            {
                var shown = string.Join(", ", overwritten.Take(5));
                if (overwritten.Count > 5) shown += ", …";
                warning =
                    $"{overwritten.Count} streamed file(s) replaced same-named assets already in the shared stream folder ({shown}). " +
                    $"Other pack ytyp(s) there ({string.Join(", ", otherYtyps)}) may still declare those archetypes with the OLD bounds — " +
                    "re-finalize or remove the affected pack(s), or rename the colliding props.";
            }
        }
        return new BuildResult(true, dstStream, null, EngineRunner.OutputMode.ServerShared, warning);
    }

    // ───────────────────────── helpers ─────────────────────────

    internal static string BuildManifest(string safeName, string displayName, string mergedYtypName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("fx_version 'cerulean'");
        sb.AppendLine("game { 'gta5' }");
        sb.AppendLine();
        sb.AppendLine("author 'FiveOS'");
        sb.AppendLine($"description '{EscapeSq(displayName)}'");
        sb.AppendLine("version '1.0.0'");
        sb.AppendLine();
        // this_is_a_map marks the resource as a streamed-asset package so
        // FiveM loads it before scripts on the server boot. Housing packs
        // all use this — without it the ytyp may register too late and
        // archetype lookups from a furniture menu race the prop stream.
        sb.AppendLine("this_is_a_map 'yes'");
        sb.AppendLine();
        sb.AppendLine("files {");
        sb.AppendLine($"    'stream/{mergedYtypName}'");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine($"data_file 'DLC_ITYP_REQUEST' 'stream/{mergedYtypName}'");
        sb.AppendLine();
        sb.AppendLine("lua54 'yes'");
        return sb.ToString();
    }

    private static string BuildReadme(
        string safeName,
        PropPackSession session,
        IReadOnlyList<string> streamFiles,
        IReadOnlyList<HousingCatalogEmitter.MappedEntry> catalog)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {session.PackName}");
        sb.AppendLine();
        sb.AppendLine($"FiveM prop pack built with **FiveOS** on {DateTime.Now:yyyy-MM-dd}.");
        sb.AppendLine();
        sb.AppendLine("## Contents");
        sb.AppendLine();
        sb.AppendLine($"- **Props:** {session.Entries.Count}");
        sb.AppendLine($"- **Archetype dictionary:** `stream/{safeName}.ytyp`");
        sb.AppendLine($"- **Housing catalogs:** `housing/` (ps-housing, qbx_properties, loaf_housing, qs-housing, nolag_properties)");
        sb.AppendLine();
        sb.AppendLine("| Archetype | Label | Category | Price | Size |");
        sb.AppendLine("|-----------|-------|----------|------:|-----:|");
        foreach (var m in catalog)
            sb.AppendLine($"| `{m.ArchetypeName}` | {m.Entry.Label} | {m.Entry.Category} | {m.Entry.Price} | {m.Entry.SizeDisplay} |");
        sb.AppendLine();
        sb.AppendLine("## Installation");
        sb.AppendLine();
        sb.AppendLine($"1. Drop the `{safeName}` folder into your server's `resources/` directory.");
        sb.AppendLine($"2. Add `ensure {safeName}` to `server.cfg`.");
        sb.AppendLine("3. Restart the server (or run `start " + safeName + "` from rcon).");
        sb.AppendLine();
        sb.AppendLine("Each prop is spawnable by its archetype name via `CreateObject`, `.ymap`");
        sb.AppendLine("placement, or any housing/map-editor script that consumes ytyp archetypes.");
        sb.AppendLine();
        sb.AppendLine("## Housing-script integration");
        sb.AppendLine();
        sb.AppendLine("Snippets in `housing/` register every prop with the most common FiveM");
        sb.AppendLine("housing systems. Pick the one that matches your server:");
        sb.AppendLine();
        sb.AppendLine("- **ps-housing** — append `housing/ps-housing.snippet.lua` into");
        sb.AppendLine("  `ps-housing/shared/config.lua` (Config.Furnitures table).");
        sb.AppendLine("- **qbx_properties** — append `housing/qbx_properties.snippet.lua` into");
        sb.AppendLine("  `qbx_properties/config/client.lua` (furniture table).");
        sb.AppendLine("- **loaf_housing** — append `housing/loaf_housing.snippet.lua` into");
        sb.AppendLine("  `loaf_housing/furniture.lua`.");
        sb.AppendLine("- **qs-housing** — bulk-import `housing/qs-housing.import.json` from");
        sb.AppendLine("  Quasar's in-game housing creator. Quasar generates thumbnails itself.");
        sb.AppendLine("- **nolag_properties** — drop `housing/nolag_properties/" + safeName + ".lua`");
        sb.AppendLine("  into `nolag_properties/custom/furniture/`. It self-registers on resource start.");
        sb.AppendLine();
        sb.AppendLine("`housing/catalog.json` is a universal source-of-truth dump for any custom");
        sb.AppendLine("integration that wants to read the pack without parsing Lua.");
        sb.AppendLine();
        sb.AppendLine("## Troubleshooting");
        sb.AppendLine();
        sb.AppendLine("- **\"Model #### does not exist (not in cd image)\"** — the resource isn't");
        sb.AppendLine("  started, or the prop name typed into the housing menu doesn't match the");
        sb.AppendLine("  archetype name. Check the table above for the exact spelling.");
        sb.AppendLine("- **Prop spawns but has no collision** — the embedded bound failed to bind.");
        sb.AppendLine("  Re-convert that prop with collision turned ON in FiveOS.");
        return sb.ToString();
    }

    /// <summary>Shell out to the bundled engine's <c>merge-pack</c>
    /// subcommand to produce a single ytyp covering every .ydr in the
    /// staging stream directory. Synchronous because finalize is itself
    /// already on a foreground UI action and the engine returns in
    /// sub-second time even for hundreds of props (it only reads each
    /// .ydr's header, not the texture pages). Shared with
    /// <see cref="AddonResourceBuilder"/>, whose add-on ytyp is the same
    /// merge over a single-model stream dir.</summary>
    internal static (bool Success, string? Error) RunMergePack(
        string packName, string streamDir, string outYtypPath, double fallbackLodDist)
    {
        if (!EngineRunner.IsEngineAvailable())
            return (false, $"Engine binary not found at {EngineRunner.EnginePath}. Re-install FiveOS.");

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = EngineRunner.EnginePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("merge-pack");
            psi.ArgumentList.Add("--pack-name"); psi.ArgumentList.Add(packName);
            psi.ArgumentList.Add("--stream-dir"); psi.ArgumentList.Add(streamDir);
            psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(outYtypPath);
            psi.ArgumentList.Add("--lod-dist");
            psi.ArgumentList.Add(((float)fallbackLodDist).ToString(System.Globalization.CultureInfo.InvariantCulture));

            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd();
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                return (false, string.IsNullOrWhiteSpace(stderr) ? $"engine exit {proc.ExitCode}" : stderr.Trim());
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"failed to spawn engine: {ex.Message}");
        }
    }

    private static void RewriteSharedFxManifest(string serverFolder)
    {
        var streamDir = Path.Combine(serverFolder, "stream");
        var entries = Directory.Exists(streamDir)
            ? Directory.EnumerateFiles(streamDir)
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()!
            : new List<string?>();

        var sb = new StringBuilder();
        sb.AppendLine("fx_version 'cerulean'");
        sb.AppendLine("game 'gta5'");
        sb.AppendLine();
        sb.AppendLine("-- Generated by FiveOS — assets are listed individually so RAGE");
        sb.AppendLine("-- streams them. Re-run any conversion to refresh this list.");
        sb.AppendLine();
        // this_is_a_map marks the resource as a streamed-asset package so
        // FiveM loads it before scripts (ytyp registration mustn't race
        // archetype lookups) and honours .ymap files dropped into stream/.
        // BuildManifest and the engine's per-resource writer set it too.
        sb.AppendLine("this_is_a_map 'yes'");
        sb.AppendLine();
        sb.AppendLine("files {");
        foreach (var name in entries)
            sb.AppendLine($"    'stream/{name}',");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("data_file 'DLC_ITYP_REQUEST' 'stream/*.ytyp'");
        File.WriteAllText(Path.Combine(serverFolder, "fxmanifest.lua"), sb.ToString());
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(source))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.EnumerateDirectories(source))
            CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    private static string UniquePath(string p)
    {
        if (!File.Exists(p) && !Directory.Exists(p)) return p;
        var dir = Path.GetDirectoryName(p)!;
        var name = Path.GetFileNameWithoutExtension(p);
        var ext = Path.GetExtension(p);
        for (int n = 2; ; n++)
        {
            var cand = Path.Combine(dir, $"{name}-{n}{ext}");
            if (!File.Exists(cand) && !Directory.Exists(cand)) return cand;
        }
    }

    private static string Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ' || ch == '-') sb.Append('_');
        }
        return sb.ToString();
    }

    private static string EscapeSq(string s) => (s ?? "").Replace("'", "\\'");

    /// <summary>Load a YDR, rewrite its embedded Drawable.Name (and
    /// embedded BoundComposite OwnerName, when present) to a new value,
    /// then re-save in place. Called when the pack builder has to
    /// disambiguate two slots whose source files share a stem — the on-disk
    /// file gets a new basename, and this rewrites the name baked INSIDE
    /// the YDR so RAGE's resource cache doesn't fold the two renamed
    /// archetypes back onto the same in-memory drawable.
    ///
    /// Best-effort: any load/save failure is swallowed and logged to
    /// stderr (the renamed file still ships — the worst case is the old
    /// behaviour of the two YDRs aliasing in RAGE, which is what we were
    /// trying to prevent but is no regression).</summary>
    internal static void RewriteYdrInternalName(string ydrPath, string newName)
    {
        try
        {
            var bytes = File.ReadAllBytes(ydrPath);
            var ydr = new YdrFile();
            ydr.Load(bytes);
            ydr.Name = newName;
            var d = ydr.Drawable;
            if (d is not null)
            {
                d.Name = newName;
                // BoundComposite (the container wrapping per-mesh bounds
                // when collision is embedded) carries its own owner-name
                // string in CW.Core. Match it to the drawable so the
                // physics-archetype binding logs cleanly in CW and any
                // downstream tool that reads it.
                if (d.Bound is not null)
                    d.Bound.OwnerName = newName;
            }
            var newBytes = ydr.Save();
            File.WriteAllBytes(ydrPath, newBytes);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pack-build] ydr rename to '{newName}' failed for {Path.GetFileName(ydrPath)}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
