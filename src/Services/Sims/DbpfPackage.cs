// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FiveOS.Services.Sims;

/// <summary>Sims 4 DBPF (v2.x) reader. Originally CLIP-only for the emote
/// importer; now also the front door for meshes and textures, which means the
/// index has to be parsed properly rather than assumed.
/// See <c>docs/research/sims-package-formats.md</c>.</summary>
public sealed class DbpfPackage : IDisposable
{
    // Animation (already shipping).
    public const uint TypeClip = 0x6B20C4F3;
    public const uint TypeClipHeader = 0xBC4A5044;
    // Geometry.
    public const uint TypeModl = 0x01661233;   // object model
    public const uint TypeMlod = 0x01D10F34;   // object model LODs
    public const uint TypeGeom = 0x015A1849;   // CAS part geometry
    public const uint TypeRig = 0x8EAF13DE;
    // Textures. Img is already a plain DDS — exactly what .ytd wants.
    public const uint TypeImg = 0x00B2D882;
    public const uint TypeImgRle = 0x3453CF95;   // DXT5 RLE
    public const uint TypeImgRles = 0xBA856C78;  // DXT5 RLES
    public const uint TypeImgOverlay = 0xB6C8B6A0;
    // Catalog.
    public const uint TypeObjd = 0xC0DB5AE7;
    public const uint TypeStbl = 0x220557DA;

    private const ushort CompressionNone = 0x0000;
    private const ushort CompressionStreamable = 0xFFFF;
    private const ushort CompressionZlib = 0x5A42;
    private const ushort CompressionRefPack = 0xFFFE;

    private readonly byte[] _data;
    private readonly List<IndexEntry> _entries;

    private DbpfPackage(byte[] data, List<IndexEntry> entries)
    {
        _data = data;
        _entries = entries;
    }

    public IReadOnlyList<IndexEntry> Entries => _entries;

    public static DbpfPackage Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Package not found.", path);

        var data = File.ReadAllBytes(path);
        if (data.Length < 96 || Encoding.ASCII.GetString(data, 0, 4) != "DBPF")
            throw new InvalidDataException("Not a Sims DBPF package.");

        var major = BitConverter.ToUInt32(data, 4);
        var minor = BitConverter.ToUInt32(data, 8);
        if (major != 2)
            throw new InvalidDataException($"Unsupported DBPF version {major}.{minor} (need 2.x).");

        var indexCount = BitConverter.ToUInt32(data, 36);
        var indexPos = BitConverter.ToUInt32(data, 64);
        if (indexPos + 4 > data.Length)
            throw new InvalidDataException("Corrupt DBPF index.");

        var pos = (int)indexPos;

        // The index opens with a constant-field bitmask: a set bit means that
        // field is stored ONCE here and omitted from every entry. Game-shipped
        // packages lean on this heavily; CC usually doesn't, which is why a
        // reader that skipped the mask still worked on hand-made content.
        var flags = BitConverter.ToUInt32(data, pos);
        pos += 4;
        uint? constType = null, constGroup = null, constInstanceHi = null;
        if ((flags & 0x1) != 0) { constType = ReadU32(data, ref pos); }
        if ((flags & 0x2) != 0) { constGroup = ReadU32(data, ref pos); }
        if ((flags & 0x4) != 0) { constInstanceHi = ReadU32(data, ref pos); }

        var entries = new List<IndexEntry>((int)indexCount);
        for (var i = 0; i < indexCount; i++)
        {
            // 28 bytes, or 32 when the size field flags the extended pair.
            // Fixed-stride reading desyncs on any package that mixes the two.
            if (pos + 16 > data.Length) break;
            var type = constType ?? ReadU32(data, ref pos);
            var group = constGroup ?? ReadU32(data, ref pos);
            var instanceHi = constInstanceHi ?? ReadU32(data, ref pos);
            var instanceLo = ReadU32(data, ref pos);
            var offset = ReadU32(data, ref pos);
            var sizeField = ReadU32(data, ref pos);
            var memSize = ReadU32(data, ref pos);

            ushort compression = CompressionNone;
            if ((sizeField & 0x80000000u) != 0)
            {
                if (pos + 4 > data.Length) break;
                compression = BitConverter.ToUInt16(data, pos);
                pos += 4;   // compressionType + committed
            }

            // High DWORD first — the Sims 4 locale byte lands in the top byte
            // of an STBL instance only with this order, which is how it was
            // confirmed. Matters for any cross-resource TGI reference.
            entries.Add(new IndexEntry(
                type, group, ((ulong)instanceHi << 32) | instanceLo,
                offset, sizeField, memSize, compression));
        }

        return new DbpfPackage(data, entries);
    }

    private static uint ReadU32(byte[] data, ref int pos)
    {
        var v = BitConverter.ToUInt32(data, pos);
        pos += 4;
        return v;
    }

    public byte[] ReadResource(IndexEntry entry)
    {
        var stored = (int)(entry.SizeField & 0x7FFFFFFFu);
        if (entry.Offset + (long)stored > _data.Length)
            throw new InvalidDataException("Resource extends past end of package.");

        var chunk = new byte[stored];
        Buffer.BlockCopy(_data, (int)entry.Offset, chunk, 0, stored);

        switch (entry.Compression)
        {
            case CompressionNone:
            case CompressionStreamable:
                return chunk;
            case CompressionZlib:
                break;
            case CompressionRefPack:
                throw new NotSupportedException(
                    "This package uses RefPack (QFS) compression, which FiveOS can't read yet.");
            default:
                // Unknown type but the entry claimed compression — a raw zlib
                // stream starts 0x78, so sniff before giving up.
                if (stored < 2 || chunk[0] != 0x78)
                    throw new NotSupportedException(
                        $"Unknown DBPF compression 0x{entry.Compression:X4}.");
                break;
        }

        using var ms = new MemoryStream(chunk);
        using var zs = new ZLibStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream(entry.MemSize > 0 ? (int)(entry.MemSize & 0x7FFFFFFFu) : stored * 2);
        zs.CopyTo(outMs);
        return outMs.ToArray();
    }

    /// <summary>Every entry of a given resource type, in package order.</summary>
    public IEnumerable<IndexEntry> EnumerateByType(uint type)
    {
        foreach (var e in _entries)
            if (e.Type == type) yield return e;
    }

    /// <summary>Resource-type histogram — what a package actually contains.
    /// Drives the "this is a pose pack, not an object" style of message, which
    /// is the difference between a useful error and "conversion failed".</summary>
    public IReadOnlyDictionary<uint, int> TypeHistogram()
    {
        var hist = new Dictionary<uint, int>();
        foreach (var e in _entries)
            hist[e.Type] = hist.TryGetValue(e.Type, out var n) ? n + 1 : 1;
        return hist;
    }

    public static string DescribeType(uint type) => type switch
    {
        TypeClip => "CLIP animation",
        TypeClipHeader => "CLIP header",
        TypeModl => "MODL object model",
        TypeMlod => "MLOD object model LODs",
        TypeGeom => "GEOM CAS mesh",
        TypeRig => "RIG skeleton",
        TypeImg => "DDS texture",
        TypeImgRle => "DDS texture (DXT5 RLE)",
        TypeImgRles => "DDS texture (DXT5 RLES)",
        TypeImgOverlay => "DDS overlay texture",
        TypeObjd => "object definition",
        TypeStbl => "string table",
        _ => $"0x{type:X8}",
    };

    public IEnumerable<(IndexEntry Clip, IndexEntry? Header)> EnumerateClips()
    {
        var headers = new Dictionary<ulong, IndexEntry>();
        foreach (var e in _entries)
        {
            if (e.Type == TypeClipHeader)
                headers[e.Instance] = e;
        }

        foreach (var e in _entries)
        {
            if (e.Type != TypeClip) continue;
            headers.TryGetValue(e.Instance, out var hdr);
            yield return (e, hdr);
        }
    }

    public void Dispose() { }

    public readonly record struct IndexEntry(
        uint Type, uint Group, ulong Instance, uint Offset, uint SizeField, uint MemSize,
        ushort Compression = 0);
}
