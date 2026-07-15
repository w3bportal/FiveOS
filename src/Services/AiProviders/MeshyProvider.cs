// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FiveOS.Services.AiProviders;

/// <summary>
/// Meshy AI — image-to-3D + text-to-3D. Free tier with daily credits;
/// paid tier for higher polycount + commercial use.
///
/// Endpoint shape (note the DIFFERENT versions per endpoint — image-to-3d is
/// still v1, but text-to-3d moved to v2; calling v1/text-to-3d now 404s with
/// "NoMatchingRoute"):
///   POST /openapi/v1/image-to-3d      →  { "result": "&lt;task_id&gt;" }
///   POST /openapi/v2/text-to-3d       →  { "result": "&lt;task_id&gt;" }
///   GET  /openapi/&lt;ver&gt;/&lt;kind&gt;/{id}  →  { status, progress, model_urls: { glb, ... } }
/// </summary>
public sealed class MeshyProvider : IAiProvider
{
    public string Id => "meshy";
    public string DisplayName => "Meshy AI";
    public string Tagline => "Free tier — image + text → 3D, ~60s, PBR optional";
    public string TokenKey => "meshy_api_token";
    public string ConsoleUrl => "https://www.meshy.ai/api";
    public string TokenHelpText => "Get a key at meshy.ai/api → API Keys (free tier available).";
    public bool SupportsImage => true;
    public bool SupportsText => true;
    public bool SupportsTexturing => true;

    private const string Base = "https://api.meshy.ai/openapi/";

    // The most recent text-to-3d PREVIEW task id. Meshy's refine (texturing)
    // stage references it. Held on the (singleton) provider instance — fine for
    // a single-user desktop app. Only text-to-3d previews are texturable;
    // image-to-3d already returns a textured mesh.
    private string? _lastTextPreviewTaskId;

    public Task GenerateFromImageAsync(
        string apiKey, string imagePath, string targetGlbPath,
        IProgress<GenerationStep>? progress, CancellationToken cancel)
        => GenerateAsync(apiKey, ImagePayload(imagePath), "v1/image-to-3d", targetGlbPath, progress, cancel);

    public Task GenerateFromTextAsync(
        string apiKey, string prompt, string targetGlbPath,
        IProgress<GenerationStep>? progress, CancellationToken cancel)
        => GenerateAsync(apiKey, TextPayload(prompt), "v2/text-to-3d", targetGlbPath, progress, cancel);

    private static object ImagePayload(string imagePath)
    {
        var bytes = File.ReadAllBytes(imagePath);
        var dataUri = $"data:{HttpHelpers.GuessImageMime(imagePath)};base64,{Convert.ToBase64String(bytes)}";
        return new
        {
            image_url = dataUri,
            ai_model = "meshy-6",
            topology = "triangle",
            target_polycount = 30000,
            should_remesh = true,
            should_texture = true,
            enable_pbr = false,
        };
    }

    private static object TextPayload(string prompt) => new
    {
        prompt,
        mode = "preview",   // "preview" is fast + free-tier-friendly
        // NB: art_style is NOT supported by meshy-6 (some combos 400) — omit it.
        ai_model = "meshy-6",
        // meshy-6 defaults should_remesh=false, which makes topology/target_polycount
        // no-ops; enable it so we actually get triangle topology at our polycount.
        should_remesh = true,
        topology = "triangle",
        target_polycount = 30000,
    };

    private async Task GenerateAsync(
        string apiKey, object payload, string apiPath, string targetGlbPath,
        IProgress<GenerationStep>? progress, CancellationToken cancel)
    {
        using var http = MakeClient(apiKey);

        progress?.Report(new GenerationStep("Submitting to Meshy…", 0));
        using var req = new HttpRequestMessage(HttpMethod.Post, apiPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, cancel);
        var text = await resp.Content.ReadAsStringAsync(cancel);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Meshy returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {HttpHelpers.Trim(text)}");

        using var doc = JsonDocument.Parse(text);
        var taskId = TryString(doc.RootElement, "result") ?? TryString(doc.RootElement, "id")
            ?? throw new InvalidOperationException(
                $"Meshy create response had no task id: {HttpHelpers.Trim(text)}");

        // Remember the preview id so a later "Texture with AI" can refine it.
        if (apiPath == "v2/text-to-3d") _lastTextPreviewTaskId = taskId;

        // Poll.
        var glbUrl = await PollAsync(http, apiPath, taskId, progress, cancel);

        progress?.Report(new GenerationStep("Downloading GLB…", 0.95));
        await HttpHelpers.DownloadPresignedAsync(glbUrl, targetGlbPath, cancel);
    }

    /// <summary>AI-texture the last text-to-3d preview via Meshy's refine stage
    /// (POST v2/text-to-3d with mode=refine + the preview task id).</summary>
    public async Task TextureLastAsync(
        string apiKey, string targetGlbPath,
        IProgress<GenerationStep>? progress, CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(_lastTextPreviewTaskId))
            throw new InvalidOperationException(
                "Nothing to texture yet — generate a model from a text prompt first.");

        using var http = MakeClient(apiKey);
        progress?.Report(new GenerationStep("Texturing with AI…", 0));

        var payload = new
        {
            mode = "refine",
            preview_task_id = _lastTextPreviewTaskId,
            enable_pbr = false,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "v2/text-to-3d")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, cancel);
        var text = await resp.Content.ReadAsStringAsync(cancel);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Meshy texture returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {HttpHelpers.Trim(text)}");

        using var doc = JsonDocument.Parse(text);
        var refineId = TryString(doc.RootElement, "result") ?? TryString(doc.RootElement, "id")
            ?? throw new InvalidOperationException(
                $"Meshy refine response had no task id: {HttpHelpers.Trim(text)}");

        var glbUrl = await PollAsync(http, "v2/text-to-3d", refineId, progress, cancel);
        progress?.Report(new GenerationStep("Downloading textured GLB…", 0.95));
        await HttpHelpers.DownloadPresignedAsync(glbUrl, targetGlbPath, cancel);
    }

    private static async Task<string> PollAsync(
        HttpClient http, string apiPath, string taskId,
        IProgress<GenerationStep>? progress, CancellationToken cancel)
    {
        var delay = TimeSpan.FromSeconds(3);
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            using var resp = await http.GetAsync($"{apiPath}/{taskId}", cancel);
            var text = await resp.Content.ReadAsStringAsync(cancel);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Meshy poll returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {HttpHelpers.Trim(text)}");

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var status = TryString(root, "status") ?? "UNKNOWN";
            var pct = root.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt32() : 0;
            progress?.Report(new GenerationStep($"Generating mesh… {pct}%", pct / 100.0 * 0.9));

            if (status == "SUCCEEDED")
            {
                if (!root.TryGetProperty("model_urls", out var urls) || urls.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Meshy SUCCEEDED but had no model_urls.");
                var glb = TryString(urls, "glb")
                    ?? throw new InvalidOperationException("Meshy SUCCEEDED but had no .glb URL.");
                // Surface Meshy's rendered preview so the UI can show it before
                // the user imports the mesh into the main viewer.
                var thumb = TryString(root, "thumbnail_url");
                progress?.Report(new GenerationStep("Preview ready — review it below", 0.9, thumb));
                return glb;
            }
            if (status is "FAILED" or "CANCELED")
            {
                string? err = null;
                if (root.TryGetProperty("task_error", out var te) && te.ValueKind == JsonValueKind.Object)
                    err = TryString(te, "message");
                throw new InvalidOperationException(
                    $"Meshy task {status.ToLowerInvariant()}: {err ?? "(no message)"}");
            }

            await Task.Delay(delay, cancel);
        }
    }

    private static HttpClient MakeClient(string apiKey)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(Base),
            Timeout = TimeSpan.FromMinutes(10),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FiveOS-tool");
        return http;
    }

    private static string? TryString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
