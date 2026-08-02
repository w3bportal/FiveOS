// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Windows;
using FiveOS.Services;

namespace FiveOS.Views;

/// <summary>
/// Names an emote on its way into the pack queue.
///
/// The name is asked for UP FRONT rather than edited afterwards because the
/// bake stamps it into the .ycd itself (the clip hash TaskPlayAnim resolves).
/// Once those bytes exist the clip name is fixed — see EmotePackEntry.ClipName.
/// The in-game command and the menu label stay editable in the pack panel;
/// they only ever appear as Lua strings.
/// </summary>
public partial class AddToPackDialog
{
    private readonly EmotePackSession _pack;

    /// <summary>Sanitised emote name — the clip name AND the initial command
    /// name. Empty until the user confirms.</summary>
    public string EmoteName { get; private set; } = "";

    /// <summary>Menu label as typed.</summary>
    public string EmoteLabel { get; private set; } = "";

    public AddToPackDialog(EmotePackSession pack, string suggestedName, string suggestedLabel)
    {
        InitializeComponent();
        _pack = pack;

        NameBox.Text = suggestedName ?? "";
        LabelBox.Text = suggestedLabel ?? "";
        UpdateHint();

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateHint();

    /// <summary>Live echo of what the name actually becomes — the sanitizer
    /// strips punctuation and folds spaces, and seeing that before clicking
    /// Add beats discovering it in the pack list afterwards.</summary>
    private void UpdateHint()
    {
        if (NameHint is null || OkButton is null) return;

        var clean = EmotePackSession.Sanitize(NameBox.Text);
        if (string.IsNullOrEmpty(clean))
        {
            // Empty box on open is the normal case for the built-in presets —
            // ask for a name rather than scolding about one they never typed.
            NameHint.Text = string.IsNullOrWhiteSpace(NameBox.Text)
                ? "Type a short name — it becomes the command players type."
                : "Needs at least one letter or number.";
            OkButton.IsEnabled = false;
            return;
        }

        if (_pack.IsCommandTaken(clean))
        {
            NameHint.Text = $"“{clean}” is already in this pack — pick another name.";
            OkButton.IsEnabled = false;
            return;
        }

        NameHint.Text = $"Plays in game as /{clean}";
        OkButton.IsEnabled = true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var clean = EmotePackSession.Sanitize(NameBox.Text);
        if (string.IsNullOrEmpty(clean) || _pack.IsCommandTaken(clean))
        {
            UpdateHint();
            return;
        }

        EmoteName = clean;
        var label = LabelBox.Text?.Trim() ?? "";
        EmoteLabel = string.IsNullOrEmpty(label) ? Humanize(clean) : label;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private static string Humanize(string slug)
    {
        var words = slug.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
            words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
        return words.Length == 0 ? "Emote" : string.Join(' ', words);
    }
}
