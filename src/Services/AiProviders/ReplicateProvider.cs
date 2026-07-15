// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FiveOS.Services.AiProviders;

/// <summary>
/// Replicate gateway — runs Microsoft TRELLIS for image-to-3D. Pay per
/// second of GPU time; one token unlocks dozens of community 3D models if
/// you ever swap the version. Image-only here for v1 (text-to-3D models on
/// Replicate are too fragmented to expose with a single button).
///
/// Flow:
///   POST /v1/models/{owner}/{name}/predictions  (json: { input })
///     → { id, status, urls.get }
///   GET  /v1/predictions/{id}                   → { status, output }
///
/// Output is typically an array of file URLs; we pick the .glb.
/// </summary>
public sealed class ReplicateProvider : IAiProvider
{
    public string Id => "replicate";
    public string DisplayName => "Replicate · TRELLIS";
    public string Tagline => "Pay-per-GPU-second — image → 3D via Microsoft TRELLIS";
    public string TokenKey => "replicate_api_token";
    public string ConsoleUrl => "https://replicate.com/account/api-tokens";
    public string TokenHelpText => "Get a token at replicate.com/account/api-tokens (billing required).";
    public bool SupportsImage => true;
    public bool SupportsText => false;

    private const string Base = "https://api.replicate.com/";

    /// <summary>Replicate model that runs TRELLIS image-to-3D. Latest
    /// version is resolved server-side when you POST to the model path.</summary>
    private const string ModelOwner = "firtoz";
    private const string ModelName = "trellis";

    public async Task GenerateFromImageAsync(
        string apiKey, string imagePath, string targetGlbPath,
        IProgress<GenerationStep>? progress, CancellationToken cancel)
    {
        using var http = MakeClient(apiKey);

        var bytes = await File.ReadAllBytesAsync(imagePath, cancel);
        var dataUri = $"data:{HttpHelpers.GuessImageMime(imagePath)};base64,{Convert.ToBase64String(bytes)}";

        progress?.Report(new GenerationStep("Submitting to Replicate…", 0));
        var body = new { input = new { images = new[] { dataUri } } };
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"v1/models/{ModelOwner}/{ModelName}/predictions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, cancel);
        var text = await resp.Content.ReadAsStringAsync(cancel);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Replicate returned {(int)resp.StatusCode}: {HttpHelpers.Trim(text)}");

        using var doc = JsonDocument.Parse(text);
        var predictionId = TryString(doc.RootElement, "id")
            ?? throw new InvalidOperationException($"Replicate had no prediction id: {HttpHelpers.Trim(text)}");

        var glbUrl = await PollAsync(http, predictionId, progress, cancel);

        progress?.Report(new GenerationStep("Downloading GLB…", 0.95));
        await HttpHelpers.DownloadPresignedAsync(glbUrl, targetGlbPath, cancel);
    }

    public Task GenerateFromTextAsync(
        string apiKey, string prompt, string targetGlbPath,
        IProgress<GenerationStep>? progress, CancellationToken cancel)
        => throw new NotSupportedException("Replicate (TRELLIS) is image-only in FiveOS.");

    private static async Task<string> PollAsync(
        HttpClient http, string predictionId,
        IProgress<GenerationStep>? progress, CancellationToken cancel)
    {
        var delay = TimeSpan.FromSeconds(3);
        var phase = 0.05;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            using var resp = await http.GetAsync($"v1/predictions/{predictionId}", cancel);
            var text = await resp.Content.ReadAsStringAsync(cancel);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Replicate poll returned {(int)resp.StatusCode}: {HttpHelpers.Trim(text)}");

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var status = TryString(root, "status") ?? "starting";

            if (status == "succeeded")
            {
                if (!root.TryGetProperty("output", out var output))
                    throw new InvalidOperationException("Replicate succeeded but no output.");
                var glb = ExtractGlbUrl(output)
                    ?? throw new InvalidOperationException("Replicate succeeded but no .glb in output.");
                return glb;
            }
            if (status is "failed" or "canceled")
            {
                var err = TryString(root, "error") ?? "(no message)";
                throw new InvalidOperationException($"Replicate prediction {status}: {err}");
            }

            phase = Math.Min(0.9, phase + 0.04);
            progress?.Report(new GenerationStep($"Generating mesh on Replicate ({status})…", phase));
            await Task.Delay(delay, cancel);
        }
    }

    private static string? ExtractGlbUrl(JsonElement output)
    {
        switch (output.ValueKind)
        {
            case JsonValueKind.String:
                var s = output.GetString();
                return s != null && s.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ? s : null;
            case JsonValueKind.Array:
                foreach (var el in output.EnumerateArray())
                {
                    var u = el.GetString();
                    if (u != null && u.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                        return u;
                }
                return null;
            case JsonValueKind.Object:
                foreach (var prop in output.EnumerateObject())
                {
                    var url = ExtractGlbUrl(prop.Value);
                    if (url != null) return url;
                }
                return null;
            default:
                return null;
        }
    }

    private static HttpClient MakeClient(string apiKey)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(Base),
            Timeout = TimeSpan.FromMinutes(15),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", apiKey);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FiveOS-tool");
        return http;
    }

    private static string? TryString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
