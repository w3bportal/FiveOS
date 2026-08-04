// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using g3;

namespace FiveOS.Services;

/// <summary>
/// End to end: an unrigged garment mesh becomes a wearable GTA clothing file.
///
/// Import → auto-weight against the freemode body → build LODs → write a
/// skinned .ydd. The caller supplies a mesh authored around the ped in GTA
/// space; everything else is derived.
///
/// What this does NOT do, deliberately: it does not pack the result into an
/// addon resource. That means no .ymt / CPedVariationInfo, no drawable
/// numbering, no shop metadata — those are what the existing community packers
/// already automate well. The unsolved part was always the rigging.
/// </summary>
/// <summary>Thrown when a garment cannot be weighted because of how it sits
/// relative to the body. Carries the measured diagnostics so the caller can
/// show the user what is actually wrong rather than a bare failure.</summary>
public sealed class ClothingFitException : Exception
{
    public ClothingFitException(string message, IReadOnlyList<string> diagnostics) : base(message)
        => Diagnostics = diagnostics;

    public IReadOnlyList<string> Diagnostics { get; }
}

public static class ClothingBuilder
{
    public sealed class Request
    {
        /// <summary>Garment mesh: .fbx / .glb / .gltf / .obj / .dae.</summary>
        public required string MeshPath { get; init; }

        /// <summary>Component name, e.g. "jbib_042_u" — drives the drawable
        /// name, the dictionary hash and the output file name.</summary>
        public required string ComponentName { get; init; }

        /// <summary>Which body to weight against: "male" or "female". A garment
        /// must be authored and weighted separately per gender; their
        /// proportions differ enough that one set of weights will not do.</summary>
        public string Variant { get; init; } = "male";

        /// <summary>Texture names (no extension) for the ped shader.</summary>
        public SkinnedDrawableBuilder.Textures? Textures { get; init; }

        /// <summary>Off to emit only the High tier — which makes the garment
        /// vanish at distance in game, so it exists for diagnostics only.</summary>
        public bool GenerateLods { get; init; } = true;

        public GarmentSkinTransfer.Options? TransferOptions { get; init; }

        /// <summary>Called with (fraction 0..1, what it is doing) on the worker
        /// thread. A garment that sits badly on the body can take minutes, so
        /// this is how the caller shows that it is still working.</summary>
        public Action<double, string>? OnProgress { get; init; }

        /// <summary>Reduce an over-budget mesh instead of refusing it.</summary>
        public bool AutoDecimate { get; init; } = true;

        /// <summary>Rescale and recentre a garment that is plainly in the wrong
        /// units or nowhere near the ped, rather than refusing it. It gets the
        /// mesh close enough to weight; where exactly a bag or jacket should
        /// sit is still the author's call.</summary>
        public bool AutoFit { get; init; } = true;

        /// <summary>Rotation in degrees about X, Y and Z, applied about the
        /// garment's own centre after auto-fit. Nothing in the geometry says
        /// which way a garment is meant to face, so orientation cannot be
        /// inferred — it has to be given.</summary>
        public Vector3d Rotation { get; init; } = Vector3d.Zero;

        /// <summary>Metres to shift the garment after rotation. This is how a
        /// bag gets moved onto the back rather than through the chest.</summary>
        public Vector3d Offset { get; init; } = Vector3d.Zero;

        /// <summary>Extra scale on top of auto-fit, for fine adjustment.</summary>
        public double ScaleMultiplier { get; init; } = 1.0;

        /// <summary>Vertex budget for the full-detail tier. RAGE's hard ceiling
        /// is 65,535; vanilla clothing sits nearer 3,600, and staying well under
        /// keeps both the solve and the game fast.</summary>
        public int MaxVertices { get; init; } = 20000;
    }

    public sealed class Report
    {
        public required byte[] Ydd { get; init; }
        public required int SourceVertices { get; init; }
        public required int SourceTriangles { get; init; }
        public required int MatchedVertices { get; init; }
        public required int InpaintedVertices { get; init; }
        public required int IsolatedIslands { get; init; }
        public required IReadOnlyList<(int vertices, int triangles)> Lods { get; init; }
        public required IReadOnlyList<string> Warnings { get; init; }
    }

    public static Report Build(Request request)
    {
        var warnings = new List<string>();
        request.OnProgress?.Invoke(0.01, "reading the mesh");
        var (garment, uvs, cornerUv) = ImportGarment(request.MeshPath, warnings);

        if (garment.TriangleCount == 0)
            throw new InvalidOperationException("the garment mesh has no triangles");

        // Check this BEFORE doing any work. RAGE stores a geometry's vertex
        // count in 16 bits, so 65535 is a hard ceiling no amount of solving
        // gets around — and real vanilla clothing is nearer 3,600. Grinding
        // for minutes on a mesh that could never be exported is the worst
        // possible way to find that out.
        request.OnProgress?.Invoke(0.02, $"{garment.VertexCount:N0} vertices, {garment.TriangleCount:N0} triangles");
        int original = garment.VertexCount;
        if (original > request.MaxVertices)
        {
            if (!request.AutoDecimate)
                throw new InvalidOperationException(
                    $"This mesh has {original:N0} vertices. GTA clothing is limited to 65,535 per piece, and " +
                    "vanilla garments are nearer 3,600 — so it cannot be exported as-is. Turn on 'Reduce " +
                    "automatically', or decimate it yourself first.");

            request.OnProgress?.Invoke(0.03,
                $"reducing {original:N0} → about {request.MaxVertices:N0} vertices");
            // Boundaries are pinned during reduction, so hems, cuffs and open
            // edges keep their silhouette — the collapse happens in the
            // interior where it shows least.
            garment = GarmentLodBuilder.ReduceToVertexBudget(garment, request.MaxVertices);
            cornerUv = null;   // triangle ids no longer line up with the originals
            double kept = 100.0 * garment.VertexCount / original;
            warnings.Add($"Reduced from {original:N0} to {garment.VertexCount:N0} vertices ({kept:F1}% kept) to fit " +
                         "what GTA allows. Open edges were pinned, but at this ratio fine detail is lost — " +
                         "check the silhouette.");
            if (kept < 10)
                warnings.Add("That is a very heavy reduction. A mesh authored nearer game budget will look " +
                             "considerably better than one crushed down from a render-quality model.");
        }

        var donor = FreemodeDonorLoader.Load(request.Variant);

        // Compare the garment against the body BEFORE solving. "Nothing
        // matched" on its own leaves the user guessing; the numbers say
        // immediately whether it is scale, position, or axis convention.
        if (request.AutoFit) AutoFitToBody(garment, donor, warnings, request.OnProgress);
        ApplyManualTransform(garment, request, warnings);
        DescribeFit(garment, donor, warnings);
        var name = request.ComponentName;
        var textures = request.Textures ?? DefaultTextures(name);

        // Each LOD tier gets its own slice of the bar: the full-detail tier is
        // much the slowest, so it owns most of the range.
        var ratios = request.GenerateLods ? GarmentLodBuilder.DefaultRatios : new[] { 1.0 };
        var slices = ratios.Length == 3 ? new[] { 0.05, 0.72, 0.88, 0.98 } : new[] { 0.05, 0.98 };
        int tierIndex = 0;
        var opts = request.TransferOptions ?? new GarmentSkinTransfer.Options();
        if (request.OnProgress is not null)
            opts.OnProgress = (f, what) =>
            {
                int i = Math.Min(tierIndex, slices.Length - 2);
                double lo = slices[i], hi = slices[i + 1];
                string tier = ratios.Length == 3 ? $" (detail level {i + 1} of 3)" : "";
                request.OnProgress(lo + (hi - lo) * f, what + tier);
            };
        opts.OnTierComplete = () => tierIndex++;

        List<SkinnedDrawableBuilder.Lod> lods;
        GarmentSkinTransfer.Result? stats;
        try
        {
            lods = GarmentLodBuilder.Build(
                donor, garment, v => uvs(v), out stats,
                request.GenerateLods ? null : new[] { 1.0 }, opts, cornerUv);
        }
        catch (InvalidOperationException ex)
        {
            // Re-throw carrying the fit measurements — on their own the inner
            // messages say what failed but not why.
            throw new ClothingFitException(ex.Message, warnings);
        }
        request.OnProgress?.Invoke(0.99, "writing the file");

        if (lods.Count == 0)
            throw new ClothingFitException(
                "No weights could be generated — nothing on this garment lined up with the body.", warnings);
        if (lods.Count < 3 && request.GenerateLods)
            warnings.Add($"only {lods.Count} LOD tier(s) generated; the garment may pop or vanish at distance.");

        // Statistics come from the full-detail tier's own solve, which the LOD
        // builder already performed — re-running it here cost a second full
        // solve purely to fill three counters.
        stats ??= GarmentSkinTransfer.Transfer(donor, garment, request.TransferOptions);
        if (stats.IsolatedIslands() > 0)
            warnings.Add($"{stats.IsolatedIslands()} disconnected piece(s) had no confident match and inherited " +
                         "their nearest neighbour's weights — check buttons, straps and separate panels.");
        if (stats.UnconvergedSolves > 0)
            warnings.Add($"{stats.UnconvergedSolves} bone solve(s) hit the iteration cap without converging — " +
                         "those weights are an approximation; check deformation around them.");
        double inpaintedShare = stats.InpaintedCount / (double)Math.Max(garment.VertexCount, 1);
        if (inpaintedShare > 0.5)
            warnings.Add($"{inpaintedShare:P0} of the garment had no confident match against the body — " +
                         "it may be too far from the ped, mis-scaled, or in the wrong coordinate space.");

        var ydd = SkinnedDrawableBuilder.Build(name, lods, textures);
        return new Report
        {
            Ydd = ydd.Save(),
            SourceVertices = garment.VertexCount,
            SourceTriangles = garment.TriangleCount,
            MatchedVertices = stats.MatchedCount,
            InpaintedVertices = stats.InpaintedCount,
            IsolatedIslands = stats.IsolatedIslands(),
            Lods = lods.Select(l => (l.Vertices.Count, l.Indices.Count / 3)).ToList(),
            Warnings = warnings,
        };
    }

    /// <summary>Convenience wrapper that writes the result next to the source.</summary>
    public static Report BuildToFile(Request request, string outputPath)
    {
        var report = Build(request);
        // GetDirectoryName is null/empty for a bare file name.
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(outputPath, report.Ydd);
        return report;
    }

    /// <summary>
    /// Rescales and recentres a garment that is plainly not on the ped. Nearly
    /// every failure is a unit mismatch — a mesh authored in millimetres or
    /// centimetres against a body measured in metres — and the size ratio says
    /// exactly what to divide by. Rather than refuse and ask the user to go
    /// back to Blender, bring it onto the body and say what was changed.
    ///
    /// Placement is the honest limit: this centres the garment on the body,
    /// which is right for a torso piece and only approximate for a bag or a
    /// hip item. It gets weighting working; the author still decides where the
    /// thing actually hangs.
    /// </summary>
    private static void AutoFitToBody(DMesh3 garment, GarmentSkinTransfer.Donor donor,
                                      List<string> warnings, Action<double, string>? progress)
    {
        var (gLo, gHi) = MeshBounds(garment);
        var (bLo, bHi) = MeshBounds(donor.Mesh);
        double gDiag = (gHi - gLo).Length, bDiag = (bHi - bLo).Length;
        if (gDiag < 1e-9 || bDiag < 1e-9) return;

        double ratio = gDiag / bDiag;
        // A wearable garment spans roughly a tenth to two-thirds of the body's
        // overall diagonal. Outside that, the mesh is in different units.
        const double plausibleLow = 0.08, plausibleHigh = 1.2;
        double scale = 1.0;
        if (ratio > plausibleHigh || ratio < plausibleLow)
        {
            // Prefer a familiar unit conversion when one lands in range — a
            // clean /1000 reads far better in the report than /406.3.
            foreach (var candidate in new[] { 1000.0, 100.0, 39.3701, 12.0, 2.54 })
            {
                double r = ratio / candidate;
                if (r is >= plausibleLow and <= plausibleHigh) { scale = 1.0 / candidate; break; }
                r = ratio * candidate;
                if (r is >= plausibleLow and <= plausibleHigh) { scale = candidate; break; }
            }
            // No standard unit fits: fall back to a direct rescale onto a
            // typical garment proportion.
            if (Math.Abs(scale - 1.0) < 1e-12) scale = 0.35 / ratio;
        }

        var gMid = (gLo + gHi) * 0.5;
        var bMid = (bLo + bHi) * 0.5;
        bool needsMove = (gMid - bMid).Length > bDiag * 0.35 || Math.Abs(scale - 1.0) > 1e-9;
        if (!needsMove) return;

        progress?.Invoke(0.04, "fitting the garment to the body");
        foreach (int vid in garment.VertexIndices())
        {
            // Scale about the garment's own centre, then move that centre onto
            // the body's — order matters, or the scaling drags it further off.
            var p = (garment.GetVertex(vid) - gMid) * scale + bMid;
            garment.SetVertex(vid, p);
        }

        var what = Math.Abs(scale - 1.0) > 1e-9
            ? $"scaled by {scale:G4}" + (Math.Abs(scale - 0.001) < 1e-9 ? " (millimetres → metres)"
                : Math.Abs(scale - 0.01) < 1e-9 ? " (centimetres → metres)" : "")
            : "repositioned";
        warnings.Add($"Auto-fit: {what} and centred on the body. Check the placement — a bag or hip item may need " +
                     "moving in your 3D tool, this only centres it.");
    }

    /// <summary>
    /// Applies the author's rotation, offset and scale. Rotation happens about
    /// the garment's own centre so it spins in place rather than orbiting the
    /// origin, then the offset moves it — the order that matches how someone
    /// thinks about placing a garment on a body.
    /// </summary>
    private static void ApplyManualTransform(DMesh3 garment, Request request, List<string> warnings)
    {
        var rot = request.Rotation;
        var off = request.Offset;
        double scale = request.ScaleMultiplier;
        bool anyRot = Math.Abs(rot.x) > 1e-9 || Math.Abs(rot.y) > 1e-9 || Math.Abs(rot.z) > 1e-9;
        bool anyOff = off.Length > 1e-9;
        bool anyScale = Math.Abs(scale - 1.0) > 1e-9;
        if (!anyRot && !anyOff && !anyScale) return;

        var (lo, hi) = MeshBounds(garment);
        var centre = (lo + hi) * 0.5;

        double rx = rot.x * Math.PI / 180.0, ry = rot.y * Math.PI / 180.0, rz = rot.z * Math.PI / 180.0;
        double cx = Math.Cos(rx), sx = Math.Sin(rx);
        double cy = Math.Cos(ry), sy = Math.Sin(ry);
        double cz = Math.Cos(rz), sz = Math.Sin(rz);

        foreach (int vid in garment.VertexIndices())
        {
            var p = garment.GetVertex(vid) - centre;
            if (anyScale) p *= scale;
            // X, then Y, then Z — the order most 3D tools present.
            if (anyRot)
            {
                double y1 = p.y * cx - p.z * sx, z1 = p.y * sx + p.z * cx;
                double x2 = p.x * cy + z1 * sy, z2 = -p.x * sy + z1 * cy;
                double x3 = x2 * cz - y1 * sz, y3 = x2 * sz + y1 * cz;
                p = new Vector3d(x3, y3, z2);
            }
            garment.SetVertex(vid, p + centre + off);
        }

        var parts = new List<string>();
        if (anyRot) parts.Add($"rotated {rot.x:G4}°, {rot.y:G4}°, {rot.z:G4}°");
        if (anyScale) parts.Add($"scaled ×{scale:G4}");
        if (anyOff) parts.Add($"moved {off.x:G4}, {off.y:G4}, {off.z:G4} m");
        warnings.Add("Manual transform: " + string.Join("; ", parts) + ".");
    }

    private static (Vector3d min, Vector3d max) MeshBounds(DMesh3 m)
    {
        var lo = new Vector3d(double.MaxValue, double.MaxValue, double.MaxValue);
        var hi = new Vector3d(double.MinValue, double.MinValue, double.MinValue);
        foreach (int vid in m.VertexIndices())
        {
            var p = m.GetVertex(vid);
            lo.x = Math.Min(lo.x, p.x); lo.y = Math.Min(lo.y, p.y); lo.z = Math.Min(lo.z, p.z);
            hi.x = Math.Max(hi.x, p.x); hi.y = Math.Max(hi.y, p.y); hi.z = Math.Max(hi.z, p.z);
        }
        return (lo, hi);
    }

    /// <summary>
    /// Reports how the garment sits relative to the body. Almost every failure
    /// that isn't a broken mesh is one of three things — wrong scale, wrong
    /// place, or Y-up authored against a Z-up body — and all three are obvious
    /// from the bounds, so say so instead of asking the user to guess.
    /// </summary>
    private static void DescribeFit(DMesh3 garment, GarmentSkinTransfer.Donor donor, List<string> warnings)
    {
        var (gLo, gHi) = MeshBounds(garment);
        var (bLo, bHi) = MeshBounds(donor.Mesh);
        var gSize = gHi - gLo; var bSize = bHi - bLo;
        var gMid = (gLo + gHi) * 0.5; var bMid = (bLo + bHi) * 0.5;

        double gDiag = gSize.Length, bDiag = bSize.Length;
        double ratio = bDiag > 1e-9 ? gDiag / bDiag : 0;
        double offset = (gMid - bMid).Length;

        // A garment is a fraction of the body's overall size; wildly outside
        // that band means units, not styling.
        if (ratio > 4.0)
            warnings.Add($"The garment is about {ratio:F0}x the size of the whole body — it is almost certainly in " +
                         "different units (centimetres or inches rather than metres). Scale it down in your 3D tool.");
        else if (ratio < 0.02)
            warnings.Add($"The garment is only {ratio * 100:F1}% of the body's size — it is probably in the wrong " +
                         "units, or is a tiny detail piece rather than a garment.");

        if (offset > bDiag * 0.75)
            warnings.Add($"The garment's centre is {offset:F2} m from the body's — it is not sitting on the ped. " +
                         "Position it as if worn, then export again.");

        // The body is tall in Z. A garment taller in Y than Z, on a mesh of
        // roughly plausible size, is the classic Y-up export.
        if (ratio is > 0.02 and < 4.0 && gSize.y > gSize.z * 1.5 && bSize.z > bSize.y)
            warnings.Add("The garment looks taller front-to-back than top-to-bottom, which usually means it was " +
                         "exported Y-up while GTA is Z-up. Rotate it -90° about X, or re-export with Z-up.");

        warnings.Add($"Garment {gSize.x:F2} x {gSize.y:F2} x {gSize.z:F2} m, centre offset {offset:F2} m from the " +
                     $"body ({bSize.x:F2} x {bSize.y:F2} x {bSize.z:F2} m).");
    }

    private static SkinnedDrawableBuilder.Textures DefaultTextures(string component)
    {
        // Follows the vanilla naming so the pack's .ytd resolves by name:
        // jbib_042_u -> jbib_diff_042_a_uni
        var parts = component.Split('_');
        if (parts.Length >= 3)
        {
            string comp = parts[0], num = parts[1], suffix = parts[2];
            string uni = suffix.StartsWith("r", StringComparison.OrdinalIgnoreCase) ? "whi" : "uni";
            return new SkinnedDrawableBuilder.Textures(
                $"{comp}_diff_{num}_a_{uni}", $"{comp}_normal_{num}", $"{comp}_spec_{num}");
        }
        return new SkinnedDrawableBuilder.Textures(
            $"{component}_diff", $"{component}_normal", $"{component}_spec");
    }

    /// <summary>
    /// Imports the garment and welds it. Welding is not optional: an .fbx from
    /// most authoring tools arrives as triangle soup with every corner split,
    /// and the cotangent Laplacian the weight solve depends on needs real
    /// connectivity or it treats the mesh as thousands of isolated islands.
    /// </summary>
    private static (DMesh3 mesh, Func<int, Vector2f> uvs, List<Vector2f[]> cornerUv) ImportGarment(string path, List<string> warnings)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("garment mesh not found", path);

        using var ctx = new Assimp.AssimpContext();
        var scene = ctx.ImportFile(path,
            Assimp.PostProcessSteps.Triangulate |
            Assimp.PostProcessSteps.JoinIdenticalVertices |
            Assimp.PostProcessSteps.PreTransformVertices);

        if (scene?.Meshes is null || scene.MeshCount == 0)
            throw new InvalidOperationException($"no mesh found in {Path.GetFileName(path)}");

        var mesh = new DMesh3();
        var uvList = new List<Vector2f>();
        var weld = new Dictionary<(long, long, long), int>();
        var seenUv = new Dictionary<int, HashSet<(float, float)>>();
        // Per-triangle corner UVs, indexed by the id AppendTriangle hands back.
        // This is what lets the exporter re-split a seam that welding merged.
        var cornerUv = new List<Vector2f[]>();
        const double quantum = 1e5;   // ~0.01 mm buckets

        foreach (var m in scene.Meshes)
        {
            var localMap = new int[m.VertexCount];
            bool hasUv = m.HasTextureCoords(0);
            for (int i = 0; i < m.VertexCount; i++)
            {
                var v = m.Vertices[i];
                var key = ((long)Math.Round(v.X * quantum),
                           (long)Math.Round(v.Y * quantum),
                           (long)Math.Round(v.Z * quantum));
                if (!weld.TryGetValue(key, out int vid))
                {
                    vid = mesh.AppendVertex(new Vector3d(v.X, v.Y, v.Z));
                    weld[key] = vid;
                    var uv0 = hasUv ? m.TextureCoordinateChannels[0][i] : new Assimp.Vector3D(0, 0, 0);
                    while (uvList.Count <= vid) uvList.Add(new Vector2f(0, 0));
                    uvList[vid] = new Vector2f(uv0.X, 1f - uv0.Y);   // glTF/FBX V is flipped vs RAGE
                }
                var thisUv = hasUv ? m.TextureCoordinateChannels[0][i] : new Assimp.Vector3D(0, 0, 0);
                if (!seenUv.TryGetValue(vid, out var set)) seenUv[vid] = set = new HashSet<(float, float)>();
                set.Add((thisUv.X, 1f - thisUv.Y));
                localMap[i] = vid;
            }
            Vector2f UvAt(int i)
            {
                if (!hasUv) return new Vector2f(0, 0);
                var t = m.TextureCoordinateChannels[0][i];
                return new Vector2f(t.X, 1f - t.Y);
            }
            foreach (var f in m.Faces)
            {
                if (f.IndexCount != 3) continue;
                int tid = mesh.AppendTriangle(localMap[f.Indices[0]], localMap[f.Indices[1]], localMap[f.Indices[2]]);
                if (tid < 0) continue;                       // rejected (non-manifold)
                while (cornerUv.Count <= tid) cornerUv.Add(null!);
                cornerUv[tid] = new[] { UvAt(f.Indices[0]), UvAt(f.Indices[1]), UvAt(f.Indices[2]) };
            }

            if (!hasUv) warnings.Add($"'{m.Name}' has no texture coordinates; its UVs default to zero.");
        }

        int welded = scene.Meshes.Sum(m => m.VertexCount) - mesh.VertexCount;
        if (welded > 0) warnings.Add($"welded {welded} duplicate vertices to restore mesh connectivity.");

        // A welded vertex that saw more than one UV is a seam. Welding is
        // required for the solve, but carrying it into the output would tear
        // the texture there, so count them and say so.
        // KNOWN LIMITATION, not yet fixed: welding is required for the solve
        // (the Laplacian needs real connectivity) but the welded vertices are
        // also what gets exported, so a seam's two UVs collapse into one and
        // the texture tears along it. Detect and say so plainly rather than
        // let it surprise someone in game.
        // Welding is required for the solve — the Laplacian needs real
        // connectivity — but the seams it merges are re-split on export from
        // the per-corner UVs recorded above, so the texture stays intact.
        int seams = seenUv.Count(kv => kv.Value.Count > 1);
        if (seams > 0)
            warnings.Add($"{seams} UV seam vertices merged for the solve and re-split on export.");

        var uvArray = uvList.ToArray();
        return (mesh, vid => vid >= 0 && vid < uvArray.Length ? uvArray[vid] : new Vector2f(0, 0), cornerUv);
    }
}

internal static class TransferResultExtensions
{
    public static int IsolatedIslands(this GarmentSkinTransfer.Result r) => r.IsolatedComponentCount;
}
