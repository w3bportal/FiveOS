// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.Windows.Data;
using System.Windows.Markup;
using FiveOS.Services;

namespace FiveOS.Markup;

/// <summary>
/// XAML markup extension: <c>{loc:Loc Some.Key}</c> resolves at parse-time
/// into a OneWay <see cref="Binding"/> against
/// <see cref="LocalizationService"/>'s indexer. When the user picks a
/// new language, the service raises <c>Item[]</c> and every binding
/// produced by this extension refreshes automatically — no XAML reload.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public LocExtension() { }

    public LocExtension(string key) { Key = key ?? string.Empty; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // Bind to LocalizationService.Instance[Key]. Square brackets in
        // the path tell the binding engine to use the indexer; the
        // service raises Item[] on language change, which invalidates
        // every such binding at once.
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
