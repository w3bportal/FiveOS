// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using g3;

namespace FiveOS.Services;

/// <summary>
/// Loads the bundled freemode body as a skin-weight donor.
///
/// The reference .glb files carry the real engine weights, so a garment
/// weighted against them inherits Rockstar's own rigging rather than an
/// approximation. Two details make this fiddlier than "load the file":
///
/// 1. Each file contains SEVERAL skins — the deform rig plus a Blender control
///    rig, and in the male file a leftover retarget rig. Taking the first one
///    gets the control rig and everything downstream is wrong.
/// 2. Blend indices written into a .ydd are positions in the PED'S skeleton,
///    so the palette order has to come from the glTF skin's own joint array.
///    Assimp exposes bones in its own order, which is not that order — mapping
///    by Assimp's ordering silently points every weight at the wrong bone.
///
/// So the joint order is read straight out of the glTF JSON and Assimp is used
/// only for geometry and weights, joined by bone name.
/// </summary>
public static class FreemodeDonorLoader
{
    /// <summary>The ped skeleton the palette indexes into.</summary>
    public const int PaletteSize = 128;

    private static readonly Dictionary<string, GarmentSkinTransfer.Donor> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>Loads the male or female donor, cached for the process lifetime.</summary>
    /// <param name="variant">"male" or "female".</param>
    /// <param name="toGtaSpace">Convert the glTF Y-up display space to GTA's
    /// Z-up. Leave on unless the garment being weighted is itself in Y-up.</param>
    public static GarmentSkinTransfer.Donor Load(string variant, bool toGtaSpace = true)
    {
        string key = (variant?.Equals("female", StringComparison.OrdinalIgnoreCase) == true ? "female" : "male")
                   + (toGtaSpace ? ":gta" : ":raw");
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var donor = LoadUncached(key.StartsWith("female", StringComparison.Ordinal) ? "female" : "male", toGtaSpace);
            Cache[key] = donor;
            return donor;
        }
    }

    /// <summary>Bone name → palette index for the given variant, in the ped's
    /// own skeleton order. This is the map a caller needs to make sense of the
    /// indices coming out of a transfer.</summary>
    public static IReadOnlyDictionary<string, int> BoneIndices(string variant)
        => ReadJointOrder(ReferencePath(variant)).Select((n, i) => (n, i))
            .ToDictionary(t => t.n, t => t.i, StringComparer.OrdinalIgnoreCase);

    private static string ReferencePath(string variant)
    {
        var v = variant.Equals("female", StringComparison.OrdinalIgnoreCase) ? "female" : "male";
        var path = Path.Combine(RuntimeAssets.ViewerDir, "reference", $"freemode_{v}.glb");
        if (!File.Exists(path))
            throw new FileNotFoundException($"the bundled freemode {v} reference is missing", path);
        return path;
    }

    private static GarmentSkinTransfer.Donor LoadUncached(string variant, bool toGtaSpace)
    {
        var path = ReferencePath(variant);
        var jointOrder = ReadJointOrder(path);
        var slot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < jointOrder.Count && i < PaletteSize; i++) slot[jointOrder[i]] = i;

        using var ctx = new Assimp.AssimpContext();
        var scene = ctx.ImportFile(path, Assimp.PostProcessSteps.Triangulate);

        // The body is the mesh whose bones belong to the deform skin. Several
        // meshes carry bones, so score them by overlap with that joint set.
        Assimp.Mesh? body = null;
        int bestOverlap = 0;
        foreach (var m in scene.Meshes)
        {
            if (!m.HasBones) continue;
            int overlap = m.Bones.Count(b => slot.ContainsKey(b.Name));
            if (overlap > bestOverlap) { bestOverlap = overlap; body = m; }
        }
        if (body is null)
            throw new InvalidOperationException($"no skinned body mesh found in freemode_{variant}.glb");

        // The mesh's node transform MUST be applied. The male body sits under a
        // node carrying a 180° yaw, so reading vertices in mesh-local space
        // hands back a body whose left arm is where the ped's right arm is —
        // and every garment weighted against it comes out mirrored, silently.
        // The female chain is identity, which is why the bug hides on one path.
        // (PostProcessSteps.PreTransformVertices would also fix the space but
        // makes Assimp discard the bones and weights this whole loader is for.)
        var world = FindMeshWorldTransform(scene, Array.IndexOf(scene.Meshes.ToArray(), body));

        var mesh = new DMesh3();
        foreach (var raw in body.Vertices)
        {
            var v = Transform(world, raw);
            mesh.AppendVertex(toGtaSpace
                ? new Vector3d(v.X, -v.Z, v.Y)     // glTF Y-up → GTA Z-up
                : new Vector3d(v.X, v.Y, v.Z));
        }
        foreach (var f in body.Faces)
            if (f.IndexCount == 3) mesh.AppendTriangle(f.Indices[0], f.Indices[1], f.Indices[2]);

        var weights = new Dictionary<int, float>[body.VertexCount];
        for (int i = 0; i < weights.Length; i++) weights[i] = new Dictionary<int, float>();
        foreach (var bone in body.Bones)
        {
            // Bones outside the deform palette (helpers, facial, the control
            // rig's extras) cannot be addressed by a blend index and are skipped
            // rather than renumbered — renumbering is exactly the bug this
            // whole loader exists to avoid.
            if (!slot.TryGetValue(bone.Name, out int index)) continue;
            foreach (var vw in bone.VertexWeights)
                if (vw.Weight > 0f && vw.VertexID < weights.Length)
                    weights[vw.VertexID][index] = weights[vw.VertexID].TryGetValue(index, out var e)
                        ? e + vw.Weight : vw.Weight;
        }
        foreach (var w in weights)
        {
            float sum = w.Values.Sum();
            if (sum > 1e-6f) foreach (var k in w.Keys.ToList()) w[k] /= sum;
        }

        return new GarmentSkinTransfer.Donor { Mesh = mesh, Weights = weights };
    }

    /// <summary>Accumulated world transform of the node that carries the given
    /// mesh, or identity if it cannot be found.</summary>
    private static Assimp.Matrix4x4 FindMeshWorldTransform(Assimp.Scene scene, int meshIndex)
    {
        if (meshIndex < 0 || scene.RootNode is null) return Assimp.Matrix4x4.Identity;

        Assimp.Matrix4x4 found = Assimp.Matrix4x4.Identity;
        bool hit = false;

        void Walk(Assimp.Node node, Assimp.Matrix4x4 parent)
        {
            if (hit) return;
            var world = parent * node.Transform;   // Assimp composes parent * local
            if (node.HasMeshes && node.MeshIndices.Contains(meshIndex))
            {
                found = world;
                hit = true;
                return;
            }
            foreach (var child in node.Children) Walk(child, world);
        }

        Walk(scene.RootNode, Assimp.Matrix4x4.Identity);
        return found;
    }

    private static Assimp.Vector3D Transform(Assimp.Matrix4x4 m, Assimp.Vector3D v) => new(
        m.A1 * v.X + m.A2 * v.Y + m.A3 * v.Z + m.A4,
        m.B1 * v.X + m.B2 * v.Y + m.B3 * v.Z + m.B4,
        m.C1 * v.X + m.C2 * v.Y + m.C3 * v.Z + m.C4);

    /// <summary>
    /// Reads the deform skin's joint names, in order, from the .glb's JSON
    /// chunk. The deform rig is the 128-joint skin containing SKEL_ROOT — the
    /// male file names it "skel" and the female "head_000_r", so it is
    /// identified by shape rather than by name.
    /// </summary>
    private static List<string> ReadJointOrder(string glbPath)
    {
        var bytes = File.ReadAllBytes(glbPath);
        if (bytes.Length < 20 || BitConverter.ToUInt32(bytes, 0) != 0x46546C67)  // "glTF"
            throw new InvalidDataException($"{Path.GetFileName(glbPath)} is not a binary glTF");
        int jsonLength = (int)BitConverter.ToUInt32(bytes, 12);
        var json = Encoding.UTF8.GetString(bytes, 20, jsonLength);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("nodes", out var nodes) || !root.TryGetProperty("skins", out var skins))
            throw new InvalidDataException($"{Path.GetFileName(glbPath)} has no skins");

        string NameOf(int nodeIndex) =>
            nodes[nodeIndex].TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

        List<string>? best = null;
        foreach (var skin in skins.EnumerateArray())
        {
            if (!skin.TryGetProperty("joints", out var joints)) continue;
            var names = joints.EnumerateArray().Select(j => NameOf(j.GetInt32())).ToList();
            if (!names.Any(n => n.Equals("SKEL_ROOT", StringComparison.OrdinalIgnoreCase))) continue;
            // Prefer exactly the ped palette; fall back to the smallest
            // SKEL_ROOT-bearing skin, which is never the 134-joint control rig.
            if (names.Count == PaletteSize) return names;
            if (best is null || names.Count < best.Count) best = names;
        }
        return best ?? throw new InvalidDataException(
            $"{Path.GetFileName(glbPath)} has no skin containing SKEL_ROOT");
    }
}
