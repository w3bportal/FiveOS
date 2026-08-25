// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

namespace YdrWriter.GtaIv;

/// <summary>Kind of a shader parameter as it appears on an .odr shader line.
/// Textures render as a name (or a model-relative .dds path when embedded),
/// floats as a single F8 value, vector4s as four F8 values joined by ';'.</summary>
internal enum IvParamKind
{
    Texture,
    Float,
    Vector4,
}

/// <summary>One dynamic parameter of a GTA IV shader preset, in the order it
/// must be written on the .odr shader line.</summary>
/// <param name="Kind">How the value is serialised.</param>
/// <param name="Name">The RAGE sampler/param name (texturesampler, bumpiness, …).
/// Texture params are matched against this name to decide which of the
/// material's maps fills the slot.</param>
/// <param name="Default">Value used when the material carries nothing for this
/// slot. For textures this is "null" — the literal RAGE writes for an
/// unbound sampler.</param>
internal sealed record IvShaderParam(IvParamKind Kind, string Name, string Default);

/// <summary>A GTA IV shader preset and the exact parameter contract
/// ResourceBuilder expects for it.</summary>
internal sealed record IvPreset(string FileName, IReadOnlyList<IvShaderParam> Params);

/// <summary>The subset of GTA IV's 94 shader presets FiveOS emits, with each
/// preset's dynamic-parameter list.
///
/// The contract is not free-form: ResourceBuilder reads
/// <c>Games\IV\OpenFormats\ShaderManager.xml</c>, walks that preset's
/// &lt;Params&gt; in document order, and consumes one whitespace-separated
/// token per parameter that has NO &lt;StaticValue&gt;. Parameters that do
/// carry a StaticValue (globalanimuv0/1, shadowmap_res, facetmask,
/// luminanceconstants …) are filled in by the builder and must NOT appear on
/// the line. Emit the wrong count and the builder fails hard — see
/// <see cref="ResourceBuilderRunner"/> for how that failure surfaces.
///
/// The lists below were lifted directly from ShaderManager.xml and checked
/// against shader lines decompiled out of shipping GTA IV assets, e.g.
///   gta_normal_spec.sps tex 60.00000000 0.80000001 1.00000000;0.00000000;0.00000000;0.00000000 1.20000005 tex_N tex_S
/// which is exactly texturesampler, specularfactor, specularcolorfactor,
/// specmapintmask, bumpiness, bumpsampler, specsampler.
///
/// Defaults for the numeric slots are picked to match the values Rockstar's
/// own props use rather than zeroes — a 0 specularfactor renders visibly
/// flat-lit next to stock geometry.</summary>
internal static class IvShaderCatalog
{
    // Sampler names the builder recognises. Kept as constants because they
    // double as the lookup key when binding a material's maps to slots.
    internal const string DiffuseSampler = "texturesampler";
    internal const string BumpSampler    = "bumpsampler";
    internal const string SpecSampler    = "specsampler";
    internal const string EnvSampler     = "environmentsampler";

    private static IvShaderParam Tex(string name) => new(IvParamKind.Texture, name, "null");
    private static IvShaderParam Flt(string name, double v) =>
        new(IvParamKind.Float, name, v.ToString("F8", System.Globalization.CultureInfo.InvariantCulture));
    private static IvShaderParam Vec(string name, string v) => new(IvParamKind.Vector4, name, v);

    // RAGE's spec-map intensity mask. (1,0,0,0) = take specular intensity from
    // the red channel, which is what every stock spec map is authored against.
    private const string SpecMaskRed = "1.00000000;0.00000000;0.00000000;0.00000000";

    private static readonly Dictionary<string, IvPreset> Presets = new(StringComparer.OrdinalIgnoreCase);

    static IvShaderCatalog()
    {
        // --- Unlit/diffuse-only family. One texture, nothing else. ---------
        foreach (var n in new[] { "gta_default.sps", "gta_alpha.sps", "gta_cutout.sps", "gta_decal.sps" })
            Add(new IvPreset(n, new[] { Tex(DiffuseSampler) }));

        // --- Normal-mapped, no spec map. -----------------------------------
        foreach (var n in new[] { "gta_normal.sps", "gta_normal_alpha.sps", "gta_normal_cutout.sps" })
            Add(new IvPreset(n, new[]
            {
                Tex(DiffuseSampler),
                Flt("specularfactor", 30.0),
                Flt("specularcolorfactor", 0.4),
                Flt("bumpiness", 1.0),
                Tex(BumpSampler),
            }));

        // --- Normal + spec map. --------------------------------------------
        foreach (var n in new[] { "gta_normal_spec.sps", "gta_normal_spec_alpha.sps" })
            Add(new IvPreset(n, new[]
            {
                Tex(DiffuseSampler),
                Flt("specularfactor", 60.0),
                Flt("specularcolorfactor", 0.8),
                Vec("specmapintmask", SpecMaskRed),
                Flt("bumpiness", 1.0),
                Tex(BumpSampler),
                Tex(SpecSampler),
            }));

        // --- Spec map, no normal map. --------------------------------------
        Add(new IvPreset("gta_spec.sps", new[]
        {
            Tex(DiffuseSampler),
            Flt("specularfactor", 60.0),
            Flt("specularcolorfactor", 0.8),
            Vec("specmapintmask", SpecMaskRed),
            Tex(SpecSampler),
        }));

        // --- Glass. Reflective, alpha-bucketed. -----------------------------
        Add(new IvPreset("gta_glass.sps", new[]
        {
            Tex(DiffuseSampler),
            Flt("specularfactor", 20.0),
            Flt("specularcolorfactor", 0.6),
            Flt("bumpiness", 1.0),
            Flt("reflectivepower", 1.2),
            Tex(BumpSampler),
            Tex(EnvSampler),
        }));
        Add(new IvPreset("gta_glass_reflect.sps", new[]
        {
            Tex(DiffuseSampler),
            Flt("specularfactor", 20.0),
            Flt("specularcolorfactor", 0.6),
            Flt("reflectivepower", 1.2),
            Tex(EnvSampler),
        }));

        // --- Emissive. zshift nudges the surface toward the camera to stop
        //     self-Z-fighting on decal-like emissive panels. ----------------
        foreach (var n in new[] { "gta_emissive.sps", "gta_emissive_alpha.sps" })
            Add(new IvPreset(n, new[]
            {
                Tex(DiffuseSampler),
                Flt("zshift", 0.0),
                Flt("emissivemultiplier", 1.0),
            }));

        static void Add(IvPreset p) => Presets[p.FileName] = p;
    }

    /// <summary>Look a preset up by file name (with or without the .sps
    /// suffix). Returns null when the preset isn't one we model — callers
    /// fall back to <see cref="Default"/> rather than guessing a param
    /// contract, because a wrong guess is a hard builder failure.</summary>
    internal static IvPreset? Find(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var key = fileName.Trim();
        if (!key.EndsWith(".sps", StringComparison.OrdinalIgnoreCase)) key += ".sps";
        return Presets.TryGetValue(key, out var p) ? p : null;
    }

    internal static IvPreset Default => Presets["gta_default.sps"];

    /// <summary>Every preset name this catalog can emit. Used by the CLI to
    /// validate <c>--iv-shader</c> before doing any work.</summary>
    internal static IEnumerable<string> Names => Presets.Keys.OrderBy(k => k, StringComparer.Ordinal);

    /// <summary>Pick a preset from what the material actually carries.
    /// Mirrors the GTA V path's preset selection so a model exported to both
    /// games lands on equivalent shading.</summary>
    internal static IvPreset Select(bool hasNormal, bool hasSpec, bool isGlass, bool hasAlpha)
    {
        if (isGlass) return hasNormal ? Presets["gta_glass.sps"] : Presets["gta_glass_reflect.sps"];
        if (hasNormal && hasSpec) return Presets[hasAlpha ? "gta_normal_spec_alpha.sps" : "gta_normal_spec.sps"];
        if (hasNormal)            return Presets[hasAlpha ? "gta_normal_alpha.sps" : "gta_normal.sps"];
        if (hasSpec)              return Presets["gta_spec.sps"];
        return Presets[hasAlpha ? "gta_alpha.sps" : "gta_default.sps"];
    }
}
