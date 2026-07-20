// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using FiveOS.Services;
using FiveOS.Services.AiProviders;
using FiveOS.Views.Controls;

namespace FiveOS.Views;

public partial class SettingsView : UserControl
{
    /// <summary>Anchor points the rest of the app can deep-link into.
    /// Specific AI providers are addressed by id via
    /// <see cref="FocusAiProvider(string)"/> — only Sketchfab needs a
    /// dedicated enum slot because it lives outside the AI list.</summary>
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
            ["cache"]   = CachePage,
            ["addons"]  = AddonsPage,
            ["about"]   = AboutPage,
        };
        AiProviderList.ItemsSource = AiProviderRegistry.All;
        RefreshOutput();
        RefreshSketchfab();
        _ = RefreshCacheStatusAsync();
        RefreshAbout();
        RefreshLanguage();
        RefreshAddons();
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

    private void OnServerFolderBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Pick your FiveM server's resource folder",
            InitialDirectory = string.IsNullOrWhiteSpace(ServerFolderBox.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : ServerFolderBox.Text,
        };
        if (dlg.ShowDialog() == true)
        {
            ServerFolderBox.Text = dlg.FolderName;
            UserSettings.SaveServerResourceFolder(dlg.FolderName);
            RefreshOutput();
        }
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
    private bool _suppressGlobalUpdateToggle;

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

        // Hydrate toggles from disk without firing their handlers.
        _suppressGlobalUpdateToggle = true;
        GlobalUpdateToggle.IsChecked = UserSettings.LoadGlobalUpdate();
        _suppressGlobalUpdateToggle = false;

        _suppressDiscordToggle = true;
        DiscordPresenceToggle.IsChecked = UserSettings.LoadEnableDiscordPresence();
        _suppressDiscordToggle = false;
    }

    private void OnGlobalUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressGlobalUpdateToggle) return;
        UserSettings.SaveGlobalUpdate(GlobalUpdateToggle.IsChecked == true);
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

    // ─────────────── Addons ───────────────

    private void RefreshAddons()
    {
        // Items are bound to MainViewModel.AllPlugins; the empty-state hint
        // needs a manual peek at the realised count.
        Dispatcher.BeginInvoke(new System.Action(UpdateNoPluginsHint),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    /// <summary>Filter the addons list against the search box. Both the
    /// built-in addon rows AND each ItemsControl-generated plugin row are
    /// filtered — every term must appear in the row's searchable hay so
    /// multi-word queries narrow rather than fan out.</summary>
    private void OnAddonsSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (AddonsList == null) return;
        var query = (AddonsSearchBox.Text ?? "").Trim().ToLowerInvariant();
        var terms = string.IsNullOrEmpty(query)
            ? System.Array.Empty<string>()
            : query.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);

        int visible = 0;
        foreach (var child in AddonsList.Children)
        {
            // Skip the ItemsControl host and the empty-state hint — the
            // plugin items are filtered below; the hint visibility is
            // computed separately.
            if (child is ItemsControl) continue;
            if (ReferenceEquals(child, NoPluginsHint)) continue;
            if (child is not FrameworkElement fe) continue;
            var hay = (fe.Tag as string)?.ToLowerInvariant() ?? "";
            bool match = terms.Length == 0 || System.Array.TrueForAll(terms, t => hay.Contains(t));
            fe.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            if (match) visible++;
        }

        // Plugin rows generated by the ItemsControl. Walk the materialised
        // containers and toggle each one's visibility against the same
        // search terms.
        if (PluginItems?.Items != null)
        {
            for (int i = 0; i < PluginItems.Items.Count; i++)
            {
                if (PluginItems.ItemContainerGenerator.ContainerFromIndex(i)
                    is not FrameworkElement container) continue;
                var row = FindChild<Border>(container);
                if (row == null) continue;
                var hay = (row.Tag as string)?.ToLowerInvariant() ?? "";
                bool match = terms.Length == 0 || System.Array.TrueForAll(terms, t => hay.Contains(t));
                container.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                if (match) visible++;
            }
        }

        AddonsEmptyHint.Visibility = (terms.Length > 0 && visible == 0)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnOpenPluginsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(FiveOS.Plugins.PluginManager.PluginsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = FiveOS.Plugins.PluginManager.PluginsDir,
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex)
        {
            AppDialog.Show("Couldn't open plugins folder:\n\n" + ex.Message,
                "FiveOS — Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnRefreshPlugins(object sender, RoutedEventArgs e)
    {
        if (Application.Current?.MainWindow?.DataContext is FiveOS.ViewModels.MainViewModel vm)
            await vm.RefreshPluginsAsync();
        UpdateNoPluginsHint();
    }

    /// <summary>Scaffold a starter plugin folder under <c>%AppData%\FiveOS\plugins\</c>
    /// and reveal it in Explorer. Two flavours:
    ///   • HTML — drop a manifest + index.html. Runs immediately, no
    ///     compile step. Sandboxed in WebView2.
    ///   • DLL — drop a manifest + .csproj + sample .cs. User runs
    ///     <c>dotnet build</c> themselves; the manifest already points at
    ///     the eventual output dll path.
    /// </summary>
    private async void OnNewPluginFromTemplate(object sender, RoutedEventArgs e)
    {
        // Tiny 2-button picker — keeps the surface small while giving the
        // user a clear pick. Could grow into a wizard later.
        var which = AppDialog.Show(
            "Pick a starter template:\n\n" +
            "Yes → HTML plugin (no build step, sandboxed in WebView2).\n" +
            "No → C# / DLL plugin (you'll need 'dotnet build' to compile).\n\n" +
            "Either way the new plugin folder gets revealed in Explorer.",
            "FiveOS — new plugin from template",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, Window.GetWindow(this));
        if (which == MessageBoxResult.Cancel) return;

        try
        {
            var dir = which == MessageBoxResult.Yes
                ? FiveOS.Plugins.PluginTemplates.ScaffoldHtmlPlugin()
                : FiveOS.Plugins.PluginTemplates.ScaffoldDllPlugin();

            // Reveal the new folder so the user can start editing right away.
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });

            // Refresh discovery so the new plugin shows up in the addons
            // list. HTML plugins are immediately usable; DLL plugins need
            // a build first and will surface as "missing entry" until then.
            if (Application.Current?.MainWindow?.DataContext is FiveOS.ViewModels.MainViewModel vm)
                await vm.RefreshPluginsAsync();
            UpdateNoPluginsHint();
        }
        catch (System.Exception ex)
        {
            AppDialog.Show("Couldn't scaffold plugin:\n\n" + ex.Message,
                "FiveOS — Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateNoPluginsHint()
    {
        if (NoPluginsHint == null) return;
        var hasPlugins = PluginItems?.Items.Count > 0;
        NoPluginsHint.Visibility = hasPlugins ? Visibility.Collapsed : Visibility.Visible;
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
