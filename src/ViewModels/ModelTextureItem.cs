// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using CommunityToolkit.Mvvm.ComponentModel;

namespace FiveOS.ViewModels;

/// <summary>One entry in the Props texture library — click to live-preview
/// on the model.</summary>
public sealed partial class ModelTextureItem : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    /// <summary>Absolute path to a previewable image (PNG/JPG/…).</summary>
    [ObservableProperty]
    private string _path = "";

    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>User-added library entries can be removed; compiled / applied
    /// rows from convert stay until the next convert.</summary>
    [ObservableProperty]
    private bool _canRemove = true;

    /// <summary>Single row for the model's original diffuse set (grouped).</summary>
    [ObservableProperty]
    private bool _isBaseGroup;

    /// <summary>Per-part staged PNGs for restoring the original look when
    /// <see cref="IsBaseGroup"/> is selected after a live preview.</summary>
    public Dictionary<string, string>? BasePartPaths { get; set; }

    /// <summary>When this row came from a pack-variant add, keep the link so
    /// Build pack / Remove stay in sync.</summary>
    public Services.TextureVariant? LinkedVariant { get; set; }

    /// <summary>Parts (by <c>ModelPart.OriginalName</c>) this texture is
    /// assigned to. Null/empty = whole-model texture (the classic recolor
    /// flow). Set when the user had layers selected while adding — each
    /// texture then sticks to its own parts and every assignment bakes
    /// into the export together.</summary>
    public List<string>? TargetParts { get; set; }

    /// <summary>True when this row is pinned to specific parts.</summary>
    public bool IsPartScoped => TargetParts is { Count: > 0 };
}
