// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FiveOS.Services.Sims;

/// <summary>
/// The RCOL container that MODL / MLOD / GEOM resources are wrapped in: a
/// table of internal chunks plus internal/external TGI reference lists.
/// Mesh groups point at their vertex format and buffers by *chunk reference*,
/// so nothing can be decoded without resolving those first.
/// <para>Layout is documented in <c>docs/research/sims-package-formats.md</c>.</para>
/// </summary>
public sealed class SimsRcol
{
    public uint Version { get; private init; }
    public int PublicChunkCount { get; private init; }
    public IReadOnlyList<Chunk> Chunks { get; private init; } = Array.Empty<Chunk>();

    public readonly record struct Chunk(uint Type, uint Group, ulong Instance, int Offset, int Size)
    {
        /// <summary>Four-character tag at the head of the chunk payload
        /// ('VRTF', 'VBUF', …) — the reliable way to identify a chunk, since
        /// the buffer type IDs aren't consistently documented.</summary>
        public string TagIn(byte[] data) =>
            Offset + 4 <= data.Length ? Encoding.ASCII.GetString(data, Offset, 4) : "";
    }

    /// <summary>Parse an RCOL. Resources that are NOT RCOL-wrapped (some GEOM
    /// variants start straight at their own magic) return null so the caller
    /// can fall back to decoding the buffer directly.</summary>
    public static SimsRcol? TryParse(byte[] data)
    {
        if (data.Length < 20) return null;
        // A bare mesh resource begins with its own tag, not an RCOL header.
        var head = Encoding.ASCII.GetString(data, 0, 4);
        if (head is "GEOM" or "MLOD" or "MODL") return null;

        try
        {
            var p = 0;
            var version = ReadU32(data, ref p);
            var publicCount = (int)ReadU32(data, ref p);
            _ = ReadU32(data, ref p);                    // index3, unused
            var externalCount = (int)ReadU32(data, ref p);
            var internalCount = (int)ReadU32(data, ref p);

            if (internalCount < 0 || internalCount > 4096 ||
                externalCount < 0 || externalCount > 4096) return null;

            var internalKeys = new (uint Type, uint Group, ulong Instance)[internalCount];
            for (var i = 0; i < internalCount; i++)
            {
                if (p + 16 > data.Length) return null;
                var instance = ReadU64(data, ref p);
                var type = ReadU32(data, ref p);
                var group = ReadU32(data, ref p);
                internalKeys[i] = (type, group, instance);
            }
            // External references are resolved against the package, not here —
            // skipped so the chunk table lands at the right offset.
            p += externalCount * 16;

            var chunks = new List<Chunk>(internalCount);
            for (var i = 0; i < internalCount; i++)
            {
                if (p + 8 > data.Length) return null;
                var offset = (int)ReadU32(data, ref p);
                var size = (int)ReadU32(data, ref p);
                if (offset < 0 || size < 0 || (long)offset + size > data.Length) return null;
                var k = internalKeys[i];
                chunks.Add(new Chunk(k.Type, k.Group, k.Instance, offset, size));
            }

            return new SimsRcol
            {
                Version = version,
                PublicChunkCount = publicCount,
                Chunks = chunks,
            };
        }
        catch (ArgumentOutOfRangeException) { return null; }
        catch (IndexOutOfRangeException) { return null; }
    }

    /// <summary>Resolve a mesh-group chunk reference to an index into
    /// <see cref="Chunks"/>. References are 1-based with a flag nibble on top:
    /// 0 = public internal, 1 = private internal (offset by the public count).
    /// Returns -1 for a null or out-of-range reference.</summary>
    public int ResolveChunkIndex(uint reference)
    {
        var flag = reference >> 28;
        var idx = (int)(reference & 0x0FFFFFFF);
        if (idx == 0) return -1;
        idx -= 1;
        if (flag == 1) idx += PublicChunkCount;
        return idx >= 0 && idx < Chunks.Count ? idx : -1;
    }

    /// <summary>First chunk whose payload starts with <paramref name="tag"/>.</summary>
    public int FindChunkByTag(byte[] data, string tag)
    {
        for (var i = 0; i < Chunks.Count; i++)
            if (Chunks[i].TagIn(data) == tag) return i;
        return -1;
    }

    /// <summary>The chunk at <paramref name="index"/>, but only if its payload
    /// carries the expected tag — guards against a mis-resolved reference
    /// silently decoding the wrong buffer as vertices.</summary>
    public Chunk? ChunkWithTag(byte[] data, int index, string tag)
    {
        if (index < 0 || index >= Chunks.Count) return null;
        var c = Chunks[index];
        return c.TagIn(data) == tag ? c : null;
    }

    private static uint ReadU32(byte[] d, ref int p)
    {
        var v = BitConverter.ToUInt32(d, p);
        p += 4;
        return v;
    }

    private static ulong ReadU64(byte[] d, ref int p)
    {
        var v = BitConverter.ToUInt64(d, p);
        p += 8;
        return v;
    }
}
