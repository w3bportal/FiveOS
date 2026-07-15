// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;

namespace FiveOS.Services;

/// <summary>
/// Central timeline viewport state: zoom, scroll, frame snap, and time↔pixel mapping.
/// Mirrors VectorForge's useTimelineViewportState + useTimelineZoomAndDuration.
/// </summary>
public sealed class TimelineController
{
    public const double PaddingX = 14;
    public const double MinZoom = 1;
    public const double MaxZoom = 32;
    public const double MinClipDurationSec = 0.1;

    public double Zoom { get; set; } = 1;
    public double ScrollOffset { get; set; }
    public bool SnapToFrame { get; set; } = true;

    public event Action? Changed;

    public double VisibleDuration(double totalDuration) =>
        Math.Max(0.001, totalDuration / Math.Clamp(Zoom, MinZoom, MaxZoom));

    public void ClampScroll(double totalDuration)
    {
        var vis = VisibleDuration(totalDuration);
        ScrollOffset = Math.Clamp(ScrollOffset, 0, Math.Max(0, totalDuration - vis));
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
        return Math.Clamp(t, 0, totalDuration);
    }

    public double SnapTime(double time, int fps)
    {
        if (!SnapToFrame || fps < 1) return time;
        var frame = 1.0 / fps;
        return Math.Round(time / frame) * frame;
    }

    public void ZoomAt(double wheelDelta, double anchorTime, double totalDuration)
    {
        var oldVis = VisibleDuration(totalDuration);
        var factor = wheelDelta > 0 ? 1.12 : 1 / 1.12;
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
        ScrollOffset = Math.Clamp(ScrollOffset + deltaSec, 0, Math.Max(0, totalDuration - VisibleDuration(totalDuration)));
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
            ScrollOffset = Math.Max(0, time - vis);
        ClampScroll(totalDuration);
    }
}
