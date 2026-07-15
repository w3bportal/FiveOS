// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace FiveOS.Views;

/// <summary>
/// One-time first-run picker letting the user choose their UI complexity
/// tier (Beginner / Standard / Advanced). The chosen level (0/1/2) is
/// returned via <see cref="ChosenLevel"/>; the caller applies + persists it.
/// </summary>
public partial class LevelPickerWindow : FluentWindow
{
    /// <summary>0=Beginner, 1=Standard, 2=Advanced. Defaults to Beginner
    /// if the window is somehow closed without a pick.</summary>
    public int ChosenLevel { get; private set; }

    public LevelPickerWindow()
    {
        InitializeComponent();
    }

    private void OnPick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string tag && int.TryParse(tag, out var level))
            ChosenLevel = level;
        DialogResult = true;
        Close();
    }
}
