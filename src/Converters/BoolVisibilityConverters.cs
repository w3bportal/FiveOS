// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FiveOS.Converters;

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v != Visibility.Visible;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;
}

/// <summary>Visible when the bound value is non-null; collapsed when null.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Multi-binding converter that returns true when the first two bound
/// values are equal as strings (case-insensitive). Used by the workspace
/// tab strip to highlight the active document's tab.
/// </summary>
public sealed class StringEqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2) return false;
        var a = values[0]?.ToString();
        var b = values[1]?.ToString();
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => Array.Empty<object>();
}

/// <summary>Converts an absolute file path to a BitmapImage suitable for
/// an <c>&lt;Image Source="..."&gt;</c>. Returns null when the path is
/// empty or doesn't exist — bound Image controls render nothing in
/// that case, and a sibling SymbolIcon takes over via Visibility.</summary>
public sealed class PathToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;
        try
        {
            // CacheOption=OnLoad lets the file be deleted/replaced after
            // load; without it WPF holds a file lock.
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            // Optional ConverterParameter = max decode width (e.g. "96") so list
            // thumbnails don't keep full-res bitmaps in memory.
            if (parameter is string ps && int.TryParse(ps, out var w) && w > 0)
                img.DecodePixelWidth = w;
            else if (parameter is int iw && iw > 0)
                img.DecodePixelWidth = iw;
            img.UriSource = new Uri(path, UriKind.Absolute);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception ex)
        {
            FiveOS.Services.FosLogger.Warn("converter", $"image load '{path}'", ex);
            return null;
        }
    }

    // One-way converter — the source path / file-existence flag is the
    // owning property, not something the UI writes back. Returning
    // Binding.DoNothing is the WPF-idiomatic "no value" reply; throwing
    // NotImplementedException would crash the app if a future TwoWay
    // binding ever hit this code path by mistake.
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}

/// <summary>True when the bound <see cref="FiveOS.ViewModels.MaterialPreset"/>
/// equals the converter parameter (e.g. <c>ConverterParameter=Glass</c>).
/// Drives the IsChecked state on the per-row Material submenu so the
/// active preset gets the checkmark. One-way: the click handlers write
/// back through MaterialPreset directly, not through this converter.</summary>
public sealed class MaterialPresetIsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FiveOS.ViewModels.MaterialPreset preset) return false;
        if (parameter is not string name) return false;
        return string.Equals(preset.ToString(), name, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
