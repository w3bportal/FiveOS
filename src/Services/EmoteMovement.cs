// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

namespace FiveOS.Services;

/// <summary>
/// How a generated emote coexists with normal ped movement. Maps to the
/// RAGE <c>TaskPlayAnim</c> flag bitmask and to dpemotes/rpemotes
/// AnimationOptions. Key RAGE flags:
///   1  = AF_LOOPING          (loop forever)
///   2  = AF_HOLD_LAST_FRAME  (freeze on the last frame when a one-shot ends)
///   16 = AF_UPPERBODY        (only the spine-up bones are driven; legs free)
///   32 = AF_SECONDARY        (play on the secondary slot, over the base task)
///
/// An UPPER-BODY emote keeps the legs free to run locomotion so the PLAYER can
/// walk with WASD — that's <see cref="UpperBody"/> (it sets the upper-body bit;
/// the clip's own leg/root motion is NOT applied).
///
/// <see cref="RootMotion"/> is the opposite and is what a travelling imported
/// clip needs: a FULL-BODY clip whose baked SKEL_ROOT "mover" is extracted
/// every frame so the PED itself physically walks/jumps along the recorded
/// path. This needs the extra RAGE flags below — the upper-body modes will
/// never move the ped no matter what's baked into the clip.
///   262144 = AF_USE_KINEMATIC_PHYSICS  (drive the capsule kinematically)
///   524288 = AF_USE_MOVER_EXTRACTION   (update ped position from the mover each frame)
///   4      = AF_REPOSITION_WHEN_FINISHED (a one-shot leaves the ped where it ended)
/// </summary>
public enum EmoteMovement
{
    /// <summary>Full-body, player locked in place — dances, full poses.</summary>
    InPlace,

    /// <summary>Upper-body only overlay; lower body keeps idling/walking so
    /// the player can move. Gestures, waves, salutes, pointing. This is the
    /// dpemotes "EmoteMoving" behaviour.</summary>
    UpperBody,

    /// <summary>Full-body with ROOT MOTION extracted — the ped physically
    /// travels along the clip's baked SKEL_ROOT mover (an imported clip that
    /// walks/moves). Needs the .ycd to carry a SKEL_ROOT position track, which
    /// FiveOS bakes for travelling clips; standard emote menus (dpemotes) can't
    /// extract movers, so this only works via the standalone resource export.</summary>
    RootMotion,
}

public static class EmoteMovementExtensions
{
    /// <summary>The RAGE TaskPlayAnim flag for this mode + loop choice.</summary>
    public static int ToAnimFlag(this EmoteMovement m, bool looping) => m switch
    {
        // Full body: loop (1) or play-once-and-hold (2).
        EmoteMovement.InPlace   => looping ? 1 : 2,
        // Upper-body overlay: + AF_UPPERBODY(16) + AF_SECONDARY(32). The legs
        // stay free for locomotion, so the player can walk while it plays.
        EmoteMovement.UpperBody => looping ? 1 + 16 + 32 : 2 + 16 + 32,   // 49 / 50
        // Root motion: full body + kinematic physics + mover extraction so the
        // ped travels along the clip's SKEL_ROOT mover. Loop keeps circling
        // (AF_LOOPING 1); one-shot leaves the ped where it ended
        // (AF_REPOSITION_WHEN_FINISHED 4) instead of snapping back to start.
        EmoteMovement.RootMotion => 262144 + 524288 + (looping ? 1 : 4), // 786433 / 786436
        _ => looping ? 1 : 2,
    };

    /// <summary>dpemotes/rpemotes EmoteMoving flag — true when the player is
    /// meant to be able to move. (RootMotion is full-body, but rpemotes uses
    /// this to allow the clip's own movement; dpemotes ignores movers entirely.)</summary>
    public static bool ToEmoteMoving(this EmoteMovement m) =>
        m is EmoteMovement.UpperBody or EmoteMovement.RootMotion;

    /// <summary>Short human label for READMEs / logs.</summary>
    public static string Label(this EmoteMovement m) => m switch
    {
        EmoteMovement.InPlace    => "in place (full body)",
        EmoteMovement.UpperBody  => "upper body (can move)",
        EmoteMovement.RootMotion => "root motion (ped travels)",
        _ => "in place",
    };
}
