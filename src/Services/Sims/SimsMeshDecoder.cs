// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace FiveOS.Services.Sims;

/// <summary>A decoded Sims mesh in engine-neutral form, ready to be written
/// out as glTF and handed to the normal prop pipeline.</summary>
public sealed class SimsMesh
{
    public string Name = "";
    public List<Vector3> Positions { get; } = new();
    public List<Vector3> Normals { get; } = new();
    public List<Vector2> Uvs { get; } = new();
    public List<int> Indices { get; } = new();

    /// <summary>Shadow-caster / drop-shadow groups. These are low-poly stand-ins
    /// the game uses to cast shadows, never drawn — converting them produces a
    /// duplicate blob sitting inside the real mesh.</summary>
    public bool ShadowOnly;

    public int TriangleCount => Indices.Count / 3;
    public bool IsUsable => Positions.Count > 0 && Indices.Count >= 3;
}

/// <summary>
/// Decodes Sims 4 object meshes (MODL / MLOD, via VRTF+VBUF+IBUF chunks) and
/// CAS-part meshes (GEOM, self-contained).
/// <para>
/// ⚠ Written against the format documentation in
/// <c>docs/research/sims-package-formats.md</c>, not against a real object
/// package — no sample with mesh resources was available. Every read is
/// bounds-checked and every group is independently guarded so a layout
/// mismatch degrades to "this group failed" rather than garbage geometry.
/// </para>
/// </summary>
public static class SimsMeshDecoder
{
    // GEOM vertex declaration — NOTE this enumeration is NOT the same as
    // VRTF's. Sharing one table between them is the obvious-looking mistake.
    private const uint GeomPosition = 1;
    private const uint GeomNormal = 2;
    private const uint GeomUv = 3;

    // VRTF usage.
    private const byte VrtfPosition = 0;
    private const byte VrtfNormal = 1;
    private const byte VrtfUv = 2;

    private const int PrimitiveTriangleList = 3;
    private const uint MeshFlagDropShadow = 0x08;
    private const uint MeshFlagShadowCaster = 0x10;

    /// <summary>Decode whatever meshes a resource holds. Handles bare and
    /// RCOL-wrapped forms of both families.</summary>
    public static List<SimsMesh> Decode(byte[] resource, IList<string>? warnings = null)
    {
        var meshes = new List<SimsMesh>();
        if (resource.Length < 8) return meshes;

        var rcol = SimsRcol.TryParse(resource);
        if (rcol == null)
        {
            var tag = Encoding.ASCII.GetString(resource, 0, 4);
            if (tag == "GEOM") TryAdd(meshes, () => DecodeGeom(resource, 0), warnings, "GEOM");
            else if (tag is "MLOD" or "MODL") warnings?.Add(
                $"{tag} resource is not RCOL-wrapped — its vertex buffers live in chunks that " +
                "aren't present, so it can't be decoded standalone.");
            return meshes;
        }

        // RCOL-wrapped: find the mesh chunk and decode through the chunk table.
        for (var i = 0; i < rcol.Chunks.Count; i++)
        {
            var tag = rcol.Chunks[i].TagIn(resource);
            if (tag == "GEOM")
                TryAdd(meshes, () => DecodeGeom(resource, rcol.Chunks[i].Offset), warnings, "GEOM");
            else if (tag is "MLOD" or "MODL")
                TryAddRange(meshes, () => DecodeMlod(resource, rcol, rcol.Chunks[i].Offset, warnings),
                    warnings, tag);
        }
        return meshes;
    }

    private static void TryAdd(List<SimsMesh> into, Func<SimsMesh?> decode,
                               IList<string>? warnings, string what)
    {
        try
        {
            var m = decode();
            if (m is { IsUsable: true }) into.Add(m);
        }
        catch (Exception ex) { warnings?.Add($"{what} decode failed: {ex.Message}"); }
    }

    private static void TryAddRange(List<SimsMesh> into, Func<List<SimsMesh>> decode,
                                    IList<string>? warnings, string what)
    {
        try { into.AddRange(decode()); }
        catch (Exception ex) { warnings?.Add($"{what} decode failed: {ex.Message}"); }
    }

    // ── GEOM ────────────────────────────────────────────────────────────────
    // Self-contained: declaration and data in one resource, no indirection.

    private static SimsMesh? DecodeGeom(byte[] d, int start)
    {
        var p = start;
        if (Tag(d, p) != "GEOM") return null;
        p += 4;
        var version = U32(d, ref p);
        _ = U32(d, ref p);                       // tgiOffset
        _ = U32(d, ref p);                       // tgiSize
        var embeddedId = U32(d, ref p);
        if (embeddedId != 0)
        {
            var mtnfSize = (int)U32(d, ref p);   // embedded material — skipped
            p += mtnfSize;
        }
        _ = U32(d, ref p);                       // mergeGroup
        _ = U32(d, ref p);                       // sortOrder

        var numVerts = (int)U32(d, ref p);
        var fCount = (int)U32(d, ref p);
        if (numVerts <= 0 || numVerts > 4_000_000 || fCount <= 0 || fCount > 32)
            throw new InvalidDataException($"implausible GEOM header (verts={numVerts}, fields={fCount})");

        var decl = new (uint Type, uint SubType, int Bytes)[fCount];
        for (var i = 0; i < fCount; i++)
        {
            var type = U32(d, ref p);
            var sub = U32(d, ref p);
            int bytes = d[p++];
            if (bytes <= 0 || bytes > 64) throw new InvalidDataException("bad GEOM element size");
            decl[i] = (type, sub, bytes);
        }

        var mesh = new SimsMesh();
        // Extra UV sets are declared as further UV entries. Pick the field to
        // read ONCE, up front — a "have I seen a UV yet" flag would go true
        // after the first vertex and silently starve every vertex after it.
        var uvField = Array.FindIndex(decl, e => e.Type == GeomUv);
        for (var v = 0; v < numVerts; v++)
        {
            for (var f = 0; f < decl.Length; f++)
            {
                var (type, _, bytes) = decl[f];
                var next = p + bytes;
                if (next > d.Length) throw new InvalidDataException("GEOM vertex data overruns resource");
                switch (type)
                {
                    case GeomPosition when bytes >= 12:
                        mesh.Positions.Add(new Vector3(F32(d, p), F32(d, p + 4), F32(d, p + 8)));
                        break;
                    case GeomNormal when bytes >= 12:
                        mesh.Normals.Add(new Vector3(F32(d, p), F32(d, p + 4), F32(d, p + 8)));
                        break;
                    case GeomUv when bytes >= 8 && f == uvField:
                        mesh.Uvs.Add(new Vector2(F32(d, p), F32(d, p + 4)));
                        break;
                }
                p = next;
            }
        }

        var itemCount = (int)U32(d, ref p);
        if (itemCount <= 0 || itemCount > 16) throw new InvalidDataException("bad GEOM item count");
        for (var item = 0; item < itemCount; item++)
        {
            int bytesPerPoint = d[p++];
            var numFacePoints = (int)U32(d, ref p);
            if (bytesPerPoint is not (2 or 4)) throw new InvalidDataException("bad GEOM index width");
            if (numFacePoints < 0 || p + (long)numFacePoints * bytesPerPoint > d.Length)
                throw new InvalidDataException("GEOM index data overruns resource");

            // Only the first item carries the drawable triangles.
            for (var i = 0; i < numFacePoints; i++)
            {
                var idx = bytesPerPoint == 2 ? BitConverter.ToUInt16(d, p) : (int)BitConverter.ToUInt32(d, p);
                if (item == 0) mesh.Indices.Add((int)idx);
                p += bytesPerPoint;
            }
        }

        _ = version;
        NormaliseChannels(mesh);
        return mesh;
    }

    // ── MLOD / MODL ─────────────────────────────────────────────────────────

    private static List<SimsMesh> DecodeMlod(byte[] d, SimsRcol rcol, int start, IList<string>? warnings)
    {
        var meshes = new List<SimsMesh>();
        var p = start;
        var tag = Tag(d, p);
        p += 4;
        var version = U32(d, ref p);
        var groupCount = (int)U32(d, ref p);
        if (groupCount < 0 || groupCount > 4096)
            throw new InvalidDataException($"implausible {tag} group count {groupCount}");

        for (var g = 0; g < groupCount; g++)
        {
            // subsetBytes counts everything after itself for this group. Using
            // it to step means the version-dependent tail (bone lists, mirror
            // plane, geometry states) can't desync the loop even where the
            // layout isn't fully pinned down.
            if (p + 4 > d.Length) break;
            var subsetStart = p;
            var subsetBytes = (int)U32(d, ref p);
            var nextGroup = subsetStart + 4 + subsetBytes;

            try
            {
                _ = U32(d, ref p);                          // nameHash
                _ = U32(d, ref p);                          // material
                var vrtfRef = U32(d, ref p);
                var vbufRef = U32(d, ref p);
                var ibufRef = U32(d, ref p);
                var flags = U32(d, ref p);
                var streamOffset = (int)U32(d, ref p);
                var startVertex = (int)U32(d, ref p);
                var startIndex = (int)U32(d, ref p);
                _ = U32(d, ref p);                          // minVertexIndex
                var vertexCount = (int)U32(d, ref p);
                var primitiveCount = (int)U32(d, ref p);

                var primitiveType = (int)(flags & 0xFF);
                var meshFlags = flags >> 8;
                if (primitiveType != PrimitiveTriangleList)
                {
                    warnings?.Add($"{tag} group {g}: primitive type {primitiveType} isn't a triangle list — skipped.");
                    continue;
                }

                var mesh = DecodeMlodGroup(d, rcol, vrtfRef, vbufRef, ibufRef,
                                           streamOffset, startVertex, vertexCount,
                                           startIndex, primitiveCount);
                if (mesh == null) continue;
                mesh.Name = $"{tag.ToLowerInvariant()}_group{g}";
                mesh.ShadowOnly = (meshFlags & (MeshFlagShadowCaster | MeshFlagDropShadow)) != 0;
                if (mesh.IsUsable) meshes.Add(mesh);
            }
            catch (Exception ex)
            {
                warnings?.Add($"{tag} group {g} failed: {ex.Message}");
            }
            finally
            {
                p = nextGroup;
            }
        }

        _ = version;
        return meshes;
    }

    private static SimsMesh? DecodeMlodGroup(
        byte[] d, SimsRcol rcol, uint vrtfRef, uint vbufRef, uint ibufRef,
        int streamOffset, int startVertex, int vertexCount, int startIndex, int primitiveCount)
    {
        var vrtf = rcol.ChunkWithTag(d, rcol.ResolveChunkIndex(vrtfRef), "VRTF");
        var vbuf = rcol.ChunkWithTag(d, rcol.ResolveChunkIndex(vbufRef), "VBUF");
        var ibuf = rcol.ChunkWithTag(d, rcol.ResolveChunkIndex(ibufRef), "IBUF");
        if (vrtf == null || vbuf == null || ibuf == null)
            throw new InvalidDataException("vertex format / buffer chunk reference did not resolve");

        var (stride, elements) = ReadVrtf(d, vrtf.Value.Offset);
        if (stride <= 0 || stride > 1024) throw new InvalidDataException($"bad vertex stride {stride}");

        var mesh = new SimsMesh();

        // VBUF: tag, version, flags, reserved — then raw vertices. The header
        // size is the one field the docs don't pin down; it mirrors IBUF's.
        var vertexBase = vbuf.Value.Offset + 16 + streamOffset + startVertex * stride;
        var vertexEnd = vertexBase + (long)vertexCount * stride;
        if (vertexCount <= 0 || vertexCount > 4_000_000)
            throw new InvalidDataException($"implausible vertex count {vertexCount}");
        if (vertexBase < 0 || vertexEnd > d.Length)
            throw new InvalidDataException("vertex buffer range overruns resource");

        // Same rule as GEOM: choose the UV element once. A rig can declare
        // several UV sets and only the first belongs in the prop.
        var uvElement = elements.FindIndex(e => e.Usage == VrtfUv);
        for (var v = 0; v < vertexCount; v++)
        {
            var vp = vertexBase + v * stride;
            for (var e = 0; e < elements.Count; e++)
            {
                var (usage, format, offset) = elements[e];
                var at = vp + offset;
                switch (usage)
                {
                    case VrtfPosition when format == 2:   // Float3
                        mesh.Positions.Add(new Vector3(F32(d, at), F32(d, at + 4), F32(d, at + 8)));
                        break;
                    case VrtfNormal when format == 2:
                        mesh.Normals.Add(new Vector3(F32(d, at), F32(d, at + 4), F32(d, at + 8)));
                        break;
                    case VrtfUv when format == 1 && e == uvElement:        // Float2
                        mesh.Uvs.Add(new Vector2(F32(d, at), F32(d, at + 4)));
                        break;
                    case VrtfUv when format == 0x10 && e == uvElement:     // Float16_4
                        mesh.Uvs.Add(new Vector2(Half(d, at), Half(d, at + 2)));
                        break;
                }
            }
        }

        ReadIbuf(d, ibuf.Value.Offset, ibuf.Value.Size, startIndex, primitiveCount * 3, mesh.Indices);
        NormaliseChannels(mesh);
        return mesh;
    }

    private static (int Stride, List<(byte Usage, byte Format, int Offset)> Elements)
        ReadVrtf(byte[] d, int offset)
    {
        var p = offset + 4;                       // tag
        _ = U32(d, ref p);                        // version
        var stride = (int)U32(d, ref p);
        var count = (int)U32(d, ref p);
        var isExtended = U32(d, ref p) != 0;
        if (count < 0 || count > 64) throw new InvalidDataException($"bad VRTF element count {count}");

        var elements = new List<(byte, byte, int)>(count);
        for (var i = 0; i < count; i++)
        {
            if (isExtended)
            {
                var usage = (byte)U32(d, ref p);
                _ = U32(d, ref p);                // usageIndex
                var format = (byte)U32(d, ref p);
                var off = (int)U32(d, ref p);
                elements.Add((usage, format, off));
            }
            else
            {
                var usage = d[p];
                var format = d[p + 2];
                var off = d[p + 3];
                p += 4;
                elements.Add((usage, format, off));
            }
        }
        return (stride, elements);
    }

    /// <summary>Index buffer. Two traps live here: indices can be 32-bit, and
    /// they can be <em>delta-encoded</em> (flag 0x1) — a running sum from zero
    /// across the whole buffer. Reading differenced data literally yields
    /// triangles that reference nonsense vertices.</summary>
    private static void ReadIbuf(byte[] d, int offset, int size, int startIndex, int indexCount, List<int> into)
    {
        var p = offset + 4;                       // tag
        _ = U32(d, ref p);                        // version
        var flags = U32(d, ref p);
        _ = U32(d, ref p);                        // always zero (known pipeline bug)

        var differenced = (flags & 0x1) != 0;
        var wide = (flags & 0x2) != 0;
        var width = wide ? 4 : 2;
        var dataStart = p;
        var end = offset + size;

        if (indexCount <= 0 || indexCount > 12_000_000)
            throw new InvalidDataException($"implausible index count {indexCount}");

        if (!differenced)
        {
            var at = dataStart + (long)startIndex * width;
            if (at + (long)indexCount * width > end)
                throw new InvalidDataException("index buffer range overruns chunk");
            for (var i = 0; i < indexCount; i++, at += width)
                into.Add(wide ? (int)BitConverter.ToUInt32(d, (int)at) : BitConverter.ToUInt16(d, (int)at));
            return;
        }

        // Differenced: the running total has to be rebuilt from the start of
        // the buffer, so startIndex can't be used to seek — decode and slice.
        var running = 0;
        var available = (end - dataStart) / width;
        var need = startIndex + indexCount;
        if (need > available) throw new InvalidDataException("differenced index buffer is short");
        for (var i = 0; i < need; i++)
        {
            var at = dataStart + i * width;
            running += wide ? BitConverter.ToInt32(d, at) : BitConverter.ToInt16(d, at);
            if (i >= startIndex) into.Add(running);
        }
    }

    // ── shared ──────────────────────────────────────────────────────────────

    /// <summary>Drop channels that didn't come through for every vertex —
    /// a partial normal or UV array is worse than none, because the glTF
    /// writer would silently pair the wrong values with the wrong vertices.
    /// Also discards triangles that point outside the vertex array.</summary>
    private static void NormaliseChannels(SimsMesh mesh)
    {
        var n = mesh.Positions.Count;
        if (mesh.Normals.Count != n) mesh.Normals.Clear();
        if (mesh.Uvs.Count != n) mesh.Uvs.Clear();

        var clean = new List<int>(mesh.Indices.Count);
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            int a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            if (a < 0 || b < 0 || c < 0 || a >= n || b >= n || c >= n) continue;
            clean.Add(a); clean.Add(b); clean.Add(c);
        }
        if (clean.Count != mesh.Indices.Count)
        {
            mesh.Indices.Clear();
            mesh.Indices.AddRange(clean);
        }
    }

    private static string Tag(byte[] d, int p) =>
        p + 4 <= d.Length ? Encoding.ASCII.GetString(d, p, 4) : "";

    private static uint U32(byte[] d, ref int p)
    {
        var v = BitConverter.ToUInt32(d, p);
        p += 4;
        return v;
    }

    private static float F32(byte[] d, int p) => BitConverter.ToSingle(d, p);

    private static float Half(byte[] d, int p) => (float)BitConverter.ToHalf(d, p);
}
