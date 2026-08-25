// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Numerics;
using Assimp;
using g3;

namespace YdrWriter.GtaIv;

/// <summary>Turns an imported Assimp scene into an <see cref="IvDrawable"/>
/// ready for OpenFormats serialisation.
///
/// The scene arrives already axis-corrected and gizmo-transformed by the
/// shared helpers on <see cref="Converter"/>, so this stage only has to
/// regroup geometry by shader, convert vertex attributes into RAGE's
/// conventions, and build the reduced LOD tiers.</summary>
internal static class IvSceneBuilder
{
    internal sealed record Options(
        string AssetName,
        IReadOnlySet<string> ExcludeMeshes,
        IReadOnlyDictionary<string, string> PartMaterials,
        bool GenerateLods,
        float MedRatio,
        float LowRatio,
        float VLowRatio,
        float[] LodDistances,
        bool EmbedTextures);

    internal static IvDrawable Build(
        Scene scene,
        IReadOnlyList<IvTextureBaker.MaterialMaps> maps,
        Options opts)
    {
        var drawable = new IvDrawable { Name = opts.AssetName };
        for (int i = 0; i < 4 && i < opts.LodDistances.Length; i++)
            drawable.LodDistances[i] = opts.LodDistances[i];

        var high = new IvMesh();
        // Shader key → index into drawable.Shaders. The key folds in the
        // resolved preset as well as the material, so a mesh promoted to glass
        // gets its own shader slot (and therefore its own Mtl block) instead of
        // silently turning its material's other users transparent.
        var slotByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var primBySlot = new Dictionary<int, IvPrimitive>();

        for (int mi = 0; mi < scene.MeshCount; mi++)
        {
            var mesh = scene.Meshes[mi];
            if (mesh.FaceCount == 0 || mesh.VertexCount == 0) continue;
            if (!string.IsNullOrEmpty(mesh.Name) && opts.ExcludeMeshes.Contains(mesh.Name)) continue;

            int matIdx = mesh.MaterialIndex;
            var mm = matIdx >= 0 && matIdx < maps.Count ? maps[matIdx] : null;

            string? matName = null;
            if (matIdx >= 0 && matIdx < scene.MaterialCount)
                matName = scene.Materials[matIdx].Name;

            // Explicit per-part tag from the layers panel wins over the
            // heuristic, same precedence as the GTA V path.
            bool isGlass =
                (!string.IsNullOrEmpty(mesh.Name)
                 && opts.PartMaterials.TryGetValue(mesh.Name, out var tag)
                 && tag.Equals("GLASS", StringComparison.OrdinalIgnoreCase))
                || TextureBaker.AutoDetectGlass(mesh.Name, matName, mm?.Diffuse) == "GLASS";

            var preset = IvShaderCatalog.Select(
                hasNormal: mm?.Normal != null,
                hasSpec: mm?.Specular != null,
                isGlass: isGlass,
                hasAlpha: mm?.DiffuseHasAlpha == true);

            var key = $"{matIdx}|{preset.FileName}";
            if (!slotByKey.TryGetValue(key, out int slot))
            {
                slot = drawable.Shaders.Count;
                slotByKey[key] = slot;

                var inst = new IvShaderInstance { Preset = preset };
                Bind(inst, IvShaderCatalog.DiffuseSampler, mm?.Diffuse, opts);
                Bind(inst, IvShaderCatalog.BumpSampler, mm?.Normal, opts);
                Bind(inst, IvShaderCatalog.SpecSampler, mm?.Specular, opts);
                drawable.Shaders.Add(inst);

                var newPrim = new IvPrimitive { ShaderIndex = slot };
                primBySlot[slot] = newPrim;
                high.Primitives.Add(newPrim);
            }

            Append(primBySlot[slot], mesh);
        }

        // A model whose every mesh was excluded still has to produce a valid
        // drawable rather than a file the builder chokes on.
        if (drawable.Shaders.Count == 0)
        {
            drawable.Shaders.Add(new IvShaderInstance { Preset = IvShaderCatalog.Default });
        }

        foreach (var p in high.Primitives) p.RecomputeBounds();
        drawable.Lods[0] = high;

        // Deliberately no med/low/vlow tiers inside this drawable.
        //
        // The GTA IV resource compiler cannot build a multi-tier drawable. Any
        // .odr with a populated med tier yields a .wdr whose lower-tier model
        // arrays point at uninitialised memory: compiling the SAME .odr twice
        // produces different garbage there, and the compiler's own reader
        // crashes (0xC0000005) reading the file back, while it reads both
        // Rockstar props and our single-tier output cleanly.
        //
        // That matches how GTA IV ships LODs anyway. Rockstar's own props keep
        // the reduced model in a SEPARATE resource — level4_bank3.wdr next to
        // level4_bank_lod.wdr — wired up through the .ide rather than stacked
        // inside one drawable. <see cref="BuildLodVariant"/> produces that
        // second asset.

        // Split last, after decimation — a reduced tier often drops under the
        // ceiling on its own and needs no split at all.
        for (int i = 0; i < 4; i++)
            if (drawable.Lods[i] != null)
                drawable.Lods[i] = SplitOversized(drawable.Lods[i]!);

        return drawable;
    }

    /// <summary>Largest vertex count one geometry block may declare.
    ///
    /// GTA IV stores a block's vertex count in a 16-bit field, so 65536 wraps
    /// to zero and the resource compiler rejects the file. Measured against
    /// the real compiler: a 65,025-vertex block builds, 65,536 does not.
    ///
    /// The cap sits at 65,532 rather than 65,535 to match GIMS IV, whose
    /// independent exporter starts a new face group at the same number
    /// (geosplit in 10_oF_DrawableOut.gmod). Three vertices of headroom costs
    /// nothing and keeps us inside a threshold that shipped tooling has
    /// exercised for years.</summary>
    private const int MaxVertsPerBlock = 65532;

    /// <summary>Break any block that exceeds <see cref="MaxVertsPerBlock"/>
    /// into several blocks that share the same shader.
    ///
    /// Repeating an Mtl index is legal — verified by compiling a mesh whose two
    /// blocks both claim shader 0 — so a split costs nothing but an extra
    /// block. Without this, any single-material model over ~65k vertices (an
    /// ordinary scan or sculpt) fails to build at all.</summary>
    private static IvMesh SplitOversized(IvMesh mesh)
    {
        if (mesh.Primitives.All(p => p.Vertices.Count <= MaxVertsPerBlock)) return mesh;

        var result = new IvMesh();
        foreach (var src in mesh.Primitives)
        {
            if (src.Vertices.Count <= MaxVertsPerBlock)
            {
                result.Primitives.Add(src);
                continue;
            }

            var chunks = new List<IvPrimitive>();
            var cur = new IvPrimitive { ShaderIndex = src.ShaderIndex };
            var remap = new Dictionary<int, int>();

            for (int t = 0; t + 2 < src.Indices.Count; t += 3)
            {
                int a = src.Indices[t], b = src.Indices[t + 1], c = src.Indices[t + 2];

                // How many of this triangle's corners are new to the current
                // chunk? Counted distinctly, since a degenerate triangle can
                // name the same vertex twice.
                int fresh = 0;
                if (!remap.ContainsKey(a)) fresh++;
                if (b != a && !remap.ContainsKey(b)) fresh++;
                if (c != a && c != b && !remap.ContainsKey(c)) fresh++;

                if (cur.Vertices.Count + fresh > MaxVertsPerBlock && cur.Indices.Count > 0)
                {
                    cur.RecomputeBounds();
                    chunks.Add(cur);
                    cur = new IvPrimitive { ShaderIndex = src.ShaderIndex };
                    remap.Clear();
                }

                cur.Indices.Add(Local(a));
                cur.Indices.Add(Local(b));
                cur.Indices.Add(Local(c));

                int Local(int srcIndex)
                {
                    if (remap.TryGetValue(srcIndex, out var existing)) return existing;
                    int local = cur.Vertices.Count;
                    cur.Vertices.Add(srcIndex >= 0 && srcIndex < src.Vertices.Count
                        ? src.Vertices[srcIndex]
                        : default);
                    remap[srcIndex] = local;
                    return local;
                }
            }

            if (cur.Indices.Count > 0)
            {
                cur.RecomputeBounds();
                chunks.Add(cur);
            }

            Console.WriteLine(
                $"[ydr-writer]   split shader {src.ShaderIndex}: {src.Vertices.Count:N0} verts " +
                $"exceeds the {MaxVertsPerBlock:N0} limit -> {chunks.Count} blocks");
            result.Primitives.AddRange(chunks);
        }
        return result;
    }

    /// <summary>Build the companion reduced-detail asset for a drawable —
    /// GTA IV's actual LOD mechanism, a separate resource the .ide points at.
    ///
    /// Shares the source drawable's shader table (and therefore its embedded
    /// textures) so the pair stays visually consistent. Returns null when the
    /// geometry is too small for reduction to be worth a second file.</summary>
    internal static IvDrawable? BuildLodVariant(IvDrawable source, float ratio, string lodName)
    {
        var high = source.Lods[0];
        if (high == null || high.IsEmpty) return null;

        var reduced = Decimate(high, ratio);
        if (reduced == null || reduced.IsEmpty) return null;

        int before = high.Primitives.Sum(p => p.Indices.Count) / 3;
        int after = reduced.Primitives.Sum(p => p.Indices.Count) / 3;
        // Nothing meaningful was removed — a second asset would just be a
        // duplicate for the streamer to load.
        if (after >= before) return null;

        var lod = new IvDrawable { Name = lodName };
        foreach (var s in source.Shaders)
        {
            var copy = new IvShaderInstance { Preset = s.Preset };
            foreach (var kv in s.Textures)
            {
                // Texture references are relative to the .odr's own folder, so
                // repoint them at the LOD asset's directory.
                copy.Textures[kv.Key] = kv.Value.StartsWith(source.Name + "\\", StringComparison.OrdinalIgnoreCase)
                    ? lodName + kv.Value[source.Name.Length..]
                    : kv.Value;
            }
            lod.Shaders.Add(copy);
        }
        for (int i = 0; i < 4; i++) lod.LodDistances[i] = source.LodDistances[i];
        lod.Lods[0] = SplitOversized(reduced);
        return lod;
    }

    /// <summary>Bind a texture file name into a sampler slot, if the preset
    /// actually declares that sampler.</summary>
    private static void Bind(IvShaderInstance inst, string sampler, string? texName, Options opts)
    {
        if (string.IsNullOrWhiteSpace(texName)) return;
        if (!inst.Preset.Params.Any(p => p.Kind == IvParamKind.Texture
                                      && p.Name.Equals(sampler, StringComparison.OrdinalIgnoreCase)))
            return;

        // Embedded form is "<asset>\<file>.dds" — a path relative to the .odr,
        // which makes the builder pull the pixels into the .wdr. The bare-name
        // form instead defers to a .wtd at runtime.
        inst.Textures[sampler] = opts.EmbedTextures
            ? $"{opts.AssetName}\\{texName}.dds"
            : texName;
    }

    /// <summary>Copy one Assimp mesh's triangles into a primitive, rebasing
    /// indices onto the primitive's existing vertex list.</summary>
    private static void Append(IvPrimitive prim, Mesh mesh)
    {
        int baseIndex = prim.Vertices.Count;
        bool hasNormals = mesh.HasNormals;
        bool hasUv = mesh.HasTextureCoords(0);
        bool hasTangents = mesh.HasTangentBasis;
        bool hasColors = mesh.HasVertexColors(0);

        for (int i = 0; i < mesh.VertexCount; i++)
        {
            var p = mesh.Vertices[i];
            var v = new IvVertex
            {
                Position = new Vector3(p.X, p.Y, p.Z),
                R = 255, G = 255, B = 255, A = 255,
            };

            if (hasNormals)
            {
                var n = mesh.Normals[i];
                v.Normal = new Vector3(n.X, n.Y, n.Z);
            }

            if (hasUv)
            {
                var t = mesh.TextureCoordinateChannels[0][i];
                // Assimp hands back OpenGL-convention V (origin bottom-left).
                // RAGE samples D3D-convention V (origin top-left), the same
                // flip DirectFbxBuilder applies on the GTA V path — without it
                // every texture ships vertically mirrored.
                v.Uv0 = new Vector2(t.X, 1.0f - t.Y);
            }

            if (hasTangents)
            {
                var tn = mesh.Tangents[i];
                var bt = mesh.BiTangents[i];
                var nv = v.Normal;
                var bv = new Vector3(bt.X, bt.Y, bt.Z);

                // GTA IV stores the -U tangent, the opposite of the +U tangent
                // Assimp computes. Measured against Rockstar's own geometry:
                // in level4_bank_lod's tl_bank_whitepanels material — the one
                // tangent-bearing material in either reference prop — the
                // tangent held in the vertex buffer is antiparallel to the
                // standard +U tangent on 10 of its 11 triangles. Passing
                // Assimp's tangent through unchanged lights normal-mapped
                // props backwards along U.
                var tv = -new Vector3(tn.X, tn.Y, tn.Z);

                // W carries handedness: which way the bitangent runs relative
                // to N x T. Derived from the flipped tangent so the basis stays
                // self-consistent.
                float w = Vector3.Dot(Vector3.Cross(nv, tv), bv) < 0f ? -1f : 1f;
                v.Tangent = new Vector4(tv, w);
            }

            if (hasColors)
            {
                var c = mesh.VertexColorChannels[0][i];
                v.R = ToByte(c.R); v.G = ToByte(c.G); v.B = ToByte(c.B); v.A = ToByte(c.A);
            }

            prim.Vertices.Add(v);
        }

        foreach (var face in mesh.Faces)
        {
            // Triangulate ran on import, but a degenerate face can still slip
            // through with fewer than three indices.
            if (face.IndexCount != 3) continue;
            prim.Indices.Add(baseIndex + face.Indices[0]);
            prim.Indices.Add(baseIndex + face.Indices[1]);
            prim.Indices.Add(baseIndex + face.Indices[2]);
        }
    }

    private static byte ToByte(float f) => (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);

    /// <summary>Build a reduced copy of a tier with g3sharp's quadric
    /// edge-collapse reducer, the same one the GTA V embedded-LOD path uses.
    /// Surviving vertices keep their original attributes (normals, UVs,
    /// tangents) — the reducer only decides which ones live.</summary>
    private static IvMesh? Decimate(IvMesh source, float ratio)
    {
        var outMesh = new IvMesh();
        bool anything = false;

        foreach (var src in source.Primitives)
        {
            var dst = new IvPrimitive { ShaderIndex = src.ShaderIndex };
            int triCount = src.Indices.Count / 3;

            // Below this the reduction costs more than it saves and tends to
            // punch holes in small props. Carry the tier through unchanged.
            if (triCount < 32)
            {
                dst.Vertices.AddRange(src.Vertices);
                dst.Indices.AddRange(src.Indices);
                dst.RecomputeBounds();
                outMesh.Primitives.Add(dst);
                if (dst.Indices.Count > 0) anything = true;
                continue;
            }

            int target = (int)Math.Round(triCount * Math.Clamp(ratio, 0.01f, 0.99f));
            if (target < 4) target = 4;

            var dmesh = new DMesh3();
            foreach (var v in src.Vertices)
                dmesh.AppendVertex(new Vector3d(v.Position.X, v.Position.Y, v.Position.Z));
            for (int t = 0; t < triCount; t++)
            {
                int a = src.Indices[t * 3 + 0];
                int b = src.Indices[t * 3 + 1];
                int c = src.Indices[t * 3 + 2];
                if (a == b || b == c || a == c) continue;
                dmesh.AppendTriangle(a, b, c);
            }
            if (dmesh.TriangleCount < 32 || target >= dmesh.TriangleCount)
            {
                dst.Vertices.AddRange(src.Vertices);
                dst.Indices.AddRange(src.Indices);
                dst.RecomputeBounds();
                outMesh.Primitives.Add(dst);
                if (dst.Indices.Count > 0) anything = true;
                continue;
            }

            try
            {
                var reducer = new Reducer(dmesh);
                reducer.ReduceToTriangleCount(target);
            }
            catch (Exception ex)
            {
                // A reducer failure must not take the export with it — ship
                // the tier undecimated and say so.
                Console.WriteLine($"[ydr-writer] LOD reduce failed (shader {src.ShaderIndex}): {ex.Message}");
                dst.Vertices.AddRange(src.Vertices);
                dst.Indices.AddRange(src.Indices);
                dst.RecomputeBounds();
                outMesh.Primitives.Add(dst);
                if (dst.Indices.Count > 0) anything = true;
                continue;
            }

            var denseMap = new Dictionary<int, int>(dmesh.VertexCount);
            foreach (var vi in dmesh.VertexIndices())
            {
                denseMap[vi] = dst.Vertices.Count;
                // Reduced vertices inherit the source attributes of whichever
                // original vertex survived the collapse.
                dst.Vertices.Add(vi >= 0 && vi < src.Vertices.Count ? src.Vertices[vi] : default);
            }
            foreach (var tIdx in dmesh.TriangleIndices())
            {
                var tri = dmesh.GetTriangle(tIdx);
                dst.Indices.Add(denseMap[tri.a]);
                dst.Indices.Add(denseMap[tri.b]);
                dst.Indices.Add(denseMap[tri.c]);
            }

            dst.RecomputeBounds();
            outMesh.Primitives.Add(dst);
            if (dst.Indices.Count > 0) anything = true;
        }

        return anything ? outMesh : null;
    }
}
