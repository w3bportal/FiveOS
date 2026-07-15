// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeWalker.GameFiles;

namespace FiveOS.Services;

/// <summary>
/// Phase 1 of the "FiveM RPF" converter: packs a loose resource
/// folder (the kind a FiveM server streams — <c>stream/*.ydr/.ytd/.ybn/…</c>
/// plus meta files) into a single OPEN (unencrypted) RPF8 archive that opens
/// cleanly in OpenIV / CodeWalker.
///
/// Packing itself is delegated to CodeWalker.Core's <see cref="RpfFile"/> API:
/// • <c>CreateNew(dir, name, OPEN)</c> — writes an empty archive to disk.
/// • <c>CreateDirectory(parent, name)</c> — adds a TOC directory entry (it does
///   NOT de-dupe, so <see cref="GetOrCreateDir"/> scans existing children first).
/// • <c>CreateFile(dir, name, bytes, overwrite)</c> — auto-detects the RSC7
///   header and stores the bytes as a RAGE *resource* entry (preserving the
///   system/graphics page flags) or a plain *binary* entry. Each call rewrites
///   the header, so the on-disk archive is valid after the last call returns.
///
/// This is the raw packer. Phase 2 (PedDlcScaffolder) layers content.xml /
/// setup2.xml generation on top to emit a singleplayer-mountable dlc.rpf.
/// </summary>
public sealed class RpfPacker
{
    // RSC7 little-endian magic ('R','S','C','7') — same check YtdOptimizer /
    // DrawableOptimizer use to tell a RAGE resource from a plain file.
    private const uint Rsc7Magic = 0x37435352;

    public sealed record Options(
        // false (default): only pack files with a known RAGE extension (stream
        // assets + game data/meta); skip lua/js/manifest/readme/image cruft.
        // true: pack every file except the hard cruft skip-list below.
        bool IncludeAllFiles = false,
        // Drop a single leading "stream\" segment so assets land at the RPF root
        // rather than under stream\ — matches how people expect a packed RPF to
        // look in OpenIV. Only the top-level stream\ folder is flattened.
        bool FlattenStreamFolder = true);

    public sealed record FileResult(
        string SourcePath,
        string ArchivePath,   // path inside the rpf, e.g. "props\chair.ydr"
        long Bytes,
        bool IsResource,      // RSC7 resource vs plain binary
        bool Packed,
        string? Skipped,      // reason if skipped (null when packed/failed)
        string? Error);       // message if it threw (null otherwise)

    public sealed record Result(
        string RpfPath,
        long RpfBytes,
        int Packed,
        int Skipped,
        int Failed,
        IReadOnlyList<FileResult> Files,
        string? Error);       // fatal error that aborted the whole pack

    // RAGE stream assets + common game-data/meta files that belong in an RPF.
    private static readonly HashSet<string> RageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        // drawables / fragments / textures / collision / clips / nav / LOD
        ".ydr", ".ydd", ".yft", ".ytd", ".ybn", ".ycd", ".ynv", ".yld",
        // archetypes / maps / particles / cloth / cover / audio / bounds db
        ".ytyp", ".ymap", ".ypt", ".yed", ".ywr", ".yvr", ".awc", ".ypdb",
        // game data / metadata
        ".meta", ".xml", ".ymt", ".ymf", ".dat", ".rel", ".gxt2", ".nametable",
    };

    // Never pack these — pure FiveM-runtime / VCS / OS cruft with no meaning
    // inside a RAGE archive. Applies even in IncludeAllFiles mode.
    private static readonly HashSet<string> CruftNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "fxmanifest.lua", "__resource.lua", "resource.lua",
        "thumbs.db", "desktop.ini", ".ds_store", ".gitkeep", ".gitattributes",
    };
    private static readonly HashSet<string> CruftExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gitignore", ".db", ".ini", ".log", ".bak",
    };

    /// <summary>
    /// Pack <paramref name="inputFolder"/> into an OPEN RPF at
    /// <paramref name="outputRpfPath"/> (a <c>.rpf</c> extension is appended if
    /// missing; an existing file at that path is overwritten). Never throws for
    /// per-file problems — those land in <see cref="FileResult.Error"/>; the
    /// top-level <see cref="Result.Error"/> is set only on a fatal abort.
    /// </summary>
    public Result Pack(string inputFolder, string outputRpfPath, Options opts, Action<string>? log = null)
    {
        var results = new List<FileResult>();
        try
        {
            if (string.IsNullOrWhiteSpace(inputFolder) || !Directory.Exists(inputFolder))
                return new Result(outputRpfPath, 0, 0, 0, 0, results, $"Input folder not found: {inputFolder}");

            outputRpfPath = Path.GetFullPath(outputRpfPath);
            var outDir = Path.GetDirectoryName(outputRpfPath)
                         ?? throw new ArgumentException("outputRpfPath has no directory.", nameof(outputRpfPath));
            var rpfName = Path.GetFileName(outputRpfPath);
            if (!rpfName.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            {
                rpfName += ".rpf";
                outputRpfPath = Path.Combine(outDir, rpfName);
            }

            Directory.CreateDirectory(outDir);
            if (File.Exists(outputRpfPath)) File.Delete(outputRpfPath);

            log?.Invoke($"Creating {rpfName} (OPEN/unencrypted)…");
            var rpf = RpfFile.CreateNew(outDir, rpfName, RpfEncryption.OPEN);

            var inputFull = Path.GetFullPath(inputFolder).TrimEnd('\\', '/');
            var allFiles = Directory.EnumerateFiles(inputFull, "*", SearchOption.AllDirectories)
                                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                    .ToList();
            log?.Invoke($"Scanning {allFiles.Count} file(s) under {inputFull}…");

            foreach (var src in allFiles)
            {
                var rel = src.Substring(inputFull.Length + 1);
                var name = Path.GetFileName(src);
                var ext = Path.GetExtension(src);

                var skip = ShouldSkip(name, ext, opts);
                if (skip != null)
                {
                    results.Add(new FileResult(src, rel, 0, false, false, skip, null));
                    continue;
                }

                // archive-relative path, '\'-separated; optionally flatten a
                // single leading stream\ so assets sit at the RPF root.
                var archiveRel = rel.Replace('/', '\\');
                if (opts.FlattenStreamFolder &&
                    archiveRel.StartsWith("stream\\", StringComparison.OrdinalIgnoreCase))
                    archiveRel = archiveRel.Substring("stream\\".Length);

                try
                {
                    var data = File.ReadAllBytes(src);
                    var isRsc = IsResource(data);

                    AddFileAtPath(rpf.Root, archiveRel, data);
                    results.Add(new FileResult(src, archiveRel, data.Length, isRsc, true, null, null));
                    log?.Invoke($"  + {archiveRel}  ({(isRsc ? "resource" : "binary")}, {data.Length:N0} B)");
                }
                catch (Exception ex)
                {
                    results.Add(new FileResult(src, archiveRel, 0, false, false, null, ex.Message));
                    log?.Invoke($"  ! {archiveRel}  FAILED: {ex.Message}");
                }
            }

            var rpfBytes = File.Exists(outputRpfPath) ? new FileInfo(outputRpfPath).Length : 0;
            var packed = results.Count(r => r.Packed);
            var skipped = results.Count(r => r.Skipped != null);
            var failed = results.Count(r => r.Error != null);
            log?.Invoke($"Done: {packed} packed, {skipped} skipped, {failed} failed → {rpfName} ({rpfBytes:N0} B)");
            return new Result(outputRpfPath, rpfBytes, packed, skipped, failed, results, null);
        }
        catch (Exception ex)
        {
            return new Result(
                outputRpfPath, 0,
                results.Count(r => r.Packed),
                results.Count(r => r.Skipped != null),
                results.Count(r => r.Error != null),
                results, ex.Message);
        }
    }

    /// <summary>null = pack it; otherwise the human-readable skip reason.</summary>
    private static string? ShouldSkip(string name, string ext, Options opts)
    {
        if (CruftNames.Contains(name)) return "cruft";
        if (CruftExts.Contains(ext)) return "cruft";
        // Nesting a pre-packed .rpf inside the output isn't supported in v1.
        if (string.Equals(ext, ".rpf", StringComparison.OrdinalIgnoreCase))
            return "already an .rpf (nesting unsupported in v1)";
        if (!opts.IncludeAllFiles && !RageExts.Contains(ext))
            return "not a RAGE asset";
        return null;
    }

    /// <summary>True if the bytes start with the RSC7 resource magic.</summary>
    internal static bool IsResource(byte[] data) =>
        data.Length > 4 && BitConverter.ToUInt32(data, 0) == Rsc7Magic;

    /// <summary>
    /// Add <paramref name="data"/> at a <c>\</c>- or <c>/</c>-separated path
    /// under <paramref name="root"/>, creating intermediate directories as
    /// needed. <paramref name="root"/> can be any rpf's <c>Root</c> — including
    /// a nested rpf created via <c>RpfFile.CreateNew(dir, name, …)</c> — which
    /// is how <see cref="PedDlcScaffolder"/> writes into <c>componentpeds.rpf</c>.
    /// CreateFile auto-detects RSC7 (resource vs binary).
    /// </summary>
    internal static RpfFileEntry AddFileAtPath(RpfDirectoryEntry root, string archiveRel, byte[] data)
    {
        var parts = archiveRel.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var leaf = parts[^1];
        var dir = root;
        for (var i = 0; i < parts.Length - 1; i++)
            dir = GetOrCreateDir(dir, parts[i]);
        return RpfFile.CreateFile(dir, leaf, data, overwrite: true);
    }

    /// <summary>Get-or-create a whole <c>\</c>/<c>/</c>-separated directory
    /// chain under <paramref name="root"/>, returning the leaf directory.</summary>
    internal static RpfDirectoryEntry GetOrCreateDirChain(RpfDirectoryEntry root, string relPath)
    {
        var dir = root;
        foreach (var part in relPath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
            dir = GetOrCreateDir(dir, part);
        return dir;
    }

    /// <summary>
    /// CodeWalker's CreateDirectory always appends a new entry, so we scan the
    /// parent's existing children first to keep the tree de-duplicated.
    /// </summary>
    internal static RpfDirectoryEntry GetOrCreateDir(RpfDirectoryEntry parent, string name)
    {
        if (parent.Directories != null)
        {
            foreach (var d in parent.Directories)
                if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                    return d;
        }
        return RpfFile.CreateDirectory(parent, name);
    }
}
