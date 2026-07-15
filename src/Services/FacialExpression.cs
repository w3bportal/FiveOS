// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Numerics;

namespace FiveOS.Services;

/// <summary>A facial expression the user can bake onto an emote's face bones
/// at export time. Independent of body motion — it only adds tracks on the
/// GTA facial rig (the <c>FB_*</c> bones), which the normal body retarget
/// never touches (GtaBoneTags rejects face bones).</summary>
public enum FacialExpression { None, Smile, Angry, Surprised, Blink, Talking }

/// <summary>
/// Builds facial-bone tracks/poses for a <see cref="FacialExpression"/>, keyed
/// to GTA V's ped facial-rig bone tags (FB_*). Injected into the emote .ycd
/// alongside the body tracks.
///
/// ⚠️ FIRST-PASS POSES. These rotations were authored WITHOUT an in-game facial
/// preview, so the per-bone AXES and MAGNITUDES are
/// educated guesses at GTA's facial bone frames and will very likely need
/// tuning against what you actually see on a ped. Angles are kept small so a
/// wrong guess reads as "too subtle / slightly off" rather than grotesque.
/// The mechanism (dropdown -> FB tracks -> .ycd) is solid; the numbers here are
/// the tunable part — adjust the <see cref="Adj"/> tables below.
/// </summary>
public static class FacialPoses
{
    // GTA V ped facial rig bone tags (16-bit), from the community bone tables.
    private const ushort JAW = 46240;
    private const ushort BROW_C = 37193;
    private const ushort L_BROW = 58331, R_BROW = 1356;
    private const ushort L_LID = 45750, R_LID = 43536;
    private const ushort L_CORNER = 29868, R_CORNER = 11174;
    private const ushort L_CHEEK = 21550, R_CHEEK = 19336;

    /// <summary>Rotate bone <paramref name="Tag"/> by <paramref name="Deg"/>°
    /// about local <paramref name="Axis"/>.</summary>
    private readonly record struct Adj(ushort Tag, Vector3 Axis, float Deg);

    private static Quaternion Q(Vector3 axis, float deg) =>
        Quaternion.Normalize(Quaternion.CreateFromAxisAngle(
            Vector3.Normalize(axis), deg * (float)Math.PI / 180f));

    private static readonly Vector3 X = new(1, 0, 0), Z = new(0, 0, 1);

    /// <summary>The held/static adjustments for an expression. For Blink and
    /// Talking (which are motion) the animated path overrides these; the held
    /// single-pose path uses them as a representative frozen frame (eyes shut /
    /// mouth ajar).</summary>
    private static Adj[] Static(FacialExpression e) => e switch
    {
        FacialExpression.Smile => new[]
        {
            new Adj(L_CORNER, Z, 14f), new Adj(R_CORNER, Z, 14f),
            new Adj(L_CHEEK, X, 6f),   new Adj(R_CHEEK, X, 6f),
        },
        FacialExpression.Angry => new[]
        {
            new Adj(BROW_C, X, -10f),
            new Adj(L_BROW, Z, -12f), new Adj(R_BROW, Z, 12f),
            new Adj(L_CORNER, Z, -8f), new Adj(R_CORNER, Z, -8f),
        },
        FacialExpression.Surprised => new[]
        {
            new Adj(BROW_C, X, 14f),
            new Adj(L_BROW, X, 12f), new Adj(R_BROW, X, 12f),
            new Adj(JAW, X, 16f),
            new Adj(L_LID, X, 6f),   new Adj(R_LID, X, 6f),
        },
        FacialExpression.Blink => new[]      // held == eyes shut
        {
            new Adj(L_LID, X, -25f), new Adj(R_LID, X, -25f),
        },
        FacialExpression.Talking => new[]    // held == mouth ajar
        {
            new Adj(JAW, X, 10f),
        },
        _ => Array.Empty<Adj>(),
    };

    /// <summary>Facial bones for a single HELD pose (static-pose emote export).</summary>
    public static List<PosedBone> BuildPose(FacialExpression e)
    {
        var outp = new List<PosedBone>();
        foreach (var a in Static(e))
            outp.Add(new PosedBone(a.Tag, Vector3.Zero, Q(a.Axis, a.Deg)));
        return outp;
    }

    /// <summary>Facial bone TRACKS for an animated emote. Smile/Angry/Surprised
    /// hold their pose across every frame; Blink and Talking animate.</summary>
    public static List<PosedBoneTrack> BuildTracks(FacialExpression e, int frames, int fps)
    {
        var outp = new List<PosedBoneTrack>();
        if (e == FacialExpression.None || frames < 1 || fps < 1) return outp;

        if (e == FacialExpression.Blink)
        {
            // Eyes open (identity) with a quick close every ~2.6 s.
            var lid = new Quaternion[frames];
            var closed = Q(X, -25f);
            double period = Math.Max(1, 2.6 * fps), blinkDur = Math.Max(1, 0.16 * fps);
            for (int f = 0; f < frames; f++)
            {
                double phase = f % period;
                float t = phase < blinkDur ? (float)Math.Sin(Math.PI * phase / blinkDur) : 0f;
                lid[f] = Quaternion.Slerp(Quaternion.Identity, closed, t);
            }
            outp.Add(new PosedBoneTrack(L_LID, lid));
            outp.Add(new PosedBoneTrack(R_LID, (Quaternion[])lid.Clone()));
            return outp;
        }

        if (e == FacialExpression.Talking)
        {
            // Jaw flaps open/closed on a smooth cosine (~3.2 flaps/sec).
            var jaw = new Quaternion[frames];
            var open = Q(X, 13f);
            const double cyclesPerSec = 3.2;
            for (int f = 0; f < frames; f++)
            {
                double s = f / (double)fps;
                float t = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * cyclesPerSec * s));
                jaw[f] = Quaternion.Slerp(Quaternion.Identity, open, t);
            }
            outp.Add(new PosedBoneTrack(JAW, jaw));
            return outp;
        }

        // Constant expression: same rotation on every frame.
        foreach (var a in Static(e))
        {
            var q = Q(a.Axis, a.Deg);
            var arr = new Quaternion[frames];
            for (int f = 0; f < frames; f++) arr[f] = q;
            outp.Add(new PosedBoneTrack(a.Tag, arr));
        }
        return outp;
    }
}
