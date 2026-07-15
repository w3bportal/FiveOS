// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.Linq;
using System.Windows;

namespace FiveOS.Views;

/// <summary>App-themed drop-in for <see cref="System.Windows.MessageBox"/>.
/// Same signature and <see cref="MessageBoxResult"/> return, so call sites just
/// swap <c>System.Windows.MessageBox.Show(...)</c> → <c>AppDialog.Show(...)</c>
/// (owner moves to the last argument). Every FiveOS popup routes through here so
/// they're all Fluent-themed instead of the grey Win32 / browser dialogs.</summary>
public static class AppDialog
{
    /// <summary>Show a themed dialog. Marshals to the UI thread and blocks until dismissed.</summary>
    public static MessageBoxResult Show(
        string message,
        string title = "FiveOS",
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        Window? owner = null)
    {
        var app = Application.Current;
        if (app == null) return MessageBoxResult.None;   // headless / shutting down

        MessageBoxResult Run()
        {
            var dlg = new ThemedDialog(message, title, buttons, icon);
            var o = owner ?? ActiveWindow();
            if (o != null && !ReferenceEquals(o, dlg) && o.IsLoaded && o.IsVisible)
                dlg.Owner = o;
            dlg.ShowDialog();
            return dlg.Result;
        }

        return app.Dispatcher.CheckAccess() ? Run() : app.Dispatcher.Invoke(Run);
    }

    private static Window? ActiveWindow()
    {
        var app = Application.Current;
        if (app == null) return null;
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
               ?? (app.MainWindow is { IsVisible: true } m ? m : null);
    }

    // ─── Convenience wrappers ────────────────────────────────────────────

    public static void Info(string message, string title = "FiveOS", Window? owner = null)
        => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information, owner);

    public static void Warn(string message, string title = "FiveOS", Window? owner = null)
        => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning, owner);

    public static void Error(string message, string title = "Something went wrong", Window? owner = null)
        => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error, owner);

    /// <summary>OK/Cancel confirm → true when the user accepts.</summary>
    public static bool Confirm(string message, string title = "Please confirm",
        MessageBoxImage icon = MessageBoxImage.Question, Window? owner = null)
        => Show(message, title, MessageBoxButton.OKCancel, icon, owner) == MessageBoxResult.OK;

    /// <summary>The one dedicated "you're offline" dialog. <paramref name="feature"/> is
    /// the thing that needs the network, e.g. "Importing from gta5-mods".</summary>
    public static void NoInternet(string feature = "This feature", Window? owner = null)
        => Show(
            $"{feature} needs an internet connection, and FiveOS can't reach the network right now.\n\n" +
            "Check your connection and try again — everything else in FiveOS works fully offline.",
            "No internet connection",
            MessageBoxButton.OK, MessageBoxImage.Warning, owner);

    /// <summary>True when <paramref name="ex"/> looks like a lost/absent connection
    /// (as opposed to a real server-side error). Lets callers show
    /// <see cref="NoInternet"/> instead of a raw exception string.</summary>
    public static bool IsOfflineError(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException) return true;
            if (e is System.Net.Http.HttpRequestException hre &&
                (hre.InnerException is System.Net.Sockets.SocketException ||
                 hre.HttpRequestError == System.Net.Http.HttpRequestError.NameResolutionError ||
                 hre.HttpRequestError == System.Net.Http.HttpRequestError.ConnectionError))
                return true;
            if (e is TaskCanceledException or OperationCanceledException) continue;
        }
        return false;
    }
}
