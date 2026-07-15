// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FiveOS.Services.AiProviders;

/// <summary>
/// Rodin (Hyper3D / Deemos) — premium image + text → 3D. Paid only;
/// produces high-detail meshes well-suited to characters and hard-surface
/// assets.
///
/// Flow:
///   POST /api/v2/rodin       (multipart: images[] OR prompt) → { uuid, jobs: { subscription_key } }
///   POST /api/v2/status      (json: { subscription_key })    → { jobs: [{ status: Done|Failed|Generating }] }
///   POST /api/v2/download    (json: { task_uuid })           → { list: [{ name, url }] }
/// </summary>
public sealed class RodinProvider : IAiProvider
{
    public string Id => "rodin";
    public string DisplayName => "Rodin (Hyper3D)";
    public string Tagline => "Paid — premium quality image + text → 3D, characters & hard-surface";
    public string TokenKey => "rodin_api_token";
    public string ConsoleUrl => "https://hyperhuman.deemos.com/api";
    public string TokenHelpText => "Get a key at hyperhuman.deemos.com → API (paid plan required).";
    public bool SupportsImage => true;
    public bool SupportsText => true;

    private const string Base = "https://hyperhuman.deemos.com/";

    public async Task GenerateFromImageAsync(
        string apiKey, string imagePath, string targetGlbPath,
        IProgress<GenerationStep>? progress, CancellationToken cancel)
    {
        using var http = MakeClient(apiKey);

        progress?.Report(new GenerationStep("Submitting image to Rodin…", 0));
        using var form = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(imagePath, cancel);
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(HttpHelpers.GuessImageMime(imagePath));
        form.Add(part, "images", Path.GetFileName(imagePath));
        form.Add(new StringContent("Regular"), "tier");
        form.Add(new StringContent("glb"), "geometry_file_format");

        var (uuid, subscriptionKey) = await CreateJobAsync(http, form, cancel);
        var glbUrl = await PollAndDownloadAsync(http, uuid, subscriptionKey, progress, cancel);

        progress?.Report(new GenerationStep("Downloading GLB…", 0.95));
        await HttpHelpers.DownloadPresignedAsync(glbUrl, targetGlbPath, cancel);
    }

    public async Task GenerateFromTextAsync(
        string apiKey, string prompt, string targetGlbPath,
        IProgress<GenerationStep>? progress, CancellationToken cancel)
    {
        using var http = MakeClient(apiKey);

        progress?.Report(new GenerationStep("Submitting prompt to Rodin…", 0));
        using var form = new MultipartFormDataContent
        {
            { new StringContent(prompt), "prompt" },
            { new StringContent("Regular"), "tier" },
            { new StringContent("glb"), "geometry_file_format" },
        };

        var (uuid, subscriptionKey) = await CreateJobAsync(http, form, cancel);
        var glbUrl = await PollAndDownloadAsync(http, uuid, subscriptionKey, progress, cancel);

        progress?.Report(new GenerationStep("Downloading GLB…", 0.95));
        await HttpHelpers.DownloadPresignedAsync(glbUrl, targetGlbPath, cancel);
    }

    private static async Task<(string uuid, string subscriptionKey)> CreateJobAsync(
        HttpClient http, MultipartFormDataContent form, CancellationToken cancel)
    {
        using var resp = await http.PostAsync("api/v2/rodin", form, cancel);
        var text = await resp.Content.ReadAsStringAsync(cancel);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Rodin returned {(int)resp.StatusCode}: {HttpHelpers.Trim(text)}");

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var uuid = TryString(root, "uuid")
            ?? throw new InvalidOperationException($"Rodin response had no uuid: {HttpHelpers.Trim(text)}");
        if (!root.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Rodin response had no jobs: {HttpHelpers.Trim(text)}");
        var sub = TryString(jobs, "subscription_key")
            ?? throw new InvalidOperationException($"Rodin response had no subscription_key: {HttpHelpers.Trim(text)}");
        return (uuid, sub);
    }

    private static async Task<string> PollAndDownloadAsync(
        HttpClient http, string uuid, string subscriptionKey,
        IProgress<GenerationStep>? progress, CancellationToken cancel)
    {
        var delay = TimeSpan.FromSeconds(4);
        var phase = 0.05;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            using var statusReq = new HttpRequestMessage(HttpMethod.Post, "api/v2/status")
            {
                Content = JsonContent(new { subscription_key = subscriptionKey }),
            };
            using var statusResp = await http.SendAsync(statusReq, cancel);
            var statusText = await statusResp.Content.ReadAsStringAsync(cancel);
            if (!statusResp.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Rodin status returned {(int)statusResp.StatusCode}: {HttpHelpers.Trim(statusText)}");

            using var doc = JsonDocument.Parse(statusText);
            if (doc.RootElement.TryGetProperty("jobs", out var jobsArr) && jobsArr.ValueKind == JsonValueKind.Array)
            {
                var states = jobsArr.EnumerateArray()
                    .Select(j => TryString(j, "status") ?? "")
                    .ToList();
                if (states.All(s => s == "Done"))
                {
                    progress?.Report(new GenerationStep("Fetching download URLs…", 0.92));
                    using var dlReq = new HttpRequestMessage(HttpMethod.Post, "api/v2/download")
                    {
                        Content = JsonContent(new { task_uuid = uuid }),
                    };
                    using var dlResp = await http.SendAsync(dlReq, cancel);
                    var dlText = await dlResp.Content.ReadAsStringAsync(cancel);
                    if (!dlResp.IsSuccessStatusCode)
                        throw new HttpRequestException(
                            $"Rodin download returned {(int)dlResp.StatusCode}: {HttpHelpers.Trim(dlText)}");

                    using var dlDoc = JsonDocument.Parse(dlText);
                    if (!dlDoc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException($"Rodin download had no list: {HttpHelpers.Trim(dlText)}");
                    foreach (var entry in list.EnumerateArray())
                    {
                        var name = TryString(entry, "name") ?? "";
                        if (name.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                        {
                            var url = TryString(entry, "url")
                                ?? throw new InvalidOperationException("Rodin glb entry had no url.");
                            return url;
                        }
                    }
                    throw new InvalidOperationException("Rodin job done but no .glb in download list.");
                }
                if (states.Any(s => s.Equals("Failed", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Rodin job failed (see hyperhuman dashboard for detail).");
            }

            phase = Math.Min(0.9, phase + 0.05);
            progress?.Report(new GenerationStep("Generating mesh…", phase));
            await Task.Delay(delay, cancel);
        }
    }

    private static StringContent JsonContent(object o) =>
        new(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

    private static HttpClient MakeClient(string apiKey)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(Base),
            Timeout = TimeSpan.FromMinutes(15),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FiveOS-tool");
        return http;
    }

    private static string? TryString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
