// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace FiveOS.Services.Sims;

/// <summary>Decodes Sims 4 CLIP resources (type 0x6B20C4F3, outer version 14
/// wrapping an inner <c>_pilC3S_</c> / reversed <c>_S3Clip_</c> body) into
/// per-bone rotation/translation tracks, then writes a temp BVH.</summary>
public static class SimsClipDecoder
{
    public const byte ChannelTypeTranslation = 18; // 0x12
    public const byte ChannelTypeOrientation = 20; // 0x14

    public sealed record ClipInfo(int Index, string Name, float DurationSeconds, ulong Instance);

    public sealed class DecodedClip
    {
        public string Name { get; init; } = "clip";
        public float TickLength { get; init; } = 1f / 30f;
        public int NumTicks { get; set; }
        public Dictionary<string, BoneTrack> Bones { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class BoneTrack
    {
        public List<(int Tick, Quaternion Rotation)> Rotations { get; } = new();
        public List<(int Tick, Vector3 Translation)> Translations { get; } = new();
    }

    public static List<ClipInfo> ListClips(string packagePath)
    {
        using var pkg = DbpfPackage.Open(packagePath);
        var list = new List<ClipInfo>();
        var i = 0;
        foreach (var (clip, header) in pkg.EnumerateClips())
        {
            string name = $"clip_{i}";
            float dur = 0f;
            try
            {
                var bytes = pkg.ReadResource(clip);
                name = TryReadClipName(bytes) ?? name;
                dur = TryReadDuration(bytes);
            }
            catch
            {
                // Still list the entry so the picker isn't empty.
            }

            if (header is { } h)
            {
                try
                {
                    var hdrBytes = pkg.ReadResource(h);
                    var hdrName = TryReadClipName(hdrBytes);
                    if (!string.IsNullOrWhiteSpace(hdrName)) name = hdrName!;
                }
                catch { /* ignore */ }
            }

            list.Add(new ClipInfo(i, SanitizeName(name), dur, clip.Instance));
            i++;
        }
        return list;
    }

    public static string ExportClipToBvh(string packagePath, int clipIndex, string? outputDir = null)
        => ExportClip(packagePath, clipIndex, outputDir);

    /// <summary>Decode a CLIP and write a temp glTF (quaternion channels) for Assimp.</summary>
    public static string ExportClip(string packagePath, int clipIndex, string? outputDir = null)
    {
        using var pkg = DbpfPackage.Open(packagePath);
        var clips = new List<DbpfPackage.IndexEntry>();
        foreach (var (clip, _) in pkg.EnumerateClips())
            clips.Add(clip);

        if (clipIndex < 0 || clipIndex >= clips.Count)
            throw new ArgumentOutOfRangeException(nameof(clipIndex), "Clip index out of range.");

        var raw = pkg.ReadResource(clips[clipIndex]);
        var decoded = Decode(raw);
        outputDir ??= Path.Combine(Path.GetTempPath(), "FiveOS", "sims-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDir);
        var safe = SanitizeName(decoded.Name);
        var path = Path.Combine(outputDir, safe + ".gltf");
        SimsGltfWriter.Write(path, decoded);
        return path;
    }

    public static DecodedClip Decode(byte[] clipBytes)
    {
        if (clipBytes.Length < 64)
            throw new InvalidDataException("CLIP resource too small.");

        var r = new BinReader(clipBytes);
        var version = r.U32();
        if (version != 14)
            throw new InvalidDataException($"Unsupported CLIP outer version {version} (need 14).");

        r.U32(); // flags
        var duration = r.F32();
        r.Skip(4 * 4); // initialOffsetQ
        r.Skip(3 * 4); // initialOffsetT
        r.U32(); r.U32(); r.U32(); r.U32(); // namespace hashes

        var clipName = r.PascalAscii();
        var rigName = r.PascalAscii();

        var nsCount = r.U32();
        for (var i = 0; i < nsCount; i++)
        {
            var len = r.U32();
            r.Skip((int)len);
        }

        var slotCount = r.U32();
        for (var i = 0; i < slotCount; i++)
        {
            r.U16(); r.U16(); // chainIdx, slotIdx
            var actorLen = r.U32();
            r.Skip((int)actorLen);
            var targetLen = r.U32();
            r.Skip((int)targetLen);
        }

        var eventCount = r.U32();
        for (var i = 0; i < eventCount; i++)
        {
            r.U32(); // type
            var len = r.U32(); // payload length after length field
            r.Skip((int)len);
        }

        var codecLen = r.U32();
        if (codecLen == 0 || r.Remaining < 48)
            throw new InvalidDataException("CLIP has no codec body.");

        var bodyStart = r.Position;
        return DecodeBody(clipBytes.AsSpan(bodyStart), clipName, duration);
    }

    private static DecodedClip DecodeBody(ReadOnlySpan<byte> body, string clipName, float outerDuration)
    {
        var r = new BinReader(body.ToArray());
        var magic = Encoding.ASCII.GetString(r.Bytes(8));
        // Writers store reversed "_S3Clip_" as "_pilC3S_".
        if (magic is not ("_pilC3S_" or "_S3Clip_"))
            throw new InvalidDataException($"Bad CLIP body magic '{magic}'.");

        var ver = r.U32();
        if (ver != 2)
            throw new InvalidDataException($"Unsupported CLIP body version {ver} (need 2).");

        r.U32(); // flags
        var tickLength = r.F32();
        if (tickLength <= 0) tickLength = 1f / 30f;
        var numTicks = r.U16();
        r.U16(); // padding
        var channelCount = r.U32();
        var f1PaletteSize = r.U32();
        var channelDataOffset = r.U32(); // typically 48
        var f1PaletteOffset = r.U32();
        var sourceNameOffset = r.U32();
        var sourceAssetNameOffset = r.U32();

        if (channelCount > 4096)
            throw new InvalidDataException("Unreasonable channel count.");

        var channels = new List<ChannelHeader>((int)channelCount);
        for (var i = 0; i < channelCount; i++)
        {
            channels.Add(new ChannelHeader(
                r.U32(), // dataOffset
                r.U32(), // bone hash
                r.F32(), // offset
                r.F32(), // scale
                r.U16(), // frameCount
                r.U8(),  // type
                r.U8()   // subType
            ));
        }

        var decoded = new DecodedClip
        {
            Name = string.IsNullOrWhiteSpace(clipName) ? "clip" : clipName,
            TickLength = tickLength,
            NumTicks = numTicks > 0 ? numTicks : Math.Max(1, (int)Math.Round(outerDuration / tickLength)),
        };

        // Palette floats (F1) live between names and frame data when present.
        float[] palette = Array.Empty<float>();
        if (f1PaletteSize > 0 && f1PaletteOffset > 0 && f1PaletteOffset + f1PaletteSize * 4 <= (uint)body.Length)
        {
            palette = new float[f1PaletteSize];
            var pr = new BinReader(body.Slice((int)f1PaletteOffset, (int)(f1PaletteSize * 4)).ToArray());
            for (var i = 0; i < f1PaletteSize; i++)
                palette[i] = pr.F32();
        }

        foreach (var ch in channels)
        {
            if (!SimsBoneHashes.TryGetName(ch.BoneHash, out var boneName))
                continue; // skip face/slot/unknown hashes

            if (!decoded.Bones.TryGetValue(boneName, out var track))
            {
                track = new BoneTrack();
                decoded.Bones[boneName] = track;
            }

            if (ch.DataOffset == 0 || ch.DataOffset >= body.Length) continue;
            var fr = new BinReader(body.Slice((int)ch.DataOffset).ToArray());

            try
            {
                if (ch.Type == ChannelTypeOrientation)
                    ReadOrientationFrames(fr, ch, track);
                else if (ch.Type == ChannelTypeTranslation)
                    ReadTranslationFrames(fr, ch, track);
                // else: morph / IK / F1 — ignore for skeletal retarget
            }
            catch
            {
                // Skip broken channel rather than aborting the whole clip.
            }
        }

        if (decoded.Bones.Count == 0)
            throw new InvalidDataException("No recognizable humanoid bone channels in this CLIP.");

        // Keyframes are appended in raw file order. The glTF writer's forward-fill
        // ("keep the last key whose tick <= t") and BindTranslation's OrderBy both
        // assume ascending ticks; guarantee it here so a non-ascending channel
        // can't silently select the wrong keyframe and warp the pose.
        foreach (var b in decoded.Bones.Values)
        {
            b.Rotations.Sort((a, c) => a.Tick.CompareTo(c.Tick));
            b.Translations.Sort((a, c) => a.Tick.CompareTo(c.Tick));
        }

        // Ensure NumTicks covers the last keyframe.
        var maxTick = 0;
        foreach (var b in decoded.Bones.Values)
        {
            foreach (var (t, _) in b.Rotations) maxTick = Math.Max(maxTick, t);
            foreach (var (t, _) in b.Translations) maxTick = Math.Max(maxTick, t);
        }
        if (maxTick + 1 > decoded.NumTicks)
            decoded.NumTicks = maxTick + 1;

        return decoded;
    }

    private static void ReadOrientationFrames(BinReader fr, ChannelHeader ch, BoneTrack track)
    {
        for (var i = 0; i < ch.FrameCount; i++)
        {
            var tick = fr.U16();
            var signs = fr.U16();
            var c0 = fr.U16();
            var c1 = fr.U16();
            var c2 = fr.U16();
            var c3 = fr.U16();

            float Decode(ushort c, int bit)
            {
                // Quantized magnitude in [0,1]; sign bit flips the NORMALIZED
                // value before scale/offset (matches s4animtools writer).
                var n = c / 4095f;
                if (((signs >> bit) & 1) != 0) n = -n;
                return n * ch.Scale + ch.Offset;
            }

            // Sign bits (Python int(bitstring,2) after writer reverse):
            // bit0=x, bit1=y, bit2=z, bit3=w, bit7=snap.
            var x = Decode(c0, 0);
            var y = Decode(c1, 1);
            var z = Decode(c2, 2);
            var w = Decode(c3, 3);
            var q = new Quaternion(x, y, z, w);
            // Near-zero means "identity" in EA clips — NEVER Normalize, or you
            // invent a 90° rotation from quantization noise (seen on pelvis).
            if (q.LengthSquared() < 1e-4f) q = Quaternion.Identity;
            else
            {
                q = Quaternion.Normalize(q);
                // All-zero encoded with a spurious sign bit becomes (0,±1,0,0)
                // (180° about Y) — treat as identity for bind/default channels.
                if (MathF.Abs(q.Y) > 0.999f && MathF.Abs(q.X) < 0.02f
                    && MathF.Abs(q.Z) < 0.02f && MathF.Abs(q.W) < 0.02f)
                    q = Quaternion.Identity;
            }
            track.Rotations.Add((tick, q));
        }
    }

    private static void ReadTranslationFrames(BinReader fr, ChannelHeader ch, BoneTrack track)
    {
        for (var i = 0; i < ch.FrameCount; i++)
        {
            var tick = fr.U16();
            var signs = fr.U16();
            var packed = fr.U32();
            var c0 = (ushort)(packed & 0x3FF);
            var c1 = (ushort)((packed >> 10) & 0x3FF);
            var c2 = (ushort)((packed >> 20) & 0x3FF);

            float Decode(ushort c, int bit)
            {
                var n = c / 1023f;
                if (((signs >> bit) & 1) != 0) n = -n;
                return n * ch.Scale + ch.Offset;
            }

            // Same sign layout: bit0=x, bit1=y, bit2=z
            var x = Decode(c0, 0);
            var y = Decode(c1, 1);
            var z = Decode(c2, 2);
            track.Translations.Add((tick, new Vector3(x, y, z)));
        }
    }

    private static string? TryReadClipName(byte[] bytes)
    {
        try
        {
            var r = new BinReader(bytes);
            if (r.U32() != 14) return null;
            r.U32(); r.F32();
            r.Skip(4 * 4 + 3 * 4 + 4 * 4);
            return r.PascalAscii();
        }
        catch { return null; }
    }

    private static float TryReadDuration(byte[] bytes)
    {
        try
        {
            var r = new BinReader(bytes);
            if (r.U32() != 14) return 0;
            r.U32();
            return r.F32();
        }
        catch { return 0; }
    }

    private static string SanitizeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Replace(':', '_').Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "clip";
        return name.Length > 80 ? name[..80] : name;
    }

    private readonly record struct ChannelHeader(
        uint DataOffset, uint BoneHash, float Offset, float Scale,
        ushort FrameCount, byte Type, byte SubType);

    private sealed class BinReader
    {
        private readonly byte[] _b;
        public int Position { get; private set; }
        public int Remaining => _b.Length - Position;
        public BinReader(byte[] b) => _b = b;
        public void Skip(int n) { Position += n; if (Position > _b.Length) throw new EndOfStreamException(); }
        public byte[] Bytes(int n)
        {
            if (Position + n > _b.Length) throw new EndOfStreamException();
            var a = new byte[n];
            Buffer.BlockCopy(_b, Position, a, 0, n);
            Position += n;
            return a;
        }
        public byte U8() { if (Position >= _b.Length) throw new EndOfStreamException(); return _b[Position++]; }
        public ushort U16() { var v = BitConverter.ToUInt16(_b, Position); Position += 2; return v; }
        public uint U32() { var v = BitConverter.ToUInt32(_b, Position); Position += 4; return v; }
        public float F32() { var v = BitConverter.ToSingle(_b, Position); Position += 4; return v; }
        public string PascalAscii()
        {
            var len = (int)U32();
            if (len < 0 || len > Remaining) throw new EndOfStreamException();
            var s = Encoding.ASCII.GetString(_b, Position, len);
            Position += len;
            return s;
        }
    }
}
