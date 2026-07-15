// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using FiveOS.Services.Sims;

namespace FiveOS.Views;

public partial class SimsClipPickerWindow
{
    public sealed record Row(int Index, string Display);

    public int? SelectedIndex { get; private set; }

    public SimsClipPickerWindow(IReadOnlyList<SimsClipDecoder.ClipInfo> clips)
    {
        InitializeComponent();
        ClipList.ItemsSource = clips.Select(c =>
        {
            var dur = c.DurationSeconds > 0.01f ? $" ({c.DurationSeconds:0.##}s)" : "";
            return new Row(c.Index, $"{c.Name}{dur}");
        }).ToList();
        if (ClipList.Items.Count > 0)
            ClipList.SelectedIndex = 0;
    }

    private void OnImport(object sender, RoutedEventArgs e) => Accept();
    private void OnCancel(object sender, RoutedEventArgs e) { SelectedIndex = null; DialogResult = false; }
    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (ClipList.SelectedItem is Row row)
        {
            SelectedIndex = row.Index;
            DialogResult = true;
        }
    }
}
