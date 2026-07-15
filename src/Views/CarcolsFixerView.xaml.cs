// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using FiveOS.ViewModels;

namespace FiveOS.Views;

/// <summary>
/// Code-behind for the Carcols Fixer tab. Like the other tool tabs, user
/// actions are plain event handlers calling public methods on the VM — the
/// folder picker and the overwrite-confirm dialog are view concerns, so they
/// live here rather than in the VM.
/// </summary>
public partial class CarcolsFixerView : UserControl
{
    public CarcolsFixerView()
    {
        InitializeComponent();
    }

    private CarcolsFixerViewModel? Vm => DataContext as CarcolsFixerViewModel;

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var dlg = new OpenFolderDialog
        {
            Title = "Pick the resources folder to scan for carcols conflicts",
            InitialDirectory = Services.UserSettings.LoadCarcolsScanFolder()
                ?? Services.UserSettings.LoadServerResourceFolder()
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog() == true)
            Vm.SetFolder(dlg.FolderName);
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        await Vm.ScanAsync();
    }

    private async void OnFix(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;

        var pick = AppDialog.Show(
            $"Remap {Vm.FixableCount} clashing ID(s) — the carcols/carvariations metas in your resources folder will be overwritten in place.\n\n" +
            (Vm.BackupOriginals
                ? "Originals will be backed up to Downloads\\FiveOS_Carcols_Backup_… first.\n\n"
                : "Backups are OFF — make sure you have copies elsewhere.\n\n") +
            "Continue?",
            "Fix carcols conflicts",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            Window.GetWindow(this));
        if (pick != MessageBoxResult.OK) return;

        await Vm.FixAsync();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Vm?.RequestCancel();

    private void OnOpenBackup(object sender, RoutedEventArgs e)
    {
        var path = Vm?.LastBackupRoot;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")
                { UseShellExecute = true });
        }
        catch { /* explorer failure is non-fatal */ }
    }
}
