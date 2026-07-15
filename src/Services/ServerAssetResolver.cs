// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace FiveOS.Services;

/// <summary>
/// Resolves a txAdmin asset reference ("resourceName/fileName") to an absolute
/// path under the user's configured server resources folder
/// (<see cref="UserSettings.LoadServerResourceFolder"/>).
///
/// FiveM resource folders are commonly grouped under transparent bracket
/// category folders (<c>[streamables]/[mapping]/&lt;resource&gt;/stream/&lt;file&gt;</c>),
/// and the file can live under <c>stream/</c>, <c>data/</c>, or anywhere nested —
/// so nothing about the path is hardcoded. We recursively locate the resource
/// folder by name, then recursively locate the file inside it, with a
/// whole-tree filename fallback only when the resource folder name doesn't
/// match anything. Results are cached because each lookup walks the tree and
/// <see cref="UserSettings"/> itself does no caching.
/// </summary>
public static class ServerAssetResolver
{
    private static readonly ConcurrentDictionary<string, string?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static string? _cacheRoot;
    private static readonly object _rootLock = new();

    // IgnoreInaccessible skips locked/permission-denied dirs instead of
    // throwing mid-walk; skipping reparse points avoids symlink/junction loops.
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>The currently-configured resources root, or null when server
    /// mode isn't set up / the folder is missing.</summary>
    public static string? ServerRoot()
    {
        var root = UserSettings.LoadServerResourceFolder();
        return (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root)) ? root : null;
    }

    /// <summary>Drop any cached lookups (e.g. after the user points FiveOS at a
    /// different server folder).</summary>
    public static void InvalidateCache()
    {
        lock (_rootLock) { _cache.Clear(); _cacheRoot = null; }
    }

    /// <summary>
    /// Resolve a reference to an absolute file path, or null if no server
    /// folder is configured or the file can't be found.
    /// </summary>
    public static string? Resolve(string resourceName, string fileName)
    {
        var root = ServerRoot();
        if (root == null) return null;

        // Invalidate the cache if the configured root changed under us.
        lock (_rootLock)
        {
            if (!string.Equals(_cacheRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                _cache.Clear();
                _cacheRoot = root;
            }
        }

        // NUL separator — illegal in both resource and file names, so the key
        // can't collide the way a bare "{res}{file}" concatenation could
        // (e.g. ("foo","bar") vs ("foob","ar")).
        var key = resourceName + "\0" + fileName;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var resolved = ResolveUncached(root, resourceName, Path.GetFileName(fileName));
        _cache[key] = resolved;
        return resolved;
    }

    private static string? ResolveUncached(string root, string resourceName, string fileName)
    {
        try
        {
            // Stage A — find the resource folder by exact name, anywhere under
            // the root (descends [streamables]/[mapping]/… transparently). We
            // pass "*" and match names ourselves to stay case-insensitive and
            // immune to any glob-special chars in the resource name.
            var resDir = Directory
                .EnumerateDirectories(root, "*", WalkOptions)
                .FirstOrDefault(d => string.Equals(
                    Path.GetFileName(d), resourceName, StringComparison.OrdinalIgnoreCase));

            // Stage B — the file lives inside its resource folder. If we located
            // the folder, trust it: returning a same-named file from a DIFFERENT
            // resource would be a mis-map, so we don't broaden the search here.
            if (resDir != null)
            {
                return Directory
                    .EnumerateFiles(resDir, "*", WalkOptions)
                    .FirstOrDefault(f => string.Equals(
                        Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
            }

            // Fallback (the resource folder name matched nothing) — search the
            // whole tree for the filename, biasing toward a path that contains
            // the resource name as a segment.
            var matches = Directory
                .EnumerateFiles(root, "*", WalkOptions)
                .Where(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0) return null;

            var biased = matches.FirstOrDefault(p => p
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(seg => string.Equals(seg, resourceName, StringComparison.OrdinalIgnoreCase)));
            return biased ?? matches[0];
        }
        catch (Exception ex)
        {
            FosLogger.Warn("txadmin", $"asset resolve failed for {resourceName}/{fileName}", ex);
            return null;
        }
    }
}
