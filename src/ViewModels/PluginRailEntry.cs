// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FiveOS.Plugins;

namespace FiveOS.ViewModels;

/// <summary>
/// View-model row for one discovered plugin. Wraps the disk-side
/// <see cref="PluginRecord"/> with mutable UI state (enable toggle) plus
/// a cached lazy view so we only instantiate the heavy WebView2 / DLL
/// plugin once per session.
///
/// Bound twice in the UI: once in Settings → Addons (toggle row) and
/// once in the activity rail (only when <see cref="IsEnabled"/> is true).
/// </summary>
public partial class PluginRailEntry : ObservableObject
{
    public PluginRecord Record { get; }

    public PluginRailEntry(PluginRecord record)
    {
        Record = record;
    }

    [ObservableProperty]
    private bool _isEnabled;

    public string Id          => Record.Id;
    public string Name        => Record.Name;
    public string Description => Record.Description;
    public string Version     => Record.Version;
    public string Author      => Record.Author;
    public string? IconPath   => Record.IconPath;

    /// <summary>Pre-formatted tag string for the activity rail's Border —
    /// matches the "plugin:&lt;id&gt;" convention <c>OpenViewCommand</c>
    /// dispatches on. Built as a property here because XAML's
    /// <c>StringFormat</c> on a <c>Binding</c> with an <c>object</c>
    /// target (Border.Tag) is silently dropped.</summary>
    public string RailTag => $"plugin:{Record.Id}";

    /// <summary>Searchable text for the addons-page filter. Lowercased so
    /// the search code-behind can do case-insensitive contains-checks
    /// without re-allocating per keystroke.</summary>
    public string SearchHay =>
        ((Name ?? "") + " " + (Description ?? "") + " " + (Author ?? "")).ToLowerInvariant();

    private UserControl? _view;

    /// <summary>Build (or return the cached) view for this plugin. Errors
    /// are surfaced as a tiny inline error card so the rail doesn't get
    /// stuck on a half-loaded plugin.</summary>
    public UserControl GetOrCreateView()
    {
        if (_view != null) return _view;
        try { _view = Record.ViewFactory(); }
        catch (System.Exception ex) { _view = BuildErrorCard(ex); }
        return _view;
    }

    private static UserControl BuildErrorCard(System.Exception ex)
    {
        var tb = new System.Windows.Controls.TextBlock
        {
            Text = $"Plugin failed to load:\n\n{ex.Message}",
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(24),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Foreground = System.Windows.Media.Brushes.Salmon,
        };
        return new UserControl { Content = tb };
    }
}
