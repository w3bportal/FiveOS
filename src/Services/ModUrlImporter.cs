// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace FiveOS.Services;

/// <summary>
/// Paste-a-link importer for the SP-car converter: takes a gta5-mods.com mod
/// page URL (or a direct archive URL), downloads the mod, extracts it, and
/// hands back a folder the converter can chew on (it finds the dlc.rpf(s)
/// itself).
///
/// gta5-mods flow (verified with plain HTTP — no cookies or JS needed):
///   mod page  →  href="/vehicles/&lt;slug&gt;/download/&lt;id&gt;" (take the
///   highest id = newest version)  →  that page embeds the real file at
///   https://files.gta5-mods.com/uploads/…/&lt;file&gt;.rar|.zip|.7z|.oiv
///
/// Mods hosted OFF-site (mega.nz / drive.google / mediafire links instead of
/// a files.gta5-mods.com upload) can't be fetched non-interactively — those
/// return a clear "download it manually" error instead of a mystery failure.
///
/// Extraction uses SharpCompress's forward-only ReaderFactory so solid RAR
/// archives (the de-facto standard for car mods) work; entry paths are
/// checked against traversal before anything touches disk.
/// </summary>
public sealed class ModUrlImporter
{
    public sealed record Result(bool Success, string? ExtractedDir, string? ModName, string? Error);

    private static readonly Regex VersionedDownload = new(
        @"href=""(/[a-z0-9\-]+/[a-z0-9\-_%.]+/download/(\d+))""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FileHostUrl = new(
        @"https://files\.gta5-mods\.com/[^""'<>\s]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ArchiveExts = { ".zip", ".rar", ".7z", ".oiv" };

    private static readonly string[] OffsiteHosts =
        { "mega.nz", "mega.co.nz", "drive.google.com", "mediafire.com", "dropbox.com", "patreon.com" };

    public async Task<Result> ImportAsync(string url, Action<string> log, CancellationToken ct = default)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new Result(false, null, null, "That doesn't look like a link. Paste the mod's gta5-mods.com page URL.");

        if (OffsiteHosts.Any(h => uri.Host.EndsWith(h, StringComparison.OrdinalIgnoreCase)))
            return new Result(false, null, null,
                $"{uri.Host} needs a browser (captcha/consent pages). Download the mod manually, then drop the archive's extracted folder — or its dlc.rpf — onto this tab.");

        using var http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        })
        { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) FiveOS-mod-import");

        try
        {
            // Site quirk (verified): the versioned download page 302s back to
            // the mod page unless the request carries a Referer from the site.
            async Task<string> GetPageAsync(string pageUrl, string? referer)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                if (referer != null) req.Headers.Referrer = new Uri(referer);
                using var r = await http.SendAsync(req, ct);
                r.EnsureSuccessStatusCode();
                return await r.Content.ReadAsStringAsync(ct);
            }

            // ── Resolve the actual archive URL ───────────────────────────
            string fileUrl;
            string? fileReferer = null;
            if (ArchiveExts.Any(e => uri.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                || uri.Host.Equals("files.gta5-mods.com", StringComparison.OrdinalIgnoreCase))
            {
                fileUrl = url;
            }
            else if (uri.Host.EndsWith("gta5-mods.com", StringComparison.OrdinalIgnoreCase))
            {
                log("Reading the mod page…");
                var page = await GetPageAsync(url, null);

                // A mod page links versioned download pages; the newest id wins.
                // (A pasted /download/<id> URL skips straight to the next hop.)
                string dlPage = url;
                if (!Regex.IsMatch(uri.AbsolutePath, @"/download/\d+$"))
                {
                    var versions = VersionedDownload.Matches(page)
                        .Select(m => (Path: m.Groups[1].Value, Id: long.Parse(m.Groups[2].Value)))
                        .OrderByDescending(v => v.Id)
                        .ToList();
                    if (versions.Count == 0)
                        return new Result(false, null, null,
                            "No download found on that page — the mod may host its file off-site (mega/drive). Download it manually and drop the folder here.");
                    dlPage = $"{uri.Scheme}://{uri.Host}{versions[0].Path}";
                }
                page = await GetPageAsync(dlPage, url);

                var m = FileHostUrl.Match(page);
                if (!m.Success)
                    return new Result(false, null, null,
                        "That version's file isn't hosted on gta5-mods (off-site link). Download it manually and drop the folder here.");
                fileUrl = WebUtility.HtmlDecode(m.Value);
                fileReferer = dlPage;
            }
            else
                return new Result(false, null, null,
                    $"Unsupported site ({uri.Host}). Paste a gta5-mods.com mod link, or a direct .zip/.rar/.7z/.oiv URL.");

            // ── Download ─────────────────────────────────────────────────
            var workDir = Path.Combine(Path.GetTempPath(), "FiveOS", "modimport",
                Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(workDir);

            using var fileReq = new HttpRequestMessage(HttpMethod.Get, fileUrl);
            if (fileReferer != null) fileReq.Headers.Referrer = new Uri(fileReferer);
            using var resp = await http.SendAsync(fileReq, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var fileName = SanitizeFileName(
                resp.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? Path.GetFileName(WebUtility.UrlDecode(new Uri(fileUrl).AbsolutePath)));
            if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName))) fileName += ".zip";
            var archivePath = Path.Combine(workDir, fileName);

            var total = resp.Content.Headers.ContentLength;
            log($"Downloading {fileName}" + (total is long t ? $" ({t / 1024 / 1024} MB)…" : "…"));
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(archivePath))
            {
                var buf = new byte[1 << 16];
                long read = 0; int lastPct = -1; int n;
                while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    read += n;
                    if (total is long tt)
                    {
                        var pct = (int)(read * 100 / tt);
                        if (pct >= lastPct + 10) { lastPct = pct; log($"Downloading… {pct}%"); }
                    }
                }
            }

            // ── Extract ──────────────────────────────────────────────────
            log("Extracting archive…");
            var extractDir = Path.Combine(workDir, "extracted");
            Directory.CreateDirectory(extractDir);
            await Task.Run(() =>
            {
                using var stream = File.OpenRead(archivePath);
                using var reader = ReaderFactory.OpenReader(stream);
                while (reader.MoveToNextEntry())
                {
                    ct.ThrowIfCancellationRequested();
                    if (reader.Entry.IsDirectory) continue;
                    var key = reader.Entry.Key ?? "";
                    // Zip-slip guard: never let an entry escape extractDir.
                    var target = Path.GetFullPath(Path.Combine(extractDir, key.Replace('/', '\\')));
                    if (!target.StartsWith(Path.GetFullPath(extractDir) + Path.DirectorySeparatorChar,
                                           StringComparison.OrdinalIgnoreCase))
                        continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    reader.WriteEntryToFile(target, new ExtractionOptions { Overwrite = true });
                }
            }, ct);

            // Keep the temp footprint sane — the archive served its purpose.
            try { File.Delete(archivePath); } catch { /* best-effort */ }

            var rpfs = Directory.EnumerateFiles(extractDir, "*.rpf", SearchOption.AllDirectories).Count();
            log($"Extracted {Path.GetFileNameWithoutExtension(fileName)} — {rpfs} .rpf file(s) found.");
            if (rpfs == 0)
                return new Result(false, extractDir, null,
                    "The archive contains no .rpf — it may be a replace-style mod (loose yft/ytd) rather than an add-on. Check its readme.");

            return new Result(true, extractDir, Path.GetFileNameWithoutExtension(fileName), null);
        }
        catch (OperationCanceledException) { return new Result(false, null, null, "Import canceled."); }
        catch (HttpRequestException ex)
        {
            return new Result(false, null, null, $"Download failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new Result(false, null, null, $"Import failed: {ex.Message}");
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "mod.zip" : name;
    }
}
