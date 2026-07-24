// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Windows;
using Wpf.Ui.Controls;

namespace FiveOS.Views;

/// <summary>Pre-export settings for a Layers group: final pack name and
/// whether the output is a plain prop pack or a furniture pack (which adds
/// the nolag_properties catalog — the only supported housing system).</summary>
public partial class ExportPackDialog : FluentWindow
{
    public string PackName { get; private set; } = "";
    public bool EmitHousing { get; private set; }

    public ExportPackDialog(string groupName, int propCount)
    {
        InitializeComponent();
        NameBox.Text = groupName;
        CountText.Text = propCount == 1
            ? "1 prop exports into this pack."
            : $"{propCount} props export into this pack.";

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            CountText.Text = "Enter a pack name.";
            return;
        }
        PackName = name;
        EmitHousing = TypeFurniture.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
