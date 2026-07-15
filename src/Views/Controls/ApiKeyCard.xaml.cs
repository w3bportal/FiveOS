// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using FiveOS.Services;
using FiveOS.Services.AiProviders;
using Wpf.Ui.Controls;

namespace FiveOS.Views.Controls;

/// <summary>
/// Reusable Settings card for an API-key-secured AI provider. Reads the
/// IAiProvider passed via <see cref="Provider"/> dependency property and
/// drives all save/edit/clear/help-link plumbing internally so the parent
/// page only needs to instantiate one of these per provider.
/// </summary>
public partial class ApiKeyCard : UserControl
{
    public static readonly DependencyProperty ProviderProperty =
        DependencyProperty.Register(nameof(Provider), typeof(IAiProvider), typeof(ApiKeyCard),
            new PropertyMetadata(null, (d, _) => ((ApiKeyCard)d).OnProviderChanged()));

    public IAiProvider? Provider
    {
        get => (IAiProvider?)GetValue(ProviderProperty);
        set => SetValue(ProviderProperty, value);
    }

    public ApiKeyCard()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void OnProviderChanged()
    {
        if (Provider == null) return;
        TitleText.Text = Provider.DisplayName;

        // Inject the provider's help text — the leading "Get a key at <link>"
        // segment is owned by the Hyperlink, the rest is plain run text.
        // Splitting on " at " keeps the link compact when the help string
        // follows our standard wording; otherwise the whole string is shown
        // as the suffix and the link reads "Get a key".
        HelpLinkText.Text = "Get a key";
        HelpTextSuffix.Text = " — " + Provider.TokenHelpText;
        Refresh();
    }

    /// <summary>Re-read SecretStore and redraw the saved/not-saved state.</summary>
    public void Refresh()
    {
        if (Provider == null) return;
        var saved = SecretStore.Has(Provider.TokenKey);
        if (saved)
        {
            EditRow.Visibility = Visibility.Collapsed;
            HelpText.Visibility = Visibility.Collapsed;
            SavedRow.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Collapsed;
            StatusText.Text = "Saved";
            StatusText.Opacity = 1.0;
            StatusPill.Background = (Brush)
                new BrushConverter().ConvertFrom("#1B4CAF50")!;
        }
        else
        {
            EditRow.Visibility = Visibility.Visible;
            HelpText.Visibility = Visibility.Visible;
            SavedRow.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            StatusText.Text = "Not saved";
            StatusText.Opacity = 0.7;
            StatusPill.Background = (Brush)FindResource("ControlFillColorTertiaryBrush");
        }
    }

    /// <summary>Open this card into edit mode and focus the key input.
    /// Called when navigating here from a feature that found the key missing.</summary>
    public void FocusEditMode()
    {
        if (Provider == null) return;
        EditRow.Visibility = Visibility.Visible;
        HelpText.Visibility = Visibility.Visible;
        SavedRow.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = SecretStore.Has(Provider.TokenKey) ? Visibility.Visible : Visibility.Collapsed;
        KeyBox.Focus();
        BringIntoView();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (Provider == null) return;
        var key = KeyBox.Password?.Trim() ?? "";
        if (string.IsNullOrEmpty(key)) return;
        SecretStore.Save(Provider.TokenKey, key);
        KeyBox.Clear();
        Refresh();
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (Provider == null) return;
        EditRow.Visibility = Visibility.Visible;
        HelpText.Visibility = Visibility.Visible;
        SavedRow.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        KeyBox.Focus();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        KeyBox.Clear();
        Refresh();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (Provider == null) return;
        SecretStore.Clear(Provider.TokenKey);
        KeyBox.Clear();
        Refresh();
    }

    private void OnOpenConsole(object sender, RoutedEventArgs e)
    {
        if (Provider == null) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Provider.ConsoleUrl,
                UseShellExecute = true,
            });
        }
        catch { /* swallow */ }
    }
}
