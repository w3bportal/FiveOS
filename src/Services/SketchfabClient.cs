// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FiveOS.Services;

/// <summary>
/// Thin Sketchfab v3 API client for the "Import from URL" flow.
///
/// Authentication note: Sketchfab uses <c>Authorization: Token &lt;api_token&gt;</c>
/// (not <c>Bearer</c>). The token comes from
/// <a href="https://sketchfab.com/settings/password">sketchfab.com/settings/password</a>
/// → API tokens.
///
/// Download note: <c>GET /v3/models/{uid}/download</c> returns presigned
/// AWS URLs for each format (gltf, source, usdz). The URLs expire after
/// ~30s, so we kick off the actual download immediately.
/// </summary>
public sealed class SketchfabClient : IDisposable
{
    public const string TokenKey = "sketchfab_api_token";

    private readonly HttpClient _http;

    public SketchfabClient(string token)
    {
        _http = new HttpClient
        {
            // Sketchfab API is at api.sketchfab.com, model pages at sketchfab.com.
            BaseAddress = new Uri("https://api.sketchfab.com/v3/"),
            Timeout = TimeSpan.FromMinutes(5),  // big models can be 100+ MB
        };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Token", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FiveOS-tool");
    }

    public void Dispose() => _http.Dispose();

    public sealed record ModelInfo(
        string Uid,
        string Name,
        string Author,
        string LicenseLabel,
        bool IsDownloadable,
        long? FileSizeBytes,
        long? VertexCount,
        long? FaceCount);

    /// <summary>Quality bands for triangle count, used to colour-code the
    /// "is this model FiveM-ready?" hint in the import dialog.</summary>
    public enum GeometryQuality { Excellent, Good, Heavy, Bad }

    /// <summary>Bucket a triangle count into a quality band. Ranges defer
    /// to <see cref="MeshThresholds"/> for the prop limits so the import
    /// dialog and the in-viewport health banner can never disagree.
    /// "Excellent" maps to ≤ half the recommended budget — a comfortably
    /// optimized model that needs no further decimation.</summary>
    public static GeometryQuality ClassifyTriangles(long? tris)
    {
        if (tris is null) return GeometryQuality.Good;
        var t = tris.Value;
        if (t < MeshThresholds.PropRecommendedTris / 2) return GeometryQuality.Excellent;
        if (t < MeshThresholds.PropWarnTris)            return GeometryQuality.Good;
        if (t < MeshThresholds.PropFailTris)            return GeometryQuality.Heavy;
        return GeometryQuality.Bad;
    }

    /// <summary>
    /// Pull the 32-char hex UID out of a Sketchfab URL.
    ///
    /// Supported shapes:
    ///   https://sketchfab.com/models/&lt;uid&gt;
    ///   https://sketchfab.com/3d-models/&lt;slug&gt;-&lt;uid&gt;
    ///   https://sketchfab.com/3d-models/&lt;slug&gt;-&lt;uid&gt;/embed
    ///   sketchfab.com/3d-models/&lt;slug&gt;-&lt;uid&gt;
    ///   bare 32-char UID
    /// </summary>
    public static string? ParseUid(string urlOrUid)
    {
        if (string.IsNullOrWhiteSpace(urlOrUid)) return null;
        var s = urlOrUid.Trim();

        // Bare UID — accept as-is.
        if (Regex.IsMatch(s, @"^[a-fA-F0-9]{32}$"))
            return s.ToLowerInvariant();

        // Pull the last 32-hex-char run anywhere in the string. The /3d-models/
        // slug ends with "-{uid}", and /models/{uid} is a clean tail — both
        // collapse to "find the trailing hex chunk".
        var m = Regex.Match(s, @"[a-fA-F0-9]{32}");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>Fetch model metadata. Throws on HTTP errors.</summary>
    public async Task<ModelInfo> GetInfoAsync(string uid, CancellationToken cancel = default)
    {
        using var resp = await _http.GetAsync($"models/{uid}", cancel);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Sketchfab returned {(int)resp.StatusCode} {resp.ReasonPhrase} for /v3/models/{uid}.");

        await using var stream = await resp.Content.ReadAsStreamAsync(cancel);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancel);
        var root = doc.RootElement;

        var name = TryString(root, "name") ?? "(untitled)";
        var isDl = root.TryGetProperty("isDownloadable", out var dl) && dl.GetBoolean();
        string author = "(unknown)";
        if (root.TryGetProperty("user", out var user))
            author = TryString(user, "displayName") ?? TryString(user, "username") ?? author;
        string lic = "(unknown license)";
        if (root.TryGetProperty("license", out var licE))
            lic = TryString(licE, "label") ?? TryString(licE, "slug") ?? lic;
        long? size = null;
        if (root.TryGetProperty("archives", out var arch)
            && arch.TryGetProperty("gltf", out var gltfA)
            && gltfA.TryGetProperty("size", out var sz)
            && sz.ValueKind == JsonValueKind.Number)
            size = sz.GetInt64();

        // Sketchfab's v3 model object exposes total mesh counts at the
        // root — vertexCount + faceCount. We use these to colour-code the
        // "FiveM-friendly?" warning in the import dialog *before* the
        // user commits to downloading a 50 MB archive.
        long? verts = null, faces = null;
        if (root.TryGetProperty("vertexCount", out var vc) && vc.ValueKind == JsonValueKind.Number)
            verts = vc.GetInt64();
        if (root.TryGetProperty("faceCount", out var fc) && fc.ValueKind == JsonValueKind.Number)
            faces = fc.GetInt64();

        return new ModelInfo(uid, name, author, lic, isDl, size, verts, faces);
    }

    /// <summary>
    /// Download the glTF archive into <paramref name="targetDir"/> and return
    /// the absolute path of the resulting <c>scene.gltf</c> (or <c>scene.glb</c>)
    /// inside it. Reports 0..1 progress as bytes flow in.
    /// </summary>
    public async Task<string> DownloadGlbAsync(
        string uid, string targetDir,
        IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        // Step 1: ask Sketchfab for the presigned glTF archive URL.
        using var dlResp = await _http.GetAsync($"models/{uid}/download", cancel);
        if (dlResp.StatusCode == System.Net.HttpStatusCode.Forbidden ||
            dlResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                "This model isn't downloadable with the current API token. " +
                "Either the model is viewable-only, or your token doesn't have access.");
        if (!dlResp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Sketchfab returned {(int)dlResp.StatusCode} {dlResp.ReasonPhrase} for /v3/models/{uid}/download.");

        await using var dlStream = await dlResp.Content.ReadAsStreamAsync(cancel);
        using var dlDoc = await JsonDocument.ParseAsync(dlStream, cancellationToken: cancel);
        if (!dlDoc.RootElement.TryGetProperty("gltf", out var gltf)
            || !gltf.TryGetProperty("url", out var urlE))
            throw new InvalidOperationException("No glTF download URL in Sketchfab response.");
        var presigned = urlE.GetString()
            ?? throw new InvalidOperationException("Sketchfab glTF URL is empty.");

        // Step 2: stream the presigned ZIP (S3-hosted, no auth header).
        Directory.CreateDirectory(targetDir);
        var zipPath = Path.Combine(targetDir, "model.zip");
        using (var bareHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        using (var zipResp = await bareHttp.GetAsync(presigned, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            zipResp.EnsureSuccessStatusCode();
            var total = zipResp.Content.Headers.ContentLength;
            await using var src = await zipResp.Content.ReadAsStreamAsync(cancel);
            await using var dst = File.Create(zipPath);
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, cancel)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), cancel);
                copied += read;
                if (total is long t && t > 0)
                    progress?.Report(copied / (double)t);
            }
        }

        // Step 3: extract. Sketchfab's glTF archive contains scene.gltf at root.
        var extractDir = Path.Combine(targetDir, "extracted");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        // Find the entrypoint. Prefer scene.gltf, then scene.glb, then any *.gltf/*.glb.
        var glb = FindFirst(extractDir, "scene.gltf")
                  ?? FindFirst(extractDir, "scene.glb")
                  ?? FindFirstByExt(extractDir, ".gltf")
                  ?? FindFirstByExt(extractDir, ".glb")
                  ?? throw new InvalidOperationException(
                      "Sketchfab archive didn't contain a .gltf/.glb file.");
        return glb;
    }

    // ─────────────── helpers ───────────────

    private static string? TryString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? FindFirst(string dir, string name)
    {
        foreach (var p in Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories))
            return p;
        return null;
    }

    private static string? FindFirstByExt(string dir, string ext)
    {
        foreach (var p in Directory.EnumerateFiles(dir, "*" + ext, SearchOption.AllDirectories))
            return p;
        return null;
    }
}
