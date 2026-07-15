// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FiveOS.Services;

/// <summary>
/// Checks GitHub Releases for a newer FiveOS build and compares the latest
/// release tag against the running assembly version. Unauthenticated (no
/// token in the binary), so the <see cref="Repo"/> whose Releases are read
/// must be PUBLIC. If you keep the source repo private, point
/// <see cref="Owner"/>/<see cref="Repo"/> at a small public releases repo
/// that only holds the published FiveOS.exe.
/// </summary>
public static class UpdateChecker
{
    // Releases are read from this GitHub repo. It must be PUBLIC so the
    // unauthenticated API call + the asset download work from every user's
    // machine. GitHub's "latest" endpoint skips drafts and pre-releases.
    private const string Owner = "w3bportal";
    private const string Repo  = "FiveOS";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    public sealed record CheckResult(
        Status Status,
        Version Current,
        Version? Latest,
        string? LatestTag,
        string? ReleaseUrl,
        string? DownloadUrl,
        string? ReleaseName,
        string? Notes,
        string? Error,
        string? Sha256 = null);

    public enum Status
    {
        UpToDate,
        UpdateAvailable,
        NoReleases,
        Error,
    }

    public static Version CurrentVersion()
    {
        var raw = Assembly.GetExecutingAssembly().GetName().Version;
        return raw ?? new Version(0, 0, 0, 0);
    }

    public static async Task<CheckResult> CheckAsync(CancellationToken cancel = default)
    {
        var current = CurrentVersion();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // GitHub's API requires a User-Agent; the Accept header pins the API version.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FiveOS-update-check");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            http.DefaultRequestHeaders.CacheControl =
                new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };

            using var resp = await http.GetAsync(LatestReleaseApi, cancel);
            // 404 = no published release yet, or the repo isn't public.
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new CheckResult(Status.NoReleases, current, null, null, null, null, null, null,
                    "No release has been published yet.");
            if (!resp.IsSuccessStatusCode)
                return new CheckResult(Status.Error, current, null, null, null, null, null, null,
                    $"GitHub returned {(int)resp.StatusCode} {resp.ReasonPhrase}.");

            var body = await resp.Content.ReadAsStringAsync(cancel);
            GitHubReleaseDto? release;
            try
            {
                release = JsonSerializer.Deserialize<GitHubReleaseDto>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            }
            catch (JsonException ex)
            {
                return new CheckResult(Status.Error, current, null, null, null, null, null, null,
                    $"GitHub release response was not valid JSON: {ex.Message}");
            }
            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
                return new CheckResult(Status.Error, current, null, null, null, null, null, null,
                    "Latest GitHub release has no tag.");

            var latest = ParseVersionTag(release.TagName);
            if (latest == null)
                return new CheckResult(Status.Error, current, null, release.TagName,
                    release.HtmlUrl, null, release.Name, release.Body,
                    $"Couldn't parse the release tag \"{release.TagName}\" as a version.");

            // Prefer the attached FiveOS .exe asset for the one-click install.
            var exeAsset = release.Assets?.FirstOrDefault(a =>
                a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);

            var status = latest > current ? Status.UpdateAvailable : Status.UpToDate;
            return new CheckResult(status, current, latest, release.TagName,
                release.HtmlUrl, exeAsset?.BrowserDownloadUrl, release.Name, release.Body, null);
        }
        catch (TaskCanceledException)
        {
            return new CheckResult(Status.Error, current, null, null, null, null, null, null,
                "Update check timed out. Check your connection and try again.");
        }
        catch (Exception ex)
        {
            return new CheckResult(Status.Error, current, null, null, null, null, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Stream the new exe from <paramref name="downloadUrl"/> to
    /// <c>FiveOS.exe.new</c> next to the running binary, then spawn a
    /// detached cmd script that waits for this process to exit, swaps
    /// the file in place, relaunches FiveOS, and self-deletes. Caller
    /// should call <see cref="System.Windows.Application.Shutdown()"/>
    /// immediately after this returns so the swap script can proceed.
    /// </summary>
    public static async Task DownloadAndInstallAsync(
        string downloadUrl,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancel = default,
        string? expectedSha256 = null)
    {
        // Security: only ever fetch an executable over TLS. A plaintext (or
        // scheme-less) download_url is a MITM code-execution vector — refuse it
        // rather than run whatever bytes come back.
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var dlUri) ||
            !string.Equals(dlUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to download the update over a non-HTTPS URL ({downloadUrl}).");

        var currentExe = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Couldn't resolve the running executable path.");
        var dir = Path.GetDirectoryName(currentExe)
            ?? throw new InvalidOperationException("Running executable has no parent directory.");
        var newExe = Path.Combine(dir, "FiveOS.exe.new");

        // Clean up any leftover from a previous failed attempt.
        try { if (File.Exists(newExe)) File.Delete(newExe); } catch { /* swallow */ }

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FiveOS-update-download");

        using var resp = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;

        await using (var src = await resp.Content.ReadAsStreamAsync(cancel))
        await using (var dst = new FileStream(newExe, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), cancel)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), cancel);
                read += n;
                progress?.Report(new DownloadProgress(read, total));
            }
        }

        // Sanity: the file should be at least a few hundred KB. A
        // ContentLength-less stream that closed at 0 bytes is almost
        // certainly a misconfigured host serving a redirect or HTML
        // error page — bail before we hand a broken exe to the swap.
        var finalSize = new FileInfo(newExe).Length;
        if (finalSize < 100_000)
        {
            try { File.Delete(newExe); } catch { /* swallow */ }
            throw new InvalidOperationException(
                $"Downloaded file is only {finalSize} bytes — the manifest's download_url is probably wrong.");
        }

        // Integrity: if the manifest pinned a SHA-256, the downloaded bytes MUST
        // match before we hand them to the swap script. Guards against a tampered
        // or cache-poisoned binary even when the manifest itself is intact.
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            string actual;
            await using (var fs = File.OpenRead(newExe))
                actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs, cancel));
            if (!actual.Equals(expectedSha256.Trim().Replace("-", ""), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(newExe); } catch { /* swallow */ }
                throw new InvalidOperationException(
                    "Downloaded update failed its SHA-256 integrity check — aborting install.");
            }
        }

        SpawnSwapScript(currentExe, newExe);
    }

    public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes)
    {
        public double? Fraction => TotalBytes is long t && t > 0 ? (double)BytesReceived / t : null;
    }

    /// <summary>
    /// Write and launch a small batch script that waits for the current
    /// FiveOS process to exit, then renames the .new file over the live
    /// exe and relaunches it. Detached via cmd.exe + /c so it survives
    /// the host process shutdown.
    /// </summary>
    private static void SpawnSwapScript(string currentExe, string newExe)
    {
        var pid = Process.GetCurrentProcess().Id;
        var batPath = Path.Combine(Path.GetTempPath(), $"fiveos-update-{Guid.NewGuid():N}.cmd");

        // Quote everything: paths may contain spaces (Program Files, user
        // names with spaces, etc.). Double the % so the file's "%~f0"
        // self-delete trick works when we interpolate.
        var script = new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine("setlocal")
            .AppendLine(":wait")
            .AppendLine($"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL")
            .AppendLine("if \"%ERRORLEVEL%\"==\"0\" (")
            .AppendLine("    timeout /t 1 /nobreak >NUL")
            .AppendLine("    goto wait")
            .AppendLine(")")
            // Brief extra pause: tasklist can report "gone" while the OS
            // is still releasing the exe handle for a beat or two.
            .AppendLine("timeout /t 1 /nobreak >NUL")
            .AppendLine($"move /Y \"{currentExe}\" \"{currentExe}.old\" >NUL 2>&1")
            .AppendLine($"move /Y \"{newExe}\" \"{currentExe}\" >NUL")
            .AppendLine($"start \"\" \"{currentExe}\"")
            .AppendLine($"del \"{currentExe}.old\" >NUL 2>&1")
            // Self-delete: jump out of this script and remove the file.
            .AppendLine("(goto) 2>nul & del \"%~f0\"")
            .ToString();

        File.WriteAllText(batPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{batPath}\"\"",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }

    /// <summary>Parse a tag like "v0.2.3" or "0.2.3-beta" into a
    /// <see cref="Version"/>. Trailing pre-release suffixes are dropped —
    /// build metadata isn't meaningful for the "is this newer?" check.</summary>
    private static Version? ParseVersionTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var trimmed = tag.TrimStart('v', 'V');
        var match = Regex.Match(trimmed, @"^\d+(\.\d+){1,3}");
        if (!match.Success) return null;
        return Version.TryParse(match.Value, out var v) ? v : null;
    }

    // Shape of GitHub's "get latest release" API response (subset we use).
    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
