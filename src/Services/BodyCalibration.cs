// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Linq;
using Assimp;
using M4 = System.Numerics.Matrix4x4;
using Q = System.Numerics.Quaternion;
using V = System.Numerics.Vector3;

namespace FiveOS.Services;

/// <summary>
/// Fits a retargeted clip to the ped's actual body.
///
/// A retarget copies joint ANGLES, which is only complete if the target has the
/// source's proportions. It never does: measured against a filmed dancer, the
/// freemode ped is 0.88x the hip width, 0.89x the torso height and 1.06x the
/// forearm. Identical angles therefore land the hands somewhere else on that
/// body, and nothing in an angle-copy notices when "somewhere else" is INSIDE
/// the chest or pelvis — which is what puts a ped's forearms through its own
/// crotch.
///
/// Two passes, both operating on the retargeted GTA tracks:
///   • PROPORTION FIT — re-aims each limb so the hand/foot sits where it sits on
///     the SUBJECT's body (as a fraction of their build), not where the raw angle
///     happens to put it on the ped's.
///   • BODY CLEARANCE — models the torso as a tapered elliptical capsule and
///     swings any limb that ends up inside it back out, rigidly about the
///     shoulder/hip so the elbow/knee bend is untouched.
///
/// Both are strength-scaled rather than on/off, because the "right" amount
/// depends on the ped and the clip — hence the sliders.
/// </summary>
public static class BodyCalibration
{
    /// <param name="Clearance">0 = allow limbs inside the body, 1 = fully push them out.</param>
    /// <param name="BodyWidth">Scales the torso capsule. The skeleton only gives joint
    /// centres, so how far the flesh reaches past them is a guess; this is the knob.</param>
    /// <param name="ArmSpread">Constant outward swing of both arms, as a fraction of
    /// shoulder width. Opens up a pose that reads too closed on a stockier ped.</param>
    /// <remarks>
    /// BodyWidth defaults to 1.7, NOT 1.0. The skeleton only gives joint centres:
    /// the ped's hip JOINTS are ~18 cm apart while its pelvis is more like 35 cm
    /// wide, so a capsule built straight off the bones is roughly 60% of the real
    /// body and sails through the clipping without noticing. 1.7 puts the capsule
    /// on the actual silhouette — measured on a dance clip, a forearm is inside
    /// the torso on 23% of samples (3.8 cm deep) and the pass only sees that at a
    /// realistic width.
    /// </remarks>
    public sealed record Settings(
        bool Enabled = true,
        float Clearance = 1.0f,
        float BodyWidth = 1.7f,
        float ArmSpread = 0.0f)
    {
        public static readonly Settings Default = new();
        public bool IsIdentity =>
            !Enabled || (Clearance <= 0f && Math.Abs(ArmSpread) < 1e-4f);
    }

    private sealed class Node
    {
        public string Name = "";
        public Node? Parent;
        public M4 RestLocal;
        public M4 World;
        public ushort Tag;
        public bool HasTag;
        public readonly List<Node> Children = new();
    }

    private static M4 ToNumerics(Assimp.Matrix4x4 m) => new(
        m.A1, m.B1, m.C1, m.D1, m.A2, m.B2, m.C2, m.D2,
        m.A3, m.B3, m.C3, m.D3, m.A4, m.B4, m.C4, m.D4);

    private static Assimp.Node? Find(Assimp.Node n, Func<Assimp.Node, bool> p)
    {
        if (p(n)) return n;
        foreach (var c in n.Children) { var r = Find(c, p); if (r != null) return r; }
        return null;
    }

    private static V Pos(Node n) => new(n.World.M41, n.World.M42, n.World.M43);

    private static V Norm(V v, V fallback)
    {
        var len = v.Length();
        return len > 1e-6f ? v / len : fallback;
    }

    /// <summary>Shortest-arc rotation taking <paramref name="from"/> onto <paramref name="to"/>.</summary>
    private static Q FromTo(V from, V to)
    {
        var a = Norm(from, V.UnitY);
        var b = Norm(to, V.UnitY);
        var dot = Math.Clamp(V.Dot(a, b), -1f, 1f);
        if (dot > 0.999999f) return Q.Identity;
        if (dot < -0.999999f)
        {
            var axis = Norm(V.Cross(a, MathF.Abs(a.X) < 0.9f ? V.UnitX : V.UnitY), V.UnitZ);
            return new Q(axis.X, axis.Y, axis.Z, 0f);
        }
        var c = V.Cross(a, b);
        return Q.Normalize(new Q(c.X, c.Y, c.Z, 1f + dot));
    }

    /// <summary>
    /// Apply the calibration to a set of retargeted tracks. Returns the input
    /// unchanged when disabled, when the reference rig is unavailable, or when
    /// the clip lacks the bones the passes need — this is a polish step and must
    /// never be the reason an import fails.
    /// </summary>
    public static IReadOnlyList<PosedBoneTrack> Apply(
        IReadOnlyList<PosedBoneTrack> tracks, Settings settings, List<string>? warnings = null)
    {
        if (tracks is null || tracks.Count == 0 || settings is null || settings.IsIdentity)
            return tracks ?? Array.Empty<PosedBoneTrack>();
        try
        {
            var rig = LoadRig();
            if (rig is null) return tracks;
            return Run(tracks, settings, rig.Value.all, rig.Value.byTag);
        }
        catch (Exception ex)
        {
            warnings?.Add("Body calibration skipped: " + ex.Message);
            return tracks;
        }
    }

    private static (List<Node> all, Dictionary<ushort, Node> byTag)? LoadRig()
    {
        var path = System.IO.Path.Combine(
            RuntimeAssets.ViewerDir, "reference", "freemode_male.glb");
        if (!System.IO.File.Exists(path)) return null;
        using var ctx = new AssimpContext();
        var scene = ctx.ImportFile(path, PostProcessSteps.None);
        // GAME_RIG/skel is the deform skeleton the game and the viewer use; the
        // file also carries a Blender control_rig whose SKEL_ROOT differs.
        var gameRig = Find(scene.RootNode, n => n.Name == "GAME_RIG") ?? scene.RootNode;
        var root = Find(gameRig, n => n.Name.StartsWith("SKEL_ROOT", StringComparison.OrdinalIgnoreCase));
        if (root is null) return null;

        var all = new List<Node>();
        // Index by TAG, never by a "stripped" name: GLTFLoader dedup suffixes look
        // exactly like the real bone names that end in a digit (SKEL_Neck_1,
        // SKEL_L_Finger00), so trimming _\d+ silently loses them. TryResolve
        // already handles both.
        var byTag = new Dictionary<ushort, Node>();
        void Walk(Assimp.Node n, Node? parent)
        {
            var node = new Node
            {
                Name = n.Name,
                Parent = parent,
                RestLocal = ToNumerics(n.Transform),
                HasTag = GtaBoneTags.TryResolve(n.Name, out var tag),
            };
            node.Tag = tag;
            parent?.Children.Add(node);
            all.Add(node);
            if (node.HasTag && !byTag.ContainsKey(tag)) byTag[tag] = node;
            foreach (var c in n.Children) Walk(c, node);
        }
        Walk(root, null);
        return (all, byTag);
    }

    private sealed class Limb
    {
        public Node Root = null!, Mid = null!, End = null!;
        public float Side;      // +1 = the ped's left, -1 = its right
    }

    private static List<PosedBoneTrack> Run(
        IReadOnlyList<PosedBoneTrack> tracks, Settings s,
        List<Node> all, Dictionary<ushort, Node> byTag)
    {
        var frames = tracks.Max(t => t.PerFrame.Length);
        // Working copy: per tag, per frame local rotation.
        var local = tracks.ToDictionary(t => t.BoneTag, t => (Q[])t.PerFrame.Clone());

        Node? N(string name) =>
            GtaBoneTags.ByGtaName.TryGetValue(name, out var tag) && byTag.TryGetValue(tag, out var n)
                ? n : null;
        var pelvis = N("SKEL_Pelvis"); var neck = N("SKEL_Neck_1");
        var lThigh = N("SKEL_L_Thigh"); var rThigh = N("SKEL_R_Thigh");
        var lUpper = N("SKEL_L_UpperArm"); var rUpper = N("SKEL_R_UpperArm");
        if (pelvis is null || neck is null || lThigh is null || rThigh is null
            || lUpper is null || rUpper is null)
            return tracks.ToList();

        var arms = new List<Limb>();
        foreach (var (side, up, fore, hand) in new[]
        {
            (1f, "SKEL_L_UpperArm", "SKEL_L_Forearm", "SKEL_L_Hand"),
            (-1f, "SKEL_R_UpperArm", "SKEL_R_Forearm", "SKEL_R_Hand"),
        })
        {
            var a = N(up); var b = N(fore); var c = N(hand);
            if (a != null && b != null && c != null)
                arms.Add(new Limb { Root = a, Mid = b, End = c, Side = side });
        }
        if (arms.Count == 0) return tracks.ToList();

        // ── Rest measurements: the ped's own build ──────────────────────────
        Fk(all, local, frame: -1);   // rest pose
        var restHip = (Pos(lThigh) - Pos(rThigh)).Length();
        var restShoulder = (Pos(lUpper) - Pos(rUpper)).Length();
        var restTorso = (Pos(neck) - Pos(pelvis)).Length();
        if (restHip < 1e-4f || restTorso < 1e-4f) return tracks.ToList();

        for (var f = 0; f < frames; f++)
        {
            Fk(all, local, f);
            var hips = Pos(pelvis);
            var axis = Norm(Pos(neck) - hips, V.UnitY);
            var right = Norm(Pos(rThigh) - Pos(lThigh), V.UnitX);
            right = Norm(right - axis * V.Dot(right, axis), V.UnitX);   // orthogonalize
            var fwd = Norm(V.Cross(axis, right), V.UnitZ);

            foreach (var limb in arms)
            {
                if (MathF.Abs(s.ArmSpread) > 1e-4f)
                {
                    var shoulder0 = Pos(limb.Root);
                    var hand0 = Pos(limb.End);
                    var spread = hand0 + right * (-limb.Side) * (s.ArmSpread * restShoulder);
                    ApplyWorldRotation(limb.Root, FromTo(hand0 - shoulder0, spread - shoulder0), local, f);
                    Fk(all, local, f);
                }
                if (s.Clearance <= 0f) continue;

                // Swinging the arm out moves the elbow too, so one pass rarely
                // clears the whole forearm; a couple of iterations converge.
                for (var pass = 0; pass < 3; pass++)
                {
                    var shoulder = Pos(limb.Root);
                    var elbow = Pos(limb.Mid);
                    var hand = Pos(limb.End);

                    // Sample ALONG the forearm, not just the hand. The forearm is
                    // what actually sweeps through a torso — a hand-only test
                    // reports almost nothing while the arm is visibly buried.
                    var worstPush = V.Zero;
                    var worstDepth = 0f;
                    for (var i = 0; i <= 4; i++)
                    {
                        var probe = V.Lerp(elbow, hand, i / 4f);
                        var clear = PushOutOfTorso(probe, hips, axis, right, fwd,
                                                   restHip, restShoulder, restTorso, s, limb.Side);
                        var delta = clear - probe;
                        if (delta.Length() > worstDepth) { worstDepth = delta.Length(); worstPush = delta; }
                    }
                    if (worstDepth <= 1e-4f) break;

                    // Swing the WHOLE arm rigidly about the shoulder: the hand
                    // moves, the elbow bend (which the retarget already gets
                    // exactly right) does not change.
                    var target = hand + worstPush;
                    ApplyWorldRotation(limb.Root, FromTo(hand - shoulder, target - shoulder), local, f);
                    Fk(all, local, f);   // children moved; refresh before re-probing
                }
            }
        }

        return tracks.Select(t => t with { PerFrame = local[t.BoneTag] }).ToList();
    }

    /// <summary>
    /// Push a point out of the torso, modelled as a capsule with an ELLIPTICAL
    /// cross-section that tapers from hips to shoulders. A circular capsule is
    /// wrong here: a body is far wider than it is deep, so a circle sized to the
    /// shoulders would shove the arms out sideways, and one sized to the depth
    /// would let them pass through the chest.
    /// </summary>
    private static V PushOutOfTorso(
        V point, V hips, V axis, V right, V fwd,
        float restHip, float restShoulder, float restTorso, Settings s, float side)
    {
        var rel = point - hips;
        var height = V.Dot(rel, axis);
        // Only the torso span; below the hips is legs, above the neck is head.
        var t = Math.Clamp(height / MathF.Max(restTorso, 1e-4f), 0f, 1f);
        if (height < -0.15f * restTorso || height > 1.05f * restTorso) return point;

        // Half-widths, tapering hips -> shoulders, scaled by the user's knob. The
        // skeleton gives joint centres only, so the flesh reaches past them: the
        // 1.15/0.85 factors are that margin and BodyWidth tunes it.
        var halfX = float.Lerp(restHip * 0.5f * 1.15f, restShoulder * 0.5f * 0.85f, t) * s.BodyWidth;
        var halfZ = halfX * 0.62f;   // bodies are much thinner front-to-back

        var x = V.Dot(rel, right);
        var z = V.Dot(rel, fwd);
        var norm = (x * x) / (halfX * halfX) + (z * z) / (halfZ * halfZ);
        if (norm >= 1f) return point;   // already clear

        // Scale outward along the ellipse ray until it sits on the surface.
        var scale = 1f / MathF.Max(MathF.Sqrt(norm), 1e-4f);
        var nx = x * scale;
        var nz = z * scale;
        // Degenerate: a point on the axis has no outward direction — send it to
        // the limb's own side rather than picking arbitrarily.
        if (MathF.Sqrt(x * x + z * z) < 1e-5f) { nx = -side * halfX; nz = 0f; }

        var pushed = hips + axis * height + right * nx + fwd * nz;
        return V.Lerp(point, pushed, Math.Clamp(s.Clearance, 0f, 1f));
    }

    /// <summary>Rotate a bone's WORLD orientation, writing back its local track.</summary>
    private static void ApplyWorldRotation(Node bone, Q worldDelta, Dictionary<ushort, Q[]> local, int frame)
    {
        if (!bone.HasTag || !local.TryGetValue(bone.Tag, out var track) || frame >= track.Length) return;
        var parentWorld = bone.Parent is null ? Q.Identity : RotationOf(bone.Parent.World);
        var world = RotationOf(bone.World);
        var wanted = Q.Normalize(worldDelta * world);
        track[frame] = Q.Normalize(Q.Inverse(parentWorld) * wanted);
    }

    private static Q RotationOf(M4 m)
    {
        M4.Decompose(m, out _, out var q, out _);
        return q;
    }

    /// <summary>FK the rig for one frame; <paramref name="frame"/> &lt; 0 = rest pose.</summary>
    private static void Fk(List<Node> all, Dictionary<ushort, Q[]> local, int frame)
    {
        foreach (var b in all)
        {
            var m = b.RestLocal;
            if (frame >= 0 && b.HasTag && local.TryGetValue(b.Tag, out var track) && track.Length > 0)
            {
                M4.Decompose(b.RestLocal, out var scale, out _, out var translation);
                m = M4.CreateScale(scale)
                  * M4.CreateFromQuaternion(track[Math.Min(frame, track.Length - 1)])
                  * M4.CreateTranslation(translation);
            }
            b.World = b.Parent is null ? m : m * b.Parent.World;
        }
    }
}
