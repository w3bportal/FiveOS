using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FiveOS.Services;

public sealed partial class TextureVariant : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _name = "";

    public Dictionary<string, string> PartTextures { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string Summary
    {
        get
        {
            if (PartTextures.Count == 0) return "(no textures)";
            if (PartTextures.Count == 1)
                return Path.GetFileName(PartTextures.Values.First());
            return $"{PartTextures.Count} textures";
        }
    }

    /// <summary>First staged image — used for sidebar thumbnails.</summary>
    public string? PreviewPath =>
        PartTextures.Count == 0 ? null : PartTextures.Values.FirstOrDefault();

    public bool HasTextures => PartTextures.Count > 0;

    public void NotifyTexturesChanged()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasTextures));
        OnPropertyChanged(nameof(PreviewPath));
    }
}

public static class TextureVariantImport
{
    public const int MaxVariants = 30;

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds", ".webp",
    };

    public static bool IsImageFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        return ImageExtensions.Contains(Path.GetExtension(path));
    }

    public static Dictionary<string, string> MatchImagesToParts(
        IReadOnlyList<string> imagePaths,
        IReadOnlyList<string> partNames)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (imagePaths.Count == 0 || partNames.Count == 0) return result;

        var usedParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unmatched = new List<string>();

        foreach (var path in imagePaths)
        {
            if (!IsImageFile(path)) continue;
            var stem = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(stem)) continue;
            var stemLower = stem.ToLowerInvariant();

            var match = partNames.FirstOrDefault(p =>
                string.Equals(p, stem, StringComparison.OrdinalIgnoreCase) && !usedParts.Contains(p));
            if (match == null)
            {
                match = partNames.FirstOrDefault(p =>
                    !usedParts.Contains(p) &&
                    (p.ToLowerInvariant().Contains(stemLower) ||
                     stemLower.Contains(p.ToLowerInvariant())));
            }
            if (match == null)
            {
                unmatched.Add(path);
                continue;
            }

            result[match] = path;
            usedParts.Add(match);
        }

        foreach (var path in unmatched)
        {
            var target = partNames.FirstOrDefault(p => !usedParts.Contains(p));
            if (target == null) break;
            result[target] = path;
            usedParts.Add(target);
        }

        return result;
    }

    public static TextureVariant? FromFolder(string folderPath, IReadOnlyList<string> partNames, string? stageDir = null)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return null;

        var images = Directory.EnumerateFiles(folderPath)
            .Where(IsImageFile)
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (images.Count == 0) return null;

        var matched = MatchImagesToParts(images, partNames);
        if (matched.Count == 0) return null;

        var variant = new TextureVariant
        {
            Name = SanitizeName(Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))),
        };
        foreach (var kv in matched)
            variant.PartTextures[kv.Key] = StageImage(kv.Value, stageDir);
        variant.NotifyTexturesChanged();
        return variant;
    }

    public static TextureVariant? FromSingleImage(string imagePath, IReadOnlyList<string> partNames, string? stageDir = null)
    {
        if (!IsImageFile(imagePath) || partNames.Count == 0) return null;
        var matched = MatchImagesToParts(new[] { imagePath }, partNames);
        if (matched.Count == 0) return null;

        var variant = new TextureVariant
        {
            Name = SanitizeName(Path.GetFileNameWithoutExtension(imagePath)),
        };
        foreach (var kv in matched)
            variant.PartTextures[kv.Key] = StageImage(kv.Value, stageDir);
        variant.NotifyTexturesChanged();
        return variant;
    }

    public static string SanitizeName(string raw)
    {
        var chars = (raw ?? "").ToLowerInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-')
                chars[i] = '_';
        var s = new string(chars).Trim('_', '-');
        return string.IsNullOrEmpty(s) ? "variant" : s;
    }

    public static string UniqueName(string preferred, IEnumerable<string> existing)
    {
        var set = new HashSet<string>(existing.Select(SanitizeName), StringComparer.OrdinalIgnoreCase);
        var baseName = SanitizeName(preferred);
        if (string.IsNullOrEmpty(baseName)) baseName = "variant";
        var cand = baseName;
        int n = 2;
        while (set.Contains(cand))
        {
            cand = $"{baseName}_{n:D2}";
            n++;
        }
        return cand;
    }

    public static string IndexedName(string propBase, int oneBasedIndex)
    {
        var stem = SanitizeName(propBase);
        if (string.IsNullOrEmpty(stem)) stem = "prop";
        return $"{stem}_{oneBasedIndex:D2}";
    }

    private static string StageImage(string imagePath, string? stageDir)
    {
        if (string.IsNullOrEmpty(stageDir))
            return Path.GetFullPath(imagePath);

        Directory.CreateDirectory(stageDir);
        var dest = Path.Combine(stageDir,
            Guid.NewGuid().ToString("N")[..8] + "_" + Path.GetFileName(imagePath));
        File.Copy(imagePath, dest, overwrite: true);
        return dest;
    }
}
