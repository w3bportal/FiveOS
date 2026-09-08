// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using CodeWalker.GameFiles;
using FiveOS.Services.Sims;

namespace FiveOS.Services;

/// <summary>Turns a GTA V model file into a .glb so the Props viewport can show
/// it before conversion. Reads drawables (<c>.ydr</c>), fragments
/// (<c>.yft</c> — the main drawable) and drawable dictionaries (<c>.ydd</c> —
/// every drawable, each as its own named mesh group).
///
/// This is a PREVIEW path only, and now also what the exporter converts:
/// the engine imports mesh formats, not RAGE resources. Routing an export through this
/// .glb would throw away vertex colours, tangents and the source shader
/// names. So the caller keeps the original file as the convert source and
/// hands the viewer the .glb this produces. The GTA V exporter only reads
/// mesh formats, so it converts this .glb (textures embedded).
///
/// Textures: a GTA V model normally keeps its diffuse in a separate .ytd
/// (same name, a <c>+hi</c>/<c>+hifr</c> high-detail set, or one shared
/// dictionary for a set of props) rather than embedded. Every .ytd beside the
/// model is read, and when both an embedded and an external copy of a texture
/// exist the larger one wins — that is the game's own hi-res version.
///
/// Mirrors what the Sims .package path already does: unpack to a .glb, let
/// everything downstream treat it as an ordinary import.</summary>
public static class YdrPreviewImporter
{
    public sealed record Result(
        bool Success,
        string? GlbPath,
        int MeshCount,
        int TriangleCount,
        string? Error,
        int TextureCount = 0,
        int DrawableCount = 1);

    /// <summary>Extract the model's High detail geometry into a .glb. The
    /// extension decides the loader; textures come from any .ytd beside it.</summary>
    public static Result Import(string modelPath)
    {
        try
        {
            var ext = Path.GetExtension(modelPath).ToLowerInvariant();
            var stem = Path.GetFileNameWithoutExtension(modelPath);
            var bytes = File.ReadAllBytes(modelPath);
            var drawables = new List<(DrawableBase Drawable, string Name)>();

            // An Enhanced-edition (gen9) file uses a different block layout;
            // CodeWalker picks it from the process-wide RpfManager.IsGen9.
            // Only ever switch it ON here — legacy files still parse under
            // gen9 (CodeWalker downgrades per file), the reverse reads garbage.
            if (GameResourceHeader.IsGen9(bytes) && !RpfManager.IsGen9)
            {
                FosLogger.Info("ydr-preview", "Enhanced-edition resource detected — switching CodeWalker to gen9 layout");
                RpfManager.IsGen9 = true;
            }

            switch (ext)
            {
                case ".yft":
                {
                    var yft = new YftFile();
                    yft.Load(bytes);
                    var frag = yft.Fragment;
                    var d = frag?.Drawable ?? frag?.DrawableCloth;
                    if (d != null) drawables.Add((d, stem));
                    else
                    {
                        // Some fragments keep their meshes in DrawableArray only.
                        var arr = frag?.DrawableArray?.data_items;
                        if (arr != null)
                            for (int i = 0; i < arr.Length; i++)
                                if (arr[i] != null) drawables.Add((arr[i], $"{stem}_part{i:D2}"));
                    }
                    if (drawables.Count == 0) return new Result(false, null, 0, 0, "That .yft contains no drawable.");
                    break;
                }
                case ".ydd":
                {
                    var ydd = new YddFile();
                    ydd.Load(bytes);
                    var all = ydd.Drawables;
                    if (all == null || all.Length == 0) return new Result(false, null, 0, 0, "That .ydd contains no drawables.");
                    foreach (var d in all)
                    {
                        if (d == null) continue;
                        // CodeWalker names dictionary drawables from JenkIndex when
                        // it knows the string, else "hash_XXXXXXXX" — both usable.
                        var n = string.IsNullOrWhiteSpace(d.Name) ? $"drawable{drawables.Count:D2}" : d.Name;
                        drawables.Add((d, n));
                    }
                    if (drawables.Count == 0) return new Result(false, null, 0, 0, "That .ydd contains no drawables.");
                    break;
                }
                default:
                {
                    var ydr = new YdrFile();
                    ydr.Load(bytes);
                    var d = ydr.Drawable;
                    if (d == null) return new Result(false, null, 0, 0, "That .ydr contains no drawable.");
                    drawables.Add((d, stem));
                    break;
                }
            }

            Dictionary<uint, Texture>? external = null;
            HashSet<uint>? hiHashes = null;
            try
            {
                var bases = new List<DrawableBase>(drawables.Count);
                foreach (var (d, _) in drawables) bases.Add(d);
                var resolved = ClothingTextureResolver.LoadDetailed(modelPath, bases);
                if (resolved.Map.Count > 0) external = resolved.Map;
                if (resolved.HiHashes.Count > 0) hiHashes = resolved.HiHashes;
            }
            catch (Exception ex) { FosLogger.Warn("ydr-preview", "sibling .ytd load failed", ex); }

            return ImportDrawables(drawables, stem, external, hiHashes);
        }
        catch (Exception ex)
        {
            return new Result(false, null, 0, 0, $"Couldn't read that model: {ex.Message}");
        }
    }

    /// <summary>Same as <see cref="Import"/> for a drawable that is already in
    /// memory (e.g. pulled straight out of the game RPFs by
    /// <see cref="GamePropPreview"/>). Positions stay in raw RAGE model space
    /// (Z-up) — no axis swap — so a bone-relative preview lines up with what
    /// AttachEntityToEntity does in-game.
    ///
    /// <paramref name="externalTextures"/> (name-hash → texture) fills in the
    /// diffuse for shaders whose texture isn't embedded; null previews the
    /// geometry untextured.</summary>
    public static Result ImportDrawable(DrawableBase drawable, string name,
        Dictionary<uint, Texture>? externalTextures = null)
        => ImportDrawables(new[] { (drawable, name) }, name, externalTextures, preferLargerExternalFor: null);

    /// <summary>One .glb holding every drawable, each drawable's geometries
    /// grouped under a node named after it (so the layers panel can separate
    /// the models of a dictionary). <paramref name="preferLargerExternalFor"/>
    /// = name hashes from the model's own high-detail set, allowed to beat an
    /// embedded copy when larger.</summary>
    public static Result ImportDrawables(IReadOnlyList<(DrawableBase Drawable, string Name)> drawables, string fileName,
        Dictionary<uint, Texture>? externalTextures, HashSet<uint>? preferLargerExternalFor)
    {
        try
        {
            if (drawables.Count == 0) return new Result(false, null, 0, 0, "Nothing to preview.");

            // The extractor decodes any textures it resolves into this session
            // folder (tex/*.png); the .glb writer then embeds them.
            var sessionDir = Path.Combine(Path.GetTempPath(), "FiveOS",
                "YdrPreview-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sessionDir);

            var extractor = new DrawableMeshExtractor(sessionDir)
            {
                PreferLargerExternalFor = preferLargerExternalFor,
            };
            extractor.SetExternalTextures(externalTextures is { Count: > 0 } ? externalTextures : null);
            var meshes = new List<SimsMesh>();
            var textureFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int tris = 0;
            int emptyDrawables = 0;

            foreach (var (drawable, drawableName) in drawables)
            {
                var models = drawable?.DrawableModels?.High;
                if (drawable == null || models == null || models.Length == 0) { emptyDrawables++; continue; }

                int geomIndex = 0;
                foreach (var model in models)
                {
                    var geoms = model?.Geometries;
                    if (geoms == null || model == null) continue;

                    for (int gi = 0; gi < geoms.Length; gi++)
                    {
                        var geom = geoms[gi];
                        if (geom == null) continue;

                        DrawableMeshExtractor.Part? part;
                        try { part = extractor.ExtractGeometry(geom, model, gi, drawable); }
                        catch { continue; }
                        if (part == null || part.Positions.Length < 9 || part.Indices.Length < 3) continue;

                        // One drawable → "geomNN"; a dictionary → "<drawable>/geomNN"
                        // so every model keeps its identity in the layers panel.
                        var meshName = drawables.Count == 1
                            ? $"geom{meshes.Count:D2}"
                            : $"{SafeNodeName(drawableName)}/geom{geomIndex:D2}";
                        geomIndex++;

                        var mesh = new SimsMesh { Name = meshName };
                        for (int i = 0; i + 2 < part.Positions.Length; i += 3)
                            mesh.Positions.Add(new Vector3(part.Positions[i], part.Positions[i + 1], part.Positions[i + 2]));

                        if (part.Normals != null)
                            for (int i = 0; i + 2 < part.Normals.Length; i += 3)
                                mesh.Normals.Add(new Vector3(part.Normals[i], part.Normals[i + 1], part.Normals[i + 2]));

                        if (part.Uvs != null)
                            for (int i = 0; i + 1 < part.Uvs.Length; i += 2)
                                mesh.Uvs.Add(new Vector2(part.Uvs[i], part.Uvs[i + 1]));

                        foreach (var idx in part.Indices) mesh.Indices.Add(idx);

                        if (!mesh.IsUsable) continue;

                        // TextureFile is viewer-root relative ("tex/foo.png").
                        if (!string.IsNullOrEmpty(part.TextureFile))
                        {
                            var abs = Path.Combine(sessionDir, part.TextureFile.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(abs))
                            {
                                mesh.TexturePath = abs;
                                textureFiles.Add(abs);
                            }
                        }

                        tris += mesh.TriangleCount;
                        meshes.Add(mesh);
                    }
                }
            }

            if (meshes.Count == 0)
                return new Result(false, null, 0, 0, emptyDrawables == drawables.Count
                    ? "That model has no High detail geometry."
                    : "Couldn't read any geometry out of that model.");

            var safeName = string.IsNullOrWhiteSpace(fileName) ? "drawable" : fileName;
            foreach (var c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');
            var glbPath = Path.Combine(sessionDir, safeName + ".glb");
            SimsGlbWriter.Write(glbPath, meshes);
            return new Result(true, glbPath, meshes.Count, tris, null, textureFiles.Count, drawables.Count - emptyDrawables);
        }
        catch (Exception ex)
        {
            return new Result(false, null, 0, 0, $"Couldn't read that drawable: {ex.Message}");
        }
    }

    private static string SafeNodeName(string name)
    {
        var s = (name ?? "").Trim();
        if (s.Length == 0) return "drawable";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_');
        return sb.ToString();
    }
}

/// <summary>Reads the 16-byte RSC7 header of an on-disk GTA V resource.</summary>
public static class GameResourceHeader
{
    private const uint Rsc7 = 0x37435352u;

    /// <summary>Resource version encoded in the header flags (0 when the
    /// bytes carry no RSC7 header).</summary>
    public static int Version(byte[] data)
    {
        if (data == null || data.Length < 16 || BitConverter.ToUInt32(data, 0) != Rsc7) return 0;
        uint sys = BitConverter.ToUInt32(data, 8), gfx = BitConverter.ToUInt32(data, 12);
        return (int)(((sys >> 28) << 4) | (gfx >> 28));
    }

    /// <summary>True for the Enhanced-edition (gen9) drawable/dictionary
    /// (159) and fragment (171) versions; legacy files are 165 / 162.</summary>
    public static bool IsGen9(byte[] data) => Version(data) is 159 or 171;
}
