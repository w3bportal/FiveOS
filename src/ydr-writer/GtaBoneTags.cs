// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace YdrWriter;

/// <summary>
/// Engine-side copy of FiveOS.Services.GtaBoneTags. Stays in sync by hand
/// because the engine ships as a self-contained .exe and can't reference
/// the shell's services assembly. Same table, same TryResolve heuristics.
/// </summary>
internal static class GtaBoneTags
{
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
        ["SKEL_L_Clavicle"]      = 64729,
        ["SKEL_L_UpperArm"]      = 45509,
        ["SKEL_L_Forearm"]       = 61163,
        ["SKEL_L_Hand"]          = 18905,
        ["SKEL_R_Clavicle"]      = 10706,
        ["SKEL_R_UpperArm"]      = 40269,
        ["SKEL_R_Forearm"]       = 28252,
        ["SKEL_R_Hand"]          = 6286,
        ["SKEL_L_Thigh"]         = 58271,
        ["SKEL_L_Calf"]          = 63931,
        ["SKEL_L_Foot"]          = 14201,
        ["SKEL_L_Toe0"]          = 2108,
        ["SKEL_R_Thigh"]         = 51826,
        ["SKEL_R_Calf"]          = 36864,
        ["SKEL_R_Foot"]          = 52301,
        ["SKEL_R_Toe0"]          = 20781,
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

    public static bool TryResolve(string boneName, out ushort tag)
    {
        tag = 0;
        if (string.IsNullOrWhiteSpace(boneName)) return false;

        if (ByGtaName.TryGetValue(boneName, out tag)) return true;

        var stripped = Regex.Replace(boneName, @"(_\d+|\.\d+|_dup_\d*)+$", "");
        if (stripped != boneName && ByGtaName.TryGetValue(stripped, out tag)) return true;

        var n = stripped.Trim();
        foreach (var pfx in new[] { "mixamorig:", "mixamorig_", "Armature|", "J_Bip_", "J_Sec_", "DEF-", "ORG-" })
        {
            if (n.StartsWith(pfx, System.StringComparison.OrdinalIgnoreCase))
                n = n.Substring(pfx.Length);
        }
        n = n.Replace(":", "_").Replace(".", "_").Replace("-", "_");

        var bare = n;
        if (bare.StartsWith("SKEL_", System.StringComparison.OrdinalIgnoreCase)) bare = bare.Substring(5);

        var lower = bare.ToLowerInvariant();

        bool isLeft  = lower.Contains("left") || lower.StartsWith("l_") || lower.EndsWith("_l") || lower.EndsWith(".l");
        bool isRight = lower.Contains("right") || lower.StartsWith("r_") || lower.EndsWith("_r") || lower.EndsWith(".r");

        if (lower.Contains("hips") || lower.Contains("pelvis")) { tag = ByGtaName["SKEL_Pelvis"]; return true; }
        if (lower.Contains("spine") && lower.Contains("3")) { tag = ByGtaName["SKEL_Spine3"]; return true; }
        if (lower.Contains("spine") && lower.Contains("2")) { tag = ByGtaName["SKEL_Spine2"]; return true; }
        if (lower.Contains("spine") && lower.Contains("1")) { tag = ByGtaName["SKEL_Spine1"]; return true; }
        if (lower.Contains("spine") && (lower.Contains("0") || lower.EndsWith("spine") || lower == "spine"))
        {
            tag = ByGtaName["SKEL_Spine0"]; return true;
        }
        if (lower.Contains("neck")) { tag = ByGtaName["SKEL_Neck_1"]; return true; }
        if (lower.Contains("head") && !lower.Contains("forehead")) { tag = ByGtaName["SKEL_Head"]; return true; }

        string? Side(string l, string r) => isLeft ? "SKEL_L_" + l : isRight ? "SKEL_R_" + r : null;
        string? key;

        if (lower.Contains("clavicle") || lower.Contains("shoulder"))
        {
            key = Side("Clavicle", "Clavicle");
            if (key != null) { tag = ByGtaName[key]; return true; }
        }
        if (lower.Contains("upperarm") || lower.Contains("upper_arm") || (lower.Contains("arm") && !lower.Contains("forearm") && !lower.Contains("lower")))
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
