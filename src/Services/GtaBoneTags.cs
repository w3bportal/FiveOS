// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;

namespace FiveOS.Services;

/// <summary>
/// 16-bit RAGE bone tag table for the GTA V male/female player skeleton.
/// These are the values RAGE expects in <c>AnimationBoneId.BoneId</c> and
/// are derived from the SKEL_* names by Rockstar's custom 16-bit string
/// hash (NOT plain Jenkins). The constants below are taken from
/// CodeWalker's published skeleton dumps and confirmed against the
/// dpemotes pack — the same values that appear in shipping FiveM emotes.
///
/// <see cref="TryResolve"/> accepts the bone name as seen in a loaded
/// .glb/.fbx (GTA SKEL_*, Mixamo mixamorig:*, VRM J_Bip_*, or generic
/// Left/Right/Spine/etc.) and best-effort maps it to the GTA tag.
/// Anything we can't resolve is skipped — the result still plays, just
/// without those bones contributing to the pose.
/// </summary>
public static class GtaBoneTags
{
    /// <summary>Canonical GTA SKEL_* name -> RAGE bone tag.</summary>
    public static readonly IReadOnlyDictionary<string, ushort> ByGtaName = new Dictionary<string, ushort>(System.StringComparer.OrdinalIgnoreCase)
    {
        ["SKEL_ROOT"]            = 0,
        ["SKEL_Pelvis"]          = 11816,
        ["SKEL_Spine_Root"]      = 57597,
        ["SKEL_Spine0"]          = 23553,
        ["SKEL_Spine1"]          = 24816,
        ["SKEL_Spine2"]          = 24817,
        ["SKEL_Spine3"]          = 24818,
        ["SKEL_Neck_1"]          = 39317,
        ["SKEL_Head"]            = 31086,
        // Arms — left
        ["SKEL_L_Clavicle"]      = 64729,
        ["SKEL_L_UpperArm"]      = 45509,
        ["SKEL_L_Forearm"]       = 61163,
        ["SKEL_L_Hand"]          = 18905,
        // Arms — right
        ["SKEL_R_Clavicle"]      = 10706,
        ["SKEL_R_UpperArm"]      = 40269,
        ["SKEL_R_Forearm"]       = 28252,
        ["SKEL_R_Hand"]          = 6286,
        // Legs — left
        ["SKEL_L_Thigh"]         = 58271,
        ["SKEL_L_Calf"]          = 63931,
        ["SKEL_L_Foot"]          = 14201,
        ["SKEL_L_Toe0"]          = 2108,
        // Legs — right
        ["SKEL_R_Thigh"]         = 51826,
        ["SKEL_R_Calf"]          = 36864,
        ["SKEL_R_Foot"]          = 52301,
        ["SKEL_R_Toe0"]          = 20781,
        // Fingers — left (thumb, index, middle, ring, pinky × 3 joints)
        ["SKEL_L_Finger00"]      = 26610,
        ["SKEL_L_Finger01"]      = 4089,
        ["SKEL_L_Finger02"]      = 4090,
        ["SKEL_L_Finger10"]      = 26611,
        ["SKEL_L_Finger11"]      = 4169,
        ["SKEL_L_Finger12"]      = 4170,
        ["SKEL_L_Finger20"]      = 26612,
        ["SKEL_L_Finger21"]      = 4185,
        ["SKEL_L_Finger22"]      = 4186,
        ["SKEL_L_Finger30"]      = 26613,
        ["SKEL_L_Finger31"]      = 4153,
        ["SKEL_L_Finger32"]      = 4154,
        ["SKEL_L_Finger40"]      = 26614,
        ["SKEL_L_Finger41"]      = 4137,
        ["SKEL_L_Finger42"]      = 4138,
        // Fingers — right
        ["SKEL_R_Finger00"]      = 58866,
        ["SKEL_R_Finger01"]      = 64016,
        ["SKEL_R_Finger02"]      = 64017,
        ["SKEL_R_Finger10"]      = 58867,
        ["SKEL_R_Finger11"]      = 64096,
        ["SKEL_R_Finger12"]      = 64097,
        ["SKEL_R_Finger20"]      = 58868,
        ["SKEL_R_Finger21"]      = 64112,
        ["SKEL_R_Finger22"]      = 64113,
        ["SKEL_R_Finger30"]      = 58869,
        ["SKEL_R_Finger31"]      = 64080,
        ["SKEL_R_Finger32"]      = 64081,
        ["SKEL_R_Finger40"]      = 58870,
        ["SKEL_R_Finger41"]      = 64064,
        ["SKEL_R_Finger42"]      = 64065,
    };

    /// <summary>Reverse of <see cref="ByGtaName"/> for the canonical SKEL_*
    /// names — used to turn a baked track's tag back into the bone name the
    /// 3D viewer poses by (Animation → Emote preview).</summary>
    private static readonly Dictionary<ushort, string> _byTag = BuildByTag();
    private static Dictionary<ushort, string> BuildByTag()
    {
        var m = new Dictionary<ushort, string>();
        foreach (var kv in ByGtaName)
            if (kv.Key.StartsWith("SKEL_", System.StringComparison.OrdinalIgnoreCase) && !m.ContainsKey(kv.Value))
                m[kv.Value] = kv.Key;
        return m;
    }
    /// <summary>Canonical SKEL_* name for a tag, or null.</summary>
    public static string? NameForTag(ushort tag) => _byTag.TryGetValue(tag, out var n) ? n : null;

    /// <summary>
    /// Try to resolve any rig-flavoured bone name to a GTA bone tag.
    /// Returns false (and 0) for bones we can't map — caller should skip
    /// those when building the .ycd rather than failing the whole export.
    /// </summary>
    public static bool TryResolve(string boneName, out ushort tag)
    {
        tag = 0;
        if (string.IsNullOrWhiteSpace(boneName)) return false;

        // Direct match on the canonical name.
        if (ByGtaName.TryGetValue(boneName, out tag)) return true;

        // Sollumz/Blender often duplicates skeletons across components, so
        // it appends "_1" / ".001" / "_dup_" to bone names to keep them
        // unique within a single export. Strip those before matching --
        // we don't care which mesh component "owns" the bone, only what
        // SKEL_* it corresponds to.
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            boneName, @"(_\d+|\.\d+|_dup_\d*)+$", "");
        if (stripped != boneName && ByGtaName.TryGetValue(stripped, out tag)) return true;

        // Normalise: strip common rig prefixes, fold case, swap separators.
        var n = stripped.Trim();
        // Mixamo versions the namespace: "mixamorig:", "mixamorig1:",
        // "mixamorig8:" … — strip "mixamorig" + optional digits + separator.
        n = System.Text.RegularExpressions.Regex.Replace(
            n, @"^mixamorig\d*[:_]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // ActorCore / Character Creator / iClone rigs prefix every bone with
        // "CC_Base_" (e.g. CC_Base_L_Upperarm, CC_Base_L_Index1, CC_Base_Hip).
        foreach (var pfx in new[] { "CC_Base_", "Armature|", "J_Bip_", "J_Sec_", "DEF-", "ORG-" })
        {
            if (n.StartsWith(pfx, System.StringComparison.OrdinalIgnoreCase))
                n = n.Substring(pfx.Length);
        }
        n = n.Replace(":", "_").Replace(".", "_").Replace("-", "_");

        // Build a normalised lookup key (no SKEL_ prefix, no L/R prefix order)
        // and match against the canonical SKEL_* names by suffix.
        var bare = n;
        if (bare.StartsWith("SKEL_", System.StringComparison.OrdinalIgnoreCase)) bare = bare.Substring(5);

        // Heuristics for the most common rig conventions.
        var lower = bare.ToLowerInvariant();

        // Non-skeletal helpers never map (face/breast/eye/jaw etc.).
        if (lower.Contains("facial") || lower.Contains("breast") || lower.Contains("eye")
            || lower.Contains("jaw") || lower.Contains("tongue")) return false;

        // Side markers — Mixamo uses "Left" / "Right" prefixes.
        bool isLeft  = lower.Contains("left") || lower.StartsWith("l_") || lower.Contains("_l_") || lower.EndsWith("_l") || lower.EndsWith(".l");
        bool isRight = lower.Contains("right") || lower.StartsWith("r_") || lower.Contains("_r_") || lower.EndsWith("_r") || lower.EndsWith(".r");

        // Core / spine / head — no side marker needed.
        if (lower.Contains("hip") || lower.Contains("pelvis")) { tag = ByGtaName["SKEL_Pelvis"]; return true; }
        if (lower.Contains("spine") && lower.Contains("3")) { tag = ByGtaName["SKEL_Spine3"]; return true; }
        if (lower.Contains("spine") && lower.Contains("2")) { tag = ByGtaName["SKEL_Spine2"]; return true; }
        if (lower.Contains("spine") && lower.Contains("1")) { tag = ByGtaName["SKEL_Spine1"]; return true; }
        if (lower.Contains("waist") || (lower.Contains("spine") && (lower.Contains("0") || lower.EndsWith("spine") || lower == "spine")))
        {
            tag = ByGtaName["SKEL_Spine0"]; return true;   // CC's "Waist" is the lower spine
        }
        if (lower.Contains("neck")) { tag = ByGtaName["SKEL_Neck_1"]; return true; }
        if (lower.Contains("head") && !lower.Contains("forehead")) { tag = ByGtaName["SKEL_Head"]; return true; }

        // Skip auxiliary roll/twist/share helper bones AFTER the core checks (so
        // CC's real NeckTwist/spine map above, but limb helpers — CalfTwist01,
        // ForearmTwist02, ElbowShareBone, ToeBaseShareBone — don't steal the
        // main bone's tag via the one-bone-per-tag dedup).
        if (lower.Contains("twist") || lower.Contains("share") || lower.Contains("adjust")) return false;

        // Arms.
        string Side(string l, string r) => isLeft ? "SKEL_L_" + l : isRight ? "SKEL_R_" + r : null!;
        string key;

        // Fingers FIRST — Mixamo names them "LeftHand<Finger><joint>" (e.g.
        // LeftHandIndex1), which contain "hand"/"index" etc., so they'd wrongly
        // hit the hand check below. GTA finger tags are SKEL_x_FingerDJ where
        // D = thumb0/index1/middle2/ring3/pinky4 and J = Mixamo joint 1/2/3 → 0/1/2.
        // "toe" guard: ActorCore names toes L_IndexToe1, L_MidToe1, L_RingToe1…
        // which contain finger keywords — they must fall through to the toe check,
        // not map to finger tags. CC also abbreviates middle as "Mid".
        if (!lower.Contains("toe"))
        {
            int digit = lower.Contains("thumb") ? 0 : lower.Contains("index") ? 1
                      : (lower.Contains("middle") || lower.Contains("mid")) ? 2 : lower.Contains("ring") ? 3
                      : (lower.Contains("pinky") || lower.Contains("little")) ? 4 : -1;
            if (digit >= 0)
            {
                int joint = 0;
                var dm = System.Text.RegularExpressions.Regex.Match(lower, @"(\d)\D*$");
                if (dm.Success) { int j = dm.Groups[1].Value[0] - '0'; joint = System.Math.Clamp(j > 0 ? j - 1 : 0, 0, 2); }
                var fkey = Side($"Finger{digit}{joint}", $"Finger{digit}{joint}");
                if (fkey != null && ByGtaName.TryGetValue(fkey, out tag)) return true;
            }
        }

        if (lower.Contains("clavicle") || lower.Contains("shoulder"))
        {
            key = Side("Clavicle", "Clavicle");
            if (key != null) { tag = ByGtaName[key]; return true; }
        }
        if (lower.Contains("upperarm") || lower.Contains("upper_arm") || lower.Contains("arm") && !lower.Contains("forearm") && !lower.Contains("lower"))
        {
            key = Side("UpperArm", "UpperArm");
            if (key != null) { tag = ByGtaName[key]; return true; }
        }
        if (lower.Contains("forearm") || lower.Contains("lowerarm") || lower.Contains("lower_arm"))
        {
            key = Side("Forearm", "Forearm");
            if (key != null) { tag = ByGtaName[key]; return true; }
        }
        if (lower.Contains("hand") || lower.Contains("wrist"))
        {
            key = Side("Hand", "Hand");
            if (key != null) { tag = ByGtaName[key]; return true; }
        }

        // Legs.
        if (lower.Contains("thigh") || lower.Contains("upleg"))
        {
            key = Side("Thigh", "Thigh");
            if (key != null) { tag = ByGtaName[key]; return true; }
        }
        if (lower.Contains("calf") || lower.Contains("shin") || (lower.Contains("leg") && !lower.Contains("upleg")))
        {
            key = Side("Calf", "Calf");
            if (key != null) { tag = ByGtaName[key]; return true; }
        }
        if (lower.Contains("foot") || lower.Contains("ankle"))
        {
            key = Side("Foot", "Foot");
            if (key != null) { tag = ByGtaName[key]; return true; }
        }
        if (lower.Contains("toe"))
        {
            key = Side("Toe0", "Toe0");
            if (key != null) { tag = ByGtaName[key]; return true; }
        }

        return false;
    }
}
