// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace FiveOS.Services;

public static class ThemeAccent
{
    public static readonly Color DefaultColor = Color.FromRgb(0x00, 0x78, 0xD4);

    public static readonly AccentPreset[] Presets =
    {
        new("Blue",    "#0078D4"),
        new("Indigo",  "#545A99"),
        new("Teal",    "#0D9488"),
        new("Green",   "#16A34A"),
        new("Orange",  "#EA580C"),
        new("Red",     "#DC2626"),
        new("Pink",    "#DB2777"),
        new("Gold",    "#C9A227"),
    };

    public static Color Current { get; private set; } = DefaultColor;

    public static event EventHandler? Changed;

    public readonly record struct AccentPreset(string Name, string Hex);

    private static readonly Dictionary<object, SolidColorBrush> SharedBrushes = new();

    public static Color LoadSaved()
    {
        var hex = UserSettings.LoadAccentColorHex();
        return TryParseHex(hex, out var c) ? c : DefaultColor;
    }

    public static void ApplyFromSettings() => Apply(LoadSaved(), persist: false);

    public static void Apply(Color accent, bool persist = true)
    {
        Current = accent;
        if (persist)
            UserSettings.SaveAccentColorHex(ToHex(accent));

        if (Application.Current is not null)
        {
            ApplicationAccentColorManager.Apply(accent, ApplicationTheme.Dark);
            ForcePinAllAccentResources(accent);
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ApplyHex(string hex, bool persist = true)
    {
        if (!TryParseHex(hex, out var c))
            c = DefaultColor;
        Apply(c, persist);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool TryParseHex(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length == 8) hex = hex[^6..];
        if (hex.Length != 6) return false;
        try
        {
            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);
            color = Color.FromRgb(r, g, b);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ForcePinAllAccentResources(Color accent)
    {
        var app = Application.Current;
        if (app?.Resources is null) return;

        var bright = Mix(accent, Colors.White, 0.22);
        var deep = Mix(accent, Colors.Black, 0.18);
        var soft = Color.FromArgb(0x33, accent.R, accent.G, accent.B);
        var soft90 = Color.FromArgb(229, accent.R, accent.G, accent.B);
        var soft80 = Color.FromArgb(204, accent.R, accent.G, accent.B);

        PinColor(app, "FiveOSAccentColor", accent);
        PinColor(app, "FiveOSAccentBrightColor", bright);
        PinColor(app, "FiveOSAccentDeepColor", deep);
        PinColor(app, "FiveOSAccentSoftColor", soft);
        PinColor(app, "FiveOSSelectionColor", accent);
        PinColor(app, "SystemAccentColor", accent);
        PinColor(app, "SystemAccentColorPrimary", bright);
        PinColor(app, "SystemAccentColorSecondary", accent);
        PinColor(app, "SystemAccentColorTertiary", deep);
        PinColor(app, "AccentFillColorDefault", accent);
        PinColor(app, "AccentFillColorSecondary", soft90);
        PinColor(app, "AccentFillColorTertiary", soft80);

        PinBrush(app, "FiveOSAccentBrush", accent);
        PinBrush(app, "FiveOSAccentBrightBrush", bright);
        PinBrush(app, "FiveOSAccentDeepBrush", deep);
        PinBrush(app, "FiveOSAccentSoftBrush", soft);
        PinBrush(app, "FiveOSSelectionBrush", accent);

        PinBrush(app, "SystemAccentColorBrush", accent);
        PinBrush(app, "SystemAccentColorPrimaryBrush", bright);
        PinBrush(app, "SystemAccentColorSecondaryBrush", accent);
        PinBrush(app, "SystemAccentColorTertiaryBrush", deep);
        PinBrush(app, "SystemAccentBrush", accent);
        PinBrush(app, "SystemFillColorAttentionBrush", accent);

        PinBrush(app, "AccentTextFillColorPrimaryBrush", bright);
        PinBrush(app, "AccentTextFillColorSecondaryBrush", accent);
        PinBrush(app, "AccentTextFillColorTertiaryBrush", deep);

        PinBrush(app, "AccentFillColorDefaultBrush", accent);
        PinBrush(app, "AccentFillColorPrimaryBrush", accent);
        PinBrush(app, "AccentFillColorSecondaryBrush", soft90);
        PinBrush(app, "AccentFillColorTertiaryBrush", soft80);
        PinBrush(app, "AccentFillColorSelectedTextBackgroundBrush", accent);
        PinBrush(app, "AccentControlElevationBorderBrush", deep);

        // WPF-UI's ToggleSwitch resolves its checked (on) track fill + stroke
        // through these keys, which are StaticResource aliases of the AccentFill*
        // brushes captured when the control dictionary first loaded. Re-pinning
        // only the AccentFill* brushes leaves an already-"on" switch painted with
        // the PREVIOUS accent in its rest state, while its hover/pressed states
        // pick up the new one — the mismatch where one toggle looks red and its
        // neighbours stay blue after a colour swap. Pin them directly so every
        // on-switch repaints uniformly.
        PinBrush(app, "ToggleSwitchFillOn", accent);
        PinBrush(app, "ToggleSwitchFillOnPointerOver", bright);
        PinBrush(app, "ToggleSwitchStrokeOn", accent);
        PinBrush(app, "ToggleSwitchStrokeOnPointerOver", bright);

        PinBrush(app, "AccentButtonBackground", accent);
        PinBrush(app, "AccentButtonBackgroundPointerOver", bright);
        PinBrush(app, "AccentButtonBackgroundPressed", deep);
        PinBrush(app, "AccentButtonBorderBrushPressed", Colors.Transparent);

        PinBrush(app, SystemColors.HighlightBrushKey, accent);
        PinBrush(app, SystemColors.HighlightTextBrushKey, Colors.White);
        PinBrush(app, SystemColors.HotTrackBrushKey, bright);
        PinBrush(app, SystemColors.GradientActiveCaptionBrushKey, accent);
        PinBrush(app, SystemColors.ActiveCaptionBrushKey, accent);
    }

    private static void PinColor(Application app, object key, Color color)
    {
        app.Resources[key] = color;
        WriteColorInTree(app.Resources, key, color);
    }

    private static void PinBrush(Application app, object key, Color color)
    {
        if (app.Resources[key] is SolidColorBrush { IsFrozen: false } live)
        {
            live.Color = color;
            SharedBrushes[key] = live;
            WriteBrushInTree(app.Resources, key, live);
            return;
        }

        var brush = SharedBrush(key, color);
        app.Resources[key] = brush;
        WriteBrushInTree(app.Resources, key, brush);
    }

    private static SolidColorBrush SharedBrush(object key, Color color)
    {
        if (SharedBrushes.TryGetValue(key, out var existing))
        {
            if (!existing.IsFrozen)
            {
                existing.Color = color;
                return existing;
            }
            SharedBrushes.Remove(key);
        }

        var brush = new SolidColorBrush(color);
        SharedBrushes[key] = brush;
        return brush;
    }

    private static void WriteColorInTree(ResourceDictionary dict, object key, Color color)
    {
        foreach (var md in dict.MergedDictionaries)
            WriteColorInTree(md, key, color);

        if (dict.Source is not null) return;
        if (!dict.Contains(key)) return;
        try { dict[key] = color; }
        catch (InvalidOperationException) { }
    }

    private static void WriteBrushInTree(ResourceDictionary dict, object key, SolidColorBrush brush)
    {
        foreach (var md in dict.MergedDictionaries)
            WriteBrushInTree(md, key, brush);

        if (dict.Source is not null) return;
        if (!dict.Contains(key)) return;
        try { dict[key] = brush; }
        catch (InvalidOperationException) { }
    }

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}
