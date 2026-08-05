// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using CodeWalker.GameFiles;

namespace FiveOS.Services;

/// <summary>Single bone entry to bake into a Pose -> Emote .ycd.</summary>
/// <param name="BoneTag">RAGE bone tag (the 16-bit hash CodeWalker shows
/// in AnimationBoneId.BoneId). For GTA peds this is the SKEL_* name tag.</param>
/// <param name="Position">Bone local-space (parent-relative) position.
/// Pass <see cref="Vector3.Zero"/> to omit the position track entirely —
/// vanilla ped clips only carry position tracks for bones that actually
/// translate, and a zero position would COLLAPSE the bone onto its parent,
/// not "keep the bind pose" as an earlier build assumed.</param>
/// <param name="Rotation">Bone local-space rotation. xyzw quaternion;
/// identity = (0,0,0,1).</param>
public record PosedBone(ushort BoneTag, Vector3 Position, Quaternion Rotation);

/// <summary>
/// Builds a held-pose .ycd from a list of posed bones, matching the layout
/// vanilla GTA V pose clips and Sollumz exports use (verified against a scan
/// of 1,200 in-game ped .ycd files + the Sollumz ycd exporter):
///
///  - One <c>StaticQuaternion</c> rotation channel per bone (Track=1) —
///    fully-static clips DO play (vanilla ships them, e.g.
///    amb@incar@male@patrol@base.ycd); the old "must be animated" RawFloat
///    workaround was chasing a different bug entirely (see Unk0 below).
///  - <b>Unk0 is the track FORMAT, not padding</b>: 0=Vector3, 1=Quaternion,
///    2=Float. Rotation tracks MUST carry Unk0=1 — with 0 the game decodes
///    the track as a Vector3 and ignores it, which is exactly the
///    "dict loads, task runs, ped never moves" failure previous builds hit.
///  - StaticQuaternion stores only XYZ on disk; W is reconstructed as
///    +sqrt(1-|xyz|^2), so every quat must be canonicalised to W >= 0
///    before writing or the rotation silently flips on load.
///  - No scale tracks: not one Track=2 entry exists in vanilla ped clips.
///  - Position tracks only for bones that need them (non-zero), with real
///    parent-relative translations.
///  - BoneIds sorted track-major then BoneId ascending (Sollumz sort key
///    <c>boneId | track &lt;&lt; 16</c>), SequenceData in the same order.
///
/// Approach: hand-crafted .ycd.xml round-tripped through CodeWalker's
/// XmlYcd parser and YcdFile.Save — avoids reimplementing CW's binary
/// writer (RSC7 framing, Sequence.Data bit packing, bucket hashing).
/// </summary>
public static class YcdPoseBuilder
{
    public const float HoldSeconds = 2f;
    public const int Fps = 30;

    public static byte[] Build(string clipName, IReadOnlyList<PosedBone> bones)
    {
        var xml = BuildXml(clipName, bones);
        var ycd = XmlYcd.GetYcd(xml);
        return ycd.Save();
    }

    /// <summary>Same shape as <see cref="Build"/> but returns the source
    /// XML instead of the compiled bytes. Useful for diagnostics — write
    /// it next to the .ycd in the resource folder and the user can compile
    /// it via CodeWalker.exe to compare against our binary writer.</summary>
    public static string BuildXml(string clipName, IReadOnlyList<PosedBone> bones)
    {
        if (string.IsNullOrWhiteSpace(clipName))
            throw new System.ArgumentException("clipName must be non-empty.", nameof(clipName));
        if (bones is null || bones.Count == 0)
            throw new System.ArgumentException("At least one bone is required.", nameof(bones));

        // Sanitise the clip name — RAGE jenkins-hashes it at runtime, so
        // case + non-ASCII garbage causes silent misses on TaskPlayAnim.
        // Clip.Hash, Clip.AnimationHash and Animation.Hash all share the
        // same name (verified against shipped packs).
        var safe = SanitizeClipName(clipName);
        // Unknown1C: dpemotes/Sollumz use joaat(animName)+1. Match that.
        var animDataName = "hash_" + (Joaat(safe) + 1).ToString("X8");

        int frames = (int)(HoldSeconds * Fps);
        var duration = HoldSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        int sequenceFrameLimit = frames + 30;

        // Stable ordering: BoneId ascending within each track block.
        var sorted = bones.OrderBy(b => b.BoneTag).ToList();
        // Position tracks only where a real translation was provided.
        var positioned = sorted.Where(b => b.Position != Vector3.Zero).ToList();

        var sb = new StringBuilder(16 * 1024);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<ClipDictionary>");
        sb.AppendLine(" <Clips>");
        sb.AppendLine("  <Item>");
        sb.AppendLine($"   <Hash>{safe}</Hash>");
        // Name = "pack:/" + clip name, matching <Hash>, no ".clip" suffix
        // (verified against shipped packs: Hash=x_clip, Name=pack:/x_clip).
        sb.AppendLine($"   <Name>pack:/{safe}</Name>");
        sb.AppendLine("   <Type value=\"Animation\" />");
        sb.AppendLine("   <Unknown30 value=\"0\" />");
        sb.AppendLine("   <Tags />");
        sb.AppendLine("   <Properties />");
        sb.AppendLine($"   <AnimationHash>{safe}</AnimationHash>");
        sb.AppendLine("   <StartTime value=\"0\" />");
        sb.AppendLine($"   <EndTime value=\"{duration}\" />");
        sb.AppendLine("   <Rate value=\"1.0\" />");
        sb.AppendLine("  </Item>");
        sb.AppendLine(" </Clips>");
        sb.AppendLine(" <Animations>");
        sb.AppendLine("  <Item>");
        sb.AppendLine($"   <Hash>{safe}</Hash>");
        sb.AppendLine("   <Unknown10 value=\"0\" />");
        sb.AppendLine($"   <FrameCount value=\"{frames}\" />");
        sb.AppendLine($"   <SequenceFrameLimit value=\"{sequenceFrameLimit}\" />");
        sb.AppendLine($"   <Duration value=\"{duration}\" />");
        sb.AppendLine($"   <Unknown1C>{animDataName}</Unknown1C>");

        // Track-major BoneIds, each entry's Unk0 = the track's value FORMAT:
        //   Track=0 position -> Unk0=0 (Vector3)
        //   Track=1 rotation -> Unk0=1 (Quaternion)   <-- the critical one
        //   Track=5/6 mover  -> the entity mover pair, sorts last
        sb.AppendLine("   <BoneIds>");
        foreach (var b in positioned)
            sb.AppendLine($"    <Item><BoneId value=\"{b.BoneTag}\" /><Track value=\"0\" /><Unk0 value=\"0\" /></Item>");
        foreach (var b in sorted)
            sb.AppendLine($"    <Item><BoneId value=\"{b.BoneTag}\" /><Track value=\"1\" /><Unk0 value=\"1\" /></Item>");
        AppendMoverBoneIds(sb);
        sb.AppendLine("   </BoneIds>");

        sb.AppendLine("   <Sequences>");
        sb.AppendLine("    <Item>");
        sb.AppendLine("     <Hash />");
        sb.AppendLine($"     <FrameCount value=\"{frames}\" />");
        sb.AppendLine("     <SequenceData>");
        // SequenceData order MUST mirror BoneIds order 1:1.
        foreach (var b in positioned)
            sb.AppendLine($"      <Item><Channels><Item><Type value=\"StaticVector3\" /><Value x=\"{F(b.Position.X)}\" y=\"{F(b.Position.Y)}\" z=\"{F(b.Position.Z)}\" /></Item></Channels></Item>");
        foreach (var b in sorted)
        {
            // Canonicalise to W >= 0: the binary format stores XYZ only and
            // reconstructs W as +sqrt(1-|xyz|^2). q and -q are the same
            // rotation, so this is lossless.
            var q = b.Rotation;
            if (q.W < 0f) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
            sb.AppendLine($"      <Item><Channels><Item><Type value=\"StaticQuaternion\" /><Value x=\"{F(q.X)}\" y=\"{F(q.Y)}\" z=\"{F(q.Z)}\" w=\"{F(q.W)}\" /></Item></Channels></Item>");
        }
        // A held pose never travels, so the mover is neutral — but it still has
        // to be PRESENT, or the ped's root stays unauthored and swings whenever
        // the entity underneath it moves.
        AppendNeutralMoverTranslation(sb);
        AppendMoverRotation(sb);
        sb.AppendLine("     </SequenceData>");
        sb.AppendLine("    </Item>");
        sb.AppendLine("   </Sequences>");
        sb.AppendLine("  </Item>");
        sb.AppendLine(" </Animations>");
        sb.AppendLine("</ClipDictionary>");

        return sb.ToString();
    }

    /// <summary>BoneIds entries for the entity mover: Track 5 translation
    /// (Vector3 format) and Track 6 rotation (Quaternion format), both on
    /// BoneId 0. Every vanilla ped clip carries this pair — it is what tells
    /// the game the clip owns the ped's root, instead of leaving the root to
    /// whatever motion state is playing underneath.</summary>
    internal static void AppendMoverBoneIds(StringBuilder sb)
    {
        sb.AppendLine("    <Item><BoneId value=\"0\" /><Track value=\"5\" /><Unk0 value=\"0\" /></Item>");
        sb.AppendLine("    <Item><BoneId value=\"0\" /><Track value=\"6\" /><Unk0 value=\"1\" /></Item>");
    }

    /// <summary>Zero mover translation — "this clip does not move the ped
    /// relative to its own entity". Not a freeze: the bone tracks still play
    /// in full, and an entity the ped is attached to still carries it along.</summary>
    internal static void AppendNeutralMoverTranslation(StringBuilder sb) =>
        sb.AppendLine("      <Item><Channels><Item><Type value=\"StaticVector3\" /><Value x=\"0\" y=\"0\" z=\"0\" /></Item></Channels></Item>");

    /// <summary>Identity mover rotation. FiveOS bakes no heading change into a
    /// clip, so this is the truthful value — and stating it stops the game
    /// solving a heading of its own while the clip plays.</summary>
    internal static void AppendMoverRotation(StringBuilder sb) =>
        sb.AppendLine("      <Item><Channels><Item><Type value=\"StaticQuaternion\" /><Value x=\"0\" y=\"0\" z=\"0\" w=\"1\" /></Item></Channels></Item>");

    /// <summary>Jenkins one-at-a-time (joaat) over the lowercased name —
    /// the hash RAGE uses for asset/clip names.</summary>
    internal static uint Joaat(string s)
    {
        uint h = 0;
        foreach (var c in s)
        {
            h += (byte)char.ToLowerInvariant(c);
            h += h << 10;
            h ^= h >> 6;
        }
        h += h << 3;
        h ^= h >> 11;
        h += h << 15;
        return h;
    }

    private static string F(float v) =>
        v.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture);

    private static string SanitizeClipName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ' || ch == '-') sb.Append('_');
        }
        var s = sb.ToString();
        if (s.Length == 0) s = "fiveos_pose";
        if (char.IsDigit(s[0])) s = "p_" + s;
        return s;
    }
}
