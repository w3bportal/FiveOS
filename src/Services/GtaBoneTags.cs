// Copyright (c) 2026 FiveOS. All rights reserved.
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
        // 57005 verified against real clip data (YcdBoneTagProbe): the
        // previous value here (6286) is IK_R_Hand's tag — it made imports
        // drive the right hand from the IK helper track and exports write
        // the right hand AS the IK helper.
        ["SKEL_R_Hand"]          = 57005,
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
        // Auxiliary body bones — roll/twist (RB_), corrective (MH_),
        // prop-attach (PH_) and IK helpers. Real clips animate these (the
        // wymianakola probe shows every one of them with live tracks);
        // without table entries their tracks import as "unmapped" and limbs
        // tear at the twist bands. Tags verified against that clip's
        // BoneIds via YcdBoneTagProbe + the published freemode bone list.
        ["RB_L_ThighRoll"]       = 23639,
        ["RB_R_ThighRoll"]       = 6442,
        ["RB_L_ArmRoll"]         = 5232,
        ["RB_R_ArmRoll"]         = 37119,
        ["RB_L_ForeArmRoll"]     = 61007,
        ["RB_R_ForeArmRoll"]     = 43810,
        ["RB_Neck_1"]            = 35731,
        ["MH_L_Elbow"]           = 22711,
        ["MH_R_Elbow"]           = 2992,
        ["MH_L_Knee"]            = 46078,
        ["MH_R_Knee"]            = 16335,
        ["PH_L_Hand"]            = 60309,
        ["PH_R_Hand"]            = 28422,
        ["PH_L_Foot"]            = 57717,
        ["PH_R_Foot"]            = 24806,
        ["IK_L_Hand"]            = 36029,
        ["IK_R_Hand"]            = 6286,
        ["IK_L_Foot"]            = 65245,
        ["IK_R_Foot"]            = 35502,
        ["IK_Head"]              = 12844,
        ["IK_Root"]              = 56604,
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

    /// <summary>RAGE bone tag from a bone name — delegates to CodeWalker's
    /// <c>Bone.CalculateBoneHash</c>. Only consulted for aux bones that
    /// are NOT in <see cref="ByGtaName"/> (the table's hand-verified tags
    /// from real clip probes always win via the direct match above).</summary>
    public static ushort CalculateBoneTag(string boneName)
        => CodeWalker.GameFiles.Bone.CalculateBoneHash(boneName);

    /// <summary>The freemode skeleton's auxiliary (non-SKEL_) body bones —
    /// roll/twist, corrective, prop-attach and IK helpers. Real clips carry
    /// twist animation on the RB_* bones; hosts append these to the rig
    /// name list when importing a .ycd so those tracks map even though the
    /// pose sidebar hides the bones.</summary>
    public static readonly string[] FreemodeAuxBoneNames =
    {
        "RB_L_ThighRoll", "RB_R_ThighRoll",
        "RB_L_ArmRoll", "RB_R_ArmRoll",
        "RB_L_ForeArmRoll", "RB_R_ForeArmRoll",
        "RB_Neck_1",
        "MH_L_Elbow", "MH_R_Elbow",
        "MH_L_Knee", "MH_R_Knee",
        "MH_L_CalfBack", "MH_R_CalfBack",
        "MH_L_ThighBack", "MH_R_ThighBack",
        "MH_L_HandSide", "MH_R_HandSide",
        "PH_L_Hand", "PH_R_Hand",
        "PH_L_Foot", "PH_R_Foot",
        "IK_L_Hand", "IK_R_Hand",
        "IK_L_Foot", "IK_R_Foot",
        "IK_Head", "IK_Root",
    };

    // ── Custom bone-name aliases (user-editable) ─────────────────────
    // Loaded once from custom-bone-map.json in %APPDATA%\FiveOS or next
    // to the exe — the Rokoko Studio / Rokoko Blender plugin "custom
    // bone names" JSON format ({"bones": {slotOrCustom: [names…]}}).
    // Lets users teach the importer a new rig convention without a
    // rebuild: any name in an entry that is itself a canonical SKEL_*
    // bone is the TARGET; the entry key (minus the custom_bone_ prefix)
    // and every other name are SOURCES mapped onto it. Checked before
    // the built-in heuristics so an explicit user mapping always wins.
    private static readonly object _customLock = new();
    private static Dictionary<string, ushort>? _customAliases;

    private static Dictionary<string, ushort> CustomAliases
    {
        get
        {
            if (_customAliases != null) return _customAliases;
            lock (_customLock)
            {
                if (_customAliases != null) return _customAliases;
                var map = new Dictionary<string, ushort>(System.StringComparer.OrdinalIgnoreCase);
                foreach (var dir in new[]
                         {
                             System.AppContext.BaseDirectory,
                             System.IO.Path.Combine(
                                 System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                                 "FiveOS"),
                         })
                {
                    try
                    {
                        var file = System.IO.Path.Combine(dir, "custom-bone-map.json");
                        if (System.IO.File.Exists(file))
                            LoadRokokoMapInto(map, System.IO.File.ReadAllText(file));
                    }
                    catch { /* a broken user file must never kill an import */ }
                }
                _customAliases = map;
                return map;
            }
        }
    }

    private static void LoadRokokoMapInto(Dictionary<string, ushort> map, string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("bones", out var bones)) return;
        foreach (var entry in bones.EnumerateObject())
        {
            var key = entry.Name;
            if (key.StartsWith("custom_bone_", System.StringComparison.OrdinalIgnoreCase))
                key = key.Substring("custom_bone_".Length);
            var sources = new List<string> { key };
            ushort target = 0;
            var hasTarget = false;
            if (entry.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var v in entry.Value.EnumerateArray())
                {
                    var s = v.GetString();
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    if (ByGtaName.TryGetValue(s, out var t))
                    {
                        if (!hasTarget) { target = t; hasTarget = true; }
                    }
                    else sources.Add(s);
                }
            }
            if (!hasTarget) continue;
            foreach (var src in sources)
                if (!ByGtaName.ContainsKey(src))
                    map[src] = target;
        }
    }

    /// <summary>Resolve via the canonical table or the user's
    /// custom-bone-map ONLY — no name heuristics. For importers whose joint
    /// names would mislead the heuristics (physics mocap: "Left_hip" contains
    /// "hip" → Pelvis, "Root" → SKEL_ROOT) but whose users may still pin an
    /// explicit mapping in custom-bone-map.json. An explicit mapping wins;
    /// absence returns false instead of guessing.</summary>
    public static bool TryResolveCustom(string boneName, out ushort tag)
    {
        tag = 0;
        if (string.IsNullOrWhiteSpace(boneName)) return false;
        var n = boneName.Trim();
        if (ByGtaName.TryGetValue(n, out tag)) return true;
        return CustomAliases.Count > 0 && CustomAliases.TryGetValue(n, out tag);
    }

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

        // Explicit user mapping (custom-bone-map.json) beats every heuristic.
        if (CustomAliases.Count > 0 && CustomAliases.TryGetValue(boneName.Trim(), out tag)) return true;

        // Namespaced Maya/mocap FBX namespaces land as "_1:Hips", "Character1:Spine".
        // Strip BEFORE trailing "_1" dedupe and BEFORE spine-digit heuristics —
        // otherwise "_1:Spine" → "_1_Spine" and the leftover "1" falsely maps
        // to SKEL_Spine1 (collapsing the torso on namespaced pre-retarget FBXs).
        var withoutNs = System.Text.RegularExpressions.Regex.Replace(
            boneName.Trim(), @"^_?\d+:", "");
        withoutNs = System.Text.RegularExpressions.Regex.Replace(
            withoutNs, @"^[A-Za-z][A-Za-z0-9]*:", "");
        if (withoutNs != boneName.Trim())
        {
            if (ByGtaName.TryGetValue(withoutNs, out tag)) return true;
            if (CustomAliases.Count > 0 && CustomAliases.TryGetValue(withoutNs, out tag)) return true;
        }

        // Sollumz/Blender often duplicates skeletons across components, so
        // it appends "_1" / ".001" / "_dup_" to bone names to keep them
        // unique within a single export. Strip those before matching --
        // we don't care which mesh component "owns" the bone, only what
        // SKEL_* it corresponds to.
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            withoutNs, @"(_\d+|\.\d+|_dup_\d*)+$", "");
        if (stripped != boneName && ByGtaName.TryGetValue(stripped, out tag)) return true;
        if (stripped != boneName && CustomAliases.Count > 0 && CustomAliases.TryGetValue(stripped, out tag)) return true;


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
        // Sims 4 adult rig: b__Pelvis__, b__L_UpperArm__, …
        if (n.StartsWith("b__", System.StringComparison.OrdinalIgnoreCase))
            n = n.Substring(3);
        if (n.EndsWith("__", System.StringComparison.Ordinal))
            n = n[..^2];
        n = n.Replace(":", "_").Replace(".", "_").Replace("-", "_");
        // Maya-exported game rigs (Rokoko-style "*_jnt") and Blender
        // "*_bind" suffixes carry no meaning — hips_jnt IS the hips.
        n = System.Text.RegularExpressions.Regex.Replace(
            n, @"_(jnt|bind)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
        // Sims b__ROOT__ / b__ROOT_bind__ (after strip) → SKEL_ROOT.
        if (lower is "root" or "root_bind") { tag = ByGtaName["SKEL_ROOT"]; return true; }
        if (lower.Contains("hip") || lower.Contains("pelvis")) { tag = ByGtaName["SKEL_Pelvis"]; return true; }
        // Spine index must be the TRAILING digit on the bone name (Spine1,
        // spine_2). Never use Contains("1") — Maya/Move prefixes and random
        // digits elsewhere used to steal SKEL_Spine1.
        if (lower.Contains("spine") && lower.Contains("root"))
        {
            tag = ByGtaName["SKEL_Spine_Root"]; return true;
        }
        var spineNum = System.Text.RegularExpressions.Regex.Match(lower, @"spine_?(\d+)$");
        if (spineNum.Success)
        {
            tag = spineNum.Groups[1].Value switch
            {
                "0" => ByGtaName["SKEL_Spine0"],
                "1" => ByGtaName["SKEL_Spine1"],
                "2" => ByGtaName["SKEL_Spine2"],
                "3" => ByGtaName["SKEL_Spine3"],
                _ => ByGtaName["SKEL_Spine0"],
            };
            return true;
        }
        if (lower.Contains("waist") || lower == "spine" || lower.EndsWith("_spine"))
        {
            tag = ByGtaName["SKEL_Spine0"]; return true;   // Mixamo/Move "Spine", CC "Waist"
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
                // Rokoko slot names spell the joint out: Proximal / Medial
                // (a.k.a. Intermediate) / Distal — no digits at all.
                if (lower.Contains("proximal")) joint = 0;
                else if (lower.Contains("medial") || lower.Contains("intermediate")) joint = 1;
                else if (lower.Contains("distal")) joint = 2;
                else
                {
                    var dm = System.Text.RegularExpressions.Regex.Match(lower, @"(\d)\D*$");
                    if (dm.Success)
                    {
                        int j = dm.Groups[1].Value[0] - '0';
                        // Sims adult rig: L_Thumb0/1/2 (0-based). Mixamo
                        // LeftHandThumb1/2/3 and Maya-style l_handthumb1_jnt
                        // are 1-based — the "hand" in the name is the tell,
                        // so an l_/r_ prefix alone doesn't mean 0-based.
                        bool simsStyle = System.Text.RegularExpressions.Regex.IsMatch(lower, @"^[lr]_")
                            && !lower.Contains("hand");
                        joint = System.Math.Clamp(simsStyle ? j : (j > 0 ? j - 1 : 0), 0, 2);
                    }
                }
                var fkey = Side($"Finger{digit}{joint}", $"Finger{digit}{joint}");
                if (fkey != null && ByGtaName.TryGetValue(fkey, out tag)) return true;
            }
        }

        // Physics mocap / Move-style: Left_shoulder_rotation is the UPPER ARM
        // (no separate clavicle). Must beat the generic "shoulder" → Clavicle
        // rule below or arms collapse into the neck.
        if (lower.Contains("shoulder_rotation")
            || lower.Contains("upperarm") || lower.Contains("upper_arm")
            || (lower.Contains("arm") && !lower.Contains("forearm") && !lower.Contains("lower")
                && !lower.Contains("shoulder")))
        {
            key = Side("UpperArm", "UpperArm");
            if (key != null) { tag = ByGtaName[key]; return true; }
        }
        // Elbow before clavicle/shoulder — "Left_elbow" has no forearm token.
        if (lower.Contains("elbow") || lower.Contains("forearm")
            || lower.Contains("lowerarm") || lower.Contains("lower_arm"))
        {
            key = Side("Forearm", "Forearm");
            if (key != null) { tag = ByGtaName[key]; return true; }
        }
        // Mixamo LeftShoulder / explicit clavicle → Clavicle (not UpperArm).
        if (lower.Contains("clavicle") || lower.Contains("shoulder"))
        {
            key = Side("Clavicle", "Clavicle");
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
