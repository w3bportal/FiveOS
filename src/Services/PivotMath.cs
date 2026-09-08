// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Globalization;

namespace FiveOS.Services;

/// <summary>
/// Turns a user-placed pivot into the translation the converter bakes.
///
/// The viewport wraps a loaded model in a pivot group: the group's origin is
/// what the gizmo rotates and scales about, and the inner object carries the
/// offset that keeps the mesh visually still. Pivot mode lets the user drag
/// that origin onto a hinge, a base corner, wherever the prop should turn and
/// be placed from.
///
/// The engine bakes vertices as <c>v' = R·(S·v) + T</c>
/// (ydr-writer's <c>Converter.ApplyUserTransform</c>, working in the viewer's
/// Y-up display space). For the chosen pivot to become the prop's origin, the
/// geometry has to shift by the pivot-to-origin offset carried through the
/// same scale and rotation — so <c>T = R·(S·P)</c>, where P is the offset the
/// viewer reports. The pivot point itself then lands exactly on (0,0,0),
/// which is the point the game places and rotates the prop by.
/// </summary>
public static class PivotMath
{
    /// <summary>Translation to hand the converter as <c>--pos</c>.
    /// <paramref name="pivot"/> is the viewer's reported offset (Y-up),
    /// <paramref name="scale"/> the per-axis gizmo scale and
    /// <paramref name="rotationDeg"/> the gizmo's XYZ Euler angles in degrees.</summary>
    public static (double X, double Y, double Z) BakeTranslation(
        (double X, double Y, double Z) pivot,
        (double X, double Y, double Z) scale,
        (double X, double Y, double Z) rotationDeg)
    {
        double sx = scale.X > 0 ? scale.X : 1.0;
        double sy = scale.Y > 0 ? scale.Y : 1.0;
        double sz = scale.Z > 0 ? scale.Z : 1.0;
        double px = pivot.X * sx, py = pivot.Y * sy, pz = pivot.Z * sz;

        var (r00, r01, r02, r10, r11, r12, r20, r21, r22) = RotationMatrix(rotationDeg);
        return (r00 * px + r01 * py + r02 * pz,
                r10 * px + r11 * py + r12 * pz,
                r20 * px + r21 * py + r22 * pz);
    }

    /// <summary>Same, formatted as the engine's <c>x,y,z</c> argument.</summary>
    public static string BakeTranslationArg(
        (double X, double Y, double Z) pivot,
        (double X, double Y, double Z) scale,
        (double X, double Y, double Z) rotationDeg)
    {
        var t = BakeTranslation(pivot, scale, rotationDeg);
        var ci = CultureInfo.InvariantCulture;
        return $"{t.X.ToString(ci)},{t.Y.ToString(ci)},{t.Z.ToString(ci)}";
    }

    /// <summary>The three.js XYZ-Euler rotation <c>M = Rx·Ry·Rz</c>, expanded
    /// exactly as the engine expands it so preview and export agree.</summary>
    public static (double R00, double R01, double R02,
                   double R10, double R11, double R12,
                   double R20, double R21, double R22) RotationMatrix((double X, double Y, double Z) degrees)
    {
        double rx = degrees.X * Math.PI / 180.0;
        double ry = degrees.Y * Math.PI / 180.0;
        double rz = degrees.Z * Math.PI / 180.0;
        double cx = Math.Cos(rx), sx = Math.Sin(rx);
        double cy = Math.Cos(ry), sy = Math.Sin(ry);
        double cz = Math.Cos(rz), sz = Math.Sin(rz);

        return (cy * cz, -cy * sz, sy,
                cx * sz + sx * sy * cz, cx * cz - sx * sy * sz, -sx * cy,
                sx * sz - cx * sy * cz, sx * cz + cx * sy * sz, cx * cy);
    }
}
