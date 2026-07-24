// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FiveOS.ViewModels;

/// <summary>Which section a chrome document tab belongs to.</summary>
public enum WorkspaceKind
{
    Assets,
    Optimize,
    Emotes,
    Vehicles,
    Rpf,
}

/// <summary>One open document tab in the app chrome (C4D-style). Emotes
/// tabs link to an <see cref="EmoteDocument"/> via <see cref="EmoteDocumentId"/>;
/// other kinds share their section's single workspace for now.</summary>
public sealed partial class WorkspaceDocument : ObservableObject
{
    private static int _nextSerial = 1;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public int Serial { get; } = _nextSerial++;

    private WorkspaceKind _kind;
    public WorkspaceKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(OpenViewTag));
        }
    }

    public string? EmoteDocumentId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string _title = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private bool _isDirty;

    public string DisplayTitle
    {
        get
        {
            var baseTitle = string.IsNullOrWhiteSpace(Title)
                ? DefaultTitleFor(Kind)
                : Title.Trim();
            return IsDirty ? baseTitle + " *" : baseTitle;
        }
    }

    public static string DefaultTitleFor(WorkspaceKind kind) => kind switch
    {
        WorkspaceKind.Assets => "Assets",
        WorkspaceKind.Optimize => "Optimize",
        WorkspaceKind.Emotes => "Untitled",
        WorkspaceKind.Vehicles => "Vehicles",
        WorkspaceKind.Rpf => "RPF",
        _ => "Untitled",
    };

    public static WorkspaceKind? KindFromAppView(AppView view) => view switch
    {
        AppView.Props or AppView.AnimatedProps => WorkspaceKind.Assets,
        AppView.Optimize => WorkspaceKind.Optimize,
        AppView.Emotes => WorkspaceKind.Emotes,
        AppView.Vehicles => WorkspaceKind.Vehicles,
        AppView.Rpf => WorkspaceKind.Rpf,
        _ => null,
    };

    public string OpenViewTag => Kind switch
    {
        WorkspaceKind.Assets => "Assets",
        WorkspaceKind.Optimize => "Optimize",
        WorkspaceKind.Emotes => "Emotes",
        WorkspaceKind.Vehicles => "Vehicles",
        WorkspaceKind.Rpf => "Rpf",
        _ => "Assets",
    };
}

/// <summary>Open chrome document tabs + the active one.</summary>
public sealed partial class WorkspaceDocumentSet : ObservableObject
{
    public ObservableCollection<WorkspaceDocument> Documents { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveDocumentId))]
    private WorkspaceDocument? _activeDocument;

    public string? ActiveDocumentId => ActiveDocument?.Id;

    public WorkspaceDocumentSet()
    {
        // Tabs are restored from the last session (or seeded with Assets)
        // by MainViewModel — don't force-open Assets here.
    }

    /// <summary>Replace open tabs with a restored session. Always leaves ≥1 tab.</summary>
    public void ReplaceAll(IEnumerable<WorkspaceDocument> docs, WorkspaceDocument? active)
    {
        Documents.Clear();
        foreach (var d in docs)
            Documents.Add(d);
        if (Documents.Count == 0)
        {
            var fallback = new WorkspaceDocument { Kind = WorkspaceKind.Assets, Title = "Assets" };
            Documents.Add(fallback);
            ActiveDocument = fallback;
            return;
        }
        ActiveDocument = active != null && Documents.Contains(active)
            ? active
            : Documents[0];
    }

    public WorkspaceDocument NewDocument(WorkspaceKind kind, bool activate = true, string? title = null, string? emoteDocumentId = null)
    {
        var doc = new WorkspaceDocument
        {
            Kind = kind,
            Title = title ?? WorkspaceDocument.DefaultTitleFor(kind),
            EmoteDocumentId = emoteDocumentId,
        };
        Documents.Add(doc);
        if (activate) ActiveDocument = doc;
        return doc;
    }

    public WorkspaceDocument? Find(string id)
    {
        foreach (var d in Documents)
            if (string.Equals(d.Id, id, StringComparison.Ordinal))
                return d;
        return null;
    }

    public WorkspaceDocument? FindByEmoteId(string emoteId)
    {
        foreach (var d in Documents)
            if (d.Kind == WorkspaceKind.Emotes
                && string.Equals(d.EmoteDocumentId, emoteId, StringComparison.Ordinal))
                return d;
        return null;
    }

    public WorkspaceDocument? FindLastOfKind(WorkspaceKind kind)
    {
        WorkspaceDocument? last = null;
        foreach (var d in Documents)
            if (d.Kind == kind) last = d;
        return last;
    }

    public WorkspaceDocument EnsureKind(WorkspaceKind kind, bool activate = true)
    {
        var existing = FindLastOfKind(kind);
        if (existing != null)
        {
            if (activate) ActiveDocument = existing;
            return existing;
        }
        return NewDocument(kind, activate);
    }

    public void Repurpose(WorkspaceDocument doc, WorkspaceKind kind)
    {
        if (!Documents.Contains(doc)) return;
        doc.Kind = kind;
        doc.Title = WorkspaceDocument.DefaultTitleFor(kind);
        doc.IsDirty = false;
        doc.EmoteDocumentId = null;
        ActiveDocument = doc;
    }

    public void Activate(WorkspaceDocument doc)
    {
        if (!Documents.Contains(doc)) return;
        ActiveDocument = doc;
    }

    /// <summary>Close <paramref name="doc"/>. Always leaves ≥1 tab.
    /// Returns the document that should become active next.</summary>
    public WorkspaceDocument CloseDocument(WorkspaceDocument doc)
    {
        if (Documents.Count <= 1)
        {
            // Reset sole tab rather than removing it.
            doc.Title = WorkspaceDocument.DefaultTitleFor(doc.Kind);
            doc.IsDirty = false;
            if (doc.Kind != WorkspaceKind.Emotes)
                doc.EmoteDocumentId = null;
            ActiveDocument = doc;
            return doc;
        }

        var idx = Documents.IndexOf(doc);
        var wasActive = ReferenceEquals(ActiveDocument, doc);
        Documents.Remove(doc);
        if (!wasActive && ActiveDocument != null)
            return ActiveDocument;

        var next = Documents[Math.Clamp(idx, 0, Documents.Count - 1)];
        ActiveDocument = next;
        return next;
    }
}
