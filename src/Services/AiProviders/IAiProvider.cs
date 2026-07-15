// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

namespace FiveOS.Services.AiProviders;

/// <summary>
/// One step of an AI generation job. Providers report this through an
/// <see cref="IProgress{T}"/> so the view can render a single status line +
/// progress bar regardless of how many internal phases each provider has.
/// </summary>
/// <param name="Status">Human-readable label, e.g. "Submitting image…",
/// "Generating mesh… 42%", "Downloading GLB…".</param>
/// <param name="Fraction">0..1 overall progress hint. Providers without
/// granular progress emit 0 → coarse phase changes carry the user instead.</param>
/// <param name="PreviewImageUrl">Optional URL of a rendered preview image the
/// provider produced for the finished model (e.g. Meshy's thumbnail_url).
/// When set, the UI shows it so the user can eyeball the result before
/// importing it into the main viewer. Null for providers/steps without one.</param>
public sealed record GenerationStep(string Status, double Fraction, string? PreviewImageUrl = null);

/// <summary>
/// Common surface for every Image/Text → 3D backend. Each implementation
/// owns its own auth header, polling cadence, and download semantics, but
/// from the caller's POV all you do is hand it a key + an input + an output
/// path and await.
///
/// Add a new provider by implementing this and registering in
/// <see cref="AiProviderRegistry"/>.
/// </summary>
public interface IAiProvider
{
    /// <summary>Stable id used in settings.json / SecretStore key suffix.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Short pricing/quality blurb shown under the provider name.</summary>
    string Tagline { get; }

    /// <summary><see cref="SecretStore"/> key under which the API token lives.</summary>
    string TokenKey { get; }

    /// <summary>Where the user gets/manages their token (opened from Settings).</summary>
    string ConsoleUrl { get; }

    /// <summary>One-line "where to get a key" hint shown next to the input.</summary>
    string TokenHelpText { get; }

    bool SupportsImage { get; }
    bool SupportsText { get; }

    /// <summary>True if the provider can add AI textures to the model it most
    /// recently generated (e.g. Meshy's preview→refine stage). Default false.</summary>
    bool SupportsTexturing => false;

    /// <summary>Texture the provider's most recently generated model with AI,
    /// writing the textured GLB to <paramref name="targetGlbPath"/>. Only valid
    /// when <see cref="SupportsTexturing"/> is true and a prior generation ran
    /// this session.</summary>
    Task TextureLastAsync(
        string apiKey,
        string targetGlbPath,
        IProgress<GenerationStep>? progress,
        CancellationToken cancel)
        => throw new NotSupportedException("This provider doesn't support AI texturing.");

    /// <summary>
    /// Generate a GLB from a single image, writing it to <paramref name="targetGlbPath"/>.
    /// Caller pre-validates that <see cref="SupportsImage"/> is true.
    /// </summary>
    Task GenerateFromImageAsync(
        string apiKey,
        string imagePath,
        string targetGlbPath,
        IProgress<GenerationStep>? progress,
        CancellationToken cancel);

    /// <summary>
    /// Generate a GLB from a text prompt, writing it to <paramref name="targetGlbPath"/>.
    /// Caller pre-validates that <see cref="SupportsText"/> is true.
    /// </summary>
    Task GenerateFromTextAsync(
        string apiKey,
        string prompt,
        string targetGlbPath,
        IProgress<GenerationStep>? progress,
        CancellationToken cancel);
}
