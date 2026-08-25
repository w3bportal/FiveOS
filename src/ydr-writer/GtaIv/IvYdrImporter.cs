// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Numerics;
using CodeWalker.GameFiles;
using CodeWalker.Utils;

namespace YdrWriter.GtaIv;

/// <summary>Reads a GTA V drawable (.ydr) straight into an
/// <see cref="IvDrawable"/>, bypassing Assimp entirely.
///
/// This is possible because <see cref="IvDrawable"/>, <see cref="OpenFormatsWriter"/>
/// and <see cref="IvShaderCatalog"/> carry no Assimp dependency — the mesh
/// front end and the IV writer were already separable. Going through an
/// intermediate mesh format would cost a lossy round trip for nothing, since
/// a GTA V vertex buffer already holds exactly the attributes an IV .mesh
/// line needs: position, normal, colour, texcoord0 and tangent.
///
/// Both games are metres and Z-up at a scale factor of exactly 1.0 — verified
/// by pushing identical geometry through both writers and comparing bounds —
/// so no transform is applied here.
///
/// Static drawables only. GTA V skinned content (peds, clothing) and vehicles
/// (.yft fragments) have no GTA IV target format at all.</summary>
internal static class IvYdrImporter
{
    // RAGE vertex component indices, shared by both games.
    private const int CompPosition = 0;
    private const int CompNormal = 3;
    private const int CompColour0 = 4;
    private const int CompTexcoord0 = 6;
    private const int CompTangent0 = 14;

    internal sealed record Options(
        string AssetName, string AssetDir, bool ExtractTextures,
        IReadOnlyList<string>? TextureDictionaries = null);

    internal sealed record Result(IvDrawable Drawable, List<string> Notes);

    internal static Result? Import(string ydrPath, Options opts)
    {
        var ydr = new YdrFile();
        ydr.Load(File.ReadAllBytes(ydrPath));
        var drawable = ydr.Drawable;
        if (drawable == null) return null;

        var notes = new List<string>();

        // Skinned drawables have no IV target: the IV writer emits "Skinned 0"
        // and IV keeps skinned content in .wdd dictionaries the resource
        // compiler cannot produce. Say so rather than shipping a collapsed mesh.
        if (drawable.Skeleton != null)
            notes.Add("source has a skeleton — exported as a STATIC prop (GTA IV skinned drawables are not supported)");

        var models = drawable.DrawableModels?.High;
        if (models == null || models.Length == 0)
        {
            notes.Add("no High detail models in the drawable");
            return null;
        }

        var shaders = drawable.ShaderGroup?.Shaders?.data_items;
        var textures = ExtractTextures(drawable, opts, notes);

        var result = new IvDrawable { Name = opts.AssetName };
        var high = new IvMesh();

        foreach (var model in models)
        {
            var geoms = model?.Geometries;
            if (geoms == null) continue;

            for (int gi = 0; gi < geoms.Length; gi++)
            {
                var geom = geoms[gi];
                if (geom == null) continue;

                var shader = ResolveShader(shaders, model, gi);

                // Shadow-only geometry has no counterpart in GTA IV, and there
                // is no preset that means "cast a shadow but do not draw".
                // Converting it as ordinary geometry buries a solid untextured
                // shell inside the model, so drop it instead.
                if (IvShaderMap.IsShadowProxy(ShaderFileName(shader)))
                {
                    notes.Add("dropped a shadow-only proxy mesh (GTA IV has no equivalent)");
                    continue;
                }

                // Each GTA V geometry maps to exactly one IV Mtl/Prim block:
                // its indices are already local to it, and DrawableGeometry's
                // vertex count is a UInt16 so it can never exceed IV's limit.
                var prim = ReadGeometry(geom, result.Shaders.Count);
                if (prim == null) continue;

                var maps = shader != null && textures.TryGetValue(shader, out var t) ? t : null;
                var mapping = IvShaderMap.Resolve(
                    ShaderFileName(shader),
                    hasNormalMap: maps?.Bump != null,
                    hasSpecMap: maps?.Spec != null);

                if (mapping.Lost != null)
                    notes.Add($"{ShaderFileName(shader) ?? "shader"}: {mapping.Lost}");

                var inst = new IvShaderInstance { Preset = mapping.Preset };
                Bind(inst, IvShaderCatalog.DiffuseSampler, maps?.Diffuse, opts.AssetName);
                Bind(inst, IvShaderCatalog.BumpSampler, maps?.Bump, opts.AssetName);
                Bind(inst, IvShaderCatalog.SpecSampler, maps?.Spec, opts.AssetName);

                result.Shaders.Add(inst);
                prim.RecomputeBounds();
                high.Primitives.Add(prim);
            }
        }

        if (high.Primitives.Count == 0)
        {
            notes.Add("no usable geometry found");
            return null;
        }

        result.Lods[0] = high;

        // Any sampler still empty must be filled before this ships: an unbound
        // texture parameter is a render-time crash, not a missing texture.
        IvPlaceholderTexture.FillUnboundSamplers(result, opts.AssetDir, opts.AssetName, notes);

        return new Result(result, notes);
    }

    private static void Bind(IvShaderInstance inst, string sampler, string? texName, string assetName)
    {
        if (string.IsNullOrWhiteSpace(texName)) return;
        if (!inst.Preset.Params.Any(p => p.Kind == IvParamKind.Texture
                                      && p.Name.Equals(sampler, StringComparison.OrdinalIgnoreCase)))
            return;
        inst.Textures[sampler] = $"{assetName}\\{texName}.dds";
    }

    private static ShaderFX? ResolveShader(ShaderFX[]? shaders, DrawableModel? model, int geomIdx)
    {
        if (shaders == null || shaders.Length == 0) return null;
        var map = model?.ShaderMapping;
        int idx = map != null && geomIdx < map.Length ? map[geomIdx] : geomIdx;
        return idx >= 0 && idx < shaders.Length ? shaders[idx] : shaders[0];
    }

    private static string? ShaderFileName(ShaderFX? s)
    {
        if (s == null) return null;
        try { return IvShaderMap.NameFromHashOrText(s.FileName.ToString()); } catch { return null; }
    }

    /// <summary>The type of a vertex component, or Nothing when the buffer does
    /// not actually carry it.
    ///
    /// ⚠ A RAGE vertex declaration stores a 4-bit type code for ALL SIXTEEN
    /// slots regardless of which ones the buffer holds — every drawable reads
    /// back "normal = Float3, tangent = Float4" whether or not those bytes
    /// exist. Presence lives in the separate flags bitmask, one bit per slot.
    ///
    /// Testing the type alone therefore reports attributes that are not there,
    /// and reading one does not throw: the offset lands past the end of the
    /// vertex and returns the NEXT vertex's bytes. That is silent and ugly —
    /// a position-only geometry comes back with normals of magnitude 1e21, and
    /// a geometry with no tangent gets the following vertex's position. Both
    /// were shipping until a converted palm tree turned up with 708 of 709
    /// non-unit normals.
    ///
    /// Run <c>ydr-writer dump-vertex-decl</c> on a drawable to see the flags
    /// and the resulting layout.</summary>
    private static VertexComponentType TypeOf(VertexDeclaration? decl, int component)
        => decl != null && ((uint)decl.Flags & (1u << component)) != 0
            ? decl.GetComponentType(component)
            : VertexComponentType.Nothing;

    private static bool Has(VertexDeclaration? decl, int component, VertexComponentType expected)
        => TypeOf(decl, component) == expected;

    /// <summary>Decode one geometry's vertex buffer and index buffer.</summary>
    private static IvPrimitive? ReadGeometry(DrawableGeometry geom, int shaderIndex)
    {
        var vd = geom.VertexData;
        var raw = vd?.VertexBytes;
        var idx16 = geom.IndexBuffer?.Indices;
        if (vd == null || raw == null || idx16 == null) return null;

        int stride = vd.VertexStride;
        int count = vd.VertexCount;
        if (stride <= 0 || count <= 0 || idx16.Length < 3) return null;

        var decl = vd.Info;
        // Position is always the first 12 bytes in RAGE's layout; everything
        // else is optional and read through the declaration so an unusual FVF
        // degrades attribute-by-attribute instead of failing the whole part.
        bool hasNormal = Has(decl, CompNormal, VertexComponentType.Float3);
        var colourType = TypeOf(decl, CompColour0);
        var uvType = TypeOf(decl, CompTexcoord0);
        var tanType = TypeOf(decl, CompTangent0);

        var prim = new IvPrimitive { ShaderIndex = shaderIndex };

        for (int i = 0; i < count; i++)
        {
            int o = i * stride;
            var v = new IvVertex
            {
                Position = new Vector3(
                    BitConverter.ToSingle(raw, o + 0),
                    BitConverter.ToSingle(raw, o + 4),
                    BitConverter.ToSingle(raw, o + 8)),
                R = 255, G = 255, B = 255, A = 255,
            };

            try
            {
                if (hasNormal)
                {
                    var n = vd.GetVector3(i, CompNormal);
                    v.Normal = new Vector3(n.X, n.Y, n.Z);
                }
                if (colourType is VertexComponentType.Colour or VertexComponentType.UByte4)
                {
                    var c = vd.GetColour(i, CompColour0);
                    v.R = c.R; v.G = c.G; v.B = c.B; v.A = c.A;
                }
                if (uvType == VertexComponentType.Half2)
                {
                    var h = vd.GetHalf2(i, CompTexcoord0);
                    v.Uv0 = new Vector2((float)h.X, (float)h.Y);
                }
                else if (uvType == VertexComponentType.Float2)
                {
                    var t = vd.GetVector2(i, CompTexcoord0);
                    v.Uv0 = new Vector2(t.X, t.Y);
                }
                if (tanType == VertexComponentType.Float4)
                {
                    var t = vd.GetVector4(i, CompTangent0);
                    // GTA IV stores the -U tangent; GTA V stores +U.
                    v.Tangent = new Vector4(-t.X, -t.Y, -t.Z, t.W);
                }
            }
            catch
            {
                // Declaration disagreed with the buffer — keep the position and
                // carry on; a flat-shaded part beats a dropped one.
            }

            prim.Vertices.Add(v);
        }

        foreach (var i in idx16) prim.Indices.Add(i);

        // Two ways a vertex ends up with no usable normal, both of which put a
        // zero-length normal in front of the game and light the surface black:
        //
        //   * the geometry has no normal element at all — GTA V ships shadow
        //     proxies and flat decal cards that way, since their shaders never
        //     light them, but a GTA IV .mesh line has no way to say "absent";
        //   * the element is there and the source simply stored zeros, which
        //     real map models do on stray unused vertices.
        //
        // Deriving from the triangles handles both, and the second case is
        // repaired per-vertex so the source's good normals are left alone.
        GenerateNormals(prim, onlyMissing: hasNormal);

        return prim;
    }

    /// <summary>Area-weighted smooth normals from the triangles themselves.
    /// Area weighting rather than a plain average so a fan of slivers around
    /// one vertex doesn't outvote the large face it sits on.</summary>
    /// <param name="onlyMissing">Replace only the vertices whose normal is
    /// zero-length, leaving everything the source got right untouched.</param>
    private static void GenerateNormals(IvPrimitive prim, bool onlyMissing)
    {
        if (onlyMissing && !prim.Vertices.Any(v => v.Normal.LengthSquared() < 1e-12f)) return;
        var acc = new Vector3[prim.Vertices.Count];
        for (int i = 0; i + 2 < prim.Indices.Count; i += 3)
        {
            int a = prim.Indices[i], b = prim.Indices[i + 1], c = prim.Indices[i + 2];
            if (a >= acc.Length || b >= acc.Length || c >= acc.Length) continue;
            // Un-normalised cross product is already proportional to area.
            var n = Vector3.Cross(
                prim.Vertices[b].Position - prim.Vertices[a].Position,
                prim.Vertices[c].Position - prim.Vertices[a].Position);
            acc[a] += n; acc[b] += n; acc[c] += n;
        }

        for (int i = 0; i < prim.Vertices.Count; i++)
        {
            var v = prim.Vertices[i];
            if (onlyMissing && v.Normal.LengthSquared() >= 1e-12f) continue;

            var n = acc[i];
            var len = n.Length();
            // A vertex touched only by degenerate triangles has nothing to
            // derive from; straight up is a harmless stand-in.
            v.Normal = len > 1e-12f ? n / len : new Vector3(0, 0, 1);
            prim.Vertices[i] = v;
        }
    }

    private sealed record Maps(string? Diffuse, string? Bump, string? Spec);

    /// <summary>Write the drawable's embedded textures out as .dds files the
    /// .odr can reference, keyed by the shader that uses them.</summary>
    private static Dictionary<ShaderFX, Maps> ExtractTextures(
        DrawableBase drawable, Options opts, List<string> notes)
    {
        var result = new Dictionary<ShaderFX, Maps>();
        var shaders = drawable.ShaderGroup?.Shaders?.data_items;
        if (shaders == null || !opts.ExtractTextures) return result;

        // GTA V map props routinely keep their textures in a shared .ytd rather
        // than embedding them, so the drawable knows a texture's NAME but has
        // no pixels for it. Loading the dictionaries alongside is the
        // difference between a converted prop and a white one.
        var side = LoadDictionaries(opts.TextureDictionaries, notes);

        Directory.CreateDirectory(opts.AssetDir);
        var written = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var external = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in shaders)
        {
            if (s == null) continue;
            result[s] = new Maps(
                Emit(FindSampler(s, "DiffuseSampler"), opts, written, notes, external, side),
                Emit(FindSampler(s, "BumpSampler"), opts, written, notes, external, side),
                Emit(FindSampler(s, "SpecSampler"), opts, written, notes, external, side));
        }

        // A GTA V drawable can name a texture without carrying it: map props
        // routinely leave theirs in a shared .ytd. Nothing can be extracted in
        // that case, but the NAMES are known, and listing them is the
        // difference between "this prop came out white" and "supply these
        // eight textures".
        if (external.Count > 0)
            notes.Add($"{external.Count} texture(s) live in an external .ytd and could not be "
                    + $"embedded - supply them yourself: {string.Join(", ", external)}");

        return result;
    }

    /// <summary>Find a sampler's texture reference.
    ///
    /// Returns TextureBase rather than Texture on purpose: only an EMBEDDED
    /// texture is a Texture (it carries pixel data), while one living in a
    /// shared .ytd is a bare TextureBase that has nothing but a name. Matching
    /// on Texture alone drops those silently, which is how a prop ends up
    /// fully untextured with no indication of what is missing.</summary>
    private static TextureBase? FindSampler(ShaderFX shader, string samplerName)
    {
        var ps = shader.ParametersList?.Parameters;
        var hashes = shader.ParametersList?.Hashes;
        if (ps == null || hashes == null) return null;
        for (int i = 0; i < ps.Length && i < hashes.Length; i++)
        {
            if (ps[i]?.Data is not TextureBase tex) continue;
            if (hashes[i].ToString().Equals(samplerName, StringComparison.OrdinalIgnoreCase))
                return tex;
        }
        return null;
    }

    /// <summary>Serialise one embedded texture to .dds, transcoding when GTA IV
    /// cannot use the format. Returns the file's base name, or null.</summary>
    /// <summary>Load every .ytd the caller offered into one name -> texture
    /// map. Later dictionaries do not overwrite earlier ones, so the first
    /// .ytd named wins a duplicate.</summary>
    private static Dictionary<string, Texture> LoadDictionaries(
        IReadOnlyList<string>? paths, List<string> notes)
    {
        var map = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);
        if (paths == null) return map;

        foreach (var path in paths)
        {
            try
            {
                var ytd = new YtdFile();
                ytd.Load(File.ReadAllBytes(path));
                var items = ytd.TextureDict?.Textures?.data_items;
                if (items == null) continue;
                int added = 0;
                foreach (var t in items)
                {
                    if (t?.Name is not { Length: > 0 } n) continue;
                    if (map.TryAdd(n, t)) added++;
                }
                notes.Add($"{Path.GetFileName(path)}: {added} texture(s) available");
            }
            catch (Exception ex)
            {
                notes.Add($"could not read {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return map;
    }

    private static string? Emit(
        TextureBase? texBase, Options opts, Dictionary<string, string?> cache,
        List<string> notes, SortedSet<string> external,
        Dictionary<string, Texture> side)
    {
        if (texBase == null) return null;
        var name = string.IsNullOrWhiteSpace(texBase.Name) ? "tex" : texBase.Name;

        // The drawable may only NAME a texture rather than carry it - map props
        // routinely leave theirs in a shared .ytd. Take the embedded one when
        // there is one, otherwise look the name up in the side dictionaries,
        // and only then give up and report it missing.
        var tex = texBase as Texture;
        if (tex == null) side.TryGetValue(name, out tex);

        if (tex == null)
        {
            // CodeWalker renders an unbound sampler as the literal "None";
            // that is the absence of a texture, not one to go and find.
            if (!name.Equals("None", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("null", StringComparison.OrdinalIgnoreCase)
                && name != "tex")
                external.Add(name);
            return null;
        }

        if (cache.TryGetValue(name, out var already)) return already;

        string? outName = null;
        try
        {
            var dds = DDSIO.GetDDSFile(tex);
            if (dds is { Length: > 128 })
            {
                var path = Path.Combine(opts.AssetDir, name + ".dds");
                File.WriteAllBytes(path, dds);
                outName = IvTextureBaker.MakeIvCompatible(path, notes) ? name : null;
                if (outName == null) { try { File.Delete(path); } catch { /* */ } }
            }
        }
        catch (Exception ex)
        {
            notes.Add($"texture '{name}' could not be read: {ex.Message}");
        }

        cache[name] = outName;
        return outName;
    }
}
