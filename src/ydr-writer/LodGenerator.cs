// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using CodeWalker.GameFiles;
using g3;

namespace YdrWriter;

/// <summary>
/// Generates embedded LODs inside a single YDR by deep-cloning the High
/// DrawableModels three times, decimating each clone via g3sharp's quadric
/// edge-collapse Reducer, and assigning the cloned-and-reduced arrays to
/// the Drawable's Med/Low/VLow slots. Sets the LodDist fields so RAGE
/// switches between tiers at sensible distances.
///
/// Why deep clone instead of regenerate from the source mesh: the High
/// DrawableModels already carry vertex-format-correct buffers (the full
/// FVF stride, shader indices that match the YDR's ShaderGroup, bounds
/// metadata, skeleton bindings). Decimating those buffers in place is
/// cleaner than running the FBX→YDR pipeline four times — each LOD
/// inherits the shaders, materials, textures, and bounds of the High
/// without us having to thread material-ordering invariants through
/// four conversion runs.
///
/// Scope limits we live with:
/// • Med/Low/VLow inherit High's shader bindings — they share the same
///   ShaderGroup. There's no per-LOD texture downscaling here; the
///   only saving across tiers is geometry. Vanilla GTA props get
///   bigger savings by also dropping texture resolution per tier,
///   but that needs a separate texconv pass per LOD and a per-tier
///   TextureDictionary, which doubles the YDR write cost. Skipped
///   for v1.
/// • Decimation is quadric-error-only — no UV-island / boundary
///   preservation beyond what the existing DrawableOptimizer does.
///   For props with cutout textures (chain-link fence, foliage), low
///   ratios may eat UV seams visibly.
/// </summary>
public static class LodGenerator
{
    public sealed record Config(
        float MedRatio    = 0.50f,
        float LowRatio    = 0.20f,
        float VLowRatio   = 0.05f,
        float DistHigh    = 60f,
        float DistMed     = 120f,
        float DistLow     = 250f,
        float DistVLow    = 500f);

    /// <summary>Add Med/Low/VLow LODs to a drawable that currently only has
    /// High models. Mutates <paramref name="drawable"/> in place. Returns
    /// (medTris, lowTris, vlowTris) — useful for logging.</summary>
    public static (int Med, int Low, int VLow) AddLods(
        DrawableBase drawable, Config cfg, Action<string>? log = null)
    {
        var models = drawable?.DrawableModels;
        var high = models?.High;
        if (drawable == null || models == null || high == null || high.Length == 0)
        {
            log?.Invoke("LOD: no High models on drawable — skipping");
            return (0, 0, 0);
        }

        // Sentinel for "no clamp" — we use it as the unused parameter for the
        // High tier inside BuildTier. The actual High array isn't modified.

        int medTris  = BuildTier(high, cfg.MedRatio,  out var medArr,  "Med",  log);
        int lowTris  = BuildTier(high, cfg.LowRatio,  out var lowArr,  "Low",  log);
        int vlowTris = BuildTier(high, cfg.VLowRatio, out var vlowArr, "VLow", log);
        models.Med  = medArr;
        models.Low  = lowArr;
        models.VLow = vlowArr;

        // LodDist on the drawable controls when RAGE switches tiers. Set
        // them all even if a tier ended up empty — RAGE reads the field
        // regardless and a zero distance can cause early dropouts.
        drawable.LodDistHigh = cfg.DistHigh;
        drawable.LodDistMed  = cfg.DistMed;
        drawable.LodDistLow  = cfg.DistLow;
        drawable.LodDistVlow = cfg.DistVLow;

        // The DrawableModelsBlock's per-tier pointers are derived at Save
        // time from the array references; nothing else to wire up here.
        log?.Invoke($"LOD: med={medTris:N0} low={lowTris:N0} vlow={vlowTris:N0} tris, dist={cfg.DistHigh}/{cfg.DistMed}/{cfg.DistLow}/{cfg.DistVLow}");
        return (medTris, lowTris, vlowTris);
    }

    private static int BuildTier(
        DrawableModel[] high, float ratio,
        out DrawableModel[]? outArr, string tag, Action<string>? log)
    {
        if (ratio <= 0f || ratio >= 1f)
        {
            // ratio=1 means "same as High" — skip the clone, the engine
            // will fall back to High. ratio<=0 means "drop the tier".
            outArr = null;
            return 0;
        }

        int totalTris = 0;
        var clones = new DrawableModel[high.Length];
        for (int i = 0; i < high.Length; i++)
        {
            var src = high[i];
            if (src == null) { clones[i] = null!; continue; }

            var clone = CloneModel(src);
            foreach (var geom in clone.Geometries ?? Array.Empty<DrawableGeometry>())
            {
                if (geom == null) continue;
                totalTris += DecimateGeometry(geom, ratio);
            }
            clones[i] = clone;
        }

        outArr = clones;
        return totalTris;
    }

    // ─── Cloning ───────────────────────────────────────────────────────

    /// <summary>Deep-clone a DrawableModel so subsequent in-place mutation
    /// of vertex/index buffers on the clone doesn't reach back to the
    /// source High model. Shallow fields are value-copied; arrays of
    /// pointers/references are duplicated; sub-blocks (Geometries,
    /// VertexData, IndexBuffer) are cloned with fresh byte arrays.</summary>
    private static DrawableModel CloneModel(DrawableModel src)
    {
        var dst = new DrawableModel
        {
            VFT = src.VFT,
            Unknown_4h = src.Unknown_4h,
            GeometriesCount1 = src.GeometriesCount1,
            GeometriesCount2 = src.GeometriesCount2,
            Unknown_14h = src.Unknown_14h,
            SkeletonBinding = src.SkeletonBinding,
            RenderMaskFlags = src.RenderMaskFlags,
            GeometriesCount3 = src.GeometriesCount3,
            ShaderMapping = (ushort[]?)src.ShaderMapping?.Clone(),
            BoundsData = (AABB_s[]?)src.BoundsData?.Clone(),
        };

        if (src.Geometries != null)
        {
            var geoms = new DrawableGeometry[src.Geometries.Length];
            for (int i = 0; i < src.Geometries.Length; i++)
                geoms[i] = src.Geometries[i] != null ? CloneGeometry(src.Geometries[i]) : null!;
            dst.Geometries = geoms;
        }

        return dst;
    }

    private static DrawableGeometry CloneGeometry(DrawableGeometry src)
    {
        // VertexData FIRST so we can plug it into the cloned VertexBuffer's
        // Data1 slot below. CW.Core's reader sets DrawableGeometry.VertexData
        // = VertexBuffer.Data1 (or Data2) during load, and CW.Core's writer
        // serializes VertexData as a SEPARATE block referenced by the
        // VertexDataPointer — but VertexBuffer.Data1 is what RAGE actually
        // dereferences at runtime. Both have to point to the same buffer or
        // the runtime sees zero verts while the resource report says otherwise.
        var newVd = src.VertexData != null ? CloneVertexData(src.VertexData) : null;
        var newIb = src.IndexBuffer != null ? CloneIndexBuffer(src.IndexBuffer) : null;
        var newVb = src.VertexBuffer != null ? CloneVertexBuffer(src.VertexBuffer, newVd) : null;

        var dst = new DrawableGeometry
        {
            VFT = src.VFT,
            Unknown_4h = src.Unknown_4h,
            Unknown_8h = src.Unknown_8h,
            Unknown_10h = src.Unknown_10h,
            Unknown_20h = src.Unknown_20h,
            Unknown_28h = src.Unknown_28h,
            Unknown_30h = src.Unknown_30h,
            Unknown_40h = src.Unknown_40h,
            Unknown_48h = src.Unknown_48h,
            Unknown_50h = src.Unknown_50h,
            IndicesCount = src.IndicesCount,
            TrianglesCount = src.TrianglesCount,
            VerticesCount = src.VerticesCount,
            Unknown_62h = src.Unknown_62h,
            Unknown_64h = src.Unknown_64h,
            VertexStride = src.VertexStride,
            BoneIdsCount = src.BoneIdsCount,
            Unknown_74h = src.Unknown_74h,
            Unknown_80h = src.Unknown_80h,
            Unknown_88h = src.Unknown_88h,
            Unknown_90h = src.Unknown_90h,
            BoneIds = (ushort[]?)src.BoneIds?.Clone(),
            ShaderID = src.ShaderID,
            AABB = src.AABB,
            VertexBuffer = newVb,
            VertexData = newVd,
            IndexBuffer = newIb,
        };
        return dst;
    }

    /// <summary>Clone a VertexBuffer, plugging the already-cloned VertexData
    /// into Data1/Data2 so the buffer and the per-geometry VertexData refer
    /// to the same byte array. VertexDeclaration (Info) is shared across LOD
    /// tiers — same FVF, no need to deep-copy it.</summary>
    private static VertexBuffer CloneVertexBuffer(VertexBuffer src, VertexData? sharedVd)
    {
        return new VertexBuffer
        {
            VFT = src.VFT,
            Unknown_4h = src.Unknown_4h,
            VertexStride = src.VertexStride,
            Flags = src.Flags,
            Unknown_Ch = src.Unknown_Ch,
            VertexCount = src.VertexCount,
            Unknown_1Ch = src.Unknown_1Ch,
            Unknown_28h = src.Unknown_28h,
            Unknown_38h = src.Unknown_38h,
            Unknown_40h = src.Unknown_40h,
            Unknown_48h = src.Unknown_48h,
            Unknown_50h = src.Unknown_50h,
            Unknown_58h = src.Unknown_58h,
            Unknown_60h = src.Unknown_60h,
            Unknown_68h = src.Unknown_68h,
            Unknown_70h = src.Unknown_70h,
            Unknown_78h = src.Unknown_78h,
            // Plug the cloned VertexData into both Data1 and Data2 — RAGE's
            // loader reads Data1 first, falls back to Data2; CW.FbxConverter
            // sometimes only sets Data1. We mirror whatever the source did
            // and ensure both end up pointing at the same fresh buffer.
            Data1 = sharedVd,
            Data2 = src.Data2 != null ? sharedVd : null,
            Info = src.Info,
        };
    }

    private static VertexData CloneVertexData(VertexData src)
    {
        return new VertexData
        {
            // Info is a struct describing the FVF layout — value-copy is enough,
            // there's no nested pointer state to worry about.
            Info = src.Info,
            VertexCount = src.VertexCount,
            VertexStride = src.VertexStride,
            VertexBytes = (byte[]?)src.VertexBytes?.Clone() ?? Array.Empty<byte>(),
        };
    }

    private static IndexBuffer CloneIndexBuffer(IndexBuffer src)
    {
        return new IndexBuffer
        {
            IndicesCount = src.IndicesCount,
            Indices = (ushort[]?)src.Indices?.Clone() ?? Array.Empty<ushort>(),
        };
    }

    // ─── Decimation ─────────────────────────────────────────────────────

    /// <summary>In-place decimate a single DrawableGeometry to the target
    /// triangle ratio. Mirrors the logic in DrawableOptimizer.DecimateGeometry
    /// — kept separate here so LOD generation doesn't get coupled to the
    /// post-export "optimise existing YDR" workflow. Returns the surviving
    /// triangle count.
    ///
    /// Position is read from the first 12 bytes of each vertex (fixed RAGE
    /// FVF convention). Every surviving vertex inherits its full stride
    /// from the source row, so UVs, normals, tangents, vertex colours, and
    /// skin weights carry through without parsing the FVF declaration.</summary>
    private static int DecimateGeometry(DrawableGeometry geom, float ratio)
    {
        var vd = geom.VertexData;
        var ib = geom.IndexBuffer;
        if (vd == null || ib == null) return 0;

        int stride = vd.VertexStride;
        int vertCount = vd.VertexCount;
        var rawVerts = vd.VertexBytes;
        var indices = ib.Indices;
        if (rawVerts == null || indices == null || stride <= 0 || vertCount <= 0)
            return 0;

        var positions = new Vector3d[vertCount];
        for (int i = 0; i < vertCount; i++)
        {
            int o = i * stride;
            positions[i] = new Vector3d(
                BitConverter.ToSingle(rawVerts, o + 0),
                BitConverter.ToSingle(rawVerts, o + 4),
                BitConverter.ToSingle(rawVerts, o + 8));
        }

        int triCount = indices.Length / 3;
        if (triCount < 32) return triCount; // too small to bother reducing

        int target = (int)Math.Round(triCount * Math.Clamp(ratio, 0.01f, 0.99f));
        if (target >= triCount) return triCount;
        if (target < 4) target = 4;

        var dmesh = new DMesh3();
        for (int i = 0; i < vertCount; i++)
            dmesh.AppendVertex(positions[i]);
        for (int t = 0; t < triCount; t++)
        {
            int a = indices[t * 3 + 0];
            int b = indices[t * 3 + 1];
            int c = indices[t * 3 + 2];
            if (a == b || b == c || a == c) continue;
            if (a < 0 || a >= vertCount || b < 0 || b >= vertCount || c < 0 || c >= vertCount) continue;
            dmesh.AppendTriangle(a, b, c);
        }
        if (dmesh.TriangleCount < 32) return dmesh.TriangleCount;

        var reducer = new Reducer(dmesh);
        reducer.ReduceToTriangleCount(target);

        int newVertCount = dmesh.VertexCount;
        var newVertBytes = new byte[newVertCount * stride];
        var denseMap = new Dictionary<int, int>(newVertCount);
        int dense = 0;
        foreach (var vi in dmesh.VertexIndices())
        {
            denseMap[vi] = dense;
            if (vi >= 0 && vi < vertCount)
                Buffer.BlockCopy(rawVerts, vi * stride, newVertBytes, dense * stride, stride);
            dense++;
        }

        var newIndices = new ushort[dmesh.TriangleCount * 3];
        int ti = 0;
        foreach (var tIdx in dmesh.TriangleIndices())
        {
            var tri = dmesh.GetTriangle(tIdx);
            newIndices[ti++] = (ushort)denseMap[tri.a];
            newIndices[ti++] = (ushort)denseMap[tri.b];
            newIndices[ti++] = (ushort)denseMap[tri.c];
        }

        vd.VertexBytes = newVertBytes;
        vd.VertexCount = newVertCount;
        ib.Indices = newIndices;
        ib.IndicesCount = (uint)newIndices.Length;
        geom.VerticesCount = (ushort)Math.Min(newVertCount, ushort.MaxValue);
        geom.VertexStride = (ushort)stride;
        geom.IndicesCount = (uint)newIndices.Length;
        geom.TrianglesCount = (uint)dmesh.TriangleCount;
        // Keep VertexBuffer's count in sync with VertexData's — CW.Core's
        // DrawableGeometry.Write reads VertexStride from VertexBuffer (not
        // VertexData), and RAGE's runtime reads vertex count from there too.
        // Stale counts here = mismatched-buffer assertion on stream-in.
        if (geom.VertexBuffer != null)
        {
            geom.VertexBuffer.VertexCount = (uint)newVertCount;
            geom.VertexBuffer.VertexStride = (ushort)stride;
        }

        return dmesh.TriangleCount;
    }
}
