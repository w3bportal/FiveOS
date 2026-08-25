// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Numerics;
using System.Text;

namespace YdrWriter.GtaIv;

/// <summary>GTA IV placement file (.wpl) — the IV equivalent of the classic
/// .ipl and of GTA V's .ymap.
///
/// Unlike .wdr/.wbn this is NOT an RSC resource: it's a plain little-endian
/// binary file, which makes it far simpler to work with.
///
///   u32      version                (3 in shipping files)
///   u32[16]  per-section record counts
///   ...      each non-empty section's records, in section order
///
/// ⚠ The count table is stored with the 'blok' and 'sect5' slots SWAPPED
/// relative to the order the sections actually appear — GIMS calls this out as
/// "move blok section" and it is not guessable. Both load and save go through
/// <see cref="SwapBlok"/> so the quirk lives in exactly one place.
///
/// Section record layouts (verified: 68-byte header + 855x48 objs + 9x44 tcyc
/// + 25x132 blok = 44,804 bytes = the exact size of Rockstar's manhat01.wpl):
///
///   idx name    size  contents
///    0  objs     48   7 float (position xyz, rotation xyzw) + u32 name hash
///                     + u32 + s32 + u32 + float
///    2  garage   48    5  tcyc    44    8  mlo     64
///    3  cars     56    9  lodm   388   10  slow    24
///   15  blok    132
///
/// Only 'objs' is decoded — that is the section that places props, and the one
/// worth editing. Every other section is carried through byte for byte, so a
/// load/save round trip is lossless even for sections nothing here understands.</summary>
internal sealed class IvWpl
{
    internal const int SectionCount = 16;

    /// <summary>Section names in the order their records appear in the file.</summary>
    internal static readonly string[] SectionNames =
    {
        "objs", "sect1", "garage", "cars", "tcyc", "blok", "sect6", "sect7",
        "mlo", "lodm", "slow", "sect11", "sect12", "sect13", "sect14", "sect5",
    };

    /// <summary>Bytes per record, by section index. Zero means "unknown", and
    /// an unknown section with a non-zero count makes the file unreadable
    /// rather than silently mis-parsed.</summary>
    private static readonly int[] RecordSize =
    {
        48, 0, 48, 56, 44, 132, 0, 0, 64, 388, 24, 0, 0, 0, 0, 0,
    };

    /// <summary>One placed object.</summary>
    internal sealed class Instance
    {
        public Vector3 Position;
        /// <summary>Rotation as stored: x, y, z, w.</summary>
        public Vector4 Rotation = new(0, 0, 0, 1);
        /// <summary>Jenkins hash of the model name — .wpl never stores the
        /// name itself, which is why an .ide is needed to make sense of it.</summary>
        public uint NameHash;
        public uint Unknown1;
        public int Unknown2;
        public uint Unknown3;
        public float Unknown4;
    }

    public uint Version { get; set; } = 3;
    public List<Instance> Objects { get; } = new();

    /// <summary>Raw bytes of every section other than 'objs', kept verbatim so
    /// a round trip never loses data we don't model.</summary>
    private readonly byte[]?[] _passthrough = new byte[]?[SectionCount];
    private readonly uint[] _counts = new uint[SectionCount];

    /// <summary>The file stores 'blok' and 'sect5' counts in swapped slots.
    /// Applying the same swap on read and write keeps everything else honest.</summary>
    private static void SwapBlok(uint[] counts)
        => (counts[5], counts[15]) = (counts[15], counts[5]);

    internal static IvWpl Load(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 4 + SectionCount * 4)
            throw new InvalidDataException("not a .wpl: file is shorter than its header");

        var wpl = new IvWpl { Version = BitConverter.ToUInt32(data, 0) };
        var counts = new uint[SectionCount];
        for (int i = 0; i < SectionCount; i++) counts[i] = BitConverter.ToUInt32(data, 4 + i * 4);
        SwapBlok(counts);

        int offset = 4 + SectionCount * 4;
        for (int i = 0; i < SectionCount; i++)
        {
            uint n = counts[i];
            wpl._counts[i] = n;
            if (n == 0) continue;

            int size = RecordSize[i];
            if (size == 0)
                throw new InvalidDataException(
                    $"section '{SectionNames[i]}' has {n} record(s) but its layout is unknown — refusing to guess");

            long span = (long)n * size;
            if (offset + span > data.Length)
                throw new InvalidDataException($"section '{SectionNames[i]}' runs past the end of the file");

            if (i == 0)
            {
                for (uint r = 0; r < n; r++)
                {
                    int o = offset + (int)r * size;
                    wpl.Objects.Add(new Instance
                    {
                        Position = new Vector3(
                            BitConverter.ToSingle(data, o + 0),
                            BitConverter.ToSingle(data, o + 4),
                            BitConverter.ToSingle(data, o + 8)),
                        Rotation = new Vector4(
                            BitConverter.ToSingle(data, o + 12),
                            BitConverter.ToSingle(data, o + 16),
                            BitConverter.ToSingle(data, o + 20),
                            BitConverter.ToSingle(data, o + 24)),
                        NameHash = BitConverter.ToUInt32(data, o + 28),
                        Unknown1 = BitConverter.ToUInt32(data, o + 32),
                        Unknown2 = BitConverter.ToInt32(data, o + 36),
                        Unknown3 = BitConverter.ToUInt32(data, o + 40),
                        Unknown4 = BitConverter.ToSingle(data, o + 44),
                    });
                }
            }
            else
            {
                wpl._passthrough[i] = data.AsSpan(offset, (int)span).ToArray();
            }
            offset += (int)span;
        }
        return wpl;
    }

    internal void Save(string path)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        var counts = new uint[SectionCount];
        for (int i = 0; i < SectionCount; i++)
        {
            counts[i] = i == 0
                ? (uint)Objects.Count
                : (_passthrough[i] != null && RecordSize[i] > 0
                    ? (uint)(_passthrough[i]!.Length / RecordSize[i])
                    : 0u);
        }

        w.Write(Version);
        // Write the counts back in the file's own swapped order.
        var stored = (uint[])counts.Clone();
        SwapBlok(stored);
        foreach (var c in stored) w.Write(c);

        foreach (var o in Objects)
        {
            w.Write(o.Position.X); w.Write(o.Position.Y); w.Write(o.Position.Z);
            w.Write(o.Rotation.X); w.Write(o.Rotation.Y); w.Write(o.Rotation.Z); w.Write(o.Rotation.W);
            w.Write(o.NameHash);
            w.Write(o.Unknown1);
            w.Write(o.Unknown2);
            w.Write(o.Unknown3);
            w.Write(o.Unknown4);
        }

        for (int i = 1; i < SectionCount; i++)
            if (_passthrough[i] is { Length: > 0 } raw) w.Write(raw);

        w.Flush();
        File.WriteAllBytes(path, ms.ToArray());
    }

    /// <summary>Record counts as loaded, for reporting.</summary>
    internal IEnumerable<(string Name, uint Count)> Sections()
    {
        for (int i = 0; i < SectionCount; i++)
            if (_counts[i] > 0) yield return (SectionNames[i], _counts[i]);
    }
}
