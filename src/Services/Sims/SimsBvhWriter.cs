// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace FiveOS.Services.Sims;

/// <summary>Writes a minimal BVH from a decoded Sims CLIP so Assimp /
/// <see cref="AnimEmoteImporter"/> can ingest it.</summary>
public static class SimsBvhWriter
{
    public static void Write(string path, SimsClipDecoder.DecodedClip clip)
    {
        var bones = OrderBones(clip.Bones.Keys);
        if (bones.Count == 0)
            throw new InvalidDataException("No bones to write.");

        var fps = clip.TickLength > 0 ? 1.0 / clip.TickLength : 30.0;
        var frames = Math.Max(1, clip.NumTicks);

        var sb = new StringBuilder(64 * 1024);
        sb.AppendLine("HIERARCHY");
        WriteJoint(sb, bones[0], bones, clip, indent: 0, isRoot: true);
        sb.AppendLine("MOTION");
        sb.AppendLine($"Frames: {frames}");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Frame Time: {1.0 / fps:0.########}"));

        // Sample every tick; missing keys hold last value / identity.
        var lastRot = new Dictionary<string, Quaternion>(StringComparer.OrdinalIgnoreCase);
        var lastPos = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in bones)
        {
            lastRot[b] = Quaternion.Identity;
            lastPos[b] = Vector3.Zero;
        }

        for (var t = 0; t < frames; t++)
        {
            foreach (var b in bones)
            {
                if (clip.Bones.TryGetValue(b, out var track))
                {
                    foreach (var (tick, rq) in track.Rotations)
                        if (tick <= t) lastRot[b] = rq;
                    foreach (var (tick, tp) in track.Translations)
                        if (tick <= t) lastPos[b] = tp;
                }

                var q = lastRot[b];
                // ROOT_bind carries a Sims/world orientation that tips the
                // character when replayed on our approximate Y-up offsets.
                // Keep translation (loco/height); drop rotation for retarget.
                bool isRoot = ReferenceEquals(b, bones[0]) || b.Equals(bones[0], StringComparison.OrdinalIgnoreCase);
                if (isRoot) q = Quaternion.Identity;
                // BVH channels: for ROOT → Xposition Yposition Zposition Zrotation Yrotation Xrotation
                // for joints → Zrotation Yrotation Xrotation
                var (rx, ry, rz) = QuatToBvhEulerDeg(q);
                if (isRoot)
                {
                    var p = lastPos[b];
                    sb.Append(CultureInfo.InvariantCulture, $"{p.X:0.######} {p.Y:0.######} {p.Z:0.######} ");
                }
                sb.Append(CultureInfo.InvariantCulture, $"{rz:0.######} {ry:0.######} {rx:0.######} ");
            }
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
    }

    private static void WriteJoint(
        StringBuilder sb,
        string name,
        List<string> all,
        SimsClipDecoder.DecodedClip clip,
        int indent,
        bool isRoot)
    {
        var pad = new string('\t', indent);
        sb.Append(pad);
        sb.Append(isRoot ? "ROOT " : "JOINT ");
        sb.AppendLine(Sanitize(name));
        sb.Append(pad); sb.AppendLine("{");
        // Approximate bind offsets (meters) — retarget re-aligns to freemode.
        var off = ApproxOffset(name);
        sb.Append(pad); sb.Append('\t');
        sb.AppendLine(CultureInfo.InvariantCulture, $"OFFSET {off.X:0.###} {off.Y:0.###} {off.Z:0.###}");
        sb.Append(pad); sb.Append('\t');
        if (isRoot)
            sb.AppendLine("CHANNELS 6 Xposition Yposition Zposition Zrotation Yrotation Xrotation");
        else
            sb.AppendLine("CHANNELS 3 Zrotation Yrotation Xrotation");

        var children = all.Where(c =>
        {
            SimsBoneHashes.Parents.TryGetValue(c, out var p);
            return string.Equals(p, name, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        if (children.Count == 0)
        {
            sb.Append(pad); sb.Append('\t'); sb.AppendLine("End Site");
            sb.Append(pad); sb.Append('\t'); sb.AppendLine("{");
            sb.Append(pad); sb.Append("\t\t"); sb.AppendLine("OFFSET 0 0.05 0");
            sb.Append(pad); sb.Append('\t'); sb.AppendLine("}");
        }
        else
        {
            foreach (var c in children)
                WriteJoint(sb, c, all, clip, indent + 1, isRoot: false);
        }

        sb.Append(pad); sb.AppendLine("}");
    }

    private static List<string> OrderBones(IEnumerable<string> names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        // Prefer ROOT as BVH root when present.
        string? root = set.FirstOrDefault(n => n.Equals("b__ROOT__", StringComparison.OrdinalIgnoreCase))
                    ?? set.FirstOrDefault(n => n.Equals("b__ROOT_bind__", StringComparison.OrdinalIgnoreCase))
                    ?? set.FirstOrDefault(n => n.Equals("b__Pelvis__", StringComparison.OrdinalIgnoreCase))
                    ?? set.First();

        var ordered = new List<string>();
        void Visit(string n)
        {
            if (!set.Remove(n)) return;
            ordered.Add(n);
            foreach (var (child, parent) in SimsBoneHashes.Parents)
            {
                if (parent != null && parent.Equals(n, StringComparison.OrdinalIgnoreCase) && set.Contains(child))
                    Visit(child);
            }
        }
        Visit(root!);
        // Orphans (face etc. that slipped through) — append as ROOT children conceptually ignored if empty track
        ordered.AddRange(set);
        return ordered;
    }

    private static Vector3 ApproxOffset(string name)
    {
        // Sims / Blender armature: bone length along local ±Y.
        // Spine/arms use +Y; legs use −Y so the thigh chain hangs down when
        // local rotation is near identity (CLIP carries the rest orientation).
        var n = name.ToLowerInvariant();
        float len =
            n.Contains("thigh") || n.Contains("calf") ? 0.40f :
            n.Contains("foot") ? 0.10f :
            n.Contains("toe") ? 0.06f :
            n.Contains("spine") ? 0.12f :
            n.Contains("neck") ? 0.08f :
            n.Contains("head") ? 0.12f :
            n.Contains("clavicle") ? 0.08f :
            n.Contains("upperarm") ? 0.28f :
            n.Contains("forearm") ? 0.25f :
            n.Contains("hand") ? 0.08f :
            n.Contains("pelvis") ? 0.05f :
            n.Contains("thumb") || n.Contains("index") || n.Contains("mid") || n.Contains("ring") || n.Contains("pinky") ? 0.03f :
            0.04f;
        bool leg = n.Contains("thigh") || n.Contains("calf") || n.Contains("foot") || n.Contains("toe");
        return new Vector3(0, leg ? -len : len, 0);
    }

    private static (float rx, float ry, float rz) QuatToBvhEulerDeg(Quaternion q)
    {
        // Convert quaternion to XYZ intrinsic euler then emit as ZYX channel order
        // (BVH Zrotation Yrotation Xrotation).
        q = Quaternion.Normalize(q);
        // Standard XYZ euler extraction
        var sinr = 2 * (q.W * q.X + q.Y * q.Z);
        var cosr = 1 - 2 * (q.X * q.X + q.Y * q.Y);
        var rx = MathF.Atan2(sinr, cosr);

        var sinp = 2 * (q.W * q.Y - q.Z * q.X);
        var ry = MathF.Abs(sinp) >= 1 ? MathF.CopySign(MathF.PI / 2, sinp) : MathF.Asin(sinp);

        var siny = 2 * (q.W * q.Z + q.X * q.Y);
        var cosy = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        var rz = MathF.Atan2(siny, cosy);

        const float rad2deg = 180f / MathF.PI;
        return (rx * rad2deg, ry * rad2deg, rz * rad2deg);
    }

    private static string Sanitize(string name)
    {
        var s = name.Replace(" ", "_");
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }
}
