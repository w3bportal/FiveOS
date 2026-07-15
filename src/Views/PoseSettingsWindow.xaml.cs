// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Windows;
using FiveOS.ViewModels;

namespace FiveOS.Views;

/// <summary>Body calibration / secondary motion / joint markers / onion skin,
/// lifted out of the old Inspector rail into a gear-button dialog.
///
/// It shares the <see cref="PoseToEmoteViewModel"/> instance with the pose
/// workspace rather than owning a copy: every control here binds straight to
/// VM properties, and PoseToEmoteView pushes those down to the WebView2 from
/// its own PropertyChanged subscription. That's what keeps the preview live
/// while this is open — nothing is applied on close, so there is deliberately
/// no OK/Cancel, just Close.</summary>
public partial class PoseSettingsWindow
{
    public PoseSettingsWindow(PoseToEmoteViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
