// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FiveOS.Services;

namespace FiveOS.Views;

/// <summary>
/// Manual panel reorder via mouse capture (no WPF DragDrop — window
/// file-drop handlers fight DoDragDrop). Drop on another card = swap
/// places with that card.
/// </summary>
public static class PanelLayout
{
    public static readonly DependencyProperty IdProperty =
        DependencyProperty.RegisterAttached(
            "Id", typeof(string), typeof(PanelLayout),
            new PropertyMetadata(null));

    public static void SetId(DependencyObject d, string? value) => d.SetValue(IdProperty, value);
    public static string? GetId(DependencyObject d) => (string?)d.GetValue(IdProperty);

    public static readonly DependencyProperty DragHandleProperty =
        DependencyProperty.RegisterAttached(
            "DragHandle", typeof(bool), typeof(PanelLayout),
            new PropertyMetadata(false, OnDragHandleChanged));

    public static void SetDragHandle(DependencyObject d, bool value) => d.SetValue(DragHandleProperty, value);
    public static bool GetDragHandle(DependencyObject d) => (bool)d.GetValue(DragHandleProperty);

    public static readonly DependencyProperty IsHostProperty =
        DependencyProperty.RegisterAttached(
            "IsHost", typeof(bool), typeof(PanelLayout),
            new PropertyMetadata(false, OnIsHostChanged));

    public static void SetIsHost(DependencyObject d, bool value) => d.SetValue(IsHostProperty, value);
    public static bool GetIsHost(DependencyObject d) => (bool)d.GetValue(IsHostProperty);

    public static readonly DependencyProperty OrderKeyProperty =
        DependencyProperty.RegisterAttached(
            "OrderKey", typeof(string), typeof(PanelLayout),
            new PropertyMetadata(null));

    public static void SetOrderKey(DependencyObject d, string? value) => d.SetValue(OrderKeyProperty, value);
    public static string? GetOrderKey(DependencyObject d) => (string?)d.GetValue(OrderKeyProperty);

    private static readonly DependencyProperty HostWiredProperty =
        DependencyProperty.RegisterAttached(
            "HostWired", typeof(bool), typeof(PanelLayout),
            new PropertyMetadata(false));

    private static Point _pressScreen;
    private static FrameworkElement? _dragCard;
    private static Panel? _dragHost;
    private static FrameworkElement? _captureTarget;
    private static bool _dragging;
    private static FrameworkElement? _hoverTarget;

    private static void OnIsHostChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel host) return;
        if (e.NewValue is not true) return;
        if ((bool)host.GetValue(HostWiredProperty)) return;
        host.SetValue(HostWiredProperty, true);

        if (host.IsLoaded)
            ApplySavedOrder(host);
        else
            host.Loaded += (_, _) => ApplySavedOrder(host);
    }

    private static void OnDragHandleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.MouseLeftButtonDown -= OnHandleDown;
        el.MouseMove -= OnHandleMove;
        el.MouseLeftButtonUp -= OnHandleUp;
        el.LostMouseCapture -= OnLostCapture;

        if (e.NewValue is true)
        {
            el.MouseLeftButtonDown += OnHandleDown;
            el.MouseMove += OnHandleMove;
            el.MouseLeftButtonUp += OnHandleUp;
            el.LostMouseCapture += OnLostCapture;
            el.Cursor = Cursors.SizeAll;
            if (el is Border { Background: null } b)
                b.Background = Brushes.Transparent;
            el.Focusable = false;
        }
    }

    private static void OnHandleDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement handle) return;
        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null) return;
        if (FindAncestor<TextBoxBase>(e.OriginalSource as DependencyObject) is not null) return;
        if (FindAncestor<ComboBox>(e.OriginalSource as DependencyObject) is not null) return;

        var card = FindCard(handle);
        var host = card is null ? null : FindHost(card);
        if (card is null || host is null) return;

        // Disabled ancestors block input — bail with a clear no-op rather
        // than capturing a dead handle.
        if (!handle.IsEnabled || !card.IsEnabled) return;

        _pressScreen = handle.PointToScreen(e.GetPosition(handle));
        _dragCard = card;
        _dragHost = host;
        _dragging = false;
        _hoverTarget = null;
        _captureTarget = handle;
        handle.CaptureMouse();
        e.Handled = true;
    }

    private static void OnHandleMove(object sender, MouseEventArgs e)
    {
        if (_dragCard is null || _dragHost is null || _captureTarget is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelDrag();
            return;
        }

        var screen = _captureTarget.PointToScreen(e.GetPosition(_captureTarget));
        double dx = Math.Abs(screen.X - _pressScreen.X);
        double dy = Math.Abs(screen.Y - _pressScreen.Y);
        if (!_dragging)
        {
            // Tiny threshold so a deliberate drag always starts.
            if (dx < 3 && dy < 3) return;
            _dragging = true;
            _dragCard.Opacity = 0.45;
            _dragCard.RenderTransform = new TranslateTransform(0, 0);
        }

        ClearHover();
        var pos = e.GetPosition(_dragHost);
        if (_dragCard.RenderTransform is TranslateTransform tt)
            tt.Y = pos.Y - (_dragCard.TranslatePoint(new Point(0, _dragCard.ActualHeight / 2), _dragHost).Y);

        _hoverTarget = HitOtherCard(_dragHost, _dragCard, pos.Y);
        if (_hoverTarget is not null)
        {
            _hoverTarget.Opacity = 0.75;
            if (_hoverTarget.RenderTransform is not TranslateTransform)
                _hoverTarget.RenderTransform = new TranslateTransform();
        }

        Mouse.OverrideCursor = Cursors.SizeNS;
        e.Handled = true;
    }

    private static void OnHandleUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragCard is null || _dragHost is null)
        {
            CancelDrag();
            return;
        }

        if (_dragging)
        {
            var pos = e.GetPosition(_dragHost);
            var target = _hoverTarget ?? HitOtherCard(_dragHost, _dragCard, pos.Y);
            if (target is not null)
                MoveCardOnto(_dragHost, _dragCard, target);
        }

        CancelDrag();
        e.Handled = true;
    }

    private static void OnLostCapture(object sender, MouseEventArgs e)
    {
        if (_dragCard is not null)
            CancelDrag();
    }

    /// <summary>Move <paramref name="card"/> to the index of <paramref name="target"/>.</summary>
    private static void MoveCardOnto(Panel host, FrameworkElement card, FrameworkElement target)
    {
        int from = host.Children.IndexOf(card);
        int to = host.Children.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;

        host.Children.RemoveAt(from);
        // Insert(to, …) covers both directions: moving downward the removal
        // shifted later siblings down by one, so `to` lands us just AFTER the
        // target; moving upward `to` is unchanged and lands us just before it.
        host.Children.Insert(to, card);

        PersistOrder(host);
    }

    private static void CancelDrag()
    {
        ClearHover();
        if (_dragCard is not null)
        {
            _dragCard.Opacity = 1.0;
            _dragCard.RenderTransform = Transform.Identity;
        }
        if (_captureTarget is not null && Mouse.Captured == _captureTarget)
            _captureTarget.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        _dragCard = null;
        _dragHost = null;
        _captureTarget = null;
        _dragging = false;
    }

    private static void ClearHover()
    {
        if (_hoverTarget is not null && !ReferenceEquals(_hoverTarget, _dragCard))
        {
            _hoverTarget.Opacity = 1.0;
            _hoverTarget.RenderTransform = Transform.Identity;
        }
        _hoverTarget = null;
    }

    private static FrameworkElement? HitOtherCard(Panel host, FrameworkElement dragged, double y)
    {
        FrameworkElement? best = null;
        double bestDist = double.MaxValue;
        foreach (UIElement child in host.Children)
        {
            if (child is not FrameworkElement fe || GetId(fe) is null) continue;
            if (ReferenceEquals(fe, dragged)) continue;
            if (fe.Visibility != Visibility.Visible || fe.ActualHeight <= 0) continue;

            var top = fe.TranslatePoint(new Point(0, 0), host).Y;
            var bottom = top + fe.ActualHeight;
            if (y >= top && y <= bottom)
                return fe;

            double mid = (top + bottom) / 2;
            double dist = Math.Abs(y - mid);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = fe;
            }
        }
        // Snap to nearest sibling within a generous range so short drags still swap.
        return bestDist < 120 ? best : null;
    }

    private static FrameworkElement? FindCard(DependencyObject start)
    {
        for (var d = start; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement fe && GetId(fe) != null)
                return fe;
        }
        return null;
    }

    private static Panel? FindHost(DependencyObject start)
    {
        for (var d = VisualTreeHelper.GetParent(start); d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is Panel p && GetIsHost(p))
                return p;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : class
    {
        for (var d = start; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is T match) return match;
        }
        return null;
    }

    public static void ApplySavedOrder(Panel host)
    {
        var key = GetOrderKey(host);
        if (string.IsNullOrWhiteSpace(key)) return;

        var saved = UserSettings.LoadPanelOrder(key);
        if (saved is null || saved.Count == 0) return;

        var cards = host.Children.OfType<FrameworkElement>()
            .Where(c => GetId(c) != null)
            .ToList();
        if (cards.Count == 0) return;

        var byId = cards
            .GroupBy(c => GetId(c)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var ordered = new List<FrameworkElement>();
        foreach (var id in saved)
        {
            if (byId.Remove(id, out var fe))
                ordered.Add(fe);
        }
        foreach (var fe in cards)
        {
            var id = GetId(fe)!;
            if (byId.Remove(id, out var leftover))
                ordered.Add(leftover);
        }

        foreach (var fe in cards)
            host.Children.Remove(fe);
        for (int i = 0; i < ordered.Count; i++)
            host.Children.Insert(i, ordered[i]);
    }

    private static void PersistOrder(Panel host)
    {
        var key = GetOrderKey(host);
        if (string.IsNullOrWhiteSpace(key)) return;
        var ids = host.Children.OfType<FrameworkElement>()
            .Select(GetId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList();
        UserSettings.SavePanelOrder(key, ids);
    }
}
