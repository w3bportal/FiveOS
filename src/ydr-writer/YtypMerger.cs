// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CodeWalker.GameFiles;
using SharpDX;

namespace YdrWriter;

/// <summary>
/// Builds a single merged .ytyp covering every .ydr in a pack's stream/
/// directory. Real-world FiveM prop packs ship one big ytyp that lists
/// every archetype under one CMapTypes node — housing scripts and map
/// editors register the whole dictionary in a single
/// <c>data_file 'DLC_ITYP_REQUEST'</c> declaration. Our per-prop convert
/// path produces one ytyp per asset (right when there's only one prop),
/// so the pack finalize step needs to collapse them into the conventional
/// shape.
///
/// Bounding boxes / sphere data come straight from each .ydr's embedded
/// Drawable, so no metadata sidecar is needed — the YDR is the source of
/// truth, just like RAGE itself sees it.
/// </summary>
public static class YtypMerger
{
    public static int Run(string[] args)
    {
        string? packName = null;
        string? streamDir = null;
        string? outPath = null;
        float lodDist = 500f;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pack-name": packName = args[++i]; break;
                case "--stream-dir": streamDir = args[++i]; break;
                case "--out": outPath = args[++i]; break;
                case "--lod-dist":
                    if (!float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out lodDist))
                        lodDist = 500f;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(packName) || string.IsNullOrWhiteSpace(streamDir) || string.IsNullOrWhiteSpace(outPath))
        {
            Console.Error.WriteLine("usage: ydr-writer merge-pack --pack-name <n> --stream-dir <dir> --out <ytyp> [--lod-dist 500 (fallback for ydrs without a LodDistVlow)]");
            return 2;
        }
        if (!Directory.Exists(streamDir))
        {
            Console.Error.WriteLine($"[merge-pack] stream dir not found: {streamDir}");
            return 2;
        }

        var packHash = (MetaHash)JenkHash.GenHash(packName);
        var ytyp = new YtypFile
        {
            NameHash = packHash,
            CMapTypes = new CMapTypes { name = packHash },
        };

        int added = 0;
        int skipped = 0;
        var sbLog = new StringBuilder();
        foreach (var ydrPath in Directory.EnumerateFiles(streamDir, "*.ydr"))
        {
            try
            {
                var bytes = File.ReadAllBytes(ydrPath);
                var ydr = new YdrFile();
                ydr.Load(bytes);
                var d = ydr.Drawable;
                if (d is null)
                {
                    skipped++;
                    sbLog.AppendLine($"  skip (no Drawable): {Path.GetFileName(ydrPath)}");
                    continue;
                }

                // Asset name is what archetype lookups + the YDR streamer
                // both resolve against — it must match the file basename
                // because RAGE keys streamed drawables off the file name.
                var assetName = Path.GetFileNameWithoutExtension(ydrPath);
                var nameHash = (MetaHash)JenkHash.GenHash(assetName);

                // Each .ydr carries the draw distance it was converted with
                // (Converter mirrors the user's VLow into Drawable.LodDistVlow,
                // and the archetype must agree with the drawable on when the
                // prop disappears). Use it per-archetype; --lod-dist is only
                // the fallback for a ydr that carries none.
                var archLodDist = d.LodDistVlow > 0f ? d.LodDistVlow : lodDist;

                var def = new CBaseArchetypeDef
                {
                    lodDist = archLodDist,
                    flags = 32u,                // OBJECT
                    specialAttribute = 0u,
                    bbMin = d.BoundingBoxMin,
                    bbMax = d.BoundingBoxMax,
                    bsCentre = d.BoundingCenter,
                    bsRadius = d.BoundingSphereRadius,
                    hdTextureDist = 100f,
                    name = nameHash,
                    assetName = nameHash,
                    assetType = rage__fwArchetypeDef__eAssetType.ASSET_TYPE_DRAWABLE,
                    textureDictionary = (MetaHash)0,
                    drawableDictionary = (MetaHash)0,
                    clipDictionary = (MetaHash)0,
                    // physicsDictionary MUST equal the archetype's own
                    // name hash for embedded-bound props — same fix the
                    // per-prop YtypBuilder applies. See its class doc.
                    physicsDictionary = nameHash,
                };
                var arch = new Archetype();
                arch.Init(ytyp, ref def);
                ytyp.AddArchetype(arch);
                added++;
                sbLog.AppendLine($"  + {assetName}  lodDist={archLodDist:F0}  bbox=({d.BoundingBoxMin.X:F1},{d.BoundingBoxMin.Y:F1},{d.BoundingBoxMin.Z:F1})..({d.BoundingBoxMax.X:F1},{d.BoundingBoxMax.Y:F1},{d.BoundingBoxMax.Z:F1})  r={d.BoundingSphereRadius:F1}");
            }
            catch (Exception ex)
            {
                skipped++;
                sbLog.AppendLine($"  skip (load failed: {ex.GetType().Name}): {Path.GetFileName(ydrPath)}");
            }
        }

        if (added == 0)
        {
            Console.Error.WriteLine($"[merge-pack] no archetypes added — {skipped} ydr file(s) skipped.");
            Console.Error.Write(sbLog.ToString());
            return 3;
        }

        var outBytes = ytyp.Save();
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllBytes(outPath, outBytes);

        Console.WriteLine($"[merge-pack] wrote: {outPath} ({outBytes.Length:N0} bytes, {added} archetype(s), {skipped} skipped)");
        Console.Write(sbLog.ToString());
        return 0;
    }
}
