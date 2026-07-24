// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FiveOS.ViewModels;

/// <summary>One open emote document in the Emotes tab strip. Timeline /
/// pose ground truth lives in the shared WebView2; this holds the
/// snapshot used when the tab is inactive.</summary>
public sealed partial class EmoteDocument : ObservableObject
{
    private static int _nextSerial = 1;

    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Monotonic open order (internal).</summary>
    public int Serial { get; } = _nextSerial++;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string _title = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(HasContent))]
    private string? _loadedModelPath;

    /// <summary>JSON from <c>poseCaptureTimelineState()</c> — the format
    /// <c>poseRestoreTimelineState</c> expects.</summary>
    public string? TimelineCaptureJson { get; set; }

    /// <summary>Host-side mirrors saved with the capture so chrome can
    /// restore instantly before the viewer finishes.</summary>
    public double TimelineDuration { get; set; } = 4;
    public int TimelineFps { get; set; } = 30;
    public double TimelineTime { get; set; }
    public bool TimelineLoop { get; set; } = true;
    public int MovementIndex { get; set; }

    public bool HasContent =>
        !string.IsNullOrEmpty(LoadedModelPath) || !string.IsNullOrEmpty(TimelineCaptureJson);

    public bool HasDefaultTitle =>
        string.IsNullOrWhiteSpace(Title)
        || string.Equals(Title.Trim(), "Untitled", StringComparison.OrdinalIgnoreCase);

    public string DisplayTitle
    {
        get
        {
            var baseTitle = !string.IsNullOrWhiteSpace(Title)
                ? Title.Trim()
                : (!string.IsNullOrEmpty(LoadedModelPath)
                    ? System.IO.Path.GetFileNameWithoutExtension(LoadedModelPath)
                    : "Untitled");
            return IsDirty ? baseTitle + " •" : baseTitle;
        }
    }

    public void SetDefaultTitleIfEmpty()
    {
        if (string.IsNullOrWhiteSpace(Title))
            Title = "Untitled";
    }
}

/// <summary>Open emote documents + the active tab for the Emotes workspace.</summary>
public sealed partial class EmoteDocumentSet : ObservableObject
{
    public ObservableCollection<EmoteDocument> Documents { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveDocumentId))]
    private EmoteDocument? _activeDocument;

    public string? ActiveDocumentId => ActiveDocument?.Id;

    public EmoteDocumentSet()
    {
        var first = new EmoteDocument();
        first.SetDefaultTitleIfEmpty();
        Documents.Add(first);
        ActiveDocument = first;
    }

    public EmoteDocument NewDocument(bool activate = true, string? title = null)
    {
        var doc = new EmoteDocument();
        if (!string.IsNullOrWhiteSpace(title))
            doc.Title = title.Trim();
        else
            doc.SetDefaultTitleIfEmpty();
        Documents.Add(doc);
        if (activate) ActiveDocument = doc;
        return doc;
    }

    /// <summary>Next free "Untitled" / "Untitled N" name so stacked blank
    /// tabs stay distinguishable instead of all reading as duplicates.</summary>
    public string NextUntitledTitle()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Documents)
            if (!string.IsNullOrWhiteSpace(d.Title))
                used.Add(d.Title.Trim());
        if (!used.Contains("Untitled")) return "Untitled";
        for (int n = 2; ; n++)
        {
            var candidate = "Untitled " + n;
            if (!used.Contains(candidate)) return candidate;
        }
    }

    public EmoteDocument? Find(string id)
    {
        foreach (var d in Documents)
            if (string.Equals(d.Id, id, StringComparison.Ordinal))
                return d;
        return null;
    }

    /// <summary>Close <paramref name="doc"/>. Always leaves at least one
    /// document. Returns the document that should become active next.</summary>
    public EmoteDocument CloseDocument(EmoteDocument doc)
    {
        if (Documents.Count <= 1)
        {
            // Reset the sole tab to empty rather than removing it.
            doc.Title = "";
            doc.SetDefaultTitleIfEmpty();
            doc.IsDirty = false;
            doc.LoadedModelPath = null;
            doc.TimelineCaptureJson = null;
            doc.TimelineTime = 0;
            doc.TimelineDuration = 4;
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

    public void Activate(EmoteDocument doc)
    {
        if (!Documents.Contains(doc)) return;
        ActiveDocument = doc;
    }

    /// <summary>Remove a document outright (no "reset the sole tab" behaviour
    /// of <see cref="CloseDocument"/>). Used when an emote's chrome tab is
    /// navigated to another section so the orphaned doc can't resurrect as a
    /// duplicate tab. No-op for the last remaining document.</summary>
    public bool RemoveDocument(EmoteDocument doc)
    {
        if (Documents.Count <= 1) return false;
        var idx = Documents.IndexOf(doc);
        if (idx < 0) return false;
        var wasActive = ReferenceEquals(ActiveDocument, doc);
        Documents.Remove(doc);
        if (wasActive)
            ActiveDocument = Documents[Math.Clamp(idx, 0, Documents.Count - 1)];
        return true;
    }
}
