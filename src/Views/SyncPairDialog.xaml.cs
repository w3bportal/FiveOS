// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace FiveOS.Views;

/// <summary>One placed partner in a synced emote: where it stands relative
/// to the initiator (rpemotes semantics — heading is subtracted, 180 = face
/// each other). The preview body is derived from the primary ped by the
/// viewer, not chosen here.</summary>
public sealed class SyncPartner
{
    public double Front { get; set; } = 1.0;
    public double Side { get; set; }
    public double Height { get; set; }
    public double Heading { get; set; } = 180;
}

/// <summary>Offset editor for a synced (shared) emote — one partner for the
/// classic two-player pair, or several for a group formation. The caller
/// shows the teal partner ghosts in the viewport and re-positions them live
/// via <see cref="PartnersChanged"/>; Install closes with
/// <see cref="Confirmed"/> and the caller writes the RPEmotes entry (single
/// partner only — the Shared format is two-player) or the standalone
/// N-slot FiveM resource.</summary>
public partial class SyncPairDialog
{
    private const int MaxPartners = 5;

    private readonly List<SyncPartner> _partners = new() { new SyncPartner() };
    private int _sel;
    private bool _syncingUi;

    /// <summary>Fired whenever ANY partner changes (offsets, body, add,
    /// remove) — the caller re-sends the whole list to the viewer.</summary>
    public event Action? PartnersChanged;

    /// <summary>True when the user clicked Install (the window is shown
    /// MODELESS so the viewport stays interactive — DialogResult is illegal
    /// on modeless windows, hence this flag).</summary>
    public bool Confirmed { get; private set; }

    /// <summary>True = install into rpemotes-reborn; false = write a
    /// standalone FiveM resource folder. Forced to standalone (and locked)
    /// while more than one partner exists.</summary>
    public bool TargetRpEmotes => TargetRp.IsChecked == true;

    public string EmoteName => NameBox.Text?.Trim() ?? "";
    public string EmoteLabel => LabelBox.Text?.Trim() ?? "";

    /// <summary>All partners in slot order (slot 1 = first entry).</summary>
    public IReadOnlyList<SyncPartner> Partners => _partners;

    public SyncPairDialog(string defaultName, string defaultLabel)
    {
        InitializeComponent();
        NameBox.Text = defaultName;
        LabelBox.Text = defaultLabel;
        RefreshChips();
        LoadSelectedIntoUi();
    }

    private SyncPartner Sel => _partners[_sel];

    private void RefreshChips()
    {
        PartnerChips.Children.Clear();
        for (int i = 0; i < _partners.Count; i++)
        {
            int idx = i;
            var chip = new ToggleButton
            {
                Content = (i + 1).ToString(),
                IsChecked = i == _sel,
                MinWidth = 34,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = 12,
                ToolTip = $"Partner {i + 1} — select to edit its body and placement.",
            };
            chip.Click += (_, _) => SelectPartner(idx);
            PartnerChips.Children.Add(chip);
        }
        AddPartnerBtn.IsEnabled = _partners.Count < MaxPartners;
        RemovePartnerBtn.IsEnabled = _partners.Count > 1;
        UpdateInstallTargets();
    }

    private void SelectPartner(int idx)
    {
        _sel = Math.Max(0, Math.Min(idx, _partners.Count - 1));
        // Re-check every chip — clicking the already-selected chip must not
        // leave it toggled off.
        for (int i = 0; i < PartnerChips.Children.Count; i++)
            if (PartnerChips.Children[i] is ToggleButton tb) tb.IsChecked = i == _sel;
        LoadSelectedIntoUi();
    }

    private void LoadSelectedIntoUi()
    {
        _syncingUi = true;
        try
        {
            FrontSlider.Value = Sel.Front;
            SideSlider.Value = Sel.Side;
            HeightSlider.Value = Sel.Height;
            HeadingSlider.Value = Sel.Heading;
            UpdateValueLabels();
        }
        finally { _syncingUi = false; }
    }

    private void UpdateValueLabels()
    {
        FrontVal.Text = $"{FrontSlider.Value:0.00} m";
        SideVal.Text = $"{SideSlider.Value:0.00} m";
        HeightVal.Text = $"{HeightSlider.Value:0.00} m";
        HeadingVal.Text = $"{HeadingSlider.Value:0}°";
    }

    private void UpdateInstallTargets()
    {
        if (TargetRp == null || TargetFxr == null) return;
        bool group = _partners.Count > 1;
        TargetRp.IsEnabled = !group;
        if (group && TargetRp.IsChecked == true) TargetFxr.IsChecked = true;
        TargetRp.ToolTip = group
            ? "RPEmotes' shared-emote format supports exactly two players — remove the extra partners to install there."
            : "Install straight into your rpemotes-reborn resource.";
    }

    private void OnAddPartner(object sender, RoutedEventArgs e)
    {
        if (_partners.Count >= MaxPartners) return;
        // Fan new partners out to alternating sides so they never spawn
        // stacked inside an existing ghost.
        int n = _partners.Count;
        double side = (n % 2 == 1 ? 0.75 : -0.75) * Math.Ceiling(n / 2.0);
        _partners.Add(new SyncPartner
        {
            Side = Math.Max(-1.5, Math.Min(1.5, side)),
        });
        _sel = _partners.Count - 1;
        RefreshChips();
        LoadSelectedIntoUi();
        PartnersChanged?.Invoke();
    }

    private void OnRemovePartner(object sender, RoutedEventArgs e)
    {
        if (_partners.Count <= 1) return;
        _partners.RemoveAt(_sel);
        _sel = Math.Min(_sel, _partners.Count - 1);
        RefreshChips();
        LoadSelectedIntoUi();
        PartnersChanged?.Invoke();
    }

    private void OnOffsetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires during InitializeComponent before all named fields exist.
        if (FrontVal == null || SideVal == null || HeightVal == null || HeadingVal == null) return;
        UpdateValueLabels();
        if (_syncingUi) return;
        Sel.Front = FrontSlider.Value;
        Sel.Side = SideSlider.Value;
        Sel.Height = HeightSlider.Value;
        Sel.Heading = HeadingSlider.Value;
        PartnersChanged?.Invoke();
    }

    private void OnTargetChanged(object sender, RoutedEventArgs e)
    {
        if (InstallBtn == null) return; // fires during InitializeComponent
        InstallBtn.Content = TargetRpEmotes ? "Install to RPEmotes" : "Save FiveM resource…";
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(EmoteName))
        {
            AppDialog.Warn("Give the emote a name first.", "Synced emote", this);
            return;
        }
        Confirmed = true;
        Close();
    }
}
