// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using CodeWalker.GameFiles;

namespace YdrWriter.GtaIv;

/// <summary>The <c>dump-vertex-decl</c> subcommand: print the real per-vertex
/// layout of every geometry in a GTA V drawable.
///
/// Exists because reading a vertex attribute through the declaration is only
/// safe when the declaration agrees with the buffer. When it does not — a
/// component flagged present that the stride has no room for — the typed
/// getters do not throw. They compute an offset past the end of the vertex and
/// quietly return bytes belonging to the NEXT one, so a tangent comes back as
/// the following vertex's position and a normal comes back as garbage with a
/// magnitude in the 1e21 range. Nothing about that failure is visible without
/// laying the offsets against the stride, which is what this prints.</summary>
internal static class VertexDeclCommand
{
    /// <summary>Byte width of each RAGE vertex component type.</summary>
    private static int SizeOf(VertexComponentType t) => t switch
    {
        VertexComponentType.Float => 4,
        VertexComponentType.Float2 => 8,
        VertexComponentType.Float3 => 12,
        VertexComponentType.Float4 => 16,
        VertexComponentType.UByte4 => 4,
        VertexComponentType.Colour => 4,
        VertexComponentType.Half2 => 4,
        VertexComponentType.Half4 => 8,
        _ => 0,
    };

    private static readonly string[] ComponentNames =
    {
        "position", "blendWeight", "blendIndex", "normal", "colour0", "colour1",
        "texcoord0", "texcoord1", "texcoord2", "texcoord3", "texcoord4",
        "texcoord5", "texcoord6", "texcoord7", "tangent0", "binormal0",
    };

    internal static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: ydr-writer dump-vertex-decl <file.ydr>");
            return 2;
        }
        if (!File.Exists(args[0])) { Console.Error.WriteLine($"not found: {args[0]}"); return 2; }

        var ydr = new YdrFile();
        ydr.Load(File.ReadAllBytes(args[0]));
        var drawable = ydr.Drawable;
        if (drawable == null) { Console.Error.WriteLine("no drawable"); return 3; }

        int problems = 0;
        var models = drawable.DrawableModels?.High;
        if (models == null) { Console.WriteLine("(no models)"); return 0; }

        for (int mi = 0; mi < models.Length; mi++)
        {
            var model = models[mi];
            var geoms = model?.Geometries;
            if (geoms == null) continue;

            for (int gi = 0; gi < geoms.Length; gi++)
            {
                var vd = geoms[gi]?.VertexData;
                var decl = vd?.Info;
                if (vd == null) continue;

                int stride = vd.VertexStride;
                Console.WriteLine($"\nmodel {mi} geometry {gi}   stride={stride}  verts={vd.VertexCount}");
                if (decl == null) { Console.WriteLine("  (no declaration)"); continue; }

                Console.WriteLine($"  flags=0x{(uint)decl.Flags:X8}  types=0x{(ulong)decl.Types:X16}");

                int offset = 0;
                for (int c = 0; c < 16; c++)
                {
                    // Presence comes from the flags bitmask, NOT from the type
                    // nibble: Types holds a 4-bit code for all sixteen slots
                    // whether or not the buffer carries them, so treating a
                    // non-zero type as "present" invents components that are
                    // not there.
                    bool present = (decl.Flags & (1u << c)) != 0;
                    var type = decl.GetComponentType(c);
                    int size = SizeOf(type);
                    if (!present) { if (size != 0) Console.WriteLine($"  [{c,2}] {ComponentNames[c],-12} {type,-9} (type set but flag clear - NOT in the buffer)"); continue; }
                    if (size == 0) continue;

                    bool fits = offset + size <= stride;
                    if (!fits) problems++;
                    Console.WriteLine(
                        $"  [{c,2}] {ComponentNames[c],-12} {type,-9} offset={offset,3} size={size,2}"
                        + (fits ? "" : $"   <-- RUNS PAST THE {stride}-BYTE VERTEX"));
                    offset += size;
                }

                if (offset != stride)
                    Console.WriteLine($"  declared components total {offset} bytes but the stride is {stride}");
            }
        }

        Console.WriteLine(problems == 0
            ? "\nevery declared component fits inside its vertex"
            : $"\n{problems} component(s) are declared past the end of their vertex - "
            + "reading those returns the NEXT vertex's bytes");
        return 0;
    }
}
