// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Animation;

namespace FiveOS.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v != null) VersionText.Text = $"Version {v.Major}.{v.Minor}.{v.Build}";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var fadeIn = (Storyboard)Resources["FadeIn"];
        fadeIn.Begin(this);
    }

    /// <summary>
    /// Update the splash caption. The percent argument is retained for
    /// callsite compatibility — the bar was replaced by an indeterminate
    /// dot-matrix loader, so percent is now ignored beyond the 100% case
    /// which switches the loader to its "ready" style.
    /// </summary>
    public void SetProgress(double percent, string caption)
    {
        percent = Math.Clamp(percent, 0, 100);
        CaptionText.Text = caption;
        if (percent >= 100)
            Loader.LoaderStyle = Controls.DotMatrixLoaderStyle.CoreSpiral;
    }

    /// <summary>
    /// Close the splash immediately the moment init reports done — no
    /// minimum hold, no fade-out animation. The main window is already
    /// fully ready by the time this fires (App orchestrates it on
    /// MainWindow.ViewerReady), so any delay here just feels like lag.
    /// </summary>
    public void FinishAndClose(Action onClose)
    {
        onClose();
        Close();
    }
}
