// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

namespace FiveOS.Services;

/// <summary>
/// FiveM / RAGE optimization thresholds used by the import-time health
/// banner and the Sketchfab dialog. Numbers are sourced from a mix of
/// engine-enforced limits (citizenfx/fivem source, Cfx.re forum stickies),
/// CodeWalker / Sollumz docs, and the modding-forum.com community guides.
///
/// We tune defaults for the common case — map props (.ydr) — because
/// that's the primary 3D-to-YDR flow. Vehicles and peds get a separate
/// classifier when we know the asset type.
/// </summary>
public static class MeshThresholds
{
    /// <summary>Map prop / static drawable. Most permissive recommendations.</summary>
    public const int PropRecommendedTris = 10_000;
    public const int PropWarnTris = 20_000;
    public const int PropFailTris = 50_000;

    /// <summary>Vehicle hi-LOD. Tighter than props because vehicles are duplicated
    /// across NPC traffic — each tri scales by world-population.</summary>
    public const int VehicleRecommendedTris = 60_000;
    public const int VehicleWarnTris = 150_000;
    public const int VehicleFailTris = 250_000;

    /// <summary>Per-component ped (.ydd) — head, torso, legs each.</summary>
    public const int PedRecommendedTris = 20_000;
    public const int PedWarnTris = 40_000;
    public const int PedFailTris = 80_000;

    /// <summary>YTD streaming cap (citizenfx/fivem ResourceStreamComponent.cpp).
    /// Hard fail — engine cannot stream YTD beyond this in a single frame.</summary>
    public const long YtdHardFailBytes = 16L * 1024 * 1024;
    /// <summary>Cfx.re official "oversized assets" warning threshold.</summary>
    public const long PhysicalMemWarnBytes = 48L * 1024 * 1024;

    /// <summary>Source-file size for raw 3D imports (.glb / .fbx / .obj).
    /// Numbers calibrated from a working FiveM mapper: optimized props
    /// land at 2–10 MB on disk, with ~8 MB being the comfortable
    /// sweet spot. Anything past 10 MB is almost certainly carrying
    /// dead-weight (uncompressed textures, redundant verts, no LODs).</summary>
    public const long SourceFileSweetSpotBytes = 8L * 1024 * 1024;
    public const long SourceFileWarnBytes = 10L * 1024 * 1024;
    public const long SourceFileFailBytes = 25L * 1024 * 1024;

    /// <summary>Vehicle texture clamp from the engine (str_maxVehicleTextureRes=1024).</summary>
    public const int VehicleTextureMaxDim = 1024;
    /// <summary>Map prop texture cap — community consensus, not engine-enforced.</summary>
    public const int PropTextureMaxDim = 2048;

    public enum Severity { Ok, Warn, Fail }

    /// <summary>Classify a triangle count for the default (prop) flow.</summary>
    public static Severity ClassifyPropTris(long tris)
    {
        if (tris < PropWarnTris) return Severity.Ok;
        if (tris < PropFailTris) return Severity.Warn;
        return Severity.Fail;
    }

    /// <summary>Classify a source file size on disk (covers raw imports
    /// before any conversion; correlates with tri count + embedded textures).</summary>
    public static Severity ClassifySourceFile(long bytes)
    {
        if (bytes < SourceFileWarnBytes) return Severity.Ok;
        if (bytes < SourceFileFailBytes) return Severity.Warn;
        return Severity.Fail;
    }

    /// <summary>One-shot classifier that takes the worst of the tri count
    /// and source file size — both are user-visible signals of "this thing
    /// needs optimizing before you ship it."</summary>
    public static Severity ClassifyImport(long tris, long sourceBytes)
    {
        var t = ClassifyPropTris(tris);
        var f = ClassifySourceFile(sourceBytes);
        return (Severity)System.Math.Max((int)t, (int)f);
    }

    /// <summary>Suggested target tri count for one-click auto-optimize. We
    /// aim for the top of the "Ok" band — gives users headroom without
    /// over-decimating to silhouette quality. Capped at 95% of input so the
    /// reducer always has *something* to do.</summary>
    public static int SuggestedAutoOptimizeTarget(long currentTris)
    {
        var target = PropRecommendedTris;
        var maxTarget = (int)(currentTris * 0.95);
        return System.Math.Min(target, maxTarget);
    }
}
