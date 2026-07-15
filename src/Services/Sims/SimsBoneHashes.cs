// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;

namespace FiveOS.Services.Sims;

/// <summary>FNV-32 bone-name hashes for the Sims 4 adult humanoid rig
/// (from community tables used by s4animtools).</summary>
public static class SimsBoneHashes
{
    /// <summary>Hash → canonical Sims bone name (e.g. b__Pelvis__).</summary>
    public static readonly IReadOnlyDictionary<uint, string> ByHash;

    /// <summary>Approximate parent map for BVH hierarchy (child → parent).</summary>
    public static readonly IReadOnlyDictionary<string, string?> Parents;

    static SimsBoneHashes()
    {
        var byHash = new Dictionary<uint, string>();
        void Add(string name, uint hash)
        {
            byHash[hash] = name;
            // Also register lowercase FNV (s4animtools hashes lowercase by default).
            byHash[Fnv32(name.ToLowerInvariant())] = name;
        }

        // Core
        Add("b__ROOT__", 0xfeae6981);
        Add("b__ROOT_bind__", 0x57884bb9);
        Add("b__Pelvis__", 0x556b181a);
        Add("b__Spine0__", 0x6fa96266);
        Add("b__Spine1__", 0xafac05cf);
        Add("b__Spine2__", 0x6faf7238);
        Add("b__Neck__", 0xbc81d5b8);
        Add("b__Head__", 0x0f97b21b);

        // Arms R
        Add("b__R_Clavicle__", 0x646ea315);
        Add("b__R_UpperArm__", 0xa92b596e);
        Add("b__R_Forearm__", 0xf0143b40);
        Add("b__R_Hand__", 0xceb0355b);
        Add("b__R_Thumb0__", 0xd2a9e720);
        Add("b__R_Thumb1__", 0x92abc109);
        Add("b__R_Thumb2__", 0x92a505ce);
        Add("b__R_Index0__", 0xeb208104);
        Add("b__R_Index1__", 0xeb22bfad);
        Add("b__R_Index2__", 0x2b1c68b2);
        Add("b__R_Mid0__", 0xbd2ce17e);
        Add("b__R_Mid1__", 0xbd2f1fe7);
        Add("b__R_Mid2__", 0x7d30f9d0);
        Add("b__R_Ring0__", 0x1ca23c66);
        Add("b__R_Ring1__", 0x5ca4dfcf);
        Add("b__R_Ring2__", 0x1ca84c38);
        Add("b__R_Pinky0__", 0xa8b5e7d3);
        Add("b__R_Pinky1__", 0xa8b3a8aa);
        Add("b__R_Pinky2__", 0xa8ba64e5);

        // Arms L
        Add("b__L_Clavicle__", 0xa303ce83);
        Add("b__L_UpperArm__", 0x0c9e57d0);
        Add("b__L_Forearm__", 0x15af037e);
        Add("b__L_Hand__", 0x7ccd6d29);
        Add("b__L_Thumb0__", 0xeff5bb2e);
        Add("b__L_Thumb1__", 0xaff79517);
        Add("b__L_Thumb2__", 0xaff9d380);
        Add("b__L_Index0__", 0xb1f508aa);
        Add("b__L_Index1__", 0xb1f747d3);
        Add("b__L_Index2__", 0xf1f9eabc);
        Add("b__L_Mid0__", 0x27bfbad0);
        Add("b__L_Mid1__", 0x27c1f979);
        Add("b__L_Mid2__", 0x67bba27e);
        Add("b__L_Ring0__", 0x17472b18);
        Add("b__L_Ring1__", 0x17496981);
        Add("b__L_Ring2__", 0x17411b46);
        Add("b__L_Pinky0__", 0xb3881055);
        Add("b__L_Pinky1__", 0xf386366c);
        Add("b__L_Pinky2__", 0xf383f7c3);

        // Legs
        Add("b__L_Thigh__", 0xc6035e5e);
        Add("b__L_Calf__", 0x85e195d0);
        Add("b__L_Foot__", 0x23b79422);
        Add("b__L_Toe__", 0xfbcf5c32);
        Add("b__R_Thigh__", 0x83b1355c);
        Add("b__R_Calf__", 0x81b330de);
        Add("b__R_Foot__", 0x37bb051c);
        Add("b__R_Toe__", 0xa6f3b078);

        ByHash = byHash;

        Parents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["b__ROOT__"] = null,
            ["b__ROOT_bind__"] = "b__ROOT__",
            ["b__Pelvis__"] = "b__ROOT_bind__",
            ["b__Spine0__"] = "b__Pelvis__",
            ["b__Spine1__"] = "b__Spine0__",
            ["b__Spine2__"] = "b__Spine1__",
            ["b__Neck__"] = "b__Spine2__",
            ["b__Head__"] = "b__Neck__",
            ["b__L_Clavicle__"] = "b__Spine2__",
            ["b__L_UpperArm__"] = "b__L_Clavicle__",
            ["b__L_Forearm__"] = "b__L_UpperArm__",
            ["b__L_Hand__"] = "b__L_Forearm__",
            ["b__L_Thumb0__"] = "b__L_Hand__",
            ["b__L_Thumb1__"] = "b__L_Thumb0__",
            ["b__L_Thumb2__"] = "b__L_Thumb1__",
            ["b__L_Index0__"] = "b__L_Hand__",
            ["b__L_Index1__"] = "b__L_Index0__",
            ["b__L_Index2__"] = "b__L_Index1__",
            ["b__L_Mid0__"] = "b__L_Hand__",
            ["b__L_Mid1__"] = "b__L_Mid0__",
            ["b__L_Mid2__"] = "b__L_Mid1__",
            ["b__L_Ring0__"] = "b__L_Hand__",
            ["b__L_Ring1__"] = "b__L_Ring0__",
            ["b__L_Ring2__"] = "b__L_Ring1__",
            ["b__L_Pinky0__"] = "b__L_Hand__",
            ["b__L_Pinky1__"] = "b__L_Pinky0__",
            ["b__L_Pinky2__"] = "b__L_Pinky1__",
            ["b__R_Clavicle__"] = "b__Spine2__",
            ["b__R_UpperArm__"] = "b__R_Clavicle__",
            ["b__R_Forearm__"] = "b__R_UpperArm__",
            ["b__R_Hand__"] = "b__R_Forearm__",
            ["b__R_Thumb0__"] = "b__R_Hand__",
            ["b__R_Thumb1__"] = "b__R_Thumb0__",
            ["b__R_Thumb2__"] = "b__R_Thumb1__",
            ["b__R_Index0__"] = "b__R_Hand__",
            ["b__R_Index1__"] = "b__R_Index0__",
            ["b__R_Index2__"] = "b__R_Index1__",
            ["b__R_Mid0__"] = "b__R_Hand__",
            ["b__R_Mid1__"] = "b__R_Mid0__",
            ["b__R_Mid2__"] = "b__R_Mid1__",
            ["b__R_Ring0__"] = "b__R_Hand__",
            ["b__R_Ring1__"] = "b__R_Ring0__",
            ["b__R_Ring2__"] = "b__R_Ring1__",
            ["b__R_Pinky0__"] = "b__R_Hand__",
            ["b__R_Pinky1__"] = "b__R_Pinky0__",
            ["b__R_Pinky2__"] = "b__R_Pinky1__",
            ["b__L_Thigh__"] = "b__Pelvis__",
            ["b__L_Calf__"] = "b__L_Thigh__",
            ["b__L_Foot__"] = "b__L_Calf__",
            ["b__L_Toe__"] = "b__L_Foot__",
            ["b__R_Thigh__"] = "b__Pelvis__",
            ["b__R_Calf__"] = "b__R_Thigh__",
            ["b__R_Foot__"] = "b__R_Calf__",
            ["b__R_Toe__"] = "b__R_Foot__",
        };
    }

    public static uint Fnv32(string text)
    {
        uint h = 0x811c9dc5;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            h *= 0x01000193;
            h ^= b;
        }
        return h;
    }

    public static bool TryGetName(uint hash, out string name) => ByHash.TryGetValue(hash, out name!);
}
