// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FiveOS.Services.Sims;

/// <summary>
/// Sims 4 CLIP → Emote tracks via glTF + world-delta retarget.
/// <para>
/// CLIP channels are bind deltas; <see cref="SimsGltfWriter"/> authors node
/// rest rotations + delta channels. Assimp must compose
/// <c>rest * channel</c> — we force that for the duration of the import.
/// </para>
/// </summary>
public static class SimsEmoteImporter
{
    public static AnimEmoteImporter.Result Import(string packagePath, int clipIndex, int fps = 30)
    {
        var warnings = new List<string>();
        string? tempDir = null;
        string? prevCompose = null;
        try
        {
            if (!File.Exists(packagePath))
                return AnimEmoteImporter.Result.Fail("Package not found.");

            using var pkg = DbpfPackage.Open(packagePath);
            var entries = pkg.EnumerateClips().Select(x => x.Clip).ToList();
            if (entries.Count == 0)
                return AnimEmoteImporter.Result.Fail("No CLIP animations in this package.");
            if (clipIndex < 0 || clipIndex >= entries.Count)
                return AnimEmoteImporter.Result.Fail("Clip index out of range.");

            var decoded = SimsClipDecoder.Decode(pkg.ReadResource(entries[clipIndex]));
            tempDir = Path.Combine(Path.GetTempPath(), "fiveos_sims_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var gltfPath = Path.Combine(tempDir, "clip.gltf");
            SimsGltfWriter.Write(gltfPath, decoded);

            // Sims glTF rest * channel = absolute local. Without this, Assimp's
            // heuristic often treats pose-at-frame-0 as "full local" and drops
            // the thigh bind flip / root upright rest.
            prevCompose = Environment.GetEnvironmentVariable("FIVEOS_FORCE_COMPOSE");
            Environment.SetEnvironmentVariable("FIVEOS_FORCE_COMPOSE", "1");

            var importer = new AnimEmoteImporter();
            var res = importer.Import(gltfPath, fps);
            if (!res.Success)
                return res;

            warnings.AddRange(res.Warnings);
            warnings.Add(
                $"Sims CLIP '{decoded.Name}': bind+delta → glTF → world-delta retarget " +
                $"({res.MappedBones.Count} bones).");

            return new AnimEmoteImporter.Result(
                true,
                string.IsNullOrWhiteSpace(decoded.Name) ? res.ClipName : decoded.Name,
                res.Frames,
                res.Fps,
                res.DurationSeconds,
                AnimEmoteImporter.RigKind.Generic,
                Retargeted: true,
                res.Tracks,
                res.MappedBones,
                res.UnmappedBones,
                warnings,
                null,
                res.RootMotion);
        }
        catch (Exception ex)
        {
            return AnimEmoteImporter.Result.Fail("Sims import failed: " + ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FIVEOS_FORCE_COMPOSE", prevCompose);
            if (tempDir != null)
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }
    }
}
