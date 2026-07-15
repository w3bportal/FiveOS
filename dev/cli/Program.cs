// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS
//
// FiveOS.Cli — headless command-line front end over the FiveOS service layer.
// A "lite" tool for VPS / server use: no GUI, no viewer, no WebView2 — just the
// optimize / convert / pack pipelines driven from argv. See FiveOS.Cli.csproj
// for the Milestone-1 (Windows spike) build model.

using FiveOS.Services;
using ImageMagick;

namespace FiveOS.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            // Render the ✓ / · / ⚠ status glyphs correctly regardless of the
            // host console codepage. Guarded — a redirected/again-set stream throws.
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected */ }

            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
                return Usage();

            var rest = args[2..];
            return (args[0], args.Length > 1 ? args[1] : "") switch
            {
                ("optimize", "ytd")      => OptimizeYtd(rest),
                ("optimize", "drawable") => OptimizeDrawable(rest),
                ("optimize", "texture")  => OptimizeTexture(rest),
                ("optimize", "mesh")     => OptimizeMesh(rest),
                ("pack",     "rpf")      => PackRpf(rest),
                ("build",    "ped-dlc")  => BuildPedDlc(rest),
                ("build",    "replace")  => BuildReplace(rest),
                ("build",    "addon")    => BuildAddon(rest),
                ("convert",  "prop")     => ConvertProp(rest),
                ("convert",  "vehicle")  => ConvertVehicle(rest),
                ("import",   "mod-url")  => ImportModUrl(rest),
                _ => Unknown(args),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    // ─────────────────────────── optimize ytd ───────────────────────────

    private static int OptimizeYtd(string[] a)
    {
        var (pos, o) = Args.Parse(a, new() { "no-downsize", "format-opt", "only-oversized" });
        if (pos.Count == 0) return Err("usage: fiveos optimize ytd <dir|file.ytd> [--max-size 1024] [--threshold 2048] [--no-downsize] [--format-opt] [--only-oversized] [--backup <dir>]");

        var opts = new YtdOptimizer.Options(
            DownSize:              !o.ContainsKey("no-downsize"),
            FormatOptimization:    o.ContainsKey("format-opt"),
            OptimizeSizeThreshold: Args.U16(o, "threshold", 2048),
            OnlyOversized:         o.ContainsKey("only-oversized"),
            BackupRoot:            o.GetValueOrDefault("backup"),
            MaxSize:               Args.I32(o, "max-size", 0));

        var target = pos[0];
        int ok = 0, changed = 0, failed = 0;
        void Report(YtdOptimizer.FileResult r)
        {
            if (r.Error != null) { failed++; Console.WriteLine($"  ✗ {Rel(r.Path)} — {r.Error}"); return; }
            ok++;
            if (r.Changed) { changed++; Console.WriteLine($"  ✓ {Rel(r.Path)} — {r.PhysicalMbBefore:F1} → {r.PhysicalMbAfter:F1} MiB · {r.TexturesOptimized} tex"); }
            else Console.WriteLine($"  · {Rel(r.Path)} — skipped");
        }

        if (Directory.Exists(target))
        {
            Console.WriteLine($"Optimizing .ytd files under {target} …");
            new YtdOptimizer().Optimize(target, opts, onFile: Report, progress: null, cancel: default);
        }
        else if (File.Exists(target))
        {
            Report(YtdOptimizer.OptimizeFile(target, opts));
        }
        else return Err($"not found: {target}");

        Console.WriteLine($"Done. {ok} processed, {changed} changed, {failed} failed.");
        return failed > 0 ? 1 : 0;
    }

    // ───────────────────────── optimize drawable ────────────────────────

    private static int OptimizeDrawable(string[] a)
    {
        var (pos, o) = Args.Parse(a, new() { "preserve-boundary", "textures-only", "format-opt" });
        if (pos.Count == 0) return Err("usage: fiveos optimize drawable <file.ydr|.ydd|.yft> [--ratio 0.5] [--preserve-boundary] [--textures-only] [--tex-threshold 4096] [--tex-max-size 1024] [-o <out>]");
        var file = pos[0];
        if (!File.Exists(file)) return Err($"not found: {file}");

        var opts = new DrawableOptimizer.Options(
            TargetRatio:               Args.Dbl(o, "ratio", 0.5),
            PreserveBoundary:          o.ContainsKey("preserve-boundary"),
            TextureFormatOptimization: o.ContainsKey("format-opt"),
            TextureSizeThreshold:      Args.U16(o, "tex-threshold", 4096),
            TexturesOnly:              o.ContainsKey("textures-only"),
            TextureMaxSize:            Args.I32(o, "tex-max-size", 0));

        var outPath = o.GetValueOrDefault("out") ?? file;   // default: in place
        var d = new DrawableOptimizer();
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var r = ext switch
        {
            ".ydr" => d.OptimizeYdr(file, outPath, opts, Console.WriteLine),
            ".ydd" => d.OptimizeYdd(file, outPath, opts, Console.WriteLine),
            ".yft" => d.OptimizeYft(file, outPath, opts, Console.WriteLine),
            _ => null,
        };
        if (r == null) return Err($"unsupported drawable type: {ext} (expected .ydr/.ydd/.yft)");
        if (r.Error != null) return Err(r.Error);
        Console.WriteLine($"✓ {Rel(outPath)} — {Mb(r.BytesBefore)} → {Mb(r.BytesAfter)} · {r.TrianglesBefore:N0} → {r.TrianglesAfter:N0} tris · {r.TexturesOptimized} tex");
        return 0;
    }

    // ───────────────────────── optimize texture ─────────────────────────

    private static int OptimizeTexture(string[] a)
    {
        var (pos, o) = Args.Parse(a, new() { "square", "trim", "no-trim", "no-mips", "fill" });
        if (pos.Count == 0) return Err("usage: fiveos optimize texture <in.png|.dds> [-o <outdir>] [--width 1024] [--height 1024] [--square] [--trim] [--border 8] [--format dxt1|dxt5] [--no-mips]");
        var src = pos[0];
        if (!File.Exists(src)) return Err($"not found: {src}");
        var outDir = o.GetValueOrDefault("out") ?? Path.GetDirectoryName(Path.GetFullPath(src))!;

        var d = TextureOptimizer.DefaultOptions();
        var comp = (o.GetValueOrDefault("format") ?? "dxt1").ToLowerInvariant() switch
        {
            "dxt5" or "bc3" => CompressionMethod.DXT5,
            _ => CompressionMethod.DXT1,
        };
        var opts = d with
        {
            Width          = Args.U32(o, "width", d.Width),
            Height         = Args.U32(o, "height", d.Height),
            Square         = o.ContainsKey("square") ? true : d.Square,
            Trim           = o.ContainsKey("no-trim") ? false : (o.ContainsKey("trim") ? true : d.Trim),
            BorderPx       = Args.U32(o, "border", d.BorderPx),
            Compression    = comp,
            GenerateMipmaps = !o.ContainsKey("no-mips"),
        };

        var r = new TextureOptimizer().ProcessFile(src, outDir, opts);
        if (!r.Success) return Err(r.Error ?? "texture optimize failed");
        Console.WriteLine($"✓ {Rel(r.OutputPath)} — {Mb(r.InputBytes)} → {Mb(r.OutputBytes)}");
        return 0;
    }

    // ─────────────────────────────  pack rpf  ────────────────────────────

    private static int PackRpf(string[] a)
    {
        var (pos, o) = Args.Parse(a, new() { "include-all", "no-flatten" });
        if (pos.Count == 0) return Err("usage: fiveos pack rpf <resourceDir> [-o <out.rpf>] [--include-all] [--no-flatten]");
        var dir = pos[0];
        if (!Directory.Exists(dir)) return Err($"not a folder: {dir}");
        var outRpf = o.GetValueOrDefault("out") ?? dir.TrimEnd('/', '\\') + ".rpf";

        var opts = new RpfPacker.Options(
            IncludeAllFiles:     o.ContainsKey("include-all"),
            FlattenStreamFolder: !o.ContainsKey("no-flatten"));
        var r = new RpfPacker().Pack(dir, outRpf, opts, Console.WriteLine);
        if (r.Error != null) return Err(r.Error);
        Console.WriteLine($"✓ {Rel(r.RpfPath)} — {Mb(r.RpfBytes)} · {r.Packed} packed, {r.Skipped} skipped, {r.Failed} failed");
        return r.Failed > 0 ? 1 : 0;
    }

    // ───────────────────────────  build ped-dlc  ────────────────────────

    private static int BuildPedDlc(string[] a)
    {
        var (pos, o) = Args.Parse(a, new() { "openiv-mods" });
        if (pos.Count == 0) return Err("usage: fiveos build ped-dlc <resourceDir> [-o <outdir>] [--name <dlcName>] [--openiv-mods]");
        var dir = pos[0];
        if (!Directory.Exists(dir)) return Err($"not a folder: {dir}");
        var outDir = o.GetValueOrDefault("out") ?? Path.GetDirectoryName(Path.GetFullPath(dir.TrimEnd('/', '\\')))!;

        var opts = new PedDlcScaffolder.Options(
            DlcNameOverride: o.GetValueOrDefault("name"),
            FiveMModsLayout: o.ContainsKey("openiv-mods"));
        var r = new PedDlcScaffolder().Scaffold(dir, outDir, opts, Console.WriteLine);
        if (r.Error != null) return Err(r.Error);
        Console.WriteLine($"✓ {r.Classification} — {r.DlcRpfPath}");
        foreach (var w in r.Warnings) Console.WriteLine($"  ⚠ {w}");
        return 0;
    }

    // ───────────────────────────  build replace  ────────────────────────

    private static int BuildReplace(string[] a)
    {
        var (pos, o) = Args.Parse(a, new() { "client" });
        if (pos.Count == 0 || !o.ContainsKey("vanilla"))
            return Err("usage: fiveos build replace <resourceDir> --vanilla <asset_name> [-o <outdir>] [--client] [--name <packName>]");
        var dir = pos[0];
        if (!Directory.Exists(dir)) return Err($"not a folder: {dir}");
        var outDir = o.GetValueOrDefault("out") ?? Path.GetDirectoryName(Path.GetFullPath(dir.TrimEnd('/', '\\')))!;

        var opts = new ReplaceBuilder.Options(
            TargetAssetName: o["vanilla"]!,
            Output:          o.ContainsKey("client") ? ReplaceBuilder.Output.ClientOverlay : ReplaceBuilder.Output.ServerResource,
            PackName:        o.GetValueOrDefault("name"));
        var r = new ReplaceBuilder().Build(dir, outDir, opts, Console.WriteLine);
        if (r.Error != null) return Err(r.Error);
        Console.WriteLine($"✓ {r.OutputPath}");
        foreach (var w in r.Warnings) Console.WriteLine($"  ⚠ {w}");
        return 0;
    }

    // ───────────────────────────  build addon  ──────────────────────────

    private static int BuildAddon(string[] a)
    {
        var (pos, o) = Args.Parse(a, new());
        if (pos.Count == 0)
            return Err("usage: fiveos build addon <resourceDir> [-o <outdir>] [--name <newAssetName>] [--pack <packName>]");
        var dir = pos[0];
        if (!Directory.Exists(dir)) return Err($"not a folder: {dir}");
        var outDir = o.GetValueOrDefault("out") ?? Path.GetDirectoryName(Path.GetFullPath(dir.TrimEnd('/', '\\')))!;

        var opts = new AddonResourceBuilder.Options(
            NewAssetName: o.GetValueOrDefault("name"),
            PackName:     o.GetValueOrDefault("pack"));
        var r = new AddonResourceBuilder().Build(dir, outDir, opts, Console.WriteLine);
        if (r.Error != null) return Err(r.Error);
        Console.WriteLine($"✓ {r.OutputPath} — archetype(s): {string.Join(", ", r.ArchetypeNames)}");
        foreach (var w in r.Warnings) Console.WriteLine($"  ⚠ {w}");
        return 0;
    }

    // ───────────────────────────  convert vehicle  ──────────────────────

    private static int ConvertVehicle(string[] a)
    {
        var (pos, o) = Args.Parse(a, new() { "single", "merge-all" });
        if (pos.Count == 0) return Err("usage: fiveos convert vehicle <dlc.rpf|modFolder> [more...] [-o <outdir>] [--name <resource>] [--single]");
        var outRoot = o.GetValueOrDefault("out") ?? Path.GetDirectoryName(Path.GetFullPath(pos[0].TrimEnd('/', '\\')))!;

        var opts = new SpVehicleConverter.Options(
            ResourceName: o.GetValueOrDefault("name"),
            MergeAll:     !o.ContainsKey("single"));
        var r = new SpVehicleConverter().Convert(pos, outRoot, opts, Console.WriteLine, default);
        if (!r.Success) return Err(r.Error ?? "vehicle convert failed");
        Console.WriteLine($"✓ {r.ResourceName} — {r.Models.Count} vehicle(s): {string.Join(", ", r.Models)}");
        Console.WriteLine($"  {r.OutputPath}");
        foreach (var w in r.Warnings) Console.WriteLine($"  ⚠ {w}");
        return 0;
    }

    // ────────────────────────────  optimize mesh  ───────────────────────

    private static int OptimizeMesh(string[] a)
    {
        var (pos, o) = Args.Parse(a, new() { "project-surface", "no-preserve-boundary" });
        if (pos.Count == 0) return Err("usage: fiveos optimize mesh <model.glb|.fbx|.obj|.dae|.ply|.stl> [--target-tris 10000] [--project-surface] [--no-preserve-boundary] [-o <dir>]");
        var src = pos[0];
        if (!File.Exists(src)) return Err($"not found: {src}");

        var opts = new SourceMeshOptimizer.Options(
            TargetTriangles:          Args.I32(o, "target-tris", 10000),
            PreserveBoundary:         !o.ContainsKey("no-preserve-boundary"),
            ProjectToOriginalSurface: o.ContainsKey("project-surface"));
        var r = SourceMeshOptimizer.Optimize(src, opts, s => Console.WriteLine($"  {s}"));
        if (r.Error != null) return Err(r.Error);
        var landed = r.OutputPath;
        var dest = o.GetValueOrDefault("out");
        if (dest != null) { try { landed = CopyResult(r.OutputPath, dest); } catch (Exception ex) { Console.WriteLine($"  ⚠ copy to {dest} failed: {ex.Message}"); } }
        Console.WriteLine($"✓ {Rel(landed)} — {r.TrianglesBefore:N0} → {r.TrianglesAfter:N0} tris · {Mb(r.BytesBefore)} → {Mb(r.BytesAfter)}");
        return 0;
    }

    // ────────────────────────────  convert prop  ────────────────────────

    private static int ConvertProp(string[] a)
    {
        var (pos, o) = Args.Parse(a, new() { "no-collision", "embed-collision", "no-ytyp", "no-textures", "lods" });
        if (pos.Count == 0 || !o.ContainsKey("name"))
            return Err("usage: fiveos convert prop <model.glb|.fbx|.obj|.dae|.ply|.stl> --name <asset> [--up auto|y_up|z_up] [--collision-mat WOOD] [--scale x,y,z] [--pos x,y,z] [--rot x,y,z] [--lods] [--no-textures] [-o <dir>]");
        // The engine subprocess runs with its cwd set to a temp workdir, so the
        // input MUST be absolute or ydr-writer resolves it against the wrong dir.
        var src = Path.GetFullPath(pos[0]);
        if (!File.Exists(src)) return Err($"not found: {pos[0]}");
        if (!EngineRunner.IsEngineAvailable())
            return Err($"engine not found at {EngineRunner.EnginePath} — the Engine/ folder must ship beside fiveos.exe");

        var up = (o.GetValueOrDefault("up") ?? "auto").ToLowerInvariant() switch
        {
            "y_up" or "yup" => EngineRunner.UpAxis.YUp,
            "z_up" or "zup" => EngineRunner.UpAxis.ZUp,
            _ => EngineRunner.UpAxis.Auto,
        };
        var (sx, sy, sz) = ParseVec3(o.GetValueOrDefault("scale"), 1, 1, 1);

        var req = new EngineRunner.ConvertRequest(
            SourcePath:        src,
            AssetName:         o["name"]!,
            Up:                up,
            CollisionMaterial: o.GetValueOrDefault("collision-mat") ?? "concrete",
            IncludeCollision:  o.ContainsKey("collision-mat") && !o.ContainsKey("no-collision"),
            EmbedCollision:    o.ContainsKey("embed-collision"),
            IncludeYtyp:       !o.ContainsKey("no-ytyp"),
            ExtractTextures:   !o.ContainsKey("no-textures"),
            ScaleHint:         (sx, sy, sz),
            PositionHint:      o.GetValueOrDefault("pos") ?? "0,0,0",
            RotationHint:      o.GetValueOrDefault("rot") ?? "0,0,0",
            ExcludeMeshes:     null,
            GenerateLods:      o.ContainsKey("lods"));

        // CLI always delivers to an explicit dir (-o or cwd), bypassing the
        // user's GUI server-mode/output settings so it never writes into a live
        // server folder.
        var outDir = Path.GetFullPath(o.GetValueOrDefault("out") ?? Directory.GetCurrentDirectory());
        var outcome = new EngineRunner()
            .RunAsync(req, onLog: s => Console.WriteLine($"  {s}"), deliverToDir: outDir)
            .GetAwaiter().GetResult();
        if (!outcome.Success || outcome.ResultPath == null) return Err(outcome.Error ?? "convert failed");
        Console.WriteLine($"✓ {o["name"]} → {outcome.ResultPath}");
        return 0;
    }

    // ───────────────────────────  import mod-url  ───────────────────────

    private static int ImportModUrl(string[] a)
    {
        var (pos, o) = Args.Parse(a, new());
        if (pos.Count == 0) return Err("usage: fiveos import mod-url <url> [-o <dir>]");
        var r = new ModUrlImporter().ImportAsync(pos[0], s => Console.WriteLine($"  {s}")).GetAwaiter().GetResult();
        if (!r.Success) return Err(r.Error ?? "import failed");
        var landed = r.ExtractedDir!;
        var dest = o.GetValueOrDefault("out");
        if (dest != null && r.ExtractedDir != null)
        {
            try { landed = CopyResult(r.ExtractedDir, dest); }
            catch (Exception ex) { Console.WriteLine($"  ⚠ copy to {dest} failed: {ex.Message}"); }
        }
        Console.WriteLine($"✓ {r.ModName} → {landed}");
        return 0;
    }

    // ─────────────────────────────  helpers  ────────────────────────────

    private static (double, double, double) ParseVec3(string? s, double dx, double dy, double dz)
    {
        if (string.IsNullOrWhiteSpace(s)) return (dx, dy, dz);
        var p = s.Split(',', StringSplitOptions.TrimEntries);
        double G(int i, double d) => p.Length > i && double.TryParse(p[i], out var v) ? v : d;
        return (G(0, dx), G(1, dy), G(2, dz));
    }

    /// <summary>Copy the produced result (a file, e.g. a zip, or a resource
    /// folder) into <paramref name="destDir"/>. Never mutates user settings.</summary>
    private static string CopyResult(string src, string destDir)
    {
        Directory.CreateDirectory(destDir);
        if (File.Exists(src))
        {
            var dst = Path.Combine(destDir, Path.GetFileName(src));
            File.Copy(src, dst, overwrite: true);
            return dst;
        }
        var name = Path.GetFileName(src.TrimEnd('/', '\\'));
        var target = Path.Combine(destDir, name);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var dstf = Path.Combine(target, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dstf)!);
            File.Copy(f, dstf, overwrite: true);
        }
        return target;
    }

    private static int Usage()
    {
        Console.WriteLine(
            """
            FiveOS CLI — headless FiveM asset tooling (v0.1.0)

            USAGE
              fiveos <group> <command> [args] [flags]

            OPTIMIZE
              optimize ytd <dir|file.ytd>      Shrink oversized .ytd texture dictionaries
              optimize drawable <file>         Decimate + compress a .ydr/.ydd/.yft
              optimize texture <in> [-o dir]   Encode a PNG/DDS to block-compressed .dds
              optimize mesh <model>            Decimate a glb/fbx/obj/dae/ply/stl mesh

            CONVERT
              convert prop <model> --name <n>  3D model → placeable FiveM prop (.ydr)
              convert vehicle <dlc.rpf|dir>    SP add-on car → FiveM resource

            PACK / BUILD
              pack rpf <resourceDir>           Pack a folder into an OPEN .rpf
              build ped-dlc <resourceDir>      Scaffold a singleplayer ped dlc.rpf
              build replace <resourceDir>      Build a vanilla-asset replacement resource
              build addon <resourceDir>        Build a NEW-asset add-on resource (ytyp, no replace)

            IMPORT
              import mod-url <url>             Download + extract a FiveM/mod archive

            Run any command with no args to see its flags. This is the Milestone-1
            Windows build; the Linux port is in progress.
            """);
        return 0;
    }

    private static int Unknown(string[] args)
    {
        Console.Error.WriteLine($"unknown command: {string.Join(' ', args)}");
        Console.Error.WriteLine("run `fiveos help` for usage.");
        return 2;
    }

    private static int Err(string msg) { Console.Error.WriteLine(msg); return 2; }

    private static string Rel(string path)
    {
        try { return Path.GetRelativePath(Directory.GetCurrentDirectory(), path); }
        catch { return path; }
    }

    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024):F1} MiB";
}

/// <summary>Tiny argv parser: positionals + `--key value` / `--key=value` /
/// bare `--flag` (bools listed per command) / `-o` alias for `--out`.</summary>
internal static class Args
{
    public static (List<string> Positional, Dictionary<string, string?> Options) Parse(string[] a, HashSet<string> boolFlags)
    {
        var pos = new List<string>();
        var opts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < a.Length; i++)
        {
            var t = a[i];
            if (t == "-o")
            {
                if (i + 1 < a.Length) opts["out"] = a[++i];
            }
            else if (t.StartsWith("--"))
            {
                var eq = t.IndexOf('=');
                if (eq >= 0) { opts[t[2..eq]] = t[(eq + 1)..]; }
                else
                {
                    var key = t[2..];
                    if (boolFlags.Contains(key)) opts[key] = null;
                    else if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) opts[key] = a[++i];
                    else opts[key] = null;
                }
            }
            else pos.Add(t);
        }
        return (pos, opts);
    }

    public static int    I32(Dictionary<string, string?> o, string k, int d)    => o.TryGetValue(k, out var v) && int.TryParse(v, out var r) ? r : d;
    public static ushort U16(Dictionary<string, string?> o, string k, ushort d) => o.TryGetValue(k, out var v) && ushort.TryParse(v, out var r) ? r : d;
    public static uint   U32(Dictionary<string, string?> o, string k, uint d)   => o.TryGetValue(k, out var v) && uint.TryParse(v, out var r) ? r : d;
    public static double Dbl(Dictionary<string, string?> o, string k, double d) => o.TryGetValue(k, out var v) && double.TryParse(v, out var r) ? r : d;
}
