// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Threading;
using System.Windows;

namespace FiveOS.Services;

/// <summary>
/// Windows file dialogs shown on a DEDICATED STA thread instead of the app's
/// UI thread. Measured on a real machine: the shell dialog opens in ~60ms from
/// a clean process but took 16+ SECONDS on this app's UI thread — the pump is
/// contended (multiple WebView2 hosts et al) and the dialog starves. Running
/// the dialog on its own thread with its own pump makes it instant; the owner
/// window is input-disabled meanwhile so the flow still behaves modally.
/// </summary>
internal static class StaFileDialogs
{
    /// <summary>Open-file picker. Returns the chosen path, or null on cancel
    /// or failure. <paramref name="configure"/> runs on the dialog thread.</summary>
    public static Task<string?> OpenAsync(
        Window? owner, System.Action<Microsoft.Win32.OpenFileDialog> configure)
        => RunAsync(owner, () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            configure(dlg);
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        });

    /// <summary>Save-file picker. Returns the chosen path, or null on cancel
    /// or failure. <paramref name="configure"/> runs on the dialog thread.</summary>
    public static Task<string?> SaveAsync(
        Window? owner, System.Action<Microsoft.Win32.SaveFileDialog> configure)
        => RunAsync(owner, () =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog();
            configure(dlg);
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        });

    /// <summary>Multi-select open picker. Returns the chosen paths, or null on
    /// cancel or failure. Sets Multiselect = true for you.</summary>
    public static Task<string[]?> OpenManyAsync(
        Window? owner, System.Action<Microsoft.Win32.OpenFileDialog> configure)
        => RunAsync<string[]>(owner, () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
            configure(dlg);
            return dlg.ShowDialog() == true ? dlg.FileNames : null;
        });

    /// <summary>Folder picker. Returns the chosen folder, or null on cancel
    /// or failure. <paramref name="configure"/> runs on the dialog thread.</summary>
    public static Task<string?> OpenFolderAsync(
        Window? owner, System.Action<Microsoft.Win32.OpenFolderDialog> configure)
        => RunAsync(owner, () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog();
            configure(dlg);
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        });

    /// <summary>Multi-select folder picker (Multiselect = true is set for you).</summary>
    public static Task<string[]?> OpenFoldersAsync(
        Window? owner, System.Action<Microsoft.Win32.OpenFolderDialog> configure)
        => RunAsync<string[]>(owner, () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Multiselect = true };
            configure(dlg);
            return dlg.ShowDialog() == true ? dlg.FolderNames : null;
        });

    private static Task<string?> RunAsync(Window? owner, System.Func<string?> show)
        => RunAsync<string>(owner, show);

    private static async Task<T?> RunAsync<T>(Window? owner, System.Func<T?> show) where T : class
    {
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.TrySetResult(show()); }
            catch (System.Exception ex)
            {
                FosLogger.Warn("dialog", "off-thread file dialog failed", ex);
                tcs.TrySetResult(null);
            }
        })
        {
            IsBackground = true,
        };
        t.SetApartmentState(ApartmentState.STA);

        // Emulate modality: input to the owner is off while the dialog is up.
        if (owner != null) owner.IsEnabled = false;
        try
        {
            t.Start();
            return await tcs.Task.ConfigureAwait(true);
        }
        finally
        {
            if (owner != null) owner.IsEnabled = true;
        }
    }
}
