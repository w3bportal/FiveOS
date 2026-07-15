// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

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

/// <summary>
/// Multi-binding converter that returns true when the first two bound
/// values are equal as strings (case-insensitive). Used by the rail's
/// per-plugin row to tint itself when its id matches ActivePluginId.
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

/// <summary>True when the bound value is a non-empty string pointing
/// at an existing file. Returns bool by default; if the binding target
/// is a Visibility property, returns Visible/Collapsed instead.</summary>
public sealed class FileExistsToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool exists = value is string s && !string.IsNullOrWhiteSpace(s) && System.IO.File.Exists(s);
        if (targetType == typeof(Visibility))
            return exists ? Visibility.Visible : Visibility.Collapsed;
        return exists;
    }

    // One-way converter — the source path / file-existence flag is the
    // owning property, not something the UI writes back. Returning
    // Binding.DoNothing is the WPF-idiomatic "no value" reply; throwing
    // NotImplementedException would crash the app if a future TwoWay
    // binding ever hit this code path by mistake.
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}

/// <summary>Inverse of <see cref="FileExistsToBoolConverter"/> — true /
/// Visible when the path is empty or doesn't exist. Used to show the
/// fallback SymbolIcon when a plugin lacks a custom icon.</summary>
public sealed class FileMissingToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool exists = value is string s && !string.IsNullOrWhiteSpace(s) && System.IO.File.Exists(s);
        return exists ? Visibility.Collapsed : Visibility.Visible;
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

/// <summary>
/// Parses a "#rrggbb" / "#aarrggbb" string to a <see cref="Color"/>. Returns
/// Colors.Transparent for null/invalid input — callers should pair the
/// gradient with a Visibility-bound toggle so the transparent fallback
/// never shows.
/// </summary>
public sealed class HexToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            try { return (Color)ColorConverter.ConvertFromString(s); }
            catch { /* fall through */ }
        }
        return Colors.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Color c ? c.ToString() : "#00000000";
}
