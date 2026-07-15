// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace FiveOS.Services;

/// <summary>
/// Post-processes a GLB written by AssimpNet's "glb2" exporter so it is
/// actually self-contained. assimp 5.0's glTF2 exporter does not embed
/// textures the importer parsed out of the source file — FBX-embedded
/// textures land in <c>Scene.Textures</c> but the exporter writes the
/// source's original relative path (e.g. "temp.fbm/Baked_BaseColor.png")
/// straight into <c>images[].uri</c>. That folder never exists on disk, so
/// three.js 404s every texture and the optimized preview renders gray.
///
/// This rewrites each external image reference into a bufferView backed by
/// bytes appended to the GLB's BIN chunk, resolving the bytes from the
/// imported scene's embedded textures first and falling back to files next
/// to the original source model.
/// </summary>
public static class GlbTextureEmbedder
{
    private const uint GlbMagic = 0x46546C67; // 'glTF'
    private const uint ChunkJson = 0x4E4F534A; // 'JSON'
    private const uint ChunkBin = 0x004E4942;  // 'BIN\0'

    /// <summary>Embed every externally-referenced image in
    /// <paramref name="glbPath"/> whose bytes we can resolve. Returns the
    /// number of images embedded (0 = file untouched).
    /// <paramref name="maxDim"/> &gt; 0 additionally downscales any image
    /// larger than maxDim×maxDim (aspect preserved, format kept) — this is
    /// the actual "optimize" for texture-weight models, where the mesh is
    /// lean and megabytes live entirely in 4K bakes.</summary>
    public static int EmbedImages(string glbPath, Assimp.Scene scene,
                                  string? sourceDir, System.Action<string>? log = null,
                                  int maxDim = 0)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(glbPath); }
        catch { return 0; }
        if (bytes.Length < 28) return 0;
        if (System.BitConverter.ToUInt32(bytes, 0) != GlbMagic) return 0;

        var jsonLen = (int)System.BitConverter.ToUInt32(bytes, 12);
        if (System.BitConverter.ToUInt32(bytes, 16) != ChunkJson) return 0;
        if (20 + jsonLen > bytes.Length) return 0;

        JsonObject root;
        try
        {
            root = JsonNode.Parse(Encoding.UTF8.GetString(bytes, 20, jsonLen)) as JsonObject
                   ?? throw new System.InvalidOperationException("glTF JSON root is not an object");
        }
        catch (System.Exception ex)
        {
            log?.Invoke($"GLB post-process skipped (JSON parse failed: {ex.Message})");
            return 0;
        }

        if (root["images"] is not JsonArray images || images.Count == 0) return 0;

        // Existing BIN chunk (optional per spec, always present in our exports).
        var binStream = new MemoryStream();
        int binHeader = 20 + jsonLen;
        if (binHeader + 8 <= bytes.Length &&
            System.BitConverter.ToUInt32(bytes, binHeader + 4) == ChunkBin)
        {
            var binLen = (int)System.BitConverter.ToUInt32(bytes, binHeader);
            binLen = System.Math.Min(binLen, bytes.Length - binHeader - 8);
            binStream.Write(bytes, binHeader + 8, binLen);
        }

        var bufferViews = root["bufferViews"] as JsonArray;
        if (bufferViews == null)
        {
            bufferViews = new JsonArray();
            root["bufferViews"] = bufferViews;
        }

        // Decide which images may be JPEG-ed. Two exclusions:
        //  · normal maps — JPEG blocking turns into shading artifacts;
        //  · images used by a MASK/BLEND material — their alpha channel is
        //    live and JPEG can't carry one.
        // Everything else (baseColor/emissive/ORM under alphaMode OPAQUE)
        // renders identically without alpha — baked atlases DO carry a
        // transparent-gutter alpha channel, but OPAQUE materials never
        // sample it, so it's pure dead weight.
        var noJpegImages = new System.Collections.Generic.HashSet<int>();
        if (maxDim > 0 && root["materials"] is JsonArray mats && root["textures"] is JsonArray texArr)
        {
            int ImageOfTexture(int ti) =>
                (ti >= 0 && ti < texArr.Count ? (texArr[ti] as JsonObject)?["source"]?.GetValue<int>() : null) ?? -1;

            foreach (var matNode in mats)
            {
                if (matNode is not JsonObject mat) continue;

                var normalSrc = ImageOfTexture(
                    (mat["normalTexture"] as JsonObject)?["index"]?.GetValue<int>() ?? -1);
                if (normalSrc >= 0) noJpegImages.Add(normalSrc);

                var alphaMode = mat["alphaMode"]?.GetValue<string>() ?? "OPAQUE";
                if (alphaMode == "OPAQUE") continue;
                var baseSrc = ImageOfTexture(
                    ((mat["pbrMetallicRoughness"] as JsonObject)?["baseColorTexture"] as JsonObject)
                        ?["index"]?.GetValue<int>() ?? -1);
                if (baseSrc >= 0) noJpegImages.Add(baseSrc);
            }
        }

        int embedded = 0;
        for (int imgIdx = 0; imgIdx < images.Count; imgIdx++)
        {
            if (images[imgIdx] is not JsonObject img) continue;
            var uri = img["uri"]?.GetValue<string>();
            if (string.IsNullOrEmpty(uri) || uri.StartsWith("data:", System.StringComparison.OrdinalIgnoreCase))
                continue;

            var path = System.Uri.UnescapeDataString(uri).Replace('\\', '/');
            var data = ResolveImageBytes(path, scene, sourceDir, out var mime);
            if (data == null || mime == null)
            {
                log?.Invoke($"texture '{path}' unresolved — reference left as-is");
                continue;
            }
            if (maxDim > 0)
            {
                var optimized = OptimizeImage(data, maxDim, allowJpeg: !noJpegImages.Contains(imgIdx), path, log);
                if (optimized != null)
                {
                    data = optimized;
                    mime = SniffMime(data) ?? mime;
                }
            }

            while (binStream.Length % 4 != 0) binStream.WriteByte(0);
            var view = new JsonObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = (int)binStream.Length,
                ["byteLength"] = data.Length,
            };
            binStream.Write(data, 0, data.Length);
            bufferViews.Add(view);

            img.Remove("uri");
            img["bufferView"] = bufferViews.Count - 1;
            img["mimeType"] = mime;
            embedded++;
        }
        if (embedded == 0) return 0;

        while (binStream.Length % 4 != 0) binStream.WriteByte(0);

        if (root["buffers"] is not JsonArray buffers || buffers.Count == 0)
        {
            buffers = new JsonArray { new JsonObject() };
            root["buffers"] = buffers;
        }
        ((JsonObject)buffers[0]!)["byteLength"] = (int)binStream.Length;

        var jsonOut = Encoding.UTF8.GetBytes(root.ToJsonString());
        int jsonPad = (4 - jsonOut.Length % 4) % 4;
        var binOut = binStream.ToArray();

        using var outStream = new MemoryStream();
        using (var w = new BinaryWriter(outStream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(GlbMagic);
            w.Write(2u);
            w.Write((uint)(12 + 8 + jsonOut.Length + jsonPad + 8 + binOut.Length));
            w.Write((uint)(jsonOut.Length + jsonPad));
            w.Write(ChunkJson);
            w.Write(jsonOut);
            for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);
            w.Write((uint)binOut.Length);
            w.Write(ChunkBin);
            w.Write(binOut);
        }
        File.WriteAllBytes(glbPath, outStream.ToArray());
        log?.Invoke($"embedded {embedded} texture{(embedded == 1 ? "" : "s")} into GLB");
        return embedded;
    }

    private static byte[]? ResolveImageBytes(string path, Assimp.Scene scene,
                                             string? sourceDir, out string? mime)
    {
        mime = null;

        // "*N" star form — direct index into the scene's embedded textures.
        if (path.StartsWith('*') && int.TryParse(path.AsSpan(1), out var starIdx) &&
            starIdx >= 0 && starIdx < scene.TextureCount)
        {
            var data = CompressedBytes(scene.Textures[starIdx]);
            if (data != null) { mime = SniffMime(data); return mime == null ? null : data; }
        }

        // Match by full relative path, then by bare filename. Assimp's FBX
        // importer registers embeds under the path the FBX referenced
        // (usually "<name>.fbm/<file>"), which is also what the exporter
        // leaks into the URI — so full-path match is the common hit.
        var baseName = Path.GetFileName(path);
        if (scene.HasTextures)
        {
            foreach (var tex in scene.Textures)
            {
                var fn = (tex.Filename ?? "").Replace('\\', '/');
                if (fn.Length == 0) continue;
                if (!fn.Equals(path, System.StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFileName(fn).Equals(baseName, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                var data = CompressedBytes(tex);
                if (data != null) { mime = SniffMime(data); return mime == null ? null : data; }
            }
        }

        // Fall back to a real file relative to the source model (external
        // textures that EmbedTextures didn't fold in for whatever reason).
        if (!string.IsNullOrEmpty(sourceDir))
        {
            foreach (var candidate in new[]
            {
                Path.Combine(sourceDir, path.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(sourceDir, baseName),
            })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    var data = File.ReadAllBytes(candidate);
                    mime = SniffMime(data);
                    return mime == null ? null : data;
                }
                catch { /* unreadable candidate — try the next */ }
            }
        }
        return null;
    }

    /// <summary>Shrink texture bytes: resize to fit maxDim×maxDim (aspect
    /// preserved) and, for opaque non-normal maps, re-encode as JPEG when
    /// that's meaningfully smaller. Returns null when nothing improved (or
    /// decoding failed) — caller keeps the original bytes.</summary>
    private static byte[]? OptimizeImage(byte[] data, int maxDim, bool allowJpeg,
                                         string name, System.Action<string>? log)
    {
        try
        {
            using var img = new ImageMagick.MagickImage(data);
            var note = $"{img.Width}×{img.Height}";
            byte[] best = data;
            if (img.Width > (uint)maxDim || img.Height > (uint)maxDim)
            {
                img.Resize(new ImageMagick.MagickGeometry((uint)maxDim, (uint)maxDim));
                best = img.ToByteArray();
                note += $" → {img.Width}×{img.Height}";
            }
            // JPEG re-encode for color data whose alpha the material never
            // samples (caller filters normal maps and MASK/BLEND images).
            // Alpha(Off) drops the channel while keeping the RGB under it —
            // baked gutters keep their bleed color. 20% minimum win so we
            // don't trade lossless for a rounding error.
            if (allowJpeg)
            {
                img.Alpha(ImageMagick.AlphaOption.Off);
                img.Quality = 88;
                var jpg = img.ToByteArray(ImageMagick.MagickFormat.Jpeg);
                if (jpg.Length < best.Length * 0.8)
                {
                    best = jpg;
                    note += " · JPEG";
                }
            }
            if (ReferenceEquals(best, data)) return null;
            log?.Invoke($"optimized '{Path.GetFileName(name)}' {note} " +
                        $"({data.Length / 1024:N0} → {best.Length / 1024:N0} KB)");
            return best;
        }
        catch (System.Exception ex)
        {
            log?.Invoke($"optimize of '{Path.GetFileName(name)}' failed ({ex.Message}) — kept original");
            return null;
        }
    }

    private static byte[]? CompressedBytes(Assimp.EmbeddedTexture tex)
    {
        // Raw ARGB embeds (IsCompressed == false) are vanishingly rare in
        // the wild and glTF can't carry them without an encode step — skip.
        if (!tex.IsCompressed) return null;
        var data = tex.CompressedData;
        return (data != null && data.Length > 0) ? data : null;
    }

    /// <summary>glTF only allows PNG and JPEG images. Sniff the magic bytes
    /// rather than trusting extensions/format hints; return null for
    /// anything a browser can't decode (TGA, DDS, ...).</summary>
    private static string? SniffMime(byte[] data)
    {
        if (data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return "image/png";
        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8)
            return "image/jpeg";
        return null;
    }
}
