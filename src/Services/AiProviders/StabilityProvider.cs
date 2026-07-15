// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace FiveOS.Services.AiProviders;

/// <summary>
/// Stability AI Stable Fast 3D — image-only, synchronous. Posts a multipart
/// form with the source image and gets a GLB back in the response body
/// (Content-Type: model/gltf-binary). No polling, no presigned URL step.
///
/// Endpoint: POST /v2beta/3d/stable-fast-3d
/// Auth:     Authorization: Bearer &lt;api_key&gt;
/// </summary>
public sealed class StabilityProvider : IAiProvider
{
    public string Id => "stability";
    public string DisplayName => "Stability AI · Stable Fast 3D";
    public string Tagline => "Subscription credits — image → 3D, single-shot";
    public string TokenKey => "stability_api_token";
    public string ConsoleUrl => "https://platform.stability.ai/account/keys";
    public string TokenHelpText => "Get a key at platform.stability.ai → Account → API Keys (subscription credits required).";
    public bool SupportsImage => true;
    public bool SupportsText => false;

    private const string Endpoint = "https://api.stability.ai/v2beta/3d/stable-fast-3d";

    public async Task GenerateFromImageAsync(
        string apiKey, string imagePath, string targetGlbPath,
        IProgress<GenerationStep>? progress, CancellationToken cancel)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FiveOS-tool");
        // The endpoint advertises model/gltf-binary on success, JSON on error.
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("model/gltf-binary"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        progress?.Report(new GenerationStep("Submitting to Stability AI…", 0));
        using var form = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(imagePath, cancel);
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(HttpHelpers.GuessImageMime(imagePath));
        form.Add(part, "image", Path.GetFileName(imagePath));

        progress?.Report(new GenerationStep("Generating mesh (synchronous, ~10–30s)…", 0.4));
        using var resp = await http.PostAsync(Endpoint, form, cancel);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancel);
            throw new HttpRequestException(
                $"Stability returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {HttpHelpers.Trim(err)}");
        }

        progress?.Report(new GenerationStep("Saving GLB…", 0.9));
        Directory.CreateDirectory(Path.GetDirectoryName(targetGlbPath)!);
        await using var src = await resp.Content.ReadAsStreamAsync(cancel);
        await using var dst = File.Create(targetGlbPath);
        await src.CopyToAsync(dst, cancel);
    }

    public Task GenerateFromTextAsync(
        string apiKey, string prompt, string targetGlbPath,
        IProgress<GenerationStep>? progress, CancellationToken cancel)
        => throw new NotSupportedException("Stability AI (Stable Fast 3D) is image-only.");
}
