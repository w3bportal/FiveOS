// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using CodeWalker.GameFiles;
using NQuaternion = System.Numerics.Quaternion;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Vector3 = System.Numerics.Vector3;

namespace YdrWriter;

/// <summary>
/// Animated prop from PER-BONE keys authored in the app's Animate mode
/// (Props ▸ Animated). The viewer derives a rigid skeleton from the model's
/// own node tree — one bone per part — and hands us, in the model's SOURCE
/// space (the loader's node space, before the user gizmo and the Y→Z swap):
///
///   • <c>skeleton</c>: every bone with its parent, root-relative rest
///     transform (rotation + position, unit scale) and the centroid / box of
///     each mesh it owns;
///   • <c>tracks</c>: for every keyed bone, the bone's ABSOLUTE local
///     transform per frame (what a .ycd track carries);
///   • <c>bbox</c>: the vertex bounds of the whole model in that space.
///
/// We apply the same global transform G the static pipeline baked into the
/// vertices, so the skeleton lands exactly on the geometry, bind every
/// DrawableModel (all LODs) to the bone whose mesh it matches, move each
/// model's vertices into its bone's space (the RAGE convention for rigidly
/// bound geometry — the engine renders <c>vertex × boneWorld</c>; vanilla
/// props bound to a bone metres from the origin keep their vertices centred
/// on zero), and write the clip with rotation + position tracks.
///
/// JSON shape (version 2):
/// <code>
/// { "version": 2, "fps": 30, "frames": 90, "duration": 3, "loop": true,
///   "bbox": { "min": [x,y,z], "max": [x,y,z] },
///   "skeleton": [ { "name": "root", "parent": -1, "wpos": [..], "wrot": [x,y,z,w],
///                   "meshes": [ { "c": [..], "min": [..], "max": [..] } ] }, ... ],
///   "tracks": [ { "bone": 1, "rot": [[x,y,z,w], ...], "pos": [[x,y,z], ...] } ] }
/// </code>
/// </summary>
internal static partial class AnimatedPropBuilder
{
    /// <summary>Rigidly bound geometry lives in BONE space in RAGE drawables.
    /// Kept as a switch (env <c>YDR_WRITER_KEEP_MODEL_SPACE=1</c>) so the old
    /// model-space output is one flip away if a build ever disagrees.</summary>
    internal static bool RebaseVerticesToBoneSpace { get; set; } =
        Environment.GetEnvironmentVariable("YDR_WRITER_KEEP_MODEL_SPACE") != "1";

    private sealed class KeyMesh
    {
        public Vector3 C, Min, Max;
    }

    private sealed class KeyBone
    {
        public string Name = "";
        public int Index, Parent = -1;
        public ushort Tag;
        public Vector3 WPos;            // rest, root-relative, viewer space
        public NQuaternion WRot = NQuaternion.Identity;
        public List<KeyMesh> Meshes = new();
        public Matrix4x4 WorldSrc;      // rest world in SOURCE space (aligned), TR only
        public Matrix4x4 World;         // rest world in YDR space (after G), TR only
        public Matrix4x4 Local;         // rest local in YDR space
        public Vector3[]? TrackPos;     // per-frame local (viewer space), null = unkeyed
        public NQuaternion[]? TrackRot;
    }

    internal static bool IsBoneKeysJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return r.ValueKind == JsonValueKind.Object
                && r.TryGetProperty("skeleton", out var sk) && sk.ValueKind == JsonValueKind.Array
                && r.TryGetProperty("tracks", out var tr) && tr.ValueKind == JsonValueKind.Array;
        }
        catch { return false; }
    }

    public static Result BuildFromBoneKeys(YdrFile ydr, ConvertOptions opts, string streamDir, string json)
    {
        var drawable = ydr.Drawable;
        var high = drawable?.DrawableModels?.High;
        if (drawable is null || high is null || high.Length == 0)
            return new Result(false, null, 0, 0, 0, 0, 0, "no drawable models to bind");

        // ---- 1. parse ---------------------------------------------------
        List<KeyBone> bones; int fps, frames; bool loop; Vector3 bbMin, bbMax; int keyed;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            fps = Math.Clamp(GetInt(r, "fps", 30), 1, 120);
            frames = Math.Max(2, GetInt(r, "frames", 2));
            loop = !r.TryGetProperty("loop", out var lp) || lp.ValueKind != JsonValueKind.False;
            bbMin = Vec3(r.GetProperty("bbox").GetProperty("min"));
            bbMax = Vec3(r.GetProperty("bbox").GetProperty("max"));

            bones = new List<KeyBone>();
            foreach (var b in r.GetProperty("skeleton").EnumerateArray())
            {
                var kb = new KeyBone
                {
                    Name = b.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "",
                    Index = bones.Count,
                    Parent = GetInt(b, "parent", -1),
                    WPos = b.TryGetProperty("wpos", out var wp) ? Vec3(wp) : Vector3.Zero,
                    WRot = b.TryGetProperty("wrot", out var wr) ? Quat(wr) : NQuaternion.Identity,
                };
                if (b.TryGetProperty("meshes", out var ms) && ms.ValueKind == JsonValueKind.Array)
                    foreach (var m in ms.EnumerateArray())
                        kb.Meshes.Add(new KeyMesh { C = Vec3(m.GetProperty("c")), Min = Vec3(m.GetProperty("min")), Max = Vec3(m.GetProperty("max")) });
                if (string.IsNullOrWhiteSpace(kb.Name)) kb.Name = kb.Index == 0 ? "root" : "bone_" + kb.Index;
                if (kb.Parent >= kb.Index) kb.Parent = kb.Index == 0 ? -1 : 0;   // must be pre-order
                bones.Add(kb);
            }
            if (bones.Count == 0) return new Result(false, null, 0, 0, 0, 0, 0, "anim-keys skeleton is empty");
            bones[0].Parent = -1;

            keyed = 0;
            foreach (var t in r.GetProperty("tracks").EnumerateArray())
            {
                int bi = GetInt(t, "bone", -1);
                if (bi < 0 || bi >= bones.Count) continue;
                if (!t.TryGetProperty("rot", out var rot) || rot.ValueKind != JsonValueKind.Array
                    || !t.TryGetProperty("pos", out var pos) || pos.ValueKind != JsonValueKind.Array)
                { Log($"  track for bone {bi} has no rot/pos arrays — skipped"); continue; }
                int rn = rot.GetArrayLength(), pn = pos.GetArrayLength();
                if (rn == 0 || pn == 0) continue;
                var kb = bones[bi];
                kb.TrackRot = new NQuaternion[frames]; kb.TrackPos = new Vector3[frames];
                for (int f = 0; f < frames; f++)
                {
                    kb.TrackRot[f] = NQuaternion.Normalize(Quat(rot[Math.Min(f, rn - 1)]));
                    kb.TrackPos[f] = Vec3(pos[Math.Min(f, pn - 1)]);
                }
                keyed++;
            }
        }
        catch (Exception ex)
        {
            return new Result(false, null, 0, 0, 0, 0, 0, "anim-keys parse failed: " + ex.Message);
        }
        if (keyed == 0) return new Result(false, null, bones.Count, 0, 0, 0, 0, "no keyed bones in anim-keys");
        if (bones.Count > 255)
            Log($"⚠ {bones.Count} bones but rigid bind addresses at most 256 (BoneIndex is a byte) — " +
                "parts on bones past 255 may render mis-attached.");

        // ---- 2. spaces ---------------------------------------------------
        // G = what the static pipeline baked into the vertices (user gizmo,
        // then Y→Z when the source is Y-up). The keys are in the viewer's
        // node space; the model's vertex bounds in both spaces give us the
        // similarity (uniform scale + offset) between them, which absorbs
        // any loader-side unit or centring difference.
        var scene = ImportRigged(opts.InputPath);
        if (scene is null) Log("  (re-import for the up-axis probe failed — assuming Y-up unless --up says otherwise)");
        var g = BuildGlobalTransform(opts, scene);
        {
            // RAGE bones carry no scale, so a NON-uniform gizmo scale can't live
            // in the skeleton: parts still land in place at rest, but a part
            // turned in the editor would look sheared in game.
            float sx = (float)(opts.Scale.X <= 0 ? 1 : opts.Scale.X), sy = (float)(opts.Scale.Y <= 0 ? 1 : opts.Scale.Y), sz = (float)(opts.Scale.Z <= 0 ? 1 : opts.Scale.Z);
            float smin = Math.Min(sx, Math.Min(sy, sz)), smax = Math.Max(sx, Math.Max(sy, sz));
            if (smin > 0 && smax / smin > 1.02f)
                Log($"⚠ non-uniform scale ({sx:F3}, {sy:F3}, {sz:F3}): bones can't carry it — moving parts may look stretched while turning. Use a uniform scale for animated props.");
        }
        if (!Matrix4x4.Invert(g, out var gInv))
            return new Result(false, null, bones.Count, 0, 0, 0, 0, "global transform is singular");

        // The loaders agree with Assimp for glTF/FBX/OBJ, so this is a safety
        // net for a UNIT mismatch only: it engages when every axis reports
        // the same ratio (a uniform scale) and that ratio is clearly not 1.
        // Anything else — a part hidden in Layers and excluded from the
        // export, say — shrinks one axis more than another and must NOT be
        // "corrected" into the wrong size.
        var (ydrMin, ydrMax) = DrawableVertexBounds(drawable);
        var (srcMin, srcMax) = TransformBox(ydrMin, ydrMax, gInv);
        var srcExt = srcMax - srcMin; var jsonExt = bbMax - bbMin;
        float scaleFix = 1f; var offsetFix = Vector3.Zero;
        {
            var ratios = new List<float>();
            for (int a = 0; a < 3; a++)
            {
                float se = a == 0 ? srcExt.X : a == 1 ? srcExt.Y : srcExt.Z;
                float je = a == 0 ? jsonExt.X : a == 1 ? jsonExt.Y : jsonExt.Z;
                if (se > 1e-4f && je > 1e-4f) ratios.Add(se / je);
            }
            if (ratios.Count > 0)
            {
                float rMin = ratios.Min(), rMax = ratios.Max(), rMean = ratios.Average();
                bool uniform = rMax / rMin < 1.05f;
                if (uniform && Math.Abs(rMean - 1f) > 0.02f)
                {
                    scaleFix = rMean;
                    offsetFix = (srcMin + srcMax) * 0.5f - ((bbMin + bbMax) * 0.5f) * scaleFix;
                    Log($"viewer space is a uniform ×{1f / scaleFix:F4} of the source — aligning keys (scale ×{scaleFix:F4}, offset ({offsetFix.X:F3},{offsetFix.Y:F3},{offsetFix.Z:F3}))");
                }
                else if (!uniform || Math.Abs(rMean - 1f) > 0.02f)
                {
                    Log($"viewer/export bounds differ per axis (ratios {string.Join("/", ratios.Select(r => r.ToString("F3")))}) — " +
                        "hidden parts? keys used as-is.");
                }
            }
        }
        Vector3 Align(Vector3 p) => p * scaleFix + offsetFix;

        // ---- 3. rest skeleton ----------------------------------------------
        // World rests: viewer → source (aligned) → YDR via G, then TR-only
        // (RAGE bones carry no scale; the vertices already have it baked in).
        foreach (var b in bones)
        {
            b.WorldSrc = TR(b.WRot, Align(b.WPos));
            b.World = TrOnly(b.WorldSrc * g);
            foreach (var m in b.Meshes) { m.C = Align(m.C); m.Min = Align(m.Min); m.Max = Align(m.Max); }
        }
        bones[0].WorldSrc = Matrix4x4.Identity;
        bones[0].World = TrOnly(g);
        foreach (var b in bones)
        {
            if (b.Parent < 0) { b.Local = b.World; continue; }
            b.Local = TrOnly(b.World * Invert(bones[b.Parent].World));
        }

        // ---- 4. tags -------------------------------------------------------
        var usedTags = new HashSet<ushort> { 0 };
        bones[0].Tag = 0;
        for (int i = 1; i < bones.Count; i++)
        {
            ushort tag = (ushort)(Joaat(bones[i].Name) & 0xFFFF);
            if (tag == 0) tag = 1;
            while (!usedTags.Add(tag)) tag = (ushort)(tag == 0xFFFF ? 1 : tag + 1);
            bones[i].Tag = tag;
        }

        // ---- 5. bind every model (all LODs) to a bone ----------------------
        // Match by centroid + box size in SOURCE space: the viewer's meshes
        // and Assimp's may split a part differently (by primitive vs by
        // material) but every piece of a part sits inside that part's box.
        var candidates = new List<(int Bone, KeyMesh M)>();
        foreach (var b in bones) foreach (var m in b.Meshes) candidates.Add((b.Index, m));
        if (candidates.Count == 0) return new Result(false, null, bones.Count, 0, 0, 0, 0, "skeleton owns no meshes");

        int bound = 0;
        var perBone = new int[bones.Count];
        foreach (var lod in new[] { drawable.DrawableModels?.High, drawable.DrawableModels?.Med, drawable.DrawableModels?.Low, drawable.DrawableModels?.VLow })
        {
            if (lod == null) continue;
            foreach (var model in lod)
            {
                if (model?.Geometries == null) continue;
                var (mMin, mMax) = ModelVertexBounds(model);
                var (sMin, sMax) = TransformBox(mMin, mMax, gInv);
                var c = (sMin + sMax) * 0.5f; var size = sMax - sMin;
                int best = 0; float bestScore = float.MaxValue;
                foreach (var (bi, m) in candidates)
                {
                    var dc = c - m.C; var ds = size - (m.Max - m.Min);
                    float score = Math.Abs(dc.X) + Math.Abs(dc.Y) + Math.Abs(dc.Z)
                                + 0.5f * (Math.Abs(ds.X) + Math.Abs(ds.Y) + Math.Abs(ds.Z));
                    if (score < bestScore) { bestScore = score; best = bi; }
                }
                // BoneIndex is a byte: a part whose bone sits past 255 is bound
                // (and rebased) to bone 255 so it still renders where it was.
                int bound255 = Math.Min(best, 255);
                model.BoneIndex = (byte)bound255;
                model.HasSkin = 0;
                if (ReferenceEquals(lod, high)) perBone[bound255]++;
                if (RebaseVerticesToBoneSpace) RebaseModel(model, bones[bound255].World);
                bound++;
            }
        }
        var binding = string.Join(", ", bones.Where(b => perBone[b.Index] > 0).Select(b => $"{b.Name}×{perBone[b.Index]}"));
        Log($"bound {bound} model(s): {binding}{(RebaseVerticesToBoneSpace ? " (vertices rebased to bone space)" : "")}");

        // ---- 6. attach the skeleton ---------------------------------------
        var nodes = bones.Select(b => new BoneNode { Name = b.Name, Index = b.Index, ParentIndex = b.Parent, Tag = b.Tag, Local = b.Local }).ToList();
        var skel = new Skeleton();
        skel.ReadXml(LoadXml(BuildSkeletonXml(nodes)).DocumentElement!);
        skel.BuildBonesMap();
        drawable.Skeleton = skel;

        // ---- 7. tracks ------------------------------------------------------
        // Compose the full chain per frame in source space, take it through
        // G at the world level, and read each keyed bone's local transform
        // back out of the YDR-space chain. Handles the root, the Y→Z swap
        // and any user scale in one place with no special cases. Local track
        // positions are parent-relative, so only the SCALE part of the
        // viewer→source alignment applies to them (the offset cancels).
        var worldF = new Matrix4x4[bones.Count];
        var worldSrcF = new Matrix4x4[bones.Count];
        var rotTracks = new Dictionary<int, NQuaternion[]>();
        var posTracks = new Dictionary<int, Vector3[]>();
        foreach (var b in bones)
            if (b.TrackRot != null) { rotTracks[b.Index] = new NQuaternion[frames]; posTracks[b.Index] = new Vector3[frames]; }
        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < bones.Count; i++)
            {
                var b = bones[i];
                Matrix4x4 localSrc;
                if (b.TrackRot != null)
                    localSrc = b.Parent < 0
                        ? TR(b.TrackRot[f], Align(b.TrackPos![f]))
                        : TR(b.TrackRot[f], b.TrackPos![f] * scaleFix);
                else
                    localSrc = b.Parent < 0 ? b.WorldSrc : b.WorldSrc * Invert(bones[b.Parent].WorldSrc);
                worldSrcF[i] = b.Parent < 0 ? localSrc : localSrc * worldSrcF[b.Parent];
                worldF[i] = TrOnly(worldSrcF[i] * g);
                if (b.TrackRot == null) continue;
                var localYdr = b.Parent < 0 ? worldF[i] : TrOnly(worldF[i] * Invert(worldF[b.Parent]));
                Matrix4x4.Decompose(localYdr, out _, out var q, out var t);
                rotTracks[i][f] = NQuaternion.Normalize(q);
                posTracks[i][f] = t;
            }
        }
        var tracks = new List<PosedBoneTrack>();
        var positions = new List<PosedPositionTrack>();
        foreach (var b in bones)
        {
            if (!rotTracks.TryGetValue(b.Index, out var rq)) continue;
            tracks.Add(new PosedBoneTrack(b.Tag, rq));
            Matrix4x4.Decompose(b.Local, out _, out _, out var restT);
            float dev = 0f;
            foreach (var v in posTracks[b.Index]) dev = Math.Max(dev, Vector3.Distance(v, restT));
            if (dev > 0.0005f) positions.Add(new PosedPositionTrack(b.Tag, posTracks[b.Index]));
        }
        if (tracks.Count == 0) return new Result(false, null, bones.Count, 0, 0, 0, bound, "no tracks sampled");

        // ---- 8. write --------------------------------------------------------
        var clipName = YcdAnimationBuilder.SanitizeClipName(opts.AssetName);
        System.IO.File.WriteAllBytes(
            System.IO.Path.Combine(streamDir, opts.AssetName + ".ydr"), ydr.Save());
        var ycdBytes = YcdAnimationBuilder.Build(clipName, tracks, frames, fps, positions.Count > 0 ? positions : null);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(streamDir, clipName + ".ycd"), ycdBytes);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(streamDir, clipName + ".ycd.xml"),
                YcdAnimationBuilder.BuildXml(clipName, tracks, frames, fps, positions.Count > 0 ? positions : null));
        }
        catch { /* the XML sidecar is a convenience only */ }
        RewriteYtypAutoStart(drawable, opts, streamDir, clipName);

        double durationSec = (double)frames / fps;
        Log($"animated prop (bone keys): {bones.Count} bones, {tracks.Count} keyed ({positions.Count} with travel), " +
            $"{frames} frames @ {fps}fps ({durationSec:F2}s{(loop ? ", loop" : "")}) -> {clipName}.ycd ({ycdBytes.Length:N0} bytes)");
        return new Result(true, clipName, bones.Count, tracks.Count, frames, durationSec, bound, null);
    }

    // ─────────────────────────── vertex rebase ─────────────────────

    // RAGE vertex declaration slots (fixed semantic order).
    private const int CompPosition = 0, CompNormal = 3, CompTangent0 = 14, CompTangent1 = 15;

    /// <summary>Move a model's vertices from model space into its bone's space
    /// (positions by the inverse bone world, normals/tangents by its inverse
    /// rotation). Geometry boxes are recomputed in the new space.</summary>
    internal static void RebaseModel(DrawableModel model, Matrix4x4 boneWorld)
    {
        if (!Matrix4x4.Invert(boneWorld, out var inv)) return;
        Matrix4x4.Decompose(boneWorld, out _, out var rot, out _);
        var rotInv = NQuaternion.Inverse(NQuaternion.Normalize(rot));
        var geoms = model.Geometries ?? Array.Empty<DrawableGeometry>();
        foreach (var geom in geoms)
        {
            var vd = geom?.VertexData; var info = vd?.Info;
            if (geom == null || vd == null || info == null) continue;
            int n = vd.VertexCount;
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            for (int i = 0; i < n; i++)
            {
                var p = vd.GetVector3(i, CompPosition);
                var q = Vector3.Transform(new Vector3(p.X, p.Y, p.Z), inv);
                vd.SetVector3(i, CompPosition, new SharpDX.Vector3(q.X, q.Y, q.Z));
                mn = Vector3.Min(mn, q); mx = Vector3.Max(mx, q);
            }
            RotateVectorComponent(vd, info, CompNormal, rotInv, n);
            RotateVectorComponent(vd, info, CompTangent0, rotInv, n);
            RotateVectorComponent(vd, info, CompTangent1, rotInv, n);
            if (n > 0)
                geom.AABB = new AABB_s { Min = new SharpDX.Vector4(mn.X, mn.Y, mn.Z, 0), Max = new SharpDX.Vector4(mx.X, mx.Y, mx.Z, 0) };
        }
        // Model-level bounds mirror the geometry boxes (one per geometry, plus
        // a combined box first when there are several) — CodeWalker's layout.
        try
        {
            var boxes = new List<AABB_s>();
            if (geoms.Length > 1)
            {
                var lo = new SharpDX.Vector4(float.MaxValue); var hi = new SharpDX.Vector4(float.MinValue);
                foreach (var ge in geoms) { lo = SharpDX.Vector4.Min(lo, ge.AABB.Min); hi = SharpDX.Vector4.Max(hi, ge.AABB.Max); }
                lo.W = 0; hi.W = 0;
                boxes.Add(new AABB_s { Min = lo, Max = hi });
            }
            foreach (var ge in geoms) boxes.Add(ge.AABB);
            if (model.BoundsData != null && model.BoundsData.Length == boxes.Count) model.BoundsData = boxes.ToArray();
        }
        catch { /* bounds are a culling hint; the rebased vertices are what matters */ }
    }

    private static void RotateVectorComponent(VertexData vd, VertexDeclaration info, int comp, NQuaternion rotInv, int count)
    {
        // The declaration names a type for every slot; only the slots whose
        // flag bit is set exist in the buffer. Touching an absent slot writes
        // through a bogus offset into the neighbouring vertices.
        if (((uint)info.Flags & (1u << comp)) == 0) return;
        var type = info.GetComponentType(comp);
        if (type == VertexComponentType.Float3)
        {
            for (int i = 0; i < count; i++)
            {
                var v = vd.GetVector3(i, comp);
                var r = Vector3.Transform(new Vector3(v.X, v.Y, v.Z), rotInv);
                vd.SetVector3(i, comp, new SharpDX.Vector3(r.X, r.Y, r.Z));
            }
        }
        else if (type == VertexComponentType.Float4)
        {
            for (int i = 0; i < count; i++)
            {
                var v = vd.GetVector4(i, comp);
                var r = Vector3.Transform(new Vector3(v.X, v.Y, v.Z), rotInv);
                vd.SetVector4(i, comp, new SharpDX.Vector4(r.X, r.Y, r.Z, v.W));
            }
        }
        else if (type != VertexComponentType.Nothing)
        {
            Log($"  (vertex component {comp} is {type}; left unrotated — shading only)");
        }
    }

    // ─────────────────────────── geometry helpers ──────────────────

    private static (Vector3 Min, Vector3 Max) DrawableVertexBounds(DrawableBase drawable)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var m in drawable.DrawableModels?.High ?? Array.Empty<DrawableModel>())
        {
            var (a, b) = ModelVertexBounds(m);
            mn = Vector3.Min(mn, a); mx = Vector3.Max(mx, b);
        }
        if (mn.X == float.MaxValue) { mn = Vector3.Zero; mx = Vector3.Zero; }
        return (mn, mx);
    }

    private static (Vector3 Min, Vector3 Max) ModelVertexBounds(DrawableModel model)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var g in model.Geometries ?? Array.Empty<DrawableGeometry>())
        {
            var vd = g?.VertexData; if (vd == null) continue;
            for (int i = 0; i < vd.VertexCount; i++)
            {
                var p = vd.GetVector3(i, CompPosition);
                var v = new Vector3(p.X, p.Y, p.Z);
                mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v);
            }
        }
        if (mn.X == float.MaxValue) { mn = Vector3.Zero; mx = Vector3.Zero; }
        return (mn, mx);
    }

    /// <summary>Axis-aligned box of a box's 8 corners after a transform.</summary>
    private static (Vector3 Min, Vector3 Max) TransformBox(Vector3 min, Vector3 max, Matrix4x4 m)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            var t = Vector3.Transform(c, m);
            mn = Vector3.Min(mn, t); mx = Vector3.Max(mx, t);
        }
        return (mn, mx);
    }

    /// <summary>Row-vector rotation-then-translation matrix.</summary>
    private static Matrix4x4 TR(NQuaternion q, Vector3 t)
        => Matrix4x4.CreateFromQuaternion(NQuaternion.Normalize(q)) * Matrix4x4.CreateTranslation(t);

    /// <summary>Drop the scale (and any shear) out of a matrix, keeping its
    /// rotation and translation.</summary>
    private static Matrix4x4 TrOnly(Matrix4x4 m)
    {
        if (Matrix4x4.Decompose(m, out _, out var q, out var t))
            return TR(q, t);
        // Degenerate scale: orthonormalise the basis by hand.
        var x = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
        var y = new Vector3(m.M21, m.M22, m.M23);
        y = Vector3.Normalize(y - x * Vector3.Dot(x, y));
        var z = Vector3.Cross(x, y);
        return new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, m.M41, m.M42, m.M43, 1);
    }

    private static Matrix4x4 Invert(Matrix4x4 m) => Matrix4x4.Invert(m, out var inv) ? inv : Matrix4x4.Identity;

    private static int GetInt(JsonElement e, string name, int def)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble()) : def;
    private static Vector3 Vec3(JsonElement a)
        => a.ValueKind == JsonValueKind.Array && a.GetArrayLength() >= 3
            ? new Vector3((float)a[0].GetDouble(), (float)a[1].GetDouble(), (float)a[2].GetDouble())
            : Vector3.Zero;
    private static NQuaternion Quat(JsonElement a)
        => a.ValueKind == JsonValueKind.Array && a.GetArrayLength() >= 4
            ? new NQuaternion((float)a[0].GetDouble(), (float)a[1].GetDouble(), (float)a[2].GetDouble(), (float)a[3].GetDouble())
            : NQuaternion.Identity;
}
