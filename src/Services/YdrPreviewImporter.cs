// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using CodeWalker.GameFiles;
using FiveOS.Services.Sims;

namespace FiveOS.Services;

/// <summary>Turns a GTA V drawable into a .glb so the Props viewport can show
/// it before conversion.
///
/// This is a PREVIEW path only. The conversion itself reads the .ydr natively
/// (ydr-writer's GtaIv importer) — routing the real export through this .glb
/// would throw away vertex colours, tangents and the source shader names.
/// So the caller keeps the original .ydr as the convert source and hands the
/// viewer the .glb this produces.
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
        string? Error);

    /// <summary>Extract the drawable's High detail models into a .glb.</summary>
    public static Result Import(string ydrPath)
    {
        try
        {
            var ydr = new YdrFile();
            ydr.Load(File.ReadAllBytes(ydrPath));
            var drawable = ydr.Drawable;
            if (drawable == null) return new Result(false, null, 0, 0, "That .ydr contains no drawable.");

            var models = drawable.DrawableModels?.High;
            if (models == null || models.Length == 0)
                return new Result(false, null, 0, 0, "That .ydr has no High detail geometry.");

            // The extractor writes any textures it resolves into this session
            // folder; the preview only needs geometry, but it wants a real
            // directory to work against.
            var sessionDir = Path.Combine(Path.GetTempPath(), "FiveOS",
                "YdrPreview-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sessionDir);

            var extractor = new DrawableMeshExtractor(sessionDir);
            var meshes = new List<SimsMesh>();
            int tris = 0;

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

                    var mesh = new SimsMesh { Name = $"geom{meshes.Count:D2}" };
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
                    tris += mesh.TriangleCount;
                    meshes.Add(mesh);
                }
            }

            if (meshes.Count == 0)
                return new Result(false, null, 0, 0, "Couldn't read any geometry out of that .ydr.");

            var glbPath = Path.Combine(sessionDir, Path.GetFileNameWithoutExtension(ydrPath) + ".glb");
            SimsGlbWriter.Write(glbPath, meshes);
            return new Result(true, glbPath, meshes.Count, tris, null);
        }
        catch (Exception ex)
        {
            return new Result(false, null, 0, 0, $"Couldn't read that .ydr: {ex.Message}");
        }
    }
}
