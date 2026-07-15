// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FiveOS.Services;

/// <summary>
/// Spawns the bundled <c>Engine\ydr-writer.exe</c> (a self-contained .NET 8
/// build that uses CodeWalker.Core's FbxConverter for the .ydr binary write)
/// and zips its FiveM resource folder into the user's Downloads.
/// </summary>
public sealed class EngineRunner
{
    /// <summary>Output target — Prop produces a placeable .ydr; Weapon
    /// produces a .ydr with a gun_root skeleton + weapons.meta /
    /// weaponarchetypes.meta / weaponanimations.meta scaffolding.</summary>
    public enum ConvertMode { Prop, Weapon }

    /// <summary>Which base-game weapon class to clone behavior from when
    /// generating weapons.meta. Drives default damage/clip/range/anim ref.</summary>
    public enum WeaponArchetype { Pistol, Rifle, Smg, Shotgun, Sniper }

    public sealed record ConvertRequest(
        string SourcePath,
        string AssetName,
        UpAxis Up,
        string CollisionMaterial,
        bool IncludeCollision,
        bool EmbedCollision,
        bool IncludeYtyp,
        bool ExtractTextures,
        // Per-axis scale baked into the exported geometry. Identity =
        // (1,1,1). Non-uniform values (e.g. (1,1.5,1)) stretch the prop
        // on the named axis only — the gizmo's "Uniform scale" toggle
        // governs whether the viewer allows non-uniform input.
        (double X, double Y, double Z) ScaleHint,
        string PositionHint,
        string RotationHint,
        IReadOnlyCollection<string>? ExcludeMeshes = null,
        ConvertMode Mode = ConvertMode.Prop,
        WeaponArchetype WeaponArchetype = WeaponArchetype.Pistol,
        // null/empty defers to engine which derives from AssetName
        string? WeaponName = null,
        string? WeaponSlot = null,
        // Bone offsets in metres, drawable-local (Z-up, Y-forward).
        // Null leaves the engine's archetype defaults in place.
        string? MuzzleOffset = null,
        string? GripOffset = null,
        string? MagazineOffset = null,
        string? EjectOffset = null,
        // Embedded LOD generation: deep-clone the High DrawableModels and
        // decimate clones into Med/Low/VLow via the engine's g3sharp
        // pipeline. Default off; the engine reads --lods to toggle.
        bool GenerateLods = false,
        // Per-tier outer draw distances in metres. High renders 0 →
        // LodDistHigh, Med renders LodDistHigh → LodDistMed, etc. Past
        // LodDistVlow the prop disappears entirely — that value also
        // drives the .ytyp's archetype-level cull radius.
        double LodDistHigh = 60d,
        double LodDistMed = 120d,
        double LodDistLow = 250d,
        double LodDistVlow = 500d,
        // Pack-mode routing. When non-null the engine bypasses zip /
        // server delivery and instead invites the caller to consume the
        // raw resource folder via the <see cref="ConvertOutcome.PackResourceDir"/>
        // callback path — the prop-pack accumulator latches onto this to
        // copy stream/* into its staging tree before the temp workdir
        // is cleaned up.
        bool RouteToPack = false,
        // Per-part visual material override, keyed by the part's source
        // (mesh) name. Empty / unset entries default to the engine's
        // standard shader pick. Drives both the RAGE shader written into
        // the YDR and, for glass presets, the per-poly collision material.
        IReadOnlyDictionary<string, string>? PartMaterials = null);

    public sealed record ConvertOutcome(
        bool Success,
        /// <summary>Result path. For zip mode it's the .zip; for loose
        /// modes it's the resource folder (per-asset) or stream/ directory
        /// (shared) the assets ended up in.</summary>
        string? ResultPath,
        OutputMode Mode,
        string Log,
        string? Error);

    /// <summary>How the engine's resource output is delivered to the user.</summary>
    public enum OutputMode
    {
        /// <summary>Zip into the configured single output folder.</summary>
        SingleZip,
        /// <summary>Drop unzipped {asset}_resource/ into the server folder.</summary>
        ServerPerAsset,
        /// <summary>Merge stream files + fxmanifest into a shared server resource.</summary>
        ServerShared,
        /// <summary>Stage the converted prop into the active prop-pack
        /// session instead of producing a per-asset deliverable. The
        /// pack is finalised separately via <see cref="PropPackBuilder"/>.</summary>
        Pack,
    }

    public enum UpAxis { Auto, YUp, ZUp }

    /// <summary>
    /// Path to the bundled engine exe shipped beside FiveOS.exe in the
    /// install directory (or beside it in the build output when running
    /// from F5).
    /// </summary>
    public static string EnginePath
        => Path.Combine(RuntimeAssets.EngineDir, "ydr-writer.exe");

    public static bool IsEngineAvailable() => File.Exists(EnginePath);

    public async Task<ConvertOutcome> RunAsync(
        ConvertRequest req,
        Action<string>? onLog = null,
        CancellationToken cancel = default)
    {
        if (!IsEngineAvailable())
            return new ConvertOutcome(false, null, OutputMode.SingleZip, "",
                $"Engine binary not found at {EnginePath}. The FiveOS install is incomplete.");

        var workDir = Path.Combine(Path.GetTempPath(), "FiveOS", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);

        var args = new List<string>
        {
            req.SourcePath,
            "-o", workDir,
            "--name", req.AssetName,
            "--up", req.Up switch
            {
                UpAxis.YUp => "y_up",
                UpAxis.ZUp => "z_up",
                _ => "auto",
            },
            "--collision-mat", req.CollisionMaterial,
            // Pass scale as "x,y,z". The engine parses either form
            // (single number = uniform, three = per-axis).
            "--scale", $"{req.ScaleHint.X.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                       $"{req.ScaleHint.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                       $"{req.ScaleHint.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            "--pos", req.PositionHint,
            "--rot", req.RotationHint,
        };
        if (!req.IncludeCollision) args.Add("--no-collision");
        else if (req.EmbedCollision) args.Add("--embed-collision");
        if (!req.IncludeYtyp) args.Add("--no-ytyp");
        if (!req.ExtractTextures) args.Add("--no-textures");
        if (req.GenerateLods) args.Add("--lods");
        // Always ship --lod-dists so the YDR's LodDist fields and the
        // .ytyp's lodDist (which mirrors LodDistVlow) pick up the user's
        // per-tier choices. With LODs off the engine sets every tier to
        // LodDistVlow internally so the High model renders all the way
        // out to the outer cull radius.
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            args.Add("--lod-dists");
            args.Add($"{((float)req.LodDistHigh).ToString(ci)}," +
                     $"{((float)req.LodDistMed).ToString(ci)}," +
                     $"{((float)req.LodDistLow).ToString(ci)}," +
                     $"{((float)req.LodDistVlow).ToString(ci)}");
        }
        if (req.ExcludeMeshes != null && req.ExcludeMeshes.Count > 0)
        {
            args.Add("--exclude-mesh");
            args.Add(string.Join(",", req.ExcludeMeshes));
        }
        if (req.PartMaterials != null)
        {
            // Repeated --part-mat name=PRESET. Names that contain '=' or
            // ',' would break the parser; FBX/GLB mesh names in practice
            // don't, but the engine still parses by splitting on the
            // FIRST '=' to keep this robust against future edge cases.
            foreach (var kv in req.PartMaterials)
            {
                if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                args.Add("--part-mat");
                args.Add($"{kv.Key}={kv.Value}");
            }
        }

        if (req.Mode == ConvertMode.Weapon)
        {
            args.Add("--mode"); args.Add("weapon");
            args.Add("--weapon-archetype"); args.Add(req.WeaponArchetype.ToString());
            if (!string.IsNullOrWhiteSpace(req.WeaponName)) { args.Add("--weapon-name"); args.Add(req.WeaponName); }
            if (!string.IsNullOrWhiteSpace(req.WeaponSlot)) { args.Add("--weapon-slot"); args.Add(req.WeaponSlot); }
            if (!string.IsNullOrWhiteSpace(req.MuzzleOffset))   { args.Add("--muzzle-offset");   args.Add(req.MuzzleOffset); }
            if (!string.IsNullOrWhiteSpace(req.GripOffset))     { args.Add("--grip-offset");     args.Add(req.GripOffset); }
            if (!string.IsNullOrWhiteSpace(req.MagazineOffset)) { args.Add("--magazine-offset"); args.Add(req.MagazineOffset); }
            if (!string.IsNullOrWhiteSpace(req.EjectOffset))    { args.Add("--eject-offset");    args.Add(req.EjectOffset); }
        }

        var psi = new ProcessStartInfo
        {
            FileName = EnginePath,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var log = new StringBuilder();
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            log.AppendLine(e.Data);
            onLog?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            log.AppendLine("[err] " + e.Data);
            onLog?.Invoke(e.Data);
        };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(cancel);
        }
        catch (Exception ex)
        {
            return new ConvertOutcome(false, null, OutputMode.SingleZip, log.ToString(),
                $"Failed to launch engine: {ex.Message}");
        }

        if (proc.ExitCode != 0)
            return new ConvertOutcome(false, null, OutputMode.SingleZip, log.ToString(),
                $"Engine returned exit code {proc.ExitCode}. See log.");

        // Engine writes the resource folder under workDir/<asset_name>_resource/
        var resourceDir = Path.Combine(workDir, $"{req.AssetName}_resource");
        if (!Directory.Exists(resourceDir))
            return new ConvertOutcome(false, null, OutputMode.SingleZip, log.ToString(),
                $"Engine reported success but no resource folder found at {resourceDir}.");

        // Branch on user output settings.
        ConvertOutcome outcome;
        try
        {
            if (req.RouteToPack)
            {
                outcome = DeliverToPack(req, resourceDir, log);
            }
            else if (UserSettings.IsServerModeActive())
            {
                var serverFolder = UserSettings.LoadServerResourceFolder()!;
                var layout = UserSettings.LoadServerLayout();
                outcome = layout == ServerLayout.PerAsset
                    ? DeliverServerPerAsset(req, resourceDir, serverFolder, log)
                    : DeliverServerShared(req, resourceDir, serverFolder, log);
            }
            else
            {
                outcome = DeliverSingleZip(req, resourceDir, log);
            }
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* swallow */ }
        }
        return outcome;
    }

    // ─── Output delivery strategies ───────────────────────────────────

    /// <summary>Pack-mode delivery — hand the engine's resource folder
    /// off to the active <see cref="PropPackSession"/> so each subsequent
    /// conversion accumulates into a single FiveM resource. The session
    /// copies the files into a stable staging directory before we return,
    /// so the work-dir cleanup in <c>RunAsync</c>'s finally block doesn't
    /// race the consumer.</summary>
    private static ConvertOutcome DeliverToPack(
        ConvertRequest req, string resourceDir, StringBuilder log)
    {
        try
        {
            var entry = PropPackSession.Current.AddFromResourceDir(resourceDir, req.AssetName);
            if (entry is null)
            {
                return new ConvertOutcome(false, null, OutputMode.Pack, log.ToString(),
                    "Engine produced files but no stream/ tree was found to add to the pack.");
            }
            return new ConvertOutcome(true, entry.SlotDir, OutputMode.Pack, log.ToString(), null);
        }
        catch (Exception ex)
        {
            return new ConvertOutcome(false, null, OutputMode.Pack, log.ToString(),
                $"Engine produced files but adding to the pack failed: {ex.Message}");
        }
    }

    private static ConvertOutcome DeliverSingleZip(
        ConvertRequest req, string resourceDir, StringBuilder log)
    {
        var dest = UserSettings.ResolveSingleOutputFolder();
        Directory.CreateDirectory(dest);
        var zipPath = UniquePath(Path.Combine(dest, $"{req.AssetName}_resource.zip"));
        try
        {
            ZipFile.CreateFromDirectory(resourceDir, zipPath, CompressionLevel.Optimal,
                includeBaseDirectory: true);
        }
        catch (Exception ex)
        {
            return new ConvertOutcome(false, null, OutputMode.SingleZip, log.ToString(),
                $"Engine produced files but zipping failed: {ex.Message}");
        }
        return new ConvertOutcome(true, zipPath, OutputMode.SingleZip, log.ToString(), null);
    }

    private static ConvertOutcome DeliverServerPerAsset(
        ConvertRequest req, string resourceDir, string serverFolder, StringBuilder log)
    {
        Directory.CreateDirectory(serverFolder);
        var dest = UniquePath(Path.Combine(serverFolder, $"{req.AssetName}_resource"));
        try
        {
            CopyDirectory(resourceDir, dest);
        }
        catch (Exception ex)
        {
            return new ConvertOutcome(false, null, OutputMode.ServerPerAsset, log.ToString(),
                $"Engine produced files but writing to server folder failed: {ex.Message}");
        }
        return new ConvertOutcome(true, dest, OutputMode.ServerPerAsset, log.ToString(), null);
    }

    /// <summary>Shared layout: stream/* lands in &lt;server&gt;/stream/, the
    /// asset name is appended to a single shared fxmanifest.lua under the
    /// server folder root. Existing files with the same name are
    /// overwritten — that's the intended "edit in place, re-restart server"
    /// flow.</summary>
    private static ConvertOutcome DeliverServerShared(
        ConvertRequest req, string resourceDir, string serverFolder, StringBuilder log)
    {
        Directory.CreateDirectory(serverFolder);
        var srcStream = Path.Combine(resourceDir, "stream");
        var dstStream = Path.Combine(serverFolder, "stream");
        Directory.CreateDirectory(dstStream);

        try
        {
            if (Directory.Exists(srcStream))
            {
                foreach (var f in Directory.EnumerateFiles(srcStream))
                    File.Copy(f, Path.Combine(dstStream, Path.GetFileName(f)), overwrite: true);
            }
            UpdateSharedFxManifest(serverFolder);
        }
        catch (Exception ex)
        {
            return new ConvertOutcome(false, null, OutputMode.ServerShared, log.ToString(),
                $"Engine produced files but server-shared merge failed: {ex.Message}");
        }
        return new ConvertOutcome(true, dstStream, OutputMode.ServerShared, log.ToString(), null);
    }

    /// <summary>Rewrite &lt;server&gt;/fxmanifest.lua to list every asset
    /// currently in stream/. Idempotent — safe to run after every conversion.</summary>
    private static void UpdateSharedFxManifest(string serverFolder)
    {
        var streamDir = Path.Combine(serverFolder, "stream");
        var entries = Directory.Exists(streamDir)
            ? Directory.EnumerateFiles(streamDir)
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()!
            : new List<string?>();

        var sb = new StringBuilder();
        sb.AppendLine("fx_version 'cerulean'");
        sb.AppendLine("game 'gta5'");
        sb.AppendLine();
        sb.AppendLine("-- Generated by FiveOS — assets are listed individually so RAGE");
        sb.AppendLine("-- streams them. Re-run any conversion to refresh this list.");
        sb.AppendLine();
        sb.AppendLine("files {");
        foreach (var name in entries)
            sb.AppendLine($"    'stream/{name}',");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("data_file 'DLC_ITYP_REQUEST' 'stream/*.ytyp'");

        File.WriteAllText(Path.Combine(serverFolder, "fxmanifest.lua"), sb.ToString());
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(source))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.EnumerateDirectories(source))
            CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    private static string UniquePath(string p)
    {
        // Must detect an existing DIRECTORY too — DeliverServerPerAsset passes a
        // folder path (<asset>_resource); a File.Exists-only check let a re-run
        // silently merge/overwrite into the previous conversion's folder.
        if (!File.Exists(p) && !Directory.Exists(p)) return p;
        var dir = Path.GetDirectoryName(p)!;
        var name = Path.GetFileNameWithoutExtension(p);
        var ext = Path.GetExtension(p);
        for (int n = 2; ; n++)
        {
            var cand = Path.Combine(dir, $"{name}-{n}{ext}");
            if (!File.Exists(cand) && !Directory.Exists(cand)) return cand;
        }
    }
}
