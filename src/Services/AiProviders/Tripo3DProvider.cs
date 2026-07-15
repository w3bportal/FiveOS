// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FiveOS.Services.AiProviders;

/// <summary>
/// Tripo3D — image + text → 3D. Free tier (~20 credits/day at signup),
/// fast turnaround (~30s), good PBR meshes.
///
/// Two-step image flow:
///   1. POST /v2/openapi/upload      (multipart) → { data: { image_token } }
///   2. POST /v2/openapi/task        (json)      → { data: { task_id } }
/// Then poll GET /v2/openapi/task/{id} → { data: { status, progress, output: { pbr_model } } }
///
/// Text flow skips step 1 and posts type=text_to_model with a prompt directly.
/// </summary>
public sealed class Tripo3DProvider : IAiProvider
{
    public string Id => "tripo3d";
    public string DisplayName => "Tripo3D";
    public string Tagline => "Free tier — image + text → 3D, ~30s, PBR-ready";
    public string TokenKey => "tripo3d_api_token";
    public string ConsoleUrl => "https://platform.tripo3d.ai/api-keys";
    public string TokenHelpText => "Get a key at platform.tripo3d.ai → API Keys (free tier with daily credits).";
    public bool SupportsImage => true;
    public bool SupportsText => true;

    private const string Base = "https://api.tripo3d.ai/v2/openapi/";

    public async Task GenerateFromImageAsync(
        string apiKey, string imagePath, string targetGlbPath,
        IProgress<GenerationStep>? progress, CancellationToken cancel)
    {
        using var http = MakeClient(apiKey);

        progress?.Report(new GenerationStep("Uploading image to Tripo3D…", 0));
        var imageToken = await UploadImageAsync(http, imagePath, cancel);

        var fileType = Path.GetExtension(imagePath).TrimStart('.').ToLowerInvariant();
        if (fileType == "jpg") fileType = "jpeg";
        var taskBody = new
        {
            type = "image_to_model",
            file = new { type = fileType, file_token = imageToken },
            model_version = "v2.0-20240919",
        };

        progress?.Report(new GenerationStep("Submitting to Tripo3D…", 0.05));
        var taskId = await CreateTaskAsync(http, taskBody, cancel);
        var glbUrl = await PollAsync(http, taskId, progress, cancel);

        progress?.Report(new GenerationStep("Downloading GLB…", 0.95));
        await HttpHelpers.DownloadPresignedAsync(glbUrl, targetGlbPath, cancel);
    }

    public async Task GenerateFromTextAsync(
        string apiKey, string prompt, string targetGlbPath,
        IProgress<GenerationStep>? progress, CancellationToken cancel)
    {
        using var http = MakeClient(apiKey);

        var taskBody = new
        {
            type = "text_to_model",
            prompt,
            model_version = "v2.0-20240919",
        };

        progress?.Report(new GenerationStep("Submitting to Tripo3D…", 0));
        var taskId = await CreateTaskAsync(http, taskBody, cancel);
        var glbUrl = await PollAsync(http, taskId, progress, cancel);

        progress?.Report(new GenerationStep("Downloading GLB…", 0.95));
        await HttpHelpers.DownloadPresignedAsync(glbUrl, targetGlbPath, cancel);
    }

    private static async Task<string> UploadImageAsync(
        HttpClient http, string imagePath, CancellationToken cancel)
    {
        using var form = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(imagePath, cancel);
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(HttpHelpers.GuessImageMime(imagePath));
        form.Add(part, "file", Path.GetFileName(imagePath));

        using var resp = await http.PostAsync("upload", form, cancel);
        var text = await resp.Content.ReadAsStringAsync(cancel);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Tripo3D upload returned {(int)resp.StatusCode}: {HttpHelpers.Trim(text)}");

        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("data", out var data))
            throw new InvalidOperationException($"Tripo3D upload had no data: {HttpHelpers.Trim(text)}");
        return TryString(data, "image_token") ?? TryString(data, "file_token")
            ?? throw new InvalidOperationException(
                $"Tripo3D upload had no image_token: {HttpHelpers.Trim(text)}");
    }

    private static async Task<string> CreateTaskAsync(
        HttpClient http, object body, CancellationToken cancel)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "task")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, cancel);
        var text = await resp.Content.ReadAsStringAsync(cancel);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Tripo3D task create returned {(int)resp.StatusCode}: {HttpHelpers.Trim(text)}");

        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("data", out var data))
            throw new InvalidOperationException($"Tripo3D task had no data: {HttpHelpers.Trim(text)}");
        return TryString(data, "task_id")
            ?? throw new InvalidOperationException($"Tripo3D task had no task_id: {HttpHelpers.Trim(text)}");
    }

    private static async Task<string> PollAsync(
        HttpClient http, string taskId,
        IProgress<GenerationStep>? progress, CancellationToken cancel)
    {
        var delay = TimeSpan.FromSeconds(3);
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            using var resp = await http.GetAsync($"task/{taskId}", cancel);
            var text = await resp.Content.ReadAsStringAsync(cancel);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Tripo3D poll returned {(int)resp.StatusCode}: {HttpHelpers.Trim(text)}");

            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("data", out var data))
                throw new InvalidOperationException($"Tripo3D poll had no data: {HttpHelpers.Trim(text)}");

            var status = TryString(data, "status")?.ToLowerInvariant() ?? "unknown";
            var pct = data.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt32() : 0;
            progress?.Report(new GenerationStep($"Generating mesh… {pct}%", pct / 100.0 * 0.9));

            if (status == "success")
            {
                if (!data.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Tripo3D success but no output object.");
                var glb = TryString(output, "pbr_model") ?? TryString(output, "model")
                    ?? throw new InvalidOperationException("Tripo3D success but no model URL.");
                return glb;
            }
            if (status is "failed" or "cancelled" or "banned")
                throw new InvalidOperationException(
                    $"Tripo3D task {status}: {TryString(data, "error_msg") ?? "(no message)"}");

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
