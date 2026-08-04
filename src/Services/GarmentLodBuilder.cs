// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Linq;
using g3;

namespace FiveOS.Services;

/// <summary>
/// Produces the High / Med / Low tiers a ped clothing drawable needs. Shipping
/// only the High tier is the single most common defect in custom clothing —
/// the garment simply vanishes once the player walks a few metres away, because
/// the engine has nothing to draw at that distance and will not generate it.
///
/// Each tier is decimated from the original and then re-weighted against the
/// donor rather than inheriting interpolated weights from the tier above. That
/// costs an extra transfer (tens of milliseconds at these sizes) and avoids
/// compounding error down the chain — and it sidesteps the known weakness of
/// plain quadric collapse, which has no bone-aware error metric and visibly
/// distorts skinning past roughly 70% reduction.
/// </summary>
public static class GarmentLodBuilder
{
    /// <summary>Triangle budget per tier, as a fraction of the original. Med at
    /// half and Low at a tenth follows what vanilla clothing ships.</summary>
    public static readonly double[] DefaultRatios = { 1.0, 0.5, 0.1 };

    /// <summary>Below this the silhouette collapses and decimating further buys
    /// nothing worth the artefacts.</summary>
    private const int MinTriangles = 24;

    /// <summary>
    /// Builds the LOD set for a garment. <paramref name="uv"/> supplies texture
    /// coordinates per garment vertex; decimated tiers inherit them from the
    /// nearest surviving original vertex.
    /// </summary>
    public static List<SkinnedDrawableBuilder.Lod> Build(
        GarmentSkinTransfer.Donor donor,
        DMesh3 garment,
        Func<int, Vector2f> uv,
        IReadOnlyList<double>? ratios = null,
        GarmentSkinTransfer.Options? options = null,
        IReadOnlyList<Vector2f[]>? cornerUv = null)
        => Build(donor, garment, uv, out _, ratios, options, cornerUv);

    /// <summary>As above, also handing back the full-detail tier's transfer
    /// result so the caller can report match statistics without paying for a
    /// second full-resolution solve.</summary>
    public static List<SkinnedDrawableBuilder.Lod> Build(
        GarmentSkinTransfer.Donor donor,
        DMesh3 garment,
        Func<int, Vector2f> uv,
        out GarmentSkinTransfer.Result? fullDetailStats,
        IReadOnlyList<double>? ratios = null,
        GarmentSkinTransfer.Options? options = null,
        IReadOnlyList<Vector2f[]>? cornerUv = null)
    {
        var wanted = ratios ?? DefaultRatios;
        var lods = new List<SkinnedDrawableBuilder.Lod>();
        var sourceTree = new DMeshAABBTree3(garment, autoBuild: true);
        fullDetailStats = null;

        foreach (var ratio in wanted)
        {
            bool fullDetail = ratio >= 0.999;
            var tier = fullDetail ? new DMesh3(garment) : Decimate(garment, ratio);
            if (tier.TriangleCount == 0) continue;

            var weights = GarmentSkinTransfer.Transfer(donor, tier, options);
            if (fullDetail) fullDetailStats ??= weights;
            // Only the full-detail tier can re-split seams — the corner UVs are
            // indexed by the ORIGINAL triangles, which decimation destroys.
            var lod = fullDetail && cornerUv is not null
                ? AssembleWithSeams(tier, weights, cornerUv)
                : Assemble(tier, weights, sourceTree, garment, uv);
            // A dropped tier must not let a coarser one slide into its slot —
            // promoting Med into High would ship the low-poly mesh as the
            // close-up model. If the full-detail tier fails, nothing after it
            // is usable either.
            if (lod is null)
            {
                if (fullDetail) break;
                continue;
            }
            lods.Add(lod);
            options?.OnTierComplete?.Invoke();

            // Once a tier bottoms out there is nothing left to coarsen.
            if (tier.TriangleCount <= MinTriangles) break;
        }
        return lods;
    }

    /// <summary>Reduces a mesh to a target vertex count, pinning open
    /// boundaries so hems, cuffs and edges keep their shape. Returns the
    /// original when it already fits.</summary>
    public static DMesh3 ReduceToVertexBudget(DMesh3 source, int maxVertices)
    {
        if (source.VertexCount <= maxVertices) return source;
        // Triangles run roughly 2x vertices on a closed surface; aim by ratio
        // and let the reducer land where it lands.
        double ratio = (double)maxVertices / source.VertexCount;
        var reduced = Decimate(source, ratio);
        // One corrective pass if the estimate overshot.
        if (reduced.VertexCount > maxVertices)
            reduced = Decimate(reduced, (double)maxVertices / reduced.VertexCount * 0.95);
        return reduced;
    }

    private static DMesh3 Decimate(DMesh3 source, double ratio)
    {
        var copy = new DMesh3(source);
        int target = Math.Max(MinTriangles, (int)(copy.TriangleCount * ratio));
        if (target >= copy.TriangleCount) return copy;

        var reducer = new Reducer(copy);
        // Keep the hem and cuffs where they are — a garment's open boundaries
        // are its silhouette, and letting them wander reads as the sleeve
        // shrinking as the player walks away.
        var constraints = new MeshConstraints();
        int pinned = 0;
        foreach (int eid in copy.BoundaryEdgeIndices())
        {
            var ev = copy.GetEdgeV(eid);
            constraints.SetOrUpdateVertexConstraint(ev.a, VertexConstraint.Pinned);
            constraints.SetOrUpdateVertexConstraint(ev.b, VertexConstraint.Pinned);
            pinned++;
        }
        if (pinned > 0) reducer.SetExternalConstraints(constraints);

        reducer.ReduceToTriangleCount(target);
        // Deliberately NOT compacted: g3's CompactInPlace throws on a mesh the
        // reducer has just torn holes in, and everything downstream walks
        // VertexIndices() and remaps, so sparse ids are harmless.
        return copy;
    }

    private static SkinnedDrawableBuilder.Lod? Assemble(
        DMesh3 tier, GarmentSkinTransfer.Result weights,
        DMeshAABBTree3 sourceTree, DMesh3 source, Func<int, Vector2f> uv)
    {
        var normals = VertexNormals(tier);
        var verts = new List<SkinnedDrawableBuilder.Vertex>();
        var remap = new Dictionary<int, int>();

        // UVs first — tangents are derived from them, and the ped shader is
        // normal-mapped, so a garment shipped with placeholder tangents lights
        // incorrectly no matter how good its weights are.
        var uvPerVertex = new Vector2f[tier.MaxVertexID];
        foreach (int vid in tier.VertexIndices())
            uvPerVertex[vid] = NearestUv(tier.GetVertex(vid), sourceTree, source, uv);
        var tangents = VertexTangents(tier, normals, uvPerVertex);

        foreach (int vid in tier.VertexIndices())
        {
            var influences = weights.Weights[vid];
            if (influences.Count == 0) continue;      // nothing drives it; drop it

            var p = tier.GetVertex(vid);
            var texcoord = uvPerVertex[vid];
            var t = tangents[vid];
            var v = new SkinnedDrawableBuilder.Vertex
            {
                Position = new SharpDX.Vector3((float)p.x, (float)p.y, (float)p.z),
                Normal = new SharpDX.Vector3((float)normals[vid].x, (float)normals[vid].y, (float)normals[vid].z),
                Uv0 = new SharpDX.Vector2(texcoord.x, texcoord.y),
                Tangent = t,
            };
            SkinnedDrawableBuilder.PackWeights(influences, v.BoneIndices, v.Weights);
            remap[vid] = verts.Count;
            verts.Add(v);
        }

        if (verts.Count == 0 || verts.Count > ushort.MaxValue) return null;

        var indices = new List<ushort>();
        foreach (int tid in tier.TriangleIndices())
        {
            var t = tier.GetTriangle(tid);
            if (!remap.TryGetValue(t.a, out int a) ||
                !remap.TryGetValue(t.b, out int b) ||
                !remap.TryGetValue(t.c, out int c)) continue;
            indices.Add((ushort)a); indices.Add((ushort)b); indices.Add((ushort)c);
        }
        if (indices.Count == 0) return null;

        return new SkinnedDrawableBuilder.Lod { Vertices = verts, Indices = indices };
    }

    /// <summary>
    /// Assembles the full-detail tier, re-splitting the UV seams that welding
    /// merged. Welding is necessary for the solve — the Laplacian needs real
    /// connectivity — but exporting the welded vertices directly would give a
    /// seam one UV where it needs two, and the texture tears along it.
    ///
    /// So an output vertex is keyed by (welded vertex, UV): a vertex used with
    /// one UV stays single, and one used with several becomes several copies
    /// sharing identical position, normal and weights. That is exactly how the
    /// mesh was authored, and only duplicates where a seam genuinely exists.
    /// </summary>
    private static SkinnedDrawableBuilder.Lod? AssembleWithSeams(
        DMesh3 tier, GarmentSkinTransfer.Result weights, IReadOnlyList<Vector2f[]> cornerUv)
    {
        var normals = VertexNormals(tier);
        var uvPerVertex = new Vector2f[tier.MaxVertexID];
        foreach (int tid in tier.TriangleIndices())
        {
            if (tid >= cornerUv.Count || cornerUv[tid] is null) continue;
            var t = tier.GetTriangle(tid);
            uvPerVertex[t.a] = cornerUv[tid][0];
            uvPerVertex[t.b] = cornerUv[tid][1];
            uvPerVertex[t.c] = cornerUv[tid][2];
        }
        var tangents = VertexTangents(tier, normals, uvPerVertex);

        var verts = new List<SkinnedDrawableBuilder.Vertex>();
        var indices = new List<ushort>();
        var byVertexAndUv = new Dictionary<(int vid, float u, float v), int>();

        int Emit(int vid, Vector2f texcoord)
        {
            var key = (vid, texcoord.x, texcoord.y);
            if (byVertexAndUv.TryGetValue(key, out int existing)) return existing;

            var influences = weights.Weights[vid];
            if (influences.Count == 0) return -1;

            var p = tier.GetVertex(vid);
            var vtx = new SkinnedDrawableBuilder.Vertex
            {
                Position = new SharpDX.Vector3((float)p.x, (float)p.y, (float)p.z),
                Normal = new SharpDX.Vector3((float)normals[vid].x, (float)normals[vid].y, (float)normals[vid].z),
                Uv0 = new SharpDX.Vector2(texcoord.x, texcoord.y),
                Tangent = tangents[vid],
            };
            SkinnedDrawableBuilder.PackWeights(influences, vtx.BoneIndices, vtx.Weights);
            int index = verts.Count;
            if (index > ushort.MaxValue) return -1;
            verts.Add(vtx);
            byVertexAndUv[key] = index;
            return index;
        }

        foreach (int tid in tier.TriangleIndices())
        {
            var t = tier.GetTriangle(tid);
            var uvs = tid < cornerUv.Count && cornerUv[tid] is not null
                ? cornerUv[tid]
                : new[] { uvPerVertex[t.a], uvPerVertex[t.b], uvPerVertex[t.c] };
            int a = Emit(t.a, uvs[0]), b = Emit(t.b, uvs[1]), c = Emit(t.c, uvs[2]);
            if (a < 0 || b < 0 || c < 0) continue;
            indices.Add((ushort)a); indices.Add((ushort)b); indices.Add((ushort)c);
        }

        if (verts.Count == 0 || indices.Count == 0 || verts.Count > ushort.MaxValue) return null;
        return new SkinnedDrawableBuilder.Lod { Vertices = verts, Indices = indices };
    }

    /// <summary>Decimation invents new vertex positions, so a coarser tier takes
    /// its texture coordinates from whichever original vertex is nearest.</summary>
    private static Vector2f NearestUv(Vector3d p, DMeshAABBTree3 tree, DMesh3 source, Func<int, Vector2f> uv)
    {
        int tid = tree.FindNearestTriangle(p, out _);
        if (tid == DMesh3.InvalidID) return new Vector2f(0, 0);
        var tri = source.GetTriangle(tid);
        int best = tri.a;
        double bd = source.GetVertex(tri.a).DistanceSquared(p);
        double d2 = source.GetVertex(tri.b).DistanceSquared(p);
        if (d2 < bd) { bd = d2; best = tri.b; }
        if (source.GetVertex(tri.c).DistanceSquared(p) < bd) best = tri.c;
        return uv(best);
    }

    /// <summary>Per-vertex tangents in the usual accumulate-then-orthogonalize
    /// form, with handedness in W. The ped shader samples a normal map, so
    /// these have to be real — a constant placeholder tangent lights the
    /// garment wrongly however good its weights are. Degenerate UVs fall back
    /// to an arbitrary vector perpendicular to the normal, which is no worse
    /// than the placeholder and never produces NaN.</summary>
    private static SharpDX.Vector4[] VertexTangents(DMesh3 mesh, Vector3d[] normals, Vector2f[] uv)
    {
        int n = mesh.MaxVertexID;
        var tan = new Vector3d[n];
        var bitan = new Vector3d[n];

        foreach (int tid in mesh.TriangleIndices())
        {
            var t = mesh.GetTriangle(tid);
            var p0 = mesh.GetVertex(t.a); var p1 = mesh.GetVertex(t.b); var p2 = mesh.GetVertex(t.c);
            var w0 = uv[t.a]; var w1 = uv[t.b]; var w2 = uv[t.c];

            var e1 = p1 - p0; var e2 = p2 - p0;
            double s1 = w1.x - w0.x, t1 = w1.y - w0.y;
            double s2 = w2.x - w0.x, t2 = w2.y - w0.y;
            double det = s1 * t2 - s2 * t1;
            if (Math.Abs(det) < 1e-12) continue;      // degenerate UV triangle
            double r = 1.0 / det;

            var sdir = (e1 * t2 - e2 * t1) * r;
            var tdir = (e2 * s1 - e1 * s2) * r;
            tan[t.a] += sdir; tan[t.b] += sdir; tan[t.c] += sdir;
            bitan[t.a] += tdir; bitan[t.b] += tdir; bitan[t.c] += tdir;
        }

        var result = new SharpDX.Vector4[n];
        foreach (int vid in mesh.VertexIndices())
        {
            var nrm = normals[vid];
            var t = tan[vid];
            // Gram-Schmidt against the normal.
            var ortho = t - nrm * nrm.Dot(t);
            double len = ortho.Length;
            if (len < 1e-9)
            {
                // No usable UV gradient here: any perpendicular will do.
                var seed = Math.Abs(nrm.x) < 0.9 ? Vector3d.AxisX : Vector3d.AxisY;
                ortho = seed - nrm * nrm.Dot(seed);
                len = ortho.Length;
                if (len < 1e-9) { result[vid] = new SharpDX.Vector4(1, 0, 0, 1); continue; }
            }
            ortho /= len;
            float handed = nrm.Cross(t).Dot(bitan[vid]) < 0.0 ? -1f : 1f;
            result[vid] = new SharpDX.Vector4((float)ortho.x, (float)ortho.y, (float)ortho.z, handed);
        }
        return result;
    }

    private static Vector3d[] VertexNormals(DMesh3 mesh)
    {
        var normals = new Vector3d[mesh.MaxVertexID];
        foreach (int tid in mesh.TriangleIndices())
        {
            var t = mesh.GetTriangle(tid);
            var n = (mesh.GetVertex(t.b) - mesh.GetVertex(t.a))
                .Cross(mesh.GetVertex(t.c) - mesh.GetVertex(t.a));
            normals[t.a] += n; normals[t.b] += n; normals[t.c] += n;
        }
        for (int i = 0; i < normals.Length; i++)
        {
            double len = normals[i].Length;
            normals[i] = len > 1e-12 ? normals[i] / len : Vector3d.AxisY;
        }
        return normals;
    }
}
