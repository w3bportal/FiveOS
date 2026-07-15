// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using CodeWalker.GameFiles;
using CodeWalker.Utils;
using ImageMagick;

namespace FiveOS.Services;

/// <summary>
/// Decodes every texture inside a queued file into a small PNG thumbnail for
/// the Optimize tab's flat texture gallery (the right-hand panel shown in the
/// texture modes). Handles both standalone <c>.ytd</c> dictionaries and the
/// embedded <c>ShaderGroup.TextureDictionary</c> carried by drawable models
/// (<c>.ydd</c> / <c>.ydr</c> / <c>.yft</c>).
///
/// Reuses the DDS→PNG path proven by <see cref="DrawableMeshExtractor"/> —
/// <c>DDSIO.GetDDSFile</c> + Magick.NET — but resizes to a thumbnail so a
/// dictionary full of 4K maps doesn't pull hundreds of MB of bitmaps into the
/// UI. Never throws: a texture it can't decode still yields a tile with its
/// name / dimensions / format and a null thumbnail.
/// </summary>
public static class TextureGalleryExtractor
{
    /// <summary>One texture's gallery entry. <paramref name="Width"/>/<paramref
    /// name="Height"/> are the ORIGINAL dimensions; <paramref name="ThumbPng"/>
    /// is a shrink-to-256 PNG (null when decode failed).</summary>
    public sealed record TexInfo(string Name, int Width, int Height, string Format, byte[]? ThumbPng);

    // Longest-side cap for the thumbnail; "256x256>" only shrinks larger images.
    private static readonly MagickGeometry ThumbGeometry = new("256x256>");

    public static List<TexInfo> Extract(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var dicts = new List<TextureDictionary?>();
        switch (ext)
        {
            case ".ytd":
                dicts.Add(DrawableOptimizer.LoadResource<YtdFile>(path).TextureDict);
                break;
            case ".ydd":
                var ydd = DrawableOptimizer.LoadResource<YddFile>(path);
                if (ydd.Drawables != null)
                    foreach (var d in ydd.Drawables)
                        if (d != null) dicts.Add(d.ShaderGroup?.TextureDictionary);
                break;
            case ".ydr":
                dicts.Add(DrawableOptimizer.LoadResource<YdrFile>(path).Drawable?.ShaderGroup?.TextureDictionary);
                break;
            case ".yft":
                dicts.Add(DrawableOptimizer.LoadResource<YftFile>(path).Fragment?.Drawable?.ShaderGroup?.TextureDictionary);
                break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<TexInfo>();
        foreach (var td in dicts)
        {
            var items = td?.Textures?.data_items;
            if (items == null) continue;
            foreach (var tex in items)
            {
                if (tex == null) continue;
                var name = tex.Name ?? tex.NameHash.ToString();
                if (!seen.Add(name)) continue;   // shared across drawables — decode once

                byte[]? thumb = null;
                try
                {
                    var dds = DDSIO.GetDDSFile(tex);
                    if (dds != null && dds.Length > 0)
                    {
                        using var img = new MagickImage(dds);
                        img.Resize(ThumbGeometry);
                        thumb = img.ToByteArray(MagickFormat.Png);
                    }
                }
                catch
                {
                    thumb = null;   // undecodable — still list it (name/size/format)
                }

                list.Add(new TexInfo(name, tex.Width, tex.Height, tex.Format.ToString(), thumb));
            }
        }
        return list;
    }
}
