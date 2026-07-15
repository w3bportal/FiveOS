// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace FiveOS.Views;

/// <summary>
/// Blender-style startup splash: quick-launch tiles for every tool on the
/// left, a Recent Files list on the right, and a "show on startup" toggle.
/// Floats over the main window (which boots into the 3D Model tool) and is
/// dismissed by picking something, pressing Esc, or the close button.
/// Navigation + file opening are delegated to the owning <see cref="MainWindow"/>.
/// </summary>
public partial class WelcomeWindow : Window
{
    /// <summary>One recent entry: display name, its folder, and the full path.</summary>
    public sealed record RecentItem(string Name, string Dir, string Path);

    public WelcomeWindow()
    {
        InitializeComponent();

        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v != null ? $"Version {v.Major}.{v.Minor}.{v.Build}" : "";

        ShowOnStartupCheck.IsChecked = Services.UserSettings.LoadShowWelcomeOnStartup();
        LoadRecents();
    }

    private void LoadRecents()
    {
        var items = new List<RecentItem>();
        foreach (var p in Services.UserSettings.LoadRecentFiles())
        {
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
            items.Add(new RecentItem(
                Path.GetFileName(p),
                Path.GetDirectoryName(p) ?? string.Empty,
                p));
            if (items.Count >= 12) break;
        }
        RecentList.ItemsSource = items;
        EmptyRecent.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTile(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string tag)
            (Owner as MainWindow)?.NavigateFromWelcome(tag);
        Close();
    }

    private void OnRecent(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string path)
            (Owner as MainWindow)?.OpenModelFromWelcome(path);
        Close();
    }

    private void OnToggleStartup(object sender, RoutedEventArgs e)
        => Services.UserSettings.SaveShowWelcomeOnStartup(ShowOnStartupCheck.IsChecked == true);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
