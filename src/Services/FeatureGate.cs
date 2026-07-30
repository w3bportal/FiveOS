// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;

namespace FiveOS.Services;

/// <summary>
/// Entitlement seam for features reserved to FiveOS Motion customers.
///
/// The predicate is injected by the Motion layer (proprietary, not in this
/// repository) once account state is known, so this file carries no billing
/// logic and an open-source build simply reports "not entitled".
///
/// SCOPE / HONEST LIMITS — this is a CLIENT-SIDE gate in GPL-3.0 code. It keeps
/// the feature off in the open-source build and behind sign-in in official
/// builds, and it drives the upsell copy. It is NOT an enforcement boundary:
/// anyone building from source can flip <see cref="MotionEntitled"/>. Real
/// enforcement only exists for work the server performs (Motion generation,
/// which is metered). Keeping every check funnelled through this one type is
/// deliberate: it's the single place to swap in a server-signed entitlement
/// later without touching the retarget or the viewer.
/// </summary>
internal static class FeatureGate
{
    /// <summary>Set by the Motion layer at sign-in / account refresh. Null means
    /// no Motion layer is present (open-source build) → nothing is entitled.</summary>
    public static Func<bool>? MotionEntitled;

    /// <summary>True when the current user is a Motion customer.</summary>
    public static bool IsMotionUser
    {
        get
        {
            try { return MotionEntitled?.Invoke() == true; }
            catch { return false; }   // never let a billing probe break an import
        }
    }

    /// <summary>Per-joint finger retargeting on animation import. Motion-only AND
    /// opt-in, because it is not finished.
    ///
    /// STATUS (2026-07-30): mapping and emit are correct — a Motion/Mixamo clip
    /// resolves all 15 joints per hand and the tracks reach the viewer (see the
    /// "[retarget] fingers:" log line). What is NOT correct is the Assimp→three
    /// rebase for finger bones: the hands come out visibly mangled. That is the
    /// same failure the viewer's blanket finger skip originally existed to avoid.
    /// Until the bind/axis handling for the finger chains is worked out this
    /// stays off, so nobody imports broken hands. Flip
    /// <c>FingerRetarget</c> in %APPDATA%\FiveOS settings to keep debugging it.</summary>
    public static bool FingerRetarget => IsMotionUser && UserSettings.LoadFingerRetarget();
}
