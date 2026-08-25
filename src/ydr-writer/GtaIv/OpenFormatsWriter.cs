// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Globalization;
using System.Numerics;
using System.Text;

namespace YdrWriter.GtaIv;

/// <summary>Serialises an <see cref="IvDrawable"/> to GTA IV OpenFormats —
/// the plain-text intermediate (.odr + .mesh, plus .otd for texture
/// dictionaries) that ResourceBuilder compiles into .wdr / .wtd.
///
/// Formatting here is not cosmetic. The layout below matches, byte for byte,
/// what ResourceBuilder itself writes when it decompiles a shipping .wdr —
/// which is how the format was pinned down. Two details are easy to get
/// wrong and both are hard failures:
///
///   1. The version header is "Version 110" with NO second number. GIMS IV
///      writes "Version 110 12" (a tool-internal revision); ResourceBuilder
///      does not, and we follow ResourceBuilder.
///   2. A .mesh vertex line ALWAYS carries all sixteen slash-separated
///      groups, whatever the shader declares. The builder reads the full
///      superset and then picks the groups the preset's vertex declaration
///      names. GIMS writes only ten groups for unskinned geometry — copying
///      GIMS there produces files ResourceBuilder rejects.
///
/// Line endings are CRLF and indentation is tabs, again matching the
/// reference writer.</summary>
internal static class OpenFormatsWriter
{
    private const string Nl = "\r\n";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Indices per line inside an Idx block — 5 triangles' worth,
    /// which is the grouping the reference writer uses.</summary>
    private const int IndicesPerLine = 15;

    private static string F(float v)
    {
        // Non-finite coordinates make it all the way into the resource and
        // crash the game rather than the builder, so clamp them here. Mirrors
        // the NaN/Inf guard on the GTA V export path.
        if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;
        return v.ToString("F8", Inv);
    }

    private static string F3(Vector3 v) => $"{F(v.X)} {F(v.Y)} {F(v.Z)}";

    /// <summary>Write the .odr and every non-empty .mesh tier.</summary>
    /// <param name="drawable">Model to serialise.</param>
    /// <param name="outDir">Directory that receives &lt;name&gt;.odr and the
    /// &lt;name&gt;\ subfolder holding the meshes and any embedded textures.</param>
    /// <returns>Full path of the written .odr.</returns>
    internal static string WriteDrawable(IvDrawable drawable, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var assetDir = Path.Combine(outDir, drawable.Name);
        Directory.CreateDirectory(assetDir);

        // --- meshes -----------------------------------------------------
        // One .mesh per populated tier. The name pattern matches what
        // ResourceBuilder emits when decompiling: <asset>_<tier>_<n>.mesh.
        var lodMeshFiles = new string?[4];
        for (int i = 0; i < 4; i++)
        {
            var lod = drawable.Lods[i];
            if (lod == null || lod.IsEmpty) continue;
            var fileName = $"{drawable.Name}_{IvDrawable.LodNames[i]}_0.mesh";
            File.WriteAllText(Path.Combine(assetDir, fileName), BuildMesh(lod), new UTF8Encoding(false));
            lodMeshFiles[i] = fileName;
        }

        // --- .odr --------------------------------------------------------
        var odrPath = Path.Combine(outDir, drawable.Name + ".odr");
        File.WriteAllText(odrPath, BuildOdr(drawable, lodMeshFiles), new UTF8Encoding(false));
        return odrPath;
    }

    internal static string BuildOdr(IvDrawable d, string?[] lodMeshFiles)
    {
        var sb = new StringBuilder();
        sb.Append("Version 110").Append(Nl);

        sb.Append("shadinggroup").Append(Nl).Append('{').Append(Nl);
        sb.Append('\t').Append("Shaders ").Append(d.Shaders.Count.ToString(Inv)).Append(Nl);
        sb.Append('\t').Append('{').Append(Nl);
        foreach (var s in d.Shaders)
            sb.Append("\t\t").Append(ShaderLine(s)).Append(Nl);
        sb.Append('\t').Append('}').Append(Nl);
        sb.Append('}').Append(Nl);

        // No skel block: a static prop has no skeleton, and shipping props
        // without one (e.g. level4_bank_lod.wdr) omit it entirely rather
        // than writing an empty or null skeleton.

        sb.Append("lodgroup").Append(Nl).Append('{').Append(Nl);
        for (int i = 0; i < 4; i++)
        {
            var file = lodMeshFiles[i];
            sb.Append('\t').Append(IvDrawable.LodNames[i]).Append(' ');
            if (file == null)
            {
                sb.Append("none");
            }
            else
            {
                // "<count> <path> <parentBoneIndex>" repeated per mesh. Static
                // props have no skeleton, so the parent index is always 0.
                sb.Append("1 ").Append(d.Name).Append('\\').Append(file).Append(" 0");
            }
            // Every tier carries a trailing draw distance, "none" included.
            sb.Append(' ').Append(F(d.LodDistances[i])).Append(Nl);
        }

        var (c, r, min, max) = d.Bounds();
        sb.Append('\t').Append("center ").Append(F3(c)).Append(Nl);
        sb.Append('\t').Append("AABBMin ").Append(F3(min)).Append(Nl);
        sb.Append('\t').Append("AABBMax ").Append(F3(max)).Append(Nl);
        sb.Append('\t').Append("radius ").Append(F(r)).Append(Nl);
        sb.Append('}').Append(Nl);
        return sb.ToString();
    }

    /// <summary>Render one shader's .odr line: the preset file name followed
    /// by one token per dynamic parameter, in ShaderManager.xml order.</summary>
    internal static string ShaderLine(IvShaderInstance s)
    {
        var sb = new StringBuilder(s.Preset.FileName);
        foreach (var p in s.Preset.Params)
        {
            sb.Append(' ');
            if (p.Kind == IvParamKind.Texture)
                sb.Append(s.Textures.TryGetValue(p.Name, out var tex) && !string.IsNullOrWhiteSpace(tex)
                    ? tex
                    : p.Default);
            else
                sb.Append(p.Default);
        }
        return sb.ToString();
    }

    internal static string BuildMesh(IvMesh mesh)
    {
        var prims = mesh.Primitives.Where(p => p.Indices.Count > 0 && p.Vertices.Count > 0).ToList();

        var sb = new StringBuilder();
        sb.Append("Version 11").Append(Nl);
        sb.Append('{').Append(Nl);
        sb.Append('\t').Append("Skinned 0").Append(Nl);

        // Bounds block. When the tier holds more than one material the first
        // entry is the tier-wide sphere and the per-material spheres follow —
        // hence a count of materials+1. A single-material tier writes just the
        // one sphere. Getting this count wrong desynchronises the builder's
        // read of the whole block.
        bool multi = prims.Count > 1;
        sb.Append('\t').Append("Bounds ").Append((multi ? prims.Count + 1 : prims.Count).ToString(Inv)).Append(Nl);
        sb.Append('\t').Append('{').Append(Nl);
        if (multi)
        {
            var (c, r, _, _) = mesh.Bounds();
            sb.Append("\t\t").Append(F3(c)).Append(' ').Append(F(r)).Append(Nl);
        }
        foreach (var p in prims)
            sb.Append("\t\t").Append(F3(p.Center)).Append(' ').Append(F(p.Radius)).Append(Nl);
        sb.Append('\t').Append('}').Append(Nl);

        foreach (var p in prims)
        {
            sb.Append('\t').Append("Mtl ").Append(p.ShaderIndex.ToString(Inv)).Append(Nl);
            sb.Append('\t').Append('{').Append(Nl);
            sb.Append("\t\t").Append("Prim 0").Append(Nl);
            sb.Append("\t\t").Append('{').Append(Nl);

            sb.Append("\t\t\t").Append("Idx ").Append(p.Indices.Count.ToString(Inv)).Append(Nl);
            sb.Append("\t\t\t").Append('{').Append(Nl);
            for (int i = 0; i < p.Indices.Count; i += IndicesPerLine)
            {
                sb.Append("\t\t\t\t");
                int end = Math.Min(i + IndicesPerLine, p.Indices.Count);
                for (int j = i; j < end; j++)
                {
                    if (j > i) sb.Append(' ');
                    sb.Append(p.Indices[j].ToString(Inv));
                }
                sb.Append(Nl);
            }
            sb.Append("\t\t\t").Append('}').Append(Nl);

            sb.Append("\t\t\t").Append("Verts ").Append(p.Vertices.Count.ToString(Inv)).Append(Nl);
            sb.Append("\t\t\t").Append('{').Append(Nl);
            foreach (var v in p.Vertices)
                sb.Append("\t\t\t\t").Append(VertexLine(v)).Append(Nl);
            sb.Append("\t\t\t").Append('}').Append(Nl);

            sb.Append("\t\t").Append('}').Append(Nl);
            sb.Append('\t').Append('}').Append(Nl);
        }

        sb.Append('}').Append(Nl);
        return sb.ToString();
    }

    /// <summary>The sixteen-group vertex line.
    ///
    /// Group order is RAGE's vertex-usage enum, which is also the order
    /// ShaderManager.xml declares elements in:
    ///   position, blendWeight, blendIndex, normal, colour, specular,
    ///   texcoord0..texcoord7, tangent, binormal.
    /// Groups this prop doesn't use are zero-filled rather than omitted.</summary>
    internal static string VertexLine(in IvVertex v)
    {
        var sb = new StringBuilder(320);
        sb.Append(F3(v.Position));
        // blendWeight / blendIndex — unused by static props.
        sb.Append(" / 0.00000000 0.00000000 0.00000000 0.00000000");
        sb.Append(" / 0 0 0 0");
        sb.Append(" / ").Append(F3(v.Normal));
        // d3dcolor is a packed BGRA byte quad; the reference writer emits it
        // in that memory order, so a vertex-coloured source needs B,G,R,A
        // here and not R,G,B,A. Defaults are white, where the distinction
        // doesn't bite.
        sb.Append(" / ").Append(v.B).Append(' ').Append(v.G).Append(' ').Append(v.R).Append(' ').Append(v.A);
        // specular — unused.
        sb.Append(" / 0 0 0 0");
        sb.Append(" / ").Append(F(v.Uv0.X)).Append(' ').Append(F(v.Uv0.Y));
        for (int i = 1; i < 8; i++) sb.Append(" / 0.00000000 0.00000000");
        sb.Append(" / ").Append(F(v.Tangent.X)).Append(' ').Append(F(v.Tangent.Y))
          .Append(' ').Append(F(v.Tangent.Z)).Append(' ').Append(F(v.Tangent.W));
        // binormal — RAGE derives it from normal x tangent.
        sb.Append(" / 0.00000000 0.00000000 0.00000000");
        return sb.ToString();
    }

    /// <summary>Write an .otd texture dictionary listing every .dds in
    /// <paramref name="texDir"/>. Compiles to a .wtd, for props that share
    /// textures with other assets instead of embedding their own.</summary>
    /// <param name="dictName">Dictionary name; also the folder the .otd
    /// references its textures through.</param>
    /// <param name="outDir">Directory receiving &lt;dictName&gt;.otd.</param>
    /// <param name="texDir">Directory holding the .dds files. Must be
    /// &lt;outDir&gt;\&lt;dictName&gt;\texotd for the relative paths to resolve.</param>
    internal static string WriteTextureDictionary(string dictName, string outDir, string texDir)
    {
        var files = Directory.Exists(texDir)
            ? Directory.GetFiles(texDir, "*.dds").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

        var sb = new StringBuilder();
        sb.Append("Version 8").Append(Nl);
        sb.Append('{').Append(Nl);
        foreach (var f in files)
        {
            var info = IvDds.Read(f);
            sb.Append('\t').Append("grcTexture").Append(Nl);
            sb.Append('\t').Append('{').Append(Nl);
            sb.Append("\t\t").Append("Type regular").Append(Nl);
            sb.Append("\t\t").Append("Name ").Append(dictName).Append("\\texotd\\").Append(Path.GetFileName(f)).Append(Nl);
            sb.Append("\t\t").Append("MipMaps ").Append(info.MipMaps.ToString(Inv)).Append(Nl);
            sb.Append("\t\t").Append("PixelFormat ").Append(info.PixelFormat).Append(Nl);
            sb.Append('\t').Append('}').Append(Nl);
        }
        sb.Append('}').Append(Nl);

        var path = Path.Combine(outDir, dictName + ".otd");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }
}
