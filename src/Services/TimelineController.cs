// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;

namespace FiveOS.Services;

/// <summary>
/// Central timeline viewport state: zoom, scroll, frame snap, and time↔pixel mapping.
/// Mirrors VectorForge's useTimelineViewportState + useTimelineZoomAndDuration.
///
/// Blender-style: the view can scroll/zoom past the clip (and past In/Out).
/// Soft padding before 0 and after duration keeps those regions reachable.
/// </summary>
public sealed class TimelineController
{
    public const double PaddingX = 14;
    public const double MinZoom = 0.2;
    public const double MaxZoom = 32;
    public const double MinClipDurationSec = 0.1;

    public double Zoom { get; set; } = 1;
    public double ScrollOffset { get; set; }
    public bool SnapToFrame { get; set; } = true;

    public event Action? Changed;

    /// <summary>Soft margin past the clip so In/Out aren't glued to the
    /// viewport edges (Blender lets you pan into empty frames).</summary>
    public static double ViewPad(double totalDuration)
    {
        var dur = Math.Max(0.001, totalDuration);
        return Math.Max(1.0, dur * 0.4);
    }

    public double VisibleDuration(double totalDuration) =>
        Math.Max(0.001, totalDuration / Math.Clamp(Zoom, MinZoom, MaxZoom));

    public double MinScroll(double totalDuration) => -ViewPad(totalDuration);

    public double MaxScroll(double totalDuration)
    {
        var dur = Math.Max(0.001, totalDuration);
        var vis = VisibleDuration(dur);
        var pad = ViewPad(dur);
        return Math.Max(MinScroll(dur), dur + pad - vis);
    }

    public void ClampScroll(double totalDuration)
    {
        ScrollOffset = Math.Clamp(ScrollOffset, MinScroll(totalDuration), MaxScroll(totalDuration));
    }

    public double UsableWidth(double canvasWidth) =>
        Math.Max(1, canvasWidth - PaddingX * 2);

    public double TimeToX(double time, double totalDuration, double canvasWidth)
    {
        var usable = UsableWidth(canvasWidth);
        var vis = VisibleDuration(totalDuration);
        var t = Math.Clamp(time, ScrollOffset, ScrollOffset + vis);
        return PaddingX + ((t - ScrollOffset) / vis) * usable;
    }

    public double XToTime(double x, double totalDuration, double canvasWidth)
    {
        var usable = UsableWidth(canvasWidth);
        var vis = VisibleDuration(totalDuration);
        var t = ScrollOffset + (x - PaddingX) / usable * vis;
        // Scrubbing still locks to the clip — empty pad is view-only.
        return Math.Clamp(t, 0, Math.Max(0.001, totalDuration));
    }

    /// <summary>Unclamped pick for zoom/pan anchors (may land in the pad).</summary>
    public double XToTimeRaw(double x, double totalDuration, double canvasWidth)
    {
        var usable = UsableWidth(canvasWidth);
        var vis = VisibleDuration(totalDuration);
        return ScrollOffset + (x - PaddingX) / usable * vis;
    }

    public double SnapTime(double time, int fps)
    {
        if (!SnapToFrame || fps < 1) return time;
        var frame = 1.0 / fps;
        return Math.Round(time / frame) * frame;
    }

    public void ZoomAt(double wheelDelta, double anchorTime, double totalDuration) =>
        ZoomBy(wheelDelta > 0 ? 1.12 : 1 / 1.12, anchorTime, totalDuration);

    /// <summary>Zoom by an arbitrary factor keeping <paramref name="anchorTime"/>
    /// pinned to its current pixel (drag-zoom uses continuous factors).</summary>
    public void ZoomBy(double factor, double anchorTime, double totalDuration)
    {
        var oldVis = VisibleDuration(totalDuration);
        Zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        var newVis = VisibleDuration(totalDuration);
        if (oldVis > 1e-9)
        {
            var rel = (anchorTime - ScrollOffset) / oldVis;
            ScrollOffset = anchorTime - rel * newVis;
        }
        ClampScroll(totalDuration);
        Changed?.Invoke();
    }

    public void ScrollByTime(double deltaSec, double totalDuration)
    {
        ScrollOffset = Math.Clamp(
            ScrollOffset + deltaSec,
            MinScroll(totalDuration),
            MaxScroll(totalDuration));
        Changed?.Invoke();
    }

    public void Fit()
    {
        Zoom = 1;
        ScrollOffset = 0;
        Changed?.Invoke();
    }

    public void EnsurePlayheadVisible(double time, double totalDuration, double canvasWidth)
    {
        var vis = VisibleDuration(totalDuration);
        if (time < ScrollOffset)
            ScrollOffset = time;
        else if (time > ScrollOffset + vis)
            ScrollOffset = time - vis;
        ClampScroll(totalDuration);
    }
}
