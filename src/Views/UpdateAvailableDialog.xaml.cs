// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Diagnostics;
using System.Windows;

namespace FiveOS.Views;

/// <summary>
/// Fluent-themed "update available" prompt (replaces the unthemed stock
/// MessageBox). Three paths out:
///   Update    → caller runs the in-place download + swap.
///   Skip      → caller persists the version so launch stops auto-prompting
///               (the title-bar badge stays for a manual change of mind).
///   Show Info → expands the release notes + release-page link inline;
///               doesn't close the dialog.
/// </summary>
public partial class UpdateAvailableDialog : Wpf.Ui.Controls.FluentWindow
{
    public enum Choice { None, Update, Skip }

    /// <summary>What the user picked. <see cref="Choice.None"/> = closed
    /// the window without deciding (treat like a soft dismiss: no skip
    /// persistence, badge stays).</summary>
    public Choice Result { get; private set; } = Choice.None;

    private readonly string? _releaseUrl;

    public UpdateAvailableDialog(Services.UpdateChecker.CheckResult result)
    {
        InitializeComponent();

        var current = $"v{result.Current.Major}.{result.Current.Minor}.{result.Current.Build}";
        HeadlineText.Text = $"FiveOS {result.LatestTag ?? "?"} is available";
        CurrentText.Text = $"You're running {current}. FiveOS restarts automatically after updating.";

        _releaseUrl = result.ReleaseUrl;
        NotesText.Text = string.IsNullOrWhiteSpace(result.Notes)
            ? "No release notes were published for this version."
            : result.Notes;
        ReleasePageLink.Visibility = string.IsNullOrWhiteSpace(_releaseUrl)
            ? Visibility.Collapsed
            : Visibility.Visible;

        // Download-less manifest: Update falls back to the release page,
        // so make that explicit on the button.
        if (string.IsNullOrWhiteSpace(result.DownloadUrl))
        {
            UpdateButton.Content = "Open download page";
            UpdateButton.ToolTip = "This release has no direct download — opens the release page in your browser.";
        }
    }

    private void OnShowInfo(object sender, RoutedEventArgs e)
    {
        var show = InfoPanel.Visibility != Visibility.Visible;
        InfoPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ShowInfoButton.Content = show ? "Hide Info" : "Show Info";
    }

    private void OnReleasePage(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_releaseUrl)) return;
        try { Process.Start(new ProcessStartInfo { FileName = _releaseUrl, UseShellExecute = true }); }
        catch { /* browser launch is best-effort */ }
    }

    private void OnUpdate(object sender, RoutedEventArgs e)
    {
        Result = Choice.Update;
        Close();
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        Result = Choice.Skip;
        Close();
    }
}
