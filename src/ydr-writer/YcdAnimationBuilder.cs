// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using CodeWalker.GameFiles;

namespace YdrWriter;

// NOTE: this is a synchronized copy of FiveOS.Services.YcdAnimationBuilder.
// The two MUST emit the same .ycd structure. If you touch one, mirror the
// other and re-run tools/build-engine.ps1. (Kept as a copy rather than a
// linked source only to keep the engine self-contained.)

/// <summary>One bone's full per-frame local rotation track.</summary>
internal sealed record PosedBoneTrack(ushort BoneTag, Quaternion[] PerFrame);

/// <summary>
/// Multi-frame .ycd writer matching the layout Sollumz exports and vanilla
/// clips use (see FiveOS.Services.YcdPoseBuilder for the full recipe notes):
///  - Rotation tracks carry Unk0=1 — Unk0 is the track FORMAT (0=Vector3,
///    1=Quaternion, 2=Float); the old 0 made the game decode rotations as
///    Vector3s and silently ignore them.
///  - Constant rotation -> one StaticQuaternion channel (W canonicalised
///    to >= 0; the binary stores XYZ only and reconstructs +W).
///  - Animated rotation -> FOUR float channels (x, y, z, w explicit — no
///    implicit W reconstruction exists for plain float layouts):
///    QuantizeFloat for varying components, StaticFloat for constant ones.
///  - No position or scale tracks.
///  - BoneIds sorted ascending; SequenceData mirrors that order.
/// </summary>
internal static class YcdAnimationBuilder
{
    public static byte[] Build(string clipName, IReadOnlyList<PosedBoneTrack> bones, int frames, int fps)
    {
        var xml = BuildXml(clipName, bones, frames, fps);
        var ycd = XmlYcd.GetYcd(xml);
        return ycd.Save();
    }

    public static string BuildXml(string clipName, IReadOnlyList<PosedBoneTrack> bones, int frames, int fps)
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

        var safe = SanitizeClipName(clipName);
        var animDataName = "hash_" + (Joaat(safe) + 1).ToString("X8");

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

        sb.AppendLine("   <BoneIds>");
        foreach (var b in sorted)
            sb.AppendLine($"    <Item><BoneId value=\"{b.BoneTag}\" /><Track value=\"1\" /><Unk0 value=\"1\" /></Item>");
        sb.AppendLine("   </BoneIds>");

        sb.AppendLine("   <Sequences>");
        sb.AppendLine("    <Item>");
        sb.AppendLine("     <Hash />");
        sb.AppendLine($"     <FrameCount value=\"{frames}\" />");
        sb.AppendLine("     <SequenceData>");

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

    private static bool IsConstant(Quaternion[] frames)
    {
        if (frames.Length <= 1) return true;
        var first = frames[0];
        for (int i = 1; i < frames.Length; i++)
        {
            var q = frames[i];
            if (Math.Abs(q.X - first.X) > 1e-6f) return false;
            if (Math.Abs(q.Y - first.Y) > 1e-6f) return false;
            if (Math.Abs(q.Z - first.Z) > 1e-6f) return false;
            if (Math.Abs(q.W - first.W) > 1e-6f) return false;
        }
        return true;
    }

    private static Quaternion[] AlignSigns(Quaternion[] frames)
    {
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

    private static uint Joaat(string s)
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
        v.ToString("0.#######", CultureInfo.InvariantCulture);

    internal static string SanitizeClipName(string raw)
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
