using System.Globalization;
using Assimp;
using CodeWalker.GameFiles;

namespace YdrWriter;

public static class RetextureCommand
{
    public static int Run(string[] args)
    {
        string? baseYdr = null;
        string? input = null;
        string? outputDir = null;
        string? assetName = null;
        double glassOpacity = 0.6;
        string? partDiffusePath = null;
        var excludeMeshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var partMaterials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--base-ydr":
                    if (i + 1 < args.Length) baseYdr = args[++i];
                    break;
                case "-o":
                case "--out":
                case "--output-dir":
                    if (i + 1 < args.Length) outputDir = args[++i];
                    break;
                case "--name":
                    if (i + 1 < args.Length) assetName = args[++i];
                    break;
                case "--up":
                    if (i + 1 < args.Length) i++;
                    break;
                case "--glass-opacity":
                    if (i + 1 < args.Length &&
                        double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var go))
                        glassOpacity = go;
                    break;
                case "--part-diffuse":
                    if (i + 1 < args.Length) partDiffusePath = args[++i];
                    break;
                case "--exclude-mesh":
                    if (i + 1 < args.Length)
                    {
                        foreach (var n in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            excludeMeshes.Add(n);
                    }
                    break;
                case "--part-mat":
                    if (i + 1 < args.Length)
                    {
                        var pair = args[++i];
                        var eq = pair.IndexOf('=');
                        if (eq > 0)
                            partMaterials[pair[..eq]] = pair[(eq + 1)..];
                    }
                    break;
                default:
                    if (!a.StartsWith("-") && input == null)
                        input = a;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(baseYdr) || !File.Exists(baseYdr))
        {
            Console.Error.WriteLine("usage: ydr-writer retexture <input> --base-ydr <file.ydr> -o <out_dir> --name <name> [--part-diffuse <json>]");
            return 2;
        }
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
        {
            Console.Error.WriteLine("retexture: input mesh not found");
            return 2;
        }
        if (string.IsNullOrWhiteSpace(outputDir) || string.IsNullOrWhiteSpace(assetName))
        {
            Console.Error.WriteLine("retexture: --out and --name are required");
            return 2;
        }

        var partDiffuse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(partDiffusePath) && File.Exists(partDiffusePath))
        {
            try
            {
                var json = File.ReadAllText(partDiffusePath);
                var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (map != null)
                {
                    foreach (var kv in map)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                            continue;
                        if (!File.Exists(kv.Value)) continue;
                        partDiffuse[kv.Key] = kv.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"retexture: failed to read part-diffuse: {ex.Message}");
                return 2;
            }
        }
        if (partDiffuse.Count == 0)
        {
            Console.Error.WriteLine("retexture: no valid part-diffuse textures");
            return 2;
        }

        Console.WriteLine($"[retexture] base={baseYdr}");
        Console.WriteLine($"[retexture] input={input}");
        Console.WriteLine($"[retexture] name={assetName}");
        Console.WriteLine($"[retexture] overrides={partDiffuse.Count}");

        using var ai = new AssimpContext();
        ai.SetConfig(new Assimp.Configs.BooleanPropertyConfig("PP_PTV_KEEP_HIERARCHY", true));
        // Same import flags as Converter — EmbedTextures is required so
        // override bakes still find normals / PBR maps from the source GLB/FBX
        // and write them into the YDR's embedded TextureDictionary.
        const PostProcessSteps steps =
            PostProcessSteps.Triangulate
            | PostProcessSteps.GenerateNormals
            | PostProcessSteps.JoinIdenticalVertices
            | PostProcessSteps.ImproveCacheLocality
            | PostProcessSteps.GenerateUVCoords
            | PostProcessSteps.PreTransformVertices
            | PostProcessSteps.EmbedTextures;
        var scene = ai.ImportFile(input, steps);
        if (scene == null || !scene.HasMeshes)
        {
            Console.Error.WriteLine("retexture: import produced no meshes");
            return 3;
        }

        var workDir = Path.Combine(Path.GetTempPath(), "ydr-writer", "retex-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        try
        {
            var ydr = new YdrFile();
            ydr.Load(File.ReadAllBytes(baseYdr));
            if (ydr.Drawable?.ShaderGroup == null)
            {
                Console.Error.WriteLine("retexture: base ydr has no ShaderGroup");
                return 5;
            }

            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(input)) ?? "";
            var matTexs = TextureBaker.Bake(scene, workDir, assetName, sourceDir, partDiffuse);
            // Keep normals (and any other already-baked maps) from the base
            // YDR when Assimp couldn't re-resolve them — otherwise a recolor
            // strip embeds only the override diffuse and RAGE loses bumps.
            TextureBaker.EnrichMissingMapsFromDonorYdr(matTexs, scene, ydr,
                excludeMeshes.Count > 0 ? excludeMeshes : null);
            if (!matTexs.Any(m => m.Diffuse != null || m.Normal != null))
            {
                Console.Error.WriteLine("retexture: bake produced no textures");
                return 5;
            }

            var keptMatIdx = new HashSet<int>();
            for (int mi = 0; mi < scene.MeshCount; mi++)
            {
                var mname = scene.Meshes[mi].Name;
                if (excludeMeshes.Count > 0 && !string.IsNullOrEmpty(mname) && excludeMeshes.Contains(mname))
                    continue;
                int srcMidx = scene.Meshes[mi].MaterialIndex;
                if (srcMidx >= 0) keptMatIdx.Add(srcMidx);
            }
            var keptMatTexs = matTexs
                .Select((m, idx) => (m, idx))
                .Where(t => keptMatIdx.Count == 0 || keptMatIdx.Contains(t.idx))
                .Select(t => t.m)
                .ToList();

            var td = TextureBaker.BuildDictionary(keptMatTexs);
            ydr.Drawable.ShaderGroup.TextureDictionary = td;
            TextureBaker.GlassOpacity = glassOpacity;
            TextureBaker.BindShaderTextures(
                ydr, scene, matTexs,
                excludeMeshes.Count > 0 ? excludeMeshes : null,
                partMaterials.Count > 0 ? partMaterials : null);

            ydr.Name = assetName;
            ydr.Drawable.Name = assetName;
            if (ydr.Drawable.Bound is not null)
                ydr.Drawable.Bound.OwnerName = assetName;

            var resourceDir = Path.Combine(outputDir, $"{assetName}_resource");
            var streamDir = Path.Combine(resourceDir, "stream");
            Directory.CreateDirectory(streamDir);

            var baseStream = Path.GetDirectoryName(Path.GetFullPath(baseYdr)) ?? "";
            var baseStem = Path.GetFileNameWithoutExtension(baseYdr);
            if (Directory.Exists(baseStream))
            {
                foreach (var f in Directory.EnumerateFiles(baseStream))
                {
                    var ext = Path.GetExtension(f);
                    if (ext.Equals(".ydr", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ext.Equals(".ytyp", StringComparison.OrdinalIgnoreCase)) continue;
                    var stem = Path.GetFileNameWithoutExtension(f);
                    var dstName = stem.Equals(baseStem, StringComparison.OrdinalIgnoreCase)
                        ? assetName + ext
                        : Path.GetFileName(f);
                    File.Copy(f, Path.Combine(streamDir, dstName), overwrite: true);
                }
            }

            var ydrOut = Path.Combine(streamDir, $"{assetName}.ydr");
            File.WriteAllBytes(ydrOut, ydr.Save());
            Console.WriteLine($"[retexture] wrote {ydrOut}");

            try
            {
                var d = ydr.Drawable;
                var lod = d.LodDistVlow > 0 ? d.LodDistVlow : 500f;
                var ytypBytes = YtypBuilder.Build(
                    assetName,
                    d.BoundingBoxMin, d.BoundingBoxMax,
                    d.BoundingCenter, d.BoundingSphereRadius,
                    lodDist: lod);
                File.WriteAllBytes(Path.Combine(streamDir, $"{assetName}.ytyp"), ytypBytes);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[retexture] ytyp failed (non-fatal): {ex.Message}");
            }

            var fx = Path.Combine(Path.GetDirectoryName(baseStream) ?? "", "fxmanifest.lua");
            if (File.Exists(fx))
                File.Copy(fx, Path.Combine(resourceDir, "fxmanifest.lua"), overwrite: true);

            Console.WriteLine($"[retexture] done: {resourceDir}");
            return 0;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }
}
