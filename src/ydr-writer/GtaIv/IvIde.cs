// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Globalization;
using System.Text;

namespace YdrWriter.GtaIv;

/// <summary>GTA IV object-definition file (.ide) — plain text, unlike the
/// binary .wpl beside it.
///
/// A .wpl only ever stores a Jenkins hash of the model name, so without the
/// .ide that names the archetype there is no way to tell what a placement
/// refers to. The two are read and written as a pair.
///
/// Sections are keyword-delimited (<c>objs</c> … <c>end</c>), fields are comma
/// separated. The one that matters for props:
///
///   name, txdName, drawDistance, flags, ?, bbMinX,Y,Z, bbMaxX,Y,Z,
///   centreX,Y,Z, radius, wddName
///
/// Only <c>objs</c> is modelled. Other sections (<c>tobj</c>, <c>anim</c>,
/// <c>2dfx</c> …) are carried through verbatim so a load/save round trip keeps
/// them intact.</summary>
internal sealed class IvIde
{
    internal sealed class ObjectDef
    {
        public string Name = "";
        /// <summary>Texture dictionary, or "null" when textures are embedded
        /// in the .wdr — which is what FiveOS exports produce.</summary>
        public string TxdName = "null";
        public float DrawDistance = 100f;
        public uint Flags;
        public int Unknown;
        public (float X, float Y, float Z) BoundsMin;
        public (float X, float Y, float Z) BoundsMax;
        public (float X, float Y, float Z) Centre;
        public float Radius;
        public string WddName = "null";
    }

    public List<ObjectDef> Objects { get; } = new();

    /// <summary>Whole non-objs sections, kept in file order.</summary>
    private readonly List<string> _passthrough = new();

    internal static IvIde Load(string path)
    {
        var ide = new IvIde();
        var lines = File.ReadAllLines(path);
        var inv = CultureInfo.InvariantCulture;

        string? section = null;
        var carry = new StringBuilder();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (section == null)
            {
                section = line;
                if (!section.Equals("objs", StringComparison.OrdinalIgnoreCase))
                    carry.AppendLine(line);
                continue;
            }

            if (line.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                if (!section.Equals("objs", StringComparison.OrdinalIgnoreCase))
                {
                    carry.AppendLine("end");
                    ide._passthrough.Add(carry.ToString());
                    carry.Clear();
                }
                section = null;
                continue;
            }

            if (!section.Equals("objs", StringComparison.OrdinalIgnoreCase)) { carry.AppendLine(line); continue; }

            var f = line.Split(',', StringSplitOptions.TrimEntries);
            if (f.Length < 15) continue;

            static float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
            ide.Objects.Add(new ObjectDef
            {
                Name = f[0],
                TxdName = f[1],
                DrawDistance = F(f[2]),
                Flags = uint.TryParse(f[3], out var fl) ? fl : 0u,
                Unknown = int.TryParse(f[4], out var u) ? u : 0,
                BoundsMin = (F(f[5]), F(f[6]), F(f[7])),
                BoundsMax = (F(f[8]), F(f[9]), F(f[10])),
                Centre = (F(f[11]), F(f[12]), F(f[13])),
                Radius = F(f[14]),
                WddName = f.Length > 15 ? f[15] : "null",
            });
        }
        return ide;
    }

    internal void Save(string path)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.AppendLine("objs");
        foreach (var o in Objects)
        {
            sb.Append(o.Name).Append(", ")
              .Append(o.TxdName).Append(", ")
              .Append(o.DrawDistance.ToString("0.###", inv)).Append(", ")
              .Append(o.Flags).Append(", ")
              .Append(o.Unknown).Append(", ")
              .Append(V(o.BoundsMin)).Append(", ")
              .Append(V(o.BoundsMax)).Append(", ")
              .Append(V(o.Centre)).Append(", ")
              .Append(o.Radius.ToString("0.#####", inv)).Append(", ")
              .Append(o.WddName)
              .AppendLine();
        }
        sb.AppendLine("end");

        foreach (var block in _passthrough) sb.Append(block);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        string V((float X, float Y, float Z) v) => $"{N(v.X)}, {N(v.Y)}, {N(v.Z)}";

        // A centre component is a midpoint, so it routinely lands a hair either
        // side of zero — -1.2e-8 rounds to "-0" at five decimals. Every parser
        // worth the name reads that as zero, but it is noise in a file people
        // read and hand-edit. Normalise after formatting rather than before:
        // the sign survives the rounding, not the comparison.
        string N(float f)
        {
            var s = f.ToString("0.#####", inv);
            return s == "-0" ? "0" : s;
        }
    }
}
