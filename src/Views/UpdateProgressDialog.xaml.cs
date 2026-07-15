// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Windows;
using FiveOS.Services;
using Wpf.Ui.Controls;

namespace FiveOS.Views;

/// <summary>
/// Modal progress dialog that owns the update download. The host opens
/// this with <c>ShowDialog()</c>; on success it returns <c>true</c> and
/// the caller should immediately call
/// <see cref="System.Windows.Application.Shutdown()"/> so the swap
/// script (already spawned by <see cref="UpdateChecker"/>) can take over.
/// </summary>
public partial class UpdateProgressDialog : FluentWindow
{
    private readonly string _downloadUrl;
    private readonly string _versionLabel;
    private readonly string? _expectedSha256;
    private readonly CancellationTokenSource _cts = new();
    private bool _completed;

    public Exception? Failure { get; private set; }

    public UpdateProgressDialog(string downloadUrl, string versionLabel, string? expectedSha256 = null)
    {
        InitializeComponent();
        _downloadUrl = downloadUrl;
        _versionLabel = versionLabel;
        _expectedSha256 = expectedSha256;
        HeadlineText.Text = $"Downloading {versionLabel}...";
        Loaded += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        var progress = new Progress<UpdateChecker.DownloadProgress>(OnProgress);
        try
        {
            await UpdateChecker.DownloadAndInstallAsync(_downloadUrl, progress, _cts.Token, _expectedSha256);
            _completed = true;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            DialogResult = false;
            Close();
        }
        catch (Exception ex)
        {
            Failure = ex;
            DialogResult = false;
            Close();
        }
    }

    private void OnProgress(UpdateChecker.DownloadProgress p)
    {
        if (p.Fraction is double f)
        {
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = f;
            StatusText.Text = $"{FormatBytes(p.BytesReceived)} of {FormatBytes(p.TotalBytes!.Value)} ({f:P0})";
        }
        else
        {
            // Server didn't send Content-Length — show running total and
            // let the bar churn until the stream closes.
            DownloadProgress.IsIndeterminate = true;
            StatusText.Text = $"{FormatBytes(p.BytesReceived)} downloaded";
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_completed) return;
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Canceling...";
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _cts.Dispose();
    }

    private static string FormatBytes(long bytes)
    {
        const double KB = 1024, MB = KB * 1024, GB = MB * 1024;
        return bytes switch
        {
            >= (long)GB => $"{bytes / GB:0.00} GB",
            >= (long)MB => $"{bytes / MB:0.0} MB",
            >= (long)KB => $"{bytes / KB:0.0} KB",
            _ => $"{bytes} B",
        };
    }
}
