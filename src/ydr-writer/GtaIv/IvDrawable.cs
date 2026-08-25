// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Numerics;

namespace YdrWriter.GtaIv;

/// <summary>One vertex as GTA IV OpenFormats stores it.
///
/// Every field here maps to one of the 16 slash-separated groups on a .mesh
/// vertex line. Groups the shader doesn't declare still have to be present
/// (zero-filled) — see <see cref="OpenFormatsWriter"/>.</summary>
internal struct IvVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    /// <summary>Vertex colour. Stored as RGBA here and serialised in the
    /// byte order RAGE's d3dcolor expects — see OpenFormatsWriter.</summary>
    public byte R, G, B, A;
    public Vector2 Uv0;
    /// <summary>Tangent + handedness in W. Only consumed by presets whose
    /// vertex declaration includes a tangent element (the normal-mapped
    /// family), but always written.</summary>
    public Vector4 Tangent;
}

/// <summary>A run of geometry sharing one shader. Becomes a single
/// <c>Mtl N { Prim 0 { … } }</c> block in the .mesh file.
///
/// Indices are LOCAL to this primitive (0..Vertices.Count-1), not global to
/// the mesh — verified against decompiled shipping assets, where each Mtl
/// block restarts its index numbering at 0.</summary>
internal sealed class IvPrimitive
{
    public required int ShaderIndex { get; init; }
    public List<IvVertex> Vertices { get; } = new();
    public List<int> Indices { get; } = new();

    public Vector3 Center;
    public float Radius;
    public Vector3 AabbMin;
    public Vector3 AabbMax;

    /// <summary>Recompute the bounding sphere/box from the current vertices.
    /// The sphere is centred on the AABB centre (not the centroid) because
    /// that's what ResourceBuilder emits when it decompiles stock assets, and
    /// RAGE culls against it.</summary>
    public void RecomputeBounds()
    {
        if (Vertices.Count == 0)
        {
            Center = Vector3.Zero; Radius = 0f;
            AabbMin = Vector3.Zero; AabbMax = Vector3.Zero;
            return;
        }
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in Vertices)
        {
            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }
        AabbMin = min;
        AabbMax = max;
        Center = (min + max) * 0.5f;
        float r = 0f;
        foreach (var v in Vertices)
            r = Math.Max(r, Vector3.Distance(Center, v.Position));
        Radius = r;
    }
}

/// <summary>One LOD tier's geometry — every primitive that draws at this
/// detail level. Written as a single .mesh file.</summary>
internal sealed class IvMesh
{
    public List<IvPrimitive> Primitives { get; } = new();

    public bool IsEmpty => Primitives.Count == 0 || Primitives.All(p => p.Indices.Count == 0);

    /// <summary>Bounds covering every primitive in the tier. The .mesh header
    /// writes this first when the tier has more than one material.</summary>
    public (Vector3 Center, float Radius, Vector3 Min, Vector3 Max) Bounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (var p in Primitives)
        {
            if (p.Vertices.Count == 0) continue;
            min = Vector3.Min(min, p.AabbMin);
            max = Vector3.Max(max, p.AabbMax);
            any = true;
        }
        if (!any) return (Vector3.Zero, 0f, Vector3.Zero, Vector3.Zero);
        var c = (min + max) * 0.5f;
        float r = 0f;
        foreach (var p in Primitives)
            foreach (var v in p.Vertices)
                r = Math.Max(r, Vector3.Distance(c, v.Position));
        return (c, r, min, max);
    }
}

/// <summary>A shader as instantiated for one material: which preset, and the
/// texture names bound into its sampler slots.</summary>
internal sealed class IvShaderInstance
{
    public required IvPreset Preset { get; set; }

    /// <summary>Texture reference for each sampler, keyed by sampler name.
    ///
    /// Two reference forms are legal and both appear in shipping assets:
    ///   - a bare name ("cj_green_grenade2_s") → resolved from a .wtd at runtime
    ///   - "&lt;model&gt;\&lt;file&gt;.dds"     → EMBEDDED in the .wdr itself
    /// FiveOS emits the embedded form by default so a prop is one
    /// self-contained file with no texture dictionary to ship alongside.</summary>
    public Dictionary<string, string> Textures { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The whole GTA IV drawable: its shader table, its four LOD tiers,
/// and the bounds RAGE culls against. Serialised as one .odr plus one .mesh
/// per non-empty tier.</summary>
internal sealed class IvDrawable
{
    public required string Name { get; init; }
    public List<IvShaderInstance> Shaders { get; } = new();

    /// <summary>high / med / low / vlow, in that order. A null or empty tier
    /// writes as "none" in the lodgroup block.</summary>
    public IvMesh?[] Lods { get; } = new IvMesh?[4];

    /// <summary>Draw distance per tier. Stock GTA IV props ship 9999 across
    /// the board and let the .ide/.wpl drive culling instead.</summary>
    public float[] LodDistances { get; } = { 9999f, 9999f, 9999f, 9999f };

    internal static readonly string[] LodNames = { "high", "med", "low", "vlow" };

    public (Vector3 Center, float Radius, Vector3 Min, Vector3 Max) Bounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (var lod in Lods)
        {
            if (lod == null || lod.IsEmpty) continue;
            var (_, _, lmin, lmax) = lod.Bounds();
            min = Vector3.Min(min, lmin);
            max = Vector3.Max(max, lmax);
            any = true;
        }
        if (!any) return (Vector3.Zero, 0f, Vector3.Zero, Vector3.Zero);
        var c = (min + max) * 0.5f;
        float r = 0f;
        foreach (var lod in Lods)
        {
            if (lod == null) continue;
            foreach (var p in lod.Primitives)
                foreach (var v in p.Vertices)
                    r = Math.Max(r, Vector3.Distance(c, v.Position));
        }
        return (c, r, min, max);
    }
}
