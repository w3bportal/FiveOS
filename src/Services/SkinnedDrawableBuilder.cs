// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Linq;
using CodeWalker.GameFiles;
using SharpDX;

namespace FiveOS.Services;

/// <summary>
/// Writes a SKINNED ped-clothing drawable (.ydd) — per-vertex blend weights and
/// indices, deforming with the ped skeleton. This is the counterpart to
/// <see cref="YdrWriter"/>'s rigid path, which pins a whole model to one bone.
///
/// Why this exists rather than going through the usual FBX route: CodeWalker's
/// <c>FbxConverter</c> hardcodes two UNSKINNED vertex declarations and cannot be
/// configured, so any skinned output has to build the drawable object graph
/// directly. That is what this does.
///
/// Every constant here was measured against real shipping freemode clothing
/// (~3400 files); the ones that look arbitrary are load-bearing and the comments
/// say why. The whole graph round-trips through Save/reload with all fields
/// intact — see the round-trip check in the Skinned Export dev test.
/// </summary>
public static class SkinnedDrawableBuilder
{
    /// <summary>Ped clothing is always <c>Types = GTAV1</c>; only the Flags word
    /// varies, and it is chosen by the SHADER (whether it needs a tangent /
    /// second UV set), never by the LOD tier.</summary>
    private const uint DeclFlagsPBBNCCTTX = 0x40FF;   // Pos BW BI Nrm C0 C1 T0 T1 Tangent, stride 72

    /// <summary>RAGE gives every vertex exactly four bone influences.</summary>
    public const int MaxInfluences = 4;

    /// <summary>Freemode clothing binds an identity palette over the ped's 128
    /// bones, so a vertex's blend index IS the skeleton bone index. Emitting the
    /// identity keeps us on CodeWalker's renderer fast path too.</summary>
    private const int PaletteSize = 128;

    /// <summary>One vertex of a garment, in GTA space, already weighted.</summary>
    public sealed class Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Tangent = new(1, 0, 0, 1);
        public Vector2 Uv0;
        public Vector2 Uv1;

        /// <summary>Vanilla freemode authors this as R255 G128 B0 A255. Wrong
        /// vertex colours read in game as jittering or permanently wet-looking
        /// cloth, so they are not cosmetic.</summary>
        public Color Colour0 = new(255, 128, 0, 255);
        public Color Colour1 = new(0, 0, 0, 0);

        /// <summary>Skeleton bone indices (0..127), one per influence.</summary>
        public byte[] BoneIndices = new byte[MaxInfluences];

        /// <summary>Fixed-point influences that must sum to exactly 255.
        /// Use <see cref="PackWeights"/> to produce these from float weights.</summary>
        public byte[] Weights = new byte[MaxInfluences];
    }

    /// <summary>External texture names for the ped shader (no extension).</summary>
    public readonly record struct Textures(string Diffuse, string Normal, string Specular);

    /// <summary>
    /// Converts arbitrary float bone weights into the engine's representation:
    /// keep the four strongest, quantize to bytes, and force the sum to exactly
    /// 255. Truncating AFTER normalizing is the classic bug — it leaves the
    /// vertex summing to less than 1 and the mesh visibly shrinks toward the
    /// origin — so the re-normalize happens last, on the kept four.
    /// </summary>
    public static void PackWeights(
        IReadOnlyList<(int boneIndex, float weight)> influences,
        byte[] outIndices, byte[] outWeights)
    {
        Array.Clear(outIndices);
        Array.Clear(outWeights);

        var kept = influences
            .Where(i => i.weight > 0f && i.boneIndex >= 0 && i.boneIndex < PaletteSize)
            .OrderByDescending(i => i.weight)
            .Take(MaxInfluences)
            .ToArray();

        if (kept.Length == 0)
            throw new InvalidOperationException(
                "vertex has no usable bone influence — the caller must supply a fallback bone");

        double total = kept.Sum(k => (double)k.weight);
        int assigned = 0, largest = 0;
        for (int i = 0; i < kept.Length; i++)
        {
            outIndices[i] = (byte)kept[i].boneIndex;
            outWeights[i] = (byte)Math.Round(kept[i].weight / total * 255.0);
            assigned += outWeights[i];
            if (outWeights[i] > outWeights[largest]) largest = i;
        }
        // Rounding drift lands on the dominant influence, where it is invisible.
        outWeights[largest] = (byte)Math.Clamp(outWeights[largest] + (255 - assigned), 0, 255);
    }

    /// <summary>One level of detail: the mesh RAGE draws at that distance.</summary>
    public sealed class Lod
    {
        public required IList<Vertex> Vertices { get; init; }
        public required IList<ushort> Indices { get; init; }
    }

    /// <summary>
    /// Builds a one-drawable clothing dictionary. <paramref name="name"/> is the
    /// component name such as "jbib_042_u" — it becomes both the drawable name
    /// and the dictionary hash key.
    /// </summary>
    public static YddFile Build(string name, IList<Vertex> verts, IList<ushort> indices, Textures textures)
        => Build(name, new[] { new Lod { Vertices = verts, Indices = indices } }, textures);

    /// <summary>
    /// Builds the dictionary with up to three levels of detail, in order
    /// High, Med, Low. Ship only the High tier and the garment disappears once
    /// the player walks away — the most common complaint about custom clothing.
    /// (Clothing never uses the VLow tier.)
    /// </summary>
    public static YddFile Build(string name, IReadOnlyList<Lod> lods, Textures textures)
    {
        if (lods is null || lods.Count == 0) throw new ArgumentException("no LODs", nameof(lods));
        if (lods.Count > 3) throw new ArgumentException("clothing carries at most High/Med/Low", nameof(lods));

        var shader = BuildPedShader(textures);
        var shaderGroup = new ShaderGroup
        {
            VFT = 0x939CC343,
            Shaders = new ResourcePointerArray64<ShaderFX> { data_items = new[] { shader } },
            ShadersCount1 = 1,
            ShadersCount2 = 1,
            TextureDictionary = null,                 // textures resolved from the pack's .ytd by name
        };

        var models = new DrawableModel[lods.Count];
        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        for (int i = 0; i < lods.Count; i++)
        {
            models[i] = BuildModel(lods[i].Vertices, lods[i].Indices, out var lmin, out var lmax);
            bmin = Vector3.Min(bmin, lmin);
            bmax = Vector3.Max(bmax, lmax);
        }

        var centre = (bmin + bmax) * 0.5f;
        var drawable = new Drawable
        {
            Name = name,
            ShaderGroup = shaderGroup,
            // Clothing borrows the ped's skeleton from the .yft at runtime.
            Skeleton = null,
            Joints = null,
            // Save() null-refs deep inside resource layout without this.
            LightAttributes = new ResourceSimpleList64<LightAttributes>(),
            BoundingCenter = centre,
            BoundingSphereRadius = (bmax - centre).Length(),
            BoundingBoxMin = bmin,
            BoundingBoxMax = bmax,
            // Ped LOD is driven by the ped system, not by these distances —
            // real clothing sets all four to the same sentinel.
            LodDistHigh = 9998f,
            LodDistMed = 9998f,
            LodDistLow = 9998f,
            LodDistVlow = 9998f,
            // Note the byte order is the REVERSE of DrawableModel.RenderMaskFlags:
            // here it is (RenderMask << 8) | bucket-mask. BuildRenderMasks() never
            // fills the bucket byte, so it is set by hand. A tier that does not
            // exist stays zero.
            RenderMaskFlagsHigh = 0x0000FF01,
            RenderMaskFlagsMed = lods.Count > 1 ? 0x0000FF01u : 0u,
            RenderMaskFlagsLow = lods.Count > 2 ? 0x0000FF01u : 0u,
            RenderMaskFlagsVlow = 0,
            DrawableModels = new DrawableModelsBlock
            {
                High = new[] { models[0] },
                Med = lods.Count > 1 ? new[] { models[1] } : null,
                Low = lods.Count > 2 ? new[] { models[2] } : null,
            },
        };
        drawable.BuildAllModels();
        drawable.AssignGeometryShaders(shaderGroup);

        var dict = new DrawableDictionary
        {
            Hashes = new[] { JenkHash.GenHash(name) },
            HashesCount1 = 1,
            HashesCount2 = 1,
            Drawables = new ResourcePointerArray64<Drawable> { data_items = new[] { drawable } },
            DrawablesCount1 = 1,
            DrawablesCount2 = 1,
        };

        return new YddFile { DrawableDict = dict, Drawables = new[] { drawable } };
    }

    private static DrawableModel BuildModel(IList<Vertex> verts, IList<ushort> indices,
                                            out Vector3 bmin, out Vector3 bmax)
    {
        if (verts is null || verts.Count == 0) throw new ArgumentException("no vertices", nameof(verts));
        if (verts.Count > ushort.MaxValue)
            throw new ArgumentException($"{verts.Count} vertices exceeds the 65535 per-geometry limit", nameof(verts));
        if (indices is null || indices.Count == 0 || indices.Count % 3 != 0)
            throw new ArgumentException("indices must be a non-empty triangle list", nameof(indices));

        // Types must be assigned BEFORE UpdateCountAndStride — the stride is
        // derived from the packed component types, and a zero Types word yields
        // stride 0 silently, which poisons every buffer downstream.
        var decl = new VertexDeclaration { Flags = DeclFlagsPBBNCCTTX, Types = VertexDeclarationTypes.GTAV1 };
        decl.UpdateCountAndStride();

        // Info must be set BEFORE AllocateData — it sizes the buffer. Without it
        // the allocation no-ops and every Set* call writes into nothing, with no
        // exception anywhere.
        var vd = new VertexData
        {
            Info = decl,
            VertexType = (VertexType)decl.Flags,
            VertexStride = decl.Stride,
        };
        vd.AllocateData(verts.Count);

        bmin = new Vector3(float.MaxValue);
        bmax = new Vector3(float.MinValue);
        for (int i = 0; i < verts.Count; i++)
        {
            var v = verts[i];
            int sum = v.Weights[0] + v.Weights[1] + v.Weights[2] + v.Weights[3];
            if (sum != 255)
                throw new InvalidOperationException(
                    $"vertex {i}: blend weights sum to {sum}, must be 255 — use PackWeights()");
            foreach (var b in v.BoneIndices)
                if (b >= PaletteSize)
                    throw new InvalidOperationException($"vertex {i}: bone index {b} is outside the {PaletteSize}-bone palette");

            vd.SetVector3(i, 0, v.Position);
            vd.SetColour(i, 1, new Color(v.Weights[0], v.Weights[1], v.Weights[2], v.Weights[3]));
            vd.SetColour(i, 2, new Color(v.BoneIndices[0], v.BoneIndices[1], v.BoneIndices[2], v.BoneIndices[3]));
            vd.SetVector3(i, 3, v.Normal);
            vd.SetColour(i, 4, v.Colour0);
            vd.SetColour(i, 5, v.Colour1);
            vd.SetVector2(i, 6, v.Uv0);
            vd.SetVector2(i, 7, v.Uv1);
            vd.SetVector4(i, 14, v.Tangent);

            bmin = Vector3.Min(bmin, v.Position);
            bmax = Vector3.Max(bmax, v.Position);
        }

        var ib = new IndexBuffer { Indices = indices.ToArray() };
        ib.IndicesCount = (uint)ib.Indices.Length;   // not derived for us

        var vb = new VertexBuffer
        {
            VFT = 0x34E6BDBC,
            VertexStride = (ushort)decl.Stride,      // not derived for us
            Flags = 0,
            VertexCount = (uint)verts.Count,
            Info = decl,
            // Data1, Data2 and the geometry's VertexData must all be the SAME
            // instance. Three distinct instances still pass every round-trip
            // check — CodeWalker reads each pointer independently — but RAGE
            // dereferences Data1 and the runtime then sees zero vertices. This
            // is the same silent trap already noted in DrawableLodBuilder.
            Data1 = vd,
            Data2 = vd,
        };

        var geom = new DrawableGeometry
        {
            VFT = 0xEFE7D461,
            ShaderID = 0,
            VertexBuffer = vb,
            VertexData = vd,
            IndexBuffer = ib,
            Unknown_62h = 3,                          // constant across every real file; defaults to 0
            BoneIds = Enumerable.Range(0, PaletteSize).Select(i => (ushort)i).ToArray(),
            BoneIdsCount = PaletteSize,
            VertexStride = (ushort)decl.Stride,
            VerticesCount = (ushort)verts.Count,
            IndicesCount = ib.IndicesCount,
            TrianglesCount = ib.IndicesCount / 3,
            AABB = new AABB_s { Min = new Vector4(bmin, 0), Max = new Vector4(bmax, 0) },
        };

        return new DrawableModel
        {
            VFT = 0x8BDEF787,
            // (BoneIndex 0 << 24) | (0 << 16) | (HasSkin 1 << 8) | palette size.
            // The low byte is the bound-bone count, not padding.
            SkeletonBinding = 0x00000180,
            RenderMaskFlags = 0x01FF,                 // (Flags 1 << 8) | RenderMask 255
            Geometries = new[] { geom },
            GeometriesCount1 = 1,
            GeometriesCount2 = 1,
            GeometriesCount3 = 1,
            ShaderMapping = new ushort[] { 0 },
            // One geometry means one AABB; with N > 1 this array is N + 1 long
            // and index 0 holds the union of them all.
            BoundsData = new[] { geom.AABB },
        };
    }

    /// <summary>The stock freemode "ped" shader. Parameter order matters —
    /// textures first, then vectors — and the sizes are read back off the block
    /// rather than guessed, which reproduces vanilla's 320/400 exactly.</summary>
    private static ShaderFX BuildPedShader(Textures tex)
    {
        var shader = new ShaderFX
        {
            Name = (MetaHash)JenkHash.GenHash("ped"),
            FileName = (MetaHash)540746503u,   // ped.sps — identical in every real clothing .ydd
            RenderBucket = 0,
            RenderBucketMask = 0x0000FF01,
            Unknown_12h = 32768,
        };

        var parameters = new List<ShaderParameter>();
        var hashes = new List<MetaName>();

        void Texture(string sampler, string texture)
        {
            parameters.Add(new ShaderParameter
            {
                DataType = 0,
                Data = new TextureBase { Name = texture, NameHash = JenkHash.GenHash(texture) },
            });
            hashes.Add((MetaName)(uint)JenkHash.GenHash(sampler));
        }

        // DataType on a vector parameter is the number of Vector4 rows, not a type tag.
        void Vector(string param, Vector4 value)
        {
            parameters.Add(new ShaderParameter { DataType = 1, Data = new[] { value } });
            hashes.Add((MetaName)(uint)JenkHash.GenHash(param));
        }

        Texture("diffusesampler", tex.Diffuse);
        Texture("volumesampler", "givemechecker");     // the stock stub every ped shader carries
        Texture("bumpsampler", tex.Normal);
        Texture("specsampler", tex.Specular);
        Vector("umglobalparams", new Vector4(0, 0, 0, 0));
        Vector("envefffatthickness", new Vector4(25, 25, 0, 0));
        Vector("specularintensitymult", new Vector4(1, 0, 0, 0));
        Vector("specularfalloffmult", new Vector4(250, 0, 0, 0));
        Vector("specularfresnel", new Vector4(0.96f, 0, 0, 0));
        Vector("bumpiness", new Vector4(1, 0, 0, 0));
        Vector("detailsettings", new Vector4(0.3f, 0.75f, 75, 0));
        Vector("stubblecontrol", new Vector4(2, 0.6f, 0, 0));

        var block = new ShaderParametersBlock
        {
            Parameters = parameters.ToArray(),
            Hashes = hashes.ToArray(),
            Owner = shader,
        };
        shader.ParametersList = block;
        // Write() emits ParametersList.Count, NOT ShaderFX.ParameterCount — leave
        // this unset and every parameter silently vanishes on reload.
        block.Count = parameters.Count;
        shader.ParameterCount = (byte)parameters.Count;
        shader.TextureParametersCount = (byte)parameters.Count(p => p.DataType == 0);
        shader.ParameterSize = block.ParametersSize;         // computed getters on the block
        shader.ParameterDataSize = block.ParametersDataSize;
        return shader;
    }
}
