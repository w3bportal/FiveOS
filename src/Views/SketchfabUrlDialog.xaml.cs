// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Windows;
using FiveOS.Services;
using Wpf.Ui.Controls;

namespace FiveOS.Views;

/// <summary>
/// "Import from Sketchfab" dialog. Validates the pasted URL against the
/// Sketchfab API, shows the model's name/author/license/size, then
/// downloads the glTF archive and exposes the local path of the entry
/// .gltf via <see cref="ResultModelPath"/>.
/// </summary>
public partial class SketchfabUrlDialog : FluentWindow
{
    /// <summary>Absolute path to the unpacked model entrypoint, set on success.</summary>
    public string? ResultModelPath { get; private set; }

    private readonly string _token;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public SketchfabUrlDialog(string token, string? initialUrl = null)
    {
        InitializeComponent();
        _token = token;
        StatusText.Text = "Paste a URL and click Load.";
        if (!string.IsNullOrWhiteSpace(initialUrl))
        {
            UrlBox.Text = initialUrl;
            // Auto-trigger Load once the dialog is shown so the user doesn't
            // have to click again — they already kicked off the import from
            // the SOURCE sidebar.
            Loaded += (_, _) => OnLoad(this, new RoutedEventArgs());
        }
    }

    private async void OnLoad(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var url = UrlBox.Text?.Trim() ?? "";
        var uid = SketchfabClient.ParseUid(url);
        if (uid == null)
        {
            StatusText.Text = "Couldn't find a model UID in that URL.";
            return;
        }

        _busy = true;
        LoadButton.IsEnabled = false;
        StatusText.Text = "Looking up model...";
        InfoCard.Visibility = Visibility.Collapsed;
        DownloadProgress.Visibility = Visibility.Collapsed;
        DownloadProgress.Value = 0;

        _cts = new CancellationTokenSource();
        try
        {
            using var client = new SketchfabClient(_token);

            // Step 1: validate + show metadata.
            var info = await client.GetInfoAsync(uid, _cts.Token);
            InfoName.Text = info.Name;
            InfoAuthor.Text = "by " + info.Author;
            InfoLicense.Text = info.LicenseLabel + " — make sure you comply with attribution / non-commercial terms.";
            InfoSize.Text = info.FileSizeBytes is long b
                ? $"glTF: {FormatBytes(b)}"
                : "";
            ApplyGeometryQuality(info);
            InfoCard.Visibility = Visibility.Visible;

            if (!info.IsDownloadable)
            {
                StatusText.Text = "This model is viewable-only on Sketchfab. The author hasn't enabled downloads.";
                return;
            }

            // Step 2: download.
            StatusText.Text = "Downloading glTF archive...";
            DownloadProgress.Visibility = Visibility.Visible;

            var stagingDir = Path.Combine(
                Path.GetTempPath(), "FiveOS", "sketchfab-" + uid[..8]);
            Directory.CreateDirectory(stagingDir);

            var progress = new Progress<double>(p => DownloadProgress.Value = p);
            var modelPath = await client.DownloadGlbAsync(uid, stagingDir, progress, _cts.Token);

            ResultModelPath = modelPath;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed: " + ex.Message;
        }
        finally
        {
            _busy = false;
            LoadButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _cts?.Cancel();
            return;
        }
        DialogResult = false;
        Close();
    }

    private static string FormatBytes(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:F1} KB";
        return $"{b / (1024.0 * 1024):F1} MB";
    }

    /// <summary>Drive the geometry pill + warning row off the model's
    /// triangle count. Bands come from <see cref="SketchfabClient.ClassifyTriangles"/>.
    /// Hidden entirely when the API didn't surface counts.</summary>
    private void ApplyGeometryQuality(SketchfabClient.ModelInfo info)
    {
        var hasGeom = info.FaceCount.HasValue || info.VertexCount.HasValue;
        if (!hasGeom)
        {
            InfoGeometry.Text = "";
            QualityPill.Visibility = Visibility.Collapsed;
            QualityWarning.Visibility = Visibility.Collapsed;
            return;
        }

        var verts = info.VertexCount ?? 0;
        var tris  = info.FaceCount ?? 0;
        InfoGeometry.Text = $"{tris:N0} tris · {verts:N0} verts";

        var quality = SketchfabClient.ClassifyTriangles(tris);
        var (label, fill, fg, warning) = quality switch
        {
            SketchfabClient.GeometryQuality.Excellent =>
                ("Optimized", "#1F4CAF50", "#7BD489", (string?)null),
            SketchfabClient.GeometryQuality.Good =>
                ("Good", "#1F4CAF50", "#7BD489", (string?)null),
            SketchfabClient.GeometryQuality.Heavy =>
                ("Heavy", "#3DFFA000", "#FFC857",
                 "Heavy mesh — fine for hero props. After import, the 3D Model tab will offer a one-click auto-optimize."),
            _ =>
                ("Too dense", "#3DE53935", "#FF7060",
                 "This mesh will likely fail FiveM streaming as-is. After import, hit Auto-optimize in the 3D Model tab — it decimates to FiveM-friendly tri count in one click."),
        };
        QualityText.Text = label;
        QualityText.Foreground = (System.Windows.Media.Brush)
            new System.Windows.Media.BrushConverter().ConvertFrom(fg)!;
        QualityPill.Background = (System.Windows.Media.Brush)
            new System.Windows.Media.BrushConverter().ConvertFrom(fill)!;
        QualityPill.Visibility = Visibility.Visible;

        if (warning is not null)
        {
            QualityWarning.Text = warning;
            QualityWarning.Foreground = (System.Windows.Media.Brush)
                new System.Windows.Media.BrushConverter().ConvertFrom(fg)!;
            QualityWarning.Visibility = Visibility.Visible;
        }
        else
        {
            QualityWarning.Visibility = Visibility.Collapsed;
        }
    }
}
