// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Linq;
using Assimp;
using Quaternion = System.Numerics.Quaternion;

namespace YdrWriter;

/// <summary>
/// Pulls per-bone rotation tracks out of an Assimp scene's first
/// animation, resamples them at a fixed FPS, and maps the resulting
/// bone-name keys to RAGE 16-bit bone tags via
/// <see cref="GtaBoneTags.TryResolve"/>.
///
/// The sampler is intentionally lenient: bones whose name doesn't
/// resolve to a GTA tag are skipped (the rest of the clip still
/// exports), and animation channels with no rotation keys are
/// likewise dropped. The caller decides whether a "zero usable
/// tracks" result is worth writing a .ycd for.
///
/// We sample at <c>fps</c> across the entire clip duration. Source
/// FPS is read from <c>Animation.TicksPerSecond</c>; when zero (the
/// glTF/FBX default for "unspecified") we fall back to 25 Hz which
/// is the Assimp documented convention.
/// </summary>
internal static class AnimationSampler
{
    public sealed record Result(
        string ClipName,
        int Frames,
        int Fps,
        double DurationSeconds,
        int TotalChannels,
        int UnmappedChannels,
        IReadOnlyList<PosedBoneTrack> Tracks);

    public static Result? SampleFirstClip(Scene scene, int fps = 30)
    {
        if (scene is null || !scene.HasAnimations || scene.AnimationCount == 0) return null;
        var anim = scene.Animations[0];
        if (anim is null || anim.NodeAnimationChannelCount == 0) return null;

        double ticksPerSecond = anim.TicksPerSecond > 0 ? anim.TicksPerSecond : 25.0;

        // Trust the actual key range over the header, and correct Assimp's glTF
        // quirk: its glTF2 reader scales key times to MILLISECONDS while reporting
        // a bogus TicksPerSecond (a 1 s clip reads as ~500 s). If the naive
        // duration is absurd but a ms interpretation is sane, treat ticks as ms.
        double maxKeyTicks = 0;
        foreach (var c in anim.NodeAnimationChannels)
            if (c?.RotationKeyCount > 0)
                maxKeyTicks = Math.Max(maxKeyTicks, c.RotationKeys[^1].Time);
        double durationTicks = Math.Max(anim.DurationInTicks, maxKeyTicks);
        if (durationTicks <= 0) durationTicks = ticksPerSecond;

        double durationSec = durationTicks / ticksPerSecond;
        if (durationSec > 150.0 && durationTicks / 1000.0 <= 150.0)
        {
            ticksPerSecond = 1000.0;
            durationSec = durationTicks / ticksPerSecond;
        }
        if (durationSec <= 0) durationSec = 1.0;
        if (durationSec > 120.0) durationSec = 120.0;  // emote-length cap

        int frames = Math.Max(1, (int)Math.Round(durationSec * fps) + 1);

        var tracks = new List<PosedBoneTrack>(anim.NodeAnimationChannelCount);
        int unmapped = 0;
        var seenTags = new HashSet<ushort>();

        foreach (var ch in anim.NodeAnimationChannels)
        {
            if (ch is null) continue;
            if (ch.RotationKeyCount == 0) continue;
            if (!GtaBoneTags.TryResolve(ch.NodeName ?? "", out var tag))
            {
                unmapped++;
                continue;
            }
            // Same tag appearing twice (multi-channel rigs, duplicated
            // armatures) — keep the first, skip the rest. RAGE only
            // wants one rotation track per bone.
            if (!seenTags.Add(tag)) continue;

            var samples = SampleRotations(ch, frames, fps, ticksPerSecond);
            if (samples is null) continue;
            tracks.Add(new PosedBoneTrack(tag, samples));
        }

        if (tracks.Count == 0) return null;

        var clipName = string.IsNullOrWhiteSpace(anim.Name) ? "animation" : anim.Name;
        return new Result(
            ClipName: clipName,
            Frames: frames,
            Fps: fps,
            DurationSeconds: durationSec,
            TotalChannels: anim.NodeAnimationChannelCount,
            UnmappedChannels: unmapped,
            Tracks: tracks);
    }

    /// <summary>Resample a single channel's RotationKeys at <paramref name="fps"/>
    /// across the full clip duration. Slerps between adjacent keys; clamps to
    /// the first/last key outside the authored range. Returns null when the
    /// channel produces nothing usable.</summary>
    private static Quaternion[]? SampleRotations(NodeAnimationChannel ch, int frames, int fps, double ticksPerSecond)
    {
        var keys = ch.RotationKeys;
        if (keys is null || keys.Count == 0) return null;

        // Sort by Time so binary-ish probing works reliably regardless of
        // how the source file ordered keys (Assimp normally preserves
        // chronological order, but glTF interpolation modes can produce
        // out-of-order layouts).
        var sorted = keys.OrderBy(k => k.Time).ToList();

        var output = new Quaternion[frames];
        for (int f = 0; f < frames; f++)
        {
            double t = (double)f / fps;
            double ticks = t * ticksPerSecond;
            output[f] = SampleAt(sorted, ticks);
        }
        return output;
    }

    private static Quaternion SampleAt(IList<QuaternionKey> keys, double ticks)
    {
        if (keys.Count == 1) return ToNumerics(keys[0].Value);
        if (ticks <= keys[0].Time) return ToNumerics(keys[0].Value);
        if (ticks >= keys[^1].Time) return ToNumerics(keys[^1].Value);

        // Linear search is fine — typical bone has <100 keys, and we
        // sample at most a few hundred times per channel. A binary
        // search would shave microseconds we can't measure.
        for (int i = 1; i < keys.Count; i++)
        {
            if (ticks <= keys[i].Time)
            {
                var k0 = keys[i - 1];
                var k1 = keys[i];
                double span = k1.Time - k0.Time;
                if (span <= 0) return ToNumerics(k1.Value);
                float u = (float)((ticks - k0.Time) / span);
                return Quaternion.Slerp(ToNumerics(k0.Value), ToNumerics(k1.Value), u);
            }
        }
        return ToNumerics(keys[^1].Value);
    }

    private static Quaternion ToNumerics(Assimp.Quaternion q) =>
        new(q.X, q.Y, q.Z, q.W);
}
