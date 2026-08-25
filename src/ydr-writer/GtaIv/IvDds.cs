// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

namespace YdrWriter.GtaIv;

/// <summary>Minimal DDS header reader — just enough to fill in the two fields
/// an .otd entry needs (mip count and pixel format).
///
/// We read them off the file rather than recomputing, because the .otd's
/// PixelFormat has to agree with the DDS's actual FourCC. Claim DXT5 over
/// DXT1 pixels and ResourceBuilder walks the block data with the wrong
/// stride, which is one of the inputs that trips its fail-fast crash.</summary>
internal static class IvDds
{
    // DDS_HEADER field offsets, counting the 4-byte 'DDS ' magic.
    private const int OffMipCount = 28;
    private const int OffFourCc   = 84;

    internal sealed record Info(int MipMaps, string PixelFormat);

    /// <summary>Read mip count + pixel format from a .dds on disk. Returns a
    /// conservative DXT1/1-mip guess when the file is too short or isn't a
    /// DDS at all, so a malformed texture degrades to a wrong-looking prop
    /// rather than a crashed builder.</summary>
    internal static Info Read(string path)
    {
        byte[] head;
        try
        {
            using var fs = File.OpenRead(path);
            head = new byte[128];
            int n = fs.Read(head, 0, head.Length);
            if (n < 128) return new Info(1, "DXT1");
        }
        catch
        {
            return new Info(1, "DXT1");
        }

        if (head[0] != (byte)'D' || head[1] != (byte)'D' || head[2] != (byte)'S' || head[3] != (byte)' ')
            return new Info(1, "DXT1");

        int mips = BitConverter.ToInt32(head, OffMipCount);
        if (mips < 1) mips = 1;

        var fourCc = System.Text.Encoding.ASCII.GetString(head, OffFourCc, 4);
        // ResourceBuilder's pixel-format vocabulary is the legacy D3D FourCC
        // set. Anything outside it (BC5/ATI2 normal maps written as DX10) has
        // no IV equivalent, so fall back to DXT5 which at least has the same
        // block size as BC5 and keeps the data walk in step.
        var fmt = fourCc switch
        {
            "DXT1" => "DXT1",
            "DXT3" => "DXT3",
            "DXT5" => "DXT5",
            "ATI2" => "DXT5",
            "BC5U" => "DXT5",
            _      => "DXT5",
        };
        return new Info(mips, fmt);
    }

    // DDS_HEADER dimensions, again counting the magic.
    private const int OffHeight = 12;
    private const int OffWidth  = 16;
    private const int HeaderLen = 128;

    /// <summary>True when the texture's top mip is fully opaque.
    ///
    /// Needed because the upstream bake flags "has alpha" from the PNG's
    /// colour type alone — any 32-bit PNG counts, even one whose alpha channel
    /// is solid white. Trusting that flag puts ordinary opaque props into GTA
    /// IV's alpha draw bucket, where they lose depth-write and sort against
    /// glass. Reading the encoded blocks answers the real question.
    ///
    /// Returns true on anything we can't parse: the alpha bucket is the
    /// costlier, visually riskier choice, so an unreadable texture should not
    /// be pushed into it on a guess.</summary>
    internal static bool IsFullyOpaque(string path)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); } catch { return true; }
        if (data.Length <= HeaderLen) return true;

        var info = Read(path);
        // BC1 as we encode it carries no alpha channel at all.
        if (info.PixelFormat == "DXT1") return true;

        int h = BitConverter.ToInt32(data, OffHeight);
        int w = BitConverter.ToInt32(data, OffWidth);
        if (w <= 0 || h <= 0) return true;

        int blocks = ((w + 3) / 4) * ((h + 3) / 4);
        const int BlockBytes = 16;   // both DXT3 and DXT5 are 16 bytes per block
        if ((long)HeaderLen + (long)blocks * BlockBytes > data.Length) return true;

        for (int i = 0; i < blocks; i++)
        {
            int o = HeaderLen + i * BlockBytes;
            if (info.PixelFormat == "DXT5")
            {
                // BC3 alpha block: two endpoints then 3-bit indices. Both
                // endpoints at 255 means every texel in the block is opaque,
                // whichever index is selected.
                if (data[o] != 0xFF || data[o + 1] != 0xFF) return false;
            }
            else
            {
                // DXT3: 8 bytes of explicit 4-bit alpha; opaque = all nibbles F.
                for (int k = 0; k < 8; k++)
                    if (data[o + k] != 0xFF) return false;
            }
        }
        return true;
    }
}
