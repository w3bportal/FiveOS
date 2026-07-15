// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace FiveOS.Services;

/// <summary>
/// Resolves animation import containers (UnityPackage, etc.) into an actual
/// animation file path that AnimEmoteImporter can read.
/// </summary>
public static class AnimationContainerResolver
{
    private static readonly HashSet<string> DirectFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".glb", ".gltf", ".fbx", ".dae", ".bvh"
    };

    public static ResolveResult Resolve(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            return ResolveResult.Fail("File not found.");

        var ext = Path.GetExtension(inputPath);
        if (DirectFormats.Contains(ext))
            return ResolveResult.Ok(inputPath);

        if (ext.Equals(".unitypackage", StringComparison.OrdinalIgnoreCase))
            return ResolveUnityPackage(inputPath);

        if (ext.Equals(".package", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveResult.Fail(
                "Sims 4 .package is not a direct animation exchange format. " +
                "Export/convert the clip to .fbx/.dae/.bvh first (e.g. Sims 4 Studio/Blender), then import that file.");
        }

        return ResolveResult.Fail("Unsupported animation format.");
    }

    private static ResolveResult ResolveUnityPackage(string inputPath)
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "FiveOS", "unitypackage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            // unitypackage is a tar.gz where each asset lives under a GUID folder:
            //   <guid>/pathname (text), <guid>/asset (binary content)
            var pathByGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var assetBytesByGuid = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            using var fs = File.OpenRead(inputPath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: false);
            using var reader = new TarReader(gz, leaveOpen: false);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) != null)
            {
                if (entry.EntryType == TarEntryType.Directory)
                    continue;

                var name = entry.Name?.Replace('\\', '/') ?? "";
                if (string.IsNullOrEmpty(name)) continue;
                var slash = name.IndexOf('/');
                if (slash <= 0) continue;
                var guid = name[..slash];
                var leaf = name[(slash + 1)..];

                if (leaf.Equals("pathname", StringComparison.OrdinalIgnoreCase))
                {
                    using var ms = new MemoryStream();
                    entry.DataStream?.CopyTo(ms);
                    var p = Encoding.UTF8.GetString(ms.ToArray()).Trim();
                    if (!string.IsNullOrWhiteSpace(p)) pathByGuid[guid] = p;
                }
                else if (leaf.Equals("asset", StringComparison.OrdinalIgnoreCase))
                {
                    using var ms = new MemoryStream();
                    entry.DataStream?.CopyTo(ms);
                    assetBytesByGuid[guid] = ms.ToArray();
                }
            }

            string[] supported = [".fbx", ".glb", ".gltf", ".dae", ".bvh"];
            foreach (var guid in pathByGuid.Keys)
            {
                if (!assetBytesByGuid.TryGetValue(guid, out var bytes) || bytes.Length == 0) continue;
                var unityPath = pathByGuid[guid];
                var srcExt = Path.GetExtension(unityPath);
                if (!supported.Contains(srcExt, StringComparer.OrdinalIgnoreCase)) continue;

                var safeName = Path.GetFileNameWithoutExtension(unityPath);
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "unity_anim";
                var outPath = Path.Combine(tempDir, safeName + srcExt.ToLowerInvariant());
                File.WriteAllBytes(outPath, bytes);
                return ResolveResult.Ok(outPath, $"Extracted {Path.GetFileName(unityPath)} from Unity package.");
            }

            return ResolveResult.Fail(
                "No importable animation file was found inside this .unitypackage. " +
                "FiveOS can import .fbx/.glb/.gltf/.dae/.bvh assets from Unity packages.");
        }
        catch (Exception ex)
        {
            return ResolveResult.Fail("Couldn't read .unitypackage: " + ex.Message);
        }
    }

    public sealed record ResolveResult(bool Success, string Path, string? Note, string? Error)
    {
        public static ResolveResult Ok(string path, string? note = null) => new(true, path, note, null);
        public static ResolveResult Fail(string error) => new(false, "", null, error);
    }
}

