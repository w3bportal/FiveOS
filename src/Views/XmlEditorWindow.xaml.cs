// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Wpf.Ui.Controls;

namespace FiveOS.Views;

/// <summary>
/// Modal raw-XML editor for a vehicle resource's meta files (vehicles /
/// handling / carcols / carvariations). Validates the text is well-formed
/// XML before overwriting — a broken meta crashes the resource on the server.
/// <see cref="Saved"/> is true if the file was written at least once.
/// </summary>
public partial class XmlEditorWindow : FluentWindow
{
    private readonly string _path;
    private bool _dirty;

    /// <summary>True if the file was saved during this dialog session.</summary>
    public bool Saved { get; private set; }

    public XmlEditorWindow(string path)
    {
        InitializeComponent();
        _path = path;
        Title = "Edit — " + Path.GetFileName(path);
        PathText.Text = path;
        // Route EVERY close path (title-bar X, Alt+F4, the Close button) through
        // the same unsaved-changes guard.
        Closing += OnClosing;
        Load();
    }

    private void Load()
    {
        try
        {
            Editor.TextChanged -= OnTextChanged;
            Editor.Text = File.ReadAllText(_path);
            Editor.TextChanged += OnTextChanged;
            _dirty = false;
            SaveButton.IsEnabled = false;
            StatusText.Text = $"{new FileInfo(_path).Length:N0} bytes — edit and Save.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't open: " + ex.Message;
            Editor.IsReadOnly = true;
        }
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _dirty = true;
        SaveButton.IsEnabled = true;
    }

    private void OnReload(object sender, RoutedEventArgs e) => Load();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Well-formedness gate — never write a meta the game can't parse.
        try { XDocument.Parse(Editor.Text); }
        catch (Exception ex)
        {
            StatusText.Text = "Not valid XML — not saved: " + ex.Message;
            return;
        }
        try
        {
            File.WriteAllText(_path, Editor.Text);
            Saved = true;
            _dirty = false;
            SaveButton.IsEnabled = false;
            StatusText.Text = "Saved. Restart the resource on your server to apply.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Save failed: " + ex.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_dirty) return;
        var pick = AppDialog.Show(
            "Discard unsaved changes?", "Unsaved changes",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question,
            this);
        if (pick != System.Windows.MessageBoxResult.OK) e.Cancel = true;
    }
}
