// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace FiveOS.Services.Sims;

/// <summary>
/// Writes decoded Sims meshes out as a single self-contained .glb, which the
/// normal prop pipeline then ingests exactly like any other model — same
/// importer, same Optimize pass, same .ydr writer. Mirrors how the Sims
/// animation path hands CLIP data over as glTF rather than teaching the
/// pipeline a second format.
/// </summary>
public static class SimsGlbWriter
{
    private const uint GlbMagic = 0x46546C67;   // 'glTF'
    private const uint ChunkJson = 0x4E4F534A;  // 'JSON'
    private const uint ChunkBin = 0x004E4942;   // 'BIN\0'

    private const int ComponentFloat = 5126;
    private const int ComponentUInt = 5125;

    public static string Write(string path, IReadOnlyList<SimsMesh> meshes)
    {
        if (meshes.Count == 0) throw new InvalidDataException("No meshes to write.");

        using var bin = new MemoryStream();
        var bufferViews = new List<object>();
        var accessors = new List<object>();
        var gltfMeshes = new List<object>();
        var nodes = new List<object>();
        var nodeIndices = new List<int>();

        int AddView(int byteOffset, int byteLength, int? target)
        {
            var view = new Dictionary<string, object>
            {
                ["buffer"] = 0,
                ["byteOffset"] = byteOffset,
                ["byteLength"] = byteLength,
            };
            if (target.HasValue) view["target"] = target.Value;
            bufferViews.Add(view);
            return bufferViews.Count - 1;
        }

        int AddAccessor(int view, int component, int count, string type,
                        IReadOnlyList<float>? min = null, IReadOnlyList<float>? max = null)
        {
            var acc = new Dictionary<string, object>
            {
                ["bufferView"] = view,
                ["componentType"] = component,
                ["count"] = count,
                ["type"] = type,
            };
            if (min != null) acc["min"] = min;
            if (max != null) acc["max"] = max;
            accessors.Add(acc);
            return accessors.Count - 1;
        }

        // glTF requires every bufferView to start on a 4-byte boundary for the
        // component types used here; floats and uint32 are already aligned, but
        // pad defensively so a future 16-bit path can't corrupt the file.
        void Align()
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
        }

        foreach (var mesh in meshes)
        {
            if (!mesh.IsUsable) continue;

            Align();
            var posOffset = (int)bin.Length;
            var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var max = new[] { float.MinValue, float.MinValue, float.MinValue };
            foreach (var v in mesh.Positions)
            {
                WriteFloat(bin, v.X); WriteFloat(bin, v.Y); WriteFloat(bin, v.Z);
                min[0] = MathF.Min(min[0], v.X); min[1] = MathF.Min(min[1], v.Y); min[2] = MathF.Min(min[2], v.Z);
                max[0] = MathF.Max(max[0], v.X); max[1] = MathF.Max(max[1], v.Y); max[2] = MathF.Max(max[2], v.Z);
            }
            var posAccessor = AddAccessor(
                AddView(posOffset, (int)bin.Length - posOffset, 34962),
                ComponentFloat, mesh.Positions.Count, "VEC3", min, max);

            var attributes = new Dictionary<string, object> { ["POSITION"] = posAccessor };

            if (mesh.Normals.Count == mesh.Positions.Count)
            {
                Align();
                var off = (int)bin.Length;
                foreach (var n in mesh.Normals)
                {
                    // A zero normal is legal in the source but makes glTF
                    // validators and lighting misbehave — substitute up.
                    var u = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
                    WriteFloat(bin, u.X); WriteFloat(bin, u.Y); WriteFloat(bin, u.Z);
                }
                attributes["NORMAL"] = AddAccessor(
                    AddView(off, (int)bin.Length - off, 34962),
                    ComponentFloat, mesh.Normals.Count, "VEC3");
            }

            if (mesh.Uvs.Count == mesh.Positions.Count)
            {
                Align();
                var off = (int)bin.Length;
                foreach (var t in mesh.Uvs) { WriteFloat(bin, t.X); WriteFloat(bin, t.Y); }
                attributes["TEXCOORD_0"] = AddAccessor(
                    AddView(off, (int)bin.Length - off, 34962),
                    ComponentFloat, mesh.Uvs.Count, "VEC2");
            }

            Align();
            var idxOffset = (int)bin.Length;
            foreach (var i in mesh.Indices) WriteUInt(bin, (uint)i);
            var idxAccessor = AddAccessor(
                AddView(idxOffset, (int)bin.Length - idxOffset, 34963),
                ComponentUInt, mesh.Indices.Count, "SCALAR");

            gltfMeshes.Add(new Dictionary<string, object>
            {
                ["name"] = string.IsNullOrWhiteSpace(mesh.Name) ? $"mesh{gltfMeshes.Count}" : mesh.Name,
                ["primitives"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["attributes"] = attributes,
                        ["indices"] = idxAccessor,
                        ["mode"] = 4,   // triangles
                    },
                },
            });

            nodes.Add(new Dictionary<string, object>
            {
                ["mesh"] = gltfMeshes.Count - 1,
                ["name"] = string.IsNullOrWhiteSpace(mesh.Name) ? $"node{nodes.Count}" : mesh.Name,
            });
            nodeIndices.Add(nodes.Count - 1);
        }

        if (gltfMeshes.Count == 0) throw new InvalidDataException("No usable meshes to write.");

        var binBytes = bin.ToArray();
        var gltf = new Dictionary<string, object>
        {
            ["asset"] = new Dictionary<string, object> { ["version"] = "2.0", ["generator"] = "FiveOS Sims importer" },
            ["scene"] = 0,
            ["scenes"] = new[] { new Dictionary<string, object> { ["nodes"] = nodeIndices } },
            ["nodes"] = nodes,
            ["meshes"] = gltfMeshes,
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new[] { new Dictionary<string, object> { ["byteLength"] = binBytes.Length } },
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(gltf);
        var jsonPad = (4 - json.Length % 4) % 4;
        var binPad = (4 - binBytes.Length % 4) % 4;
        var total = 12 + 8 + json.Length + jsonPad + 8 + binBytes.Length + binPad;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write(GlbMagic);
        w.Write(2u);
        w.Write((uint)total);

        w.Write((uint)(json.Length + jsonPad));
        w.Write(ChunkJson);
        w.Write(json);
        for (var i = 0; i < jsonPad; i++) w.Write((byte)0x20);   // JSON pads with spaces

        w.Write((uint)(binBytes.Length + binPad));
        w.Write(ChunkBin);
        w.Write(binBytes);
        for (var i = 0; i < binPad; i++) w.Write((byte)0);       // BIN pads with zeros

        return path;
    }

    private static void WriteFloat(Stream s, float v)
    {
        Span<byte> b = stackalloc byte[4];
        BitConverter.TryWriteBytes(b, v);
        s.Write(b);
    }

    private static void WriteUInt(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BitConverter.TryWriteBytes(b, v);
        s.Write(b);
    }
}
