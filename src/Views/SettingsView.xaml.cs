// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using FiveOS.Services;
using FiveOS.Services.AiProviders;
using FiveOS.ViewModels;
using FiveOS.Views.Controls;

namespace FiveOS.Views;

public partial class SettingsView : UserControl
{
    /// <summary>Anchor points the rest of the app can deep-link into.
    /// Specific AI providers are addressed by id via
    /// <see cref="FocusAiProvider(string)"/> — Sketchfab lives outside
    /// the AI list and needs a dedicated enum slot.</summary>
    public enum FocusSection { None, Sketchfab }

    // Pages keyed by their nav-item Tag so reordering the NavList in
    // XAML can't silently desync from a positional array.
    private System.Collections.Generic.Dictionary<string, StackPanel> _pagesByTag = null!;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _pagesByTag = new()
        {
            ["general"] = GeneralPage,
            ["api"]     = ApiKeysPage,
            ["output"]  = OutputPage,
            ["cache"]     = CachePage,
            ["shortcuts"] = ShortcutsPage,
            ["about"]     = AboutPage,
        };
        AiProviderList.ItemsSource = AiProviderRegistry.All;
        RefreshOutput();
        RefreshSketchfab();
        _ = RefreshCacheStatusAsync();
        RefreshAbout();
        RefreshLanguage();
        RefreshDefaultEmotePed();
        RefreshControlRigStyle();
        RefreshAccentSwatches();
    }

    /// <summary>
    /// Navigate to a specific top-level section (called from outside when a
    /// feature discovers its key is missing).
    /// </summary>
    public void Focus(FocusSection section)
    {
        if (section == FocusSection.None) return;
        SelectNav("api");
        if (section == FocusSection.Sketchfab)
        {
            SketchfabCard.BringIntoView();
            OnEditSketchfab(this, new RoutedEventArgs());
            SketchfabBox.Focus();
        }
    }

    /// <summary>
    /// Scroll the API-keys page to a specific AI provider card and put it
    /// into edit mode. Used by ImageTo3DView when the user-selected
    /// provider has no key saved yet.
    /// </summary>
    public void FocusAiProvider(string providerId)
    {
        SelectNav("api");
        // Walk the realised ItemsControl children to find the matching
        // ApiKeyCard. Defer to next render tick because if we just
        // assigned ItemsSource, the containers aren't materialised yet.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            for (int i = 0; i < AiProviderList.Items.Count; i++)
            {
                if (AiProviderList.Items[i] is IAiProvider p && p.Id == providerId)
                {
                    var container = AiProviderList.ItemContainerGenerator
                        .ContainerFromIndex(i) as ContentPresenter;
                    container?.ApplyTemplate();
                    var card = FindChild<ApiKeyCard>(container);
                    if (card != null)
                    {
                        card.BringIntoView();
                        card.FocusEditMode();
                    }
                    return;
                }
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static T? FindChild<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) return null;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var deeper = FindChild<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }

    // ─────────────── Navigation ───────────────

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pagesByTag == null) return;
        if (NavList.SelectedItem is ListBoxItem item && item.Tag is string tag)
            ShowPage(tag);
    }

    private void ShowPage(string tag)
    {
        foreach (var (k, page) in _pagesByTag)
            page.Visibility = string.Equals(k, tag, System.StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;

        // Lazy-load cache size on first switch to the cache page.
        if (string.Equals(tag, "cache", System.StringComparison.OrdinalIgnoreCase))
            _ = RefreshCacheStatusAsync();
    }

    /// <summary>Programmatic nav selection by tag, used by the deep-link
    /// helpers (Focus, FocusAiProvider). Falls back to the first item
    /// if the tag isn't found.</summary>
    private void SelectNav(string tag)
    {
        for (int i = 0; i < NavList.Items.Count; i++)
        {
            if (NavList.Items[i] is ListBoxItem item
                && string.Equals(item.Tag as string, tag, System.StringComparison.OrdinalIgnoreCase))
            {
                NavList.SelectedIndex = i;
                ShowPage(tag);
                return;
            }
        }
    }

    // ─────────────── Output ───────────────

    private bool _suppressDefaultEmotePedPicker;

    private void RefreshDefaultEmotePed()
    {
        _suppressDefaultEmotePedPicker = true;
        DefaultEmotePedPicker.SelectedValue = UserSettings.LoadDefaultEmotePed();
        _suppressDefaultEmotePedPicker = false;
    }

    private bool _suppressRigStyleSliders;

    private void RefreshControlRigStyle()
    {
        _suppressRigStyleSliders = true;
        RigOpacitySlider.Value = UserSettings.LoadControlRigOpacity();
        RigThicknessSlider.Value = UserSettings.LoadControlRigThickness();
        _suppressRigStyleSliders = false;
        UpdateRigStyleLabels();
    }

    private void UpdateRigStyleLabels()
    {
        RigOpacityValue.Text = $"{RigOpacitySlider.Value * 100:0}%";
        RigThicknessValue.Text = $"{RigThicknessSlider.Value * 100:0}%";
    }

    private void OnControlRigStyleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // ValueChanged fires during InitializeComponent before the sibling
        // slider exists, so bail until both are up.
        if (RigOpacitySlider is null || RigThicknessSlider is null) return;
        UpdateRigStyleLabels();
        if (_suppressRigStyleSliders) return;
        // Saving raises ControlRigStyleChanged, which the Emotes viewport
        // listens for so the rig updates live while this dialog is open.
        UserSettings.SaveControlRigStyle(RigOpacitySlider.Value, RigThicknessSlider.Value);
    }

    private void OnDefaultEmotePedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDefaultEmotePedPicker) return;
        if (DefaultEmotePedPicker.SelectedValue is string variant)
            UserSettings.SaveDefaultEmotePed(variant);
    }

    private void RefreshOutput()
    {
        SingleOutputBox.Text = UserSettings.LoadSingleOutputFolder() ?? "";
        ServerFolderBox.Text = UserSettings.LoadServerResourceFolder() ?? "";

        var layout = UserSettings.LoadServerLayout();
        ServerLayoutShared.IsChecked = layout == ServerLayout.Shared;
        ServerLayoutPerAsset.IsChecked = layout == ServerLayout.PerAsset;

        // Status pill: "On / shared", "On / per-asset", "Off". Set from
        // C# in every branch — assigning .Text overrides any XAML binding,
        // so the binding can't be the source of truth here.
        var loc = LocalizationService.Instance;
        var serverActive = UserSettings.IsServerModeActive();
        if (serverActive)
        {
            var on = layout == ServerLayout.Shared
                ? loc["Settings.LayoutShared"]
                : loc["Settings.LayoutPerAsset"];
            ServerStatusText.Text = "● " + on;
            ServerStatusText.Opacity = 1.0;
            ServerStatusPill.Background = (System.Windows.Media.Brush)
                new System.Windows.Media.BrushConverter().ConvertFrom("#1B4CAF50")!;
        }
        else
        {
            ServerStatusText.Text = loc["Settings.Off"];
            ServerStatusText.Opacity = 0.7;
            ServerStatusPill.Background = (System.Windows.Media.Brush)FindResource("ControlFillColorTertiaryBrush");
        }
    }

    private void OnSingleOutputBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Pick the default output folder for converted props and optimize results",
            InitialDirectory = string.IsNullOrWhiteSpace(SingleOutputBox.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : SingleOutputBox.Text,
        };
        if (dlg.ShowDialog() == true)
        {
            SingleOutputBox.Text = dlg.FolderName;
            UserSettings.SaveSingleOutputFolder(dlg.FolderName);
        }
    }

    private void OnSingleOutputCommit(object sender, RoutedEventArgs e)
    {
        var path = SingleOutputBox.Text?.Trim() ?? "";
        UserSettings.SaveSingleOutputFolder(string.IsNullOrEmpty(path) ? null : path);
    }

    private void OnSingleOutputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnSingleOutputCommit(sender, e);
    }

    private void OnSingleOutputReset(object sender, RoutedEventArgs e)
    {
        SingleOutputBox.Text = "";
        UserSettings.SaveSingleOutputFolder(null);
    }

    private bool _folderPickerOpen;

    private async void OnServerFolderBrowse(object sender, RoutedEventArgs e)
    {
        if (_folderPickerOpen) return;
        _folderPickerOpen = true;
        try
        {
            var initial = string.IsNullOrWhiteSpace(ServerFolderBox.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : ServerFolderBox.Text;
            var folder = await StaFileDialogs.OpenFolderAsync(Window.GetWindow(this), dlg =>
            {
                dlg.Title = "Pick your FiveM server's resource folder";
                dlg.InitialDirectory = initial;
            });
            if (folder is null) return;
            ServerFolderBox.Text = folder;
            UserSettings.SaveServerResourceFolder(folder);
            RefreshOutput();
        }
        finally { _folderPickerOpen = false; }
    }

    private void OnServerFolderCommit(object sender, RoutedEventArgs e)
    {
        var path = ServerFolderBox.Text?.Trim() ?? "";
        UserSettings.SaveServerResourceFolder(string.IsNullOrEmpty(path) ? null : path);
        RefreshOutput();
    }

    private void OnServerFolderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnServerFolderCommit(sender, e);
    }

    private void OnServerFolderClear(object sender, RoutedEventArgs e)
    {
        ServerFolderBox.Text = "";
        UserSettings.SaveServerResourceFolder(null);
        RefreshOutput();
    }

    private void OnServerLayoutChanged(object sender, RoutedEventArgs e)
    {
        var layout = ServerLayoutPerAsset.IsChecked == true
            ? ServerLayout.PerAsset
            : ServerLayout.Shared;
        UserSettings.SaveServerLayout(layout);
        RefreshOutput();
    }

    // ─────────────── Sketchfab ───────────────

    private void RefreshSketchfab()
    {
        var loc = LocalizationService.Instance;
        var saved = SecretStore.Has(SketchfabClient.TokenKey);
        if (saved)
        {
            SketchfabEditRow.Visibility = Visibility.Collapsed;
            SketchfabHelp.Visibility = Visibility.Collapsed;
            SketchfabSavedRow.Visibility = Visibility.Visible;
            SketchfabCancel.Visibility = Visibility.Collapsed;
            SketchfabStatus.Text = loc["Settings.Saved"];
            SketchfabStatus.Opacity = 1.0;
            SketchfabPill.Background = (System.Windows.Media.Brush)
                new System.Windows.Media.BrushConverter().ConvertFrom("#1B4CAF50")!;
        }
        else
        {
            SketchfabEditRow.Visibility = Visibility.Visible;
            SketchfabHelp.Visibility = Visibility.Visible;
            SketchfabSavedRow.Visibility = Visibility.Collapsed;
            SketchfabCancel.Visibility = Visibility.Collapsed;
            SketchfabStatus.Text = loc["Settings.NotSaved"];
            SketchfabStatus.Opacity = 0.7;
            SketchfabPill.Background = (System.Windows.Media.Brush)FindResource("ControlFillColorTertiaryBrush");
        }
    }

    private void OnSaveSketchfab(object sender, RoutedEventArgs e)
    {
        var token = SketchfabBox.Password?.Trim() ?? "";
        if (string.IsNullOrEmpty(token)) return;
        SecretStore.Save(SketchfabClient.TokenKey, token);
        SketchfabBox.Clear();
        RefreshSketchfab();
    }

    private void OnEditSketchfab(object sender, RoutedEventArgs e)
    {
        SketchfabEditRow.Visibility = Visibility.Visible;
        SketchfabHelp.Visibility = Visibility.Visible;
        SketchfabSavedRow.Visibility = Visibility.Collapsed;
        SketchfabCancel.Visibility = Visibility.Visible;
        SketchfabBox.Focus();
    }

    private void OnCancelSketchfab(object sender, RoutedEventArgs e)
    {
        SketchfabBox.Clear();
        RefreshSketchfab();
    }

    private void OnClearSketchfab(object sender, RoutedEventArgs e)
    {
        SecretStore.Clear(SketchfabClient.TokenKey);
        SketchfabBox.Clear();
        RefreshSketchfab();
    }

    private void OnOpenSketchfabSite(object sender, RoutedEventArgs e)
        => OpenUrl("https://sketchfab.com/settings/password");

    // ─────────────── Cache ───────────────

    private async Task RefreshCacheStatusAsync()
    {
        CacheClearButton.IsEnabled = false;
        CacheStatus.Text = "Calculating…";
        var bytes = await Task.Run(() => CacheService.ComputeSize());
        CacheStatus.Text = bytes == 0
            ? "Nothing to clear"
            : CacheService.FormatBytes(bytes) + " on disk";
        CacheClearButton.IsEnabled = bytes > 0;
    }

    private async void OnClearCache(object sender, RoutedEventArgs e)
    {
        CacheClearButton.IsEnabled = false;
        CacheStatus.Text = "Clearing…";
        var report = await Task.Run(() => CacheService.Clear());

        var msg = "Freed " + CacheService.FormatBytes(report.BytesFreed);
        if (report.SkippedDirs > 0)
            msg += $" — {report.SkippedDirs} item(s) in use; restart the app and clear again to remove them";
        CacheStatus.Text = msg;

        var remaining = await Task.Run(() => CacheService.ComputeSize());
        CacheClearButton.IsEnabled = remaining > 0;
    }

    // ─────────────── About ───────────────

    private bool _suppressDiscordToggle;

    private void RefreshAbout()
    {
        var asm = typeof(MainWindow).Assembly;
        var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
        if (info.Length > 0)
        {
            var raw = ((System.Reflection.AssemblyInformationalVersionAttribute)info[0]).InformationalVersion;
            var plus = raw.IndexOf('+');
            AboutVersion.Text = plus > 0 ? raw[..plus] : raw;
        }
        else
        {
            var v = asm.GetName().Version;
            AboutVersion.Text = v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }

        _suppressDiscordToggle = true;
        DiscordPresenceToggle.IsChecked = UserSettings.LoadEnableDiscordPresence();
        _suppressDiscordToggle = false;
    }

    private void OnDiscordPresenceToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressDiscordToggle) return;
        var enabled = DiscordPresenceToggle.IsChecked == true;
        UserSettings.SaveEnableDiscordPresence(enabled);
        if (enabled)
            DiscordPresenceService.Initialize();
        else
            DiscordPresenceService.Shutdown();
    }

    private void OnOpenCredits(object sender, RoutedEventArgs e)
    {
        // Credits used to be its own window; it's now the second tab on the
        // About dialog. Open About — the user can switch tabs from there.
        var about = new AboutWindow { Owner = Window.GetWindow(this) };
        about.ShowDialog();
    }

    // ─────────────── Language ───────────────

    private bool _suppressLanguagePicker;

    private void RefreshLanguage()
    {
        _suppressLanguagePicker = true;
        LanguagePicker.ItemsSource = LocalizationService.Available;
        LanguagePicker.SelectedValue = LocalizationService.Instance.CurrentLanguage;
        _suppressLanguagePicker = false;
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguagePicker) return;
        if (LanguagePicker.SelectedValue is string code && !string.IsNullOrEmpty(code))
        {
            UserSettings.SaveLanguage(code);
            LocalizationService.Instance.SetLanguage(code);
            // XAML bindings repaint via Item[] notification; the code-set
            // status labels (server pill, sketchfab status) need an
            // explicit re-run to pick up the new strings.
            RefreshOutput();
            RefreshSketchfab();
        }
    }

    // ─────────────── Accent color ───────────────

    private sealed class AccentSwatchRow
    {
        public string Name { get; init; } = "";
        public string Hex { get; init; } = "";
        public System.Windows.Media.Brush Fill { get; init; } = System.Windows.Media.Brushes.Transparent;
        public System.Windows.Media.Brush BorderBrush { get; init; } = System.Windows.Media.Brushes.Transparent;
    }

    private void RefreshAccentSwatches()
    {
        if (AccentSwatches is null) return;
        var current = ThemeAccent.ToHex(ThemeAccent.Current).ToUpperInvariant();
        AccentSwatches.ItemsSource = ThemeAccent.Presets.Select(p =>
        {
            ThemeAccent.TryParseHex(p.Hex, out var c);
            var selected = string.Equals(p.Hex, current, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ThemeAccent.ToHex(c), current, StringComparison.OrdinalIgnoreCase);
            return new AccentSwatchRow
            {
                Name = p.Name + " (" + p.Hex + ")",
                Hex = p.Hex,
                Fill = new System.Windows.Media.SolidColorBrush(c),
                BorderBrush = selected
                    ? System.Windows.Media.Brushes.White
                    : new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55)),
            };
        }).ToList();
    }

    private void OnAccentSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not string hex) return;
        ThemeAccent.ApplyHex(hex, persist: true);
        RefreshAccentSwatches();
    }

    // ─────────────── Shared ───────────────

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { /* swallow */ }
    }
}
