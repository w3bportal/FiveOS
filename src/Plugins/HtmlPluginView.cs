// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FiveOS.Plugins;

/// <summary>
/// UserControl that hosts an HTML plugin's entry file in a WebView2,
/// served from a per-plugin virtual host so relative <c>&lt;script src&gt;</c>
/// / <c>&lt;img src&gt;</c> resolves to files inside the plugin folder
/// without granting the page filesystem access.
/// </summary>
public sealed class HtmlPluginView : UserControl
{
    private readonly WebView2 _web;
    private readonly string _entryAbs;
    private readonly string _pluginDir;

    public HtmlPluginView(string entryAbsPath, string pluginDir)
    {
        _entryAbs = entryAbsPath;
        _pluginDir = pluginDir;
        _web = new WebView2();
        Content = _web;
        Loaded += OnLoadedOnce;
        // Dispose the WebView2 when this control leaves the visual tree
        // (plugin disabled, app shutting down). Without this each enable/
        // disable cycle leaks an Edge process tree (~40 MB).
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        try { _web.Dispose(); }
        catch (System.Exception ex) { FiveOS.Services.FosLogger.Warn("plugin", "WebView dispose", ex); }
    }

    private bool _initialized;
    private async void OnLoadedOnce(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try { await InitAsync(); }
        catch (Exception ex)
        {
            // Swallow into the WebView's source as a tiny error page so a
            // failing plugin doesn't crash the host.
            _web.Source = new Uri("data:text/html;charset=utf-8," + Uri.EscapeDataString(
                $"<html><body style='font:13px Segoe UI;color:#bbb;background:#1a1a1c;padding:24px'>" +
                $"<h2 style='color:#ff6b6b'>Plugin failed to load</h2><pre>{System.Net.WebUtility.HtmlEncode(ex.ToString())}</pre>" +
                $"</body></html>"));
        }
    }

    private async Task InitAsync()
    {
        // Reuse the same WebView2 user-data dir as the rest of the app so
        // we don't fragment the Edge profile across multiple processes.
        var userDataDir = Path.Combine(Path.GetTempPath(), "FiveOS", "WebView2");
        Directory.CreateDirectory(userDataDir);
        var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
        await _web.EnsureCoreWebView2Async(env);

        // One virtual host per plugin so file accesses can't leak across
        // plugin boundaries. Host name is derived from the folder name —
        // sanitised since CoreWebView2 rejects non-DNS characters.
        var slug = SanitiseHost(Path.GetFileName(_pluginDir));
        var host = "plugin-" + slug + ".local";
        _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            host, _pluginDir, CoreWebView2HostResourceAccessKind.Allow);
        FiveOS.Views.WebViewDialogs.Theme(_web.CoreWebView2);
        _web.Source = new Uri($"https://{host}/{Path.GetFileName(_entryAbs)}");
    }

    private static string SanitiseHost(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '-') sb.Append(c);
            else if (c == '_' || c == ' ') sb.Append('-');
        }
        var result = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(result) ? "plugin" : result;
    }
}
