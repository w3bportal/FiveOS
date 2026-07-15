// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using CodeWalker.GameFiles;
using SharpDX;

namespace YdrWriter;

/// <summary>
/// Attaches a GTA-V weapon skeleton to an already-converted YDR drawable.
///
/// Why this layer exists: CW.Core's FbxConverter only reads geometry/
/// materials from the FBX — it does not parse FbxSkeleton/Cluster/Deformer
/// nodes (verified by grepping FbxConverter.cs: zero matches for those
/// keywords). So injecting bones into the FBX upstream would be silently
/// dropped. Instead we build the Skeleton in code after ConvertToYdr
/// returns, populate ydr.Drawable.Skeleton, and rebind every DrawableModel
/// to bone index 0 (gun_root) so the rigid weapon body rides the root
/// transform. The extra bones (gun_muzzle, gun_gripr, gun_magazine,
/// gun_vfx_eject) live purely as transform markers — game code reads their
/// world transforms for muzzle flash, hand IK, mag drop, and casing eject.
///
/// No per-vertex skinning is required for non-deforming weapons (sliding
/// bolts, dropping mags as separate models are a Phase-2 concern). The
/// per-DrawableModel SkeletonBinding byte is enough.
///
/// Bone tag hash is ported from Sollumz (ydr/properties.py:calc_tag_hash),
/// which uses a PJW/ELF-family variant — NOT Jenkins/JOAAT. Root bone is
/// special-cased to tag = 0 to match Rockstar's convention.
/// </summary>
public static class WeaponSkeletonInjector
{
    /// <summary>One bone in the weapon armature. The translation is in
    /// metres relative to the parent bone (which is always gun_root for
    /// the standard set).</summary>
    public readonly record struct WeaponBone(string Name, Vector3 Translation);

    /// <summary>Injection inputs. Offsets are in metres in the drawable's
    /// local space (Z-up, Y-forward by the time we get here — the
    /// Y-up→Z-up rotation has already been baked into vertex positions in
    /// Converter.cs stage 1c).</summary>
    public sealed record Options(
        Vector3 MuzzleOffset,
        Vector3 GripOffset,
        Vector3 MagazineOffset,
        Vector3 EjectOffset);

    /// <summary>The minimum-viable weapon skeleton. Five bones; everything
    /// parents to gun_root. This matches how base-game w_pi_pistol.ydr is
    /// authored — single rigid body bound to gun_root with attach-point
    /// markers for muzzle/grip/mag/eject.</summary>
    public static void Inject(YdrFile ydr, Options opts)
    {
        var drawable = ydr.Drawable;
        if (drawable == null)
            throw new InvalidOperationException("YDR has no Drawable to attach skeleton to.");

        // NextSiblingIndex forms a proper sibling chain among children of
        // the same parent: each child points at the next sibling, last one
        // points to -1. CW's BuildIndices does NOT compute this — it only
        // builds the skeleton-level Parent/Child arrays, leaving the
        // per-bone NextSibling field as authored.
        var bones = new[]
        {
            BuildBone(0, "gun_root",      parentIdx: -1, nextSibling: -1, translation: Vector3.Zero),
            BuildBone(1, "gun_muzzle",    parentIdx:  0, nextSibling:  2, translation: opts.MuzzleOffset),
            BuildBone(2, "gun_gripr",     parentIdx:  0, nextSibling:  3, translation: opts.GripOffset),
            BuildBone(3, "gun_magazine",  parentIdx:  0, nextSibling:  4, translation: opts.MagazineOffset),
            BuildBone(4, "gun_vfx_eject", parentIdx:  0, nextSibling: -1, translation: opts.EjectOffset),
        };

        var skel = new Skeleton
        {
            Bones = new SkeletonBonesBlock { Items = bones },
        };

        // CW's own pipeline (Skeleton.ReadXml) runs these five helpers in
        // this exact order after deserialising bones from XML — we mirror
        // it so a YDR built here round-trips identically through CW.
        skel.BuildIndices();
        skel.BuildBoneTags();
        skel.AssignBoneParents();
        skel.BuildTransformations();
        skel.BuildBonesMap();

        drawable.Skeleton = skel;

        // Rebind every model in every LOD to bone index 0 (gun_root). The
        // per-Model SkeletonBinding byte is what tells RAGE which bone's
        // world transform the model rides — for a rigid weapon, all
        // geometry sits on gun_root. HasSkin stays 0 because we don't
        // emit per-vertex blend weights.
        var lods = drawable.DrawableModels;
        if (lods != null)
        {
            RebindModelsToRoot(lods.High);
            RebindModelsToRoot(lods.Med);
            RebindModelsToRoot(lods.Low);
            RebindModelsToRoot(lods.VLow);
        }
    }

    private static void RebindModelsToRoot(DrawableModel[]? models)
    {
        if (models == null) return;
        foreach (var m in models)
        {
            if (m == null) continue;
            m.BoneIndex = 0;
            m.HasSkin = 0;
        }
    }

    private static Bone BuildBone(short index, string name, short parentIdx, short nextSibling, Vector3 translation)
    {
        // Attach-point bones get full rotation freedom and the "unbounded"
        // limit flag combo Rockstar uses on weapon skeletons. RotX/Y/Z let
        // animations/IK rotate the bone; TransX/Y/Z let scripts move it.
        // Without these flags some game code paths refuse to read the
        // bone's world transform.
        var flags = EBoneFlags.RotX | EBoneFlags.RotY | EBoneFlags.RotZ;
        return new Bone
        {
            Name = name,
            Tag = CalcBoneTag(name, isRoot: parentIdx < 0),
            Index = index,
            Index2 = index,
            ParentIndex = parentIdx,
            NextSiblingIndex = nextSibling,
            Translation = translation,
            Rotation = Quaternion.Identity,
            Scale = new Vector3(1f, 1f, 1f),
            TransformUnk = new Vector4(0f, 0f, 0f, 1f),
            Flags = flags,
        };
    }

    /// <summary>PJW/ELF-family hash, ported from Sollumz
    /// ydr/properties.py:calc_tag_hash. Output is a 16-bit value in
    /// [0x170, 0x170 + 0xFE8F - 1]. The root bone is special-cased to 0
    /// to match Rockstar's convention — every base-game skeleton has its
    /// root at tag 0.</summary>
    public static ushort CalcBoneTag(string boneName, bool isRoot)
    {
        if (isRoot) return 0;

        uint h = 0;
        foreach (char raw in boneName.ToUpperInvariant())
        {
            h = (h << 4) + raw;
            uint x = h & 0xF0000000u;
            if (x != 0) h ^= x >> 24;
            h &= ~x;
        }
        return (ushort)(h % 0xFE8F + 0x170);
    }
}
