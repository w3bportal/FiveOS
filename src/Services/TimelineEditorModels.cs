// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace FiveOS.Services;

public enum TimelineEditorMode { Sequencer, DopeSheet }
public enum TimelineItemKind { Strip, Keyframe }
public enum TimelineInteractionState
{
    Idle,
    Scrubbing,
    Marquee,
    DraggingKeys,
    DraggingStrips,
    Trimming,
}

public readonly record struct TimelineItemRef(TimelineItemKind Kind, string Id);

/// <summary>Single selection authority shared by sequencer and dope sheet.</summary>
public sealed class TimelineSelectionModel
{
    private readonly HashSet<TimelineItemRef> _items = new();
    public IReadOnlyCollection<TimelineItemRef> Items => _items;
    public int Count => _items.Count;
    public event Action? Changed;

    public bool Contains(TimelineItemRef item) => _items.Contains(item);

    public void SelectOnly(TimelineItemRef item)
    {
        if (_items.Count == 1 && _items.Contains(item)) return;
        _items.Clear();
        _items.Add(item);
        Changed?.Invoke();
    }

    public void Add(TimelineItemRef item)
    {
        if (_items.Add(item)) Changed?.Invoke();
    }

    public void Toggle(TimelineItemRef item)
    {
        if (!_items.Remove(item)) _items.Add(item);
        Changed?.Invoke();
    }

    public void Replace(IEnumerable<TimelineItemRef> items)
    {
        var next = items.ToHashSet();
        if (_items.SetEquals(next)) return;
        _items.Clear();
        _items.UnionWith(next);
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke();
    }
}

/// <summary>Timeline-local transaction history, separate from pose-gizmo undo.</summary>
public sealed class TimelineCommandHistory<TSnapshot>
{
    public sealed record Entry(string Label, TSnapshot Before, TSnapshot After);
    private readonly List<Entry> _undo = new();
    private readonly List<Entry> _redo = new();
    public int Capacity { get; set; } = 100;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event Action? Changed;

    public void Push(string label, TSnapshot before, TSnapshot after)
    {
        _undo.Add(new Entry(label, before, after));
        if (_undo.Count > Math.Max(1, Capacity)) _undo.RemoveAt(0);
        _redo.Clear();
        Changed?.Invoke();
    }

    public Entry? Undo()
    {
        if (_undo.Count == 0) return null;
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(entry);
        Changed?.Invoke();
        return entry;
    }

    public Entry? Redo()
    {
        if (_redo.Count == 0) return null;
        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(entry);
        Changed?.Invoke();
        return entry;
    }

    public void Clear()
    {
        if (_undo.Count == 0 && _redo.Count == 0) return;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }
}

public static class TimelineHitTesting
{
    public static IReadOnlyList<string> Marquee(
        Rect selection,
        IEnumerable<(string Id, Rect Bounds)> candidates) =>
        candidates.Where(x => selection.IntersectsWith(x.Bounds)).Select(x => x.Id).ToList();

    public static Rect Normalize(Point start, Point end) => new(
        Math.Min(start.X, end.X),
        Math.Min(start.Y, end.Y),
        Math.Abs(end.X - start.X),
        Math.Abs(end.Y - start.Y));
}

public static class TimelineEditMath
{
    public static double SnapDelta(double anchorTime, double rawDelta, int fps, bool snap)
    {
        if (!snap || fps < 1) return rawDelta;
        var frame = 1.0 / fps;
        var snapped = Math.Round((anchorTime + rawDelta) / frame) * frame;
        return snapped - anchorTime;
    }

    public static double ClampGroupDelta(
        IEnumerable<double> originalTimes,
        double requestedDelta,
        double duration)
    {
        var values = originalTimes.ToArray();
        if (values.Length == 0) return 0;
        return Math.Clamp(requestedDelta, -values.Min(), duration - values.Max());
    }

    public static (double Start, double Duration) Trim(
        double start,
        double duration,
        double delta,
        bool trimLeft,
        double timelineDuration)
    {
        const double min = TimelineController.MinClipDurationSec;
        if (trimLeft)
        {
            var applied = Math.Clamp(delta, -start, duration - min);
            return (start + applied, duration - applied);
        }
        var rightApplied = Math.Clamp(delta, min - duration, timelineDuration - start - duration);
        return (start, duration + rightApplied);
    }
}
