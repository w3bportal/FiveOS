// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace FiveOS.Views.Controls;

public enum DotMatrixLoaderStyle
{
    PulseLadder,
    SoundBars,
    CoreSpiral,
    HelixGlow,
    BlockDrop,
}

/// <summary>
/// 5x5 dot-matrix loader. Patterns are encoded as a list of binary frames
/// (one bool[5,5] per frame). Each dot gets a per-keyframe Opacity animation
/// driven by a single shared Storyboard, so all 25 dots stay in lockstep.
/// Discrete keyframes give the crisp on/off pixel-art look that the source
/// loaders at dotmatrix.zzzzshawn.cloud use.
/// </summary>
public partial class DotMatrixLoader : UserControl
{
    private const int Grid = 5;
    private static readonly Duration FrameDuration = new(TimeSpan.FromMilliseconds(80));
    private const double DimOpacity = 0.12;
    private const double LitOpacity = 1.0;

    private readonly Ellipse[,] _dots = new Ellipse[Grid, Grid];
    private Storyboard? _storyboard;

    public DotMatrixLoader()
    {
        InitializeComponent();
        BuildGrid();
        Loaded += (_, _) => RebuildAndStart();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += OnVisibleChanged;
    }

    public static readonly DependencyProperty LoaderStyleProperty = DependencyProperty.Register(
        nameof(LoaderStyle), typeof(DotMatrixLoaderStyle), typeof(DotMatrixLoader),
        new PropertyMetadata(DotMatrixLoaderStyle.PulseLadder, OnVisualPropertyChanged));

    public DotMatrixLoaderStyle LoaderStyle
    {
        get => (DotMatrixLoaderStyle)GetValue(LoaderStyleProperty);
        set => SetValue(LoaderStyleProperty, value);
    }

    public static readonly DependencyProperty DotColorProperty = DependencyProperty.Register(
        nameof(DotColor), typeof(Brush), typeof(DotMatrixLoader),
        new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)), OnColorChanged));

    public Brush DotColor
    {
        get => (Brush)GetValue(DotColorProperty);
        set => SetValue(DotColorProperty, value);
    }

    public static readonly DependencyProperty DotSizeProperty = DependencyProperty.Register(
        nameof(DotSize), typeof(double), typeof(DotMatrixLoader),
        new PropertyMetadata(8.0, OnVisualPropertyChanged));

    public double DotSize
    {
        get => (double)GetValue(DotSizeProperty);
        set => SetValue(DotSizeProperty, value);
    }

    public static readonly DependencyProperty DotSpacingProperty = DependencyProperty.Register(
        nameof(DotSpacing), typeof(double), typeof(DotMatrixLoader),
        new PropertyMetadata(4.0, OnVisualPropertyChanged));

    public double DotSpacing
    {
        get => (double)GetValue(DotSpacingProperty);
        set => SetValue(DotSpacingProperty, value);
    }

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(DotMatrixLoader),
        new PropertyMetadata(true, OnVisualPropertyChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DotMatrixLoader self) self.RebuildAndStart();
    }

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DotMatrixLoader self) self.ApplyDotColor();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && IsActive) RebuildAndStart();
        else Stop();
    }

    private void BuildGrid()
    {
        DotsContainer.Children.Clear();
        DotsContainer.RowDefinitions.Clear();
        DotsContainer.ColumnDefinitions.Clear();
        for (int i = 0; i < Grid; i++)
        {
            DotsContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            DotsContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (int r = 0; r < Grid; r++)
        for (int c = 0; c < Grid; c++)
        {
            var dot = new Ellipse
            {
                Width = DotSize,
                Height = DotSize,
                Fill = DotColor,
                Opacity = DimOpacity,
                Margin = new Thickness(DotSpacing / 2),
            };
            System.Windows.Controls.Grid.SetRow(dot, r);
            System.Windows.Controls.Grid.SetColumn(dot, c);
            DotsContainer.Children.Add(dot);
            _dots[r, c] = dot;
        }
    }

    private void ApplyDotColor()
    {
        for (int r = 0; r < Grid; r++)
        for (int c = 0; c < Grid; c++)
            if (_dots[r, c] != null) _dots[r, c].Fill = DotColor;
    }

    private void RebuildAndStart()
    {
        Stop();
        BuildGrid();
        if (!IsActive || !IsVisible) return;

        var frames = BuildFrames(LoaderStyle);
        if (frames.Count == 0) return;

        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var totalDuration = TimeSpan.FromMilliseconds(FrameDuration.TimeSpan.TotalMilliseconds * frames.Count);

        for (int r = 0; r < Grid; r++)
        for (int c = 0; c < Grid; c++)
        {
            var anim = new DoubleAnimationUsingKeyFrames { Duration = totalDuration };
            for (int f = 0; f < frames.Count; f++)
            {
                anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(
                    frames[f][r, c] ? LitOpacity : DimOpacity,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(FrameDuration.TimeSpan.TotalMilliseconds * f))));
            }
            Storyboard.SetTarget(anim, _dots[r, c]);
            Storyboard.SetTargetProperty(anim, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(anim);
        }

        _storyboard = storyboard;
        storyboard.Begin(this, true);
    }

    private void Stop()
    {
        _storyboard?.Stop(this);
        _storyboard = null;
    }

    private static List<bool[,]> BuildFrames(DotMatrixLoaderStyle style) => style switch
    {
        DotMatrixLoaderStyle.PulseLadder => PulseLadder(),
        DotMatrixLoaderStyle.SoundBars => SoundBars(),
        DotMatrixLoaderStyle.CoreSpiral => CoreSpiral(),
        DotMatrixLoaderStyle.HelixGlow => HelixGlow(),
        DotMatrixLoaderStyle.BlockDrop => BlockDrop(),
        _ => new List<bool[,]>(),
    };

    /// <summary>
    /// Two vertical rails (cols 1 and 3) stay lit; a "rung" of 3 horizontal
    /// dots travels top→bottom→top, like climbing a ladder.
    /// </summary>
    private static List<bool[,]> PulseLadder()
    {
        var frames = new List<bool[,]>();
        // 5 rows down, then 5 rows up → 10 rung positions, plus the rung
        // hangs on each row for 2 frames so motion reads at 80ms/frame.
        int[] rungRows = { 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 3, 3, 2, 2, 1, 1 };
        foreach (var rr in rungRows)
        {
            var f = new bool[Grid, Grid];
            for (int r = 0; r < Grid; r++) { f[r, 1] = true; f[r, 3] = true; }
            f[rr, 1] = true; f[rr, 2] = true; f[rr, 3] = true;
            frames.Add(f);
        }
        return frames;
    }

    /// <summary>
    /// 5 column bars of varying height, each phase-shifted to give the
    /// audio-equalizer look. Heights bottom-anchored.
    /// </summary>
    private static List<bool[,]> SoundBars()
    {
        var frames = new List<bool[,]>();
        const int frameCount = 20;
        for (int fi = 0; fi < frameCount; fi++)
        {
            var f = new bool[Grid, Grid];
            double t = (double)fi / frameCount;
            for (int c = 0; c < Grid; c++)
            {
                double phase = c * 0.6;
                double h = 3.0 + 2.0 * Math.Sin(2 * Math.PI * t + phase);
                int height = Math.Clamp((int)Math.Round(h), 1, 5);
                for (int r = Grid - height; r < Grid; r++) f[r, c] = true;
            }
            frames.Add(f);
        }
        return frames;
    }

    /// <summary>
    /// Lights dots in spiral order from the center outward, with a 3-dot
    /// trail. After the spiral fills, all dots flash on for 2 frames, then
    /// reset.
    /// </summary>
    private static List<bool[,]> CoreSpiral()
    {
        var spiral = new (int r, int c)[]
        {
            (2,2), (2,3), (1,3), (1,2), (1,1), (2,1), (3,1), (3,2), (3,3),
            (3,4), (2,4), (1,4), (0,4), (0,3), (0,2), (0,1), (0,0),
            (1,0), (2,0), (3,0), (4,0), (4,1), (4,2), (4,3), (4,4),
        };
        var frames = new List<bool[,]>();
        const int trail = 4;
        for (int i = 0; i < spiral.Length; i++)
        {
            var f = new bool[Grid, Grid];
            for (int t = 0; t < trail && i - t >= 0; t++)
            {
                var p = spiral[i - t];
                f[p.r, p.c] = true;
            }
            frames.Add(f);
        }
        // Brief full flash, then 2 dim frames before the spiral restarts.
        var allOn = new bool[Grid, Grid];
        for (int r = 0; r < Grid; r++) for (int c = 0; c < Grid; c++) allOn[r, c] = true;
        frames.Add(allOn);
        frames.Add(allOn);
        frames.Add(new bool[Grid, Grid]);
        frames.Add(new bool[Grid, Grid]);
        return frames;
    }

    /// <summary>
    /// Two sine waves crossing across the 5 columns — one rising, one
    /// falling. Reads as a DNA helix.
    /// </summary>
    private static List<bool[,]> HelixGlow()
    {
        var frames = new List<bool[,]>();
        const int frameCount = 24;
        for (int fi = 0; fi < frameCount; fi++)
        {
            var f = new bool[Grid, Grid];
            double t = (double)fi / frameCount;
            for (int c = 0; c < Grid; c++)
            {
                double a = 2.0 + 2.0 * Math.Sin(2 * Math.PI * t + c * 0.9);
                double b = 2.0 - 2.0 * Math.Sin(2 * Math.PI * t + c * 0.9);
                int r1 = Math.Clamp((int)Math.Round(a), 0, 4);
                int r2 = Math.Clamp((int)Math.Round(b), 0, 4);
                f[r1, c] = true;
                f[r2, c] = true;
            }
            frames.Add(f);
        }
        return frames;
    }

    /// <summary>
    /// One dot drops down each column in turn; landed dots stack at the
    /// bottom row. Once the bottom row is full, everything clears and the
    /// pattern restarts.
    /// </summary>
    private static List<bool[,]> BlockDrop()
    {
        var frames = new List<bool[,]>();
        for (int c = 0; c < Grid; c++)
        {
            for (int r = 0; r < Grid; r++)
            {
                var f = new bool[Grid, Grid];
                // Stack of previously-landed dots on the bottom row.
                for (int prev = 0; prev < c; prev++) f[Grid - 1, prev] = true;
                // Falling dot.
                f[r, c] = true;
                frames.Add(f);
            }
        }
        // Hold the full bottom row, then clear before restart.
        var full = new bool[Grid, Grid];
        for (int c = 0; c < Grid; c++) full[Grid - 1, c] = true;
        frames.Add(full);
        frames.Add(full);
        frames.Add(new bool[Grid, Grid]);
        return frames;
    }
}
