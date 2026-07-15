// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using CommunityToolkit.Mvvm.ComponentModel;
using FiveOS.Services.AiProviders;

namespace FiveOS.ViewModels;

/// <summary>
/// View-model for the Image → 3D tab. Holds the in-flight generation state
/// and the user's selected provider / mode. The actual API calls live in
/// the view's code-behind because they're tightly coupled to the file
/// picker / message-box UX.
///
/// On success the generated GLB is saved to ~/Downloads/FiveOS_meshes/generated/
/// and offered up for the user to open via Reveal in Explorer / Open in 3D-to-Props.
/// </summary>
public partial class ImageTo3DViewModel : ObservableObject
{
    public IReadOnlyList<IAiProvider> Providers { get; }

    public ImageTo3DViewModel()
    {
        Providers = AiProviderRegistry.All;
        _selectedProvider = Providers[0];
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SupportsImage))]
    [NotifyPropertyChangedFor(nameof(SupportsText))]
    [NotifyPropertyChangedFor(nameof(IsImageMode))]
    [NotifyPropertyChangedFor(nameof(IsTextMode))]
    [NotifyPropertyChangedFor(nameof(ProviderTagline))]
    [NotifyPropertyChangedFor(nameof(ShowModeToggle))]
    [NotifyPropertyChangedFor(nameof(ShowTextureButton))]
    private IAiProvider _selectedProvider;

    public bool SupportsImage => SelectedProvider?.SupportsImage ?? false;
    public bool SupportsText => SelectedProvider?.SupportsText ?? false;
    public bool SupportsTexturing => SelectedProvider?.SupportsTexturing ?? false;
    public string ProviderTagline => SelectedProvider?.Tagline ?? "";
    public bool ShowModeToggle => SupportsImage && SupportsText;

    /// <summary>0 = Image, 1 = Text. Snapped back to a supported value when
    /// the provider changes (see OnSelectedProviderChanging).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageMode))]
    [NotifyPropertyChangedFor(nameof(IsTextMode))]
    private int _modeIndex;

    public bool IsImageMode => ModeIndex == 0 && SupportsImage || !SupportsText;
    public bool IsTextMode => ModeIndex == 1 && SupportsText;

    partial void OnSelectedProviderChanged(IAiProvider value)
    {
        // If the user had Text selected and switched to a provider that
        // doesn't support it (or vice-versa), drop back to a supported mode.
        if (ModeIndex == 1 && !value.SupportsText) ModeIndex = 0;
        if (ModeIndex == 0 && !value.SupportsImage) ModeIndex = 1;
    }

    [ObservableProperty] private string _textPrompt = "";

    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private double _generationProgress;
    [ObservableProperty] private string _generationStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastOutput))]
    [NotifyPropertyChangedFor(nameof(ShowTextureButton))]
    private string? _lastOutputPath;

    public bool HasLastOutput => !string.IsNullOrEmpty(LastOutputPath);

    /// <summary>True when the last output can still be AI-textured (a text
    /// preview). Cleared for image results (already textured) and after a
    /// texture pass completes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTextureButton))]
    private bool _lastOutputTexturable;

    /// <summary>Show the "Texture with AI" action.</summary>
    public bool ShowTextureButton => SupportsTexturing && LastOutputTexturable && HasLastOutput;

    /// <summary>URL of the provider's rendered preview image for the finished
    /// model (Meshy thumbnail). Shown in the result card so the user can
    /// preview before importing into the main viewer. Null when the provider
    /// didn't supply one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private string? _previewImageUrl;

    public bool HasPreview => !string.IsNullOrEmpty(PreviewImageUrl);
}
