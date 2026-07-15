// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace FiveOS.Services;

/// <summary>
/// Resolves the on-disk location of runtime asset folders (the WebView2
/// viewer bundle and the native ydr-writer engine) regardless of how
/// FiveOS was deployed.
///
/// In a single-file published build there is no <c>Assets/Viewer</c> or
/// <c>Engine</c> folder beside <c>FiveOS.exe</c>; both are embedded as
/// zip resources and extracted on first launch into
/// <c>%LOCALAPPDATA%\FiveOS\runtime\&lt;version&gt;\</c>. In a dev build
/// (F5 from VS) the loose folders next to the build output are used
/// directly so edits to viewer.html or the engine binaries don't require
/// a rebuild.
/// </summary>
internal static class RuntimeAssets
{
    private const string ViewerResource = "FiveOS.viewer.zip";
    private const string EngineResource = "FiveOS.engine.zip";

    private static readonly Lazy<string> _viewerDir = new(() =>
        Resolve(looseRelative: "Assets/Viewer", resourceName: ViewerResource, extractSubdir: "viewer"));

    private static readonly Lazy<string> _engineDir = new(() =>
        Resolve(looseRelative: "Engine", resourceName: EngineResource, extractSubdir: "engine"));

    public static string ViewerDir => _viewerDir.Value;
    public static string EngineDir => _engineDir.Value;

    private static string Resolve(string looseRelative, string resourceName, string extractSubdir)
    {
        // Dev fallback: loose folder next to the exe (e.g. F5 from VS).
        var loose = Path.Combine(
            AppContext.BaseDirectory,
            looseRelative.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(loose) && Directory.EnumerateFileSystemEntries(loose).Any())
            return loose;

        var version = typeof(RuntimeAssets).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FiveOS", "runtime", version);

        var asm = typeof(RuntimeAssets).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded runtime resource '{resourceName}' not found. " +
                "The FiveOS.exe build is incomplete — rebuild from source.");

        // The extraction dir is keyed by the zip's CONTENT HASH, not just the
        // assembly version. Two different exes can share a version string (dev
        // rebuilds are all "1.0.0"); with a shared dir each one deleted and
        // re-extracted over the other on launch, yanking viewer files (e.g.
        // the freemode reference glb) out from under any instance that was
        // still running — which broke .ycd native-space detection mid-session.
        // Hash-keyed dirs coexist instead; stale ones are cheap and are wiped
        // whenever the version folder changes on update.
        var hash = ComputeSha256Hex(stream);
        stream.Position = 0;

        var target = Path.Combine(root, $"{extractSubdir}-{hash[..8].ToLowerInvariant()}");
        var marker = Path.Combine(target, ".extracted");

        if (File.Exists(marker))
        {
            var existing = SafeReadAllText(marker);
            if (string.Equals(existing, hash, StringComparison.OrdinalIgnoreCase))
                return target;
        }

        // Only ever deletes THIS hash's dir (a torn previous extraction).
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            zip.ExtractToDirectory(target, overwriteFiles: true);
        }

        File.WriteAllText(marker, hash);
        return target;
    }

    private static string ComputeSha256Hex(Stream s)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(s);
        return Convert.ToHexString(bytes);
    }

    private static string SafeReadAllText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch { return ""; }
    }
}
