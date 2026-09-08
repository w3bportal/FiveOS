// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CodeWalker.GameFiles;

namespace FiveOS.Services;

/// <summary>
/// Builds the grouped-part payload the car previewer renders: the vehicle
/// yft's HIGH-LOD geometry split by the bone each geometry is rigidly bound
/// to (chassis / door_dside_f / bonnet / extra_1 / …), plus wheels.
///
/// Why grouping matters: vehicle yfts keep door/bonnet/extra GEOMETRY inside
/// the one main drawable, addressed through the skeleton — the fragment's
/// physics children carry the names but (usually) no geometry. Grouping by
/// bone lets the viewer toggle extras and swing doors around their bone
/// pivot without a full skeletal renderer.
///
/// Wheels are the exception: their geometry lives in fragment CHILD
/// drawables (typically only the left side). We instance the left meshes at
/// every wheel_* bone, mirroring X for the right side — the same trick the
/// game itself uses.
/// </summary>
public sealed class VehicleMeshBuilder
{
    public sealed record GroupInfo(string Name, float[] Pivot, string Kind);

    private readonly DrawableMeshExtractor _extractor;

    public VehicleMeshBuilder(DrawableMeshExtractor extractor) => _extractor = extractor;

    /// <summary>Build the fiveosWorkbench.loadLod payload (JSON) for one car
    /// yft, with parts carrying a "group" and payload-level group pivots.
    /// Returns the JSON plus the toggleable group list for the host UI.
    /// <paramref name="previewRatio"/> &lt; 1 decimates the main body's HIGH
    /// LOD NON-destructively (cloned) so the slider previews exactly what
    /// "Optimize this car" would bake — the on-disk yft is never touched.</summary>
    public sealed record BuildResult(
        string PayloadJson, List<GroupInfo> Groups, int Tris,
        int TexturedParts, int TotalParts, List<BoneInfo> Skeleton, List<PieceInfo> Pieces);

    /// <summary>One rendered piece (a bone's share of one geometry): which bone
    /// it hangs on, its triangle count and its box in RAGE vehicle space. The
    /// bone splitter lets the user pick pieces to move onto a new bone.</summary>
    public sealed record PieceInfo(int Index, int Bone, int Tris, float[] Min, float[] Max);

    /// <summary>One bone of the vehicle skeleton, for the Vehicle Anim editor.
    /// <paramref name="Pos"/>/<paramref name="Rot"/> are the LOCAL (parent-
    /// relative) rest transform in RAGE Z-up space — exactly the values a
    /// .ycd position/rotation track is written against.</summary>
    public sealed record BoneInfo(
        string Name, int Index, int ParentIndex, ushort Tag, string? Kind,
        float[] Pos, float[] Rot, int Depth, bool HasGeometry);

    /// <param name="includeSkeleton">Emit the full bone hierarchy in the
    /// payload (<c>skeleton</c>) and a <c>bone</c> index on every part. Only
    /// the Vehicle Anim editor needs it; the car previewer keeps the lighter
    /// group-only shape.</param>
    public BuildResult Build(
        string yftPath, bool frameCamera, float previewRatio = 1f, bool preserveBoundary = true,
        IReadOnlyDictionary<uint, Texture>? sharedTextures = null, bool includeSkeleton = false)
    {
        var yft = DrawableOptimizer.LoadResource<YftFile>(yftPath);
        var drawable = yft.Fragment?.Drawable
            ?? throw new InvalidDataException("Fragment has no drawable.");

        // Textures: the mod's own sibling .ytd (paint/livery/badges) first,
        // then GTA's shared vehshare textures fill the generic body samplers
        // the mod references but doesn't ship (vehicle_generic_*). Mod wins.
        try
        {
            var tex = ClothingTextureResolver.Load(yftPath, new[] { (DrawableBase)drawable });
            var map = tex.Map;
            // Livery/wrap: pick the mod's biggest "sign"/"livery" texture (the
            // full-body wrap the game swaps in per livery index) to fill body
            // panels whose diffuse is a placeholder. Detect from the MOD's own
            // textures (map), before merging generic shared ones.
            _extractor.SetLiveryTexture(FindLivery(map));
            if (sharedTextures is { Count: > 0 })
            {
                if (map.Count == 0) map = new Dictionary<uint, Texture>();
                foreach (var (h, t) in sharedTextures)
                    map.TryAdd(h, t);   // don't overwrite the mod's own textures
            }
            _extractor.SetExternalTextures(map.Count > 0 ? map : null);
        }
        catch { _extractor.SetExternalTextures(null); _extractor.SetLiveryTexture(null); }

        var skeleton = drawable.Skeleton;
        try
        {
            skeleton?.ResetBoneTransforms();
            skeleton?.BuildTransformations();
            skeleton?.UpdateBoneTransforms();
        }
        catch { /* bind-pose matrices stay at whatever the file loaded */ }

        var bones = skeleton?.Bones?.Items;
        string BoneName(int i) => (bones != null && i >= 0 && i < bones.Length ? bones[i].Name : null) ?? "body";
        float[] BonePivot(string name)
        {
            var b = bones?.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (b == null) return new[] { 0f, 0f, 0f };
            var t = b.AbsTransform.TranslationVector;
            if (t.X != 0 || t.Y != 0 || t.Z != 0)
                return new[] { t.X, t.Y, t.Z };
            return new[] { b.Translation.X, b.Translation.Y, b.Translation.Z };
        }

        var parts = new List<(DrawableMeshExtractor.Part Part, string Group, int Bone)>();
        int tris = 0;

        // ── Main drawable: one part per geometry, grouped by bound bone ──
        var models = DrawableLodBuilder.GetLodModels(drawable, DrawableLodBuilder.DrawableLod.High)
            ?? throw new InvalidDataException("No HIGH LOD geometry.");
        // Live optimize preview: clone + decimate the body so the viewport
        // shows the reduction before the user commits it to disk.
        if (previewRatio > 0f && previewRatio < 0.999f)
            models = DrawableLodBuilder.BuildTierModels(models, previewRatio, preserveBoundary) ?? models;
        foreach (var model in models)
        {
            if (model?.Geometries == null) continue;
            for (int gi = 0; gi < model.Geometries.Length; gi++)
            {
                var geom = model.Geometries[gi];
                if (geom == null) continue;
                var part = _extractor.ExtractGeometry(geom, model, gi, drawable);
                if (part == null) continue;
                // Vanilla-style cars are ONE skinned model: a single body
                // geometry carries chassis, doors and bonnet together, each
                // vertex naming its bone through BlendIndices. Split by bone
                // so a door is its own part hinged on its own bone.
                foreach (var (piece, boneIdx) in SplitByBone(part, geom, model))
                {
                    parts.Add((piece, BoneName(boneIdx), boneIdx));
                    tris += piece.Indices.Length / 3;
                }
            }
        }

        // ── Wheels: child drawables, instanced onto every wheel_* bone ──
        var wheelMeshes = CollectWheelMeshes(yft);
        if (wheelMeshes.Count > 0 && bones != null)
        {
            foreach (var bone in bones.Where(b => b.Name?.StartsWith("wheel_", StringComparison.OrdinalIgnoreCase) == true))
            {
                bool front = bone.Name.Contains("f", StringComparison.OrdinalIgnoreCase);
                var src = wheelMeshes.TryGetValue(front ? "front" : "rear", out var m) ? m
                    : wheelMeshes.Values.First();
                bool mirror = Regex.IsMatch(bone.Name, "_r", RegexOptions.IgnoreCase);
                foreach (var wp in src)
                {
                    var placed = PlacePart(wp, bone.Translation.X, bone.Translation.Y, bone.Translation.Z, mirror);
                    parts.Add((placed, bone.Name, Array.IndexOf(bones, bone)));
                    tris += placed.Indices.Length / 3;
                }
            }
        }

        // ── Toggleable groups for the host UI ────────────────────────────
        var groups = new List<GroupInfo>();
        foreach (var g in parts.Select(p => p.Group).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var kind = GroupKind(g);
            if (kind == null) continue;   // chassis/body/etc. — not toggleable
            groups.Add(new GroupInfo(g, BonePivot(g), kind));
        }

        // ── Skeleton (Vehicle Anim editor) ───────────────────────────────
        var skeletonInfo = new List<BoneInfo>();
        if (includeSkeleton && bones != null)
        {
            var withGeo = new HashSet<int>(parts.Select(p => p.Bone));
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones[i];
                if (b == null) continue;
                int parent = b.ParentIndex;
                if (parent < 0 || parent >= bones.Length || parent == i) parent = -1;
                int depth = 0;
                for (int p = parent, guard = 0; p >= 0 && guard < 64; guard++)
                {
                    depth++;
                    var pb = bones[p];
                    p = pb == null ? -1 : pb.ParentIndex;
                    if (p >= bones.Length) p = -1;
                }
                var name = string.IsNullOrEmpty(b.Name) ? $"bone_{i}" : b.Name;
                skeletonInfo.Add(new BoneInfo(
                    name, i, parent, b.Tag, BoneKind(name),
                    new[] { b.Translation.X, b.Translation.Y, b.Translation.Z },
                    new[] { b.Rotation.X, b.Rotation.Y, b.Rotation.Z, b.Rotation.W },
                    depth, withGeo.Contains(i)));
            }
        }

        var pieces = new List<PieceInfo>(parts.Count);
        if (includeSkeleton)
            for (int i = 0; i < parts.Count; i++)
            {
                var pos = parts[i].Part.Positions;
                var mn = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
                var mx = new[] { float.MinValue, float.MinValue, float.MinValue };
                for (int k = 0; k + 2 < pos.Length; k += 3)
                    for (int a = 0; a < 3; a++) { if (pos[k + a] < mn[a]) mn[a] = pos[k + a]; if (pos[k + a] > mx[a]) mx[a] = pos[k + a]; }
                if (pos.Length < 3) { mn = new float[3]; mx = new float[3]; }
                pieces.Add(new PieceInfo(i, parts[i].Bone, parts[i].Part.Indices.Length / 3, mn, mx));
            }

        int textured = parts.Count(p => p.Part.TextureFile != null);
        return new BuildResult(BuildPayload(parts, groups, tris, frameCamera, includeSkeleton ? skeletonInfo : null),
            groups, tris, textured, parts.Count, skeletonInfo, pieces);
    }

    /// <summary>Editor-facing bone category: the toggleable group kinds plus
    /// the common non-geometry hinge/anchor bones people actually animate.</summary>
    private static string? BoneKind(string name)
    {
        var k = GroupKind(name);
        if (k != null) return k;
        var g = name.ToLowerInvariant();
        if (g.StartsWith("steering") || g.StartsWith("handlebars")) return "part";
        if (g.StartsWith("suspension_") || g.StartsWith("transmission_") || g.StartsWith("hub_")) return "suspension";
        if (g.StartsWith("exhaust") || g.StartsWith("engine") || g.StartsWith("bumper") || g.StartsWith("wing_")) return "part";
        if (g.StartsWith("seat_") || g.StartsWith("misc") || g.StartsWith("neon") || g.StartsWith("indicator")
            || g.StartsWith("headlight") || g.StartsWith("taillight") || g.StartsWith("brakelight")
            || g.StartsWith("reversinglight") || g.StartsWith("platelight") || g.StartsWith("interiorlight")
            || g.StartsWith("extralight") || g.StartsWith("siren") || g.StartsWith("turret")
            || g.StartsWith("rotor") || g.StartsWith("prop") || g.StartsWith("rudder") || g.StartsWith("elevator")
            || g.StartsWith("aileron") || g.StartsWith("afterburner") || g.StartsWith("mast") || g.StartsWith("crane"))
            return "part";
        return null;
    }

    /// <summary>The mod's livery/wrap texture: the largest "sign"/"livery"-named
    /// texture (or, failing a name match, the single dominant large texture) —
    /// what GTA binds to body panels for the selected livery. Null when the mod
    /// has no wrap (then body panels stay painted metal).</summary>
    private static Texture? FindLivery(IReadOnlyDictionary<uint, Texture> map)
    {
        Texture? best = null;
        long bestArea = 0;
        foreach (var t in map.Values)
        {
            if (t?.Data == null || t.Width < 256) continue;
            var n = (t.Name ?? "").ToLowerInvariant();
            // Skip clearly non-livery maps.
            if (n.EndsWith("_n") || n.EndsWith("_nm") || n.EndsWith("_normal")
                || n.EndsWith("_s") || n.EndsWith("_spec") || n.Contains("_dirt")
                || n.Contains("interior") || n.Contains("light") || n.Contains("plate")
                || n.StartsWith("vehicle_generic"))
                continue;
            bool named = n.Contains("sign") || n.Contains("livery") || n.Contains("wrap") || n.Contains("skin");
            long area = (long)t.Width * t.Height;
            // Named liveries win outright; otherwise the biggest plausible body
            // texture (needs to be sizeable — a real wrap is 1k²+).
            if (named) { if (best == null || IsNamedBetter(best, t) || area > bestArea) { best = t; bestArea = area + (1L << 40); } }
            else if (bestArea < (1L << 40) && area > bestArea && area >= 1024 * 1024) { best = t; bestArea = area; }
        }
        return best;
    }

    private static bool IsNamedBetter(Texture cur, Texture cand)
        => (long)cand.Width * cand.Height > (long)cur.Width * cur.Height;

    // RAGE vertex declaration slots (fixed semantic order).
    private const int CompBlendWeights = 1;
    private const int CompBlendIndices = 2;

    /// <summary>Split a geometry's triangles by the bone each vertex is
    /// rigidly weighted to (BlendIndices → geometry BoneIds). Geometry with no
    /// per-vertex binding, or bound to a single bone, comes back as one piece
    /// on <see cref="GeometryBone"/>. The extractor keeps vertex order 1:1
    /// with the RAGE buffer, so blend bytes line up with part positions.</summary>
    private static IEnumerable<(DrawableMeshExtractor.Part Part, int Bone)> SplitByBone(
        DrawableMeshExtractor.Part part, DrawableGeometry geom, DrawableModel model)
    {
        int fallback = GeometryBone(geom, model);
        int[]? vertexBone = null;
        try
        {
            var ids = geom.BoneIds;
            var vd = geom.VertexData;
            var decl = vd?.Info;
            if (ids is { Length: > 0 } && vd?.VertexBytes is { } bytes && decl != null
                && ((uint)decl.Flags & (1u << CompBlendIndices)) != 0)
            {
                int offI = decl.GetComponentOffset(CompBlendIndices);
                int offW = ((uint)decl.Flags & (1u << CompBlendWeights)) != 0 ? decl.GetComponentOffset(CompBlendWeights) : -1;
                int stride = vd.VertexStride, vc = Math.Min(vd.VertexCount, part.Positions.Length / 3);
                if (offI >= 0 && stride > 0 && vc > 0)
                {
                    vertexBone = new int[vc];
                    for (int v = 0; v < vc; v++)
                    {
                        int oI = v * stride + offI;
                        if (oI + 3 >= bytes.Length) { vertexBone[v] = fallback; continue; }
                        int best = 0;
                        if (offW >= 0)
                        {
                            int oW = v * stride + offW;
                            if (oW + 3 < bytes.Length)
                            {
                                int bw = bytes[oW];
                                for (int k = 1; k < 4; k++) if (bytes[oW + k] > bw) { bw = bytes[oW + k]; best = k; }
                            }
                        }
                        int blend = bytes[oI + best];
                        vertexBone[v] = blend < ids.Length ? ids[blend] : fallback;
                    }
                }
            }
        }
        catch { vertexBone = null; }

        var idx = part.Indices;
        if (vertexBone == null || idx.Length < 3) { yield return (part, fallback); yield break; }

        // Triangle → bone (majority of its three corners, first corner on a tie).
        int triCount = idx.Length / 3;
        var triBone = new int[triCount];
        var counts = new Dictionary<int, int>();
        for (int t = 0; t < triCount; t++)
        {
            int a = idx[t * 3], b = idx[t * 3 + 1], c = idx[t * 3 + 2];
            int ba = a < vertexBone.Length ? vertexBone[a] : fallback;
            int bb = b < vertexBone.Length ? vertexBone[b] : fallback;
            int bc = c < vertexBone.Length ? vertexBone[c] : fallback;
            int bone = (bb == bc && bb != ba) ? bb : ba;
            triBone[t] = bone;
            counts[bone] = counts.TryGetValue(bone, out var n) ? n + 1 : 1;
        }
        if (counts.Count == 1) { yield return (part, triBone[0]); yield break; }

        foreach (var bone in counts.Keys.OrderBy(k => k))
        {
            var remap = new Dictionary<int, int>();
            var newIdx = new List<int>();
            var order = new List<int>();
            for (int t = 0; t < triCount; t++)
            {
                if (triBone[t] != bone) continue;
                for (int k = 0; k < 3; k++)
                {
                    int old = idx[t * 3 + k];
                    if (!remap.TryGetValue(old, out var nv)) { nv = order.Count; remap[old] = nv; order.Add(old); }
                    newIdx.Add(nv);
                }
            }
            var pos = new float[order.Count * 3];
            float[]? nrm = part.Normals != null ? new float[order.Count * 3] : null;
            float[]? uv = part.Uvs != null ? new float[order.Count * 2] : null;
            for (int i = 0; i < order.Count; i++)
            {
                int o = order[i];
                if (o * 3 + 2 < part.Positions.Length)
                { pos[i * 3] = part.Positions[o * 3]; pos[i * 3 + 1] = part.Positions[o * 3 + 1]; pos[i * 3 + 2] = part.Positions[o * 3 + 2]; }
                if (nrm != null && o * 3 + 2 < part.Normals!.Length)
                { nrm[i * 3] = part.Normals[o * 3]; nrm[i * 3 + 1] = part.Normals[o * 3 + 1]; nrm[i * 3 + 2] = part.Normals[o * 3 + 2]; }
                if (uv != null && o * 2 + 1 < part.Uvs!.Length)
                { uv[i * 2] = part.Uvs[o * 2]; uv[i * 2 + 1] = part.Uvs[o * 2 + 1]; }
            }
            yield return (part with { Positions = pos, Normals = nrm, Uvs = uv, Indices = newIdx.ToArray() }, bone);
        }
    }

    private static int GeometryBone(DrawableGeometry geom, DrawableModel model)
    {
        try
        {
            var ids = geom.BoneIds;
            if (ids is { Length: > 0 })
            {
                var vd = geom.VertexData;
                if (vd?.VertexBytes is { } bytes && vd.Info != null && vd.VertexCount > 0 && vd.VertexStride > 0)
                {
                    int off = FindBlendIndexOffset(vd.Info);
                    if (off >= 0)
                    {
                        var tallies = new Dictionary<int, int>();
                        int n = Math.Min(vd.VertexCount, 96);
                        int stride = vd.VertexStride;
                        for (int i = 0; i < n; i++)
                        {
                            int o = i * stride + off;
                            if (o < 0 || o >= bytes.Length) break;
                            int blend = bytes[o];
                            if (blend >= ids.Length) continue;
                            int bone = ids[blend];
                            tallies[bone] = tallies.TryGetValue(bone, out var c) ? c + 1 : 1;
                        }
                        if (tallies.Count > 0)
                            return tallies.OrderByDescending(kv => kv.Value).First().Key;
                    }
                }
                return ids[0];
            }
        }
        catch { /* fall through to the model's bone */ }
        return model.BoneIndex;
    }

    private static int FindBlendIndexOffset(VertexDeclaration info)
    {
        int off = info.GetComponentOffset(2);
        if (off >= 0) return off;
        for (int c = 0; c < 16; c++)
        {
            var t = info.GetComponentType(c);
            if (t is VertexComponentType.UByte4 or VertexComponentType.Colour)
            {
                off = info.GetComponentOffset(c);
                if (off >= 0) return off;
            }
        }
        return -1;
    }

    private static string? GroupKind(string group)
    {
        var g = group.ToLowerInvariant();
        if (g.StartsWith("door_dside") || g is "door_lf" or "door_lr" or "door_l") return "door_l";
        if (g.StartsWith("door_pside") || g is "door_rf" or "door_rr" or "door_r") return "door_r";
        if (g.StartsWith("door_hatch_l")) return "door_l";
        if (g.StartsWith("door_hatch_r")) return "door_r";
        if (g.StartsWith("bonnet") || g is "hood") return "bonnet";
        if (g.StartsWith("boot") || g is "trunk" or "bag") return "boot";
        if (g.StartsWith("extra_")) return "extra";
        if (g.StartsWith("wheel_")) return "wheel";
        if (g.StartsWith("misc_") || g.StartsWith("window") || g.StartsWith("windscreen")) return "part";
        return null;
    }

    // ── Wheel child meshes ("front"/"rear" sets) ─────────────────────────

    private Dictionary<string, List<DrawableMeshExtractor.Part>> CollectWheelMeshes(YftFile yft)
    {
        var result = new Dictionary<string, List<DrawableMeshExtractor.Part>>(StringComparer.OrdinalIgnoreCase);
        var children = yft.Fragment?.PhysicsLODGroup?.PhysicsLOD1?.Children?.data_items;
        if (children == null) return result;

        foreach (var child in children)
        {
            var t = child.GetType();
            var groupName = t.GetProperty("GroupName")?.GetValue(child) as string
                ?? (t.GetProperty("Group")?.GetValue(child) is { } grp
                    ? grp.GetType().GetProperty("Name")?.GetValue(grp) as string : null);
            if (groupName == null || !groupName.StartsWith("wheel_", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.GetProperty("Drawable1")?.GetValue(child) is not FragDrawable fd) continue;
            var models = fd.DrawableModels?.High;
            if (models == null || models.Length == 0) continue;

            var list = new List<DrawableMeshExtractor.Part>();
            foreach (var model in models)
            {
                if (model?.Geometries == null) continue;
                for (int gi = 0; gi < model.Geometries.Length; gi++)
                {
                    var geom = model.Geometries[gi];
                    if (geom == null) continue;
                    var p = _extractor.ExtractGeometry(geom, model, gi, fd);
                    if (p != null) list.Add(p);
                }
            }
            if (list.Count == 0) continue;
            var slot = groupName.Contains("f", StringComparison.OrdinalIgnoreCase) ? "front" : "rear";
            if (!result.ContainsKey(slot)) result[slot] = list;
        }
        return result;
    }

    /// <summary>Copy a part translated to a bone position, optionally
    /// X-mirrored (right-side wheels reuse the left meshes).</summary>
    private static DrawableMeshExtractor.Part PlacePart(
        DrawableMeshExtractor.Part p, float x, float y, float z, bool mirror)
    {
        var pos = new float[p.Positions.Length];
        for (int i = 0; i < pos.Length; i += 3)
        {
            var px = mirror ? -p.Positions[i] : p.Positions[i];
            pos[i] = px + x;
            pos[i + 1] = p.Positions[i + 1] + y;
            pos[i + 2] = p.Positions[i + 2] + z;
        }
        float[]? nrm = null;
        if (p.Normals != null)
        {
            nrm = (float[])p.Normals.Clone();
            if (mirror) for (int i = 0; i < nrm.Length; i += 3) nrm[i] = -nrm[i];
        }
        var idx = p.Indices;
        if (mirror)
        {
            // Mirroring flips winding — restore it so faces stay outward.
            idx = new int[p.Indices.Length];
            for (int i = 0; i < idx.Length; i += 3)
            { idx[i] = p.Indices[i]; idx[i + 1] = p.Indices[i + 2]; idx[i + 2] = p.Indices[i + 1]; }
        }
        return p with { Positions = pos, Normals = nrm, Indices = idx };
    }

    // ── Payload serialization (workbench loadLod shape + groups) ─────────

    private static string BuildPayload(
        List<(DrawableMeshExtractor.Part Part, string Group, int Bone)> parts,
        List<GroupInfo> groups, int tris, bool frame, List<BoneInfo>? skeleton)
    {
        var sb = new StringBuilder(1 << 19);
        sb.Append("{\"lod\":\"HIGH\",\"paint\":true,\"tris\":").Append(tris)
          .Append(",\"frame\":").Append(frame ? "true" : "false");
        if (skeleton != null)
        {
            sb.Append(",\"skeleton\":[");
            for (int i = 0; i < skeleton.Count; i++)
            {
                var b = skeleton[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":\"").Append(JsonName(b.Name)).Append("\",\"tag\":").Append(b.Tag)
                  .Append(",\"index\":").Append(b.Index).Append(",\"parent\":").Append(b.ParentIndex)
                  .Append(",\"kind\":");
                if (b.Kind != null) sb.Append('"').Append(b.Kind).Append('"'); else sb.Append("null");
                sb.Append(",\"pos\":["); AppendFloats(sb, b.Pos); sb.Append("],\"rot\":["); AppendFloats(sb, b.Rot); sb.Append("]}");
            }
            sb.Append(']');
        }
        sb.Append(",\"groups\":[");
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":\"").Append(g.Name).Append("\",\"kind\":\"").Append(g.Kind)
              .Append("\",\"pivot\":[");
            AppendFloats(sb, g.Pivot);
            sb.Append("]}");
        }
        sb.Append("],\"parts\":[");
        bool first = true;
        foreach (var (part, group, bone) in parts)
        {
            if (!first) sb.Append(',');
            first = false;
            AppendPart(sb, part, group, skeleton != null ? bone : -1);
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string JsonName(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void AppendPart(StringBuilder sb, DrawableMeshExtractor.Part part, string group, int bone)
    {
        sb.Append("{\"group\":\"").Append(JsonName(group)).Append('"');
        if (bone >= 0) sb.Append(",\"bone\":").Append(bone);
        sb.Append(",\"positions\":["); AppendFloats(sb, part.Positions); sb.Append(']');
        sb.Append(",\"normals\":");
        if (part.Normals != null) { sb.Append('['); AppendFloats(sb, part.Normals); sb.Append(']'); } else sb.Append("null");
        sb.Append(",\"uvs\":");
        if (part.Uvs != null) { sb.Append('['); AppendFloats(sb, part.Uvs); sb.Append(']'); } else sb.Append("null");
        sb.Append(",\"indices\":[");
        for (int i = 0; i < part.Indices.Length; i++) { if (i > 0) sb.Append(','); sb.Append(part.Indices[i]); }
        sb.Append(']');
        sb.Append(",\"textureUrl\":");
        if (part.TextureFile != null) sb.Append('"').Append(part.TextureFile).Append('"'); else sb.Append("null");
        // Shader class the viewer renders by (PAINT/GLASS/EMISSIVE/DECAL/CHROME/
        // TEXTURED), the RAGE render bucket, and the paint colour for PAINT parts.
        sb.Append(",\"kind\":\"").Append(part.Kind).Append('"');
        sb.Append(",\"bucket\":").Append(part.Bucket);
        sb.Append(",\"paint\":").Append(part.PaintColor);
        sb.Append('}');
    }

    private static void AppendFloats(StringBuilder sb, float[] a)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(a[i].ToString("R", CultureInfo.InvariantCulture));
        }
    }
}
