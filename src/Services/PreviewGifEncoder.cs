// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.IO;
using ImageMagick;

namespace FiveOS.Services;

/// <summary>
/// Builds a looping animated GIF from JPEG/PNG frame bytes captured
/// from the Emotes preview viewport.
/// </summary>
public static class PreviewGifEncoder
{
    /// <summary>
    /// Encode <paramref name="frames"/> into an animated GIF at
    /// <paramref name="path"/>. Frame delay is derived from
    /// <paramref name="fps"/> (ImageMagick delay units = 1/100 s).
    /// </summary>
    public static void WriteAnimatedGif(IReadOnlyList<byte[]> frames, int fps, string path)
    {
        if (frames is null || frames.Count == 0)
            throw new ArgumentException("No frames to encode.", nameof(frames));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Output path is required.", nameof(path));

        fps = Math.Clamp(fps, 1, 60);
        // AnimationDelay is centiseconds (1/100 s).
        uint delay = (uint)Math.Max(2, (int)Math.Round(100.0 / fps));

        using var images = new MagickImageCollection();
        foreach (var bytes in frames)
        {
            if (bytes is null || bytes.Length == 0) continue;
            var img = new MagickImage(bytes)
            {
                AnimationDelay = delay,
                GifDisposeMethod = GifDisposeMethod.Background,
            };
            images.Add(img);
        }

        if (images.Count == 0)
            throw new InvalidOperationException("No valid image frames.");

        images[0].AnimationIterations = 0; // loop forever
        images.Quantize(new QuantizeSettings { Colors = 128 });
        images.Optimize();

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        images.Write(path, MagickFormat.Gif);
    }
}
