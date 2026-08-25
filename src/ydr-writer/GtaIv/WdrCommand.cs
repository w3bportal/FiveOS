// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Globalization;
using Assimp;

namespace YdrWriter.GtaIv;

/// <summary>The <c>wdr</c> subcommand: mesh in, GTA IV prop out.
///
/// Shares the whole front half of the GTA V pipeline — the same Assimp import
/// settings, the same gizmo-transform bake, the same Y-up→Z-up correction, the
/// same texture discovery — and swaps only the writer. Keeping the axis and
/// transform handling on <see cref="Converter"/>'s helpers rather than
/// re-deriving it here matters: a divergence there ships props rotated or
/// mirrored relative to their GTA V twins, and it wouldn't be obvious until
/// someone loaded one in game.</summary>
internal static class WdrCommand
{
    internal static int Run(string[] args)
    {
        string? resourceBuilder = null;
        bool compile = true;
        bool externalTextures = false;
        var lodDistances = new[] { 9999f, 9999f, 9999f, 9999f };
        var placement = new PlacementOptions();
        var textureDicts = new List<string>();

        // Pull the IV-only flags out before handing the rest to the shared
        // parser, which throws on anything it doesn't recognise.
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--resource-builder":
                    if (++i < args.Length) resourceBuilder = args[i];
                    break;
                case "--no-compile":
                    compile = false;
                    break;
                case "--external-textures":
                    externalTextures = true;
                    break;
                case "--lod-dists":
                    {
                        // GTA IV defaults differ from V's: stock IV props ship
                        // 9999 on every tier and let the .ide drive culling, so
                        // we only depart from that when asked.
                        if (++i >= args.Length) break;
                        var parts = args[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        for (int k = 0; k < 4 && k < parts.Length; k++)
                            if (float.TryParse(parts[k], NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                                lodDistances[k] = d;
                        break;
                    }
                case "--ytd":
                    if (++i < args.Length) textureDicts.Add(args[i]);
                    break;
                case "--no-placement":
                    placement.Write = false;
                    break;
                case "--place":
                    {
                        if (++i >= args.Length) break;
                        var p = args[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        float C(int k) => k < p.Length && float.TryParse(p[k], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
                        placement.Position = new System.Numerics.Vector3(C(0), C(1), C(2));
                        break;
                    }
                case "--draw-dist":
                    if (++i < args.Length
                        && float.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var dd))
                        placement.DrawDistance = dd;
                    break;
                default:
                    rest.Add(args[i]);
                    break;
            }
        }

        ConvertOptions opts;
        try
        {
            opts = ConvertOptions.Parse(rest.ToArray());
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"[ydr-writer] {ex.Message}");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (!File.Exists(opts.InputPath))
        {
            Console.Error.WriteLine($"input not found: {opts.InputPath}");
            return 2;
        }

        Log($"input:  {opts.InputPath}");
        Log($"output: {opts.OutputDir}");
        Log($"name:   {opts.AssetName}");
        Log("target: GTA IV (.wdr)");

        // A GTA V drawable is read natively rather than through Assimp: its
        // vertex buffers already carry exactly what an IV .mesh line needs, so
        // routing it via an intermediate mesh format would only lose fidelity.
        if (Path.GetExtension(opts.InputPath).Equals(".ydr", StringComparison.OrdinalIgnoreCase))
            return RunFromYdr(opts, resourceBuilder, compile, externalTextures, lodDistances, placement, textureDicts);

        // --- import -----------------------------------------------------
        Log("[1/4] Importing mesh via Assimp...");
        using var ai = new AssimpContext();
        ai.SetConfig(new Assimp.Configs.BooleanPropertyConfig("PP_PTV_KEEP_HIERARCHY", true));
        const PostProcessSteps steps =
            PostProcessSteps.Triangulate
            | PostProcessSteps.GenerateNormals
            | PostProcessSteps.JoinIdenticalVertices
            | PostProcessSteps.ImproveCacheLocality
            | PostProcessSteps.GenerateUVCoords
            | PostProcessSteps.PreTransformVertices
            | PostProcessSteps.EmbedTextures
            // The normal-mapped IV presets declare a tangent element, so ask
            // Assimp for a tangent basis up front rather than deriving one.
            | PostProcessSteps.CalculateTangentSpace;

        Scene scene;
        try
        {
            scene = ai.ImportFile(opts.InputPath, steps);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"import failed: {ex.Message}");
            return 3;
        }
        if (scene == null || !scene.HasMeshes)
        {
            Console.Error.WriteLine("import produced no meshes");
            return 3;
        }
        Log($"  meshes: {scene.MeshCount}, materials: {scene.MaterialCount}, embedded textures: {scene.TextureCount}");

        Converter.ApplyUserTransform(scene, opts);
        var sourceUp = Converter.ResolveSourceUpAxis(opts, scene);
        Log($"  source up axis: {sourceUp}");
        if (sourceUp == Converter.SourceUp.YUp)
        {
            Converter.ApplyYupToZupTransform(scene);
            Log("  applied Y-up -> Z-up rotation (rot X -90)");
        }

        // --- textures ---------------------------------------------------
        var outDir = Path.GetFullPath(opts.OutputDir);
        Directory.CreateDirectory(outDir);
        var assetDir = Path.Combine(outDir, opts.AssetName);
        // Embedded textures sit directly in the model folder and get pulled
        // into the .wdr. External ones go to texotd\, where the .otd expects
        // them, and compile into a separate .wtd instead.
        var texDir = externalTextures ? Path.Combine(assetDir, "texotd") : assetDir;

        var workDir = Path.Combine(Path.GetTempPath(), "ydr-writer", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);

        var maps = new List<IvTextureBaker.MaterialMaps>();
        try
        {
            if (opts.ExtractTextures)
            {
                Log("[2/4] Baking textures to DDS...");
                var sourceDir = Path.GetDirectoryName(Path.GetFullPath(opts.InputPath)) ?? ".";
                maps = IvTextureBaker.Bake(scene, workDir, opts.AssetName, sourceDir, texDir, opts.PartDiffuseTextures);
                int bound = maps.Count(m => m.Diffuse != null);
                Log($"  materials: {maps.Count}, with diffuse: {bound}");
            }
            else
            {
                Log("[2/4] Skipping textures (--no-textures)");
            }

            // --- geometry ------------------------------------------------
            Log("[3/4] Building drawable...");
            var drawable = IvSceneBuilder.Build(scene, maps, new IvSceneBuilder.Options(
                AssetName: opts.AssetName,
                ExcludeMeshes: opts.ExcludeMeshes,
                PartMaterials: opts.PartMaterials,
                GenerateLods: opts.GenerateLods,
                MedRatio: opts.LodMedRatio,
                LowRatio: opts.LodLowRatio,
                VLowRatio: opts.LodVLowRatio,
                LodDistances: lodDistances,
                EmbedTextures: !externalTextures));

            // A shader sampler left unbound is a render-time crash in game, so
            // fill any that the materials did not cover before anything is
            // written. See IvPlaceholderTexture.
            {
                var texNotes = new List<string>();
                IvPlaceholderTexture.FillUnboundSamplers(drawable, assetDir, opts.AssetName, texNotes);
                foreach (var n in texNotes) Log($"  NOTE: {n}");
            }

            for (int i = 0; i < 4; i++)
            {
                var lod = drawable.Lods[i];
                if (lod == null) continue;
                int tris = lod.Primitives.Sum(p => p.Indices.Count) / 3;
                int verts = lod.Primitives.Sum(p => p.Vertices.Count);
                Log($"  {IvDrawable.LodNames[i],-4} {tris,7:N0} tris  {verts,7:N0} verts  {lod.Primitives.Count} material(s)");
            }
            Log($"  shaders: {string.Join(", ", drawable.Shaders.Select(s => s.Preset.FileName).Distinct())}");

            var odrPath = OpenFormatsWriter.WriteDrawable(drawable, outDir);
            Log($"  wrote {Path.GetFileName(odrPath)}");

            // GTA IV keeps reduced detail in a separate resource that the .ide
            // points at, not in extra tiers inside the drawable, so --lods
            // produces a second asset rather than a bigger one.
            string? lodOdrPath = null;
            if (opts.GenerateLods)
            {
                var lodName = opts.AssetName + "_lod";
                var lod = IvSceneBuilder.BuildLodVariant(drawable, opts.LodLowRatio, lodName);
                if (lod == null)
                {
                    Log("  --lods: geometry too small to be worth a separate LOD asset — skipped");
                }
                else
                {
                    // The LOD's shader table references textures relative to
                    // its own folder, so give it copies.
                    var lodDir = Path.Combine(outDir, lodName);
                    Directory.CreateDirectory(lodDir);
                    foreach (var dds in Directory.GetFiles(assetDir, "*.dds"))
                        File.Copy(dds, Path.Combine(lodDir, Path.GetFileName(dds)), overwrite: true);

                    lodOdrPath = OpenFormatsWriter.WriteDrawable(lod, outDir);
                    int hi = drawable.Lods[0]!.Primitives.Sum(p => p.Indices.Count) / 3;
                    int lo = lod.Lods[0]!.Primitives.Sum(p => p.Indices.Count) / 3;
                    Log($"  wrote {Path.GetFileName(lodOdrPath)}  ({hi:N0} -> {lo:N0} tris)");
                }
            }

            // Collision. A .wbn is a resource in its own right — it does not go
            // through the OpenFormats compiler, so it is written directly and
            // is unaffected by whether that tool is present.
            if (opts.IncludeCollision)
            {
                var bound = IvBoundBuilder.Build(scene, new IvBoundBuilder.Options(
                    ExcludeMeshes: opts.ExcludeMeshes,
                    PartMaterials: opts.PartMaterials,
                    DefaultMaterial: opts.CollisionMaterial));
                if (bound == null)
                {
                    Log("  no collision geometry found — prop will not be solid");
                }
                else
                {
                    var wbnPath = Path.Combine(outDir, opts.AssetName + ".wbn");
                    bound.Save(wbnPath);
                    Log($"  wrote {Path.GetFileName(wbnPath)} ({new FileInfo(wbnPath).Length:N0} bytes)");
                }
            }
            else
            {
                Log("  collision skipped (--no-collision)");
            }

            string? otdPath = null;
            if (externalTextures && opts.ExtractTextures)
            {
                otdPath = OpenFormatsWriter.WriteTextureDictionary(opts.AssetName, outDir, texDir);
                Log($"  wrote {Path.GetFileName(otdPath)}");
            }

            // --- compile -----------------------------------------------
            Log("[4/4] Compiling to GTA IV resources...");
            var exe = ResourceBuilderRunner.Locate(resourceBuilder);
            if (!compile)
            {
                Log("  skipped (--no-compile); OpenFormats written");
            }
            else if (exe == null)
            {
                Log($"  {ResourceBuilderRunner.ExeName} not found — stopping at OpenFormats.");
                Log($"  Point at it with --resource-builder <path>, or set {ResourceBuilderRunner.EnvVar}.");
            }
            else
            {
                Log($"  using {exe}");
                foreach (var src in new[] { odrPath, lodOdrPath, otdPath })
                {
                    if (src == null) continue;
                    var r = ResourceBuilderRunner.Compile(exe, src);
                    Log(r.Ok ? $"  {r.Message}" : $"  FAILED: {r.Message}");
                    if (!r.Ok) return 4;
                }
            }

            WritePlacement(outDir, opts.AssetName, drawable, externalTextures, placement);

            File.WriteAllText(Path.Combine(outDir, $"{opts.AssetName}_README.txt"),
                Readme(opts.AssetName, drawable, externalTextures, opts.CollisionMaterial, placement));
            Log("done.");
            return 0;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>How the export should declare and place the finished prop.</summary>
    private sealed class PlacementOptions
    {
        public bool Write = true;
        public System.Numerics.Vector3 Position;
        public float DrawDistance = IvPlacementWriter.DefaultDrawDistance;
    }

    /// <summary>Emit the .ide and .wpl that make a converted prop real.
    ///
    /// A .wdr on its own is inert — GTA IV never looks at a model it has no
    /// archetype for, so an export without these two files can only be spawned
    /// from script. Writing them from bounds already computed costs nothing and
    /// removes the one hand-editing step left in the pipeline.</summary>
    private static void WritePlacement(
        string outDir, string name, IvDrawable drawable, bool externalTextures, PlacementOptions p)
    {
        if (!p.Write) { Log("  placement files skipped (--no-placement)"); return; }

        // With textures embedded in the .wdr there is no dictionary to name, and
        // "null" is what Rockstar's own entries use in that case.
        var txd = externalTextures ? name : "null";
        var idePath = IvPlacementWriter.WriteIde(outDir, name, drawable, txd, p.DrawDistance);
        var wplPath = IvPlacementWriter.WriteWpl(outDir, name, p.Position);
        Log($"  wrote {Path.GetFileName(idePath)} and {Path.GetFileName(wplPath)} "
          + $"(placed at {p.Position.X:0.##}, {p.Position.Y:0.##}, {p.Position.Z:0.##})");
    }

    private static string Readme(
        string name, IvDrawable d, bool externalTextures, string mat, PlacementOptions p)
    {
        var (c, r, min, max) = d.Bounds();
        var ci = CultureInfo.InvariantCulture;
        var tex = externalTextures
            ? $"{name}.wtd  — texture dictionary (place alongside the .wdr)"
            : "(textures are embedded in the .wdr — nothing else to ship)";

        var placementFiles = p.Write
            ? $"""
          {name}.ide   object definition — declares the model to the game
          {name}.wpl   placement — puts one of it in the world
        """
            : "  (no .ide/.wpl — --no-placement was used)";

        var placementSteps = p.Write
            ? $"""
          2. Merge {name}.ide into the .ide that owns your .img — copy its
             objs line across, or add the file to your dlc's content.xml.
             It already carries this model's real bounds and radius, so the
             game culls it correctly.
          3. Merge {name}.wpl the same way to place one at
             {p.Position.X.ToString("0.##", ci)}, {p.Position.Y.ToString("0.##", ci)}, {p.Position.Z.ToString("0.##", ci)}
             — or skip it and spawn the prop from script with CREATE_OBJECT
             once the model is requested. Re-export with
             --place x,y,z to put it somewhere else.
        """
            : $"""
          2. Add an object definition so the game knows the model exists.
             A minimal .ide line, in the objs section of the .ide that
             owns your .img:

               {name}, {(externalTextures ? name : "null")}, 100, 0

             (model name, texture dictionary, draw distance, flags.)
          3. Place it with a .wpl entry, or spawn it from script with
             CREATE_OBJECT once the model is requested.
        """;

        return $"""
        {name} — GTA IV prop
        ============================================================

        Files
          {name}.wdr   the model
          {name}.wbn   its collision
          {tex}
        {placementFiles}
          {name}.odr   OpenFormats source (editable text; recompile any time)
          {name}\      meshes + textures the .odr references

        Installing
          1. Drop the .wdr, .wbn (and .wtd, if present) into a props .img
             using OpenIV in edit mode.
        {placementSteps}

        Bounds
          centre  {c.X.ToString("F4", ci)}, {c.Y.ToString("F4", ci)}, {c.Z.ToString("F4", ci)}
          min     {min.X.ToString("F4", ci)}, {min.Y.ToString("F4", ci)}, {min.Z.ToString("F4", ci)}
          max     {max.X.ToString("F4", ci)}, {max.Y.ToString("F4", ci)}, {max.Z.ToString("F4", ci)}
          radius  {r.ToString("F4", ci)}

        Collision
          {name}.wbn holds the collision. Drop it in the same .img as the
          .wdr. GTA IV also accepts a per-.img bounds dictionary (.wbd) —
          a single-object .wbn is the simpler form and is what this
          exports.

          The bound is a composite of one geometry hull per mesh, built
          from the model's own triangles, so concave props stay concave
          rather than collapsing to a box. Surface type comes from
          --collision-mat (default {mat}); tag individual meshes with
          --part-mat <mesh>=<material> to mix materials on one prop.
        """;
    }

    /// <summary>GTA V drawable in, GTA IV prop out.
    ///
    /// Static geometry only. A GTA V .ydr carrying a skeleton still converts,
    /// but as a static prop — GTA IV keeps skinned content in .wdd drawable
    /// dictionaries, which the resource compiler cannot produce.
    ///
    /// Collision comes from a sibling .ybn when one is present (or
    /// --collision-ybn), translated bound-for-bound; otherwise it is built from
    /// the drawable's own triangles as usual.</summary>
    private static int RunFromYdr(
        ConvertOptions opts, string? resourceBuilder, bool compile,
        bool externalTextures, float[] lodDistances, PlacementOptions placement,
        List<string> textureDicts)
    {
        var outDir = Path.GetFullPath(opts.OutputDir);
        Directory.CreateDirectory(outDir);
        var assetDir = Path.Combine(outDir, opts.AssetName);
        var texDir = externalTextures ? Path.Combine(assetDir, "texotd") : assetDir;

        Log("[1/3] Reading GTA V drawable...");
        IvYdrImporter.Result? imported;
        try
        {
            // A GTA V prop usually keeps its textures in a shared .ytd beside
            // the .ydr rather than embedded, so pick those up automatically -
            // otherwise the prop converts perfectly and comes out white.
            var dicts = new List<string>(textureDicts);
            foreach (var found in FindSiblingYtds(opts.InputPath))
                if (!dicts.Contains(found, StringComparer.OrdinalIgnoreCase)) dicts.Add(found);
            foreach (var d in dicts) Log($"  textures: {Path.GetFileName(d)}");

            imported = IvYdrImporter.Import(opts.InputPath, new IvYdrImporter.Options(
                AssetName: opts.AssetName,
                AssetDir: texDir,
                ExtractTextures: opts.ExtractTextures,
                TextureDictionaries: dicts));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not read {Path.GetFileName(opts.InputPath)}: {ex.Message}");
            return 3;
        }
        if (imported == null)
        {
            Console.Error.WriteLine("the drawable produced no usable geometry");
            return 3;
        }

        var drawable = imported.Drawable;
        for (int i = 0; i < 4 && i < lodDistances.Length; i++)
            drawable.LodDistances[i] = lodDistances[i];

        int tris = drawable.Lods[0]!.Primitives.Sum(p => p.Indices.Count) / 3;
        int verts = drawable.Lods[0]!.Primitives.Sum(p => p.Vertices.Count);
        Log($"  {tris:N0} tris  {verts:N0} verts  {drawable.Shaders.Count} shader(s)");
        Log($"  shaders: {string.Join(", ", drawable.Shaders.Select(s => s.Preset.FileName).Distinct())}");
        // Anything the source did that GTA IV cannot is reported rather than
        // silently dropped — a missing detail map looks like a bug otherwise.
        foreach (var note in imported.Notes.Distinct())
            Log($"  NOTE: {note}");

        Log("[2/3] Writing OpenFormats...");
        var odrPath = OpenFormatsWriter.WriteDrawable(drawable, outDir);
        Log($"  wrote {Path.GetFileName(odrPath)}");

        string? otdPath = null;
        if (externalTextures && opts.ExtractTextures)
        {
            otdPath = OpenFormatsWriter.WriteTextureDictionary(opts.AssetName, outDir, texDir);
            Log($"  wrote {Path.GetFileName(otdPath)}");
        }

        // --- collision ---------------------------------------------------
        if (opts.IncludeCollision)
        {
            var ybn = FindSiblingYbn(opts.InputPath);
            if (ybn != null)
            {
                Log($"  collision source: {Path.GetFileName(ybn)}");
                var converted = IvYbnImporter.Convert(ybn, opts.CollisionMaterial, out var colNotes);
                foreach (var n in colNotes.Distinct()) Log($"  NOTE: {n}");
                if (converted != null)
                {
                    var wbn = Path.Combine(outDir, opts.AssetName + ".wbn");
                    converted.Save(wbn);
                    Log($"  wrote {Path.GetFileName(wbn)} ({new FileInfo(wbn).Length:N0} bytes)");
                }
                else
                {
                    Log("  collision could not be translated — prop will not be solid");
                }
            }
            else
            {
                // No authored collision shipped with the model. Deriving it
                // from the drawable's own triangles is what the mesh-import
                // path already does, and it is the difference between a prop
                // and scenery the player walks through.
                Log("  no sibling .ybn found — deriving collision from the model's own geometry");
                var derived = IvBoundBuilder.Build(drawable, new IvBoundBuilder.Options(
                    ExcludeMeshes: opts.ExcludeMeshes,
                    PartMaterials: opts.PartMaterials,
                    DefaultMaterial: opts.CollisionMaterial));
                if (derived == null)
                {
                    Log("  could not derive collision — prop will not be solid");
                }
                else
                {
                    var wbn = Path.Combine(outDir, opts.AssetName + ".wbn");
                    derived.Save(wbn);
                    long colTris = IvBoundBuilder.CountTriangles(derived);
                    Log($"  wrote {Path.GetFileName(wbn)} ({new FileInfo(wbn).Length:N0} bytes, "
                      + $"{colTris:N0} triangles, surface {opts.CollisionMaterial})");
                    Log("  NOTE: this is the render mesh used as physics. It is heavier and more "
                      + "detailed than authored collision — supply a .ybn beside the .ydr to use that instead.");
                }
            }
        }

        WritePlacement(outDir, opts.AssetName, drawable, externalTextures, placement);

        Log("[3/3] Compiling to GTA IV resources...");
        var exe = ResourceBuilderRunner.Locate(resourceBuilder);
        if (!compile) Log("  skipped (--no-compile); OpenFormats written");
        else if (exe == null)
            Log($"  {ResourceBuilderRunner.ExeName} not found — stopping at OpenFormats.");
        else
        {
            foreach (var src in new[] { odrPath, otdPath })
            {
                if (src == null) continue;
                var r = ResourceBuilderRunner.Compile(exe, src);
                Log(r.Ok ? $"  {r.Message}" : $"  FAILED: {r.Message}");
                if (!r.Ok) return 4;
            }
        }

        Log("done.");
        return 0;
    }

    /// <summary>Every .ytd sitting in the same folder as the .ydr.
    ///
    /// A prop resource ships one shared dictionary for a set of models -
    /// prop_medusas.ytd next to prop_medusa_red/blue/white.ydr - so the folder,
    /// not the file stem, is the right unit to look in.</summary>
    private static IEnumerable<string> FindSiblingYtds(string ydrPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(ydrPath));
            if (string.IsNullOrEmpty(dir)) return Array.Empty<string>();
            return Directory.GetFiles(dir, "*.ytd");
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>GTA V map assets keep collision in a .ybn beside the .ydr with
    /// the same stem; that is the convention worth honouring automatically.</summary>
    private static string? FindSiblingYbn(string ydrPath)
    {
        try
        {
            var candidate = Path.ChangeExtension(ydrPath, ".ybn");
            return File.Exists(candidate) ? candidate : null;
        }
        catch { return null; }
    }

    /// <summary>The <c>compile-odr</c> subcommand: compile an existing
    /// OpenFormats file without re-exporting the model. The .odr we write is
    /// plain text and meant to be hand-editable — swapping a shader preset or
    /// retuning a specular factor shouldn't cost a full re-export.</summary>
    internal static int RunCompile(string[] args)
    {
        string? source = null;
        string? resourceBuilder = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--resource-builder") { if (++i < args.Length) resourceBuilder = args[i]; }
            else if (source == null && !args[i].StartsWith("-")) source = args[i];
        }

        if (source == null)
        {
            Console.Error.WriteLine("usage: ydr-writer compile-odr <file.odr|file.otd> [--resource-builder <path>]");
            return 2;
        }
        if (!File.Exists(source))
        {
            Console.Error.WriteLine($"not found: {source}");
            return 2;
        }

        var exe = ResourceBuilderRunner.Locate(resourceBuilder);
        if (exe == null)
        {
            Console.Error.WriteLine(
                $"{ResourceBuilderRunner.ExeName} not found. Pass --resource-builder <path> " +
                $"or set {ResourceBuilderRunner.EnvVar}.");
            return 2;
        }

        Log($"using {exe}");
        var r = ResourceBuilderRunner.Compile(exe, source);
        if (r.Ok) { Log(r.Message); return 0; }
        Console.Error.WriteLine($"[ydr-writer] FAILED: {r.Message}");
        return 4;
    }

    /// <summary>The <c>dump-iv-shaders</c> subcommand: print the .odr shader
    /// line this exporter would emit for every preset it knows.
    ///
    /// Exists so the parameter contracts in <see cref="IvShaderCatalog"/> can
    /// be checked against the real resource compiler without having to find a
    /// model that happens to select each preset. A wrong parameter count is
    /// not a warning from that compiler — it is a crash — so every preset we
    /// can emit wants to have been compiled at least once.</summary>
    internal static int RunDumpShaders()
    {
        foreach (var name in IvShaderCatalog.Names)
        {
            var preset = IvShaderCatalog.Find(name);
            if (preset == null) continue;
            var inst = new IvShaderInstance { Preset = preset };
            // Give every sampler a placeholder so texture slots render as a
            // name rather than "null" — the same shape a real export produces.
            foreach (var p in preset.Params)
                if (p.Kind == IvParamKind.Texture)
                    inst.Textures[p.Name] = "probe_" + p.Name;
            Console.WriteLine(OpenFormatsWriter.ShaderLine(inst));
        }
        return 0;
    }

    /// <summary>The <c>dump-materials</c> subcommand: list what the importer
    /// actually sees per material — the texture slot it found and the path it
    /// resolved to. Textures going missing is the single most common way an
    /// export comes out wrong, and the path is the only thing that tells you
    /// whether the file was embedded, referenced, or simply not found.</summary>
    internal static int RunDumpMaterials(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("usage: ydr-writer dump-materials <model>"); return 2; }
        var path = args[0];
        if (!File.Exists(path)) { Console.Error.WriteLine($"not found: {path}"); return 2; }

        using var ai = new AssimpContext();
        ai.SetConfig(new Assimp.Configs.BooleanPropertyConfig("PP_PTV_KEEP_HIERARCHY", true));
        var scene = ai.ImportFile(path,
            PostProcessSteps.Triangulate | PostProcessSteps.PreTransformVertices | PostProcessSteps.EmbedTextures);
        if (scene == null) { Console.Error.WriteLine("import failed"); return 3; }

        Console.WriteLine($"materials: {scene.MaterialCount}   embedded textures: {scene.TextureCount}");
        for (int i = 0; i < scene.MaterialCount; i++)
        {
            var m = scene.Materials[i];
            string slot = "<none>", kind = "";
            if (m.HasTextureDiffuse) { slot = m.TextureDiffuse.FilePath ?? ""; kind = "diffuse"; }
            else if (m.GetMaterialTexture(TextureType.BaseColor, 0, out var bc) && !string.IsNullOrEmpty(bc.FilePath))
            { slot = bc.FilePath; kind = "baseColor"; }

            string resolved;
            if (slot == "<none>") resolved = "-";
            else
            {
                var emb = scene.GetEmbeddedTexture(slot);
                resolved = emb != null
                    ? $"embedded({(emb.HasCompressedData ? emb.CompressedData.Length + "B" : "raw")})"
                    : "NOT RESOLVED";
            }
            Console.WriteLine($"  [{i:D2}] {m.Name,-18} {kind,-10} path='{slot}' -> {resolved}");
        }
        return 0;
    }

    internal const string Usage = """
        usage: ydr-writer wdr <input> -o <out_dir> --name <name> [flags]
          Builds a GTA IV prop (.wdr) via OpenFormats.
          Inputs: .obj .glb .gltf .fbx .dae .ply .stl

          --resource-builder <path>  ResourceBuilder.exe (or its folder)
          --no-compile               stop at OpenFormats (.odr/.mesh/.otd)
          --external-textures        build a .wtd instead of embedding textures
          --lod-dists h,m,l,v        per-tier draw distances (default 9999 each)
          --place x,y,z              where the generated .wpl puts it (default 0,0,0)
          --draw-dist <metres>       .ide draw distance (default 100)
          --ytd <file>               extra GTA V texture dictionary (repeatable;
                                     any .ytd beside the .ydr is used automatically)
          --no-placement             don't write the .ide/.wpl
          plus the shared flags: --up --scale --pos --rot --lods --lod-ratios
                                 --exclude-mesh --part-mat --no-textures

          Writes <name>.ide and <name>.wpl alongside the model so the prop is
          placeable as exported — GTA IV ignores a model it has no archetype
          for, so without those two files the .wdr can only be spawned from
          script.
        """;

    private static void Log(string s) => Console.WriteLine($"[ydr-writer] {s}");
}
