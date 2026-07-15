// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using CodeWalker.GameFiles;

namespace FiveOS.Services;

/// <summary>
/// Per-LOD geometry optimizer for the workbench. Each LOD tab reduces the
/// geometry that ALREADY EXISTS in that LOD (High / Med / Low) in place — it
/// never invents new tiers. Generating Med/Low from High would ADD geometry
/// and grow the file, which is the opposite of what "optimize" should do, so
/// that path was removed.
///
/// Clone helpers (used only for the non-destructive live preview) are ported
/// from <c>ydr-writer/LodGenerator.cs</c> (that project is excluded from the
/// FiveOS compile). Decimation reuses <see cref="DrawableOptimizer.DecimateModelsMerged"/>
/// so the saved geometry matches what the preview showed.
/// </summary>
public static class DrawableLodBuilder
{
    /// <summary>Which LOD tier an operation targets. (No VLow — clothing
    /// doesn't use it, and the workbench only exposes High/Med/Low.)</summary>
    public enum DrawableLod { High, Med, Low }

    // ─── Per-LOD reduce + save (in place; never adds a tier) ─────────────

    /// <summary>Reduce ONE existing LOD in place and write the file back. A
    /// missing LOD or a full-res ratio is a no-op (nothing is added). Heavy —
    /// call off the UI thread.</summary>
    public static void SaveLodReduced(string path, DrawableLod lod, float ratio, bool preserveBoundary)
    {
        var bytes = BuildLodReducedBytes(path, lod, ratio, preserveBoundary);
        if (bytes != null) File.WriteAllBytes(path, bytes);
    }

    /// <summary>Project the on-disk size after reducing this one LOD to
    /// <paramref name="ratio"/> in place — exactly what <see cref="SaveLodReduced"/>
    /// would write, measured by serializing in memory (RSC7 is compressed, so
    /// there's no shortcut). -1 for an unsupported extension. Heavy — call off
    /// the UI thread.</summary>
    public static long MeasureLodReducedSize(string path, DrawableLod lod, float ratio, bool preserveBoundary)
    {
        var bytes = BuildLodReducedBytes(path, lod, ratio, preserveBoundary);
        return bytes?.LongLength ?? -1;
    }

    /// <summary>Load, reduce a single LOD in place, serialize. Shared by save +
    /// size projection so the projected number is exactly what gets written.</summary>
    private static byte[]? BuildLodReducedBytes(string path, DrawableLod lod, float ratio, bool pb)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".ydd":
                var ydd = DrawableOptimizer.LoadResource<YddFile>(path);
                if (ydd.Drawables != null)
                    foreach (var d in ydd.Drawables)
                        if (d != null) ReduceLodInPlace(d, lod, ratio, pb);
                return ydd.Save();
            case ".ydr":
                var ydr = DrawableOptimizer.LoadResource<YdrFile>(path);
                if (ydr.Drawable != null) ReduceLodInPlace(ydr.Drawable, lod, ratio, pb);
                return ydr.Save();
            case ".yft":
                var yft = DrawableOptimizer.LoadResource<YftFile>(path);
                var main = yft.Fragment?.Drawable;
                if (main != null) ReduceLodInPlace(main, lod, ratio, pb);
                return yft.Save();
            default: return null;
        }
    }

    /// <summary>The DrawableModel[] for a tier (null when the model has none).</summary>
    public static DrawableModel[]? GetLodModels(DrawableBase d, DrawableLod lod) => lod switch
    {
        DrawableLod.High => d?.DrawableModels?.High,
        DrawableLod.Med  => d?.DrawableModels?.Med,
        DrawableLod.Low  => d?.DrawableModels?.Low,
        _ => null,
    };

    private static void ReduceLodInPlace(DrawableBase d, DrawableLod lod, float ratio, bool pb)
    {
        var models = GetLodModels(d, lod);
        if (models == null || models.Length == 0 || ratio >= 0.999f) return;
        // Never merge-decimate a rigid animated prop — it welds the separate
        // moving parts together and distorts their rest pose so the .ycd clip
        // plays torn/deformed. (Same guard as DrawableOptimizer.DecimateDrawable,
        // for the workbench per-LOD Save path that bypasses it.)
        if (DrawableOptimizer.IsRigidAnimatedDrawable(d, models)) return;
        DrawableOptimizer.DecimateModelsMerged(models, new DrawableOptimizer.Options(ratio, pb));
    }

    // ─── Live preview tier (non-destructive clone + decimate) ────────────

    /// <summary>Clone a LOD's models and decimate the clones to <paramref name="ratio"/>
    /// for the workbench preview — the on-disk geometry is never touched.
    /// Returns null for a full-res ratio (caller previews the originals).</summary>
    public static DrawableModel[]? BuildTierModels(DrawableModel[] source, float ratio, bool preserveBoundary)
    {
        if (ratio <= 0f || ratio >= 1f) return null;

        var clones = new DrawableModel[source.Length];
        for (int i = 0; i < source.Length; i++)
            clones[i] = source[i] != null ? CloneModel(source[i]) : null!;

        // Decimate ALL the cloned geometries together (merged) so shared panel
        // borders collapse instead of tearing or blocking reduction.
        DrawableOptimizer.DecimateModelsMerged(clones, new DrawableOptimizer.Options(ratio, preserveBoundary));
        return clones;
    }

    // ─── Cloning (ported from LodGenerator) ──────────────────────────────

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
        // VertexData first so it can be plugged into the cloned VertexBuffer's
        // Data1/Data2 — RAGE dereferences VertexBuffer.Data1 at runtime, so it
        // must point at the same fresh buffer as the geometry's VertexData.
        var newVd = src.VertexData != null ? CloneVertexData(src.VertexData) : null;
        var newIb = src.IndexBuffer != null ? CloneIndexBuffer(src.IndexBuffer) : null;
        var newVb = src.VertexBuffer != null ? CloneVertexBuffer(src.VertexBuffer, newVd) : null;

        return new DrawableGeometry
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
    }

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
            Data1 = sharedVd,
            Data2 = src.Data2 != null ? sharedVd : null,
            Info = src.Info,
        };
    }

    private static VertexData CloneVertexData(VertexData src)
    {
        return new VertexData
        {
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
}
