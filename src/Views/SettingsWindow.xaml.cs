// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Windows;
using System.Windows.Input;

namespace FiveOS.Views;

/// <summary>A medium, themed modal that hosts the existing <see cref="SettingsView"/>
/// (nav + pages) instead of the old full-content-column overlay. The view is
/// injected rather than built in XAML so its on-load cache scan runs only when
/// the user actually opens Settings.</summary>
public partial class SettingsWindow
{
    /// <summary>The hosted view, exposed so callers can deep-link into a
    /// section (e.g. an AI provider card) after the window is shown.</summary>
    public SettingsView SettingsView { get; }

    public SettingsWindow(SettingsView view)
    {
        InitializeComponent();
        SettingsView = view;
        Host.Content = view;

        // Default size is chosen so a whole settings page fits with no
        // scrollbar (240px nav + the content column's 780px MaxWidth). Clamp to
        // the work area so it still fits on a small display — 920px tall does
        // not fit a 1366x768 laptop.
        var work = SystemParameters.WorkArea;
        Width = Math.Min(Width, work.Width * 0.95);
        Height = Math.Min(Height, work.Height * 0.95);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
