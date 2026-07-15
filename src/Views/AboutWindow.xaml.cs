// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace FiveOS.Views;

public partial class AboutWindow : FluentWindow
{
    // Canonical, editable credits live in the repo. Refresh in-app pulls
    // the latest. Falls back to the embedded copy when offline.
    private const string CreditsRemoteUrl =
        "https://raw.githubusercontent.com/w3bportal/FiveOS/main/src/Assets/credits.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public AboutWindow()
    {
        InitializeComponent();

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        if (ver != null)
            VersionText.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build}";

        AuthorText.Text = $"By: {App.AuthorName}";
        WebLinkText.Text = "GitHub  ·  Source code";
        UpdateStatusText.Text = LastCheckedLine();

        // Lazy-load credits the first time the user opens that tab.
        Loaded += async (_, _) => await LoadCreditsAsync();
    }

    private static string LastCheckedLine()
    {
        var t = Services.UserSettings.LoadLastUpdateCheck();
        if (t == null) return "Never checked for updates.";
        var ago = DateTime.UtcNow - t.Value;
        string when = ago.TotalMinutes < 1 ? "just now"
            : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} min ago"
            : ago.TotalHours < 24 ? $"{(int)ago.TotalHours} h ago"
            : $"{(int)ago.TotalDays} d ago";
        return $"Last checked: {when}";
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking…";
        try
        {
            var r = await Services.UpdateChecker.CheckAsync();
            switch (r.Status)
            {
                case Services.UpdateChecker.Status.UpToDate:
                    Services.UserSettings.SaveLastUpdateCheck(DateTime.UtcNow);
                    UpdateStatusText.Text =
                        $"You're on the latest (v{r.Current.Major}.{r.Current.Minor}.{r.Current.Build}). {LastCheckedLine()}";
                    break;
                case Services.UpdateChecker.Status.UpdateAvailable:
                    Services.UserSettings.SaveLastUpdateCheck(DateTime.UtcNow);
                    UpdateStatusText.Text =
                        $"v{r.Latest!.Major}.{r.Latest.Minor}.{r.Latest.Build} available — use the Update badge in the status bar (or Help → Check for updates) to install.";
                    break;
                case Services.UpdateChecker.Status.NoReleases:
                    UpdateStatusText.Text = "No release manifest has been published yet.";
                    break;
                default:
                    UpdateStatusText.Text = $"Couldn't reach the update host: {r.Error}";
                    break;
            }
        }
        catch (Exception ex) { UpdateStatusText.Text = $"Update check failed: {ex.Message}"; }
        finally { CheckUpdateButton.IsEnabled = true; }
    }

    private async void OnRefreshCredits(object sender, RoutedEventArgs e)
    {
        await LoadCreditsAsync();
    }

    private void OnWebLink(object sender, RoutedEventArgs e)
    {
        Open(App.WebPortalUrl);
    }

    private void OnDiscordLink(object sender, RoutedEventArgs e)
    {
        Open(App.DiscordInviteUrl);
    }

    private const string DonateUrl = "https://paypal.me/webportal1";

    private void OnDonate(object sender, RoutedEventArgs e) => Open(DonateUrl);

    private void OnOk(object sender, RoutedEventArgs e) => Close();

    // ─────────────── Credits loader ───────────────

    private async Task LoadCreditsAsync()
    {
        CreditsStatusText.Text = "Loading latest credits from GitHub…";
        CreditsHost.Children.Clear();

        CreditsDoc? doc = null;
        string source = "";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FiveOS-credits");
            var json = await http.GetStringAsync(CreditsRemoteUrl);
            doc = JsonSerializer.Deserialize<CreditsDoc>(json, JsonOpts);
            source = "Loaded from GitHub.";
        }
        catch
        {
            // Network or parse failure — fall through to embedded copy.
        }

        if (doc is null)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var s = asm.GetManifestResourceStream("FiveOS.credits.json");
                if (s != null)
                {
                    using var sr = new StreamReader(s);
                    var json = await sr.ReadToEndAsync();
                    doc = JsonSerializer.Deserialize<CreditsDoc>(json, JsonOpts);
                    source = "Loaded from bundled copy (offline).";
                }
            }
            catch { /* fall through to error */ }
        }

        if (doc is null)
        {
            CreditsStatusText.Text = "Could not load credits. Check your connection and click Refresh.";
            return;
        }

        CreditsIntroText.Text = string.IsNullOrWhiteSpace(doc.Intro)
            ? "FiveOS is built on these third-party projects."
            : doc.Intro;
        CreditsStatusText.Text = source;

        Render(doc);
    }

    private void Render(CreditsDoc doc)
    {
        if (doc.Categories is null) return;
        foreach (var cat in doc.Categories)
        {
            if (cat?.Items is null || cat.Items.Count == 0) continue;
            CreditsHost.Children.Add(new TextBlock
            {
                Text = cat.Name ?? "(uncategorised)",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 6),
            });
            foreach (var item in cat.Items)
            {
                if (item is null) continue;
                CreditsHost.Children.Add(BuildItem(item));
            }
        }
    }

    private UIElement BuildItem(CreditsItem item)
    {
        var title = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        if (!string.IsNullOrWhiteSpace(item.Url))
        {
            var link = new Hyperlink(new Run(item.Name ?? item.Url))
            {
                NavigateUri = TryUri(item.Url),
                ToolTip = item.Url,
            };
            link.RequestNavigate += (_, e) =>
            {
                Open(e.Uri?.ToString() ?? item.Url!);
                e.Handled = true;
            };
            title.Inlines.Add(link);
        }
        else
        {
            title.Inlines.Add(new Run(item.Name ?? "(unnamed)"));
        }
        if (!string.IsNullOrWhiteSpace(item.License))
        {
            title.Inlines.Add(new Run($"  ·  {item.License}")
            {
                FontSize = 11,
                FontWeight = FontWeights.Normal,
                Foreground = Brushes.Gray,
            });
        }

        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(item.Description))
            stack.Children.Add(new TextBlock
            {
                Text = item.Description,
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0),
            });
        return stack;
    }

    private static Uri? TryUri(string? s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null;

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { /* swallow */ }
    }

    // ───── DTOs ─────

    private sealed class CreditsDoc
    {
        [JsonPropertyName("intro")] public string? Intro { get; set; }
        [JsonPropertyName("categories")] public List<CreditsCategory>? Categories { get; set; }
    }

    private sealed class CreditsCategory
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("items")] public List<CreditsItem>? Items { get; set; }
    }

    private sealed class CreditsItem
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("license")] public string? License { get; set; }
    }
}
