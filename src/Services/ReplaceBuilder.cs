// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CodeWalker.GameFiles;

namespace FiveOS.Services;

/// <summary>
/// "Replace existing asset" mode of the RPF converter: take the user's
/// custom model/texture and make it OVERRIDE a vanilla game asset (e.g. swap
/// the cellphone model) — as opposed to the ped scaffolder, which ADDS new
/// named content.
///
/// The one load-bearing operation is naming: the replacement only works if it
/// carries the EXACT vanilla stem (e.g. <c>prop_phone_ing</c>) both as the
/// on-disk filename AND inside the resource (Drawable.Name) — RAGE keys
/// streamed assets by name hash, so a same-named asset overrides the original
/// with no <c>.ytyp</c> and no copy of the original game rpf needed.
///
/// Two outputs (verified against FiveM's loader — see the research spec):
/// • <see cref="Output.ServerResource"/> — a FiveM resource (<c>stream/&lt;name&gt;</c>).
///   Reliable, never bans, but the SERVER hosts it (everyone connected sees it).
/// • <see cref="Output.ClientOverlay"/> — an OpenIV-style <c>mods\&lt;pack&gt;.rpf</c>
///   with an <c>assembly.xml</c> manifest (a bare dlcpack is silently ignored by
///   FiveM's loader). Client-side/local-only; blocked by Pure Mode servers and
///   can trip anti-cheat. Best-effort — cannot be in-game-verified from here.
///
/// v1 scope: props (<c>.ydr</c>) and textures (<c>.ytd</c>). Vehicles (<c>.yft</c>)
/// and peds are flagged and deferred (bone/rig + internal-name rewrite differ).
/// </summary>
public sealed class ReplaceBuilder
{
    public enum Output { ServerResource, ClientOverlay }

    public sealed record Options(string TargetAssetName, Output Output, string? PackName = null);

    public sealed record Result(
        bool Success,
        string? OutputPath,
        string TargetAssetName,
        Output Output,
        IReadOnlyList<string> ProducedFiles,
        IReadOnlyList<string> Warnings,
        string? Error);

    /// <summary>The ban/limits warning surfaced for any client-side output.</summary>
    public const string ClientSideWarning =
        "Client-side replacement, use at your own risk. This overrides a base-game asset on YOUR machine only — " +
        "other players and the server are unaffected. It will NOT load on servers running Pure Mode (sv_pureLevel 1/2), " +
        "and many servers' anti-cheats scan for modified client game files and can KICK or BAN you. " +
        "For everyone to see it safely, use the Server-side resource output instead.";

    private static readonly Regex NameRx = new(@"[^a-z0-9_]", RegexOptions.Compiled);

    public Result Build(string inputFolder, string outputRootDir, Options opts, Action<string>? log = null)
    {
        var warnings = new List<string>();
        var produced = new List<string>();
        var target = SanitizeAssetName(opts.TargetAssetName);
        try
        {
            if (string.IsNullOrEmpty(target))
                return Fail(target, opts.Output, "Enter the exact vanilla asset name to replace (e.g. prop_phone_ing).");
            if (string.IsNullOrWhiteSpace(inputFolder) || !Directory.Exists(inputFolder))
                return Fail(target, opts.Output, $"Input folder not found: {inputFolder}");

            var pack = string.IsNullOrWhiteSpace(opts.PackName) ? target + "_replace" : SanitizeAssetName(opts.PackName!);
            if (string.IsNullOrEmpty(pack)) pack = "replace";

            var all = Directory.EnumerateFiles(inputFolder, "*", SearchOption.AllDirectories).ToList();
            var ydr = all.FirstOrDefault(f => f.EndsWith(".ydr", StringComparison.OrdinalIgnoreCase));
            var ytd = all.FirstOrDefault(f => f.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase));
            var yft = all.FirstOrDefault(f => f.EndsWith(".yft", StringComparison.OrdinalIgnoreCase));
            var ydd = all.FirstOrDefault(f => f.EndsWith(".ydd", StringComparison.OrdinalIgnoreCase));

            if (yft != null && ydr == null)
                warnings.Add("A .yft (vehicle/ped fragment) was found — Replace v1 only handles props (.ydr) and textures (.ytd); the .yft was NOT used.");
            if (ydd != null && ydr == null)
                warnings.Add("A .ydd (ped/prop drawable dictionary) was found — Replace v1 only handles props (.ydr) and textures (.ytd); the .ydd was NOT used.");
            if (ydr == null && ytd == null)
                return Fail(target, opts.Output, "No .ydr (prop model) or .ytd (texture) found in the input folder to use as the replacement.");

            // Stage renamed copies in a GUID-unique temp dir (so concurrent
            // builds can't stomp each other) and always clean it up afterward.
            var staging = Path.Combine(Path.GetTempPath(), "fiveos_replace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var staged = new List<string>();
                if (ydr != null)
                {
                    var dst = Path.Combine(staging, target + ".ydr");
                    File.Copy(ydr, dst, overwrite: true);
                    PropPackBuilder.RewriteYdrInternalName(dst, target);
                    staged.Add(dst);
                    log?.Invoke($"Renamed model → {target}.ydr (internal name rewritten)");
                }
                if (ytd != null)
                {
                    var dst = Path.Combine(staging, target + ".ytd");
                    File.Copy(ytd, dst, overwrite: true);
                    staged.Add(dst);
                    log?.Invoke($"Renamed texture → {target}.ytd");
                }

                return opts.Output == Output.ServerResource
                    ? EmitServerResource(outputRootDir, staged, target, pack, warnings, produced, log)
                    : EmitClientOverlay(outputRootDir, staged, target, pack, warnings, produced, log);
            }
            finally
            {
                try { Directory.Delete(staging, true); } catch { /* best-effort temp cleanup */ }
            }
        }
        catch (Exception ex)
        {
            return Fail(target, opts.Output, ex.Message);
        }
    }

    // ── Server-side resource (reliable; everyone on the server sees it) ──
    private Result EmitServerResource(string outRoot, List<string> staged, string target, string pack,
                                      List<string> warnings, List<string> produced, Action<string>? log)
    {
        var resDir = Path.Combine(Path.GetFullPath(outRoot), pack);
        var streamDir = Path.Combine(resDir, "stream");
        Directory.CreateDirectory(streamDir);

        foreach (var s in staged)
        {
            var name = Path.GetFileName(s);
            File.Copy(s, Path.Combine(streamDir, name), overwrite: true);
            produced.Add("stream/" + name);
            log?.Invoke($"  + stream/{name}");
        }

        // Minimal stream resource — FiveM auto-streams stream/. A pure replace
        // of an existing asset needs NO data_file / DLC_ITYP_REQUEST / .ytyp.
        var manifest = new StringBuilder();
        manifest.AppendLine("fx_version 'cerulean'");
        manifest.AppendLine("game 'gta5'");
        manifest.AppendLine();
        manifest.AppendLine($"-- Replaces the vanilla asset '{target}' (same-named stream asset overrides it).");
        File.WriteAllText(Path.Combine(resDir, "fxmanifest.lua"), manifest.ToString(), Utf8NoBom);

        var readme = new StringBuilder();
        readme.AppendLine($"# {pack} — server-side replacement of '{target}'");
        readme.AppendLine();
        readme.AppendLine("Reliable replace: everyone connected to YOUR server sees it; base game files are never modified.");
        readme.AppendLine();
        readme.AppendLine("## Install");
        readme.AppendLine($"1. Copy the '{pack}' folder into your server's resources/ folder.");
        readme.AppendLine($"2. Add  ensure {pack}  to server.cfg (or start {pack}).");
        readme.AppendLine("3. Restart the resource / server. The custom model loads in place of the vanilla one for all players.");
        File.WriteAllText(Path.Combine(resDir, "README.txt"), readme.ToString(), Utf8NoBom);

        log?.Invoke($"Done. Server resource '{pack}' replaces '{target}'.");
        return new Result(true, resDir, target, Output.ServerResource, produced, warnings, null);
    }

    // ── Client-side overlay (local-only; assembly.xml mods package) ──
    private Result EmitClientOverlay(string outRoot, List<string> staged, string target, string pack,
                                     List<string> warnings, List<string> produced, Action<string>? log)
    {
        var modsDir = Path.Combine(Path.GetFullPath(outRoot), "mods");
        Directory.CreateDirectory(modsDir);
        var rpfPath = Path.Combine(modsDir, pack + ".rpf");
        if (File.Exists(rpfPath)) File.Delete(rpfPath);

        log?.Invoke($"Creating mods/{pack}.rpf (OPEN) with assembly.xml…");
        var rpf = RpfFile.CreateNew(modsDir, pack + ".rpf", RpfEncryption.OPEN);

        var leafNames = staged.Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToList();
        var assembly = BuildAssemblyXml(pack, leafNames);
        RpfPacker.AddFileAtPath(rpf.Root, "assembly.xml", Utf8NoBom.GetBytes(assembly));
        produced.Add("assembly.xml");
        foreach (var s in staged)
        {
            var name = Path.GetFileName(s);
            RpfPacker.AddFileAtPath(rpf.Root, "content/" + name, File.ReadAllBytes(s));
            produced.Add("content/" + name);
            log?.Invoke($"  + content/{name}");
        }

        warnings.Add(ClientSideWarning);

        var readme = new StringBuilder();
        readme.AppendLine($"# {pack} — CLIENT-SIDE replacement of '{target}'");
        readme.AppendLine();
        readme.AppendLine("## Install (FiveM client, local only)");
        readme.AppendLine(@"1. Copy the 'mods' folder next to this readme into:");
        readme.AppendLine(@"     %LOCALAPPDATA%\FiveM\FiveM.app\");
        readme.AppendLine($@"   so the package is at  FiveM.app\mods\{pack}.rpf");
        readme.AppendLine("2. Start FiveM. The custom model overrides the vanilla one for YOU only.");
        readme.AppendLine();
        readme.AppendLine("## IMPORTANT");
        readme.AppendLine(ClientSideWarning);
        readme.AppendLine();
        readme.AppendLine("This client-side package is best-effort and could not be verified in-game by the builder. " +
                          "If it doesn't load, the Server-side resource output is the reliable alternative.");
        File.WriteAllText(Path.Combine(Path.GetFullPath(outRoot), $"{pack}_INSTALL_README.txt"), readme.ToString(), Utf8NoBom);

        log?.Invoke($"Done. Client overlay mods/{pack}.rpf replaces '{target}' (local-only).");
        return new Result(true, rpfPath, target, Output.ClientOverlay, produced, warnings, null);
    }

    /// <summary>
    /// FiveM mods-package manifest (OpenIV .oiv dialect that FiveM's ModPackage
    /// parser reads). Mounts a faux streaming rpf inside update.rpf holding the
    /// same-named asset(s) so the name-hash override wins regardless of the
    /// vanilla's exact in-archive folder.
    /// </summary>
    private static string BuildAssemblyXml(string pack, List<string> leafNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<package target=\"Five\">");
        sb.AppendLine("  <content>");
        sb.AppendLine("    <archive path=\"update\\update.rpf\" createIfNotExist=\"True\" type=\"RPF7\">");
        sb.AppendLine($"      <archive path=\"x64/{pack}.rpf\" createIfNotExist=\"True\" type=\"RPF7\">");
        foreach (var n in leafNames)
            sb.AppendLine($"        <add source=\"content/{n}\">{n}</add>");
        sb.AppendLine("      </archive>");
        sb.AppendLine("    </archive>");
        sb.AppendLine("  </content>");
        sb.AppendLine("</package>");
        return sb.ToString();
    }

    private Result Fail(string target, Output output, string error) =>
        new(false, null, target, output, Array.Empty<string>(), Array.Empty<string>(), error);

    /// <summary>Vanilla asset/pack name: lowercase, [a-z0-9_] only, no extension.</summary>
    internal static string SanitizeAssetName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = Path.GetFileNameWithoutExtension(raw.Trim()).ToLowerInvariant();
        return NameRx.Replace(s, "");
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);
}
