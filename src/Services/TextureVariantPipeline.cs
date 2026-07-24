using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FiveOS.Services;

public static class TextureVariantPipeline
{
    public sealed record Result(int Ok, int Failed, string? Error);

    public static async Task<Result> RunAsync(
        EngineRunner.ConvertRequest baseRequest,
        IReadOnlyList<TextureVariant> variants,
        Action<string>? onLog = null,
        Action<int, int>? onProgress = null,
        CancellationToken cancel = default)
    {
        var ready = variants.Where(v => v.HasTextures).Take(TextureVariantImport.MaxVariants).ToList();
        if (ready.Count == 0)
            return new Result(0, 0, "No variants with textures.");

        if (!EngineRunner.IsEngineAvailable())
            return new Result(0, 0, $"Engine binary not found at {EngineRunner.EnginePath}.");

        var workRoot = Path.Combine(Path.GetTempPath(), "FiveOS", "tex-var-run",
            Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workRoot);

        var runner = new EngineRunner();
        int ok = 0, fail = 0;
        string? lastError = null;

        try
        {
            onLog?.Invoke("Converting mesh once (geometry base)…");
            onProgress?.Invoke(0, ready.Count);

            var baseName = "variant_base";
            // Always bake an embedded TextureDictionary into the base YDR —
            // retexture replaces that dict per variant. Leaving Extract off
            // would ship untextured geometry and every recolor would fail
            // or pink in-game (ytyp textureDictionary stays 0).
            var baseReq = baseRequest with
            {
                AssetName = baseName,
                RouteToPack = false,
                ExtractTextures = true,
                PartDiffuseTextures = baseRequest.PartDiffuseTextures,
            };

            var baseOutcome = await runner.RunAsync(baseReq, onLog, cancel, deliverToDir: workRoot);
            if (cancel.IsCancellationRequested)
                return new Result(ok, fail, "Cancelled.");
            if (!baseOutcome.Success || string.IsNullOrEmpty(baseOutcome.ResultPath))
                return new Result(0, 0, baseOutcome.Error ?? "Base convert failed.");

            var baseYdr = Directory.EnumerateFiles(
                    Path.Combine(baseOutcome.ResultPath, "stream"), "*.ydr")
                .FirstOrDefault();
            if (baseYdr == null || !File.Exists(baseYdr))
                return new Result(0, 0, "Base convert produced no .ydr.");

            for (int i = 0; i < ready.Count; i++)
            {
                if (cancel.IsCancellationRequested)
                    return new Result(ok, fail, "Cancelled.");

                var variant = ready[i];
                var name = TextureVariantImport.SanitizeName(variant.Name);
                if (string.IsNullOrEmpty(name))
                    name = TextureVariantImport.IndexedName(baseRequest.AssetName, i + 1);

                onProgress?.Invoke(i + 1, ready.Count);
                onLog?.Invoke($"Variant {i + 1}/{ready.Count}: {name}");

                var variantOut = Path.Combine(workRoot, "v_" + name);
                Directory.CreateDirectory(variantOut);

                var retex = await runner.RunRetextureAsync(
                    sourcePath: baseRequest.SourcePath,
                    baseYdrPath: baseYdr,
                    assetName: name,
                    outputDir: variantOut,
                    partDiffuse: variant.PartTextures,
                    excludeMeshes: baseRequest.ExcludeMeshes,
                    partMaterials: baseRequest.PartMaterials,
                    glassOpacity: baseRequest.GlassOpacity,
                    onLog: onLog,
                    cancel: cancel);

                if (!retex.Success || string.IsNullOrEmpty(retex.ResourceDir))
                {
                    fail++;
                    lastError = retex.Error ?? $"Retexture failed for {name}";
                    onLog?.Invoke($"✗ {name}: {lastError}");
                    continue;
                }

                // Recolors form a natural pack — stage them into one
                // outliner group named after the base asset so the whole
                // set finalises into a single resource.
                var entry = PropPackSession.Current.AddFromResourceDir(
                    retex.ResourceDir, name,
                    groupName: TextureVariantImport.SanitizeName(baseRequest.AssetName) + "_variants");
                if (entry is null)
                {
                    fail++;
                    lastError = $"Could not stage '{name}' into the pack.";
                    onLog?.Invoke($"✗ {name}: {lastError}");
                    continue;
                }

                ok++;
                onLog?.Invoke($"✓ {name} added to pack");
            }

            return new Result(ok, fail, fail > 0 ? lastError : null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workRoot))
                    Directory.Delete(workRoot, recursive: true);
            }
            catch { }
        }
    }
}
