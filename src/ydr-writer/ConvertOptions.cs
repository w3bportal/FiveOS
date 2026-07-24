// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

namespace YdrWriter;

public sealed class ConvertOptions
{
    public required string InputPath { get; init; }
    public required string OutputDir { get; init; }
    public required string AssetName { get; init; }
    public string Up { get; init; } = "auto";
    public string CollisionMaterial { get; init; } = "CONCRETE";
    public bool IncludeCollision { get; init; } = true;
    /// <summary>When true and <see cref="IncludeCollision"/> is also true,
    /// the BoundComposite is assigned to <c>Drawable.Bound</c> inside the
    /// YDR (single-file collision) and no external .ybn is written. Off
    /// preserves the legacy behaviour of writing a sibling .ybn.</summary>
    public bool EmbedCollision { get; init; }
    /// <summary>When true, glass parts (tagged or auto-detected) get the
    /// <c>GLASS_SHOOT_THROUGH</c> collision material so bullets pass through
    /// and the engine plays the glass-shatter VFX + sound on hit. Off leaves
    /// glass solid (the global collision material). NOTE: this is shatter-on-
    /// shot behaviour, not a true breakable .yft fragment (physical shards).</summary>
    public bool BreakableGlass { get; init; }
    /// <summary>Glass appearance 0..1: 0 = clear see-through, 1 = opaque and
    /// reflective (mirror-like). Drives the generated glass diffuse's alpha
    /// AND the glass.sps reflection strength. Default leans slightly present
    /// so glass doesn't vanish.</summary>
    public double GlassOpacity { get; init; } = 0.6;
    public bool IncludeYtyp { get; init; } = true;
    public bool ExtractTextures { get; init; } = true;
    /// <summary>When true, and the source ships a rig + animation clip, the
    /// converter attaches a RAGE <c>Skeleton</c> to the drawable and binds
    /// each model to a bone (rigid bone-binding) so the prop can be driven
    /// in-game via <c>PlayEntityAnim</c>. Writes a matching <c>.ycd</c> +
    /// <c>client.lua</c>. Falls back to a plain static prop if the source
    /// has no rig/animation. See <see cref="AnimatedPropBuilder"/>.</summary>
    public bool AnimatedProp { get; init; }
    /// <summary>When true, the converter SYNTHESIZES a spin animation for a
    /// model that has none: one rotation bone at the centroid, the whole
    /// model bound to it, and a 360° clip around <see cref="SpinAxis"/> over
    /// <see cref="SpinSeconds"/>. Implies the animated-prop pipeline. Lets a
    /// plain gear/fan/wheel spin with no pre-animated source.</summary>
    public bool AutoSpin { get; init; }
    /// <summary>Spin axis for <see cref="AutoSpin"/>: "X", "Y" or "Z"
    /// (GTA/Z-up space; Z = vertical). Default Z.</summary>
    public string SpinAxis { get; init; } = "Z";
    /// <summary>Seconds per full revolution for <see cref="AutoSpin"/>.</summary>
    public double SpinSeconds { get; init; } = 4.0;
    /// <summary>Reverse the <see cref="AutoSpin"/> direction.</summary>
    public bool SpinReverse { get; init; }
    /// <summary>Optional JSON file of authored rotation keys for the
    /// Animated workspace timeline. When set, <see cref="AnimatedPropBuilder"/>
    /// builds the .ycd from these keys (rigid spin-bone technique).</summary>
    public string? AnimKeysPath { get; init; }
    /// <summary>Comma-separated mesh-name list to drop from the FBX build
    /// and the YBN. Driven by the layers panel — hidden parts go here.</summary>
    public IReadOnlySet<string> ExcludeMeshes { get; init; }
        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per-mesh visual material preset, keyed by mesh (part) name.
    /// Values come from the layers panel: <c>GLASS</c>, <c>EMISSIVE</c>,
    /// <c>EMISSIVESTRONG</c>, <c>EMISSIVENIGHT</c>, <c>METAL</c> (forces a
    /// spec shader with a synthesized highlight) and <c>CUTOUT</c> (forces the
    /// alpha-tested <c>normal_decal</c> shader). Meshes absent from this
    /// map fall back to the engine's standard shader pick. Drives both
    /// the RAGE shader written into the YDR (see <see cref="TextureBaker"/>)
    /// and, for glass entries, the per-poly collision material in the YBN
    /// (see <see cref="YbnBuilder"/>).</summary>
    public IReadOnlyDictionary<string, string> PartMaterials { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per-mesh diffuse texture overrides from the host's
    /// "Add Missing Textures" / layer texture picker. Keyed by mesh
    /// (part) name → absolute path to an image file. When set, these
    /// win over Assimp material slots during <see cref="TextureBaker"/>.</summary>
    public IReadOnlyDictionary<string, string> PartDiffuseTextures { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per-axis scale baked into vertex coords during export.
    /// Identity (1,1,1) leaves the source untouched. CLI accepts either
    /// <c>--scale 1.5</c> (uniform) or <c>--scale 1,1.5,0.8</c> (per-axis).</summary>
    public (double X, double Y, double Z) Scale { get; init; } = (1.0, 1.0, 1.0);
    public (double X, double Y, double Z) Position { get; init; } = (0, 0, 0);
    public (double X, double Y, double Z) Rotation { get; init; } = (0, 0, 0);

    /// <summary>When true, the converter generates embedded Med/Low/VLow
    /// LODs by deep-cloning the High DrawableModels and decimating each
    /// clone via g3sharp. Adds size to the YDR (typically +30-50% over
    /// the High alone) in exchange for the engine being able to stream
    /// lower-detail tiers at distance. Off by default — embedded LODs
    /// are best for static map props placed via .ymap; weapons / vehicles
    /// already ship per-LOD assets through other mechanisms.</summary>
    public bool GenerateLods { get; init; } = false;
    /// <summary>Target triangle ratios per LOD tier (fraction of High).
    /// Defaults follow the Dekurwinator guide: Med ~50%, Low ~20%,
    /// VLow ~5%.</summary>
    public float LodMedRatio { get; init; } = 0.50f;
    public float LodLowRatio { get; init; } = 0.20f;
    public float LodVLowRatio { get; init; } = 0.05f;
    /// <summary>Outer switch distance (meters) per LOD tier. RAGE renders
    /// High from 0 → DistHigh, Med from DistHigh → DistMed, etc.</summary>
    public float LodDistHigh { get; init; } = 60f;
    public float LodDistMed  { get; init; } = 120f;
    public float LodDistLow  { get; init; } = 250f;
    public float LodDistVLow { get; init; } = 500f;

    public static ConvertOptions Parse(string[] args)
    {
        string? input = null;
        string? outDir = null;
        string? name = null;
        string up = "auto";
        string collMat = "CONCRETE";
        bool includeCol = true, embedCol = false, includeYtyp = true, extractTex = true;
        bool animatedProp = false;
        bool autoSpin = false, spinReverse = false;
        string spinAxis = "Z";
        double spinSeconds = 4.0;
        string? animKeysPath = null;
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var partMats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var partDiffuse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool breakableGlass = false;
        double glassOpacity = 0.6;
        (double X, double Y, double Z) scale = (1.0, 1.0, 1.0);
        var pos = (0d, 0d, 0d);
        var rot = (0d, 0d, 0d);
        bool genLods = false;
        float lodMed = 0.50f, lodLow = 0.20f, lodVLow = 0.05f;
        float distHigh = 60f, distMed = 120f, distLow = 250f, distVLow = 500f;

        int i = 0;
        // First positional is input.
        if (args.Length > 0 && !args[0].StartsWith("-"))
        {
            input = args[0];
            i = 1;
        }
        for (; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => ++i < args.Length ? args[i]
                : throw new ArgumentException($"missing value after {a}");
            switch (a)
            {
                case "-o":
                case "--out":
                case "--output-dir":
                    outDir = Next(); break;
                case "--name":
                    name = Next(); break;
                case "--up":
                    up = Next(); break;
                case "--collision-mat":
                    collMat = Next(); break;
                case "--no-collision": includeCol = false; break;
                case "--embed-collision": embedCol = true; break;
                case "--breakable-glass": breakableGlass = true; break;
                case "--glass-opacity":
                    {
                        var raw = Next() ?? "0.6";
                        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var go))
                            glassOpacity = Math.Clamp(go, 0.0, 1.0);
                        break;
                    }
                case "--no-ytyp":      includeYtyp = false; break;
                case "--no-textures":  extractTex = false; break;
                case "--animated-prop": animatedProp = true; break;
                case "--auto-spin": autoSpin = true; break;
                case "--spin-axis":
                    {
                        var a2 = (Next() ?? "Z").Trim().ToUpperInvariant();
                        spinAxis = a2 is "X" or "Y" or "Z" ? a2 : "Z";
                        break;
                    }
                case "--spin-seconds":
                    {
                        var raw = Next() ?? "4";
                        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var s))
                            spinSeconds = Math.Clamp(s, 0.1, 120.0);
                        break;
                    }
                case "--spin-reverse": spinReverse = true; break;
                case "--anim-keys":
                    animKeysPath = Next();
                    if (!string.IsNullOrWhiteSpace(animKeysPath))
                        animatedProp = true; // keys imply animated prop pipeline
                    break;
                case "--exclude-mesh":
                    foreach (var part in (Next() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (part.Length > 0) exclude.Add(part);
                    break;
                case "--part-mat":
                    {
                        // Format: "<meshName>=<preset>". Split on the FIRST
                        // '=' so mesh names containing '=' (rare) survive.
                        var raw = Next() ?? "";
                        var eq = raw.IndexOf('=');
                        if (eq > 0 && eq < raw.Length - 1)
                            partMats[raw[..eq].Trim()] = raw[(eq + 1)..].Trim().ToUpperInvariant();
                        break;
                    }
                case "--part-diffuse":
                    {
                        // JSON object: { "meshName": "C:\\path\\to\\tex.png", ... }
                        // Written by the host so Windows paths with spaces /
                        // backslashes don't break argv splitting.
                        var jsonPath = Next() ?? "";
                        if (File.Exists(jsonPath))
                        {
                            try
                            {
                                var json = File.ReadAllText(jsonPath);
                                var map = System.Text.Json.JsonSerializer.Deserialize
                                    <Dictionary<string, string>>(json);
                                if (map != null)
                                {
                                    foreach (var kv in map)
                                    {
                                        if (string.IsNullOrWhiteSpace(kv.Key) ||
                                            string.IsNullOrWhiteSpace(kv.Value))
                                            continue;
                                        if (File.Exists(kv.Value))
                                            partDiffuse[kv.Key.Trim()] = kv.Value;
                                    }
                                }
                            }
                            catch
                            {
                                // Malformed override file — ignore and bake
                                // from Assimp slots only.
                            }
                        }
                        break;
                    }
                case "--scale":
                    {
                        // Accept either a single uniform number ("1.5") or
                        // a per-axis triple ("1,1.5,0.8"). The host always
                        // sends the triple; the scalar form is kept for
                        // direct CLI use and older callers.
                        var raw = Next() ?? "1";
                        var ci  = System.Globalization.CultureInfo.InvariantCulture;
                        var sp  = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (sp.Length >= 3
                            && double.TryParse(sp[0], System.Globalization.NumberStyles.Float, ci, out var sx)
                            && double.TryParse(sp[1], System.Globalization.NumberStyles.Float, ci, out var sy)
                            && double.TryParse(sp[2], System.Globalization.NumberStyles.Float, ci, out var sz))
                        {
                            scale = (sx, sy, sz);
                        }
                        else
                        {
                            var u = double.Parse(raw, System.Globalization.NumberStyles.Float, ci);
                            scale = (u, u, u);
                        }
                        break;
                    }
                case "--pos":
                    pos = ParseVec3(Next()); break;
                case "--rot":
                    rot = ParseVec3(Next()); break;
                case "--generate-lods":
                case "--lods":
                    genLods = true; break;
                case "--lod-ratios":
                    {
                        // "med,low,vlow" — e.g. --lod-ratios 0.5,0.2,0.05
                        var lp = (Next() ?? "").Split(',');
                        var ci = System.Globalization.CultureInfo.InvariantCulture;
                        if (lp.Length >= 1) float.TryParse(lp[0], System.Globalization.NumberStyles.Float, ci, out lodMed);
                        if (lp.Length >= 2) float.TryParse(lp[1], System.Globalization.NumberStyles.Float, ci, out lodLow);
                        if (lp.Length >= 3) float.TryParse(lp[2], System.Globalization.NumberStyles.Float, ci, out lodVLow);
                        break;
                    }
                case "--lod-dists":
                    {
                        // "high,med,low,vlow" — e.g. --lod-dists 60,120,250,500
                        var lp = (Next() ?? "").Split(',');
                        var ci = System.Globalization.CultureInfo.InvariantCulture;
                        if (lp.Length >= 1) float.TryParse(lp[0], System.Globalization.NumberStyles.Float, ci, out distHigh);
                        if (lp.Length >= 2) float.TryParse(lp[1], System.Globalization.NumberStyles.Float, ci, out distMed);
                        if (lp.Length >= 3) float.TryParse(lp[2], System.Globalization.NumberStyles.Float, ci, out distLow);
                        if (lp.Length >= 4) float.TryParse(lp[3], System.Globalization.NumberStyles.Float, ci, out distVLow);
                        break;
                    }
                case "--keep-source-textures":
                case "--external-collision":
                case "--external-textures":
                case "--no-download":
                    // Accepted for forward CLI compatibility; ignored for now.
                    break;
                default:
                    if (input == null && !a.StartsWith("-")) { input = a; }
                    else throw new ArgumentException($"unknown arg: {a}");
                    break;
            }
        }

        if (input == null) throw new ArgumentException("input file is required");
        if (outDir == null) outDir = "out";
        if (name == null) name = Path.GetFileNameWithoutExtension(input);
        // Sanitize the asset name to a RAGE-safe identifier. This is the single
        // choke point: the name flows into the .ydr/.ytyp/.ybn file names, the
        // fxmanifest.lua (single-quoted strings — an apostrophe would break the
        // manifest and the whole resource fails to load), the .ytyp archetype
        // JenkHash, and the weapon metas' <ModelName>. Leaving it raw let an
        // edited/CLI name with quotes/spaces/dots produce a broken manifest or
        // a weapon whose streamed .ydr name (raw) ≠ meta model name (sanitized),
        // i.e. a missing model on the client. Matches the host's SanitizeAssetName.
        name = SanitizeAssetName(name);

        return new ConvertOptions
        {
            InputPath = input,
            OutputDir = outDir,
            AssetName = name,
            Up = up,
            CollisionMaterial = collMat,
            IncludeCollision = includeCol,
            EmbedCollision = embedCol,
            BreakableGlass = breakableGlass,
            GlassOpacity = glassOpacity,
            IncludeYtyp = includeYtyp,
            ExtractTextures = extractTex,
            AnimatedProp = animatedProp,
            AutoSpin = autoSpin,
            SpinAxis = spinAxis,
            SpinSeconds = spinSeconds,
            SpinReverse = spinReverse,
            AnimKeysPath = animKeysPath,
            ExcludeMeshes = exclude,
            PartMaterials = partMats,
            PartDiffuseTextures = partDiffuse,
            Scale = scale,
            Position = pos,
            Rotation = rot,
            GenerateLods = genLods,
            LodMedRatio = lodMed,
            LodLowRatio = lodLow,
            LodVLowRatio = lodVLow,
            LodDistHigh = distHigh,
            LodDistMed = distMed,
            LodDistLow = distLow,
            LodDistVLow = distVLow,
        };
    }

    /// <summary>Reduce a name to a RAGE-safe identifier: lowercase, only
    /// [a-z0-9_], leading/trailing underscores trimmed, non-empty. Mirrors the
    /// host's MainWindow.SanitizeAssetName so GUI and CLI agree.</summary>
    private static string SanitizeAssetName(string raw)
    {
        var chars = raw.ToLowerInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_')
                chars[i] = '_';
        var s = new string(chars).Trim('_');
        return string.IsNullOrEmpty(s) ? "model" : s;
    }

    private static (double, double, double) ParseVec3(string s)
    {
        var p = s.Split(',');
        if (p.Length != 3) return (0, 0, 0);
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return (double.Parse(p[0], ci), double.Parse(p[1], ci), double.Parse(p[2], ci));
    }
}
