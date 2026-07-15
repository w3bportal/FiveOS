// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.IO;
using Assimp;
using ImageMagick;

namespace FiveOS.Services;

/// <summary>
/// Helpers for the layer-panel context menu's "Textures →" actions. Walks
/// the source file with Assimp to find textures bound to a named part,
/// extracts them to a temp folder, and (for the Optimize action) runs them
/// through <see cref="TextureOptimizer"/>.
///
/// Output is always written next to the source file so the user can pick
/// up the optimized files for their YTD pipeline. The viewer is not
/// re-bound from here — live preview swaps go through viewer.html's
/// <c>setPartTexture</c> JS hook on the main window's side.
/// </summary>
public static class PartTextureService
{
    public sealed record OptimizeReport(
        int TexturesFound,
        int TexturesOptimized,
        long BytesBefore,
        long BytesAfter,
        string? OutputDir,
        string? Error);

    /// <summary>
    /// Find every texture bound to a material referenced by the named
    /// part's mesh sub-tree, write each one out as a PNG in a sibling
    /// <c>{model}_textures/</c> folder, and run <see cref="TextureOptimizer"/>
    /// over them. Embedded textures are decoded from the scene's blob list;
    /// referenced (file path) textures are pulled from disk.
    /// </summary>
    public static OptimizeReport OptimizePartTextures(string inputPath, string partName)
    {
        try
        {
            using var importer = new AssimpContext();
            var scene = importer.ImportFile(inputPath,
                PostProcessSteps.Triangulate | PostProcessSteps.EmbedTextures);
            if (scene == null || scene.RootNode == null || !scene.HasMeshes)
                return new OptimizeReport(0, 0, 0, 0, null, "Assimp couldn't load the model.");

            var meshIndices = CollectMeshIndicesForNamedNode(scene.RootNode, partName);
            if (meshIndices.Count == 0)
                return new OptimizeReport(0, 0, 0, 0, null, $"No meshes found under part '{partName}'.");

            // Materials referenced by the part's meshes (deduped).
            var materialIndices = new HashSet<int>();
            foreach (var idx in meshIndices)
                materialIndices.Add(scene.Meshes[idx].MaterialIndex);

            // Output dir: <input>_textures next to the source file.
            var srcDir = Path.GetDirectoryName(inputPath) ?? Path.GetTempPath();
            var stem = Path.GetFileNameWithoutExtension(inputPath);
            var outDir = Path.Combine(srcDir, $"{stem}_{SanitizeForPath(partName)}_textures");
            Directory.CreateDirectory(outDir);

            // Stage the part's textures as PNGs into a temp folder so the
            // shared TextureOptimizer (which expects PNG/DDS on disk) can
            // process them uniformly across embedded vs. referenced.
            var stageDir = Path.Combine(Path.GetTempPath(), "FiveOS",
                $"part-tex-{System.Guid.NewGuid().ToString("N")[..8]}");
            Directory.CreateDirectory(stageDir);

            var staged = new List<string>();
            long bytesBefore = 0;
            foreach (var mi in materialIndices)
            {
                if (mi < 0 || mi >= scene.MaterialCount) continue;
                var mat = scene.Materials[mi];
                if (mat.HasTextureDiffuse)
                    StageTexture(scene, mat.TextureDiffuse.FilePath, srcDir, stageDir, staged, ref bytesBefore);
                if (mat.HasTextureNormal)
                    StageTexture(scene, mat.TextureNormal.FilePath, srcDir, stageDir, staged, ref bytesBefore);
                if (mat.HasTextureSpecular)
                    StageTexture(scene, mat.TextureSpecular.FilePath, srcDir, stageDir, staged, ref bytesBefore);
            }

            if (staged.Count == 0)
                return new OptimizeReport(0, 0, 0, 0, null,
                    $"No textures bound to part '{partName}'.");

            var opts = TextureOptimizer.DefaultOptions();
            var optimizer = new TextureOptimizer();
            long bytesAfter = 0;
            int optimized = 0;
            foreach (var p in staged)
            {
                var r = optimizer.ProcessFile(p, outDir, opts);
                if (r.Success)
                {
                    optimized++;
                    bytesAfter += r.OutputBytes;
                }
            }

            try { Directory.Delete(stageDir, recursive: true); } catch { /* best-effort */ }

            return new OptimizeReport(staged.Count, optimized, bytesBefore, bytesAfter, outDir, null);
        }
        catch (System.Exception ex)
        {
            return new OptimizeReport(0, 0, 0, 0, null, ex.Message);
        }
    }

    /// <summary>
    /// Stage one texture (embedded "*N" form or external path) as a PNG
    /// in <paramref name="stageDir"/>, append the staged path to
    /// <paramref name="staged"/>, and accumulate input bytes.
    /// </summary>
    private static void StageTexture(Scene scene, string path, string srcDir, string stageDir,
                                     List<string> staged, ref long bytesBefore)
    {
        if (string.IsNullOrEmpty(path)) return;

        // Assimp's embedded-texture refs are "*<index>".
        if (path.StartsWith("*", System.StringComparison.Ordinal) &&
            int.TryParse(path[1..], out var ti) &&
            ti >= 0 && ti < scene.TextureCount)
        {
            var et = scene.Textures[ti];
            byte[]? bytes = null;
            if (et.HasCompressedData)
                bytes = et.CompressedData;
            else if (et.HasNonCompressedData)
            {
                using var img = new MagickImage(MagickColors.Transparent, (uint)et.Width, (uint)et.Height);
                // Bail out: building a Magick image from raw Texel struct
                // is non-trivial and rare in practice — most embedded
                // textures are PNG/JPG blobs, not raw pixel arrays.
                return;
            }
            if (bytes == null || bytes.Length == 0) return;

            var name = $"embed_{ti}";
            var outPath = Path.Combine(stageDir, name + ".png");
            try
            {
                using var img = new MagickImage(bytes);
                img.Format = MagickFormat.Png;
                img.Write(outPath);
            }
            catch { return; }
            bytesBefore += bytes.Length;
            staged.Add(outPath);
            return;
        }

        // External texture path. Assimp may give us absolute, relative-to-
        // source, or just a filename. Try in that order.
        string? resolved = null;
        if (File.Exists(path)) resolved = path;
        else
        {
            var rel = Path.Combine(srcDir, path);
            if (File.Exists(rel)) resolved = rel;
            else
            {
                var byName = Path.Combine(srcDir, Path.GetFileName(path));
                if (File.Exists(byName)) resolved = byName;
            }
        }
        if (resolved == null) return;

        var ext = Path.GetExtension(resolved).ToLowerInvariant();
        var stagedPath = Path.Combine(stageDir, Path.GetFileNameWithoutExtension(resolved) + ".png");
        try
        {
            if (ext == ".png")
            {
                File.Copy(resolved, stagedPath, overwrite: true);
            }
            else
            {
                using var img = new MagickImage(File.ReadAllBytes(resolved));
                img.Format = MagickFormat.Png;
                img.Write(stagedPath);
            }
        }
        catch { return; }

        bytesBefore += new FileInfo(resolved).Length;
        staged.Add(stagedPath);
    }

    private static List<int> CollectMeshIndicesForNamedNode(Node root, string partName)
    {
        var result = new List<int>();
        var target = FindNodeByName(root, partName);
        if (target == null) return result;

        var stack = new Stack<Node>();
        stack.Push(target);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.HasMeshes) foreach (var i in n.MeshIndices) result.Add(i);
            foreach (var c in n.Children) stack.Push(c);
        }
        return result;
    }

    // Searches the entire subtree, not just direct children: Assimp's GLTF
    // importer flattens single-scene/single-root files so the part the
    // viewer reports as a top-level child can land at the Assimp root or
    // one wrapper deeper (Sketchfab "scene.gltf" hits this case).
    private static Node? FindNodeByName(Node root, string name)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (string.Equals(n.Name, name, System.StringComparison.Ordinal)) return n;
            foreach (var c in n.Children) stack.Push(c);
        }
        return null;
    }

    private static string SanitizeForPath(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var chars = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            chars[i] = System.Array.IndexOf(bad, ch) < 0 ? ch : '_';
        }
        return new string(chars);
    }
}
