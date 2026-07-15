// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Windows;
using System.Windows.Media;

namespace FiveOS.Views.Controls;

/// <summary>
/// Retained-mode drawing surface for the timeline canvases. One instance
/// replaces the hundreds/thousands of per-keyframe <c>Polygon</c>/<c>Line</c>
/// UIElements the timeline used to rebuild on every redraw: the owner sets
/// <see cref="RenderCallback"/> once and calls <see cref="InvalidateVisual"/>
/// when the content changes; everything is emitted into a single
/// <see cref="DrawingContext"/> pass with frozen pens and batched geometry.
///
/// The element is deliberately hit-test-invisible — mouse interaction stays
/// on the parent Canvas, which hit-tests keys mathematically instead of via
/// per-shape event handlers. WPF does not clip OnRender output to the
/// element's (zero) size, so clipping is inherited from the parent canvas's
/// ClipToBounds.
/// </summary>
public sealed class TimelineRenderLayer : FrameworkElement
{
    public TimelineRenderLayer() => IsHitTestVisible = false;

    /// <summary>Draw callback; reads sizes from its owning canvas directly.</summary>
    public Action<DrawingContext>? RenderCallback { get; set; }

    protected override void OnRender(DrawingContext dc) => RenderCallback?.Invoke(dc);
}
