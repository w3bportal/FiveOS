// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Assimp;
using Q = System.Numerics.Quaternion;
using V = System.Numerics.Vector3;

namespace FiveOS.Services;

/// <summary>
/// Imports a physics-based mocap JSON clip (per-frame joint EULER rotations +
/// world positions + joint torques + ground reaction_forces) and prepares it for
/// the shared GTA retarget. Advantages over an FBX source:
///  • labelled joints (no bone-name heuristics) — see <see cref="JointToGta"/>;
///  • explicit world positions ⇒ ARMS are aim-solved from shoulder→elbow→wrist
///    instead of roll-ambiguous rotation transfer (fixes the twisted-forearm look);
///  • per-toe reaction_forces ⇒ EXACT foot-plant, not the heuristic in GroundToFeet.
/// </summary>
public static class PhysicsMocapImporter
{
    /// <summary>Physics-rig joint name → GTA bone name. "Root" also drives root
    /// motion from its position track; the *_shoulder_rotation joints are the
    /// upper-arm (this rig has no separate clavicle).</summary>
    public static readonly IReadOnlyDictionary<string, string> JointToGta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Root"]                     = "SKEL_Pelvis",
        ["Left_hip"]                 = "SKEL_L_Thigh",
        ["Left_knee"]                = "SKEL_L_Calf",
        ["Left_ankle"]               = "SKEL_L_Foot",
        ["Left_toe"]                 = "SKEL_L_Toe0",
        ["Right_hip"]                = "SKEL_R_Thigh",
        ["Right_knee"]               = "SKEL_R_Calf",
        ["Right_ankle"]              = "SKEL_R_Foot",
        ["Right_toe"]                = "SKEL_R_Toe0",
        ["Spine1"]                   = "SKEL_Spine0",
        ["Spine2"]                   = "SKEL_Spine2",
        ["Neck"]                     = "SKEL_Neck_1",
        ["Left_shoulder_rotation"]   = "SKEL_L_UpperArm",
        ["Left_elbow"]               = "SKEL_L_Forearm",
        ["Left_wrist"]               = "SKEL_L_Hand",
        ["Right_shoulder_rotation"]  = "SKEL_R_UpperArm",
        ["Right_elbow"]              = "SKEL_R_Forearm",
        ["Right_wrist"]              = "SKEL_R_Hand",
    };

    /// <summary>Joint → parent joint (the biomechanical hierarchy, read off the
    /// names + the frame-0 positions). "Root" has no parent.</summary>
    public static readonly IReadOnlyDictionary<string, string?> JointParent = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["Root"]                     = null,
        ["Left_hip"]                 = "Root",
        ["Left_knee"]                = "Left_hip",
        ["Left_ankle"]               = "Left_knee",
        ["Left_toe"]                 = "Left_ankle",
        ["Right_hip"]                = "Root",
        ["Right_knee"]               = "Right_hip",
        ["Right_ankle"]              = "Right_knee",
        ["Right_toe"]                = "Right_ankle",
        ["Spine1"]                   = "Root",
        ["Spine2"]                   = "Spine1",
        ["Neck"]                     = "Spine2",
        ["Left_shoulder_rotation"]   = "Spine2",
        ["Left_elbow"]               = "Left_shoulder_rotation",
        ["Left_wrist"]               = "Left_elbow",
        ["Right_shoulder_rotation"]  = "Spine2",
        ["Right_elbow"]              = "Right_shoulder_rotation",
        ["Right_wrist"]              = "Right_elbow",
    };

    /// <summary>The arm chains — shoulder→elbow→wrist — that get AIM-solved from
    /// the explicit joint positions to kill the FBX roll-twist artefact.</summary>
    public static readonly (string shoulder, string elbow, string wrist)[] ArmChains =
    {
        ("Left_shoulder_rotation",  "Left_elbow",  "Left_wrist"),
        ("Right_shoulder_rotation", "Right_elbow", "Right_wrist"),
    };

    /// <summary>Joint → the child whose segment defines its aim direction, for
    /// the position-driven rotation solve in <see cref="BuildScene"/>.</summary>
    private static readonly IReadOnlyDictionary<string, string> AimChild = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Left_hip"]                 = "Left_knee",
        ["Left_knee"]                = "Left_ankle",
        ["Left_ankle"]               = "Left_toe",
        ["Right_hip"]                = "Right_knee",
        ["Right_knee"]               = "Right_ankle",
        ["Right_ankle"]              = "Right_toe",
        ["Spine1"]                   = "Spine2",
        ["Spine2"]                   = "Neck",
        ["Left_shoulder_rotation"]   = "Left_elbow",
        ["Left_elbow"]               = "Left_wrist",
        ["Right_shoulder_rotation"]  = "Right_elbow",
        ["Right_elbow"]              = "Right_wrist",
    };

    /// <summary>Mixamo-style node names so <see cref="AnimRetarget"/> +
    /// <see cref="GtaBoneTags"/> map them without special-casing.</summary>
    private static readonly IReadOnlyDictionary<string, string> JointToMixamo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Root"]                     = "Hips",
        ["Left_hip"]                 = "LeftUpLeg",
        ["Left_knee"]                = "LeftLeg",
        ["Left_ankle"]               = "LeftFoot",
        ["Left_toe"]                 = "LeftToeBase",
        ["Right_hip"]                = "RightUpLeg",
        ["Right_knee"]               = "RightLeg",
        ["Right_ankle"]              = "RightFoot",
        ["Right_toe"]                = "RightToeBase",
        ["Spine1"]                   = "Spine",
        // Chest joint (parents the shoulders) → "Spine2" → SKEL_Spine2, matching
        // the user's custom-bone-map (mixamorig:spine2 / spine1_jnt → skel_spine2).
        // "Spine1" landed it on SKEL_Spine1, one bone too low in the torso.
        ["Spine2"]                   = "Spine2",
        ["Neck"]                     = "Neck",
        ["Left_shoulder_rotation"]   = "LeftArm",
        ["Left_elbow"]               = "LeftForeArm",
        ["Left_wrist"]               = "LeftHand",
        ["Right_shoulder_rotation"]  = "RightArm",
        ["Right_elbow"]              = "RightForeArm",
        ["Right_wrist"]              = "RightHand",
    };

    /// <summary>Synthetic-scene node name for a physics joint. An explicit user
    /// mapping in custom-bone-map.json wins — the joint is emitted under the
    /// canonical SKEL_* name, which always resolves and beats every heuristic.
    /// Otherwise the Mixamo-style alias from <see cref="JointToMixamo"/>.</summary>
    private static string? NodeNameFor(string joint)
    {
        if (GtaBoneTags.TryResolveCustom(joint, out var t) && GtaBoneTags.NameForTag(t) is { } skel)
            return skel;
        return JointToMixamo.TryGetValue(joint, out var mix) ? mix : null;
    }

    public static bool LooksLikePhysicsMocap(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            // Peek the start — full physics dumps are huge.
            using var fs = File.OpenRead(path);
            var buf = new byte[Math.Min(4096, (int)fs.Length)];
            int n = fs.Read(buf, 0, buf.Length);
            var head = System.Text.Encoding.UTF8.GetString(buf, 0, n);
            return head.Contains("mocap_data", StringComparison.OrdinalIgnoreCase)
                   && head.Contains("joint_data", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Import a physics mocap JSON into the same result shape as
    /// <see cref="AnimEmoteImporter"/> so the Pose→Emote timeline path is shared.</summary>
    public static AnimEmoteImporter.Result Import(string path, int fps = 30)
    {
        var warnings = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return AnimEmoteImporter.Result.Fail($"File not found: {path}");

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("mocap_data", out var mocap)
                || mocap.ValueKind != JsonValueKind.Array || mocap.GetArrayLength() == 0)
                return AnimEmoteImporter.Result.Fail("No mocap_data array in this JSON.");

            var frames = new List<Frame>();
            foreach (var entry in mocap.EnumerateArray())
            {
                if (!entry.TryGetProperty("joint_data", out var jd) || jd.ValueKind != JsonValueKind.Object)
                    continue;
                double t = 0;
                if (entry.TryGetProperty("timestamp", out var ts))
                    t = ParseDouble(ts);
                var joints = new Dictionary<string, JointSample>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in jd.EnumerateObject())
                {
                    if (!JointToMixamo.ContainsKey(prop.Name)) continue;
                    if (!TryReadJoint(prop.Value, out var sample)) continue;
                    joints[prop.Name] = sample;
                }
                if (joints.Count > 0) frames.Add(new Frame(t, joints));
            }
            if (frames.Count < 2)
                return AnimEmoteImporter.Result.Fail("Physics mocap needs at least 2 frames with joint_data.");

            // Vision-based mocap commonly labels Left/Right from the CAMERA's
            // view, i.e. anatomically swapped. Detect it from the data: knees
            // bend forward, so up×forward gives the subject's anatomical right —
            // if the labelled Right hip sits on the anatomical LEFT, the labels
            // are mirrored. Swapping here uncrosses the legs and fixes the
            // facing-derived Rc (mirrored shoulder line yaw-flips it 180°, which
            // aimed the arms BEHIND the back).
            if (DetectMirroredLabels(frames))
            {
                SwapLeftRight(frames);
                warnings.Add("Physics mocap: Left/Right joint labels are camera-mirrored — swapped to anatomical L/R.");
            }

            // Infer fps from timestamps when the header doesn't force one.
            if (int.TryParse(Environment.GetEnvironmentVariable("FIVEOS_IMPORT_FPS"), out var envFps) && envFps > 0)
                fps = Math.Clamp(envFps, 1, 120);
            else
            {
                double dt = frames[^1].Time - frames[0].Time;
                if (dt > 1e-6)
                {
                    int inferred = (int)Math.Round((frames.Count - 1) / dt);
                    if (inferred is >= 12 and <= 120) fps = inferred;
                }
            }

            var scene = BuildScene(frames, out double tps, out double durationSec, warnings);
            if (scene.AnimationCount == 0)
                return AnimEmoteImporter.Result.Fail("Failed to build animation from physics mocap.");

            int sampleFrames = Math.Max(2, (int)Math.Round(durationSec * fps) + 1);
            const int MaxImportFrames = 1801;
            if (sampleFrames > MaxImportFrames && durationSec > 0)
            {
                fps = Math.Max(1, (int)Math.Floor((MaxImportFrames - 1) / durationSec));
                sampleFrames = Math.Max(2, (int)Math.Round(durationSec * fps) + 1);
                warnings.Add($"Physics clip thinned to {fps} fps ({MaxImportFrames}-frame cap).");
            }

            var anim = scene.Animations[0];
            var rt = AnimRetarget.Retarget(scene, anim, tps, sampleFrames, fps,
                out var mapped, out var unmapped, out var rootMotion, warnings);
            if (rt == null || rt.Count == 0)
                return AnimEmoteImporter.Result.Fail("Retarget produced no bone tracks from physics mocap.");

            warnings.Add("Physics mocap JSON — retargeted onto the GTA skeleton (arms aim-solved from joint positions).");
            var clipName = Path.GetFileNameWithoutExtension(path);
            return new AnimEmoteImporter.Result(
                true, clipName, sampleFrames, fps, durationSec,
                AnimEmoteImporter.RigKind.Mixamo, true,
                rt, mapped, unmapped, warnings, null, rootMotion);
        }
        catch (Exception ex)
        {
            return AnimEmoteImporter.Result.Fail($"Physics mocap import failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Convert one joint's typed euler rotation to a quaternion. The <paramref
    /// name="type"/> is like "euler_xyz" / "euler_zyx" / "euler_x": the letters
    /// after "euler_" name the axes IN COMPOSITION ORDER, one angle (radians) each.
    /// Composed EXTRINSICALLY (each rotation about the fixed parent axes):
    /// q = q_c · q_b · q_a. Verified against the file's own joint positions — the
    /// extrinsic form predicts them markedly better than intrinsic (arms 34° vs 59°).
    /// (Legs/arms are additionally aim-solved from positions in AnimRetarget, so
    /// this convention mainly governs the spine/neck.)
    /// </summary>
    public static Q EulerToQuat(string type, IReadOnlyList<double> values)
    {
        if (string.IsNullOrEmpty(type) || !type.StartsWith("euler_", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unsupported rotation type '{type}'.", nameof(type));
        var axes = type.Substring("euler_".Length).ToLowerInvariant();
        if (values.Count != axes.Length)
            throw new ArgumentException($"Rotation type '{type}' expects {axes.Length} angle(s), got {values.Count}.");

        var q = Q.Identity;
        for (int i = 0; i < axes.Length; i++)
        {
            var axis = axes[i] switch
            {
                'x' => V.UnitX,
                'y' => V.UnitY,
                'z' => V.UnitZ,
                _ => throw new ArgumentException($"Unknown euler axis '{axes[i]}' in '{type}'."),
            };
            q = Q.CreateFromAxisAngle(axis, (float)values[i]) * q;   // extrinsic compose (verified against positions)
        }
        return Q.Normalize(q);
    }

    private readonly record struct JointSample(V Position, Q LocalRot);
    private readonly record struct Frame(double Time, Dictionary<string, JointSample> Joints);

    private static bool TryReadJoint(JsonElement el, out JointSample sample)
    {
        sample = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty("position", out var posEl) || posEl.ValueKind != JsonValueKind.Array
            || posEl.GetArrayLength() < 3)
            return false;
        var pos = new V((float)ParseDouble(posEl[0]), (float)ParseDouble(posEl[1]), (float)ParseDouble(posEl[2]));

        string type = "euler_xyz";
        if (el.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
            type = typeEl.GetString() ?? type;

        if (!el.TryGetProperty("rotations", out var rotEl) || rotEl.ValueKind != JsonValueKind.Array)
            return false;
        var angles = new List<double>();
        foreach (var a in rotEl.EnumerateArray()) angles.Add(ParseDouble(a));
        try
        {
            sample = new JointSample(pos, EulerToQuat(type, angles));
            return true;
        }
        catch { return false; }
    }

    /// <summary>True when the file's Left_*/Right_* labels are camera-mirrored.
    /// Signal: knees bend forward, so anatomical right = up × knee-forward; if
    /// the labelled Right hip accumulates on the anatomical LEFT across the clip,
    /// the labels are viewer-relative. Frames with knees under hips (no bend)
    /// carry no signal and are skipped.</summary>
    private static bool DetectMirroredLabels(List<Frame> frames)
    {
        double acc = 0;
        foreach (var fr in frames)
        {
            var j = fr.Joints;
            if (!j.TryGetValue("Root", out var root) || !j.TryGetValue("Neck", out var neck)
                || !j.TryGetValue("Left_hip", out var lh) || !j.TryGetValue("Right_hip", out var rh)
                || !j.TryGetValue("Left_knee", out var lk) || !j.TryGetValue("Right_knee", out var rk))
                continue;
            var up = neck.Position - root.Position;
            if (up.LengthSquared() < 1e-8f) continue;
            up = V.Normalize(up);
            var fwd = (lk.Position + rk.Position) * 0.5f - (lh.Position + rh.Position) * 0.5f;
            fwd -= up * V.Dot(fwd, up);
            if (fwd.LengthSquared() < 1e-6f) continue;
            var right = V.Cross(up, V.Normalize(fwd));
            acc += V.Dot(right, rh.Position - lh.Position);
        }
        return acc < 0;
    }

    private static void SwapLeftRight(List<Frame> frames)
    {
        for (int i = 0; i < frames.Count; i++)
        {
            var src = frames[i].Joints;
            var dst = new Dictionary<string, JointSample>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in src)
            {
                var n = kv.Key;
                var swapped = n.StartsWith("Left_", StringComparison.OrdinalIgnoreCase) ? "Right_" + n.Substring(5)
                            : n.StartsWith("Right_", StringComparison.OrdinalIgnoreCase) ? "Left_" + n.Substring(6)
                            : n;
                dst[swapped] = kv.Value;
            }
            frames[i] = frames[i] with { Joints = dst };
        }
    }

    /// <summary>Shortest-arc rotation taking direction a to direction b.</summary>
    private static Q FromTo(V a, V b)
    {
        a = V.Normalize(a); b = V.Normalize(b);
        float d = V.Dot(a, b);
        if (d > 0.99999f) return Q.Identity;
        if (d < -0.99999f)
        {
            var ax = V.Cross(V.UnitX, a);
            if (ax.LengthSquared() < 1e-6f) ax = V.Cross(V.UnitY, a);
            return Q.CreateFromAxisAngle(V.Normalize(ax), (float)Math.PI);
        }
        var c = V.Cross(a, b);
        return Q.Normalize(new Q(c.X, c.Y, c.Z, 1 + d));
    }

    /// <summary>Orthonormal basis (side, up) → rotation quaternion. Used to solve
    /// the Root's full orientation from the hip line + spine direction.</summary>
    private static Q BasisQuat(V side, V up)
    {
        var y = V.Normalize(up);
        var x = side - y * V.Dot(side, y);
        x = x.LengthSquared() < 1e-10f ? V.UnitX : V.Normalize(x);
        var z = V.Cross(x, y);
        var m = new System.Numerics.Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        return Q.Normalize(Q.CreateFromRotationMatrix(m));
    }

    private static double ParseDouble(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0,
        _ => 0,
    };

    private static Scene BuildScene(List<Frame> frames, out double tps, out double durationSec, List<string> warnings)
    {
        // Assimp ticks = milliseconds so duration math matches the glTF path.
        tps = 1000.0;
        durationSec = Math.Max(1e-3, frames[^1].Time - frames[0].Time);
        var f0 = frames[0];

        // Rest bind: parent-relative translation from frame-0 WORLD positions,
        // identity rotation. Animation channels carry the full local euler quat
        // (AnimRetarget's full-local vote will pick this up).
        var restLocalPos = new Dictionary<string, V>(StringComparer.OrdinalIgnoreCase);
        foreach (var joint in JointToMixamo.Keys)
        {
            if (!f0.Joints.TryGetValue(joint, out var sample)) continue;
            JointParent.TryGetValue(joint, out var parent);
            if (parent != null && f0.Joints.TryGetValue(parent, out var pSample))
                restLocalPos[joint] = sample.Position - pSample.Position;
            else
                restLocalPos[joint] = sample.Position;
        }

        var scene = new Scene { RootNode = new Node("RootNode") };
        var nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);

        // Create nodes depth-first so parents exist before children.
        void EnsureNode(string joint)
        {
            if (nodes.ContainsKey(joint)) return;
            var mixName = NodeNameFor(joint);
            if (mixName == null) return;
            JointParent.TryGetValue(joint, out var parent);
            if (parent != null) EnsureNode(parent);
            var node = new Node(mixName);
            var lp = restLocalPos.TryGetValue(joint, out var p) ? p : V.Zero;
            // Assimp Matrix4x4 is row-major; translation in D1/D2/D3.
            node.Transform = new Assimp.Matrix4x4(
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0,
                lp.X, lp.Y, lp.Z, 1);
            nodes[joint] = node;
            if (parent != null && nodes.TryGetValue(parent, out var pNode))
                pNode.Children.Add(node);
            else
                scene.RootNode.Children.Add(node);
        }
        foreach (var j in JointToMixamo.Keys) EnsureNode(j);

        var anim = new Animation
        {
            Name = "physics_mocap",
            TicksPerSecond = tps,
            DurationInTicks = durationSec * tps,
        };

        // ── Position-driven rotation solve ──────────────────────────────────
        // The JSON's world POSITIONS are ground truth; its euler rotations carry
        // ~24–34° of error against those positions regardless of intrinsic vs
        // extrinsic reading (measured with the physcheck harness) — enough to
        // scissor the legs and pull the arms behind the back. So each joint's
        // per-frame WORLD rotation is solved from its own segment direction
        // (frame-0 → frame-f shortest arc; the bind is identity so world = delta),
        // and the Root gets a full basis from hips + spine. FK of these rotations
        // reproduces the file's positions by construction, so everything
        // downstream (world-delta transfer, aim-solves, root motion) sees truth.
        // Childless end joints (wrist / toe / neck) keep their euler quats — the
        // only joints where the (small) euler values are still used.
        // FIVEOS_PHYS_EULER=1 reverts to raw euler channels for A/B.
        bool eulerOnly = Environment.GetEnvironmentVariable("FIVEOS_PHYS_EULER") == "1";

        var order = new List<string>();
        void AddOrdered(string j)
        {
            if (order.Contains(j)) return;
            if (JointParent.TryGetValue(j, out var pp) && pp != null) AddOrdered(pp);
            order.Add(j);
        }
        foreach (var j in JointToMixamo.Keys) AddOrdered(j);

        var f0j = f0.Joints;
        var locals = order.ToDictionary(j => j, _ => new Q[frames.Count], StringComparer.OrdinalIgnoreCase);
        for (int fi = 0; fi < frames.Count; fi++)
        {
            var fj = frames[fi].Joints;
            var world = new Dictionary<string, Q>(StringComparer.OrdinalIgnoreCase);
            foreach (var j in order)
            {
                JointParent.TryGetValue(j, out var par);
                Q parentW = par != null && world.TryGetValue(par, out var pw) ? pw : Q.Identity;
                Q w; bool solved = false;
                if (!eulerOnly)
                {
                    if (j.Equals("Root", StringComparison.OrdinalIgnoreCase)
                        && f0j.TryGetValue("Left_hip", out var lh0) && f0j.TryGetValue("Right_hip", out var rh0)
                        && f0j.TryGetValue("Spine1", out var sp0) && f0j.TryGetValue("Root", out var rt0)
                        && fj.TryGetValue("Left_hip", out var lhf) && fj.TryGetValue("Right_hip", out var rhf)
                        && fj.TryGetValue("Spine1", out var spf) && fj.TryGetValue("Root", out var rtf))
                    {
                        var q0 = BasisQuat(lh0.Position - rh0.Position, sp0.Position - rt0.Position);
                        var qf = BasisQuat(lhf.Position - rhf.Position, spf.Position - rtf.Position);
                        w = Q.Normalize(qf * Q.Inverse(q0));
                        solved = true;
                    }
                    else if (AimChild.TryGetValue(j, out var c)
                        && f0j.TryGetValue(j, out var j0) && f0j.TryGetValue(c, out var c0)
                        && fj.TryGetValue(j, out var jf2) && fj.TryGetValue(c, out var cf2)
                        && (c0.Position - j0.Position).LengthSquared() > 1e-10f
                        && (cf2.Position - jf2.Position).LengthSquared() > 1e-10f)
                    {
                        w = FromTo(c0.Position - j0.Position, cf2.Position - jf2.Position);
                        solved = true;
                    }
                    else w = Q.Identity;
                }
                else w = Q.Identity;
                if (!solved)
                    w = parentW * (fj.TryGetValue(j, out var s) ? s.LocalRot : Q.Identity);
                world[j] = w;
                locals[j][fi] = Q.Normalize(Q.Inverse(parentW) * w);
            }
        }

        foreach (var joint in order)
        {
            var mixName = NodeNameFor(joint);
            if (mixName == null) continue;
            var ch = new NodeAnimationChannel { NodeName = mixName };
            for (int fi = 0; fi < frames.Count; fi++)
            {
                var fr = frames[fi];
                double tick = (fr.Time - frames[0].Time) * tps;
                var q = locals[joint][fi];
                ch.RotationKeys.Add(new QuaternionKey(tick, new Assimp.Quaternion(q.W, q.X, q.Y, q.Z)));
                // World position on Root drives root motion; other joints keep
                // bind translation (AnimRetarget samples pos channels for pelvis).
                if (joint.Equals("Root", StringComparison.OrdinalIgnoreCase) && fr.Joints.TryGetValue("Root", out var rs))
                    ch.PositionKeys.Add(new VectorKey(tick, new Vector3D(rs.Position.X, rs.Position.Y, rs.Position.Z)));
            }
            if (ch.RotationKeyCount > 0)
                anim.NodeAnimationChannels.Add(ch);
        }

        if (anim.NodeAnimationChannelCount == 0)
            warnings.Add("Physics mocap: no joint channels could be built.");
        scene.Animations.Add(anim);
        return scene;
    }
}
