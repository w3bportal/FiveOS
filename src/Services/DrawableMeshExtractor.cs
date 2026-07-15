// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Text;
using CodeWalker.GameFiles;
using CodeWalker.Utils;
using ImageMagick;

namespace FiveOS.Services;

/// <summary>
/// Pulls a renderable, TEXTURED mesh out of a CodeWalker drawable LOD for the
/// Optimize workbench's 3D preview. For each geometry it reads positions
/// (raw, the proven first-12-bytes path), then best-effort normals + UV0 via
/// the vertex declaration's typed getters, and resolves + decodes the
/// geometry's diffuse texture to a PNG in the WebView2 session dir. Anything
/// it can't decode degrades to a flat-shaded, position-only part — it never
/// throws, so one odd geometry can't break the whole preview.
/// </summary>
public sealed class DrawableMeshExtractor
{
    /// <summary>One geometry's mesh. Positions are raw RAGE coords (Z-up), 3
    /// floats/vertex; Normals/Uvs are null when absent (viewer computes
    /// normals / falls back to a flat material). TextureFile is a path
    /// relative to the viewer root (e.g. "tex/foo.png") or null.
    /// <para><see cref="Kind"/> is the shader class the viewer renders by —
    /// PAINT (metallic body colour, NOT the black placeholder), GLASS,
    /// EMISSIVE, DECAL, CHROME, or TEXTURED. This is the fix for the black-body
    /// bug: GTA bodies are painted a solid colour whose real value lives in
    /// carcols (outside the .yft), so the placeholder diffuse must be ignored
    /// and PAINT parts rendered with <see cref="PaintColor"/>.</para></summary>
    public sealed record Part(
        float[] Positions, float[]? Normals, float[]? Uvs, int[] Indices, string? TextureFile,
        string Kind = "TEXTURED", int Bucket = 0, uint PaintColor = 0);

    public sealed record LodMesh(Part[] Parts, int Tris, string? Error);

    // Component indices in the RAGE vertex declaration (fixed semantic order).
    private const int CompPosition = 0;
    private const int CompNormal = 3;
    private const int CompTexcoord0 = 6;
    // TexCoord1 (second UV set). GTA vehicle LIVERIES are mapped to UV1, not
    // UV0 — so a wrap applied with UV0 lands in the wrong place / off-model.
    private const int CompTexcoord1 = 7;

    // joaat("diffusesampler") — the DiffuseSampler shader-param name hash
    // (CodeWalker stores param names as lowercase joaat). We match it so the
    // preview shows the albedo/diffuse, not the bump/normal or spec sampler
    // (a normal map rendered as colour reads as garbled neon blue/pink).
    private const uint DiffuseSamplerHash = 4059966321;

    // ─── Vehicle shader classification (the black-body fix) ─────────────────
    // CodeWalker just binds the DiffuseSampler texture verbatim; for GTA paint
    // shaders that texture is a tiny placeholder (the runtime paint swap-slot),
    // so an add-on shipping a black 4x4 renders black. We instead classify each
    // geometry's shader by its joaat name hash and render painted panels a
    // solid metallic COLOUR — never the placeholder. Buckets: 1=glass, 2=decal.
    private static uint Joaat(string s)
    {
        uint h = 0;
        foreach (var ch in s) { h += (uint)char.ToLowerInvariant(ch); h += h << 10; h ^= h >> 6; }
        h += h << 3; h ^= h >> 11; h += h << 15;
        return h;
    }
    private static HashSet<uint> HashSetOf(params string[] names)
    {
        var s = new HashSet<uint>();
        foreach (var n in names) s.Add(Joaat(n));
        return s;
    }

    private static readonly HashSet<uint> PaintHashes = HashSetOf(
        "vehicle_paint1", "vehicle_paint1_enveff", "vehicle_paint2", "vehicle_paint2_enveff",
        "vehicle_paint3", "vehicle_paint3_enveff", "vehicle_paint3_lvr", "vehicle_paint4",
        "vehicle_paint4_emissive", "vehicle_paint4_enveff", "vehicle_paint5_enveff",
        "vehicle_paint6", "vehicle_paint6_enveff", "vehicle_paint7", "vehicle_paint7_enveff",
        "vehicle_paint8", "vehicle_paint9");
    private static readonly HashSet<uint> GlassHashes = HashSetOf(
        "vehicle_vehglass", "vehicle_vehglass_inner");
    private static readonly HashSet<uint> EmissiveHashes = HashSetOf(
        "vehicle_lightsemissive", "vehicle_dash_emissive", "vehicle_tire_emissive");
    private static readonly HashSet<uint> DecalHashes = HashSetOf(
        "vehicle_badges", "vehicle_decal", "vehicle_decal2", "vehicle_licenseplate");

    /// <summary>Default preview paint colour — a neutral metallic silver. The
    /// real colour lives in carcols/carvariations (outside the .yft); this reads
    /// far better than black or primer-grey and can be overridden live.</summary>
    private const uint DefaultPaintColor = 0x9A9EA3;

    /// <summary>Classify a geometry's shader into a render class the viewer
    /// switches on. Hash sets are primary (deterministic, no JenkIndex needed);
    /// the render bucket and the reversed name are safety fallbacks.</summary>
    private static string ClassifyShader(ShaderFX? s, out int bucket, out uint paintColor)
    {
        paintColor = 0;
        bucket = 0;
        if (s == null) return "TEXTURED";
        try { bucket = s.RenderBucket; } catch { bucket = 0; }
        uint h = (uint)s.Name;
        string name;
        try { name = s.Name.ToString()?.ToLowerInvariant() ?? ""; } catch { name = ""; }

        // Paint first — vehicle_paint*_emissive is a BODY panel, not a light.
        if (PaintHashes.Contains(h) || name.StartsWith("vehicle_paint"))
        {
            paintColor = DefaultPaintColor;
            return "PAINT";
        }
        if (GlassHashes.Contains(h) || bucket == 1 || name.Contains("vehglass") || name.Contains("glass"))
            return "GLASS";
        if (EmissiveHashes.Contains(h) || name.Contains("emissive") || name.Contains("lightsemissive"))
            return "EMISSIVE";
        if (DecalHashes.Contains(h) || bucket == 2 || name.Contains("decal")
            || name.Contains("badge") || name.Contains("licenseplate"))
            return "DECAL";
        if (name.Contains("chrome") || name.Contains("_reflect"))
            return "CHROME";
        return "TEXTURED";
    }

    private readonly string _sessionDir;
    private readonly string _texDir;
    // Texture name → relative PNG path (or null if it failed). Dedups decoding
    // across geometries that share a texture and across LOD re-extractions.
    private readonly Dictionary<string, string?> _texCache = new(StringComparer.OrdinalIgnoreCase);
    // Optional external .ytd textures (name-hash → Texture) the host loads for
    // clothing/props whose diffuse lives in a separate .ytd, not embedded. When
    // set, a sampler with no embedded pixel data resolves from here. Null = off.
    private Dictionary<uint, Texture>? _externalTex;

    /// <summary>Supply (or clear with null) an external texture set so the
    /// preview can resolve a drawable's diffuse from a sibling .ytd. Changing
    /// it takes effect on the next ExtractLod.</summary>
    public void SetExternalTextures(Dictionary<uint, Texture>? map) => _externalTex = map;

    public DrawableMeshExtractor(string sessionDir)
    {
        _sessionDir = sessionDir;
        _texDir = Path.Combine(sessionDir, "tex");
    }

    /// <summary>Flatten one LOD's models into a list of textured parts.</summary>
    public LodMesh ExtractLod(DrawableModel[]? models, DrawableBase drawable)
    {
        if (models == null || models.Length == 0)
            return new LodMesh(Array.Empty<Part>(), 0, "No geometry at this LOD.");

        var parts = new List<Part>();
        int tris = 0;
        foreach (var model in models)
        {
            if (model?.Geometries == null) continue;
            for (int gi = 0; gi < model.Geometries.Length; gi++)
            {
                var geom = model.Geometries[gi];
                if (geom == null) continue;
                var part = ExtractPart(geom, model, gi, drawable);
                if (part == null) continue;
                parts.Add(part);
                tris += part.Indices.Length / 3;
            }
        }
        if (parts.Count == 0)
            return new LodMesh(Array.Empty<Part>(), 0, "No renderable geometry at this LOD.");
        return new LodMesh(parts.ToArray(), tris, null);
    }

    /// <summary>Extract a single geometry — public so the vehicle previewer
    /// can group per-geometry parts by their bone (doors/extras/wheels)
    /// instead of consuming the flattened <see cref="ExtractLod"/> list.</summary>
    public Part? ExtractGeometry(DrawableGeometry geom, DrawableModel model, int geomIdx, DrawableBase drawable)
        => ExtractPart(geom, model, geomIdx, drawable);

    private Part? ExtractPart(DrawableGeometry geom, DrawableModel model, int geomIdx, DrawableBase drawable)
    {
        var vd = geom.VertexData;
        var ib = geom.IndexBuffer;
        var rawVerts = vd?.VertexBytes;
        var indices16 = ib?.Indices;
        if (vd == null || rawVerts == null || indices16 == null) return null;

        int stride = vd.VertexStride;
        int vc = vd.VertexCount;
        if (stride <= 0 || vc <= 0 || indices16.Length < 3) return null;

        // Positions: the guaranteed-safe path (first 12 bytes, fixed RAGE layout).
        var pos = new float[vc * 3];
        for (int i = 0; i < vc; i++)
        {
            int o = i * stride;
            pos[i * 3 + 0] = BitConverter.ToSingle(rawVerts, o + 0);
            pos[i * 3 + 1] = BitConverter.ToSingle(rawVerts, o + 4);
            pos[i * 3 + 2] = BitConverter.ToSingle(rawVerts, o + 8);
        }

        // Normals + UV0 + UV1: best-effort via the declaration's typed getters.
        float[]? nrm = null;
        float[]? uv0 = null;
        float[]? uv1 = null;
        var decl = vd.Info;
        if (decl != null)
        {
            bool hasNormal = decl.GetComponentType(CompNormal) == VertexComponentType.Float3;
            try
            {
                if (hasNormal)
                {
                    nrm = new float[vc * 3];
                    for (int i = 0; i < vc; i++)
                    {
                        var n = vd.GetVector3(i, CompNormal);
                        nrm[i * 3 + 0] = n.X; nrm[i * 3 + 1] = n.Y; nrm[i * 3 + 2] = n.Z;
                    }
                }
                uv0 = ReadUv(vd, decl, CompTexcoord0, vc);
                uv1 = ReadUv(vd, decl, CompTexcoord1, vc);
            }
            catch
            {
                // Declaration mismatch — drop the optional attributes, keep the
                // (already-valid) positions so the part still renders flat.
                nrm = null; uv0 = null; uv1 = null;
            }
        }

        var indices = new int[indices16.Length];
        for (int k = 0; k < indices16.Length; k++) indices[k] = indices16[k];

        string? texFile = null;
        bool usedLivery = false;
        string kind = "TEXTURED";
        int bucket = 0;
        uint paintColor = 0;
        try { texFile = ResolveTexture(geom, model, geomIdx, drawable, out usedLivery, out kind, out bucket, out paintColor); }
        catch { texFile = null; }

        // Liveries are UV1-mapped — use the second UV set for the wrap, else UV0.
        var uv = (usedLivery && uv1 != null) ? uv1 : uv0;
        return new Part(pos, nrm, uv, indices, texFile, kind, bucket, paintColor);
    }

    private static float[]? ReadUv(VertexData vd, VertexDeclaration decl, int component, int vc)
    {
        var uvType = decl.GetComponentType(component);
        if (uvType is not (VertexComponentType.Float2 or VertexComponentType.Half2)) return null;
        var uv = new float[vc * 2];
        for (int i = 0; i < vc; i++)
        {
            if (uvType == VertexComponentType.Half2)
            {
                var h = vd.GetHalf2(i, component);
                uv[i * 2 + 0] = (float)h.X; uv[i * 2 + 1] = (float)h.Y;
            }
            else
            {
                var t = vd.GetVector2(i, component);
                uv[i * 2 + 0] = t.X; uv[i * 2 + 1] = t.Y;
            }
        }
        return uv;
    }

    /// <summary>Resolve the geometry's DIFFUSE texture and decode it to a PNG.
    /// Prefers the DiffuseSampler param (by name hash) over the first texture
    /// param — many cloth shaders list the bump/normal sampler first, and a
    /// normal map shown as colour looks like garbled neon. Null when the
    /// texture is external (.ytd) or undecodable.</summary>
    private string? ResolveTexture(DrawableGeometry geom, DrawableModel model, int geomIdx, DrawableBase drawable,
        out bool usedLivery, out string kind, out int bucket, out uint paintColor)
    {
        usedLivery = false;
        var shader = ResolveShader(geom, model, geomIdx, drawable);
        kind = ClassifyShader(shader, out bucket, out paintColor);
        var pl = shader?.ParametersList;
        var prms = pl?.Parameters;
        if (prms == null) return null;
        var hashes = pl!.Hashes;   // MetaName[] parallel to Parameters

        // Capture the sampler's TextureBase — works for embedded Textures AND
        // for external references (a bare TextureBase with no pixel data, whose
        // bytes live in a separate .ytd we may have been handed).
        TextureBase? diffuse = null, firstTex = null;
        for (int i = 0; i < prms.Length; i++)
        {
            var param = prms[i];
            if (param == null || param.DataType != 0) continue;   // 0 = texture sampler
            if (param.Data is not TextureBase tb) continue;
            firstTex ??= tb;
            if (hashes != null && i < hashes.Length && (uint)hashes[i] == DiffuseSamplerHash)
            {
                diffuse = tb;
                break;
            }
        }

        // Prefer the diffuse sampler (embedded or external). Only fall back to
        // the first sampler when it has EMBEDDED data — never promote a random
        // external sampler (often a normal/spec map) to diffuse, which reads as
        // garbled neon.
        var chosen = ResolveDecodable(diffuse);
        if (chosen == null && firstTex is Texture ft && ft.Data != null && ft.Width > 0 && ft.Height > 0)
            chosen = ft;

        // THE BLACK-BODY FIX: PAINT / CHROME never render the tiny paint
        // placeholder swatch. GTA bodies are a solid metallic COLOUR (the real
        // value lives in carcols, outside the .yft). We deliberately do NOT
        // auto-apply a "livery" wrap here — the heuristic grabs the wrong
        // texture and mismaps it across panels (patchy body). A PAINT panel that
        // carries a REAL bespoke diffuse (custom baked paint) still shows it.
        if (kind == "CHROME") return null;
        if (kind == "PAINT")
            return IsLiveryPlaceholder(chosen) ? null : (chosen != null ? DecodeTexture(chosen) : null);

        // Non-paint placeholder panels (rare) can still take a supplied wrap.
        if (_livery != null && IsLiveryPlaceholder(chosen))
        {
            chosen = _livery;
            usedLivery = true;   // the caller maps this part with UV1, not UV0
        }

        return chosen != null ? DecodeTexture(chosen) : null;
    }

    // Optional livery (wrap) texture: applied where a geometry's diffuse is a
    // placeholder the game would swap for the selected livery.
    private Texture? _livery;

    /// <summary>Set (or clear) the wrap/livery texture used to fill body
    /// panels whose diffuse is a placeholder swap-slot.</summary>
    public void SetLiveryTexture(Texture? livery) => _livery = livery;

    private static bool IsLiveryPlaceholder(Texture? t)
    {
        if (t == null) return false;
        if (t.Width <= 4 && t.Height <= 4) return true;   // 4x4 swap placeholder
        var n = t.Name;
        return n != null && (n.Equals("black", StringComparison.OrdinalIgnoreCase)
                          || n.Equals("white", StringComparison.OrdinalIgnoreCase)
                          || n.Equals("default", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolve a sampler's TextureBase to a decodable Texture: its own
    /// embedded pixels when present, else the external .ytd set (by name hash).</summary>
    private Texture? ResolveDecodable(TextureBase? tb)
    {
        if (tb == null) return null;
        if (tb is Texture embedded && embedded.Data != null && embedded.Width > 0 && embedded.Height > 0)
            return embedded;
        if (_externalTex != null && _externalTex.TryGetValue((uint)tb.NameHash, out var ext)
            && ext?.Data != null && ext.Width > 0 && ext.Height > 0)
            return ext;
        return null;
    }

    /// <summary>Resolve a geometry's shader: prefer the resolved reference,
    /// then the model's ShaderMapping (geometry index → shader index), then
    /// the geometry's own ShaderID.</summary>
    private static ShaderFX? ResolveShader(DrawableGeometry geom, DrawableModel model, int geomIdx, DrawableBase drawable)
    {
        if (geom.Shader != null) return geom.Shader;
        var shaders = drawable?.ShaderGroup?.Shaders?.data_items;
        if (shaders == null) return null;

        var map = model?.ShaderMapping;
        if (map != null && geomIdx >= 0 && geomIdx < map.Length)
        {
            int mi = map[geomIdx];
            if (mi >= 0 && mi < shaders.Length) return shaders[mi];
        }
        int idx = geom.ShaderID;
        return idx >= 0 && idx < shaders.Length ? shaders[idx] : null;
    }

    private string? DecodeTexture(Texture tex)
    {
        var key = tex.Name ?? tex.NameHash.ToString();
        if (_texCache.TryGetValue(key, out var cached)) return cached;

        string? rel = null;
        try
        {
            var dds = DDSIO.GetDDSFile(tex);
            if (dds != null && dds.Length > 0)
            {
                Directory.CreateDirectory(_texDir);
                var fileName = SafeName(key) + ".png";
                var full = Path.Combine(_texDir, fileName);
                using var img = new MagickImage(dds);
                img.Format = MagickFormat.Png;
                img.Write(full);
                rel = "tex/" + fileName;
            }
        }
        catch { rel = null; }

        _texCache[key] = rel;
        return rel;
    }

    private static string SafeName(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        var name = sb.Length == 0 ? "tex" : sb.ToString();
        return name.Length > 64 ? name[..64] : name;
    }
}
