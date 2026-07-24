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

    /// <summary>One diffuse map pulled from the source model for the
    /// Props sidebar Textures list.</summary>
    public sealed record ModelTextureInfo(string Name, string Path);

    /// <summary>Grouped original textures for the Base Texture library row.</summary>
    public sealed record BaseTextureGroup(
        string? ThumbPath,
        int UniqueMapCount,
        Dictionary<string, string> PartDiffusePaths);

    /// <summary>
    /// Extract every unique diffuse texture from the model into
    /// <paramref name="stageDir"/> (PNG) for sidebar thumbnails.
    /// Best-effort — missing embeds / broken paths are skipped.
    /// </summary>
    public static IReadOnlyList<ModelTextureInfo> ListDiffuseTextures(string inputPath, string stageDir)
    {
        var group = ExtractBaseTextureGroup(inputPath, stageDir);
        if (group.UniqueMapCount == 0) return Array.Empty<ModelTextureInfo>();
        var result = new List<ModelTextureInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in group.PartDiffusePaths.Values)
        {
            if (!seen.Add(path)) continue;
            result.Add(new ModelTextureInfo(Path.GetFileNameWithoutExtension(path), path));
        }
        if (result.Count == 0 && !string.IsNullOrEmpty(group.ThumbPath))
            result.Add(new ModelTextureInfo("base", group.ThumbPath!));
        return result;
    }

    /// <summary>
    /// Pull diffuse maps from the source and group them for one
    /// "Base Texture" library row (thumb + per-part restore paths).
    /// </summary>
    public static BaseTextureGroup ExtractBaseTextureGroup(string inputPath, string stageDir)
    {
        var partPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? thumb = null;
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath)
            || string.IsNullOrWhiteSpace(stageDir))
            return new BaseTextureGroup(null, 0, partPaths);

        try
        {
            Directory.CreateDirectory(stageDir);
            using var importer = new AssimpContext();
            var scene = importer.ImportFile(inputPath,
                PostProcessSteps.Triangulate | PostProcessSteps.EmbedTextures);
            if (scene == null || !scene.HasMaterials)
                return new BaseTextureGroup(null, 0, partPaths);

            var srcDir = Path.GetDirectoryName(inputPath) ?? Path.GetTempPath();
            var texCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string? StageCached(string texPath)
            {
                if (string.IsNullOrEmpty(texPath)) return null;
                if (texCache.TryGetValue(texPath, out var hit)) return hit;
                var staged = new List<string>();
                long bytes = 0;
                StageTexture(scene, texPath, srcDir, stageDir, staged, ref bytes);
                if (staged.Count == 0) return null;
                texCache[texPath] = staged[^1];
                unique.Add(staged[^1]);
                return staged[^1];
            }

            if (scene.RootNode != null)
                WalkParts(scene.RootNode, scene, StageCached, partPaths);

            // Materials with no mesh walk hit (odd hierarchies) — still stage.
            if (unique.Count == 0)
            {
                foreach (var mat in scene.Materials)
                {
                    foreach (var texPath in EnumerateEmbeddablePaths(mat))
                        StageCached(texPath);
                }
            }
            else
            {
                // Also count normals / extra maps so "Original · N maps" matches
                // what Convert embeds into the YDR TextureDictionary.
                foreach (var mat in scene.Materials)
                {
                    foreach (var texPath in EnumerateEmbeddablePaths(mat))
                        StageCached(texPath);
                }
            }

            if (unique.Count == 0 && scene.HasTextures)
            {
                for (int i = 0; i < scene.TextureCount; i++)
                    StageCached("*" + i);
            }

            thumb = unique
                .Select(p => new FileInfo(p))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.Length)
                .Select(f => f.FullName)
                .FirstOrDefault()
                ?? unique.FirstOrDefault();
        }
        catch
        {
            // Sidebar preview only — never fail the load.
        }

        return new BaseTextureGroup(thumb, unique.Count, partPaths);
    }

    private static void WalkParts(
        Node node,
        Scene scene,
        Func<string, string?> stageCached,
        Dictionary<string, string> partPaths)
    {
        if (node.HasMeshes)
        {
            var partName = string.IsNullOrWhiteSpace(node.Name) ? "(unnamed)" : node.Name.Trim();
            foreach (var mi in node.MeshIndices)
            {
                if (mi < 0 || mi >= scene.MeshCount) continue;
                var mesh = scene.Meshes[mi];
                if (mesh.MaterialIndex < 0 || mesh.MaterialIndex >= scene.MaterialCount) continue;
                var mat = scene.Materials[mesh.MaterialIndex];
                foreach (var texPath in EnumerateAlbedoPaths(mat))
                {
                    var staged = stageCached(texPath);
                    if (staged == null) continue;
                    // First albedo wins per part — matches typical single-diffuse props.
                    if (!partPaths.ContainsKey(partName))
                        partPaths[partName] = staged;
                    break;
                }
            }
        }

        if (node.HasChildren)
        {
            foreach (var child in node.Children)
                WalkParts(child, scene, stageCached, partPaths);
        }
    }

    /// <summary>Diffuse + BaseColor (+ any other albedo-ish slots) paths
    /// on a material, in display priority order.</summary>
    private static IEnumerable<string> EnumerateAlbedoPaths(Material mat)
    {
        if (mat.HasTextureDiffuse && !string.IsNullOrEmpty(mat.TextureDiffuse.FilePath))
            yield return mat.TextureDiffuse.FilePath;

        if (mat.GetMaterialTexture(TextureType.BaseColor, 0, out var baseColor) &&
            !string.IsNullOrEmpty(baseColor.FilePath))
            yield return baseColor.FilePath;

        // Some importers park glTF baseColor under Unknown / Diffuse only
        // after a second pass — sweep remaining slots for color maps.
        foreach (var type in new[] { TextureType.Diffuse, TextureType.BaseColor, TextureType.Unknown })
        {
            foreach (var slot in mat.GetMaterialTextures(type))
            {
                if (!string.IsNullOrEmpty(slot.FilePath))
                    yield return slot.FilePath;
            }
        }
    }

    /// <summary>Maps TextureBaker embeds into the YDR: albedo + normals
    /// (legacy and PBR slots). Used for Base Texture library counts.</summary>
    private static IEnumerable<string> EnumerateEmbeddablePaths(Material mat)
    {
        foreach (var p in EnumerateAlbedoPaths(mat))
            yield return p;

        if (mat.HasTextureNormal && !string.IsNullOrEmpty(mat.TextureNormal.FilePath))
            yield return mat.TextureNormal.FilePath;

        if (mat.GetMaterialTexture(TextureType.NormalCamera, 0, out var nCam) &&
            !string.IsNullOrEmpty(nCam.FilePath))
            yield return nCam.FilePath;

        foreach (var type in new[] { TextureType.Normals, TextureType.NormalCamera, TextureType.Height })
        {
            foreach (var slot in mat.GetMaterialTextures(type))
            {
                if (!string.IsNullOrEmpty(slot.FilePath))
                    yield return slot.FilePath;
            }
        }
    }

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
    /// Stage one texture (embedded "*N" / ".fbm/…" or external path) as a PNG
    /// in <paramref name="stageDir"/>, append the staged path to
    /// <paramref name="staged"/>, and accumulate input bytes.
    /// </summary>
    private static void StageTexture(Scene scene, string path, string srcDir, string stageDir,
                                     List<string> staged, ref long bytesBefore)
    {
        if (string.IsNullOrEmpty(path)) return;

        // GetEmbeddedTexture resolves "*N" AND virtual FBX ".fbm/Image_N" paths.
        var emb = scene.GetEmbeddedTexture(path);
        if (emb != null && emb.HasCompressedData && emb.CompressedData is { Length: > 0 } bytes)
        {
            var stem = path.StartsWith("*", System.StringComparison.Ordinal)
                ? "embed_" + path.TrimStart('*')
                : SanitizeForPath(Path.GetFileNameWithoutExtension(path));
            if (string.IsNullOrEmpty(stem)) stem = "embed";
            var outPath = Path.Combine(stageDir, stem + ".png");
            // Dedup if two slots point at the same embed.
            if (File.Exists(outPath))
            {
                staged.Add(outPath);
                return;
            }
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

        // Legacy "*N" index walk when GetEmbeddedTexture misses.
        if (path.StartsWith("*", System.StringComparison.Ordinal) &&
            int.TryParse(path[1..], out var ti) &&
            ti >= 0 && ti < scene.TextureCount)
        {
            var et = scene.Textures[ti];
            if (!et.HasCompressedData || et.CompressedData is not { Length: > 0 } raw)
                return;
            var outPath = Path.Combine(stageDir, $"embed_{ti}.png");
            if (File.Exists(outPath)) { staged.Add(outPath); return; }
            try
            {
                using var img = new MagickImage(raw);
                img.Format = MagickFormat.Png;
                img.Write(outPath);
            }
            catch { return; }
            bytesBefore += raw.Length;
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
        if (File.Exists(stagedPath)) { staged.Add(stagedPath); return; }
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
