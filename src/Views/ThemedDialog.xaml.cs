// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Windows;
using System.Windows.Media;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace FiveOS.Views;

public partial class ThemedDialog : Wpf.Ui.Controls.FluentWindow
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public ThemedDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        InitializeComponent();
        Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
        Title = title;
        TitleBarCtl.Title = title;
        MessageCtl.Text = message;
        ConfigureIcon(icon);
        ConfigureButtons(buttons);
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        FitToContent();
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        FitToContent();
    }

    private void FitToContent()
    {
        if (RootBorder is null) return;
        RootBorder.UpdateLayout();
        RootBorder.Measure(new Size(Width, double.PositiveInfinity));
        var contentH = RootBorder.DesiredSize.Height;
        if (contentH <= 1) return;
        SizeToContent = SizeToContent.Manual;
        Height = Math.Ceiling(contentH);
    }

    private void ConfigureIcon(MessageBoxImage icon)
    {
        (SymbolRegular sym, Color col) = icon switch
        {
            MessageBoxImage.Error       => (SymbolRegular.ErrorCircle24,    Color.FromRgb(0xE8, 0x11, 0x23)),
            MessageBoxImage.Warning     => (SymbolRegular.Warning24,        Color.FromRgb(0xF7, 0x63, 0x0C)),
            MessageBoxImage.Question    => (SymbolRegular.QuestionCircle24, Color.FromRgb(0x4C, 0x9A, 0xFF)),
            MessageBoxImage.Information => (SymbolRegular.Info24,           Color.FromRgb(0x4C, 0x9A, 0xFF)),
            _                           => (SymbolRegular.Info24,           Colors.Transparent),
        };
        if (icon == MessageBoxImage.None) { IconCtl.Visibility = Visibility.Collapsed; return; }
        IconCtl.Symbol = sym;
        IconCtl.Foreground = new SolidColorBrush(col);
    }

    private void ConfigureButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OK:
                PrimaryBtn.Content = "OK";
                break;
            case MessageBoxButton.OKCancel:
                PrimaryBtn.Content = "OK";
                CloseBtn.Content = "Cancel"; CloseBtn.Visibility = Visibility.Visible;
                break;
            case MessageBoxButton.YesNo:
                PrimaryBtn.Content = "Yes";
                CloseBtn.Content = "No"; CloseBtn.Visibility = Visibility.Visible;
                break;
            case MessageBoxButton.YesNoCancel:
                PrimaryBtn.Content = "Yes";
                SecondaryBtn.Content = "No"; SecondaryBtn.Visibility = Visibility.Visible;
                CloseBtn.Content = "Cancel"; CloseBtn.Visibility = Visibility.Visible;
                break;
        }
        _buttons = buttons;
    }

    private MessageBoxButton _buttons;

    private void OnPrimary(object sender, RoutedEventArgs e)
        => Finish(_buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel
            ? MessageBoxResult.Yes : MessageBoxResult.OK);

    private void OnSecondary(object sender, RoutedEventArgs e) => Finish(MessageBoxResult.No);

    private void OnClose(object sender, RoutedEventArgs e)
        => Finish(_buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel);

    private void Finish(MessageBoxResult r)
    {
        Result = r;
        try { DialogResult = r is MessageBoxResult.OK or MessageBoxResult.Yes; } catch { }
        Close();
    }
}
