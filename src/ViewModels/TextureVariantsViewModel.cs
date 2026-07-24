using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FiveOS.Services;

namespace FiveOS.ViewModels;

public sealed partial class TextureVariantsViewModel : ObservableObject
{
    private readonly string _stageDir;
    private readonly List<string> _partNames;

    public ObservableCollection<TextureVariant> Variants { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(CanConvert))]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "Add textures below — each image becomes its own prop in the pack.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _progressCurrent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _progressTotal;

    public string ProgressText => ProgressTotal <= 0
        ? ""
        : $"{ProgressCurrent} / {ProgressTotal}";

    public int Count => Variants.Count;
    public bool IsEmpty => Variants.Count == 0;
    public bool HasItems => Variants.Count > 0;
    public bool IsIdle => !IsRunning;
    public bool CanAdd => IsIdle && Variants.Count < TextureVariantImport.MaxVariants;
    public bool CanConvert => IsIdle && Variants.Any(v => v.HasTextures);

    public TextureVariantsViewModel(IReadOnlyList<string> partNames, string propBaseName)
    {
        _partNames = partNames.ToList();
        _ = propBaseName;
        _stageDir = Path.Combine(Path.GetTempPath(), "FiveOS", "tex-variants",
            Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_stageDir);
        Variants.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(CanAdd));
            OnPropertyChanged(nameof(CanConvert));
        };
    }

    public string StageDir => _stageDir;

    public void UpdatePartNames(IReadOnlyList<string> partNames)
    {
        _partNames.Clear();
        _partNames.AddRange(partNames);
    }

    /// <summary>One image → every part (library preview / recolor pack).</summary>
    public TextureVariant? AddFullModelImage(string imagePath, string? preferredName = null)
    {
        if (!CanAdd || !TextureVariantImport.IsImageFile(imagePath) || _partNames.Count == 0)
            return null;

        var staged = Path.Combine(_stageDir,
            Guid.NewGuid().ToString("N")[..8] + "_" + Path.GetFileName(imagePath));
        Directory.CreateDirectory(_stageDir);
        File.Copy(imagePath, staged, overwrite: true);

        var variant = new TextureVariant
        {
            Name = TextureVariantImport.UniqueName(
                preferredName ?? Path.GetFileNameWithoutExtension(imagePath),
                Variants.Select(v => v.Name)),
        };
        foreach (var part in _partNames)
            variant.PartTextures[part] = staged;
        variant.NotifyTexturesChanged();
        Variants.Add(variant);
        StatusText = $"Added '{variant.Name}'.";
        return variant;
    }

    public bool TryAddFolder(string folderPath)
    {
        if (!CanAdd) return false;
        var variant = TextureVariantImport.FromFolder(folderPath, _partNames, _stageDir);
        if (variant is null || !variant.HasTextures) return false;
        variant.Name = TextureVariantImport.UniqueName(variant.Name, Variants.Select(v => v.Name));
        Variants.Add(variant);
        StatusText = $"Added folder '{variant.Name}' · {variant.Summary}";
        return true;
    }

    public int AddImages(IEnumerable<string> imagePaths)
    {
        int added = 0;
        foreach (var path in imagePaths)
        {
            if (!CanAdd) break;
            if (!TextureVariantImport.IsImageFile(path)) continue;
            var variant = TextureVariantImport.FromSingleImage(path, _partNames, _stageDir);
            if (variant is null || !variant.HasTextures) continue;
            variant.Name = TextureVariantImport.UniqueName(variant.Name, Variants.Select(v => v.Name));
            Variants.Add(variant);
            added++;
        }
        if (added > 0)
            StatusText = $"Added {added} image variant(s).";
        return added;
    }

    public void Remove(TextureVariant variant)
    {
        if (IsRunning || variant is null) return;
        Variants.Remove(variant);
        StatusText = Variants.Count == 0
            ? "Add textures below — each image becomes its own prop in the pack."
            : $"{Variants.Count} ready — Build pack to stage them.";
    }

    public void Clear()
    {
        if (IsRunning) return;
        Variants.Clear();
        StatusText = "Add textures below — each image becomes its own prop in the pack.";
    }

    public void DisposeStage()
    {
        try
        {
            if (Directory.Exists(_stageDir))
                Directory.Delete(_stageDir, recursive: true);
        }
        catch { }
    }

    public IReadOnlyList<TextureVariant> ReadyVariants()
        => Variants.Where(v => v.HasTextures).Take(TextureVariantImport.MaxVariants).ToList();
}
