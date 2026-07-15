// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace FiveOS.Services;

/// <summary>
/// App-wide UI strings, loaded from JSON locale files embedded as
/// <c>FiveOS.Locales.{code}.json</c>. Bound to XAML via the
/// <c>{loc:Loc Key}</c> markup extension; switching language at
/// runtime fires <c>Item[]</c> so every binding refreshes in place.
///
/// English is the reference: any key missing in the active locale
/// falls back to English, then to the key itself (so untranslated
/// strings still render something readable instead of going blank).
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    /// <summary>BCP-47-ish codes for locale files we ship.</summary>
    public static IReadOnlyList<LocaleInfo> Available { get; } = new[]
    {
        new LocaleInfo("en",    "English"),
        new LocaleInfo("pt-BR", "Português (Brasil)"),
        new LocaleInfo("de",    "Deutsch"),
        new LocaleInfo("fr",    "Français"),
        new LocaleInfo("es",    "Español"),
        new LocaleInfo("tr",    "Türkçe"),
        new LocaleInfo("pl",    "Polski"),
        new LocaleInfo("nl",    "Nederlands"),
        new LocaleInfo("it",    "Italiano"),
        new LocaleInfo("ru",    "Русский"),
    };

    private readonly Dictionary<string, string> _english = new();
    private Dictionary<string, string> _active = new();

    public string CurrentLanguage { get; private set; } = "en";

    private LocalizationService()
    {
        _english = LoadLocale("en") ?? new Dictionary<string, string>();
        _active = _english;
    }

    /// <summary>Indexer used by XAML bindings — empty path means "all
    /// keys may have changed", so notifying <c>Item[]</c> on language
    /// switch fans out to every {loc:Loc} binding in the tree.</summary>
    public string this[string key]
    {
        get
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            if (_active.TryGetValue(key, out var v)) return v;
            if (_english.TryGetValue(key, out var en)) return en;
            return key;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Pick the best supported locale for the current OS UI culture.
    /// Falls back to English when no JSON file matches.
    /// </summary>
    public static string ResolveDefaultLanguage()
    {
        string code;
        try { code = CultureInfo.CurrentUICulture.Name; }
        catch { return "en"; }

        if (string.IsNullOrEmpty(code)) return "en";

        // Exact match wins (e.g. "pt-BR").
        foreach (var loc in Available)
        {
            if (string.Equals(loc.Code, code, System.StringComparison.OrdinalIgnoreCase))
                return loc.Code;
        }

        // Two-letter family match (e.g. "pt" → pt-BR, "es-MX" → es).
        var twoLetter = code.Split('-', '_')[0];
        if (string.Equals(twoLetter, "pt", System.StringComparison.OrdinalIgnoreCase))
            return "pt-BR";

        foreach (var loc in Available)
        {
            var locTwo = loc.Code.Split('-', '_')[0];
            if (string.Equals(locTwo, twoLetter, System.StringComparison.OrdinalIgnoreCase))
                return loc.Code;
        }

        return "en";
    }

    public void SetLanguage(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) code = "en";

        // Reject codes we don't ship — silently fall back to English so
        // a stale setting never bricks the UI.
        bool known = false;
        foreach (var loc in Available)
        {
            if (string.Equals(loc.Code, code, System.StringComparison.OrdinalIgnoreCase))
            {
                code = loc.Code;
                known = true;
                break;
            }
        }
        if (!known) code = "en";

        if (string.Equals(code, CurrentLanguage, System.StringComparison.OrdinalIgnoreCase))
            return;

        var loaded = code == "en" ? _english : LoadLocale(code);
        _active = loaded ?? _english;
        CurrentLanguage = code;

        // Empty path == "any indexer entry may have changed". This is
        // the WPF idiom that flushes every Binding pointing at [Key].
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    private static Dictionary<string, string>? LoadLocale(string code)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = $"FiveOS.Locales.{code}.json";
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch
        {
            return null;
        }
    }

    public sealed record LocaleInfo(string Code, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}
