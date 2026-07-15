// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using ImageMagick;

namespace FiveOS.Services;

/// <summary>
/// Downscales and re-encodes loose textures (PNG / DDS) to a compact
/// block-compressed .dds for FiveM. Built entirely on Magick.NET (Apache-2.0):
/// optionally trims empty margins and keeps a small bleed, scales DOWN to a
/// target resolution (never up), pads to the exact canvas, and writes DXT/BCn
/// with a mip chain.
/// </summary>
public sealed class TextureOptimizer
{
    public sealed record Options(
        bool Trim,
        bool Scale,
        bool FillBackground,
        MagickColor BackgroundColor,
        uint Width,
        uint Height,
        bool Square,
        uint BorderPx,
        CompressionMethod Compression,
        bool GenerateMipmaps);

    public sealed record FileResult(
        string SourcePath,
        string OutputPath,
        long InputBytes,
        long OutputBytes,
        bool Success,
        string? Error);

    /// <summary>Sensible defaults: square 1024, trim + 8px bleed, DXT1 + mips.</summary>
    public static Options DefaultOptions() => new(
        Trim: true,
        Scale: true,
        FillBackground: false,
        BackgroundColor: new MagickColor(0, 0, 0, 0),
        Width: 1024,
        Height: 1024,
        Square: true,
        BorderPx: 8,
        Compression: CompressionMethod.DXT1,
        GenerateMipmaps: true);

    /// <summary>Process one PNG/DDS file → <paramref name="outputDir"/>/&lt;name&gt;.dds.</summary>
    public FileResult ProcessFile(string sourcePath, string outputDir, Options opts)
    {
        try
        {
            long inputBytes = new FileInfo(sourcePath).Length;
            uint canvasW = opts.Width;
            uint canvasH = opts.Square ? opts.Width : opts.Height;
            var pad = opts.FillBackground ? opts.BackgroundColor : MagickColors.Transparent;

            using var img = LoadImage(sourcePath);

            // Trim uniform margins, then re-add a small transparent bleed so a
            // later downscale doesn't bleed the edge into neighbouring pixels.
            if (opts.Trim)
            {
                img.Trim();
                if (opts.BorderPx > 0)
                {
                    img.BackgroundColor = pad;
                    img.BorderColor = pad;
                    img.Border(opts.BorderPx, opts.BorderPx);
                }
            }

            // Fit inside the target box, aspect preserved — Greater = shrink only,
            // never upscale (we don't invent pixels).
            if (opts.Scale)
                img.Resize(new MagickGeometry(canvasW, canvasH) { Greater = true });

            // Pad to the exact canvas so every output is uniform, even if the
            // source was already smaller than the target.
            if (opts.Scale || opts.Trim)
            {
                img.BackgroundColor = pad;
                img.Extent(canvasW, canvasH, Gravity.Center, pad);
            }

            img.Format = MagickFormat.Dds;
            img.Settings.Compression = opts.Compression;
            // dds:mipmaps is a LEVEL COUNT, not a flag: write a full chain
            // (log2(longest)+1 levels) when asked, otherwise just the base level.
            var mipLevels = opts.GenerateMipmaps
                ? ((int)Math.Log2(Math.Max(img.Width, img.Height)) + 1).ToString()
                : "1";
            img.Settings.SetDefine(MagickFormat.Dds, "mipmaps", mipLevels);

            Directory.CreateDirectory(outputDir);
            var outPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(sourcePath) + ".dds");
            var bytes = img.ToByteArray();
            File.WriteAllBytes(outPath, bytes);
            return new FileResult(sourcePath, outPath, inputBytes, bytes.LongLength, true, null);
        }
        catch (Exception ex)
        {
            return new FileResult(sourcePath, "", 0, 0, false, ex.Message);
        }
    }

    private static MagickImage LoadImage(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Path.GetExtension(path).ToLowerInvariant() == ".png"
            ? new MagickImage(bytes, MagickFormat.Png)
            : new MagickImage(bytes);
    }
}
