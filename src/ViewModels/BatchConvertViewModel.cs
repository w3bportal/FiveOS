// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FiveOS.ViewModels;

/// <summary>One row in the batch-convert queue. <see cref="Status"/>
/// drives the colour of the badge in the list — Pending stays grey,
/// Converting goes accent, Done goes green, Failed goes red. The user
/// can remove a row while the queue is idle; rows already converted
/// (Done) are left in place as a record of what shipped.</summary>
public partial class BatchConvertItem : ObservableObject
{
    public string SourcePath { get; }
    public string SourceName => Path.GetFileName(SourcePath);
    public string AssetName { get; }
    public long FileBytes { get; }

    public string SizeDisplay
    {
        get
        {
            double b = FileBytes;
            if (b < 1024) return $"{b:0} B";
            if (b < 1024 * 1024) return $"{b / 1024:0.#} KB";
            return $"{b / (1024 * 1024):0.##} MB";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(StatusBackgroundBrush))]
    [NotifyPropertyChangedFor(nameof(StatusForegroundBrush))]
    private BatchConvertStatus _status = BatchConvertStatus.Pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Pill background colour by status. Kept on the item VM so
    /// the XAML doesn't need a per-status value converter — Wpf.Ui's
    /// theme brushes don't expose a "neutral / accent / success / danger"
    /// API we can switch on directly.</summary>
    public Brush StatusBackgroundBrush => Status switch
    {
        BatchConvertStatus.Pending    => new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80)),
        BatchConvertStatus.Converting => new SolidColorBrush(Color.FromArgb(0x33, 0x4C, 0xC2, 0xFF)),
        BatchConvertStatus.Done       => new SolidColorBrush(Color.FromArgb(0x33, 0x4C, 0xAF, 0x50)),
        BatchConvertStatus.Failed     => new SolidColorBrush(Color.FromArgb(0x33, 0xF4, 0x43, 0x36)),
        BatchConvertStatus.Skipped    => new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xA7, 0x26)),
        _ => Brushes.Transparent,
    };

    public Brush StatusForegroundBrush => Status switch
    {
        BatchConvertStatus.Pending    => new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
        BatchConvertStatus.Converting => new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE)),
        BatchConvertStatus.Done       => new SolidColorBrush(Color.FromRgb(0xA8, 0xD8, 0xA8)),
        BatchConvertStatus.Failed     => new SolidColorBrush(Color.FromRgb(0xFF, 0xA8, 0xA0)),
        BatchConvertStatus.Skipped    => new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x7C)),
        _ => Brushes.White,
    };

    public string StatusLabel => Status switch
    {
        BatchConvertStatus.Pending    => "Queued",
        BatchConvertStatus.Converting => "Converting…",
        BatchConvertStatus.Done       => "Done",
        BatchConvertStatus.Failed     => "Failed",
        BatchConvertStatus.Skipped    => "Skipped",
        _ => Status.ToString(),
    };

    public bool IsBusy => Status == BatchConvertStatus.Converting;

    public BatchConvertItem(string sourcePath, string assetName, long fileBytes)
    {
        SourcePath = sourcePath;
        AssetName = assetName;
        FileBytes = fileBytes;
    }
}

public enum BatchConvertStatus
{
    Pending,
    Converting,
    Done,
    Failed,
    Skipped,
}

/// <summary>
/// Backs <see cref="Views.BatchConvertWindow"/>. Owns a 1–30-item queue
/// of source meshes and the shared convert settings that get applied to
/// every item. Each item flows through the existing
/// <see cref="Services.EngineRunner"/> with <c>RouteToPack=true</c>, so
/// the converted YDR/YBN land in the global <see cref="Services.PropPackSession"/>
/// instead of producing a per-asset .zip. When the queue is finished the
/// dialog hands off to <see cref="Services.PropPackBuilder"/> to compile
/// the merged resource — same code path the single-prop "Add to Pack +
/// Finalize" flow uses, just driven from one click instead of N.
/// </summary>
public partial class BatchConvertViewModel : ObservableObject
{
    /// <summary>Hard cap on queue size. RAGE has no per-pack archetype
    /// limit, but the UI's list gets unwieldy past ~30 rows and the
    /// engine spawn-per-item overhead means even a beefy machine takes a
    /// couple minutes to chew through that many. The cap is also a
    /// scope-control signal — for bigger batches the user should script
    /// the engine directly.</summary>
    public const int MaxItems = 30;

    public ObservableCollection<BatchConvertItem> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SafePackName))]
    private string _packName = "props_pack";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanClear))]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "Drop or browse 3D files to queue (up to 30).";

    /// <summary>When true, the dialog compiles the merged pack as soon
    /// as the queue finishes. When false, items stay parked in
    /// <see cref="Services.PropPackSession.Current"/> and the user
    /// finalises later from the main window's pack panel — useful when
    /// they want to append additional individually-converted props
    /// before zipping.</summary>
    [ObservableProperty]
    private bool _finalizeWhenDone = true;

    [ObservableProperty] private bool _includeCollision = true;
    [ObservableProperty] private bool _embedCollision = true;
    [ObservableProperty] private bool _includeYtyp = true;
    [ObservableProperty] private bool _extractTextures = true;
    [ObservableProperty] private bool _generateLods = false;
    [ObservableProperty] private string _collisionMaterial = "CONCRETE";

    /// <summary>Index of the item currently being converted (1-based for
    /// display, e.g. "Converting 3 of 17…"). Zero when idle.</summary>
    [ObservableProperty]
    private int _currentIndex;

    public int Count => Items.Count;
    public int RemainingCapacity => Math.Max(0, MaxItems - Items.Count);
    public bool IsIdle => !IsRunning;
    public bool CanStart => !IsRunning && Items.Any(i => i.Status == BatchConvertStatus.Pending);
    public bool CanClear => !IsRunning && Items.Count > 0;
    public bool CanAdd => !IsRunning && Items.Count < MaxItems;
    public bool IsEmpty => Items.Count == 0;
    public bool HasItems => Items.Count > 0;

    /// <summary>Sanitised pack name used as the on-disk folder/zip stem.
    /// Live preview so the dialog can show what the file will be called.</summary>
    public string SafePackName
    {
        get
        {
            var s = SanitizeAssetName(PackName);
            return string.IsNullOrEmpty(s) ? "props_pack" : s;
        }
    }

    public BatchConvertViewModel()
    {
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(RemainingCapacity));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanClear));
            OnPropertyChanged(nameof(CanAdd));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasItems));
        };
    }

    /// <summary>Push file paths into the queue. Silently skips paths
    /// that don't exist, aren't a supported mesh format, are
    /// already in the queue, or push past the 30-item cap. Returns the
    /// number actually added so the caller can surface a toast.</summary>
    public int AddPaths(IEnumerable<string> paths)
    {
        int added = 0;
        int rejected = 0;
        foreach (var rawPath in paths)
        {
            if (Items.Count >= MaxItems) break;
            if (string.IsNullOrWhiteSpace(rawPath) || !File.Exists(rawPath))
            {
                rejected++;
                continue;
            }
            if (!IsSupportedForBatch(rawPath))
            {
                rejected++;
                continue;
            }
            if (Items.Any(i => string.Equals(i.SourcePath, rawPath, StringComparison.OrdinalIgnoreCase)))
                continue;

            long size = 0;
            try { size = new FileInfo(rawPath).Length; } catch { /* size cosmetic */ }

            var stem = Path.GetFileNameWithoutExtension(rawPath);
            var assetName = UniqueAssetName(SanitizeAssetName(stem));
            Items.Add(new BatchConvertItem(rawPath, assetName, size));
            added++;
        }

        if (added > 0 && rejected == 0)
            StatusText = $"{Items.Count} of {MaxItems} queued.";
        else if (added > 0 && rejected > 0)
            StatusText = $"{Items.Count} of {MaxItems} queued ({rejected} skipped — unsupported or missing).";
        else if (rejected > 0)
            StatusText = $"Nothing added — {rejected} file(s) were unsupported or missing.";
        return added;
    }

    public void Remove(BatchConvertItem item)
    {
        if (IsRunning) return;
        Items.Remove(item);
    }

    public void Clear()
    {
        if (IsRunning) return;
        Items.Clear();
        StatusText = "Queue cleared.";
    }

    public void ResetItemStatuses()
    {
        foreach (var i in Items)
        {
            i.Status = BatchConvertStatus.Pending;
            i.Error = null;
        }
    }

    /// <summary>Formats supported by the Assimp loader inside the engine.</summary>
    public static bool IsSupportedForBatch(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".obj" or ".glb" or ".gltf" or ".fbx" or ".dae" or ".ply" or ".stl";
    }

    /// <summary>Asset-name sanitiser — same shape as MainWindow's
    /// <c>SanitizeAssetName</c> but kept private here so the batch
    /// VM doesn't reach into the view layer. Lowercases, strips to
    /// alphanumerics + underscore, prefixes a leading digit with
    /// "p_" (RAGE archetypes don't allow leading digits), and caps
    /// at 32 chars.</summary>
    private static string SanitizeAssetName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "prop";
        var sb = new StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ' || ch == '-') sb.Append('_');
        }
        var s = sb.ToString();
        if (string.IsNullOrEmpty(s)) s = "prop";
        if (char.IsDigit(s[0])) s = "p_" + s;
        return s.Length > 32 ? s.Substring(0, 32) : s;
    }

    /// <summary>Ensure the new asset name doesn't collide with any
    /// already queued. We could leave it for the pack-build step's
    /// slot disambiguation to handle, but pre-uniquing here means the
    /// user sees the final archetype name in the list straight away
    /// (e.g. "table" + "table" → "table" and "table-2").</summary>
    private string UniqueAssetName(string baseName)
    {
        var cand = baseName;
        int n = 2;
        while (Items.Any(i => string.Equals(i.AssetName, cand, StringComparison.OrdinalIgnoreCase)))
        {
            cand = $"{baseName}-{n}";
            n++;
        }
        return cand;
    }
}
