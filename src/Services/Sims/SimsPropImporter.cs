// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FiveOS.Services.Sims;

/// <summary>
/// Sims 4 <c>.package</c> → a .glb the prop pipeline can convert, plus any
/// textures the package carried.
/// <para>
/// Same shape as <see cref="SimsEmoteImporter"/>: decode the Sims-specific
/// binary, write a neutral intermediate, and hand that to the pipeline that
/// already exists — no second model path to maintain.
/// </para>
/// </summary>
public static class SimsPropImporter
{
    public sealed record Result(
        bool Success,
        string? GlbPath,
        string? OutputDirectory,
        string Name,
        int MeshCount,
        int TriangleCount,
        IReadOnlyList<string> TexturePaths,
        IReadOnlyList<string> Warnings,
        string? Error = null)
    {
        public static Result Fail(string error, IReadOnlyList<string>? warnings = null) =>
            new(false, null, null, "", 0, 0, Array.Empty<string>(),
                warnings ?? Array.Empty<string>(), error);
    }

    /// <summary>What a package holds, without converting anything. Lets the UI
    /// say "this is a pose pack, open it in Emotes" instead of failing.</summary>
    public sealed record Survey(
        int MeshResources, int ClipResources, int TextureResources,
        IReadOnlyList<string> Contents);

    public static Survey Inspect(string packagePath)
    {
        using var pkg = DbpfPackage.Open(packagePath);
        var hist = pkg.TypeHistogram();
        var lines = hist
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{DbpfPackage.DescribeType(kv.Key)} × {kv.Value}")
            .ToList();

        int Count(params uint[] types) => types.Sum(t => hist.TryGetValue(t, out var n) ? n : 0);

        return new Survey(
            Count(DbpfPackage.TypeModl, DbpfPackage.TypeMlod, DbpfPackage.TypeGeom),
            Count(DbpfPackage.TypeClip),
            Count(DbpfPackage.TypeImg, DbpfPackage.TypeImgRle,
                  DbpfPackage.TypeImgRles, DbpfPackage.TypeImgOverlay),
            lines);
    }

    /// <summary>Convert the highest-detail mesh set in a package.</summary>
    /// <param name="outputDirectory">Where the .glb and textures land. A temp
    /// folder is used when null; the caller owns cleanup either way.</param>
    public static Result Import(string packagePath, string? outputDirectory = null)
    {
        var warnings = new List<string>();
        try
        {
            if (!File.Exists(packagePath)) return Result.Fail("Package not found.");

            using var pkg = DbpfPackage.Open(packagePath);
            var name = Path.GetFileNameWithoutExtension(packagePath);

            // Object meshes first (MODL/MLOD), then CAS parts (GEOM) — a
            // package with both is an object whose GEOM is a swatch thumbnail.
            var candidates = pkg.EnumerateByType(DbpfPackage.TypeModl)
                .Concat(pkg.EnumerateByType(DbpfPackage.TypeMlod))
                .Concat(pkg.EnumerateByType(DbpfPackage.TypeGeom))
                .ToList();

            if (candidates.Count == 0)
            {
                var survey = Inspect(packagePath);
                var what = survey.ClipResources > 0
                    ? $"This is an animation package ({survey.ClipResources} clips) — import it from the Emotes workspace instead."
                    : "No MODL, MLOD or GEOM mesh resources in this package.";
                return Result.Fail($"{what}\n\nContents: {string.Join(", ", survey.Contents)}");
            }

            // Each mesh resource is one LOD level; the densest is LOD 0.
            List<SimsMesh>? best = null;
            var bestTris = -1;
            foreach (var entry in candidates)
            {
                List<SimsMesh> decoded;
                try { decoded = SimsMeshDecoder.Decode(pkg.ReadResource(entry), warnings); }
                catch (Exception ex)
                {
                    warnings.Add($"{DbpfPackage.DescribeType(entry.Type)} 0x{entry.Instance:X16}: {ex.Message}");
                    continue;
                }

                // Shadow-caster groups are invisible stand-ins; converting them
                // buries a duplicate blob inside the real prop.
                var dropped = decoded.RemoveAll(m => m.ShadowOnly);
                if (dropped > 0) warnings.Add($"Skipped {dropped} shadow-only mesh group(s).");

                var tris = decoded.Sum(m => m.TriangleCount);
                if (tris > bestTris) { bestTris = tris; best = decoded; }
            }

            if (best == null || best.Count == 0 || bestTris <= 0)
                return Result.Fail(
                    "Found mesh resources but couldn't decode geometry from any of them. " +
                    "This is the part of the Sims format FiveOS hasn't been able to verify " +
                    "against a real object package yet.", warnings);

            var dir = outputDirectory ?? Path.Combine(
                Path.GetTempPath(), "fiveos_sims_prop_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            var glbPath = Path.Combine(dir, Sanitize(name) + ".glb");
            SimsGlbWriter.Write(glbPath, best);

            var textures = ExtractTextures(pkg, dir, warnings);
            if (textures.Count == 0)
                warnings.Add("No textures in this package — the prop converts untextured.");

            return new Result(true, glbPath, dir, name, best.Count, bestTris, textures, warnings);
        }
        catch (Exception ex)
        {
            return Result.Fail("Sims prop import failed: " + ex.Message, warnings);
        }
    }

    /// <summary>Sims textures are already DDS, which is what a .ytd wants — so
    /// they're written straight out with no re-encode. The RLE variants are the
    /// exception and are reported rather than silently written as broken DDS.</summary>
    private static List<string> ExtractTextures(DbpfPackage pkg, string dir, List<string> warnings)
    {
        var paths = new List<string>();
        var rleSkipped = 0;

        foreach (var type in new[] { DbpfPackage.TypeImg, DbpfPackage.TypeImgOverlay,
                                     DbpfPackage.TypeImgRle, DbpfPackage.TypeImgRles })
        {
            foreach (var entry in pkg.EnumerateByType(type))
            {
                byte[] data;
                try { data = pkg.ReadResource(entry); }
                catch (Exception ex)
                {
                    warnings.Add($"Texture 0x{entry.Instance:X16}: {ex.Message}");
                    continue;
                }

                if (data.Length < 4 || Encoding.ASCII.GetString(data, 0, 4) != "DDS ")
                {
                    // The RLE types pack DXT5 with a custom scheme; without an
                    // unpacker a straight write would produce a corrupt file.
                    rleSkipped++;
                    continue;
                }

                var path = Path.Combine(dir, $"texture_{entry.Instance:X16}.dds");
                try
                {
                    File.WriteAllBytes(path, data);
                    paths.Add(path);
                }
                catch (Exception ex) { warnings.Add($"Couldn't write {Path.GetFileName(path)}: {ex.Message}"); }
            }
        }

        if (rleSkipped > 0)
            warnings.Add($"Skipped {rleSkipped} RLE-packed texture(s) — that variant needs an unpacker FiveOS doesn't have yet.");
        return paths;
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) || c is ' ' or '\'' ? '_' : c);
        var s = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(s) ? "sims_prop" : s;
    }
}
