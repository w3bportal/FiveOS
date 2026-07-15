// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using CodeWalker.GameFiles;

namespace FiveOS.Services;

/// <summary>One bone's full per-frame rotation track. No position data —
/// vanilla ped clips only carry position tracks for bones that actually
/// translate (root/pelvis), and emitting zero positions would collapse
/// the bone onto its parent.</summary>
/// <param name="BoneTag">RAGE 16-bit bone tag.</param>
/// <param name="PerFrame">Local-space rotations sampled at the clip FPS.
/// Length must match the clip's frame count.</param>
public record PosedBoneTrack(ushort BoneTag, Quaternion[] PerFrame, string? SourceName = null);

/// <summary>One bone's per-frame POSITION track (a "mover"). Used for
/// SKEL_ROOT root motion — the whole-body travel an imported clip carries.
/// Values are RAGE parent-relative metres (Z-up), one per clip frame. Vanilla
/// ped clips carry these only for bones that actually translate.</summary>
public record PosedPositionTrack(ushort BoneTag, Vector3[] PerFrame);

/// <summary>
/// Multi-frame .ycd writer matching the layout Sollumz exports and vanilla
/// clips use (verified against the Sollumz ycd exporter + a 1,200-file scan
/// of in-game ped .ycds — see YcdPoseBuilder for the full recipe notes):
///
///  - Rotation tracks carry <b>Unk0=1</b> (track FORMAT = Quaternion; the
///    old 0 made the game decode rotations as Vector3s and ignore them).
///  - Constant rotation  -> one StaticQuaternion channel (W canonicalised).
///  - Animated rotation  -> FOUR float channels (x, y, z, w — explicit W;
///    nothing reconstructs W for plain float layouts): QuantizeFloat for
///    varying components, StaticFloat for constant ones — exactly what
///    Sollumz emits. Sign-continuity is enforced first so component lerp
///    never crosses the q/-q boundary mid-clip.
///  - No position or scale tracks (vanilla ped clips have neither for
///    rotation-only motion).
///  - BoneIds sorted by BoneId ascending, SequenceData in the same order.
/// </summary>
public static class YcdAnimationBuilder
{
    public static byte[] Build(string clipName, IReadOnlyList<PosedBoneTrack> bones, int frames, int fps,
        IReadOnlyList<PosedPositionTrack>? positions = null)
    {
        var xml = BuildXml(clipName, bones, frames, fps, positions);
        var ycd = XmlYcd.GetYcd(xml);
        return ycd.Save();
    }

    /// <summary>Same shape as <see cref="Build"/> but returns the source
    /// XML instead of the compiled bytes (callers can compile it via
    /// CodeWalker.exe as a check on CW.Core's binary writer).</summary>
    public static string BuildXml(string clipName, IReadOnlyList<PosedBoneTrack> bones, int frames, int fps,
        IReadOnlyList<PosedPositionTrack>? positions = null)
    {
        if (string.IsNullOrWhiteSpace(clipName))
            throw new ArgumentException("clipName must be non-empty.", nameof(clipName));
        if (bones is null || bones.Count == 0)
            throw new ArgumentException("At least one bone is required.", nameof(bones));
        if (frames < 2) throw new ArgumentException("frames must be >= 2 (single-frame clips are rejected by the game).", nameof(frames));
        if (fps < 1) throw new ArgumentException("fps must be >= 1.", nameof(fps));

        foreach (var b in bones)
            if (b.PerFrame is null || b.PerFrame.Length != frames)
                throw new ArgumentException($"Bone tag {b.BoneTag} has {b.PerFrame?.Length ?? 0} samples; clip needs {frames}.");

        // Optional position (mover) tracks — SKEL_ROOT root motion. Track-major
        // ordering means these come BEFORE the rotation block in both BoneIds
        // and SequenceData (Sollumz sort key boneId | track<<16).
        var posTracks = positions?.Where(p => p.PerFrame is { Length: > 0 }).OrderBy(p => p.BoneTag).ToList()
                        ?? new List<PosedPositionTrack>();
        foreach (var p in posTracks)
            if (p.PerFrame.Length != frames)
                throw new ArgumentException($"Position track tag {p.BoneTag} has {p.PerFrame.Length} samples; clip needs {frames}.");

        var safe = SanitizeClipName(clipName);
        var animDataName = "hash_" + (YcdPoseBuilder.Joaat(safe) + 1).ToString("X8");

        var duration = (frames / (float)fps).ToString("0.######", CultureInfo.InvariantCulture);
        int sequenceFrameLimit = frames + 30;
        var sorted = bones.OrderBy(b => b.BoneTag).ToList();

        var sb = new StringBuilder(64 * 1024);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<ClipDictionary>");
        sb.AppendLine(" <Clips>");
        sb.AppendLine("  <Item>");
        sb.AppendLine($"   <Hash>{safe}</Hash>");
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

        // BoneIds — Unk0 is the track FORMAT: 0=Vector3 (position), 1=Quaternion
        // (rotation). Position (mover) tracks first, then the rotation block.
        sb.AppendLine("   <BoneIds>");
        foreach (var p in posTracks)
            sb.AppendLine($"    <Item><BoneId value=\"{p.BoneTag}\" /><Track value=\"0\" /><Unk0 value=\"0\" /></Item>");
        foreach (var b in sorted)
            sb.AppendLine($"    <Item><BoneId value=\"{b.BoneTag}\" /><Track value=\"1\" /><Unk0 value=\"1\" /></Item>");
        sb.AppendLine("   </BoneIds>");

        sb.AppendLine("   <Sequences>");
        sb.AppendLine("    <Item>");
        sb.AppendLine("     <Hash />");
        sb.AppendLine($"     <FrameCount value=\"{frames}\" />");
        sb.AppendLine("     <SequenceData>");

        // Position items first — mirror the BoneIds order 1:1.
        foreach (var p in posTracks)
            AppendPositionItem(sb, p.PerFrame);

        foreach (var bone in sorted)
        {
            var aligned = AlignSigns(bone.PerFrame);
            if (IsConstant(aligned))
            {
                var q = aligned[0];
                if (q.W < 0f) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
                sb.AppendLine($"      <Item><Channels><Item><Type value=\"StaticQuaternion\" /><Value x=\"{F(q.X)}\" y=\"{F(q.Y)}\" z=\"{F(q.Z)}\" w=\"{F(q.W)}\" /></Item></Channels></Item>");
            }
            else
            {
                // Animated: 4 explicit component channels, x y z w.
                sb.AppendLine("      <Item><Channels>");
                AppendFloatChannel(sb, aligned, q => q.X);
                AppendFloatChannel(sb, aligned, q => q.Y);
                AppendFloatChannel(sb, aligned, q => q.Z);
                AppendFloatChannel(sb, aligned, q => q.W);
                sb.AppendLine("      </Channels></Item>");
            }
        }

        sb.AppendLine("     </SequenceData>");
        sb.AppendLine("    </Item>");
        sb.AppendLine("   </Sequences>");
        sb.AppendLine("  </Item>");
        sb.AppendLine(" </Animations>");
        sb.AppendLine("</ClipDictionary>");

        return sb.ToString();
    }

    /// <summary>One quaternion component across the clip: StaticFloat when
    /// constant, else QuantizeFloat with Sollumz's quantum rule
    /// (quantum = max(1e-9, range/2^20), offset = min).</summary>
    private static void AppendFloatChannel(StringBuilder sb, Quaternion[] frames, Func<Quaternion, float> picker)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < frames.Length; i++)
        {
            var v = picker(frames[i]);
            if (v < min) min = v;
            if (v > max) max = v;
        }

        if (max - min < 1e-7f)
        {
            sb.AppendLine($"       <Item><Type value=\"StaticFloat\" /><Value value=\"{F(min)}\" /></Item>");
            return;
        }

        float quantum = Math.Max(1e-9f, (max - min) / 1048575f);
        sb.AppendLine("       <Item>");
        sb.AppendLine("        <Type value=\"QuantizeFloat\" />");
        sb.AppendLine($"        <Quantum value=\"{F(quantum)}\" />");
        sb.AppendLine($"        <Offset value=\"{F(min)}\" />");
        sb.Append("        <Values>");
        for (int i = 0; i < frames.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(F(picker(frames[i])));
        }
        sb.AppendLine("</Values>");
        sb.AppendLine("       </Item>");
    }

    /// <summary>One position (Vector3) track item: a StaticVector3 channel when
    /// the mover never moves, else three explicit float channels (x, y, z) —
    /// the Vector3 analog of the animated-rotation 4-channel layout.</summary>
    private static void AppendPositionItem(StringBuilder sb, Vector3[] frames)
    {
        if (IsConstantV(frames))
        {
            var p = frames[0];
            sb.AppendLine($"      <Item><Channels><Item><Type value=\"StaticVector3\" /><Value x=\"{F(p.X)}\" y=\"{F(p.Y)}\" z=\"{F(p.Z)}\" /></Item></Channels></Item>");
        }
        else
        {
            sb.AppendLine("      <Item><Channels>");
            AppendFloatChannelV(sb, frames, v => v.X);
            AppendFloatChannelV(sb, frames, v => v.Y);
            AppendFloatChannelV(sb, frames, v => v.Z);
            sb.AppendLine("      </Channels></Item>");
        }
    }

    /// <summary>Vector3-component sibling of <see cref="AppendFloatChannel"/>.</summary>
    private static void AppendFloatChannelV(StringBuilder sb, Vector3[] frames, Func<Vector3, float> picker)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < frames.Length; i++)
        {
            var v = picker(frames[i]);
            if (v < min) min = v;
            if (v > max) max = v;
        }

        if (max - min < 1e-7f)
        {
            sb.AppendLine($"       <Item><Type value=\"StaticFloat\" /><Value value=\"{F(min)}\" /></Item>");
            return;
        }

        float quantum = Math.Max(1e-9f, (max - min) / 1048575f);
        sb.AppendLine("       <Item>");
        sb.AppendLine("        <Type value=\"QuantizeFloat\" />");
        sb.AppendLine($"        <Quantum value=\"{F(quantum)}\" />");
        sb.AppendLine($"        <Offset value=\"{F(min)}\" />");
        sb.Append("        <Values>");
        for (int i = 0; i < frames.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(F(picker(frames[i])));
        }
        sb.AppendLine("</Values>");
        sb.AppendLine("       </Item>");
    }

    private static bool IsConstantV(Vector3[] frames)
    {
        if (frames.Length <= 1) return true;
        var first = frames[0];
        for (int i = 1; i < frames.Length; i++)
        {
            if (Math.Abs(frames[i].X - first.X) > 1e-6f) return false;
            if (Math.Abs(frames[i].Y - first.Y) > 1e-6f) return false;
            if (Math.Abs(frames[i].Z - first.Z) > 1e-6f) return false;
        }
        return true;
    }

    private static bool IsConstant(Quaternion[] frames)
    {
        if (frames.Length <= 1) return true;
        var first = frames[0];
        for (int i = 1; i < frames.Length; i++)
        {
            var q = frames[i];
            // 1e-6 tolerance per component is well below visible-pose
            // delta. Any user-meaningful change exceeds this.
            if (Math.Abs(q.X - first.X) > 1e-6f) return false;
            if (Math.Abs(q.Y - first.Y) > 1e-6f) return false;
            if (Math.Abs(q.Z - first.Z) > 1e-6f) return false;
            if (Math.Abs(q.W - first.W) > 1e-6f) return false;
        }
        return true;
    }

    private static Quaternion[] AlignSigns(Quaternion[] frames)
    {
        // The game lerps raw components without a dot check (same reason
        // Sollumz applies this fix on export): force successive frames to
        // the same hemisphere so interpolation never crosses q/-q.
        var aligned = new Quaternion[frames.Length];
        aligned[0] = frames[0];
        for (int i = 1; i < frames.Length; i++)
        {
            var q = frames[i];
            var dot = q.X * aligned[i - 1].X + q.Y * aligned[i - 1].Y
                    + q.Z * aligned[i - 1].Z + q.W * aligned[i - 1].W;
            aligned[i] = dot < 0 ? new Quaternion(-q.X, -q.Y, -q.Z, -q.W) : q;
        }
        return aligned;
    }

    private static string F(float v) =>
        v.ToString("0.#######", CultureInfo.InvariantCulture);

    private static string SanitizeClipName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ' || ch == '-') sb.Append('_');
        }
        var s = sb.ToString();
        if (s.Length == 0) s = "fiveos_anim";
        if (char.IsDigit(s[0])) s = "p_" + s;
        return s;
    }
}
