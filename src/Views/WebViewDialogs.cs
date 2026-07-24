// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace FiveOS.Views;

/// <summary>Routes a WebView2's JS <c>alert</c> / <c>confirm</c> / <c>prompt</c>
/// (and beforeunload) through the app-themed <see cref="AppDialog"/> instead of
/// the default grey browser popup. Call <see cref="Theme"/> once the
/// CoreWebView2 is ready (right where the virtual-host mapping is set).</summary>
public static class WebViewDialogs
{
    private static readonly ConditionalWeakTable<CoreWebView2, object> Themed = new();
    private static readonly object Marker = new();

    public static void Theme(CoreWebView2 core)
    {
        // Idempotent — re-init used to stack ScriptDialogOpening handlers.
        if (!Themed.TryAdd(core, Marker))
            return;

        core.ScriptDialogOpening += OnScriptDialogOpening;
    }

    private static void OnScriptDialogOpening(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
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
                    AppDialog.Show(e.Message, "FiveOS", MessageBoxButton.OK, MessageBoxImage.Information);
                    e.Accept();
                    break;
                default:
                    AppDialog.Show(e.Message, "FiveOS", MessageBoxButton.OK, MessageBoxImage.Information);
                    e.Accept();
                    break;
            }
        }
        finally { deferral.Complete(); }
    }
}
