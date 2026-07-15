// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Diagnostics;
using Assimp;
using CodeWalker.GameFiles;
using CodeWalker.Utils;

namespace YdrWriter;

/// <summary>
/// Builds the YDR's embedded TextureDictionary and binds shader sampler
/// references after CW's FbxConverter has produced the geometry.
///
/// Why this is separate from the FBX path: CodeWalker.FbxConverter only
/// emits geometry + shader parameter slots — it doesn't pull glTF/FBX
/// embedded textures into the YDR's .ytd. We do that here using Assimp
/// for the source images, texconv for the DDS encode, and DDSIO.GetTexture
/// for the CW Texture object construction.
/// </summary>
public static class TextureBaker
{
    public sealed record MaterialTextures(
        string MaterialName,
        Texture? Diffuse,
        Texture? Normal,
        bool DiffuseHasAlpha);

    /// <summary>
    /// Walks `scene.Materials`, encodes each material's diffuse + normal
    /// (where present) to DDS, and returns one MaterialTextures record per
    /// material in the same order Assimp exposes them.
    ///
    /// `sourceDir` is the directory containing the original input model. Used
    /// to resolve file-referenced texture paths (FBX/OBJ with sidecar PNGs)
    /// — glTF/GLB use embedded "*N" indices and don't need it.
    ///
    /// <paramref name="partDiffuseOverrides"/> maps mesh (part) name → image
    /// path. When present, the override wins over Assimp diffuse/baseColor
    /// for every material used by that mesh (host "Add Missing Textures").
    /// </summary>
    public static List<MaterialTextures> Bake(
        Scene scene, string workDir, string assetName, string sourceDir,
        IReadOnlyDictionary<string, string>? partDiffuseOverrides = null)
    {
        Directory.CreateDirectory(workDir);
        var texconv = LocateTexconv();
        Log($"  texconv: {texconv}");

        // Mesh name → image path collapses to material index → image path
        // because bake is per Assimp material. Shared materials: last
        // matching mesh wins (typical jewelry/prop exports use 1:1).
        Dictionary<int, string>? matDiffuseOverrides = null;
        if (partDiffuseOverrides is { Count: > 0 })
        {
            matDiffuseOverrides = new Dictionary<int, string>();
            for (int mi = 0; mi < scene.MeshCount; mi++)
            {
                var meshName = scene.Meshes[mi].Name;
                if (string.IsNullOrEmpty(meshName)) continue;
                if (!partDiffuseOverrides.TryGetValue(meshName, out var texPath)) continue;
                if (string.IsNullOrEmpty(texPath) || !File.Exists(texPath)) continue;
                int matIdx = scene.Meshes[mi].MaterialIndex;
                if (matIdx < 0) continue;
                matDiffuseOverrides[matIdx] = texPath;
            }
            Log($"  part diffuse overrides: {partDiffuseOverrides.Count} part(s) → {matDiffuseOverrides.Count} material(s)");
        }

        var results = new List<MaterialTextures>();
        for (int i = 0; i < scene.MaterialCount; i++)
        {
            var mat = scene.Materials[i];
            string matName = SanitizeName(mat.Name) ?? $"{assetName}_mat{i:D2}";

            string? diffuseOverride = null;
            matDiffuseOverrides?.TryGetValue(i, out diffuseOverride);

            var (diffuse, diffuseHasAlpha) = ExtractAndEncode(
                scene, mat, "diffuse", workDir, assetName, i, texconv, sourceDir, diffuseOverride);
            var (normal,  _)               = ExtractAndEncode(
                scene, mat, "normal",  workDir, assetName, i, texconv, sourceDir);

            results.Add(new MaterialTextures(matName, diffuse, normal, diffuseHasAlpha));
            Log($"  mat[{i:D2}] '{matName}' -> diffuse={diffuse?.Name ?? "<none>"}{(diffuseHasAlpha ? " (alpha)" : "")} normal={normal?.Name ?? "<none>"}");
        }
        return results;
    }

    /// <summary>
    /// Builds a TextureDictionary from the deduplicated set of textures
    /// across all materials.
    /// </summary>
    public static TextureDictionary BuildDictionary(IEnumerable<MaterialTextures> mats)
    {
        var unique = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mats)
        {
            if (m.Diffuse != null && !unique.ContainsKey(m.Diffuse.Name))
                unique[m.Diffuse.Name] = m.Diffuse;
            if (m.Normal != null && !unique.ContainsKey(m.Normal.Name))
                unique[m.Normal.Name] = m.Normal;
        }

        var list = new ResourcePointerList64<Texture>();
        var hashes = new ResourceSimpleList64_uint();
        var hashList = new List<uint>();
        var texList = new List<Texture>();
        var dict = new Dictionary<uint, Texture>();
        foreach (var t in unique.Values.OrderBy(t => JenkHash.GenHash(t.Name.ToLowerInvariant())))
        {
            var h = JenkHash.GenHash(t.Name.ToLowerInvariant());
            t.NameHash = h;
            hashList.Add(h);
            texList.Add(t);
            dict[h] = t;
        }
        list.data_items = texList.ToArray();
        hashes.data_items = hashList.ToArray();
        var td = new TextureDictionary
        {
            Textures = list,
            TextureNameHashes = hashes,
            Dict = dict,
        };
        return td;
    }

    /// <summary>
    /// Bind each shader's TextureRef parameters to its source material's
    /// textures.
    ///
    /// CW.FbxConverter creates ONE SHADER PER MESH, not per material. Two
    /// meshes that share a material end up with two distinct shader
    /// entries. So shader[i] doesn't map to scene.Materials[i] - it maps
    /// to the i-th mesh that DirectFbxBuilder actually emitted, which is
    /// the i-th SURVIVING entry in scene.Meshes after applying the
    /// excludeMeshNames filter.
    ///
    /// Pre-2026-05 this iterated scene.Meshes by raw index, which broke
    /// any conversion that hid one or more layers: shader[0] got bound
    /// using scene.Meshes[0]'s material even when mesh[0] was excluded,
    /// so the surviving mesh ended up wearing the wrong texture. Symptom
    /// surfaced most obviously in the layer-split flow where every per-
    /// layer YDR came out with mesh[0]'s texture.
    /// </summary>
    public static void BindShaderTextures(
        YdrFile ydr, Scene scene, List<MaterialTextures> matsByMaterial,
        IReadOnlySet<string>? excludeMeshNames = null,
        IReadOnlyDictionary<string, string>? partMaterials = null)
    {
        var shaders = ydr.Drawable.ShaderGroup.Shaders.data_items;
        if (shaders == null) return;

        // Re-derive the kept-mesh order the same way DirectFbxBuilder did
        // when it emitted the FBX. shader[i] corresponds to surviving[i],
        // NOT scene.Meshes[i]. Match must use the same case-insensitive
        // name compare DirectFbxBuilder uses.
        var surviving = new List<int>(scene.MeshCount);
        for (int mi = 0; mi < scene.MeshCount; mi++)
        {
            var name = scene.Meshes[mi].Name;
            if (excludeMeshNames != null && !string.IsNullOrEmpty(name)
                && excludeMeshNames.Contains(name))
                continue;
            surviving.Add(mi);
        }

        int bound = 0, skipped = 0;
        bool glassUsed = false;
        for (int i = 0; i < shaders.Length; i++)
        {
            if (i >= surviving.Count) { skipped++; continue; }
            int meshIdx = surviving[i];
            int srcMatIdx = scene.Meshes[meshIdx].MaterialIndex;
            if (srcMatIdx < 0 || srcMatIdx >= matsByMaterial.Count) { skipped++; continue; }

            string? preset = null;
            var meshName = scene.Meshes[meshIdx].Name;
            if (partMaterials != null && !string.IsNullOrEmpty(meshName))
                partMaterials.TryGetValue(meshName, out preset);
            // Auto-classify glass when the user didn't hand-tag this part in
            // the layers panel. Keeps typical windows/panes exporting with the
            // real glass.sps shader instead of an opaque default — the whole
            // point of KEEP_HIERARCHY keeping meshes separate. Manual --part-mat
            // always wins (checked first).
            preset ??= AutoDetectGlass(meshName, matsByMaterial[srcMatIdx].MaterialName,
                                       matsByMaterial[srcMatIdx].Diffuse?.Name);

            if (string.Equals(preset, "GLASS", StringComparison.OrdinalIgnoreCase))
                glassUsed = true;

            var filled = BindOneShader(shaders[i], matsByMaterial[srcMatIdx], preset);
            Log($"  shader[{i:D2}] <- mesh[{meshIdx:D2}] '{scene.Meshes[meshIdx].Name}' -> mat[{srcMatIdx}] '{matsByMaterial[srcMatIdx].MaterialName}' ({filled} tex, preset={preset ?? "STANDARD"})");
            bound++;
        }
        // The generated glass diffuse GLASS shaders bind must live in the
        // embedded YTD or the engine renders the pane untextured. Add it once.
        if (glassUsed)
        {
            AddTextureToDict(ydr.Drawable.ShaderGroup.TextureDictionary, GetGlassDiffuse());
            AddTextureToDict(ydr.Drawable.ShaderGroup.TextureDictionary, GetGlassNormal());
            Log("  embedded generated glass diffuse + flat normal (fiveos_glass) for see-through panes");
        }
        Log($"  shader bindings: {bound} ok, {skipped} skipped (kept meshes: {surviving.Count} / {scene.MeshCount})");
    }

    // ── Generated glass diffuse ─────────────────────────────────────────
    // A small semi-transparent tint, hand-built as an uncompressed A8R8G8B8
    // DDS (no image library needed). Bound to GLASS shaders so the pane is
    // actually see-through — GTA's glass.sps reads opacity from the diffuse
    // alpha, and a model's own texture is opaque. One shared instance per
    // process (ydr-writer is a one-shot CLI); registered into the YTD once.
    private const string GlassTexName = "fiveos_glass";
    private const string GlassNormalName = "fiveos_glass_n";
    private static Texture? _glassDiffuse;
    private static Texture? _glassNormal;

    /// <summary>Glass appearance 0..1 (see-through → reflective/opaque), set
    /// once per conversion from ConvertOptions before shader binding. Drives
    /// the generated glass alpha and the glass.sps reflection strength.</summary>
    internal static double GlassOpacity = 0.6;

    private static Texture GetGlassDiffuse()
    {
        // Cool light tint. Alpha scales with GlassOpacity: 0 ≈ clear glass,
        // 1 ≈ near-opaque reflective mirror. 45..235 keeps a hint of glass at
        // the clear end and never fully opaque at the mirror end.
        double t = Math.Clamp(GlassOpacity, 0.0, 1.0);
        byte a = (byte)Math.Round(45 + t * (235 - 45));
        return _glassDiffuse ??= BuildSolidTexture(205, 215, 222, a, GlassTexName);
    }

    /// <summary>Flat tangent-space normal (128,128,255) = no bumps. glass.sps
    /// perturbs the surface with its BumpSampler; binding a null/unbound sampler
    /// there makes it read GARBAGE → the speckled "glitch" over see-through
    /// glass. A real flat normal keeps the pane smooth.</summary>
    private static Texture GetGlassNormal() =>
        _glassNormal ??= BuildSolidTexture(128, 128, 255, 255, GlassNormalName);

    /// <summary>Build a solid-colour 64×64 uncompressed A8R8G8B8 texture by
    /// hand (no image library). Used for the generated glass diffuse and its
    /// flat normal.</summary>
    private static Texture BuildSolidTexture(byte r, byte g, byte b, byte a, string name)
    {
        const int S = 64;
        var dds = new byte[128 + S * S * 4];
        dds[0] = (byte)'D'; dds[1] = (byte)'D'; dds[2] = (byte)'S'; dds[3] = (byte)' ';
        void W(int off, uint v)
        { dds[off] = (byte)v; dds[off + 1] = (byte)(v >> 8); dds[off + 2] = (byte)(v >> 16); dds[off + 3] = (byte)(v >> 24); }
        W(4, 124);            // dwSize
        W(8, 0x100F);         // CAPS|HEIGHT|WIDTH|PIXELFORMAT|PITCH
        W(12, S);             // height
        W(16, S);             // width
        W(20, (uint)(S * 4)); // pitch (bytes per row)
        W(76, 32);            // pixelformat size
        W(80, 0x41);          // DDPF_RGB | DDPF_ALPHAPIXELS
        W(88, 32);            // rgb bit count
        W(92, 0x00FF0000);    // R mask
        W(96, 0x0000FF00);    // G mask
        W(100, 0x000000FF);   // B mask
        W(104, 0xFF000000);   // A mask
        W(108, 0x1000);       // DDSCAPS_TEXTURE
        // Pixels in A8R8G8B8 memory order: B, G, R, A.
        for (int i = 0, p = 128; i < S * S; i++)
        { dds[p++] = b; dds[p++] = g; dds[p++] = r; dds[p++] = a; }

        var tex = DDSIO.GetTexture(dds);
        tex.Name = name;
        tex.NameHash = JenkHash.GenHash(name);
        tex.Usage = TextureUsage.UNKNOWN;
        return tex;
    }

    /// <summary>Append a texture to an embedded YTD, keeping the name-hash list
    /// sorted (GTA binary-searches it at lookup). No-op if already present.</summary>
    private static void AddTextureToDict(TextureDictionary? td, Texture tex)
    {
        if (td == null) return;
        td.Dict ??= new Dictionary<uint, Texture>();
        if (td.Dict.ContainsKey(tex.NameHash)) return;
        var texs = (td.Textures?.data_items ?? Array.Empty<Texture>()).ToList();
        var hashes = (td.TextureNameHashes?.data_items ?? Array.Empty<uint>()).ToList();
        texs.Add(tex);
        hashes.Add(tex.NameHash);
        var ordered = texs.Zip(hashes, (t, h) => (t, h)).OrderBy(p => p.h).ToArray();
        td.Textures ??= new ResourcePointerList64<Texture>();
        td.TextureNameHashes ??= new ResourceSimpleList64_uint();
        td.Textures.data_items = ordered.Select(p => p.t).ToArray();
        td.TextureNameHashes.data_items = ordered.Select(p => p.h).ToArray();
        td.Dict[tex.NameHash] = tex;
    }

    // Words that mark a part as glass, and words that mark it as structure
    // (frame / stand / body — never glass). A part is auto-tagged GLASS when
    // any of its names carries a glass word AND its MESH name carries no
    // structure word. The mesh-only structure test is deliberate: a cheval
    // mirror names every mesh "mirrorA…" (mirrorAMirror, mirrorAFrame,
    // mirrorALegs, …) and shares ONE atlas material, so the material/texture
    // name can't separate the pane from the frame — only the mesh suffix can.
    private static readonly string[] GlassWords =
        { "glass", "window", "windshield", "windscreen", "windowpane", "plexi", "acrylic", "mirror" };
    private static readonly string[] StructureWords =
        { "frame", "arm", "leg", "base", "plate", "back", "stand", "foot", "body",
          "handle", "rim", "trim", "post", "mount", "bracket", "hinge", "wood",
          "metal", "border", "surround", "casing", "housing", "pillar", "rail", "screw", "bolt" };

    private static bool ContainsAny(string s, string[] words)
    {
        foreach (var w in words) if (s.Contains(w, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Heuristic glass classifier for parts the user didn't tag.
    /// Returns "GLASS" or null. Glass word in any name + no structure word in
    /// the MESH name. Errs toward NOT tagging (a mis-tag makes a solid part
    /// see-through, which is worse than leaving a pane opaque — the user can
    /// still tag it manually).</summary>
    internal static string? AutoDetectGlass(string? meshName, string? materialName, string? diffuseName)
    {
        string mesh = (meshName ?? "").ToLowerInvariant();
        bool hasGlass = ContainsAny(mesh, GlassWords)
                     || ContainsAny((materialName ?? "").ToLowerInvariant(), GlassWords)
                     || ContainsAny((diffuseName ?? "").ToLowerInvariant(), GlassWords);
        bool meshIsStructure = ContainsAny(mesh, StructureWords);
        return (hasGlass && !meshIsStructure) ? "GLASS" : null;
    }

    private static int BindOneShader(CodeWalker.GameFiles.ShaderFX s, MaterialTextures texs, string? preset = null)
    {
        if (s.ParametersList?.Parameters == null) return 0;

        // Material preset (glass / emissive variants) takes priority over
        // the alpha-detect heuristic. The preset's render bucket + shader
        // name match the canonical Sollumz Shaders.xml entries — see plan.
        (string? name, string? file, byte bucket) shaderOverride = preset switch
        {
            "GLASS"          => ("glass",          "glass.sps",          (byte)1),
            "EMISSIVE"       => ("emissive",       "emissive.sps",       (byte)0),
            "EMISSIVESTRONG" => ("emissivestrong", "emissivestrong.sps", (byte)0),
            "EMISSIVENIGHT"  => ("emissivenight",  "emissivenight.sps",  (byte)0),
            _                => (null, null, (byte)0),
        };

        if (shaderOverride.name != null)
        {
            s.Name         = JenkHash.GenHash(shaderOverride.name);
            s.FileName     = JenkHash.GenHash(shaderOverride.file!);
            s.RenderBucket = shaderOverride.bucket;
            // For non-Standard presets, REBUILD the ParametersList from
            // scratch — FbxConverter laid out parameters keyed on default.sps
            // (wetnessMultiplier in slot 4, no BumpSampler, no envmap). CW's
            // RPF viewer crashes when the shader name says e.g. emissivestrong
            // but the parameter hashes belong to default. Sollumz-canonical
            // layouts (from Shaders.xml) are baked here.
            s.ParametersList = BuildPresetParametersBlock(s, preset!, texs);
            s.ParameterCount        = (byte)s.ParametersList.Count;
            s.ParameterSize         = s.ParametersList.ParametersSize;
            s.ParameterDataSize     = s.ParametersList.ParametersDataSize;
            s.TextureParametersCount = s.ParametersList.TextureParamsCount;
            // RenderBucketMask is derived from RenderBucket; keep them in sync
            // so the binary matches what CW emits for native YDRs.
            s.RenderBucketMask = (1u << shaderOverride.bucket) | 0xFF00u;
            return s.ParametersList.TextureParamsCount;
        }
        else if (texs.DiffuseHasAlpha)
        {
            // If the diffuse has alpha, swap the shader from the default
            // opaque (`normal.sps`, bucket 0) to the cutout variant
            // (`normal_decal.sps`, bucket 1) so RAGE actually samples alpha.
            // Otherwise the alpha bytes we just preserved with BC3 still
            // wouldn't reach the rasterizer — `normal.sps` ignores them
            // and the geometry still renders opaque, casting a hard shadow
            // exactly like the broken laptop-screen YDR.
            const string AlphaShaderName = "normal_decal";
            const string AlphaShaderFile = "normal_decal.sps";
            s.Name     = JenkHash.GenHash(AlphaShaderName);
            s.FileName = JenkHash.GenHash(AlphaShaderFile);
            s.RenderBucket = 1; // alpha-tested cutout
        }

        // Slot 0 = DiffuseSampler, slot 1 = BumpSampler in CW's normal.sps
        // (verified via hash-resolved param dump). Anything past slot 1 is
        // a vector parameter we leave at the engine default.
        int slot = 0, filled = 0;
        foreach (var p in s.ParametersList.Parameters)
        {
            if (p.DataType != 0) continue; // 0 = TextureRef
            object? tex = slot switch
            {
                0 => (object?)texs.Diffuse,
                1 => (object?)(texs.Normal ?? texs.Diffuse),
                _ => null,
            };
            if (tex != null) { p.Data = tex; filled++; }
            slot++;
        }
        return filled;
    }

    /// <summary>Construct a fresh <see cref="CodeWalker.GameFiles.ShaderParametersBlock"/>
    /// matching the canonical Sollumz layout for the chosen preset. Slot
    /// order, hashes, data types, and default values all come from
    /// <c>szio/src/szio/gta5/Shaders.xml</c>. Diffuse + bump textures from
    /// the source material are wired into the right sampler slots.</summary>
    private static CodeWalker.GameFiles.ShaderParametersBlock BuildPresetParametersBlock(
        CodeWalker.GameFiles.ShaderFX owner, string preset, MaterialTextures texs)
    {
        var block = new CodeWalker.GameFiles.ShaderParametersBlock { Owner = owner };

        static CodeWalker.GameFiles.ShaderParameter Tex(Texture? t) =>
            new() { DataType = 0, Data = t };
        static CodeWalker.GameFiles.ShaderParameter Vec(float x, float y = 0, float z = 0, float w = 0) =>
            new() { DataType = 1, Data = new SharpDX.Vector4(x, y, z, w) };
        static CodeWalker.GameFiles.MetaName H(string name) =>
            (CodeWalker.GameFiles.MetaName)JenkHash.GenHash(name);

        switch (preset)
        {
            case "GLASS":
                // glass.sps — 3 textures + 6 floats. glass.sps takes its
                // TRANSPARENCY from the diffuse alpha channel; a model's own
                // texture is almost always fully opaque, so the pane would
                // render solid. Bind a generated semi-transparent glass diffuse
                // instead (added to the embedded YTD by BindShaderTextures) so
                // the glass is actually see-through regardless of the source.
                // More opaque glass reads as a mirror: crank the env reflection
                // and specular as GlassOpacity climbs toward 1.
                float gt = (float)Math.Clamp(GlassOpacity, 0.0, 1.0);
                float reflectivePower = 0.35f + gt * 0.95f;   // 0.35 → 1.30
                float specIntensity   = 0.6f  + gt * 1.4f;    // 0.6  → 2.0
                block.Parameters = new[]
                {
                    Tex(GetGlassDiffuse()),
                    Tex(GetGlassNormal()),      // BumpSampler — flat normal (NOT null: an
                                                // unbound bump sampler reads garbage → glitch)
                    Tex(null),                  // EnvironmentSampler — engine picks the global cubemap
                    Vec(0),                     // useTessellation
                    Vec(reflectivePower),       // reflectivePower — scales with GlassOpacity
                    Vec(1.0f),                  // bumpiness
                    Vec(specIntensity),         // specularIntensityMult
                    Vec(300f),                  // specularFalloffMult
                    Vec(0.96f),                 // specularFresnel
                };
                block.Hashes = new[]
                {
                    H("DiffuseSampler"), H("BumpSampler"), H("EnvironmentSampler"),
                    H("useTessellation"), H("reflectivePower"), H("bumpiness"),
                    H("specularIntensityMult"), H("specularFalloffMult"), H("specularFresnel"),
                };
                break;
            case "EMISSIVE":
            case "EMISSIVESTRONG":
            case "EMISSIVENIGHT":
                // emissive(strong/night).sps — 1 texture + 6 vectors. Slot 4
                // is emissiveMultiplier — the magnitude is what makes the
                // model actually glow. Values from Shaders.xml defaults.
                float emis = preset switch
                {
                    "EMISSIVESTRONG" => 16f,
                    _                => 1f,
                };
                block.Parameters = new[]
                {
                    Tex(texs.Diffuse),
                    Vec(1, 0, 0, 1),    // matMaterialColorScale
                    Vec(1),             // HardAlphaBlend
                    Vec(0),             // useTessellation
                    Vec(emis),          // emissiveMultiplier
                    Vec(0, 1, 0),       // globalAnimUV1
                    Vec(1, 0, 0),       // globalAnimUV0
                };
                block.Hashes = new[]
                {
                    H("DiffuseSampler"), H("matMaterialColorScale"), H("HardAlphaBlend"),
                    H("useTessellation"), H("emissiveMultiplier"),
                    H("globalAnimUV1"), H("globalAnimUV0"),
                };
                break;
            default:
                // Should be unreachable — the caller already filtered out
                // STANDARD. Fall back to a single-diffuse layout matching
                // default.sps so we never emit an empty parameter block.
                block.Parameters = new[] { Tex(texs.Diffuse) };
                block.Hashes = new[] { H("DiffuseSampler") };
                break;
        }
        // Guard: RAGE reads N parameter headers followed by N hashes, so the
        // two arrays MUST be equal length. They're hand-written parallel
        // literals ~15 lines apart per case; a one-sided future edit would
        // silently emit a block whose header count ≠ hash count → structural
        // corruption → client crash on load. Fail loudly at build instead.
        if (block.Parameters.Length != block.Hashes.Length)
            throw new InvalidOperationException(
                $"shader preset '{preset}' param/hash desync: {block.Parameters.Length} params vs {block.Hashes.Length} hashes");
        block.Count = block.Parameters.Length;
        return block;
    }

    // ---------------------------------------------------------------------

    private static (Texture? Texture, bool HasAlpha) ExtractAndEncode(
        Scene scene, Assimp.Material mat, string role,
        string workDir, string assetName, int matIdx, string texconv, string sourceDir,
        string? diffuseOverridePath = null)
    {
        byte[]? srcBytes = null;
        string srcExt = ".png";

        // Host "Add Missing Textures" / layer swap — absolute image path wins
        // over whatever Assimp found (or failed to find) for this material.
        if (role == "diffuse"
            && !string.IsNullOrEmpty(diffuseOverridePath)
            && File.Exists(diffuseOverridePath))
        {
            srcBytes = File.ReadAllBytes(diffuseOverridePath);
            srcExt = ExtForOverride(diffuseOverridePath, srcBytes);
            Log($"  mat[{matIdx:D2}] diffuse override ← {Path.GetFileName(diffuseOverridePath)}");
        }
        else
        {
            // Meshy (and most modern glTF/FBX exporters) author PBR
            // metallic-roughness materials. Assimp exposes the base-colour map
            // under aiTextureType_BASE_COLOR and the normal map under
            // NORMAL_CAMERA — NOT the legacy DIFFUSE/NORMALS slots. So
            // HasTextureDiffuse is false and the model comes out untextured even
            // though the texture is right there (embedded "*N" or a sidecar).
            // Fall back to the PBR slots when the legacy ones are empty.
            var slot = role switch
            {
                "diffuse" => mat.HasTextureDiffuse ? (TextureSlot?)mat.TextureDiffuse
                           : PbrSlot(mat, TextureType.BaseColor),
                "normal"  => mat.HasTextureNormal  ? (TextureSlot?)mat.TextureNormal
                           : PbrSlot(mat, TextureType.NormalCamera),
                _ => null,
            };
            if (slot == null) return (null, false);
            var path = slot.Value.FilePath ?? "";

            // Source-data paths to support:
            //   1. EMBEDDED textures — glTF "*N" indices AND FBX ".fbm/Image_N"
            //      virtual paths. scene.GetEmbeddedTexture resolves EITHER form to
            //      the actual EmbeddedTexture (CompressedData = raw JPEG/PNG).
            //      Meshy FBX exports embed as ".fbm/Image_0.jpg" — the old "*N"-only
            //      check missed those, so every AI model came out untextured.
            //   2. EXTERNAL sidecar files (FBX/OBJ referencing real PNGs on disk).
            var emb = scene.GetEmbeddedTexture(path);
            if (emb != null && emb.HasCompressedData && emb.CompressedData is { Length: > 0 })
            {
                srcBytes = emb.CompressedData;
                srcExt = NormalizeExt(emb.CompressedFormatHint);
            }
            if (srcBytes == null && !string.IsNullOrEmpty(path))
            {
                srcBytes = ReadExternalTexture(path, sourceDir);
                if (srcBytes != null) srcExt = SniffImageExt(srcBytes);
            }
        }
        if (srcBytes == null) return (null, false);

        string suffix = role == "normal" ? "_n" : "";
        string baseName = $"{assetName}_mat{matIdx:D2}{suffix}";
        // IMPORTANT: write with the REAL image extension. texconv picks its
        // input codec from the extension, so a JPEG written as ".png" fails to
        // decode and the model comes out untextured. Meshy diffuse maps are JPEG.
        var srcImgPath = Path.Combine(workDir, baseName + srcExt);
        File.WriteAllBytes(srcImgPath, srcBytes);

        // Diffuse with an alpha channel must be encoded as BC3 (8-bit alpha).
        // JPEG can't carry alpha, so only probe PNG sources.
        bool hasAlpha = role == "diffuse" && srcExt == ".png" && PngHasAlphaChannel(srcBytes);

        // Use DX9 (legacy DDS9) format — CW.Core's TextureFormat enum doesn't
        // distinguish sRGB at the format level (no D3DFMT_DXT1_SRGB). The
        // sRGB-decode in GTA's shader is driven by the Texture.Usage tag
        // (DIFFUSE vs NORMAL vs SPECULAR), which we set after DDSIO returns.
        var fmt = role == "normal" ? "BC5_UNORM"
                : hasAlpha          ? "BC3_UNORM"
                                    : "BC1_UNORM";
        var ddsPath = Path.Combine(workDir, baseName + ".dds");
        var args = new List<string> {
            "-y", "-nologo",
            "-f", fmt,
            "-m", "0", "-pow2", "-fl", "11.0",
            "-dx9",
            "-o", workDir, srcImgPath,
        };

        var psi = new ProcessStartInfo
        {
            FileName = texconv,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = Process.Start(psi);
        if (proc == null) return (null, false);
        proc.WaitForExit(30_000);
        if (proc.ExitCode != 0)
        {
            Log($"  texconv failed for {baseName}: {proc.StandardError.ReadToEnd().Trim()[..Math.Min(200, proc.StandardError.ReadToEnd().Length)]}");
            return (null, false);
        }
        if (!File.Exists(ddsPath)) return (null, false);

        var ddsBytes = File.ReadAllBytes(ddsPath);
        Texture cwTex;
        try
        {
            cwTex = DDSIO.GetTexture(ddsBytes);
        }
        catch (Exception ex)
        {
            Log($"  DDSIO.GetTexture failed for {baseName}: {ex.Message}");
            return (null, false);
        }
        cwTex.Name = baseName;
        cwTex.NameHash = JenkHash.GenHash(baseName.ToLowerInvariant());

        // Set Usage=UNKNOWN to match Sollumz/CodeWalker default behavior for
        // legacy DDS9 (D3DFMT_DXT1) art assets. Setting Usage=DIFFUSE causes
        // GTA's shader to apply an sRGB->linear gamma decode on top of the
        // already-sRGB-encoded BC1 data, double-darkening the result.
        // Verified against Sollumz-generated working YDR for the same model
        // (gen8/deadnaut.ydr.xml — every diffuse + normal entry uses
        // <Usage>UNKNOWN</Usage>).
        cwTex.Usage = TextureUsage.UNKNOWN;
        return (cwTex, hasAlpha);
    }

    /// <summary>Get a PBR texture slot (BaseColor / NormalCamera / …) from a
    /// material, or null if it has none. Used as the fallback when the legacy
    /// diffuse/normal slots are empty (the norm for glTF/FBX PBR exports).</summary>
    private static TextureSlot? PbrSlot(Assimp.Material mat, TextureType type)
        => mat.GetMaterialTexture(type, 0, out var s) && !string.IsNullOrEmpty(s.FilePath)
            ? s : (TextureSlot?)null;

    /// <summary>Map an Assimp CompressedFormatHint ("jpg"/"png"/…) to a file
    /// extension texconv understands. Defaults to .png.</summary>
    private static string NormalizeExt(string? hint) =>
        (hint ?? "").TrimStart('.').ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => ".jpg",
            "png"           => ".png",
            "bmp"           => ".bmp",
            "tga"           => ".tga",
            "tif" or "tiff" => ".tif",
            "dds"           => ".dds",
            _               => ".png",
        };

    /// <summary>Sniff an image's type from its magic bytes → file extension.</summary>
    private static string SniffImageExt(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
        if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return ".bmp";
        if (b.Length >= 4 && b[0] == 0x44 && b[1] == 0x44 && b[2] == 0x53 && b[3] == 0x20) return ".dds";
        return ".png";
    }

    /// <summary>Resolve a path Assimp gave us in a TextureSlot to actual
    /// file bytes. FBX/OBJ store these as relative paths (most common) or
    /// absolute paths (DCC tools that bake the source rig location into the
    /// FBX). We try, in order: as-is if absolute, then relative to the
    /// source model's directory, then bare filename in the source directory
    /// (handles backslash-vs-forward-slash and stripped subdirs).</summary>
    private static byte[]? ReadExternalTexture(string path, string sourceDir)
    {
        try
        {
            if (Path.IsPathRooted(path) && File.Exists(path))
                return File.ReadAllBytes(path);

            var combined = Path.Combine(sourceDir, path);
            if (File.Exists(combined)) return File.ReadAllBytes(combined);

            // Some FBXs store paths like "..\textures\body.png" with mixed
            // separators or stale subdir refs that don't exist on this disk.
            // Fallback: bare filename next to the model, then common texture
            // subfolders artists leave beside the source file.
            var bare = Path.GetFileName(path.Replace('\\', '/'));
            if (string.IsNullOrEmpty(bare)) return null;
            var alt = Path.Combine(sourceDir, bare);
            if (File.Exists(alt)) return File.ReadAllBytes(alt);

            foreach (var sub in new[] { "textures", "Textures", "tex", "maps", "Maps" })
            {
                var nested = Path.Combine(sourceDir, sub, bare);
                if (File.Exists(nested)) return File.ReadAllBytes(nested);
            }
        }
        catch { /* fall through to null = "treat as missing" */ }
        return null;
    }

    /// <summary>Pick a texconv-friendly extension for a host-supplied
    /// override image. Prefer magic-byte sniff for png/jpg (Meshy often
    /// ships JPEG bytes under a .png name); keep the file extension for
    /// formats sniff doesn't cover (TGA/DDS).</summary>
    private static string ExtForOverride(string path, byte[] bytes)
    {
        var sniffed = SniffImageExt(bytes);
        if (sniffed is ".jpg" or ".png" or ".bmp" or ".dds") return sniffed;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ".jpg",
            ".png"            => ".png",
            ".bmp"            => ".bmp",
            ".tga"            => ".tga",
            ".tif" or ".tiff" => ".tif",
            ".dds"            => ".dds",
            _                 => sniffed,
        };
    }

    /// <summary>True iff the PNG declares an RGBA / gray+alpha color type
    /// in its IHDR chunk. We only check the header byte (offset 25), not
    /// actual pixel values — conservative: a fully-opaque RGBA PNG still
    /// gets BC3, costing 2x storage but never silently dropping alpha.</summary>
    private static bool PngHasAlphaChannel(byte[] png)
    {
        if (png == null || png.Length < 26) return false;
        if (png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return false;
        var colorType = png[25];
        return colorType == 4 || colorType == 6;
    }

    private static string LocateTexconv()
    {
        var here = AppContext.BaseDirectory;
        foreach (var rel in new[] { "tools/texconv.exe", "../tools/texconv.exe", "Engine/tools/texconv.exe" })
        {
            var c = Path.Combine(here, rel);
            if (File.Exists(c)) return Path.GetFullPath(c);
        }
        return "texconv.exe";
    }

    private static string? SanitizeName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var chars = raw.ToLowerInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_')
                chars[i] = '_';
        return new string(chars).Trim('_');
    }

    private static void Log(string s) => Console.WriteLine($"[ydr-writer] {s}");
}
