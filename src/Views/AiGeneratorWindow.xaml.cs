// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using FiveOS.ViewModels;
using Wpf.Ui.Controls;

namespace FiveOS.Views;

/// <summary>
/// Modal window that hosts the <see cref="ImageTo3DView"/> generator on the
/// left and an interactive 3D preview (reused <c>viewer.html</c>) on the
/// right. The user can orbit/zoom the generated mesh before clicking
/// "Use latest" to load it into the main 3D Model viewer. Returns the chosen
/// model path via <see cref="ResultModelPath"/>.
/// </summary>
public partial class AiGeneratorWindow : FluentWindow
{
    /// <summary>Set to the latest generated model path when the user clicks
    /// "Use latest". Null on Cancel / close-without-generating.</summary>
    public string? ResultModelPath { get; private set; }

    private readonly ImageTo3DViewModel _vm;

    // ── Preview viewer state ──
    private string? _previewSessionDir;
    private bool _previewReady;
    private string? _pendingPreviewUrl;     // model queued before viewer booted
    private string? _lastPreviewedPath;     // avoid re-staging the same file

    public AiGeneratorWindow(ImageTo3DViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        GeneratorView.DataContext = vm;

        RefreshUseButton();
        _vm.PropertyChanged += OnVmPropertyChanged;

        Loaded += async (_, _) => await InitPreviewAsync();
        Closed += (_, _) =>
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            TeardownPreview();
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImageTo3DViewModel.LastOutputPath) ||
            e.PropertyName == nameof(ImageTo3DViewModel.HasLastOutput))
        {
            RefreshUseButton();
            ShowPreview(_vm.LastOutputPath);
        }
    }

    private void RefreshUseButton()
    {
        UseButton.IsEnabled = _vm.HasLastOutput;
        if (_vm.HasLastOutput)
            StatusText.Text = "Preview it on the right, then Use latest — or Texture with AI first.";
    }

    // ─────────────── Interactive preview ───────────────

    private async System.Threading.Tasks.Task InitPreviewAsync()
    {
        try
        {
            // Distinct user-data folder from the main window's viewer so the
            // two WebView2 environments never contend for the same profile.
            var userDataDir = Path.Combine(Path.GetTempPath(), "FiveOS", "WebView2-aipreview");
            Directory.CreateDirectory(userDataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await PreviewViewport.EnsureCoreWebView2Async(env);

            // Per-session dir with a copy of the viewer bundle, served from a
            // dedicated virtual host (distinct from the main window's
            // viewer.local so the two never collide).
            _previewSessionDir = Path.Combine(Path.GetTempPath(), "FiveOS", "AiPreview-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_previewSessionDir);
            CopyDirectory(FiveOS.Services.RuntimeAssets.ViewerDir, _previewSessionDir);

            PreviewViewport.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "aipreview.local", _previewSessionDir, CoreWebView2HostResourceAccessKind.Allow);
            WebViewDialogs.Theme(PreviewViewport.CoreWebView2);
            PreviewViewport.CoreWebView2.WebMessageReceived += OnPreviewMessage;
            PreviewViewport.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            PreviewViewport.Source = new Uri("https://aipreview.local/viewer.html");

            // If a model was already generated this session, stage it now.
            if (_vm.HasLastOutput) ShowPreview(_vm.LastOutputPath);
        }
        catch (Exception ex)
        {
            // Preview is a nice-to-have; the static thumbnail in the generator
            // panel remains as the fallback if WebView2 won't come up.
            FiveOS.Services.FosLogger.Warn("aipreview", "preview viewer init failed", ex);
        }
    }

    private void OnPreviewMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!e.WebMessageAsJson.Contains("\"ready\"")) return;
            _previewReady = true;
            if (_pendingPreviewUrl != null)
            {
                var url = _pendingPreviewUrl;
                _pendingPreviewUrl = null;
                _ = PreviewViewport.CoreWebView2.ExecuteScriptAsync($"window.loadModel('{JsEscape(url)}')");
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>Stage the generated GLB into the preview host and load it.</summary>
    private void ShowPreview(string? modelPath)
    {
        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath)) return;
        if (_previewSessionDir == null) return;                 // viewer not up yet
        if (string.Equals(modelPath, _lastPreviewedPath, StringComparison.OrdinalIgnoreCase)) return;
        _lastPreviewedPath = modelPath;

        try
        {
            var modelDir = Path.Combine(_previewSessionDir, "model");
            Directory.CreateDirectory(modelDir);
            var fileName = Path.GetFileName(modelPath);
            var staged = Path.Combine(modelDir, fileName);
            File.Copy(modelPath, staged, overwrite: true);

            var url = $"https://aipreview.local/model/{Uri.EscapeDataString(fileName)}";

            // Reveal the viewer, hide the empty state.
            PreviewViewport.Visibility = Visibility.Visible;
            PreviewEmpty.Visibility = Visibility.Collapsed;

            if (_previewReady && PreviewViewport.CoreWebView2 != null)
                _ = PreviewViewport.CoreWebView2.ExecuteScriptAsync($"window.loadModel('{JsEscape(url)}')");
            else
                _pendingPreviewUrl = url;   // load once the viewer signals ready
        }
        catch (Exception ex)
        {
            FiveOS.Services.FosLogger.Warn("aipreview", "staging model for preview failed", ex);
        }
    }

    private void TeardownPreview()
    {
        try { PreviewViewport?.Dispose(); } catch { /* already gone */ }
        if (!string.IsNullOrEmpty(_previewSessionDir) && Directory.Exists(_previewSessionDir))
        {
            try { Directory.Delete(_previewSessionDir, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    private static string JsEscape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    // ─────────────── Footer actions ───────────────

    private void OnUseLatest(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.LastOutputPath)) return;
        ResultModelPath = _vm.LastOutputPath;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
