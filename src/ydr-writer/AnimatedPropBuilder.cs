// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Assimp;
using CodeWalker.GameFiles;
using NQuaternion = System.Numerics.Quaternion;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Vector3 = System.Numerics.Vector3;
using Node = Assimp.Node;
using Scene = Assimp.Scene;
using Animation = Assimp.Animation;

namespace YdrWriter;

/// <summary>
/// Turns the static drawable the FbxConverter produced into a genuinely
/// animated prop, using RIGID bone-binding:
///
///   • A <see cref="Skeleton"/> is built from the source rig and attached
///     to the drawable (authored as XML → <c>Skeleton.ReadXml</c>, the
///     path CodeWalker/Sollumz use — no fragile resource-block hand-wiring).
///   • Each <see cref="DrawableModel"/> (FbxConverter emits one per source
///     mesh) is bound to a single bone via <c>DrawableModel.BoneIndex</c>.
///     The vertex buffers are left BYTE-FOR-BYTE untouched — no skinned
///     vertex-format surgery, so we can't hand RAGE's streamer a malformed
///     buffer. This is why rigid binding is the safe technique to ship.
///   • A <c>.ycd</c> is written keyed to the SAME bone tags as the
///     skeleton (via <see cref="YcdAnimationBuilder"/>), so a client
///     script can drive it with <c>PlayEntityAnim</c>.
///
/// Rigid binding animates per WHOLE PART: ideal for mechanical / hard-
/// surface models (robots, machinery, doors) whose parts are separate
/// meshes. A single skinned mesh that needs to bend at a joint would need
/// per-vertex skinning — out of scope here (and much riskier to ship blind).
///
/// Everything is best-effort: any failure returns <c>Success=false</c> so
/// the caller falls back to the plain static export instead of aborting.
/// </summary>
internal static class AnimatedPropBuilder
{
    public sealed record Result(
        bool Success,
        string? ClipName,
        int BoneCount,
        int AnimatedBoneCount,
        int Frames,
        double DurationSeconds,
        int BoundModels,
        string? Message);

    private static void Log(string s) => Console.WriteLine($"[ydr-writer]   {s}");

    /// <summary>One node promoted to a skeleton bone.</summary>
    private sealed class BoneNode
    {
        public required string Name;
        public int Index;
        public int ParentIndex = -1;
        public ushort Tag;
        public Matrix4x4 Local;        // parent-relative, row-vector (v*M) convention
        public Node AssimpNode = null!;
        public NodeAnimationChannel? Channel;
    }

    public static Result Build(YdrFile ydr, string inputPath, ConvertOptions opts, string streamDir)
    {
        var drawable = ydr.Drawable;
        var models = drawable?.DrawableModels?.High;
        if (drawable is null || models is null || models.Length == 0)
            return new Result(false, null, 0, 0, 0, 0, 0, "no drawable models to bind");

        Scene? scene = ImportRigged(inputPath);
        if (scene is null || !scene.HasMeshes)
            return new Result(false, null, 0, 0, 0, 0, 0, "re-import for rig failed");
        if (!scene.HasAnimations || scene.AnimationCount == 0)
            return new Result(false, null, 0, 0, 0, 0, 0, "source has no animation clip");

        // Global source→YDR transform G = R(Y→Z) · M(user gizmo). Matches
        // exactly what the static pipeline baked into the vertices, so the
        // skeleton bind pose lines up with the drawable geometry.
        var g = BuildGlobalTransform(opts, scene);

        // ---- 1. promote rig nodes to bones -----------------------------
        var anim = scene.Animations[0];
        var channelsByNode = new Dictionary<string, NodeAnimationChannel>(StringComparer.Ordinal);
        foreach (var ch in anim.NodeAnimationChannels)
            if (ch?.NodeName != null && ch.RotationKeyCount > 0)
                channelsByNode[ch.NodeName] = ch;

        // Bones that actually deform something (referenced by a mesh) or
        // are animated — plus every ancestor up to the scene root so the
        // hierarchy is a connected tree rooted at RootNode.
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mesh in scene.Meshes)
            if (mesh.HasBones)
                foreach (var b in mesh.Bones)
                    if (!string.IsNullOrEmpty(b.Name)) wanted.Add(b.Name);
        foreach (var n in channelsByNode.Keys) wanted.Add(n);
        if (wanted.Count == 0)
            return new Result(false, null, 0, 0, 0, 0, 0, "no deforming/animated bones found");

        var nodeByName = new Dictionary<string, Node>(StringComparer.Ordinal);
        IndexNodes(scene.RootNode, nodeByName);

        // Expand to include ancestors.
        var include = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in wanted)
        {
            var cur = nodeByName.TryGetValue(w, out var nd) ? nd : null;
            while (cur != null)
            {
                include.Add(cur.Name);
                cur = cur.Parent;
            }
        }

        // Pre-order traversal from root → stable indices, root == index 0.
        var bones = new List<BoneNode>();
        var boneIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        void Visit(Node node, int parentIdx)
        {
            int myIdx = parentIdx;
            if (include.Contains(node.Name))
            {
                var bn = new BoneNode
                {
                    Name = node.Name,
                    Index = bones.Count,
                    ParentIndex = parentIdx,
                    AssimpNode = node,
                    Local = ToRowMatrix(node.Transform),
                    Channel = channelsByNode.TryGetValue(node.Name, out var c) ? c : null,
                };
                boneIndexByName[node.Name] = bn.Index;
                bones.Add(bn);
                myIdx = bn.Index;
            }
            foreach (var child in node.Children) Visit(child, myIdx);
        }
        Visit(scene.RootNode, -1);
        if (bones.Count == 0)
            return new Result(false, null, 0, 0, 0, 0, 0, "no bones after hierarchy build");
        // DrawableModel.BoneIndex is a byte: bones past 255 cannot be addressed
        // by rigid binding at all (a RAGE-format limit, not just ours), so warn
        // rather than silently bind those parts to the wrong bone.
        if (bones.Count > 255)
            Log($"⚠ rig promoted {bones.Count} bones but rigid bind addresses at most 256 " +
                "(BoneIndex is a byte) — parts driven by bones beyond 255 may render mis-attached. " +
                "Simplify the rig if parts look wrong.");

        // Root bone folds in G so the whole skeleton sits in YDR space.
        bones[0].Local = bones[0].Local * g;

        // ---- 2. bone tags ---------------------------------------------
        var usedTags = new HashSet<ushort> { 0 };
        bones[0].Tag = 0; // SKEL_ROOT convention
        for (int i = 1; i < bones.Count; i++)
        {
            ushort tag = (ushort)(Joaat(bones[i].Name) & 0xFFFF);
            if (tag == 0) tag = 1;
            while (!usedTags.Add(tag)) tag = (ushort)(tag == 0xFFFF ? 1 : tag + 1);
            bones[i].Tag = tag;
        }

        // ---- 3. bind world transforms (for centroid→bone matching) -----
        var world = new Matrix4x4[bones.Count];
        for (int i = 0; i < bones.Count; i++)
            world[i] = bones[i].ParentIndex < 0
                ? bones[i].Local
                : bones[i].Local * world[bones[i].ParentIndex];
        var boneWorldPos = world.Select(m => new Vector3(m.M41, m.M42, m.M43)).ToArray();

        // ---- 4. dominant bone per source mesh --------------------------
        // Each source mesh → the bone with the most skin weight (or, if the
        // mesh isn't skinned, the nearest bone in the rig it hangs under).
        var meshBone = new int[scene.MeshCount];
        var meshCentroid = new Vector3[scene.MeshCount];
        var meshNodeWorld = BuildMeshNodeWorld(scene, g);
        for (int mi = 0; mi < scene.MeshCount; mi++)
        {
            var mesh = scene.Meshes[mi];
            meshCentroid[mi] = MeshCentroidZup(mesh, meshNodeWorld, mi);
            int dom = -1;
            if (mesh.HasBones)
            {
                var weightByBone = new Dictionary<int, double>();
                foreach (var b in mesh.Bones)
                {
                    if (!boneIndexByName.TryGetValue(b.Name ?? "", out var bidx)) continue;
                    double w = b.HasVertexWeights ? b.VertexWeights.Sum(v => v.Weight) : 0;
                    weightByBone[bidx] = (weightByBone.TryGetValue(bidx, out var e) ? e : 0) + w;
                }
                if (weightByBone.Count > 0)
                    dom = weightByBone.OrderByDescending(kv => kv.Value).First().Key;
            }
            if (dom < 0) dom = NearestBone(meshCentroid[mi], boneWorldPos);
            meshBone[mi] = Math.Max(0, dom);
        }

        // ---- 5. bind each DrawableModel to a bone ----------------------
        // Match model → source mesh by nearest centroid (robust to any
        // reordering between the PTV and rigged import passes).
        int bound = 0;
        for (int m = 0; m < models.Length; m++)
        {
            var model = models[m];
            var c = ModelCentroid(model);
            int nearestMesh = 0; float best = float.MaxValue;
            for (int mi = 0; mi < meshCentroid.Length; mi++)
            {
                float dsq = (meshCentroid[mi] - c).LengthSquared();
                if (dsq < best) { best = dsq; nearestMesh = mi; }
            }
            byte boneIdx = (byte)Math.Clamp(meshBone[nearestMesh], 0, 255);
            model.BoneIndex = boneIdx;
            model.HasSkin = 0;
            bound++;
        }

        // ---- 6. attach the skeleton ------------------------------------
        var skel = new Skeleton();
        skel.ReadXml(LoadXml(BuildSkeletonXml(bones)).DocumentElement!);
        skel.BuildBonesMap();
        drawable.Skeleton = skel;

        // ---- 7. sample the animation into a YCD keyed to our tags ------
        var (frames, fps, durationSec, ticksPerSecond) = ResolveTiming(anim);
        var tracks = new List<PosedBoneTrack>();
        var gRot = NQuaternion.CreateFromRotationMatrix(g);
        foreach (var bn in bones)
        {
            if (bn.Channel is null || bn.Channel.RotationKeyCount == 0) continue;
            var quats = SampleRotations(bn.Channel, frames, fps, ticksPerSecond);
            if (bn.Index == 0) // root carries the global orientation
                for (int f = 0; f < quats.Length; f++) quats[f] = NQuaternion.Normalize(gRot * quats[f]);
            tracks.Add(new PosedBoneTrack(bn.Tag, quats));
        }
        if (tracks.Count == 0)
            return new Result(false, null, bones.Count, 0, 0, 0, bound, "no rotation tracks sampled");

        // Name the .ycd + its anim dict after the ASSET (so it sits next to
        // <asset>.ydr / <asset>.ytyp as <asset>.ycd), NOT the source pack's
        // internal clip name — a name like "animalscrocodile11" is
        // meaningless and makes the clip look unrelated to the model. The
        // original clip name is kept only for the log line.
        var sourceClip = string.IsNullOrWhiteSpace(anim.Name) ? "(unnamed)" : anim.Name;
        var clipName = YcdAnimationBuilder.SanitizeClipName(opts.AssetName);
        // Persist the skeleton-bearing YDR FIRST — the .ycd + auto-start .ytyp
        // below only make sense atop a drawable that actually has the skeleton,
        // so commit it before them. If this throws, we bail here having written
        // neither, and the caller falls back to the earlier static YDR cleanly.
        System.IO.File.WriteAllBytes(
            System.IO.Path.Combine(streamDir, opts.AssetName + ".ydr"), ydr.Save());
        var ycdBytes = YcdAnimationBuilder.Build(clipName, tracks, frames, fps);
        var ycdPath = System.IO.Path.Combine(streamDir, clipName + ".ycd");
        System.IO.File.WriteAllBytes(ycdPath, ycdBytes);

        // Make the placed prop auto-play its clip with NO script: the ytyp
        // side of the Sollumz/CodeWalker animated-prop workflow.
        RewriteYtypAutoStart(drawable, opts, streamDir, clipName);

        Log($"animated prop: {bones.Count} bones, {tracks.Count} animated, {bound} model(s) bound, " +
            $"{frames} frames @ {fps}fps ({durationSec:F2}s), source clip '{sourceClip}' -> " +
            $"{System.IO.Path.GetFileName(ycdPath)} ({ycdBytes.Length:N0} bytes)");

        return new Result(true, clipName, bones.Count, tracks.Count, frames, durationSec, bound, null);
    }

    /// <summary>
    /// SYNTHESIZE a spin for a model that has NO animation of its own: attach
    /// a 2-bone skeleton (root + one rotation bone at the model's centroid),
    /// rigid-bind every model to the rotation bone, and write a .ycd that
    /// rotates that bone a full 360° around the chosen axis over
    /// <c>SpinSeconds</c>. Same rigid technique + auto-start ytyp as
    /// <see cref="Build"/> — the difference is the clip is generated here
    /// instead of imported. Mirrors the "auto create animation" workflow of
    /// the Sollumz-based prop tools (one rotation bone, whole mesh weighted
    /// to it, 0→360° keys) but produced natively — no Blender required.
    /// </summary>
    public static Result BuildAutoSpin(YdrFile ydr, ConvertOptions opts, string streamDir)
    {
        var drawable = ydr.Drawable;
        var models = drawable?.DrawableModels?.High;
        if (drawable is null || models is null || models.Length == 0)
            return new Result(false, null, 0, 0, 0, 0, 0, "no drawable models to spin");

        // Centroid of all drawable geometry, in YDR (Z-up) space — the pivot
        // the prop spins around. Component 0 is Position for every layout.
        var acc = Vector3.Zero; long n = 0;
        foreach (var m in models)
            foreach (var g in m.Geometries ?? Array.Empty<DrawableGeometry>())
            {
                var vd = g.VertexData; if (vd == null) continue;
                for (int i = 0; i < vd.VertexCount; i++)
                { var p = vd.GetVector3(i, 0); acc += new Vector3(p.X, p.Y, p.Z); n++; }
            }
        var centroid = n > 0 ? acc / n : Vector3.Zero;

        // 2-bone skeleton: root@origin (tag 0), rotation bone@centroid.
        ushort rotTag = (ushort)(Joaat(opts.AssetName + "_rotation") & 0xFFFF);
        if (rotTag == 0) rotTag = 1;
        var skel = new Skeleton();
        skel.ReadXml(LoadXml(BuildSpinSkeletonXml(centroid, rotTag)).DocumentElement!);
        skel.BuildBonesMap();
        drawable.Skeleton = skel;

        // Every model rides the rotation bone (index 1) rigidly.
        foreach (var m in models) { m.BoneIndex = 1; m.HasSkin = 0; }

        // Synthesize the 0→360° spin track at 30 fps. The last sampled frame
        // stops just short of 360° so a looped clip wraps seamlessly.
        const int fps = 30;
        double seconds = opts.SpinSeconds > 0.05 ? opts.SpinSeconds : 4.0;
        int frames = Math.Max(2, (int)Math.Round(seconds * fps));
        var axis = (opts.SpinAxis ?? "Z").ToUpperInvariant() switch
        {
            "X" => Vector3.UnitX,
            "Y" => Vector3.UnitY,
            _ => Vector3.UnitZ,
        };
        double dir = opts.SpinReverse ? -1.0 : 1.0;
        var quats = new NQuaternion[frames];
        for (int f = 0; f < frames; f++)
        {
            float ang = (float)(2.0 * Math.PI * dir * f / frames);
            quats[f] = NQuaternion.Normalize(NQuaternion.CreateFromAxisAngle(axis, ang));
        }

        var clipName = YcdAnimationBuilder.SanitizeClipName(opts.AssetName);
        // Persist the skeleton-bearing YDR before the .ycd/.ytyp (see Build).
        System.IO.File.WriteAllBytes(
            System.IO.Path.Combine(streamDir, opts.AssetName + ".ydr"), ydr.Save());
        var ycdBytes = YcdAnimationBuilder.Build(clipName,
            new List<PosedBoneTrack> { new(rotTag, quats) }, frames, fps);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(streamDir, clipName + ".ycd"), ycdBytes);

        RewriteYtypAutoStart(drawable, opts, streamDir, clipName);

        double durationSec = (double)frames / fps;
        Log($"auto-spin: axis {(opts.SpinAxis ?? "Z").ToUpperInvariant()}{(opts.SpinReverse ? " (reverse)" : "")}, " +
            $"{seconds:F1}s/rev, {frames} frames, {models.Length} model(s) bound, pivot " +
            $"({centroid.X:F2},{centroid.Y:F2},{centroid.Z:F2}) -> {clipName}.ycd ({ycdBytes.Length:N0} bytes)");
        return new Result(true, clipName, 2, 1, frames, durationSec, models.Length, null);
    }

    /// <summary>Rewrite the archetype .ytyp (if present) so the placed prop
    /// auto-plays the clip: clip-dictionary reference + Has Anim (512) +
    /// Auto Start Anim (524288) flags. Shared by the import + auto-spin paths.</summary>
    private static void RewriteYtypAutoStart(DrawableBase drawable, ConvertOptions opts, string streamDir, string clipName)
    {
        try
        {
            var ytypPath = System.IO.Path.Combine(streamDir, opts.AssetName + ".ytyp");
            if (System.IO.File.Exists(ytypPath))
            {
                var ytypBytes = YtypBuilder.Build(
                    opts.AssetName, drawable.BoundingBoxMin, drawable.BoundingBoxMax,
                    drawable.BoundingCenter, drawable.BoundingSphereRadius,
                    opts.LodDistVLow, clipDictionaryName: clipName, animated: true);
                System.IO.File.WriteAllBytes(ytypPath, ytypBytes);
                Log($"ytyp set to auto-start anim (clipDictionary '{clipName}', flags +512 +524288)");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ydr-writer] ytyp auto-start update failed (non-fatal): {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string BuildSpinSkeletonXml(Vector3 centroid, ushort rotTag)
    {
        string F3(float v) => v.ToString("0.#######", CultureInfo.InvariantCulture);
        var sb = new StringBuilder(1024);
        sb.AppendLine("<Skeleton>");
        sb.AppendLine("  <Unknown1C value=\"1178556674\" />");
        sb.AppendLine("  <Unknown50 value=\"0\" />");
        sb.AppendLine("  <Unknown54 value=\"0\" />");
        sb.AppendLine("  <Unknown58 value=\"0\" />");
        sb.AppendLine("  <Bones>");
        sb.AppendLine("    <Item>");
        sb.AppendLine("      <Name>root</Name>");
        sb.AppendLine("      <Tag value=\"0\" />");
        sb.AppendLine("      <Index value=\"0\" />");
        sb.AppendLine("      <ParentIndex value=\"-1\" />");
        sb.AppendLine("      <SiblingIndex value=\"-1\" />");
        sb.AppendLine("      <Flags />");
        sb.AppendLine("      <Translation x=\"0\" y=\"0\" z=\"0\" />");
        sb.AppendLine("      <Rotation x=\"0\" y=\"0\" z=\"0\" w=\"1\" />");
        sb.AppendLine("      <Scale x=\"1\" y=\"1\" z=\"1\" />");
        sb.AppendLine("      <TransformUnk x=\"0\" y=\"0\" z=\"0\" w=\"0\" />");
        sb.AppendLine("    </Item>");
        sb.AppendLine("    <Item>");
        sb.AppendLine("      <Name>spin</Name>");
        sb.AppendLine($"      <Tag value=\"{rotTag}\" />");
        sb.AppendLine("      <Index value=\"1\" />");
        sb.AppendLine("      <ParentIndex value=\"0\" />");
        sb.AppendLine("      <SiblingIndex value=\"-1\" />");
        sb.AppendLine("      <Flags />");
        sb.AppendLine($"      <Translation x=\"{F3(centroid.X)}\" y=\"{F3(centroid.Y)}\" z=\"{F3(centroid.Z)}\" />");
        sb.AppendLine("      <Rotation x=\"0\" y=\"0\" z=\"0\" w=\"1\" />");
        sb.AppendLine("      <Scale x=\"1\" y=\"1\" z=\"1\" />");
        sb.AppendLine("      <TransformUnk x=\"0\" y=\"0\" z=\"0\" w=\"0\" />");
        sb.AppendLine("    </Item>");
        sb.AppendLine("  </Bones>");
        sb.AppendLine("</Skeleton>");
        return sb.ToString();
    }

    // ─────────────────────────── import ────────────────────────────

    private static Scene? ImportRigged(string path)
    {
        try
        {
            using var ai = new AssimpContext();
            // No PreTransformVertices — that would flatten the rig away.
            return ai.ImportFile(path, PostProcessSteps.Triangulate);
        }
        catch { return null; }
    }

    // ─────────────────────────── geometry ──────────────────────────

    private static void IndexNodes(Node node, Dictionary<string, Node> map)
    {
        if (!string.IsNullOrEmpty(node.Name)) map[node.Name] = node;
        foreach (var c in node.Children) IndexNodes(c, map);
    }

    /// <summary>World transform (row-vector) of each mesh's owning node,
    /// pre-multiplied by G so mesh vertices land in YDR (Z-up) space.</summary>
    private static Matrix4x4[] BuildMeshNodeWorld(Scene scene, Matrix4x4 g)
    {
        var result = new Matrix4x4[scene.MeshCount];
        for (int i = 0; i < scene.MeshCount; i++) result[i] = g; // default: mesh at root
        void Walk(Node node, Matrix4x4 parentWorld)
        {
            var world = ToRowMatrix(node.Transform) * parentWorld;
            foreach (var mi in node.MeshIndices)
                if (mi >= 0 && mi < result.Length) result[mi] = world * g;
            foreach (var c in node.Children) Walk(c, world);
        }
        // parentWorld starts identity; G applied at the leaf so ordering is
        // world(source) then G, matching vertex baking order.
        Walk(scene.RootNode, Matrix4x4.Identity);
        return result;
    }

    private static Vector3 MeshCentroidZup(Assimp.Mesh mesh, Matrix4x4[] meshNodeWorld, int mi)
    {
        if (mesh.VertexCount == 0) return Vector3.Zero;
        var m = meshNodeWorld[mi];
        var acc = Vector3.Zero;
        foreach (var v in mesh.Vertices)
            acc += Vector3.Transform(new Vector3(v.X, v.Y, v.Z), m);
        return acc / mesh.VertexCount;
    }

    private static Vector3 ModelCentroid(DrawableModel model)
    {
        var acc = Vector3.Zero; long n = 0;
        foreach (var g in model.Geometries ?? Array.Empty<DrawableGeometry>())
        {
            var vd = g.VertexData;
            if (vd == null) continue;
            int count = vd.VertexCount;
            for (int i = 0; i < count; i++)
            {
                var p = vd.GetVector3(i, 0); // component 0 == Position for every GTAV layout
                acc += new Vector3(p.X, p.Y, p.Z);
                n++;
            }
        }
        return n > 0 ? acc / n : Vector3.Zero;
    }

    private static int NearestBone(Vector3 p, Vector3[] boneWorldPos)
    {
        int best = 0; float bestd = float.MaxValue;
        for (int i = 0; i < boneWorldPos.Length; i++)
        {
            float d = (boneWorldPos[i] - p).LengthSquared();
            if (d < bestd) { bestd = d; best = i; }
        }
        return best;
    }

    // ─────────────────────────── transforms ────────────────────────

    /// <summary>G = R(Y→Z) · M(user gizmo), in row-vector form, matching the
    /// order the static pipeline bakes into vertices (user TRS first, then
    /// Y-up→Z-up). Identity user transform collapses to just the axis swap.</summary>
    private static Matrix4x4 BuildGlobalTransform(ConvertOptions opts, Scene scene)
    {
        // User gizmo (three.js Y-up): M = T · R · S applied as v*S*R*T for
        // row vectors.
        var s = Matrix4x4.CreateScale(
            (float)(opts.Scale.X <= 0 ? 1 : opts.Scale.X),
            (float)(opts.Scale.Y <= 0 ? 1 : opts.Scale.Y),
            (float)(opts.Scale.Z <= 0 ? 1 : opts.Scale.Z));
        float rx = (float)(opts.Rotation.X * Math.PI / 180.0);
        float ry = (float)(opts.Rotation.Y * Math.PI / 180.0);
        float rz = (float)(opts.Rotation.Z * Math.PI / 180.0);
        // The static geometry pipeline (Converter.ApplyUserTransform) bakes the
        // column-vector product R = Rx·Ry·Rz applied p'=R·p. Its row-vector
        // (v·M) equivalent is the TRANSPOSE = CreateRotationZ·CreateRotationY·
        // CreateRotationX — reversed. Building it in Rx·Ry·Rz order here would
        // disagree with the baked geometry under any multi-axis gizmo rotation.
        var r = Matrix4x4.CreateRotationZ(rz) * Matrix4x4.CreateRotationY(ry) * Matrix4x4.CreateRotationX(rx);
        var t = Matrix4x4.CreateTranslation((float)opts.Position.X, (float)opts.Position.Y, (float)opts.Position.Z);
        var mUser = s * r * t;

        // Y-up → Z-up: (x,y,z) → (x,-z,y). Row-vector matrix.
        var yz = new Matrix4x4(
            1, 0, 0, 0,
            0, 0, 1, 0,
            0, -1, 0, 0,
            0, 0, 0, 1);

        bool yUp = ResolveYUp(opts, scene);
        return yUp ? mUser * yz : mUser;
    }

    private static bool ResolveYUp(ConvertOptions opts, Scene scene)
    {
        var up = (opts.Up ?? "auto").ToLowerInvariant();
        if (up == "y_up") return true;
        if (up == "z_up") return false;
        // auto: only FBX is ambiguous; glTF/OBJ/etc. are Y-up.
        var ext = System.IO.Path.GetExtension(opts.InputPath).ToLowerInvariant();
        if (ext == ".fbx" && scene.RootNode?.Metadata != null
            && scene.RootNode.Metadata.TryGetValue("UpAxis", out var entry)
            && entry.Data is int axis)
            return axis == 1;
        return true;
    }

    /// <summary>Assimp Matrix4x4 (column-vector M·v, row-major storage) →
    /// System.Numerics (row-vector v·M). This is a transpose.</summary>
    private static Matrix4x4 ToRowMatrix(Assimp.Matrix4x4 m) => new(
        m.A1, m.B1, m.C1, m.D1,
        m.A2, m.B2, m.C2, m.D2,
        m.A3, m.B3, m.C3, m.D3,
        m.A4, m.B4, m.C4, m.D4);

    // ─────────────────────────── skeleton xml ──────────────────────

    private static System.Xml.XmlDocument LoadXml(string xml)
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml);
        return doc;
    }

    private static string BuildSkeletonXml(List<BoneNode> bones)
    {
        // Sibling links: first child stored per parent, chained by
        // NextSiblingIndex. CW tolerates -1 but real links keep its
        // traversal helpers honest.
        var nextSibling = new int[bones.Count];
        Array.Fill(nextSibling, -1);
        var lastChildOf = new Dictionary<int, int>();
        for (int i = 0; i < bones.Count; i++)
        {
            int p = bones[i].ParentIndex;
            if (lastChildOf.TryGetValue(p, out var prev)) nextSibling[prev] = i;
            lastChildOf[p] = i;
        }

        var sb = new StringBuilder(8192);
        sb.AppendLine("<Skeleton>");
        sb.AppendLine("  <Unknown1C value=\"1178556674\" />");
        sb.AppendLine("  <Unknown50 value=\"0\" />");
        sb.AppendLine("  <Unknown54 value=\"0\" />");
        sb.AppendLine("  <Unknown58 value=\"0\" />");
        sb.AppendLine("  <Bones>");
        foreach (var b in bones)
        {
            Matrix4x4.Decompose(b.Local, out var scale, out var rot, out var trans);
            rot = NQuaternion.Normalize(rot);
            sb.AppendLine("    <Item>");
            sb.AppendLine($"      <Name>{Escape(b.Name)}</Name>");
            sb.AppendLine($"      <Tag value=\"{b.Tag}\" />");
            sb.AppendLine($"      <Index value=\"{b.Index}\" />");
            sb.AppendLine($"      <ParentIndex value=\"{b.ParentIndex}\" />");
            sb.AppendLine($"      <SiblingIndex value=\"{nextSibling[b.Index]}\" />");
            sb.AppendLine("      <Flags />");
            sb.AppendLine($"      <Translation x=\"{F(trans.X)}\" y=\"{F(trans.Y)}\" z=\"{F(trans.Z)}\" />");
            sb.AppendLine($"      <Rotation x=\"{F(rot.X)}\" y=\"{F(rot.Y)}\" z=\"{F(rot.Z)}\" w=\"{F(rot.W)}\" />");
            sb.AppendLine($"      <Scale x=\"{F(scale.X)}\" y=\"{F(scale.Y)}\" z=\"{F(scale.Z)}\" />");
            sb.AppendLine("      <TransformUnk x=\"0\" y=\"0\" z=\"0\" w=\"0\" />");
            sb.AppendLine("    </Item>");
        }
        sb.AppendLine("  </Bones>");
        sb.AppendLine("</Skeleton>");
        return sb.ToString();
    }

    // ─────────────────────────── animation ─────────────────────────

    private static (int frames, int fps, double durationSec, double ticksPerSecond) ResolveTiming(Animation anim, int fps = 30)
    {
        double ticksPerSecond = anim.TicksPerSecond > 0 ? anim.TicksPerSecond : 25.0;
        double maxKeyTicks = 0;
        foreach (var c in anim.NodeAnimationChannels)
            if (c?.RotationKeyCount > 0)
                maxKeyTicks = Math.Max(maxKeyTicks, c.RotationKeys[^1].Time);
        double durationTicks = Math.Max(anim.DurationInTicks, maxKeyTicks);
        if (durationTicks <= 0) durationTicks = ticksPerSecond;
        double durationSec = durationTicks / ticksPerSecond;
        // Assimp glTF quirk: key times land in MILLISECONDS with a bogus
        // TicksPerSecond. Treat ticks as ms (1000/sec) — and carry that
        // corrected rate out so EVERY channel shares one time base.
        if (durationSec > 150.0 && durationTicks / 1000.0 <= 150.0)
        {
            ticksPerSecond = 1000.0;
            durationSec = durationTicks / ticksPerSecond;
        }
        if (durationSec <= 0) durationSec = 1.0;
        if (durationSec > 120.0) durationSec = 120.0;
        // N unique frames = one loop period; Duration (emitted as frames/fps)
        // then matches. No +1: the clip loops, so frame at t=duration == f0.
        int frames = Math.Max(2, (int)Math.Round(durationSec * fps));
        return (frames, fps, durationSec, ticksPerSecond);
    }

    private static NQuaternion[] SampleRotations(NodeAnimationChannel ch, int frames, int fps, double ticksPerSecond)
    {
        // ONE shared tick->second scale for every channel (from ResolveTiming's
        // corrected rate). A per-channel scale derived from each track's own
        // last key would time-stretch any bone that ends before the global end,
        // desyncing bones that were authored to move together.
        var keys = ch.RotationKeys.OrderBy(k => k.Time).ToList();
        double secPerTick = ticksPerSecond > 0 ? 1.0 / ticksPerSecond : 0;
        var outq = new NQuaternion[frames];
        for (int f = 0; f < frames; f++)
        {
            double t = (double)f / fps;
            outq[f] = SampleAt(keys, t, secPerTick);
        }
        return outq;
    }

    private static NQuaternion SampleAt(List<QuaternionKey> keys, double tSec, double scale)
    {
        if (keys.Count == 0) return NQuaternion.Identity;
        if (keys.Count == 1) return Q(keys[0].Value);
        for (int i = 1; i < keys.Count; i++)
        {
            double t1 = keys[i].Time * scale;
            if (tSec <= t1)
            {
                double t0 = keys[i - 1].Time * scale;
                double span = t1 - t0;
                if (span <= 0) return Q(keys[i].Value);
                float u = (float)Math.Clamp((tSec - t0) / span, 0, 1);
                return NQuaternion.Slerp(Q(keys[i - 1].Value), Q(keys[i].Value), u);
            }
        }
        return Q(keys[^1].Value);
    }

    private static NQuaternion Q(Assimp.Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    // ─────────────────────────── misc ──────────────────────────────

    private static uint Joaat(string s)
    {
        uint h = 0;
        foreach (var c in s)
        {
            h += (byte)char.ToLowerInvariant(c);
            h += h << 10; h ^= h >> 6;
        }
        h += h << 3; h ^= h >> 11; h += h << 15;
        return h;
    }

    private static string F(float v) => v.ToString("0.#######", CultureInfo.InvariantCulture);
    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
