// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

namespace YdrWriter.GtaIv;

/// <summary>Maps a GTA V shader preset onto the closest GTA IV one.
///
/// The mapping is narrower than the shader lists suggest. Across a corpus of
/// 140 shipping GTA V drawables (892 shader slots) only 42 distinct presets
/// appear, and seven of them account for 86.6%: normal_spec, default, spec,
/// default_spec, emissive, glass_env, normal. All seven have a direct IV
/// counterpart, so a couple of dozen entries here covers the overwhelming
/// majority of real content.
///
/// Where GTA V has a feature GTA IV never shipped — parallax, detail maps,
/// terrain blending, tint palettes — the mapping degrades to the nearest
/// preset and flags it, so the caller can tell the user what was lost rather
/// than letting it fail silently in game.</summary>
internal static class IvShaderMap
{
    /// <param name="Preset">The GTA IV preset to emit.</param>
    /// <param name="Lost">What the source shader did that IV cannot, or null
    /// when the mapping is faithful.</param>
    internal sealed record Mapping(IvPreset Preset, string? Lost);

    /// <summary>The GTA V shader presets worth recognising by name.
    ///
    /// A .ydr stores its shader as a Jenkins hash, and CodeWalker can only
    /// turn that back into a string if a name dictionary happens to be loaded —
    /// which it is not here, so <c>ShaderFX.FileName.ToString()</c> comes back
    /// as a bare number like "1145906525". Hashing these names ourselves
    /// recovers the mapping without needing an external strings database.</summary>
    private static readonly string[] KnownShaders =
    {
        "default", "default_spec", "default_tnt",
        "normal", "normal_spec", "normal_alpha", "normal_spec_alpha",
        "normal_reflect", "normal_spec_reflect", "normal_cutout", "normal_decal",
        "normal_detail", "normal_spec_detail", "normal_spec_detail_dpm",
        "spec", "spec_alpha", "spec_reflect", "spec_decal", "spec_const",
        "cutout", "cutout_fence", "cutout_spec", "cutout_hard",
        "alpha", "decal", "decal_dirt", "decal_normal_only", "decal_glue",
        "glass", "glass_env", "glass_spec", "glass_reflect", "glass_normal_spec_reflect",
        "glass_emissive", "glass_emissivenight", "glass_breakable",
        "emissive", "emissive_alpha", "emissive_speclum", "emissivenight",
        "emissivestrong", "reflect", "reflect_alpha", "reflect_decal",
        "parallax", "parallax_specmap", "parallax_steep",
        "trees", "trees_normal", "trees_normal_spec", "trees_lod",
        "trees_normal_diffspec", "trees_tnt", "trees_normal_spec_tnt",
        // Shadow/depth-only proxies. GTA V never draws these in the colour
        // pass; they exist so a dense canopy can cast a cheap shadow. They are
        // listed so they RESOLVE - IsShadowProxy then drops the geometry
        // instead of letting an unknown name fall through to gta_default and
        // ship an untextured blob inside the tree.
        "trees_shadow_proxy", "shadow_proxy", "cutout_hard_shadow",
        "grass", "grass_batch", "mirror_default",
        "terrain_cb_w_4lyr", "terrain_cb_w_4lyr_spec", "terrain_cb_4lyr_2tex",
        "water", "water_river", "cloth_default", "cloth_normal_spec",
        "ped", "ped_default", "ped_hair_spiked", "vehicle_paint1", "vehicle_generic",
    };

    /// <summary>True for GTA V shaders that only ever render into the shadow
    /// or depth pass.
    ///
    /// GTA IV has no equivalent, and there is no sensible preset to map one
    /// to: converting it as ordinary geometry puts a solid, untextured shell
    /// in the middle of the model — very visible on vegetation, where the
    /// proxy is a coarse blob wrapped around the canopy. Dropping the
    /// geometry is the faithful translation.</summary>
    internal static bool IsShadowProxy(string? fileName)
    {
        var name = NameFromHashOrText(fileName);
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim().ToLowerInvariant();
        if (name.EndsWith(".sps", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Contains("shadow_proxy") || name.Contains("shadowproxy");
    }

    /// <summary>Jenkins hash → shader name, for the presets above.</summary>
    private static readonly Dictionary<uint, string> ByHash = BuildHashIndex();

    private static Dictionary<uint, string> BuildHashIndex()
    {
        var map = new Dictionary<uint, string>();
        foreach (var n in KnownShaders)
        {
            // RAGE hashes the file name including its extension, lower-cased.
            map[CodeWalker.GameFiles.JenkHash.GenHash(n + ".sps")] = n;
            map[CodeWalker.GameFiles.JenkHash.GenHash(n)] = n;
        }
        return map;
    }

    /// <summary>Turn whatever CodeWalker gave us — a real name or a bare hash
    /// rendered as digits — into a shader name we can map. Returns null when
    /// the hash isn't one we know, so the caller falls back to inferring the
    /// preset from which texture maps are actually present.</summary>
    internal static string? NameFromHashOrText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (uint.TryParse(s, out var hash))
            return ByHash.TryGetValue(hash, out var name) ? name : null;
        return s;
    }

    /// <summary>Resolve a GTA V shader file name (with or without .sps).</summary>
    internal static Mapping Resolve(string? gtaVShader, bool hasNormalMap, bool hasSpecMap)
    {
        var name = (gtaVShader ?? "").Trim();
        if (name.EndsWith(".sps", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        name = name.ToLowerInvariant();

        // Alpha handling is a suffix on most families; detect once.
        bool alpha = name.Contains("_alpha") || name.EndsWith("alpha");
        bool cutout = name.Contains("cutout") || name.Contains("fence")
                   || name.StartsWith("trees") || name.StartsWith("grass");
        bool glass = name.Contains("glass") || name.Contains("mirror");
        bool emissive = name.Contains("emissive");
        bool decal = name.Contains("decal");

        // --- families GTA IV simply does not have -------------------------
        string? lost = null;
        if (name.Contains("parallax")) lost = "parallax (no IV equivalent — renders as flat normal mapping)";
        else if (name.Contains("detail")) lost = "detail map (no IV DetailSampler — detail channel dropped)";
        else if (name.Contains("terrain")) lost = "terrain layer blending (IV cannot blend 4 layers — bake the blend into one diffuse)";
        else if (name.Contains("_tnt") || name.Contains("tint")) lost = "tint palette (no IV TintPaletteSampler — bake the tint into the diffuse)";
        else if (name.StartsWith("ped") || name.Contains("cloth")) lost = "ped/cloth shading (skinned shading has no IV static equivalent)";
        else if (name.StartsWith("vehicle")) lost = "vehicle paint/livery shading";
        else if (name.Contains("water")) lost = "water shading";

        // --- pick the closest preset --------------------------------------
        IvPreset preset;
        if (glass)
        {
            // IV's glass family splits on whether a bump map is present.
            preset = Find(hasNormalMap ? "gta_glass" : "gta_glass_reflect");
        }
        else if (emissive)
        {
            preset = Find(name.Contains("night")
                ? "gta_emissive"          // IV's emissivenight takes different params; plain emissive is the safe pick
                : alpha ? "gta_emissive_alpha" : "gta_emissive");
        }
        else if (decal)
        {
            preset = Find("gta_decal");
        }
        else if (cutout)
        {
            preset = Find(hasNormalMap ? "gta_normal_cutout" : "gta_cutout");
        }
        else if (hasNormalMap && hasSpecMap)
        {
            preset = Find(alpha ? "gta_normal_spec_alpha" : "gta_normal_spec");
        }
        else if (hasNormalMap)
        {
            preset = Find(alpha ? "gta_normal_alpha" : "gta_normal");
        }
        else if (hasSpecMap)
        {
            preset = Find("gta_spec");
        }
        else
        {
            preset = Find(alpha ? "gta_alpha" : "gta_default");
        }

        return new Mapping(preset, lost);
    }

    private static IvPreset Find(string name)
        => IvShaderCatalog.Find(name) ?? IvShaderCatalog.Default;
}
