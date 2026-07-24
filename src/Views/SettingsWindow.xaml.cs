// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

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
