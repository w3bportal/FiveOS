// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Windows;
using Wpf.Ui.Controls;

namespace FiveOS.Views;

/// <summary>Small themed input dialog for renaming a file or a car. For a file
/// it pre-selects the name part (not the extension), like Explorer's F2.</summary>
public partial class RenameDialog : FluentWindow
{
    public string ResultName { get; private set; } = "";

    public RenameDialog(string title, string prompt, string current, bool isFile, string? hint = null)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        NameBox.Text = current;
        if (hint != null) HintText.Text = hint;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            // Select the editable stem so the extension isn't clobbered by typing.
            var selLen = isFile ? current.Length - Path.GetExtension(current).Length : current.Length;
            NameBox.Select(0, System.Math.Max(0, selLen));
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var v = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(v)) { HintText.Text = "Enter a name."; return; }
        ResultName = v;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
