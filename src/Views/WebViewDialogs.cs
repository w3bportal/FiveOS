// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace FiveOS.Views;

/// <summary>Routes a WebView2's JS <c>alert</c> / <c>confirm</c> / <c>prompt</c>
/// (and beforeunload) through the app-themed <see cref="AppDialog"/> instead of
/// the default grey browser popup. Call <see cref="Theme"/> once the
/// CoreWebView2 is ready (right where the virtual-host mapping is set).</summary>
public static class WebViewDialogs
{
    public static void Theme(CoreWebView2 core)
    {
        core.ScriptDialogOpening += (_, e) =>
        {
            // Deferral so our modal themed dialog can complete before we tell
            // WebView2 the script dialog is resolved.
            var deferral = e.GetDeferral();
            try
            {
                switch (e.Kind)
                {
                    case CoreWebView2ScriptDialogKind.Confirm:
                    case CoreWebView2ScriptDialogKind.Beforeunload:
                        if (AppDialog.Show(e.Message, "FiveOS",
                                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                            e.Accept();
                        break;
                    case CoreWebView2ScriptDialogKind.Prompt:
                        // No themed text-entry popup — surface the message and accept the default.
                        AppDialog.Show(e.Message, "FiveOS", MessageBoxButton.OK, MessageBoxImage.Information);
                        e.Accept();
                        break;
                    default: // Alert
                        AppDialog.Show(e.Message, "FiveOS", MessageBoxButton.OK, MessageBoxImage.Information);
                        e.Accept();
                        break;
                }
            }
            finally { deferral.Complete(); }
        };
    }
}
