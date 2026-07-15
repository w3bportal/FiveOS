// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

namespace YdrWriter;

public enum ConvertMode { Prop, Weapon }

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
    public bool IncludeYtyp { get; init; } = true;
    public bool ExtractTextures { get; init; } = true;
    /// <summary>Comma-separated mesh-name list to drop from the FBX build
    /// and the YBN. Driven by the layers panel — hidden parts go here.</summary>
    public IReadOnlySet<string> ExcludeMeshes { get; init; }
        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per-mesh visual material preset, keyed by mesh (part) name.
    /// Values come from the layers panel: <c>GLASS</c>, <c>EMISSIVE</c>,
    /// <c>EMISSIVESTRONG</c>, <c>EMISSIVENIGHT</c>. Meshes absent from this
    /// map fall back to the engine's standard shader pick. Drives both
    /// the RAGE shader written into the YDR (see <see cref="TextureBaker"/>)
    /// and, for glass entries, the per-poly collision material in the YBN
    /// (see <see cref="YbnBuilder"/>).</summary>
    public IReadOnlyDictionary<string, string> PartMaterials { get; init; }
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

    // ── Weapon-mode fields ──────────────────────────────────────────
    //
    // Only consulted when Mode == Weapon. Empty/zero values are accepted
    // by the writer and surface as engine defaults — the user can leave
    // muzzle/grip offsets at zero for a single-bone "everything on
    // gun_root" skeleton and still get a valid YDR.

    public ConvertMode Mode { get; init; } = ConvertMode.Prop;
    public WeaponMetaWriter.Archetype WeaponArchetype { get; init; } = WeaponMetaWriter.Archetype.Pistol;
    /// <summary>e.g. <c>WEAPON_CUSTOMRIFLE</c>. Defaults to
    /// <c>WEAPON_</c> + <see cref="AssetName"/> uppercased.</summary>
    public string WeaponName { get; init; } = "";
    /// <summary>e.g. <c>SLOT_CUSTOMRIFLE</c>. Defaults to
    /// <c>SLOT_</c> + <see cref="AssetName"/> uppercased.</summary>
    public string WeaponSlot { get; init; } = "";
    /// <summary>Muzzle bone position in metres, drawable-local. Z-up,
    /// Y-forward by the time we reach the injector (rotation already baked).</summary>
    public (float X, float Y, float Z) MuzzleOffset { get; init; } = (0, 0.3f, 0);
    public (float X, float Y, float Z) GripOffset { get; init; } = (0, 0, 0);
    public (float X, float Y, float Z) MagazineOffset { get; init; } = (0, 0.05f, -0.08f);
    public (float X, float Y, float Z) EjectOffset { get; init; } = (0.03f, 0.05f, 0.05f);

    public static ConvertOptions Parse(string[] args)
    {
        string? input = null;
        string? outDir = null;
        string? name = null;
        string up = "auto";
        string collMat = "CONCRETE";
        bool includeCol = true, embedCol = false, includeYtyp = true, extractTex = true;
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var partMats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        (double X, double Y, double Z) scale = (1.0, 1.0, 1.0);
        var pos = (0d, 0d, 0d);
        var rot = (0d, 0d, 0d);
        var mode = ConvertMode.Prop;
        var archetype = WeaponMetaWriter.Archetype.Pistol;
        string wName = "";
        string wSlot = "";
        (float X, float Y, float Z) muzzle = (0, 0.3f, 0);
        (float X, float Y, float Z) grip = (0, 0, 0);
        (float X, float Y, float Z) magOff = (0, 0.05f, -0.08f);
        (float X, float Y, float Z) eject = (0.03f, 0.05f, 0.05f);
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
                case "--no-ytyp":      includeYtyp = false; break;
                case "--no-textures":  extractTex = false; break;
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
                case "--mode":
                    mode = string.Equals(Next(), "weapon", StringComparison.OrdinalIgnoreCase)
                        ? ConvertMode.Weapon : ConvertMode.Prop;
                    break;
                case "--weapon-archetype":
                    archetype = Enum.TryParse<WeaponMetaWriter.Archetype>(Next(), ignoreCase: true, out var p)
                        ? p : WeaponMetaWriter.Archetype.Pistol;
                    break;
                case "--weapon-name":  wName = Next(); break;
                case "--weapon-slot":  wSlot = Next(); break;
                case "--muzzle-offset":   muzzle = ParseVec3F(Next()); break;
                case "--grip-offset":     grip   = ParseVec3F(Next()); break;
                case "--magazine-offset": magOff = ParseVec3F(Next()); break;
                case "--eject-offset":    eject  = ParseVec3F(Next()); break;
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

        // Weapon defaults: derive WEAPON_/SLOT_ names from the asset name
        // unless the caller overrode them.
        if (mode == ConvertMode.Weapon)
        {
            if (string.IsNullOrWhiteSpace(wName)) wName = "WEAPON_" + name.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(wSlot)) wSlot = "SLOT_"   + name.ToUpperInvariant();
        }

        return new ConvertOptions
        {
            InputPath = input,
            OutputDir = outDir,
            AssetName = name,
            Up = up,
            CollisionMaterial = collMat,
            IncludeCollision = includeCol,
            EmbedCollision = embedCol,
            IncludeYtyp = includeYtyp,
            ExtractTextures = extractTex,
            ExcludeMeshes = exclude,
            PartMaterials = partMats,
            Scale = scale,
            Position = pos,
            Rotation = rot,
            Mode = mode,
            WeaponArchetype = archetype,
            WeaponName = wName,
            WeaponSlot = wSlot,
            MuzzleOffset = muzzle,
            GripOffset = grip,
            MagazineOffset = magOff,
            EjectOffset = eject,
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

    private static (double, double, double) ParseVec3(string s)
    {
        var p = s.Split(',');
        if (p.Length != 3) return (0, 0, 0);
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return (double.Parse(p[0], ci), double.Parse(p[1], ci), double.Parse(p[2], ci));
    }

    private static (float, float, float) ParseVec3F(string s)
    {
        var p = s.Split(',');
        if (p.Length != 3) return (0, 0, 0);
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return (float.Parse(p[0], ci), float.Parse(p[1], ci), float.Parse(p[2], ci));
    }
}
