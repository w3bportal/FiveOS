// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FiveOS.Services.Sims;

/// <summary>Minimal Sims 4 DBPF (v2.1) reader for CLIP / ClipHeader resources.</summary>
public sealed class DbpfPackage : IDisposable
{
    public const uint TypeClip = 0x6B20C4F3;
    public const uint TypeClipHeader = 0xBC4A5044;

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

        // Sims 4 index: flags DWORD, then fixed 32-byte entries (type, group, instance64, offset, sizeFlags, memSize).
        var pos = (int)indexPos + 4;
        var entries = new List<IndexEntry>((int)indexCount);
        for (var i = 0; i < indexCount; i++)
        {
            if (pos + 32 > data.Length) break;
            var type = BitConverter.ToUInt32(data, pos);
            var group = BitConverter.ToUInt32(data, pos + 4);
            var instLo = BitConverter.ToUInt32(data, pos + 8);
            var instHi = BitConverter.ToUInt32(data, pos + 12);
            var offset = BitConverter.ToUInt32(data, pos + 16);
            var sizeField = BitConverter.ToUInt32(data, pos + 20);
            var memSize = BitConverter.ToUInt32(data, pos + 24);
            entries.Add(new IndexEntry(type, group, ((ulong)instHi << 32) | instLo, offset, sizeField, memSize));
            pos += 32;
        }

        return new DbpfPackage(data, entries);
    }

    public byte[] ReadResource(IndexEntry entry)
    {
        var compressed = (entry.SizeField & 0x80000000u) != 0;
        var stored = (int)(entry.SizeField & 0x7FFFFFFFu);
        if (entry.Offset + stored > _data.Length)
            throw new InvalidDataException("Resource extends past end of package.");

        var chunk = new byte[stored];
        Buffer.BlockCopy(_data, (int)entry.Offset, chunk, 0, stored);
        if (!compressed)
            return chunk;

        // Sims 4: raw zlib stream (CMF 0x78…).
        using var ms = new MemoryStream(chunk);
        using var zs = new ZLibStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream(entry.MemSize > 0 ? (int)(entry.MemSize & 0x7FFFFFFFu) : stored * 2);
        zs.CopyTo(outMs);
        return outMs.ToArray();
    }

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
        uint Type, uint Group, ulong Instance, uint Offset, uint SizeField, uint MemSize);
}
