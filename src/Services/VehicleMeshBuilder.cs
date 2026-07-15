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
        int TexturedParts, int TotalParts);

    public BuildResult Build(
        string yftPath, bool frameCamera, float previewRatio = 1f, bool preserveBoundary = true,
        IReadOnlyDictionary<uint, Texture>? sharedTextures = null)
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

        var bones = drawable.Skeleton?.Bones?.Items;
        string BoneName(int i) => (bones != null && i >= 0 && i < bones.Length ? bones[i].Name : null) ?? "body";
        float[] BonePivot(string name)
        {
            var b = bones?.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return b != null ? new[] { b.Translation.X, b.Translation.Y, b.Translation.Z } : new[] { 0f, 0f, 0f };
        }

        var parts = new List<(DrawableMeshExtractor.Part Part, string Group)>();
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
                parts.Add((part, BoneName(GeometryBone(geom, model))));
                tris += part.Indices.Length / 3;
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
                    parts.Add((placed, bone.Name));
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

        int textured = parts.Count(p => p.Part.TextureFile != null);
        return new BuildResult(BuildPayload(parts, groups, tris, frameCamera),
            groups, tris, textured, parts.Count);
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

    /// <summary>Vehicle geometries are rigidly skinned: every vertex carries
    /// the same blend index, which selects into the geometry's BoneIds
    /// palette (verified: 1 distinct blend per geometry on real cars). The
    /// first vertex's blend byte (declaration component 2, "BlendIndices")
    /// therefore names the bone driving the whole geometry.</summary>
    private static int GeometryBone(DrawableGeometry geom, DrawableModel model)
    {
        try
        {
            var vd = geom.VertexData;
            var ids = geom.BoneIds;
            if (vd?.VertexBytes is { } bytes && vd.Info != null && ids is { Length: > 0 })
            {
                int off = vd.Info.GetComponentOffset(2);
                if (off >= 0 && bytes.Length > off)
                {
                    int blend = bytes[off];
                    if (blend < ids.Length) return ids[blend];
                }
                return ids[0];
            }
            if (ids is { Length: > 0 }) return ids[0];
        }
        catch { /* fall through to the model's bone */ }
        return model.BoneIndex;
    }

    /// <summary>Which sidebar toggle a group gets; null = always-on body.</summary>
    private static string? GroupKind(string group)
    {
        var g = group.ToLowerInvariant();
        if (g.StartsWith("door_dside")) return "door_l";
        if (g.StartsWith("door_pside")) return "door_r";
        if (g.StartsWith("bonnet")) return "bonnet";
        if (g.StartsWith("boot")) return "boot";
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
        List<(DrawableMeshExtractor.Part Part, string Group)> parts,
        List<GroupInfo> groups, int tris, bool frame)
    {
        var sb = new StringBuilder(1 << 19);
        sb.Append("{\"lod\":\"HIGH\",\"paint\":true,\"tris\":").Append(tris)
          .Append(",\"frame\":").Append(frame ? "true" : "false")
          .Append(",\"groups\":[");
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
        foreach (var (part, group) in parts)
        {
            if (!first) sb.Append(',');
            first = false;
            AppendPart(sb, part, group);
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static void AppendPart(StringBuilder sb, DrawableMeshExtractor.Part part, string group)
    {
        sb.Append("{\"group\":\"").Append(group).Append('"');
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
