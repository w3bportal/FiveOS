// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using FiveOS.ViewModels;

namespace FiveOS.Views;

/// <summary>
/// Code-behind for the "RPF" tab. Like the other tabs, user actions are
/// plain event handlers that call public methods on the VM — folder/file
/// pickers and drag-drop are view concerns, so they live here.
/// (The SP car → FiveM studio moved to its own Vehicles tab.)
/// </summary>
public partial class RpfConverterView : UserControl
{
    public RpfConverterView() => InitializeComponent();

    private RpfConverterViewModel? Vm => DataContext as RpfConverterViewModel;

    private void OnBrowseInput(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var dlg = new OpenFolderDialog
        {
            Title = "Pick the FiveM resource folder to pack",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog() == true)
            Vm.SetInputFolder(dlg.FolderName);
    }

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        if (Vm.IsFolderOutput)
        {
            // DLC / replace modes: OutputPath is the destination FOLDER; the
            // builder creates the pack tree under it.
            var dlg = new OpenFolderDialog
            {
                Title = "Pick the output folder",
                InitialDirectory = GetOutputDir(Vm.OutputPath)
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            if (dlg.ShowDialog() == true) Vm.OutputPath = dlg.FolderName;
        }
        else
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save the packed .rpf as…",
                Filter = "RAGE Package (*.rpf)|*.rpf",
                DefaultExt = ".rpf",
                FileName = string.IsNullOrWhiteSpace(Vm.OutputPath) ? "addon.rpf" : Path.GetFileName(Vm.OutputPath),
                InitialDirectory = GetOutputDir(Vm.OutputPath),
            };
            if (dlg.ShowDialog() == true) Vm.OutputPath = dlg.FileName;
        }
    }

    private static string? GetOutputDir(string outputPath)
    {
        try { return string.IsNullOrWhiteSpace(outputPath) ? null : Path.GetDirectoryName(outputPath); }
        catch { return null; }
    }

    private async void OnConvert(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;

        // Raw mode writes a single .rpf in place — confirm overwrite. Folder
        // modes write into a subfolder the builder manages itself.
        if (Vm.IsRawMode && File.Exists(Vm.OutputPath))
        {
            var pick = AppDialog.Show(
                $"{Path.GetFileName(Vm.OutputPath)} already exists and will be overwritten.\n\nContinue?",
                "Overwrite .rpf?",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                Window.GetWindow(this));
            if (pick != MessageBoxResult.OK) return;
        }

        await Vm.ConvertAsync();
    }

    // ─── Drag-drop a folder onto the tab ─────────────────────────────────

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedFolder(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (TryGetDroppedFolder(e) != null) DropOverlay.Visibility = Visibility.Visible;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var folder = TryGetDroppedFolder(e);
        if (folder != null) Vm?.SetInputFolder(folder);
    }

    private void OnOpenOutput(object sender, RoutedEventArgs e)
    {
        var path = Vm?.LastOutputPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* explorer failure is non-fatal */ }
    }

    /// <summary>First dropped path that is a directory, or null.</summary>
    private static string? TryGetDroppedFolder(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return null;
        foreach (var p in paths)
            if (Directory.Exists(p)) return p;
        return null;
    }
}
