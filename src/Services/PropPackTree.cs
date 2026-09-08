// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FiveOS.ViewModels;
using Wpf.Ui.Controls;

namespace FiveOS.Services;

/// <summary>Unified Layers outliner node (Blender / C4D style):
/// <c>prop → parts</c>, plus <c>pack → stream → prop</c> and optional
/// <c>queue</c> for pending one-by-one converts.</summary>
public sealed partial class PropPackTreeNode : ObservableObject
{
    public enum NodeKind
    {
        /// <summary>Current loaded prop (root). Display name is <c>PropName</c>.</summary>
        Working,
        Part,
        Pack,
        Stream,
        Prop,
        Queue,
        QueueItem,

        /// <summary>An animatable part of the loaded prop, nested the way the
        /// source file nests it. Only present while the Animated stage is up.</summary>
        Bone,
    }

    public NodeKind Kind { get; init; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>Multi-select highlight in the outliner.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Photoshop-style eye. Parts mirror <c>Part.IsVisible</c>;
    /// props mirror <c>Entry.IsIncluded</c>; group rows show the aggregate
    /// (on iff any child is on). Rows dim when the eye is off.</summary>
    [ObservableProperty]
    private bool _isEyeOn = true;

    /// <summary>Inline rename in progress (group rows) — swaps the name
    /// TextBlock for a TextBox in the row template.</summary>
    [ObservableProperty]
    private bool _isRenaming;

    /// <summary>Row is the current drop target of an outliner drag —
    /// template shows an accent border so the user sees where the layer
    /// will land.</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    /// <summary>Group identity for <see cref="NodeKind.Pack"/> rows — the
    /// exact group name entries reference via <c>PropPackEntry.GroupName</c>
    /// (Name may get decorated later; this stays canonical).</summary>
    public string? GroupKey { get; init; }

    /// <summary>Staged prop leaf — remove / edit housing fields.</summary>
    public PropPackEntry? Entry { get; init; }

    /// <summary>Pending convert queue leaf.</summary>
    public PropPackQueueItem? QueueItem { get; init; }

    /// <summary>When <see cref="NodeKind.Part"/>, the live mesh part.</summary>
    public MainViewModel.ModelPart? Part { get; init; }

    /// <summary>When <see cref="NodeKind.Bone"/>, the animatable part it stands
    /// for. The row mirrors it both ways — eye, name and key count.</summary>
    public BoneRow? Bone { get; init; }

    public ObservableCollection<PropPackTreeNode> Children { get; } = new();

    public bool IsWorking => Kind == NodeKind.Working;
    public bool IsPart => Kind == NodeKind.Part;
    public bool IsPack => Kind == NodeKind.Pack;
    public bool IsStream => Kind == NodeKind.Stream;
    public bool IsProp => Kind == NodeKind.Prop;
    public bool IsQueue => Kind == NodeKind.Queue;
    public bool IsQueueItem => Kind == NodeKind.QueueItem;
    public bool IsBone => Kind == NodeKind.Bone;
    public bool CanRemove => IsProp || IsQueueItem;

    /// <summary>Bold folder-style rows: a pack group or the loaded model.</summary>
    public bool IsGroupRow => IsPack || IsWorking;

    public SymbolRegular Icon => Kind switch
    {
        NodeKind.Working => SymbolRegular.Box24,
        NodeKind.Part => SymbolRegular.Shapes24,
        NodeKind.Pack => SymbolRegular.Folder24,
        NodeKind.Stream => SymbolRegular.Folder24,
        NodeKind.Prop => SymbolRegular.Cube24,
        NodeKind.Queue => SymbolRegular.ArrowSync24,
        NodeKind.QueueItem => SymbolRegular.Document24,
        NodeKind.Bone => Bone?.HasGeometry == true ? SymbolRegular.Shapes24 : SymbolRegular.Braces24,
        _ => SymbolRegular.Circle24,
    };
}

/// <summary>Nests the Animated stage's parts the way the source file nests
/// them. Kept apart from the view model so the shape is testable on its own.</summary>
public static class BoneOutlinerTree
{
    /// <summary>Hang every listed part off its nearest listed ancestor. A part
    /// whose own parents are filtered out of <paramref name="listed"/> lands on
    /// the nearest one that survived, or at the top — so hiding the dummies
    /// flattens a branch instead of inventing one. <paramref name="all"/> is the
    /// full part list, needed to walk a chain through the hidden ones.</summary>
    public static List<PropPackTreeNode> Build(
        IReadOnlyList<BoneRow> listed,
        IReadOnlyList<BoneRow> all,
        Action<PropPackTreeNode, BoneRow>? onNode = null)
    {
        var byName = new Dictionary<string, BoneRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in all) byName[b.Name] = b;
        var inList = new HashSet<BoneRow>(listed);
        var nodeOf = new Dictionary<BoneRow, PropPackTreeNode>();
        var roots = new List<PropPackTreeNode>();

        BoneRow? ParentOf(BoneRow b)
            => b.ParentName is not null && byName.TryGetValue(b.ParentName, out var p) && !ReferenceEquals(p, b) ? p : null;

        foreach (var b in listed)
        {
            var node = new PropPackTreeNode
            {
                Kind = PropPackTreeNode.NodeKind.Bone,
                Name = b.Name,
                Bone = b,
                IsExpanded = true,
                IsEyeOn = b.IsVisible,
                Detail = b.IsKeyed ? b.Detail : "",
                IsSelected = b.IsSelected,
            };
            nodeOf[b] = node;

            PropPackTreeNode? parent = null;
            // Seeded with the row itself: a malformed file can name a parent
            // chain that loops back round to it, and a row must never end up
            // its own ancestor (it would vanish from the tree entirely).
            var seen = new HashSet<BoneRow> { b };
            for (var p = ParentOf(b); p is not null && seen.Add(p); p = ParentOf(p))
                if (inList.Contains(p) && nodeOf.TryGetValue(p, out var pn)) { parent = pn; break; }

            if (parent is null) roots.Add(node); else parent.Children.Add(node);
            onNode?.Invoke(node, b);
        }
        return roots;
    }
}

/// <summary>One source mesh waiting to be converted into the pack stream.</summary>
public sealed partial class PropPackQueueItem : ObservableObject
{
    public PropPackQueueItem(string sourcePath, string assetName)
    {
        SourcePath = sourcePath;
        AssetName = assetName;
        _status = "Pending";
    }

    public string SourcePath { get; }
    public string AssetName { get; }
    public string SourceName => System.IO.Path.GetFileName(SourcePath);

    /// <summary>Group the converted prop should land in (null = loose
    /// layer). Stamped at enqueue time from the drop target / selection,
    /// carried into the engine's pack delivery.</summary>
    public string? GroupName { get; set; }

    /// <summary>Layer eye for pending rows — off = skipped at export,
    /// exactly like a staged layer's eye.</summary>
    [ObservableProperty]
    private bool _isIncluded = true;

    /// <summary>Full convert request captured at drag-drop time. A loaded
    /// model dropped on a group enqueues INSTANTLY with the user's gizmo
    /// transform / hidden parts / materials frozen here — the queue run
    /// uses this instead of a default-settings request, so nothing the
    /// user set up in the viewport is lost.</summary>
    public EngineRunner.ConvertRequest? RequestSnapshot { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    private string _status;

    [ObservableProperty]
    private string? _error;

    public bool IsPending => Status == "Pending";
    public bool IsRunning => Status == "Converting";
    public bool IsDone => Status == "Done";
    public bool IsFailed => Status == "Failed";
}
