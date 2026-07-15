// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Assimp;
using Q = System.Numerics.Quaternion;
using V = System.Numerics.Vector3;

namespace FiveOS.Services;

/// <summary>
/// Retargets a foreign rig (Mixamo / generic humanoid) onto the GTA freemode
/// skeleton, using the same method the Rokoko Blender addon uses (verified
/// against its source) plus the fixes that make it correct for GTA specifically.
///
/// The algorithm (per mapped bone, per frame):
///   R_delta_world = R_source_world(t) · inverse(R_source_rest_world)   // source's rotation deviation from ITS rest
///   R_target_world = Rc · R_delta_world · inverse(Rc) · R_ref_world     // applied onto the target's reference orientation
///   R_target_local = inverse(R_targetParent_world) · R_target_world     // -> the local channel the .ycd stores
/// where Rc aligns the two rigs' world facing (Mixamo faces opposite GTA).
///
/// Two GTA-specific corrections, learned the hard way:
///  1. TARGET SKELETON: the freemode glb contains TWO skeletons — a Blender
///     `control_rig` and the actual deform skeleton `skel` under `GAME_RIG`.
///     The game (and FiveOS's proven Pose→Emote path) uses GAME_RIG/skel; its
///     SKEL_ROOT bakes a +90°-about-X (Y-up→Z-up) rest. Retargeting onto
///     control_rig produces output that looks fine offline but contorts in
///     game. We root at GAME_RIG's SKEL_ROOT.
///  2. REST REFERENCE = a constructed T-POSE, not the freemode A-pose. Mixamo
///     animations are authored against a T-pose; the GTA skeleton rests in an
///     A-pose (arms ~45° down). Using the A-pose as the reference drops every
///     limb by that difference. We build a matching T-pose by rotating each
///     GTA bone's rest so it points where the Mixamo bone points (preserving
///     the GTA bone's roll), exactly what the tutorial workflow does by hand.
///
/// Output is per-bone LOCAL (parent-relative) rotation on GAME_RIG/skel, with
/// SKEL_ROOT skipped — byte-identical convention to the proven Pose→Emote path.
/// </summary>
internal static class AnimRetarget
{
    private sealed class Bone
    {
        public string Name = "", Parent = "";
        public Q RestRot;
        public V RestPos;
        public ushort Tag;
        public bool HasTag;
    }

    public static List<PosedBoneTrack>? Retarget(
        Scene srcScene, Animation anim, double tps, int frames, int fps,
        out List<string> mapped, out List<string> unmapped, out V[] rootMotion, List<string> warnings)
    {
        mapped = new List<string>();
        unmapped = new List<string>();
        rootMotion = Array.Empty<V>();

        // ── source skeleton (pivots already collapsed by the caller) ──
        var srcBones = BuildTable(srcScene.RootNode);
        var chan = new Dictionary<string, NodeAnimationChannel>();
        // Position channels are kept separately: root motion (the character's
        // travel across the ground) lives in the TRANSLATION keys of the root /
        // pelvis bones, which the rotation-only path would silently drop.
        var posChan = new Dictionary<string, NodeAnimationChannel>();
        foreach (var c in anim.NodeAnimationChannels)
        {
            if (c == null) continue;
            var nm = c.NodeName ?? "";
            int piv = nm.IndexOf("_$AssimpFbx$_", StringComparison.Ordinal);
            if (piv >= 0) nm = nm.Substring(0, piv);
            if (c.RotationKeyCount > 0 && !chan.ContainsKey(nm)) chan[nm] = c;
            if (c.PositionKeyCount > 0 && !posChan.ContainsKey(nm)) posChan[nm] = c;
        }

        // ── GTA reference: root at GAME_RIG's SKEL_ROOT (the deform skeleton) ──
        var gtaScene = LoadGtaScene(warnings);
        if (gtaScene == null) return null;
        var gameRig = FindNode(gtaScene.RootNode, "GAME_RIG");
        var gtaRoot = FindNode(gameRig ?? gtaScene.RootNode, "SKEL_ROOT");
        if (gtaRoot == null) { warnings.Add("GTA reference skeleton has no SKEL_ROOT."); return null; }
        var gtaBones = BuildTable(gtaRoot);

        // ── bind FK + facing alignment Rc ──
        var srcBindR = new Dictionary<string, Q>(); var srcBindP = new Dictionary<string, V>();
        Fk(srcBones, n => srcBones[n].RestRot, srcBindR, srcBindP);
        var gtaBindR = new Dictionary<string, Q>(); var gtaBindP = new Dictionary<string, V>();
        Fk(gtaBones, n => gtaBones[n].RestRot, gtaBindR, gtaBindP);

        ushort tPelvis = GtaBoneTags.ByGtaName["SKEL_Pelvis"], tHead = GtaBoneTags.ByGtaName["SKEL_Head"],
               tLArm = GtaBoneTags.ByGtaName["SKEL_L_UpperArm"], tRArm = GtaBoneTags.ByGtaName["SKEL_R_UpperArm"];
        Q Rc = Q.Identity;
        var uS = DirT(srcBones, srcBindP, tPelvis, tHead); var sS = DirT(srcBones, srcBindP, tLArm, tRArm);
        var uT = DirT(gtaBones, gtaBindP, tPelvis, tHead); var sT = DirT(gtaBones, gtaBindP, tLArm, tRArm);
        if (uS.HasValue && sS.HasValue && uT.HasValue && sT.HasValue)
            Rc = DeriveAlign(uS.Value, sS.Value, uT.Value, sT.Value);
        else
            warnings.Add("Couldn't derive rig facing from bind pose — assuming aligned.");

        var srcPC = PrimaryChild(srcBones); var gtaPC = PrimaryChild(gtaBones);
        var srcByTag = new Dictionary<ushort, string>();
        foreach (var kv in srcBones) if (kv.Value.HasTag && !srcByTag.ContainsKey(kv.Value.Tag)) srcByTag[kv.Value.Tag] = kv.Key;

        // ── Topology fix: SKEL_Pelvis must be driven by the source bone that
        // ACTUALLY parents the thighs (the leg root), not merely the first bone
        // whose name contains "hip"/"pelvis". ActorCore / Character Creator rigs
        // carry BOTH CC_Base_Hip (whole-body root) AND its child CC_Base_Pelvis
        // (the real thigh parent). The tree walk hits Hip first, so the legs end
        // up hanging off the wrong frame — the pelvis's own ~11° tilt gets
        // mis-attributed to both thighs and the hips read wrong. Mixamo has a
        // single "Hips" that IS the thigh parent, so this resolves to the same
        // bone there (no regression). Pick the thigh's parent when it, too, maps
        // to SKEL_Pelvis; Hip's rotation is not lost — it lives in the world
        // deltas of Pelvis and Waist (its children), which drive legs and spine.
        {
            ushort tLThigh = GtaBoneTags.ByGtaName["SKEL_L_Thigh"], tRThigh = GtaBoneTags.ByGtaName["SKEL_R_Thigh"];
            var thighName = srcBones.FirstOrDefault(kv => kv.Value.HasTag && (kv.Value.Tag == tLThigh || kv.Value.Tag == tRThigh)).Key;
            if (thighName != null && srcBones.TryGetValue(thighName, out var thighBone)
                && thighBone.Parent != "" && srcBones.TryGetValue(thighBone.Parent, out var legRoot)
                && legRoot.HasTag && legRoot.Tag == tPelvis
                && srcByTag.TryGetValue(tPelvis, out var curPelvis) && curPelvis != thighBone.Parent)
            {
                warnings.Add($"Pelvis retarget: driving SKEL_Pelvis from '{thighBone.Parent}' (thigh parent) instead of '{curPelvis}' (body root).");
                srcByTag[tPelvis] = thighBone.Parent;
            }
        }

        foreach (var kv in srcBones) if (kv.Value.HasTag && chan.ContainsKey(kv.Key)) mapped.Add(kv.Key);
        foreach (var c in chan.Keys) if (!srcBones.TryGetValue(c, out var sb) || !sb.HasTag) unmapped.Add(c);

        // ── constructed T-pose reference world orientation per GTA bone ──
        // Rotate each GTA bone's rest so it POINTS where the Mixamo bone points
        // (shortest arc — keeps the GTA roll, unlike a full-orientation copy
        // which tips the whole body because the GTA hierarchy differs).
        var tpose = new Dictionary<string, Q>();
        foreach (var name in Ordered(gtaBones))
        {
            var b = gtaBones[name]; Q rest = gtaBindR[name]; Q tp = rest;
            // The pelvis has no meaningful "aim" (it's a hub, not a limb) — aiming
            // it at a child flips it (the old reclined bug). Drive it from REST so
            // it just inherits the source hip's rotation delta (sway/tilt).
            if (b.HasTag && b.Tag != tPelvis && gtaPC.TryGetValue(name, out var tc)
                && srcByTag.TryGetValue(b.Tag, out var s) && srcPC.TryGetValue(s, out var sc)
                && srcBindP.ContainsKey(sc) && srcBindP.ContainsKey(s))
            {
                V gtaDir = Norm(V.Transform(gtaBones[tc].RestPos, rest));
                V mixDir = Norm(V.Transform(Norm(srcBindP[sc] - srcBindP[s]), Rc));
                tp = FromTo(gtaDir, mixDir) * rest;
            }
            tpose[name] = tp;
        }

        // which GTA bones do we DRIVE? tagged, non-root (SKEL_ROOT carries the
        // coordinate conversion + gets root motion separately), with a source
        // correspondence. The pelvis IS driven (from rest) so the hips sway/tilt
        // with the animation instead of sitting as a rigid anchor.
        var driveNames = new HashSet<string>();
        var emit = new List<(ushort tag, string name)>();
        var seenTags = new HashSet<ushort>();
        bool noPelvis = Environment.GetEnvironmentVariable("FIVEOS_NO_PELVIS") == "1";
        foreach (var name in Ordered(gtaBones))
        {
            var b = gtaBones[name];
            if (noPelvis && b.Tag == tPelvis) continue;   // test: leave the pelvis at rest
            // one bone per tag (the glb duplicates some SKEL_* names across rigs/helpers)
            if (b.HasTag && b.Tag != 0 && srcByTag.ContainsKey(b.Tag) && seenTags.Add(b.Tag))
            { driveNames.Add(name); emit.Add((b.Tag, name)); }
        }
        if (emit.Count == 0) return new List<PosedBoneTrack>();

        var perFrame = emit.ToDictionary(e => e.tag, _ => new Q[frames]);
        Q Rci = Q.Inverse(Rc);

        // FBX PreRotation handling. Assimp's pivot-collapse puts each bone's
        // PreRotation on the NODE (RestRot). The animation channel is EITHER a
        // small LclRotation-from-identity (→ true local = RestRot·channel; the
        // channel alone drops Mixamo's ~180° leg pre-rotation and flips the legs
        // up) OR already the full local (→ composing doubles the pre-rotation and
        // folds the body). This is even MIXED within a rig — ActorCore's root
        // carries a 90° up-conversion (compose) while its limbs are full-local.
        //
        // Decide per bone by whether channel(0) sits closer to RestRot (full) or
        // to identity (a delta → compose). But that only works when frame 0 is
        // near the bind for that bone; a bone already posed at frame 0 (e.g. an
        // arm raised in an aiming clip) is AMBIGUOUS. So: bones with a decisive
        // gap vote and decide themselves; ambiguous bones follow the majority.
        const double thresh = 0.785; // 45°
        var toR = new Dictionary<string, double>(); var toI = new Dictionary<string, double>();
        double clearVote = 0;
        foreach (var kv in srcBones)
            if (chan.TryGetValue(kv.Key, out var c) && c.RotationKeyCount > 0)
            {
                var c0 = NQ(c.RotationKeys[0].Value);
                double tR = AngleTo(c0, kv.Value.RestRot), tI = AngleTo(c0, Q.Identity);
                toR[kv.Key] = tR; toI[kv.Key] = tI;
                if (Math.Abs(tR - tI) > thresh) clearVote += (tI - tR); // >0 favours full-local
            }
        bool limbFull = clearVote > 0;
        var useFull = new Dictionary<string, bool>();
        foreach (var kv in toR)
            useFull[kv.Key] = Math.Abs(kv.Value - toI[kv.Key]) > thresh ? kv.Value < toI[kv.Key] : limbFull;
        // Test overrides: force every bone to compose (RestRot·channel) or full.
        var forceCompose = Environment.GetEnvironmentVariable("FIVEOS_FORCE_COMPOSE");
        if (forceCompose == "1") foreach (var k in useFull.Keys.ToList()) useFull[k] = false;
        else if (forceCompose == "full") foreach (var k in useFull.Keys.ToList()) useFull[k] = true;

        // ── root-motion setup ──
        // Root motion GROUNDS the feet: in the source the pelvis moves and the
        // planted foot's leg-rotation cancels it, so the foot stays fixed on the
        // floor. We copy the leg rotations, so we MUST also move the pelvis (via
        // SKEL_ROOT) by the same amount or the foot skates. The scale that makes
        // it cancel exactly is the LEG-LENGTH ratio (pelvis→foot), not the torso
        // ratio: the foot's swing scales with leg length, so the pelvis travel
        // must too.
        string? srcPelvis = srcByTag.TryGetValue(tPelvis, out var sp0) ? sp0 : null;
        string? GtaName(ushort tg) => gtaBones.FirstOrDefault(k => k.Value.HasTag && k.Value.Tag == tg).Key;
        string? gtaPelvis = GtaName(tPelvis);
        ushort tLFoot0 = GtaBoneTags.ByGtaName["SKEL_L_Foot"];
        string? srcFoot0 = srcByTag.TryGetValue(tLFoot0, out var sf0) ? sf0 : null;
        string? gtaFoot0 = GtaName(tLFoot0);
        double scale = 1;
        if (srcPelvis != null && srcFoot0 != null && gtaPelvis != null && gtaFoot0 != null
            && srcBindP.ContainsKey(srcFoot0) && gtaBindP.ContainsKey(gtaFoot0))
        {
            double srcLen = (srcBindP[srcFoot0] - srcBindP[srcPelvis]).Length();  // source leg
            double gtaLen = (gtaBindP[gtaFoot0] - gtaBindP[gtaPelvis]).Length();  // GTA leg
            if (srcLen > 1e-4) scale = gtaLen / srcLen;
        }
        var rootWorld = new V[frames];

        // ── foot-lock setup: source foot world paths (for plant detection) ──
        ushort tLThighG = GtaBoneTags.ByGtaName["SKEL_L_Thigh"], tRThighG = GtaBoneTags.ByGtaName["SKEL_R_Thigh"];
        ushort tLCalfG = GtaBoneTags.ByGtaName["SKEL_L_Calf"], tRCalfG = GtaBoneTags.ByGtaName["SKEL_R_Calf"];
        ushort tLFootG = GtaBoneTags.ByGtaName["SKEL_L_Foot"], tRFootG = GtaBoneTags.ByGtaName["SKEL_R_Foot"];
        string? srcLFoot = srcByTag.TryGetValue(tLFootG, out var slf) ? slf : null;
        string? srcRFoot = srcByTag.TryGetValue(tRFootG, out var srf) ? srf : null;
        var srcLFootP = new V[frames]; var srcRFootP = new V[frames];

        for (int f = 0; f < frames; f++)
        {
            double ticks = (double)f / fps * tps;
            var sR = new Dictionary<string, Q>(); var sP = new Dictionary<string, V>();
            Fk(srcBones, n => {
                if (!chan.ContainsKey(n)) return srcBones[n].RestRot;
                var ch = SampleLocal(chan, srcBones[n], n, ticks);
                return useFull[n] ? ch : Q.Normalize(srcBones[n].RestRot * ch);
            }, sR, sP, n => SamplePos(posChan, srcBones[n], n, ticks));
            if (srcPelvis != null && sP.TryGetValue(srcPelvis, out var pw)) rootWorld[f] = pw;
            if (srcLFoot != null && sP.TryGetValue(srcLFoot, out var lfp)) srcLFootP[f] = lfp;
            if (srcRFoot != null && sP.TryGetValue(srcRFoot, out var rfp)) srcRFootP[f] = rfp;

            var animW = new Dictionary<string, Q>(); var local = new Dictionary<string, Q>();
            foreach (var name in Ordered(gtaBones))
            {
                var b = gtaBones[name];
                Q parentW = (b.Parent != "" && animW.ContainsKey(b.Parent)) ? animW[b.Parent] : Q.Identity;
                Q taw;
                if (driveNames.Contains(name) && srcByTag.TryGetValue(b.Tag, out var s))
                {
                    Q dW = sR[s] * Q.Inverse(srcBindR[s]);     // source deviation from its rest
                    taw = (Rc * dW * Rci) * tpose[name];       // onto the T-pose reference
                }
                else taw = parentW * b.RestRot;
                animW[name] = taw;
                local[name] = Q.Normalize(Q.Inverse(parentW) * taw);
            }
            // Fingers: soften the curl toward the GTA rest. Mixamo fishing clips
            // grip a rod that we don't render, so a faithful copy claws at empty
            // air. Blending keeps some motion while reading as a natural hand.
            const float fingerWeight = 0.45f;
            foreach (var (tag, name) in emit)
                perFrame[tag][f] = name.Contains("Finger", StringComparison.OrdinalIgnoreCase)
                    ? Q.Slerp(gtaBones[name].RestRot, local[name], fingerWeight)
                    : local[name];
        }

        // Root motion is ON by default so imported clips TRAVEL like the source
        // (Mixamo / ActorCore) instead of being glued to the origin. The export
        // side only bakes the SKEL_ROOT mover when the user keeps the Root Motion
        // movement mode (default for imports) and targets a standalone resource
        // that extracts movers; picking In Place drops it for a clean dpemotes
        // clip. (Env: FIVEOS_NO_ROOTMOTION=1 to force it off,
        // FIVEOS_FOOTLOCK=1 to force-enable the foot-lock IK.)
        bool doRoot = Environment.GetEnvironmentVariable("FIVEOS_NO_ROOTMOTION") != "1";
        // Foot-lock is COUPLED to root motion: its only job is to keep planted feet
        // from sliding as the root TRAVELS. With root motion off (default) there is
        // no travel to counter, and running it anyway re-solves the legs with 2-bone
        // IK that STRAIGHTENS the knees — flattening every crouch/step/weight-shift
        // of a dance (measured: source knees bend 30-64°, foot-locked GTA only 5-24°;
        // whole-body pose error 5.3° with it vs 1.2° without). So it's OFF unless
        // root motion is on. FIVEOS_FOOTLOCK=1 forces it back on for experiments.
        bool doFoot = (doRoot || Environment.GetEnvironmentVariable("FIVEOS_FOOTLOCK") == "1")
                      && Environment.GetEnvironmentVariable("FIVEOS_NO_FOOTLOCK") != "1";
        if (srcPelvis != null && frames > 0 && doRoot)
        {
            rootMotion = new V[frames];
            var origin = rootWorld[0];
            for (int f = 0; f < frames; f++)
            {
                var d = rootWorld[f] - origin;
                d = new V(d.X, 0, d.Z);                        // ground plane only (source Y-up)
                var rm = V.Transform(d, Rc) * (float)scale;
                // Re-strip vertical in the GTA glTF frame too — Rc rotates the
                // horizontal displacement and can inject a Y component that lifts
                // the whole ped off the floor in preview and in-game.
                rootMotion[f] = new V(rm.X, 0, rm.Z);
            }
        }

        // Foot-lock (IK): pin planted feet to the ground so the ped grips instead
        // of skating (the "moving in place" look) as the hips / root move over them.
        if (frames >= 6 && doFoot)
        {
            string rootName = gtaBones.FirstOrDefault(kv => kv.Value.Parent == "").Key;
            string? gLThigh = GtaName(tLThighG), gLCalf = GtaName(tLCalfG), gLFoot = GtaName(tLFootG);
            string? gRThigh = GtaName(tRThighG), gRCalf = GtaName(tRCalfG), gRFoot = GtaName(tRFootG);
            float pelvisDrop = 0;
            if (gLFoot != null && gRFoot != null)
                pelvisDrop = ComputePelvisDrop(gtaBones, driveNames, perFrame, rootMotion, rootName, gLFoot, gRFoot, frames);
            bool[]? plantedL = null, plantedR = null;
            if (srcLFoot != null && gLThigh != null && gLCalf != null && gLFoot != null)
                plantedL = FootLock(gtaBones, driveNames, perFrame, rootMotion, rootName, gLThigh, gLCalf, gLFoot, tLThighG, tLCalfG, tLFootG, srcLFootP, frames, pelvisDrop);
            if (srcRFoot != null && gRThigh != null && gRCalf != null && gRFoot != null)
                plantedR = FootLock(gtaBones, driveNames, perFrame, rootMotion, rootName, gRThigh, gRCalf, gRFoot, tRThighG, tRCalfG, tRFootG, srcRFootP, frames, pelvisDrop);
            if (rootMotion != null && plantedL != null && plantedR != null)
                ClampRootWhilePlanted(rootMotion, plantedL, plantedR, frames);
        }

        return emit.Select(e => new PosedBoneTrack(e.tag, perFrame[e.tag],
            srcByTag.TryGetValue(e.tag, out var _sn) ? _sn : null)).ToList();
    }

    // ── helpers ──
    private static Dictionary<string, Bone> BuildTable(Node root)
    {
        var t = new Dictionary<string, Bone>();
        void Rec(Node n, string parent)
        {
            n.Transform.Decompose(out _, out var r, out var tr);
            var b = new Bone { Name = n.Name, Parent = parent, RestRot = Q.Normalize(new Q(r.X, r.Y, r.Z, r.W)), RestPos = new V(tr.X, tr.Y, tr.Z) };
            if (GtaBoneTags.TryResolve(StripPivot(n.Name), out var tag)) { b.Tag = tag; b.HasTag = true; }
            if (!t.ContainsKey(n.Name)) t[n.Name] = b;
            for (int i = 0; i < n.ChildCount; i++) Rec(n.Children[i], n.Name);
        }
        Rec(root, "");
        return t;
    }

    private static void Fk(Dictionary<string, Bone> tbl, Func<string, Q> lr, Dictionary<string, Q> wR, Dictionary<string, V> wP, Func<string, V>? lp = null)
    {
        foreach (var name in Ordered(tbl))
        {
            var b = tbl[name]; var l = lr(name); var p = lp != null ? lp(name) : b.RestPos;
            if (b.Parent == "" || !tbl.ContainsKey(b.Parent)) { wR[name] = l; wP[name] = p; }
            else { wR[name] = wR[b.Parent] * l; wP[name] = wP[b.Parent] + V.Transform(p, wR[b.Parent]); }
        }
    }

    // Foot-lock via 2-bone IK. Detects when the SOURCE foot is planted (near-zero
    // speed) and, for each plant, pins the GTA foot to the ground position it
    // started the plant at — solving the thigh/calf so the knee bends naturally
    // while the hips/root travel over the foot. Blended at the plant edges so it
    // eases in/out of contact. This is what gives the ped weight instead of the
    // skating "moving in place" look a rotation-only copy produces.
    // Foot-lock via 2-bone IK. Returns the planted-frame mask (for root clamp).
    private static bool[]? FootLock(
        Dictionary<string, Bone> gtaBones, HashSet<string> driveNames,
        Dictionary<ushort, Q[]> perFrame, V[] rootMotion, string rootName,
        string thighName, string calfName, string footName,
        ushort thighTag, ushort calfTag, ushort footTag, V[] srcFootP, int frames,
        float pelvisDrop = 0)
    {
        if (!perFrame.ContainsKey(thighTag) || !perFrame.ContainsKey(calfTag)) return null;
        if (!gtaBones.ContainsKey(thighName) || !gtaBones.ContainsKey(calfName) || !gtaBones.ContainsKey(footName)) return null;
        if (!perFrame.ContainsKey(footTag)) return null;
        var ordered = Ordered(gtaBones);
        string thighParent = gtaBones[thighName].Parent;
        float L1 = gtaBones[calfName].RestPos.Length();   // thigh length
        float L2 = gtaBones[footName].RestPos.Length();   // calf length
        if (L1 < 1e-5f || L2 < 1e-5f) return null;
        V thighAxis = Norm(gtaBones[calfName].RestPos);
        V calfAxis = Norm(gtaBones[footName].RestPos);
        bool hasRoot = rootMotion != null && rootMotion.Length == frames;

        // GTA FK at frame f from the current perFrame rotations (+ root motion).
        var wR = new Dictionary<string, Q>(); var wP = new Dictionary<string, V>();
        void FkAt(int f)
        {
            foreach (var name in ordered)
            {
                var b = gtaBones[name];
                Q lr = (driveNames.Contains(name) && perFrame.TryGetValue(b.Tag, out var pf)) ? pf[f] : b.RestRot;
                V lp = (name == rootName && hasRoot && rootMotion != null) ? b.RestPos + rootMotion[f] : b.RestPos;
                if (b.Parent == "" || !gtaBones.ContainsKey(b.Parent)) { wR[name] = lr; wP[name] = lp; }
                else { wR[name] = wR[b.Parent] * lr; wP[name] = wP[b.Parent] + V.Transform(lp, wR[b.Parent]); }
            }
        }

        // Plant detection = the source foot is BOTH slow AND near the ground.
        // Speed-alone was the bug: it flags the top of a swing (slow, airborne)
        // as "planted", then the IK yanks the leg down into a hunch. Gating on
        // foot height (the standard contact "schedule" — ozz foot_ik / GMR) locks
        // only feet genuinely on the floor. Source is Y-up, so Y is height.
        var planted = DetectPlantedFrames(srcFootP, frames);
        if (planted is null) return null;

        const int blend = 3;
        int i = 0;
        while (i < frames)
        {
            if (!planted[i]) { i++; continue; }
            int start = i; while (i < frames && planted[i]) i++; int end = i - 1;
            if (end - start < 1) continue;   // allow short (2-frame) stances
            FkAt(start);
            V lockPos = wP[footName];
            if (pelvisDrop > 1e-4f)
                lockPos = new V(lockPos.X, lockPos.Y - pelvisDrop, lockPos.Z);
            string footParent = gtaBones[footName].Parent;
            var footArr = perFrame[footTag];
            for (int f = start; f <= end; f++)
            {
                FkAt(f);
                V H = wP[thighName]; V Kc = wP[calfName];
                Q parentW = wR.TryGetValue(thighParent, out var pwr) ? pwr : Q.Identity;
                Q thighW = wR[thighName];
                Q origThigh = perFrame[thighTag][f], origCalf = perFrame[calfTag][f];

                V toF = lockPos - H; float d = toF.Length();
                if (d < 1e-4f) continue;
                V dir = toF / d;
                d = Math.Clamp(d, Math.Abs(L1 - L2) + 1e-3f, L1 + L2 - 1e-3f);
                float cosHip = Math.Clamp((L1 * L1 + d * d - L2 * L2) / (2 * L1 * d), -1f, 1f);
                float angHip = (float)Math.Acos(cosHip);
                V pole = (Kc - H) - dir * V.Dot(Kc - H, dir);
                if (pole.LengthSquared() < 1e-8f) pole = V.Cross(dir, V.UnitY);
                if (pole.LengthSquared() < 1e-8f) pole = V.Cross(dir, V.UnitX);
                pole = Norm(pole);
                V kneeUnit = dir * (float)Math.Cos(angHip) + pole * (float)Math.Sin(angHip);
                V Knew = H + kneeUnit * L1;

                V curThighAim = Norm(V.Transform(thighAxis, thighW));
                Q newThighW = Q.Normalize(FromTo(curThighAim, Norm(Knew - H)) * thighW);
                Q calfWafter = Q.Normalize(newThighW * origCalf);
                V curCalfAim = Norm(V.Transform(calfAxis, calfWafter));
                Q newCalfW = Q.Normalize(FromTo(curCalfAim, Norm(lockPos - Knew)) * calfWafter);

                Q ikThigh = Q.Normalize(Q.Inverse(parentW) * newThighW);
                Q ikCalf = Q.Normalize(Q.Inverse(newThighW) * newCalfW);

                // Ease IK in at plant start and out at lift-off. The old formula
                // zeroed weight on the LAST planted frame (w=0 at f==end), which
                // released the foot one frame early and caused visible skating.
                float w = 1f;
                if (end - start >= blend * 2)
                {
                    if (f < start + blend) w = Math.Min(w, (f - start + 1f) / blend);
                    if (f > end - blend) w = Math.Min(w, (end - f + 1f) / blend);
                }
                perFrame[thighTag][f] = Q.Slerp(origThigh, ikThigh, w);
                perFrame[calfTag][f] = Q.Slerp(origCalf, ikCalf, w);

                // ozz-style ankle aim: align foot up vector to ground normal (Y-up).
                Q origFoot = footArr[f];
                FkAt(f);
                Q footW = wR[footName];
                Q parentFootW = wR.TryGetValue(footParent, out var pfw) ? pfw : Q.Identity;
                V footUp = Norm(V.Transform(V.UnitY, footW));
                V targetUp = V.UnitY;
                if (V.Dot(footUp, targetUp) < 0.995f)
                {
                    Q alignUp = FromTo(footUp, targetUp);
                    Q newFootW = Q.Normalize(alignUp * footW);
                    Q ikFoot = Q.Normalize(Q.Inverse(parentFootW) * newFootW);
                    footArr[f] = Q.Slerp(origFoot, ikFoot, w * 0.65f);
                }
            }
        }
        return planted;
    }

    /// <summary>ozz UpdatePelvisOffset — lower lock targets when feet float above ground at bind.</summary>
    private static float ComputePelvisDrop(
        Dictionary<string, Bone> gtaBones, HashSet<string> driveNames,
        Dictionary<ushort, Q[]> perFrame, V[]? rootMotion, string rootName,
        string lFootName, string rFootName, int frames)
    {
        if (frames < 1) return 0;
        var ordered = Ordered(gtaBones);
        bool hasRoot = rootMotion != null && rootMotion.Length == frames;
        var wR = new Dictionary<string, Q>(); var wP = new Dictionary<string, V>();
        void FkAt(int f)
        {
            foreach (var name in ordered)
            {
                var b = gtaBones[name];
                Q lr = (driveNames.Contains(name) && perFrame.TryGetValue(b.Tag, out var pf)) ? pf[f] : b.RestRot;
                V lp = (name == rootName && hasRoot) ? b.RestPos + rootMotion![f] : b.RestPos;
                if (b.Parent == "" || !gtaBones.ContainsKey(b.Parent)) { wR[name] = lr; wP[name] = lp; }
                else { wR[name] = wR[b.Parent] * lr; wP[name] = wP[b.Parent] + V.Transform(lp, wR[b.Parent]); }
            }
        }
        FkAt(0);
        float minY = float.MaxValue;
        if (gtaBones.ContainsKey(lFootName)) minY = Math.Min(minY, wP[lFootName].Y);
        if (gtaBones.ContainsKey(rFootName)) minY = Math.Min(minY, wP[rFootName].Y);
        const float ground = 0.02f;
        return minY > ground ? minY - ground : 0;
    }

    /// <summary>Detect planted frames from source foot world path (speed + height).</summary>
    private static bool[]? DetectPlantedFrames(V[] srcFootP, int frames)
    {
        var speed = new float[frames]; float maxSpeed = 0;
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int f = 0; f < frames; f++) { float y = srcFootP[f].Y; if (y < minY) minY = y; if (y > maxY) maxY = y; }
        for (int f = 1; f < frames; f++) { speed[f] = (srcFootP[f] - srcFootP[f - 1]).Length(); if (speed[f] > maxSpeed) maxSpeed = speed[f]; }
        if (maxSpeed < 1e-5f) return null;
        float thr = maxSpeed * 0.20f;
        float yGate = minY + (maxY - minY) * 0.35f;
        var planted = new bool[frames];
        for (int f = 0; f < frames; f++) planted[f] = speed[Math.Max(1, f)] < thr && srcFootP[f].Y <= yGate;
        for (int f = 1; f < frames - 3; f++)
            if (!planted[f] && planted[f - 1] && (planted[f + 1] || planted[f + 2] || planted[f + 3])) planted[f] = true;
        return planted;
    }

    /// <summary>While both feet are planted (idle stance), freeze horizontal
    /// root travel so pelvis sway from the source doesn't drag the ped.</summary>
    private static void ClampRootWhilePlanted(V[] rootMotion, bool[] plantedL, bool[] plantedR, int frames)
    {
        int i = 0;
        while (i < frames)
        {
            if (!(plantedL[i] && plantedR[i])) { i++; continue; }
            int start = i;
            var segAnchor = rootMotion[i];
            while (i < frames && plantedL[i] && plantedR[i]) i++;
            int end = i - 1;
            for (int f = start; f <= end; f++)
                rootMotion[f] = new V(segAnchor.X, rootMotion[f].Y, segAnchor.Z);
        }
    }

    // Interpolate a bone's animated TRANSLATION (root motion lives here); falls
    // back to the bind position for bones without a position channel.
    private static V SamplePos(Dictionary<string, NodeAnimationChannel> posChan, Bone b, string name, double t)
    {
        if (!posChan.TryGetValue(name, out var c) || c.PositionKeyCount == 0) return b.RestPos;
        var k = c.PositionKeys;
        if (t <= k[0].Time) return VV(k[0].Value);
        if (t >= k[k.Count - 1].Time) return VV(k[k.Count - 1].Value);
        for (int i = 1; i < k.Count; i++) { if (k[i].Time < t) continue; var a = k[i - 1]; var bb = k[i]; float u = (float)((t - a.Time) / (bb.Time - a.Time)); return V.Lerp(VV(a.Value), VV(bb.Value), u); }
        return VV(k[k.Count - 1].Value);
    }
    private static V VV(Assimp.Vector3D v) => new V(v.X, v.Y, v.Z);

    private static List<string> Ordered(Dictionary<string, Bone> tbl)
    {
        var done = new HashSet<string>(); var o = new List<string>();
        void Add(string n) { if (done.Contains(n)) return; var p = tbl[n].Parent; if (p != "" && tbl.ContainsKey(p) && !done.Contains(p)) Add(p); done.Add(n); o.Add(n); }
        foreach (var n in tbl.Keys) Add(n);
        return o;
    }

    private static Dictionary<string, string> PrimaryChild(Dictionary<string, Bone> tbl)
    {
        var kids = new Dictionary<string, List<string>>();
        foreach (var kv in tbl) { var p = kv.Value.Parent; if (p != "" && tbl.ContainsKey(p)) { if (!kids.ContainsKey(p)) kids[p] = new(); kids[p].Add(kv.Key); } }
        var res = new Dictionary<string, string>();
        foreach (var kv in tbl) if (kids.TryGetValue(kv.Key, out var cs)) { var tg = cs.FirstOrDefault(c => tbl[c].HasTag); if (tg != null) res[kv.Key] = tg; }
        return res;
    }

    private static Q SampleLocal(Dictionary<string, NodeAnimationChannel> chan, Bone b, string name, double t)
    {
        if (!chan.TryGetValue(name, out var c)) return b.RestRot;
        var k = c.RotationKeys;
        if (t <= k[0].Time) return NQ(k[0].Value);
        if (t >= k[k.Count - 1].Time) return NQ(k[k.Count - 1].Value);
        for (int i = 1; i < k.Count; i++) { if (k[i].Time < t) continue; var a = k[i - 1]; var bb = k[i]; float u = (float)((t - a.Time) / (bb.Time - a.Time)); return Q.Normalize(Q.Slerp(NQ(a.Value), NQ(bb.Value), u)); }
        return NQ(k[k.Count - 1].Value);
    }

    private static V? DirT(Dictionary<string, Bone> tbl, Dictionary<string, V> pos, ushort a, ushort b)
    {
        var na = tbl.FirstOrDefault(k => k.Value.HasTag && k.Value.Tag == a).Key;
        var nb = tbl.FirstOrDefault(k => k.Value.HasTag && k.Value.Tag == b).Key;
        if (na == null || nb == null) return null;
        return Norm(pos[nb] - pos[na]);
    }

    private static Q DeriveAlign(V uS, V sS, V uT, V sT) => MatToQuat(Mul(Basis(sT, uT), Transpose(Basis(sS, uS))));
    private static float[] Basis(V side, V up) { var y = Norm(up); var x = Norm(side - y * V.Dot(side, y)); var z = V.Cross(x, y); return new[] { x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z }; }
    private static float[] Mul(float[] a, float[] b) { var r = new float[9]; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) { float s = 0; for (int k = 0; k < 3; k++) s += a[i * 3 + k] * b[k * 3 + j]; r[i * 3 + j] = s; } return r; }
    private static float[] Transpose(float[] a) => new[] { a[0], a[3], a[6], a[1], a[4], a[7], a[2], a[5], a[8] };
    private static Q MatToQuat(float[] m)
    {
        float tr = m[0] + m[4] + m[8]; Q q;
        if (tr > 0) { float s = (float)Math.Sqrt(tr + 1) * 2; q = new Q((m[7] - m[5]) / s, (m[2] - m[6]) / s, (m[3] - m[1]) / s, 0.25f * s); }
        else if (m[0] > m[4] && m[0] > m[8]) { float s = (float)Math.Sqrt(1 + m[0] - m[4] - m[8]) * 2; q = new Q(0.25f * s, (m[1] + m[3]) / s, (m[2] + m[6]) / s, (m[7] - m[5]) / s); }
        else if (m[4] > m[8]) { float s = (float)Math.Sqrt(1 + m[4] - m[0] - m[8]) * 2; q = new Q((m[1] + m[3]) / s, 0.25f * s, (m[5] + m[7]) / s, (m[2] - m[6]) / s); }
        else { float s = (float)Math.Sqrt(1 + m[8] - m[0] - m[4]) * 2; q = new Q((m[2] + m[6]) / s, (m[5] + m[7]) / s, 0.25f * s, (m[3] - m[1]) / s); }
        return Q.Normalize(q);
    }

    private static Q FromTo(V a, V b)
    {
        a = Norm(a); b = Norm(b); float d = V.Dot(a, b);
        if (d > 0.99999f) return Q.Identity;
        if (d < -0.99999f) { var ax = V.Cross(V.UnitX, a); if (ax.LengthSquared() < 1e-6f) ax = V.Cross(V.UnitY, a); return Q.CreateFromAxisAngle(Norm(ax), (float)Math.PI); }
        var c = V.Cross(a, b); return Q.Normalize(new Q(c.X, c.Y, c.Z, 1 + d));
    }

    private static double AngleTo(Q a, Q b) { var d = Q.Normalize(Q.Inverse(b) * a); return 2 * Math.Acos(Math.Min(1, Math.Abs(d.W))); }
    private static V Norm(V v) => v.LengthSquared() < 1e-12f ? v : V.Normalize(v);
    private static Q NQ(Assimp.Quaternion q) => Q.Normalize(new Q(q.X, q.Y, q.Z, q.W));
    private static string StripPivot(string nm) { int p = nm.IndexOf("_$AssimpFbx$_", StringComparison.Ordinal); return p >= 0 ? nm.Substring(0, p) : nm; }
    private static Node? FindNode(Node n, string name) { if (n.Name == name) return n; for (int i = 0; i < n.ChildCount; i++) { var f = FindNode(n.Children[i], name); if (f != null) return f; } return null; }

    private static Scene? LoadGtaScene(List<string> warnings)
    {
        try
        {
            var glb = Path.Combine(RuntimeAssets.ViewerDir, "reference", "freemode_male.glb");
            if (!File.Exists(glb)) glb = Path.Combine(RuntimeAssets.ViewerDir, "reference", "freemode_female.glb");
            if (!File.Exists(glb)) { warnings.Add("GTA reference skeleton not found."); return null; }
            // `using`: AssimpContext holds a native importer; ImportFile returns a
            // fully-marshaled managed Scene that stays valid after the context is
            // disposed, so dispose it here instead of leaking one native context +
            // scene per retarget (matches every other call site, e.g.
            // AnimEmoteImporter.LoadGtaBindPose).
            using var ctx = new AssimpContext();
            return ctx.ImportFile(glb, PostProcessSteps.None);
        }
        catch (Exception ex) { warnings.Add("Couldn't read GTA reference skeleton: " + ex.Message); return null; }
    }
}
