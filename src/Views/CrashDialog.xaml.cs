// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using FiveOS.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace FiveOS.Views;

/// <summary>
/// Themed replacement for the crash MessageBox: shows a bold headline
/// and an optional explanation, then the full engine log in a
/// monospaced read-only TextBox the user can copy from or save to disk.
/// Used both for engine conversion failures and any other future "show
/// the user a multi-line log" surface.
/// </summary>
public partial class CrashDialog : FluentWindow
{
    /// <summary>The fully assembled log text — header line + engine log.</summary>
    private readonly string _fullLog;

    private CrashDialog(string headline, string summary, string fullLog, string suggestedFileName)
    {
        InitializeComponent();
        HeadlineText.Text = headline;
        SummaryText.Text = summary;
        if (string.IsNullOrWhiteSpace(summary))
            SummaryText.Visibility = Visibility.Collapsed;
        _fullLog = fullLog ?? "";
        LogTextBox.Text = _fullLog;
        SuggestedFileName = suggestedFileName;
        UpdateStats();
    }

    /// <summary>Default file name suggested in the Save dialog.</summary>
    public string SuggestedFileName { get; }

    /// <summary>
    /// Build a crash dialog for an engine conversion failure. Strips the
    /// internal <c>[ydr-writer]</c> tag so the user only ever sees FiveOS
    /// branding. The headline pulls from the localized error key when one
    /// matches a known failure pattern; otherwise it falls back to the
    /// raw error message.
    /// </summary>
    public static CrashDialog FromEngineFailure(string error, string log)
    {
        // Sanitize the engine log: strip embedded tool name so users see
        // FiveOS branding, not "ydr-writer".
        var sanitizedLog = System.Text.RegularExpressions.Regex.Replace(
            log ?? "", @"(?m)^\s*\[ydr-writer\]\s*", "");

        // Pick a friendlier headline + summary when we recognize the
        // failure mode. Falls back to the raw error otherwise.
        var (headline, summary) = ClassifyEngineError(error ?? "", sanitizedLog);

        var sb = new StringBuilder();
        sb.AppendLine($"FiveOS · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Error: {error}");
        sb.AppendLine();
        sb.AppendLine("--- engine log ---");
        sb.Append(sanitizedLog);

        var fileName = $"FiveOS-crash-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        return new CrashDialog(headline, summary, sb.ToString(), fileName);
    }

    /// <summary>
    /// Pattern-match the engine error/log to surface a friendlier
    /// headline + actionable hint when we recognize the failure. Order
    /// matters — most specific first.
    /// </summary>
    private static (string headline, string summary) ClassifyEngineError(string error, string log)
    {
        var loc = LocalizationService.Instance;
        var combined = error + "\n" + log;

        // AssimpNet missing-DLL pattern: shows up as ERROR_MOD_NOT_FOUND
        // when the native library can't be loaded for any reason.
        if (combined.Contains("Error loading unmanaged library") ||
            combined.Contains("0x8007007E") ||
            combined.Contains("specified module could not be found"))
        {
            return (loc["Crash.AssimpHeadline"], loc["Crash.AssimpSummary"]);
        }

        // Bad / unreadable input file.
        if (combined.Contains("import produced no meshes") ||
            combined.Contains("input not found"))
        {
            return (loc["Crash.ImportHeadline"], loc["Crash.ImportSummary"]);
        }

        // Generic engine exit-code failure.
        return (loc["Crash.GenericHeadline"], string.IsNullOrWhiteSpace(error)
            ? loc["Crash.GenericSummary"]
            : error);
    }

    private void UpdateStats()
    {
        var bytes = Encoding.UTF8.GetByteCount(_fullLog);
        var lines = _fullLog.Length == 0 ? 0 : _fullLog.Split('\n').Length;
        LogStatsText.Text = $"{lines} lines · {FormatBytes(bytes)}";
    }

    private static string FormatBytes(long n)
    {
        if (n < 1024) return $"{n} B";
        if (n < 1024 * 1024) return $"{n / 1024.0:0.#} KB";
        return $"{n / (1024.0 * 1024):0.##} MB";
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_fullLog);
        }
        catch
        {
            // Clipboard occasionally throws CLIPBRD_E_CANT_OPEN when
            // another process is holding it open. Retry once after a
            // short delay before giving up — better than crashing the
            // dialog the user is using to look at a crash.
            try { await Task.Delay(80); Clipboard.SetText(_fullLog); }
            catch { return; }
        }

        // Brief visual confirmation: swap the button label to "Copied"
        // for a moment so the user sees something happened.
        var original = CopyButtonText.Text;
        CopyButtonText.Text = LocalizationService.Instance["Crash.Copied"];
        await Task.Delay(1400);
        CopyButtonText.Text = original;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = LocalizationService.Instance["Crash.SaveLog"],
            FileName = SuggestedFileName,
            Filter = "Log files (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*",
            DefaultExt = ".log",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, _fullLog, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // The crash dialog is already on-screen, so its themed resources
            // are fine — surface the save failure in a themed dialog too.
            AppDialog.Show(
                $"{LocalizationService.Instance["Crash.SaveFailed"]}\n\n{ex.Message}",
                LocalizationService.Instance["Crash.Title"],
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning, this);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Convenience: build, parent on the given owner, and ShowDialog.
    /// </summary>
    public static void ShowEngineFailure(Window? owner, string error, string log)
    {
        var dlg = FromEngineFailure(error, log);
        if (owner != null) dlg.Owner = owner;
        dlg.ShowDialog();
    }
}
