// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using FiveOS.Services;
using FiveOS.Services.AiProviders;
using FiveOS.ViewModels;

namespace FiveOS.Views;

public partial class ImageTo3DView : UserControl
{
    public ImageTo3DView()
    {
        InitializeComponent();
    }

    private ImageTo3DViewModel? Vm => DataContext as ImageTo3DViewModel;

    // ─────────────── Mode toggle ───────────────

    private void OnModeImage(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        Vm.ModeIndex = 0;
    }

    private void OnModeText(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        Vm.ModeIndex = 1;
    }

    // ─────────────── Generate ───────────────

    private async void OnGenerateFromImage(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var provider = Vm.SelectedProvider;
        if (!provider.SupportsImage) return;

        var token = SecretStore.Load(provider.TokenKey);
        if (string.IsNullOrEmpty(token))
        {
            NavigateToProviderSettings(provider);
            return;
        }

        var picker = new OpenFileDialog
        {
            Title = $"Pick an image to send to {provider.DisplayName}",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|All files (*.*)|*.*",
            Multiselect = false,
        };
        if (picker.ShowDialog() != true) return;

        var stem = Path.GetFileNameWithoutExtension(picker.FileName);
        var label = $"{stem}_{provider.Id}.glb";
        await RunGenerationAsync(provider,
            (token, target, progress, cancel) =>
                provider.GenerateFromImageAsync(token, picker.FileName, target, progress, cancel),
            label, token!, texturable: false);   // image results are already textured
    }

    private async void OnGenerateFromText(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var provider = Vm.SelectedProvider;
        if (!provider.SupportsText) return;

        var prompt = (Vm.TextPrompt ?? "").Trim();
        if (prompt.Length == 0)
        {
            AppDialog.Show(
                "Enter a prompt first.", provider.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Information, Window.GetWindow(this));
            return;
        }

        var token = SecretStore.Load(provider.TokenKey);
        if (string.IsNullOrEmpty(token))
        {
            NavigateToProviderSettings(provider);
            return;
        }

        var label = $"prompt_{Sanitize(prompt)}_{provider.Id}.glb";
        await RunGenerationAsync(provider,
            (token, target, progress, cancel) =>
                provider.GenerateFromTextAsync(token, prompt, target, progress, cancel),
            label, token!, texturable: true);   // text previews can be AI-textured
    }

    // ─────────────── Texture with AI ───────────────

    private async void OnTextureWithAi(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var provider = Vm.SelectedProvider;
        if (!provider.SupportsTexturing) return;

        var token = SecretStore.Load(provider.TokenKey);
        if (string.IsNullOrEmpty(token))
        {
            NavigateToProviderSettings(provider);
            return;
        }

        var baseName = Path.GetFileNameWithoutExtension(Vm.LastOutputPath ?? "model");
        var label = $"{baseName}_textured.glb";
        await RunGenerationAsync(provider,
            (t, target, progress, cancel) => provider.TextureLastAsync(t, target, progress, cancel),
            label, token!, texturable: false);   // once textured, no further pass
    }

    private async Task RunGenerationAsync(
        IAiProvider provider,
        Func<string, string, IProgress<GenerationStep>, CancellationToken, Task> generate,
        string outputLabel,
        string token,
        bool texturable)
    {
        if (Vm == null) return;
        var owner = Window.GetWindow(this);

        if (FiveOS.Services.Net.LikelyOffline())
        { AppDialog.NoInternet($"AI generation with {provider.DisplayName}", owner); return; }

        var outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "FiveOS_meshes", "generated");
        var outPath = Path.Combine(outDir, outputLabel);

        try
        {
            Vm.IsGenerating = true;
            Vm.GenerationProgress = 0;
            Vm.PreviewImageUrl = null;   // drop any previous run's preview
            Vm.GenerationStatus = $"Starting {provider.DisplayName}…";

            var progress = new Progress<GenerationStep>(s =>
            {
                Vm.GenerationStatus = s.Status;
                Vm.GenerationProgress = s.Fraction;
                if (!string.IsNullOrEmpty(s.PreviewImageUrl))
                    Vm.PreviewImageUrl = s.PreviewImageUrl;
            });

            await generate(token, outPath, progress, CancellationToken.None);

            Vm.LastOutputPath = outPath;
            Vm.LastOutputTexturable = texturable;
            Vm.GenerationStatus = $"✓ Saved {Path.GetFileName(outPath)} — open it in 3D Model to convert.";
        }
        catch (NotSupportedException nse)
        {
            AppDialog.Show(nse.Message, provider.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Information, owner);
            Vm.GenerationStatus = $"✗ {nse.Message}";
        }
        catch (Exception ex)
        {
            AppDialog.Show(
                $"{provider.DisplayName} failed:\n\n{ex.Message}",
                provider.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Error, owner);
            Vm.GenerationStatus = $"✗ {ex.Message}";
        }
        finally
        {
            Vm.IsGenerating = false;
        }
    }

    private static string Sanitize(string s)
    {
        var slug = new string(s.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        if (slug.Length > 24) slug = slug[..24];
        return slug.Trim('_');
    }

    // ─────────────── Settings nav ───────────────

    private void OnEditProviderKey(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        NavigateToProviderSettings(Vm.SelectedProvider);
    }

    private void NavigateToProviderSettings(IAiProvider provider)
    {
        var main = Window.GetWindow(this) as MainWindow;
        if (main == null) return;
        main.NavigateToAiProviderSettings(provider.Id);
    }

    private void OnRevealOutput(object sender, RoutedEventArgs e)
    {
        var path = Vm?.LastOutputPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }
}
