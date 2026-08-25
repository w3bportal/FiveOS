// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

namespace YdrWriter.GtaIv;

/// <summary>Guarantees every texture sampler a GTA IV shader declares actually
/// has a texture behind it.
///
/// ⚠ THIS PREVENTS A GAME CRASH, not a cosmetic problem.
///
/// ShaderManager.xml marks a sampler like <c>texturesampler</c> as a parameter
/// with NO static value — the shader has no default and the .odr must supply
/// one. Writing "null" there compiles without complaint and produces a drawable
/// that loads, streams, and returns a valid handle from CREATE_OBJECT. It then
/// kills the game the moment the object enters draw range, because the renderer
/// binds the sampler and dereferences a pointer that was never written:
///
///     0xC0000005 ACCESS VIOLATION at GTAIV.exe+0xD6812, reading 0x020AAE80
///
/// A GTA V drawable reaches this state easily: map props routinely leave their
/// textures in a shared .ytd rather than embedding them, so the converted model
/// knows the texture's NAME but has no pixels for it. Every one of a converted
/// palm's six shaders came out "null" that way.
///
/// Binding a visible placeholder is the right answer rather than silently
/// dropping the geometry: the prop renders, obviously untextured, and the export
/// log names the textures the user needs to supply.</summary>
internal static class IvPlaceholderTexture
{
    /// <summary>Base name of the generated texture, without extension.</summary>
    internal const string Name = "fiveos_notex";

    /// <summary>A 4x4 opaque white DXT1 image: a 128-byte DDS header plus one
    /// 8-byte block. The smallest thing a sampler will accept, and the same
    /// shape as the 4x4 DXT1 textures Rockstar's own props carry for flat
    /// colours.</summary>
    private static byte[] BuildDds()
    {
        var dds = new byte[128 + 8];
        var w = new BinaryWriter(new MemoryStream(dds));

        w.Write(0x20534444);            // "DDS "
        w.Write(124);                   // header size
        // CAPS | HEIGHT | WIDTH | PIXELFORMAT | LINEARSIZE
        w.Write(0x1 | 0x2 | 0x4 | 0x1000 | 0x80000);
        w.Write(4);                     // height
        w.Write(4);                     // width
        w.Write(8);                     // linear size: one DXT1 block
        w.Write(0);                     // depth
        w.Write(1);                     // mip count
        for (int i = 0; i < 11; i++) w.Write(0);   // reserved

        w.Write(32);                    // pixel format size
        w.Write(0x4);                   // DDPF_FOURCC
        w.Write(0x31545844);            // "DXT1"
        w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);

        w.Write(0x1000);                // DDSCAPS_TEXTURE
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);

        // One DXT1 block, both endpoints white, every texel index 0.
        w.BaseStream.Position = 128;
        w.Write((ushort)0xFFFF);
        w.Write((ushort)0xFFFF);
        w.Write(0u);
        w.Flush();
        return dds;
    }

    /// <summary>Bind a placeholder into every texture sampler that a shader
    /// declares but nothing filled, writing the .dds into <paramref name="assetDir"/>
    /// the first time one is needed.</summary>
    /// <returns>How many samplers had to be filled.</returns>
    internal static int FillUnboundSamplers(
        IvDrawable drawable, string assetDir, string assetName, List<string> notes)
    {
        int filled = 0;
        bool written = false;

        foreach (var shader in drawable.Shaders)
        {
            foreach (var p in shader.Preset.Params)
            {
                if (p.Kind != IvParamKind.Texture) continue;
                if (shader.Textures.TryGetValue(p.Name, out var bound)
                    && !string.IsNullOrWhiteSpace(bound)) continue;

                if (!written)
                {
                    try
                    {
                        Directory.CreateDirectory(assetDir);
                        File.WriteAllBytes(Path.Combine(assetDir, Name + ".dds"), BuildDds());
                        written = true;
                    }
                    catch (Exception ex)
                    {
                        notes.Add($"could not write the placeholder texture: {ex.Message} - "
                                + "the prop would crash the game on draw, so this must be fixed");
                        return filled;
                    }
                }

                shader.Textures[p.Name] = $"{assetName}\\{Name}.dds";
                filled++;
            }
        }

        if (filled > 0)
        {
            notes.Add($"{filled} shader texture slot(s) had no texture and were bound to a blank "
                    + $"placeholder ({Name}.dds). GTA IV crashes on render if a sampler is left "
                    + "empty, so this is not optional - supply the real textures to fix the look");
        }
        return filled;
    }
}
