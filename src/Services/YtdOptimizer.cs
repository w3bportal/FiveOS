// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using CodeWalker.GameFiles;
using CodeWalker.Utils;

namespace FiveOS.Services;

/// <summary>
/// Shrinks RAGE texture dictionaries (.ytd) — the textures FiveM streams for
/// props, clothing, and vehicles. Each texture is optionally downscaled and
/// re-encoded to a compact block-compressed format so the resource costs less
/// stream memory.
///
/// Everything here is built on permissively-licensed pieces: the .ytd is read
/// and written through CodeWalker.Core (MIT), and the actual pixel re-encode is
/// done by Microsoft's texconv (MIT, DirectXTex). This class is the FiveOS glue
/// that decides <i>what</i> to shrink and drives those tools.
/// </summary>
public sealed class YtdOptimizer
{
    /// <param name="DownSize">Halve a qualifying texture once (ignored when
    ///   <paramref name="MaxSize"/> is set — the cap supersedes it).</param>
    /// <param name="FormatOptimization">Re-encode toward the smallest sensible
    ///   BCn format instead of round-tripping the original format.</param>
    /// <param name="OptimizeSizeThreshold">Process a texture when width+height
    ///   is at least this. Default 8192 (i.e. 4K).</param>
    /// <param name="OnlyOversized">Skip files whose physical size is under 16 MB
    ///   (batch mode only).</param>
    /// <param name="BackupRoot">Mirror originals here before overwriting; null
    ///   to skip backups.</param>
    /// <param name="MaxSize">Hard ceiling on the longest side (px). 0 = no cap.
    ///   When set, a processed texture is halved repeatedly until it fits.</param>
    public sealed record Options(
        bool DownSize,
        bool FormatOptimization,
        ushort OptimizeSizeThreshold,
        bool OnlyOversized,
        string? BackupRoot,
        int MaxSize = 0);

    public sealed record FileResult(
        string Path,
        float VirtualMbBefore,
        float PhysicalMbBefore,
        float PhysicalMbAfter,
        int TexturesOptimized,
        bool Changed,
        bool Skipped,
        string? Error);

    public sealed record StatsSnapshot(
        int FilesCount,
        int OversizedCount,
        float VirtualMb,
        float PhysicalMb);

    public static Options DefaultOptions() => new(
        DownSize: true,
        FormatOptimization: false,
        OptimizeSizeThreshold: 8192,
        OnlyOversized: false,
        BackupRoot: null);

    private const uint Rsc7Magic = 0x37435352;   // "RSC7" little-endian
    private const int OversizedPhysMb = 16;      // batch "only oversized" cutoff

    // ─────────────── Batch (directory) API ───────────────

    /// <summary>Count every .ytd under a folder and total the sizes its RSC7
    /// headers report (MB). Used to preview a batch job before running it.</summary>
    public StatsSnapshot ComputeStats(string inputDirectory, IProgress<double>? progress = null)
    {
        var files = Directory.GetFiles(inputDirectory, "*.ytd", SearchOption.AllDirectories);
        if (files.Length == 0) return new StatsSnapshot(0, 0, 0, 0);

        float virt = 0, phys = 0;
        int oversized = 0;
        for (int i = 0; i < files.Length; i++)
        {
            var (v, p) = ReadRsc7Sizes(files[i]);
            virt += v;
            phys += p;
            if (p > OversizedPhysMb) oversized++;
            progress?.Report((i + 1) / (double)files.Length);
        }
        return new StatsSnapshot(files.Length, oversized, virt, phys);
    }

    /// <summary>Optimize every .ytd under <paramref name="inputDirectory"/>.
    /// Per-file outcomes are streamed back through <paramref name="onFile"/>.</summary>
    public List<FileResult> Optimize(
        string inputDirectory,
        Options opts,
        Action<FileResult>? onFile = null,
        IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        var results = new List<FileResult>();
        var files = Directory.GetFiles(inputDirectory, "*.ytd", SearchOption.AllDirectories);
        if (files.Length == 0) return results;

        var texconv = LocateTexconv();
        for (int i = 0; i < files.Length; i++)
        {
            cancel.ThrowIfCancellationRequested();
            FileResult r;
            try { r = ProcessOne(files[i], inputDirectory, opts, texconv); }
            catch (Exception ex) { r = new FileResult(files[i], 0, 0, 0, 0, false, false, ex.Message); }

            results.Add(r);
            onFile?.Invoke(r);
            progress?.Report((i + 1) / (double)files.Length);
        }
        return results;
    }

    private FileResult ProcessOne(string path, string root, Options opts, string texconv)
    {
        var (virtMb, physMb) = ReadRsc7Sizes(path);

        // In batch mode skip files that are already small (when asked), and any
        // file with no readable RSC7 header (nothing meaningful to size/shrink).
        bool tooSmall = opts.OnlyOversized && physMb < OversizedPhysMb;
        bool unreadable = virtMb == 0 && physMb == 0;
        if (tooSmall || unreadable)
            return new FileResult(path, virtMb, physMb, physMb, 0, false, true, null);

        var ytd = LoadYtd(path);
        var textures = ytd.TextureDict?.Textures?.data_items;
        if (textures == null)
            return new FileResult(path, virtMb, physMb, physMb, 0, false, false, null);

        int touched = 0;
        bool backedUp = false;
        for (int i = 0; i < textures.Length; i++)
        {
            if (ShrinkTexture(textures, i, opts, texconv, () => Backup(path, root, opts.BackupRoot, ref backedUp)))
                touched++;
        }

        float physAfter = physMb;
        if (touched > 0)
        {
            File.WriteAllBytes(path, ytd.Save());
            (_, physAfter) = ReadRsc7Sizes(path);
        }
        return new FileResult(path, virtMb, physMb, physAfter, touched, touched > 0, false, null);
    }

    // ─────────────── Single-file / dictionary API ───────────────

    /// <summary>Optimize one .ytd in place — no batch skip rule, so a
    /// hand-picked file is always processed. Used by the right-click Optimize.</summary>
    public static FileResult OptimizeFile(string path, Options opts)
    {
        var (virtMb, physMb) = ReadRsc7Sizes(path);
        try
        {
            var ytd = LoadYtd(path);
            int touched = OptimizeTextureDictionary(ytd.TextureDict, opts);
            float physAfter = physMb;
            if (touched > 0)
            {
                // Mirror the original before overwriting it in place. OptimizeFile
                // rewrites the whole file at once (no per-texture callback), so the
                // batch Backup() path doesn't apply here — copy it ourselves. A
                // failed backup throws and is caught below, leaving the source
                // untouched instead of destroyed.
                if (opts.BackupRoot != null)
                {
                    Directory.CreateDirectory(opts.BackupRoot);
                    File.Copy(path, Path.Combine(opts.BackupRoot, Path.GetFileName(path)), overwrite: true);
                }
                File.WriteAllBytes(path, ytd.Save());
                (_, physAfter) = ReadRsc7Sizes(path);
            }
            return new FileResult(path, virtMb, physMb, physAfter, touched, touched > 0, false, null);
        }
        catch (Exception ex)
        {
            return new FileResult(path, virtMb, physMb, physMb, 0, false, false, ex.Message);
        }
    }

    /// <summary>Optimize the textures of an already-loaded dictionary in place
    /// (e.g. the one embedded in a drawable's shader group). Returns how many
    /// texture entries were rewritten. Per-texture failures are swallowed so one
    /// bad image can't abort the whole drawable.</summary>
    public static int OptimizeTextureDictionary(TextureDictionary? td, Options opts)
    {
        var textures = td?.Textures?.data_items;
        if (textures == null) return 0;

        var texconv = LocateTexconv();
        int touched = 0;
        for (int i = 0; i < textures.Length; i++)
        {
            try { if (ShrinkTexture(textures, i, opts, texconv, null)) touched++; }
            catch { /* leave this texture as-is */ }
        }
        return touched;
    }

    // ─────────────── Per-texture work ───────────────

    /// <summary>Decide whether a single texture slot should be rewritten and, if
    /// so, do it. Returns true when the slot was replaced.</summary>
    private static bool ShrinkTexture(Texture[] slots, int i, Options opts, string texconv, Action? onWillChange)
    {
        var tex = slots[i];
        if (tex == null) return false;

        // Script render targets corrupt if re-encoded compressed — just expand
        // them to plain RGBA so they stay usable.
        bool isScriptTarget = tex.Name?.ToLowerInvariant().Contains("script_rt") == true;
        if (isScriptTarget)
        {
            if (!IsBlockCompressed(tex.Format)) return false;
            onWillChange?.Invoke();
            return ReEncode(tex, "R8G8B8A8_UNORM", applyResize: false, opts, texconv);
        }

        // Everything else: only touch it once it's big enough to be worth it.
        if (tex.Width + tex.Height < opts.OptimizeSizeThreshold) return false;
        onWillChange?.Invoke();
        var fmt = opts.FormatOptimization ? CompactFormat(tex.Format) : SameFamilyFormat(tex.Format);
        return ReEncode(tex, fmt, applyResize: true, opts, texconv);
    }

    /// <summary>Resize (per <paramref name="opts"/>) and re-encode a texture to
    /// <paramref name="targetFormat"/> using texconv, updating the Texture in
    /// place with the original name/hash preserved. Returns true only when the
    /// texture actually changed — a failed export or a would-be no-op leaves the
    /// slot untouched and must not count as optimized work.</summary>
    private static bool ReEncode(Texture tex, string targetFormat, bool applyResize, Options opts, string texconv)
    {
        ushort w0 = tex.Width, h0 = tex.Height;
        byte lv0 = tex.Levels;
        var (w1, h1, lv1) = TargetShape(w0, h0, lv0, applyResize, opts);

        // Same size, same mips, and the target is the texture's own format:
        // texconv would only recompress the pixels lossily and report work that
        // never happened. Leave the texture alone.
        if (w1 == w0 && h1 == h0 && lv1 == lv0 && targetFormat == SameFamilyFormat(tex.Format))
            return false;

        // Export the pixels at their ORIGINAL size, THEN set the target size so
        // texconv downscales the source we just wrote (order matters — exporting
        // after the resize would write a header that lies about the pixel data).
        var work = ExportDds(tex);
        if (work.Bytes == null)
        {
            DeleteDir(work.Dir);
            return false;
        }
        tex.Width = w1; tex.Height = h1; tex.Levels = lv1;
        try
        {
            RunTexconv(tex, targetFormat, work.Path, texconv);
            return true;
        }
        catch
        {
            // Restore the header we touched so a texconv failure doesn't ship a
            // half-resized texture with mismatched data.
            tex.Width = w0; tex.Height = h0; tex.Levels = lv0;
            throw;
        }
        finally { DeleteDir(work.Dir); }
    }

    /// <summary>Compute the post-resize dimensions and mip count without touching
    /// the texture: a hard cap halves repeatedly to fit; otherwise a single
    /// optional halve. Dimensions stay powers of two so mips stay valid. The mip
    /// count is only ever LOWERED — capped at a 4x4 smallest mip (BC blocks are
    /// 4x4, and sub-4px mips trip some hardware paths) — never raised, so a
    /// mipless texture doesn't gain a fabricated mip chain and grow.</summary>
    private static (ushort W, ushort H, byte Levels) TargetShape(
        ushort w, ushort h, byte levels, bool applyResize, Options opts)
    {
        if (!applyResize) return (w, h, levels);

        if (opts.MaxSize > 0)
        {
            while (Math.Max(w, h) > opts.MaxSize && w > 4 && h > 4)
            {
                w /= 2;
                h /= 2;
            }
        }
        else if (opts.DownSize)
        {
            w /= 2;
            h /= 2;
        }

        int cap = Math.Max(1, (int)Math.Log(Math.Min(w, h), 2) - 1);
        if (levels > cap) levels = (byte)cap;
        return (w, h, levels);
    }

    private readonly record struct DdsWork(string Dir, string Path, byte[]? Bytes);

    private static DdsWork ExportDds(Texture tex)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FiveOS", "ytd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "temp.dds");
        byte[]? bytes;
        try { bytes = DDSIO.GetDDSFile(tex); }
        catch { return new DdsWork(dir, file, null); }
        File.WriteAllBytes(file, bytes);
        return new DdsWork(dir, file, bytes);
    }

    /// <summary>Drive texconv over the exported .dds at the target size/format,
    /// then read the result back onto the live Texture.</summary>
    private static void RunTexconv(Texture tex, string targetFormat, string ddsPath, string texconv)
    {
        var psi = new ProcessStartInfo
        {
            FileName = texconv,
            WorkingDirectory = Path.GetDirectoryName(ddsPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // texconv CLI: explicit width/height/mips, target format, dithered
        // BC1-3 compression, overwrite in place.
        foreach (var a in new[]
                 {
                     "-w", tex.Width.ToString(),
                     "-h", tex.Height.ToString(),
                     "-m", tex.Levels.ToString(),
                     "-f", targetFormat,
                     "-bc", "d",
                     "-y",
                     Path.GetFileName(ddsPath),
                 })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start texconv.");

        // Drain both pipes before waiting or a full buffer deadlocks the wait.
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        // BC7 encodes on machines without DirectCompute run on the CPU and can
        // take minutes for a 4096² source — give texconv real headroom.
        if (!proc.WaitForExit(300_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new InvalidOperationException($"texconv timed out for {Path.GetFileName(ddsPath)}.");
        }
        var err = errTask.GetAwaiter().GetResult();
        _ = outTask.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"texconv failed ({proc.ExitCode}) for {Path.GetFileName(ddsPath)}: {Clip(err, 200)}");

        var newTex = DDSIO.GetTexture(File.ReadAllBytes(ddsPath));
        tex.Data   = newTex.Data;
        tex.Depth  = newTex.Depth;
        tex.Levels = newTex.Levels;
        tex.Format = newTex.Format;
        tex.Stride = newTex.Stride;
    }

    // ─────────────── Format helpers ───────────────

    private static readonly Regex CompressedRx = new("D3DFMT_(DXT|ATI|BC)[0-9]", RegexOptions.Compiled);
    private static bool IsBlockCompressed(TextureFormat f) => CompressedRx.IsMatch(f.ToString());

    // Compact re-encode: block-compress what isn't already. Formats that are
    // already BCn stay in their own family — BC2/3/5/7 are the same 8 bpp as
    // BC3, so "compacting" them saves nothing and destroys data (DXT3/BC7
    // alpha, ATI2 normal channels). A8 is pure alpha at the same 8 bpp as BC3,
    // so it stays too. Uncompressed color gets BC3 with alpha, BC1 without.
    private static string CompactFormat(TextureFormat f) => f switch
    {
        _ when IsBlockCompressed(f) => SameFamilyFormat(f),
        TextureFormat.D3DFMT_A8       => "A8_UNORM",
        TextureFormat.D3DFMT_A1R5G5B5
        or TextureFormat.D3DFMT_A8B8G8R8
        or TextureFormat.D3DFMT_A8R8G8B8 => "BC3_UNORM",
        _ => "BC1_UNORM",
    };

    // Keep the texture's own format family when just resizing.
    private static string SameFamilyFormat(TextureFormat f) => f switch
    {
        TextureFormat.D3DFMT_DXT1     => "BC1_UNORM",
        TextureFormat.D3DFMT_DXT3     => "BC2_UNORM",
        TextureFormat.D3DFMT_DXT5     => "BC3_UNORM",
        TextureFormat.D3DFMT_ATI1     => "BC4_UNORM",
        TextureFormat.D3DFMT_ATI2     => "BC5_UNORM",
        TextureFormat.D3DFMT_BC7      => "BC7_UNORM",
        TextureFormat.D3DFMT_A1R5G5B5 => "B5G5R5A1_UNORM",
        TextureFormat.D3DFMT_A8       => "A8_UNORM",
        TextureFormat.D3DFMT_A8B8G8R8 => "R8G8B8A8_UNORM",
        TextureFormat.D3DFMT_L8       => "R8_UNORM",
        TextureFormat.D3DFMT_A8R8G8B8 => "B8G8R8A8_UNORM",
        TextureFormat.D3DFMT_X8R8G8B8 => "B8G8R8X8_UNORM",
        _ => "BC3_UNORM",
    };

    // ─────────────── Backups ───────────────

    private static void Backup(string filePath, string root, string? backupRoot, ref bool done)
    {
        if (done || backupRoot == null) return;
        // Do NOT swallow a failed copy: the caller mirrors the original here
        // *before* overwriting it in place, so a backup that fails must abort the
        // overwrite (the batch loop in Optimize() turns the throw into a per-file
        // Error) rather than silently destroy the source with no backup on disk.
        var dst = Path.Combine(backupRoot, Path.GetRelativePath(root, filePath));
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(filePath, dst, overwrite: true);
        done = true;   // only after a verified copy
    }

    // ─────────────── .ytd I/O (CodeWalker.Core, MIT) ───────────────

    private static YtdFile LoadYtd(string path)
    {
        var data = File.ReadAllBytes(path);
        var entry = BuildEntry(new FileInfo(path).Name, path, ref data);
        return RpfFile.GetFile<YtdFile>(entry, data);
    }

    /// <summary>Wrap raw bytes in an RpfFileEntry so CodeWalker can parse them,
    /// decompressing first when the file is an RSC7 resource.</summary>
    private static RpfFileEntry BuildEntry(string name, string path, ref byte[] data)
    {
        uint magic = data?.Length > 4 ? BitConverter.ToUInt32(data, 0) : 0;
        RpfFileEntry entry;
        if (magic == Rsc7Magic)
        {
            entry = RpfFile.CreateResourceFileEntry(ref data!, 0);
            data = ResourceBuilder.Decompress(data);
        }
        else
        {
            var bin = new RpfBinaryFileEntry { FileSize = (uint)(data?.Length ?? 0) };
            bin.FileUncompressedSize = bin.FileSize;
            entry = bin;
        }
        entry.Name = name;
        entry.NameLower = name.ToLowerInvariant();
        entry.NameHash = JenkHash.GenHash(entry.NameLower);
        entry.ShortNameHash = JenkHash.GenHash(Path.GetFileNameWithoutExtension(entry.NameLower));
        entry.Path = path;
        return entry;
    }

    /// <summary>Read the RSC7 header's virtual + physical page sizes (in MB).
    /// Returns (0, 0) for a file that isn't an RSC7 resource.</summary>
    public static (float VirtMb, float PhysMb) ReadRsc7Sizes(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            if (br.ReadBytes(4) is not { Length: 4 } magic ||
                System.Text.Encoding.ASCII.GetString(magic) != "RSC7")
                return (0, 0);
            _ = br.ReadInt32();                 // version
            int virtFlag = br.ReadInt32();
            int physFlag = br.ReadInt32();
            const float toMb = 1f / 1024f / 1024f;
            return (PagesToBytes(virtFlag) * toMb, PagesToBytes(physFlag) * toMb);
        }
        catch { return (0, 0); }
    }

    // RAGE page-flag -> byte count (documented RSC7 layout, same as OpenIV/CodeWalker).
    private static long PagesToBytes(int flag) =>
        (((flag >> 17) & 0x7f)
         + (((flag >> 11) & 0x3f) << 1)
         + (((flag >> 7) & 0xf) << 2)
         + (((flag >> 5) & 0x3) << 3)
         + (((flag >> 4) & 0x1) << 4))
        * (0x2000L << (flag & 0xF));

    // ─────────────── misc ───────────────

    private static string LocateTexconv()
    {
        try
        {
            var rt = Path.Combine(RuntimeAssets.EngineDir, "tools", "texconv.exe");
            if (File.Exists(rt)) return Path.GetFullPath(rt);
        }
        catch { /* RuntimeAssets can throw; fall back to loose paths */ }

        foreach (var rel in new[] { "tools/texconv.exe", "Engine/tools/texconv.exe", "../tools/texconv.exe" })
        {
            var c = Path.Combine(AppContext.BaseDirectory, rel);
            if (File.Exists(c)) return Path.GetFullPath(c);
        }
        return "texconv.exe";
    }

    private static void DeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* swallow */ }
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
