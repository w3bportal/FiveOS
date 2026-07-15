// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Net.Http;

namespace FiveOS.Services.AiProviders;

/// <summary>
/// Shared low-level helpers for AI-provider clients. Each provider does its
/// own auth + payload formatting; these only handle the bits that are
/// genuinely identical across providers (downloading a presigned URL,
/// guessing image MIME, trimming error bodies for status messages).
/// </summary>
internal static class HttpHelpers
{
    public static string GuessImageMime(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };

    public static string Trim(string s) =>
        s.Length > 400 ? s[..400] + "…" : s;

    /// <summary>
    /// Stream a presigned-URL download to disk. Most providers return S3
    /// presigned URLs that fail if you forward your auth header, so this
    /// uses a fresh client.
    /// </summary>
    public static async Task DownloadPresignedAsync(
        string url, string targetPath, CancellationToken cancel)
    {
        using var bare = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var resp = await bare.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        resp.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await using var src = await resp.Content.ReadAsStreamAsync(cancel);
        await using var dst = File.Create(targetPath);
        await src.CopyToAsync(dst, cancel);
    }
}
