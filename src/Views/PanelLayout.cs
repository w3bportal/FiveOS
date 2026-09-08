// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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

        host.Loaded += (_, _) => WireCollapsibleHeaders(host);
        if (host.IsLoaded) WireCollapsibleHeaders(host);
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

    private static readonly DependencyProperty CollapseWiredProperty =
        DependencyProperty.RegisterAttached(
            "CollapseWired", typeof(bool), typeof(PanelLayout),
            new PropertyMetadata(false));

    /// <summary>Let every panel in this stack fold shut from its own header.
    ///
    /// The cards all share one shape — Border &gt; Panel whose first child is the
    /// header row and whose remaining children are the body — so this is wired
    /// once here rather than repeated in the markup for each panel. A body
    /// element whose Visibility is bound keeps its binding: collapsing sets the
    /// value at current-value precedence, and expanding re-runs the binding.</summary>
    private static void WireCollapsibleHeaders(Panel host)
    {
        var stackKey = GetOrderKey(host) ?? "";
        foreach (var card in host.Children.OfType<FrameworkElement>())
        {
            var id = GetId(card);
            if (string.IsNullOrEmpty(id)) continue;
            var content = (card as Border)?.Child as Panel ?? card as Panel;
            if (content is null || content.Children.Count < 2) continue;
            if (content.Children[0] is not FrameworkElement header) continue;
            if ((bool)header.GetValue(CollapseWiredProperty)) continue;
            header.SetValue(CollapseWiredProperty, true);

            // An unpainted panel is invisible to hit-testing, so a click on the
            // empty half of the header row would land on nothing at all. Paint
            // it transparent to make the whole strip the click target.
            if (header is Panel { Background: null } strip) strip.Background = Brushes.Transparent;
            else if (header is Border { Background: null } bd) bd.Background = Brushes.Transparent;

            // The same chevron the collapsible groups elsewhere in the app use,
            // so a folded panel reads as folded at a glance.
            var chevron = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown12,
                FontSize = 11,
                Foreground = (Brush)Application.Current.FindResource("TextFillColorTertiaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
            };
            if (header is DockPanel dock)
            {
                DockPanel.SetDock(chevron, Dock.Left);
                dock.Children.Insert(Math.Min(1, dock.Children.Count), chevron);
            }
            else if (header is Panel plain) plain.Children.Insert(0, chevron);

            void Apply(bool collapsed, bool persist)
            {
                for (int i = 1; i < content.Children.Count; i++)
                {
                    if (content.Children[i] is not UIElement body) continue;
                    if (collapsed)
                    {
                        body.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
                    }
                    else
                    {
                        var bound = BindingOperations.GetBindingExpression(body, UIElement.VisibilityProperty);
                        if (bound != null) bound.UpdateTarget();
                        else body.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
                    }
                }
                chevron.Symbol = collapsed
                    ? Wpf.Ui.Controls.SymbolRegular.ChevronRight12
                    : Wpf.Ui.Controls.SymbolRegular.ChevronDown12;
                header.Tag = collapsed;
                if (persist) UserSettings.SavePanelCollapsed(stackKey, id!, collapsed);
            }

            header.Cursor = Cursors.Hand;
            header.MouseLeftButtonUp += (s, e) =>
            {
                // The drag grip starts a reorder and the help icon owns its own
                // hover — neither should fold the panel.
                for (var o = e.OriginalSource as DependencyObject; o != null && !ReferenceEquals(o, header); o = VisualTreeHelper.GetParent(o))
                    if (GetDragHandle(o) || o is Wpf.Ui.Controls.SymbolIcon) return;
                if (_dragging) return;
                Apply(!(header.Tag is true), persist: true);
                e.Handled = true;
            };

            Apply(UserSettings.LoadPanelCollapsed(stackKey, id!), persist: false);
        }
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
        // A panel the saved order has never seen (added in a later build)
        // keeps the slot the layout gave it instead of dropping to the bottom.
        for (int i = 0; i < cards.Count; i++)
        {
            var id = GetId(cards[i])!;
            if (byId.Remove(id, out var leftover))
                ordered.Insert(Math.Min(i, ordered.Count), leftover);
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
