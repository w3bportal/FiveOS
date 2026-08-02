// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FiveOS.Services;

/// <summary>
/// Writes ONE standalone FiveM resource holding N baked emotes — the finalise
/// step of the emote pack queue (<see cref="EmotePackSession"/>).
///
///   &lt;pack&gt;/
///     fxmanifest.lua
///     client.lua                     table-driven, one command per emote
///     stream/&lt;pack&gt;_&lt;clip&gt;@anim.ycd  one dictionary per emote
///     README.txt
///
/// This is a sibling of <see cref="FiveMResourceBuilder"/> (single emote) and
/// <see cref="SyncFiveMResourceBuilder"/> (synced pair), not a replacement:
/// the house pattern is one static builder per resource shape. The single-
/// emote client.lua interpolates one name into a dozen format strings; this
/// one loops a data table. Merging them would make both worse.
///
/// Two naming rules matter and are easy to get wrong:
///
/// 1. The DICTIONARY is the file stem and appears nowhere inside the .ycd, so
///    we're free to prefix it with the pack name. We must: FiveM's streamed-
///    asset namespace is GLOBAL, so two packs both shipping `wave@anim.ycd`
///    means one silently shadows the other server-wide.
/// 2. The CLIP is baked into the .ycd's ClipMap and cannot be renamed after
///    the fact — see EmotePackEntry.ClipName. We emit whatever it was baked
///    with, never a name re-derived from the file.
/// </summary>
public static class EmotePackBuilder
{
    /// <summary>Marks a folder as ours so a re-export can replace it without
    /// asking, while a folder we didn't write still gets a prompt.</summary>
    private const string Marker = "-- FiveOS emote pack";

    /// <summary>One emote in the pack. Mirrors
    /// <see cref="FiveMResourceBuilder.BuildFolder"/>'s per-emote parameters
    /// so a packed emote behaves identically to the same emote exported
    /// alone.</summary>
    public readonly record struct Emote(
        string ClipName,
        string CommandName,
        string Label,
        byte[] YcdBytes,
        EmoteMovement Movement,
        bool IsLooping,
        DpemotesPackBuilder.PropInfo? Prop);

    public sealed record BuildResult(string FolderPath, string PackName, int EmoteCount, long TotalBytes);

    /// <summary>
    /// Write the pack into <paramref name="parentFolder"/> as a subfolder
    /// named after <paramref name="packName"/>.
    ///
    /// The whole tree is built in %TEMP% first and only moved into place once
    /// every byte is written, so a disk-full, an antivirus lock, or a .ycd
    /// held open by a running FXServer leaves the user's folder untouched
    /// rather than half-replaced.
    /// </summary>
    /// <param name="confirmOverwriteForeign">Asked once, with the target path,
    /// when the destination exists but wasn't written by FiveOS. Returning
    /// false aborts with <see cref="OperationCanceledException"/>.</param>
    public static BuildResult BuildFolder(
        string parentFolder,
        string packName,
        IReadOnlyList<Emote> emotes,
        Func<string, bool>? confirmOverwriteForeign = null)
    {
        if (string.IsNullOrWhiteSpace(parentFolder))
            throw new ArgumentException("Pick a folder to write the pack into.", nameof(parentFolder));
        if (emotes is null || emotes.Count == 0)
            throw new ArgumentException("The pack has no emotes to export.", nameof(emotes));

        var safePack = EmotePackSession.Sanitize(packName);
        if (string.IsNullOrEmpty(safePack))
            throw new ArgumentException(
                "The pack name needs at least one letter or number.", nameof(packName));

        // Resolve every per-emote name up front. Dictionary stems must be
        // unique within the pack or one .ycd would overwrite another in
        // stream/ and an emote would silently play its neighbour's animation.
        // Command names likewise, and neither may collide with the pack's own
        // commands — RegisterCommand does not reject a duplicate, so a rogue
        // entry would just quietly shadow /<pack> or /<pack>_stop.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            safePack, "stop_" + safePack,
        };
        var dictSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<Resolved>(emotes.Count);

        foreach (var e in emotes)
        {
            if (e.YcdBytes is null || e.YcdBytes.Length == 0) continue;

            var clip = EmotePackSession.Sanitize(e.ClipName);
            if (string.IsNullOrEmpty(clip)) continue;

            var dictStem = Unique($"{safePack}_{clip}", dictSeen);
            var cmd = Unique(
                Fallback(EmotePackSession.Sanitize(e.CommandName), clip),
                reserved);

            resolved.Add(new Resolved(
                Clip: clip,
                Dict: dictStem + "@anim",
                Command: cmd,
                Label: string.IsNullOrWhiteSpace(e.Label) ? Humanize(cmd) : e.Label.Trim(),
                Flag: e.Movement.ToAnimFlag(e.IsLooping),
                Mode: e.Movement.Label(),
                Loop: e.IsLooping,
                Prop: e.Prop,
                Bytes: e.YcdBytes));
        }

        if (resolved.Count == 0)
            throw new InvalidOperationException("None of the queued emotes carried a baked animation.");

        var targetPath = Path.Combine(parentFolder, safePack);
        var workRoot = Path.Combine(Path.GetTempPath(), "FiveOS",
            "emotepack-" + Guid.NewGuid().ToString("N")[..8]);
        var stagePath = Path.Combine(workRoot, safePack);

        try
        {
            // ── 1. stage the complete resource ───────────────────────
            var stageStream = Path.Combine(stagePath, "stream");
            Directory.CreateDirectory(stageStream);

            long total = 0;
            foreach (var r in resolved)
            {
                File.WriteAllBytes(Path.Combine(stageStream, r.Dict + ".ycd"), r.Bytes);
                total += r.Bytes.Length;
            }

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(Path.Combine(stagePath, "fxmanifest.lua"),
                BuildManifest(safePack, resolved), utf8);
            File.WriteAllText(Path.Combine(stagePath, "client.lua"),
                BuildClient(safePack, resolved), utf8);
            File.WriteAllText(Path.Combine(stagePath, "README.txt"),
                BuildReadme(safePack, resolved, total), utf8);

            // ── 2. clear the way, then promote ───────────────────────
            if (Directory.Exists(targetPath))
            {
                if (!IsOurPack(targetPath))
                {
                    var ok = confirmOverwriteForeign?.Invoke(targetPath) ?? false;
                    if (!ok) throw new OperationCanceledException("Export cancelled.");
                }
                ClearPreviousPack(targetPath);
            }

            Directory.CreateDirectory(targetPath);
            CopyDirectory(stagePath, targetPath);

            return new BuildResult(targetPath, safePack, resolved.Count, total);
        }
        finally
        {
            try { if (Directory.Exists(workRoot)) Directory.Delete(workRoot, recursive: true); }
            catch (Exception ex) { FosLogger.Warn("export", "emote pack staging cleanup failed", ex); }
        }
    }

    private readonly record struct Resolved(
        string Clip, string Dict, string Command, string Label,
        int Flag, string Mode, bool Loop,
        DpemotesPackBuilder.PropInfo? Prop, byte[] Bytes);

    // ─────────────────────────────────────────────────────────────────
    // Target handling
    // ─────────────────────────────────────────────────────────────────

    private static bool IsOurPack(string folder)
    {
        var manifest = Path.Combine(folder, "fxmanifest.lua");
        if (!File.Exists(manifest)) return false;
        try { return File.ReadAllText(manifest).Contains(Marker, StringComparison.Ordinal); }
        catch { return false; }
    }

    /// <summary>Remove what a previous export of this pack left behind, and
    /// nothing else. Stale .ycd files MUST go: drop an emote from the queue,
    /// re-export, and its animation would otherwise keep streaming forever
    /// with nothing in client.lua referencing it.
    ///
    /// Deleted file-by-file rather than Directory.Delete(recursive) + re-
    /// create — Windows defers directory deletion while any handle is open
    /// (Explorer preview, antivirus), and the immediate CreateDirectory then
    /// throws.</summary>
    private static void ClearPreviousPack(string folder)
    {
        foreach (var name in new[] { "fxmanifest.lua", "client.lua", "README.txt" })
        {
            var p = Path.Combine(folder, name);
            if (File.Exists(p)) File.Delete(p);
        }
        var stream = Path.Combine(folder, "stream");
        if (!Directory.Exists(stream)) return;
        foreach (var f in Directory.EnumerateFiles(stream, "*.ycd"))
            File.Delete(f);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    // ─────────────────────────────────────────────────────────────────
    // fxmanifest.lua
    // ─────────────────────────────────────────────────────────────────

    private static string BuildManifest(string pack, List<Resolved> emotes)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Marker + " — regenerated by FiveOS on every export of this pack.");
        sb.AppendLine("-- Edits to this file, client.lua or README.txt will be overwritten;");
        sb.AppendLine("-- put customisation in a separate resource that calls the exports.");
        sb.AppendLine();
        sb.AppendLine("fx_version 'cerulean'");
        sb.AppendLine("game 'gta5'");
        sb.AppendLine();
        sb.AppendLine("author 'FiveOS'");
        sb.AppendLine($"description 'FiveOS emote pack: {Sq(pack)} ({emotes.Count} emote{(emotes.Count == 1 ? "" : "s")})'");
        sb.AppendLine("version '1.0.0'");
        sb.AppendLine();
        sb.AppendLine("client_script 'client.lua'");
        sb.AppendLine();
        sb.AppendLine("-- Every .ycd under stream/ is registered automatically by FiveM's");
        sb.AppendLine("-- resource streamer — one streaming entry per file, keyed on the file");
        sb.AppendLine("-- name without its extension. That name is exactly what RequestAnimDict");
        sb.AppendLine("-- resolves:");
        sb.AppendLine("--");
        foreach (var r in emotes.Take(4))
            sb.AppendLine($"--     stream/{r.Dict}.ycd   ->   RequestAnimDict('{r.Dict}')");
        if (emotes.Count > 4)
            sb.AppendLine($"--     ... ({emotes.Count} dictionaries in total)");
        sb.AppendLine("--");
        sb.AppendLine("-- No files{} block is needed or wanted: that exists for assets fetched");
        sb.AppendLine("-- BY PATH (a .ytyp paired with DLC_ITYP_REQUEST, vehicle metas, NUI");
        sb.AppendLine("-- pages). Listing a streamed .ycd there does nothing at all.");
        sb.AppendLine("--");
        sb.AppendLine("-- lua54 is deliberately NOT set: client.lua sticks to Lua 5.3 syntax so");
        sb.AppendLine("-- the pack runs unchanged on every server.");
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────
    // client.lua
    // ─────────────────────────────────────────────────────────────────

    private static string BuildClient(string pack, List<Resolved> emotes)
    {
        var first = emotes[0];
        var sb = new StringBuilder(16 * 1024);

        sb.AppendLine("-- Auto-generated by FiveOS. See README.txt for install + usage notes.");
        sb.AppendLine($"-- Pack   : {pack}");
        sb.AppendLine($"-- Emotes : {emotes.Count}   (one streamed .ycd per emote under stream/)");
        sb.AppendLine($"-- Built  : {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("--");
        sb.AppendLine("-- RAGE convention: stock animation dictionaries are named with an `@`");
        sb.AppendLine("-- separator (e.g. `amb@world_human_smoking@male@base`). FiveOS follows the");
        sb.AppendLine("-- same pattern, prefixing each dictionary with the pack name so two packs");
        sb.AppendLine("-- on the same server can never shadow each other's streamed assets.");
        sb.AppendLine("-- TaskPlayAnim takes (dict, clip) as two separate strings: the dict is the");
        sb.AppendLine("-- .ycd file name, the clip is the Hash baked inside it.");
        sb.AppendLine();
        sb.AppendLine("-- Commands follow the FOLDER name, so renaming the resource renames them.");
        sb.AppendLine("local PACK = GetCurrentResourceName()");
        sb.AppendLine();
        sb.AppendLine("-- ── switches ─────────────────────────────────────────────────────────");
        sb.AppendLine();
        sb.AppendLine("-- Register the bare /<name> command for every emote (e.g. /" + first.Command + ").");
        sb.AppendLine("-- Worth reading before you leave this on: FiveM does NOT reject a duplicate");
        sb.AppendLine("-- command name. It keeps both handlers registered and fires both, with");
        sb.AppendLine("-- nothing logged — so if dpemotes, rpemotes or a job script already owns one");
        sb.AppendLine("-- of the names below, two animations end up fighting over your ped. The");
        sb.AppendLine("-- /<pack> <name> dispatcher and the /<pack>_<name> aliases can never collide,");
        sb.AppendLine("-- because two resources cannot share a folder name.");
        sb.AppendLine("local BARE_ALIASES = true");
        sb.AppendLine();
        sb.AppendLine("-- Stream every dictionary in at resource start instead of on first use.");
        sb.AppendLine("-- Off by default: a requested dict is reference-held so the engine can never");
        sb.AppendLine($"-- evict it, and all {emotes.Count} would load while players are still spawning,");
        sb.AppendLine("-- competing with world streaming. Lazy loading costs one sub-second hitch the");
        sb.AppendLine("-- first time each emote is played, and nothing after that.");
        sb.AppendLine("local PRELOAD_ALL = false");
        sb.AppendLine();
        sb.AppendLine("-- ── emote table ──────────────────────────────────────────────────────");
        sb.AppendLine("-- flag = TaskPlayAnim bitmask:");
        sb.AppendLine("--   1       AF_LOOPING                          loop forever");
        sb.AppendLine("--   2       AF_HOLD_LAST_FRAME                  play once, hold the last frame");
        sb.AppendLine("--   49/50   + UPPERBODY(16) + SECONDARY(32)     overlay; you can keep walking");
        sb.AppendLine("--   786433  ROOT MOTION, looping                mover extraction(524288)");
        sb.AppendLine("--   786436  ROOT MOTION, one-shot               + kinematic physics(262144)");
        sb.AppendLine("-- The 786xxx flags make the PED PHYSICALLY TRAVEL along the clip's baked");
        sb.AppendLine("-- SKEL_ROOT mover. Each emote carries the flag for the mode it was authored");
        sb.AppendLine("-- with; /<pack> <name> <flag> still overrides it for one call.");
        sb.AppendLine();
        sb.AppendLine("local EMOTES = {");
        foreach (var r in emotes)
        {
            sb.AppendLine("  {");
            sb.AppendLine($"    cmd   = '{Sq(r.Command)}',");
            sb.AppendLine($"    dict  = '{Sq(r.Dict)}',");
            sb.AppendLine($"    clip  = '{Sq(r.Clip)}',");
            sb.AppendLine($"    label = '{Sq(r.Label)}',");
            sb.AppendLine($"    mode  = '{Sq(r.Mode)}',");
            sb.AppendLine($"    flag  = {r.Flag},");
            sb.AppendLine($"    loop  = {(r.Loop ? "true" : "false")},");
            if (r.Prop is { } p && !string.IsNullOrWhiteSpace(p.ModelName))
            {
                var pl = p.Placement ?? Array.Empty<float>();
                float V(int i) => i < pl.Length ? pl[i] : 0f;
                sb.AppendLine($"    prop  = {{ model = '{Sq(p.ModelName)}', bone = {p.BoneTag},");
                sb.AppendLine($"              place = {{ {F(V(0))}, {F(V(1))}, {F(V(2))}, {F(V(3))}, {F(V(4))}, {F(V(5))} }} }},");
            }
            sb.AppendLine("  },");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("local BY_CMD = {}");
        sb.AppendLine("for i = 1, #EMOTES do BY_CMD[EMOTES[i].cmd] = EMOTES[i] end");
        sb.AppendLine();

        // ── plumbing ────────────────────────────────────────────────
        sb.AppendLine("-- ── plumbing ─────────────────────────────────────────────────────────");
        sb.AppendLine();
        sb.AppendLine("local function say(text)");
        sb.AppendLine("  print(('[%s] %s'):format(PACK, text))");
        sb.AppendLine("  -- No-op when no chat resource is running.");
        sb.AppendLine("  TriggerEvent('chat:addMessage', { args = { '^3' .. PACK, text } })");
        sb.AppendLine("end");
        sb.AppendLine();
        sb.AppendLine("-- 2 s deadline: far more than a locally mounted .ycd needs, and bounded so");
        sb.AppendLine("-- a missing file reports instead of spinning forever.");
        sb.AppendLine("local function loadAnimDict(d)");
        sb.AppendLine("  if HasAnimDictLoaded(d) then return true end");
        sb.AppendLine("  RequestAnimDict(d)");
        sb.AppendLine("  local tries = 0");
        sb.AppendLine("  while not HasAnimDictLoaded(d) and tries < 200 do");
        sb.AppendLine("    Wait(10)");
        sb.AppendLine("    tries = tries + 1");
        sb.AppendLine("  end");
        sb.AppendLine("  return HasAnimDictLoaded(d)");
        sb.AppendLine("end");
        sb.AppendLine();
        sb.AppendLine("local AF_MOVER = 524288   -- update ped position from the clip's mover");
        sb.AppendLine();
        sb.AppendLine("local function isRootMotion(flag)");
        sb.AppendLine("  return (flag & AF_MOVER) ~= 0");
        sb.AppendLine("end");
        sb.AppendLine();
        sb.AppendLine("-- ── state ────────────────────────────────────────────────────────────");
        sb.AppendLine();
        sb.AppendLine("local current           = nil   -- cmd of the emote believed to be playing");
        sb.AppendLine("local activeProp        = nil   -- attached object, if the emote has one");
        sb.AppendLine("local ragdollBlocked    = false -- we disabled ragdoll for a root-motion clip");
        sb.AppendLine("local groundSnapPending = false -- root-motion clip needs a ground snap on stop");
        sb.AppendLine("local playToken         = 0     -- supersedes an in-flight play when spammed");
        sb.AppendLine();

        // ── props ───────────────────────────────────────────────────
        sb.AppendLine("-- ── props ────────────────────────────────────────────────────────────");
        sb.AppendLine();
        sb.AppendLine("local function detachProp()");
        sb.AppendLine("  if activeProp and DoesEntityExist(activeProp) then");
        sb.AppendLine("    DetachEntity(activeProp, true, true)");
        sb.AppendLine("    DeleteEntity(activeProp)");
        sb.AppendLine("  end");
        sb.AppendLine("  activeProp = nil");
        sb.AppendLine("end");
        sb.AppendLine();
        sb.AppendLine("local function attachProp(ped, p)");
        sb.AppendLine("  if not p then return end");
        sb.AppendLine("  -- GetHashKey rather than a backtick literal: the model name comes from");
        sb.AppendLine("  -- user data and a stray backtick would break this whole file.");
        sb.AppendLine("  local model = GetHashKey(p.model)");
        sb.AppendLine("  if not IsModelInCdimage(model) then");
        sb.AppendLine("    print(('[%s] [WARN] prop model %q is not in the game files -- playing without it')");
        sb.AppendLine("      :format(PACK, p.model))");
        sb.AppendLine("    return");
        sb.AppendLine("  end");
        sb.AppendLine("  RequestModel(model)");
        sb.AppendLine("  local tries = 0");
        sb.AppendLine("  while not HasModelLoaded(model) and tries < 200 do");
        sb.AppendLine("    Wait(20)");
        sb.AppendLine("    tries = tries + 1");
        sb.AppendLine("  end");
        sb.AppendLine("  if not HasModelLoaded(model) then");
        sb.AppendLine("    print(('[%s] [WARN] prop model %q never loaded -- playing without it')");
        sb.AppendLine("      :format(PACK, p.model))");
        sb.AppendLine("    return");
        sb.AppendLine("  end");
        sb.AppendLine("  local c = GetEntityCoords(ped)");
        sb.AppendLine("  activeProp = CreateObject(model, c.x, c.y, c.z + 0.2, true, true, false)");
        sb.AppendLine("  AttachEntityToEntity(activeProp, ped, GetPedBoneIndex(ped, p.bone),");
        sb.AppendLine("    p.place[1], p.place[2], p.place[3], p.place[4], p.place[5], p.place[6],");
        sb.AppendLine("    true, true, false, true, 1, true)");
        sb.AppendLine("  SetModelAsNoLongerNeeded(model)");
        sb.AppendLine("end");
        sb.AppendLine();

        // ── stop ────────────────────────────────────────────────────
        sb.AppendLine("-- ── stop / switch ────────────────────────────────────────────────────");
        sb.AppendLine();
        sb.AppendLine("-- Tears down whatever the pack last started, driven by activeProp rather");
        sb.AppendLine("-- than by what the INCOMING emote needs -- otherwise /" + first.Command + " after a prop");
        sb.AppendLine("-- emote would leave the prop welded to the ped. Never yields, so");
        sb.AppendLine("-- onResourceStop can call it.");
        sb.AppendLine("local function stopEmote(quiet)");
        sb.AppendLine("  local ped  = PlayerPedId()");
        sb.AppendLine("  local prev = current and BY_CMD[current] or nil");
        sb.AppendLine();
        sb.AppendLine("  -- Object first: clearing the task with the prop still attached leaves a");
        sb.AppendLine("  -- one-frame window where it can be orphaned in world space.");
        sb.AppendLine("  detachProp()");
        sb.AppendLine();
        sb.AppendLine("  if prev then");
        sb.AppendLine("    -- Blends an upper-body overlay out instead of snapping it. Harmless");
        sb.AppendLine("    -- no-op when that clip isn't actually running.");
        sb.AppendLine("    StopAnimTask(ped, prev.dict, prev.clip, 1.0)");
        sb.AppendLine("  end");
        sb.AppendLine();
        sb.AppendLine("  if IsPedInAnyVehicle(ped, false) then");
        sb.AppendLine("    -- ClearPedTasksImmediately on a seated ped can pop them out of the seat.");
        sb.AppendLine("    ClearPedTasks(ped)");
        sb.AppendLine("  else");
        sb.AppendLine("    ClearPedTasksImmediately(ped)");
        sb.AppendLine("  end");
        sb.AppendLine();
        sb.AppendLine("  if ragdollBlocked then");
        sb.AppendLine("    SetPedCanRagdoll(ped, true)");
        sb.AppendLine("    ragdollBlocked = false");
        sb.AppendLine("  end");
        sb.AppendLine();
        sb.AppendLine("  if groundSnapPending then");
        sb.AppendLine("    -- A root-motion clip moved the capsule kinematically; cutting it can");
        sb.AppendLine("    -- leave the ped hovering above or sunk into whatever they travelled onto.");
        sb.AppendLine("    groundSnapPending = false");
        sb.AppendLine("    local c = GetEntityCoords(ped)");
        sb.AppendLine("    local found, gz = GetGroundZFor_3dCoord(c.x, c.y, c.z + 1.0, false)");
        sb.AppendLine("    if found then SetEntityCoordsNoOffset(ped, c.x, c.y, gz, false, false, false) end");
        sb.AppendLine("  end");
        sb.AppendLine();
        sb.AppendLine("  current = nil");
        sb.AppendLine("  if not quiet then say('stopped') end");
        sb.AppendLine("end");
        sb.AppendLine();

        // ── play ────────────────────────────────────────────────────
        sb.AppendLine("-- ── play ─────────────────────────────────────────────────────────────");
        sb.AppendLine();
        sb.AppendLine("local function playEmote(e, flagOverride)");
        sb.AppendLine("  local flag = flagOverride or e.flag");
        sb.AppendLine();
        sb.AppendLine("  -- Load BEFORE clearing tasks: a 2 s stream stall between the clear and");
        sb.AppendLine("  -- the TaskPlayAnim would leave the player standing frozen for no reason.");
        sb.AppendLine("  if not loadAnimDict(e.dict) then");
        sb.AppendLine("    print(('[%s] [FAIL] dict %q not loaded -- is stream/%s.ycd present?')");
        sb.AppendLine("      :format(PACK, e.dict, e.dict))");
        sb.AppendLine("    return false");
        sb.AppendLine("  end");
        sb.AppendLine();
        sb.AppendLine("  playToken = playToken + 1");
        sb.AppendLine("  local token = playToken");
        sb.AppendLine();
        sb.AppendLine("  stopEmote(true)");
        sb.AppendLine("  Wait(50)");
        sb.AppendLine("  if token ~= playToken then return false end   -- superseded by a newer request");
        sb.AppendLine();
        sb.AppendLine("  local ped = PlayerPedId()   -- re-read: the wait can straddle a ped swap");
        sb.AppendLine();
        sb.AppendLine("  if e.prop then");
        sb.AppendLine("    attachProp(ped, e.prop)");
        sb.AppendLine("    if token ~= playToken then detachProp() return false end");
        sb.AppendLine("  end");
        sb.AppendLine();
        sb.AppendLine("  if isRootMotion(flag) then");
        sb.AppendLine("    -- Mover extraction drives the capsule: a frozen or ragdolling ped will");
        sb.AppendLine("    -- not travel, and a bump mid-clip otherwise dumps the kinematic capsule.");
        sb.AppendLine("    FreezeEntityPosition(ped, false)");
        sb.AppendLine("    SetPedCanRagdoll(ped, false)");
        sb.AppendLine("    ragdollBlocked    = true");
        sb.AppendLine("    groundSnapPending = true");
        sb.AppendLine("  end");
        sb.AppendLine();
        sb.AppendLine("  TaskPlayAnim(ped, e.dict, e.clip, 8.0, -8.0, -1, flag, 0.0, false, false, false)");
        sb.AppendLine("  current = e.cmd");
        sb.AppendLine("  Wait(150)");
        sb.AppendLine("  if token ~= playToken then return false end");
        sb.AppendLine();
        sb.AppendLine("  -- Poll after a short wait: RAGE rejects a clip silently, so this line is");
        sb.AppendLine("  -- what tells the user whether to try a different flag.");
        sb.AppendLine("  if IsEntityPlayingAnim(ped, e.dict, e.clip, 3) then");
        sb.AppendLine("    print(('[%s] [OK] playing %s/%s (flag=%d)'):format(PACK, e.dict, e.clip, flag))");
        sb.AppendLine("    return true");
        sb.AppendLine("  end");
        sb.AppendLine();
        sb.AppendLine("  print(('[%s] [FAIL] engine rejected %s/%s -- try \"/%s %s 2\", \"/%s %s 49\", or \"/%s %s 786433\"')");
        sb.AppendLine("    :format(PACK, e.dict, e.clip, PACK, e.cmd, PACK, e.cmd, PACK, e.cmd))");
        sb.AppendLine("  current = nil");
        sb.AppendLine("  return false");
        sb.AppendLine("end");
        sb.AppendLine();
        sb.AppendLine("-- playEmote yields, so every entry point owns a thread rather than blocking");
        sb.AppendLine("-- a command handler or an export caller.");
        sb.AppendLine("local function request(e, flag)");
        sb.AppendLine("  CreateThread(function() playEmote(e, flag) end)");
        sb.AppendLine("end");
        sb.AppendLine();

        // ── list / debug ────────────────────────────────────────────
        sb.AppendLine("-- ── list / debug ─────────────────────────────────────────────────────");
        sb.AppendLine();
        sb.AppendLine("local function listEmotes()");
        sb.AppendLine("  print(('[%s] %d emote(s) in this pack:'):format(PACK, #EMOTES))");
        sb.AppendLine("  for i = 1, #EMOTES do");
        sb.AppendLine("    local e = EMOTES[i]");
        sb.AppendLine("    print(('[%s]   /%s %-20s %-22s [%s%s%s]'):format(");
        sb.AppendLine("      PACK, PACK, e.cmd, e.label, e.mode,");
        sb.AppendLine("      e.loop and ', loop' or '', e.prop and ', prop' or ''))");
        sb.AppendLine("  end");
        sb.AppendLine("  local shown, names = math.min(#EMOTES, 15), {}");
        sb.AppendLine("  for i = 1, shown do names[i] = EMOTES[i].cmd end");
        sb.AppendLine("  local extra = ''");
        sb.AppendLine("  if #EMOTES > shown then extra = (' ... (+%d more, see F8)'):format(#EMOTES - shown) end");
        sb.AppendLine("  TriggerEvent('chat:addMessage', { args = { '^3' .. PACK,");
        sb.AppendLine("    ('%s%s  --  /%s <name>'):format(table.concat(names, ', '), extra, PACK) } })");
        sb.AppendLine("end");
        sb.AppendLine();
        sb.AppendLine("local function debugEmote(e)");
        sb.AppendLine("  local ped = PlayerPedId()");
        sb.AppendLine("  print(('[%s] === debug : %s ==='):format(PACK, e.cmd))");
        sb.AppendLine("  print(('[%s] DICT        = %q'):format(PACK, e.dict))");
        sb.AppendLine("  print(('[%s] CLIP        = %q'):format(PACK, e.clip))");
        sb.AppendLine("  print(('[%s] dict loaded = %s'):format(PACK, tostring(HasAnimDictLoaded(e.dict))))");
        sb.AppendLine("  print(('[%s] flag        = %d (root motion: %s)')");
        sb.AppendLine("    :format(PACK, e.flag, tostring(isRootMotion(e.flag))))");
        sb.AppendLine("  print(('[%s] playing     = %s')");
        sb.AppendLine("    :format(PACK, tostring(IsEntityPlayingAnim(ped, e.dict, e.clip, 3))))");
        sb.AppendLine("  print(('[%s] current     = %s'):format(PACK, tostring(current)))");
        sb.AppendLine("  if e.prop then");
        sb.AppendLine("    print(('[%s] prop        = %s (bone %d), attached = %s'):format(");
        sb.AppendLine("      PACK, e.prop.model, e.prop.bone,");
        sb.AppendLine("      tostring(activeProp ~= nil and DoesEntityExist(activeProp))))");
        sb.AppendLine("  end");
        sb.AppendLine("end");
        sb.AppendLine();

        // ── commands ────────────────────────────────────────────────
        sb.AppendLine("-- ── commands ─────────────────────────────────────────────────────────");
        sb.AppendLine("--   /" + pack + "                 list every emote in this pack");
        sb.AppendLine("--   /" + pack + " <name>          play it with its authored flag");
        sb.AppendLine("--   /" + pack + " <name> 49       play it with a flag override");
        sb.AppendLine("--   /" + pack + " stop            stop and clean up");
        sb.AppendLine("--   /" + pack + " debug <name>    dict / clip / loaded / playing report");
        sb.AppendLine();
        sb.AppendLine("RegisterCommand(PACK, function(_source, args)");
        sb.AppendLine("  local sub = args[1] and string.lower(args[1]) or nil");
        sb.AppendLine();
        sb.AppendLine("  if not sub or sub == 'list' or sub == 'help' then listEmotes() return end");
        sb.AppendLine("  if sub == 'stop' then CreateThread(function() stopEmote(false) end) return end");
        sb.AppendLine();
        sb.AppendLine("  if sub == 'debug' then");
        sb.AppendLine("    local e = BY_CMD[string.lower(args[2] or '')]");
        sb.AppendLine("    if not e then");
        sb.AppendLine("      say(('debug: unknown emote \"%s\" -- try /%s list'):format(tostring(args[2]), PACK))");
        sb.AppendLine("      return");
        sb.AppendLine("    end");
        sb.AppendLine("    debugEmote(e)");
        sb.AppendLine("    return");
        sb.AppendLine("  end");
        sb.AppendLine();
        sb.AppendLine("  local e = BY_CMD[sub]");
        sb.AppendLine("  if not e then");
        sb.AppendLine("    say(('unknown emote \"%s\" -- /%s list shows all %d'):format(sub, PACK, #EMOTES))");
        sb.AppendLine("    return");
        sb.AppendLine("  end");
        sb.AppendLine("  request(e, tonumber(args[2]))");
        sb.AppendLine("end, false)");
        sb.AppendLine();
        sb.AppendLine("RegisterCommand('stop_' .. PACK, function()");
        sb.AppendLine("  CreateThread(function() stopEmote(false) end)");
        sb.AppendLine("end, false)");
        sb.AppendLine();
        sb.AppendLine("for i = 1, #EMOTES do");
        sb.AppendLine("  local e = EMOTES[i]");
        sb.AppendLine("  -- Namespaced alias: collision-proof, since two resources cannot share a");
        sb.AppendLine("  -- folder name.");
        sb.AppendLine("  RegisterCommand(PACK .. '_' .. e.cmd, function(_source, args)");
        sb.AppendLine("    request(e, tonumber(args[1]))");
        sb.AppendLine("  end, false)");
        sb.AppendLine("  if BARE_ALIASES then");
        sb.AppendLine("    RegisterCommand(e.cmd, function(_source, args)");
        sb.AppendLine("      request(e, tonumber(args[1]))");
        sb.AppendLine("    end, false)");
        sb.AppendLine("  end");
        sb.AppendLine("end");
        sb.AppendLine();

        // ── exports ─────────────────────────────────────────────────
        sb.AppendLine("-- ── exports ──────────────────────────────────────────────────────────");
        sb.AppendLine($"--   exports['{Sq(pack)}']:Play('{Sq(first.Command)}')       -- authored flag");
        sb.AppendLine($"--   exports['{Sq(pack)}']:Play('{Sq(first.Command)}', 49)   -- flag override");
        sb.AppendLine($"--   exports['{Sq(pack)}']:Stop()");
        sb.AppendLine($"--   exports['{Sq(pack)}']:List()      -- table describing every emote");
        sb.AppendLine($"--   exports['{Sq(pack)}']:Current()   -- playing emote name, or nil");
        sb.AppendLine();
        sb.AppendLine("exports('Play', function(name, flag)");
        sb.AppendLine("  local e = name and BY_CMD[string.lower(name)] or nil");
        sb.AppendLine("  if not e then");
        sb.AppendLine("    print(('[%s] [FAIL] Play(%q): no such emote in this pack')");
        sb.AppendLine("      :format(PACK, tostring(name)))");
        sb.AppendLine("    return false");
        sb.AppendLine("  end");
        sb.AppendLine("  request(e, tonumber(flag))");
        sb.AppendLine("  return true   -- accepted; watch the console for the [OK] / [FAIL] line");
        sb.AppendLine("end)");
        sb.AppendLine();
        sb.AppendLine("-- Calls the local function rather than ExecuteCommand: the console command");
        sb.AppendLine("-- manager is a multimap, so a command name shared with another resource");
        sb.AppendLine("-- would fire theirs too.");
        sb.AppendLine("exports('Stop', function() CreateThread(function() stopEmote(false) end) end)");
        sb.AppendLine();
        sb.AppendLine("exports('Current', function() return current end)");
        sb.AppendLine();
        sb.AppendLine("exports('List', function()");
        sb.AppendLine("  -- Fresh copy: a consumer must not be able to mutate our table.");
        sb.AppendLine("  local out = {}");
        sb.AppendLine("  for i = 1, #EMOTES do");
        sb.AppendLine("    local e = EMOTES[i]");
        sb.AppendLine("    out[i] = { name = e.cmd, label = e.label, dict = e.dict, clip = e.clip,");
        sb.AppendLine("               flag = e.flag, loop = e.loop, mode = e.mode, prop = e.prop ~= nil }");
        sb.AppendLine("  end");
        sb.AppendLine("  return out");
        sb.AppendLine("end)");
        sb.AppendLine();

        // ── startup ─────────────────────────────────────────────────
        sb.AppendLine("-- ── startup ──────────────────────────────────────────────────────────");
        sb.AppendLine();
        sb.AppendLine("CreateThread(function()");
        sb.AppendLine("  -- Give the streamer a moment to register this resource's stream/ entries");
        sb.AppendLine("  -- before asking about them.");
        sb.AppendLine("  Wait(1000)");
        sb.AppendLine();
        sb.AppendLine("  local bad = {}");
        sb.AppendLine("  for i = 1, #EMOTES do");
        sb.AppendLine("    local d = EMOTES[i].dict");
        sb.AppendLine("    -- DoesAnimDictExist asks whether the dictionary is REGISTERED. Unlike");
        sb.AppendLine("    -- RequestAnimDict it neither streams the data in nor takes a reference,");
        sb.AppendLine("    -- so verifying every entry costs nothing.");
        sb.AppendLine("    if DoesAnimDictExist and not DoesAnimDictExist(d) then bad[#bad + 1] = d end");
        sb.AppendLine("  end");
        sb.AppendLine();
        sb.AppendLine("  if #bad == 0 then");
        sb.AppendLine("    print(('[%s] [OK] %d/%d anim dictionaries registered')");
        sb.AppendLine("      :format(PACK, #EMOTES, #EMOTES))");
        sb.AppendLine("  else");
        sb.AppendLine("    print(('[%s] [FAIL] %d of %d anim dictionaries are missing:')");
        sb.AppendLine("      :format(PACK, #bad, #EMOTES))");
        sb.AppendLine("    for i = 1, #bad do");
        sb.AppendLine("      print(('[%s] [FAIL]   %s   <- expected stream/%s.ycd'):format(PACK, bad[i], bad[i]))");
        sb.AppendLine("    end");
        sb.AppendLine("    print(('[%s] [FAIL] if you just restarted this resource in-game, run the')");
        sb.AppendLine("      :format(PACK))");
        sb.AppendLine("    print(('[%s] [FAIL] command once more -- streamed assets remount on restart.')");
        sb.AppendLine("      :format(PACK))");
        sb.AppendLine("  end");
        sb.AppendLine();
        sb.AppendLine("  print(('[%s] ready : %d emotes  --  /%s list'):format(PACK, #EMOTES, PACK))");
        sb.AppendLine("  print(('[%s] commands : /%s <name> [flag]   /%s stop   /%s debug <name>')");
        sb.AppendLine("    :format(PACK, PACK, PACK, PACK))");
        sb.AppendLine();
        sb.AppendLine("  TriggerEvent('chat:addSuggestion', '/' .. PACK,");
        sb.AppendLine("    ('FiveOS emote pack (%d emotes)'):format(#EMOTES), {");
        sb.AppendLine("      { name = 'name', help = 'emote name, or: list / stop / debug' },");
        sb.AppendLine("      { name = 'flag', help = 'optional TaskPlayAnim flag override' },");
        sb.AppendLine("    })");
        sb.AppendLine("end)");
        sb.AppendLine();
        sb.AppendLine("if PRELOAD_ALL then");
        sb.AppendLine("  CreateThread(function()");
        sb.AppendLine("    Wait(2000)");
        sb.AppendLine("    for i = 1, #EMOTES do");
        sb.AppendLine("      loadAnimDict(EMOTES[i].dict)");
        sb.AppendLine("      Wait(250)   -- staggered so we don't fight world streaming");
        sb.AppendLine("    end");
        sb.AppendLine("    print(('[%s] preloaded %d anim dictionaries'):format(PACK, #EMOTES))");
        sb.AppendLine("  end)");
        sb.AppendLine("end");
        sb.AppendLine();
        sb.AppendLine("-- Watchdog: keeps `current` honest so a finished one-shot doesn't leave a");
        sb.AppendLine("-- prop welded to the ped, and cleans up on death.");
        sb.AppendLine("CreateThread(function()");
        sb.AppendLine("  local misses = 0");
        sb.AppendLine("  while true do");
        sb.AppendLine("    Wait(500)");
        sb.AppendLine("    if current then");
        sb.AppendLine("      local e   = BY_CMD[current]");
        sb.AppendLine("      local ped = PlayerPedId()");
        sb.AppendLine("      if IsEntityDead(ped) then");
        sb.AppendLine("        stopEmote(true)");
        sb.AppendLine("        misses = 0");
        sb.AppendLine("      elseif e and not IsEntityPlayingAnim(ped, e.dict, e.clip, 3) then");
        sb.AppendLine("        -- Two consecutive misses: a blend-out or a one-frame secondary-slot");
        sb.AppendLine("        -- gap shouldn't trigger cleanup.");
        sb.AppendLine("        misses = misses + 1");
        sb.AppendLine("        if misses >= 2 then");
        sb.AppendLine("          stopEmote(true)");
        sb.AppendLine("          misses = 0");
        sb.AppendLine("        end");
        sb.AppendLine("      else");
        sb.AppendLine("        misses = 0");
        sb.AppendLine("      end");
        sb.AppendLine("    else");
        sb.AppendLine("      misses = 0");
        sb.AppendLine("    end");
        sb.AppendLine("  end");
        sb.AppendLine("end)");
        sb.AppendLine();
        sb.AppendLine("AddEventHandler('onResourceStop', function(res)");
        sb.AppendLine("  if res ~= GetCurrentResourceName() then return end");
        sb.AppendLine("  -- Without this a /restart mid-emote orphans the attached object: it is a");
        sb.AppendLine("  -- networked entity with no owner left to delete it.");
        sb.AppendLine("  detachProp()");
        sb.AppendLine("  if current then ClearPedTasks(PlayerPedId()) end");
        sb.AppendLine("end)");
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────
    // README.txt
    // ─────────────────────────────────────────────────────────────────

    private static string BuildReadme(string pack, List<Resolved> emotes, long totalBytes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FiveOS emote pack: {pack}");
        sb.AppendLine($"  {emotes.Count} emote{(emotes.Count == 1 ? "" : "s")} · built {DateTime.Now:yyyy-MM-dd HH:mm} · "
                    + $"{EmotePackSession.FormatBytes(totalBytes)} of animation data");
        sb.AppendLine();
        sb.AppendLine("INSTALL");
        sb.AppendLine();
        sb.AppendLine($"  1. Copy the entire \"{pack}\" folder into your server's resources/:");
        sb.AppendLine($"       <server>/resources/{pack}/");
        sb.AppendLine();
        sb.AppendLine("  2. Add this line to server.cfg:");
        sb.AppendLine($"       ensure {pack}");
        sb.AppendLine();
        sb.AppendLine($"  3. Restart the server (or `start {pack}` from rcon).");
        sb.AppendLine();
        sb.AppendLine("IN-GAME");
        sb.AppendLine();
        sb.AppendLine($"  /{pack}                 list every emote in this pack");
        sb.AppendLine($"  /{pack} <name>          play it");
        sb.AppendLine($"  /{pack} <name> <flag>   play it with a TaskPlayAnim flag override");
        sb.AppendLine($"  /{pack} stop            stop and clean up (same as /stop_{pack})");
        sb.AppendLine($"  /{pack} debug <name>    print dict / clip / loaded / playing status");
        sb.AppendLine();
        sb.AppendLine($"  Every emote also answers to its own name (/{emotes[0].Command}) and to a");
        sb.AppendLine($"  namespaced alias (/{pack}_{emotes[0].Command}). See BARE COMMAND NAMES below.");
        sb.AppendLine();
        sb.AppendLine("  The commands are named after the FOLDER, so renaming the folder renames");
        sb.AppendLine("  them. Rename it and `ensure` the new name.");
        sb.AppendLine();
        sb.AppendLine("EMOTES");
        sb.AppendLine();
        sb.AppendLine("  Command                Label                  Mode                       Loop Prop  File");
        sb.AppendLine("  ---------------------- ---------------------- -------------------------- ---- ----  ----------------------------");
        foreach (var r in emotes)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  /{0,-21} {1,-22} {2,-26} {3,-4} {4,-5} stream/{5}.ycd",
                r.Command, Clip(r.Label, 22), r.Mode, r.Loop ? "yes" : "no",
                r.Prop is null ? "no" : "yes", r.Dict));
        }
        sb.AppendLine();
        sb.AppendLine("  \"Mode\" is how the emote coexists with movement:");
        sb.AppendLine("    in place (full body)      - you are locked in place for the clip");
        sb.AppendLine("    upper body (can move)     - overlay; you can keep walking (flag 49 / 50)");
        sb.AppendLine("    root motion (ped travels) - the ped physically walks the recorded path");
        sb.AppendLine("                                (flag 786433 / 786436)");
        sb.AppendLine();
        sb.AppendLine("BARE COMMAND NAMES");
        sb.AppendLine();
        sb.AppendLine("  On by default: each emote gets its own short command. Worth knowing why");
        sb.AppendLine("  that carries a risk -- FiveM does NOT reject a duplicate command name. If");
        sb.AppendLine("  dpemotes, rpemotes or a job script already registers one of the names in");
        sb.AppendLine("  the table above, BOTH handlers stay registered and BOTH fire, so two");
        sb.AppendLine("  animations fight over your ped and nothing is logged.");
        sb.AppendLine();
        sb.AppendLine("  To turn the short names off and use only the collision-proof forms, open");
        sb.AppendLine("  client.lua and set:");
        sb.AppendLine();
        sb.AppendLine("      local BARE_ALIASES = false");
        sb.AppendLine();
        sb.AppendLine($"  /{pack} <name> and /{pack}_<name> always work either way.");
        sb.AppendLine();
        sb.AppendLine("PRELOADING");
        sb.AppendLine();
        sb.AppendLine("  Each animation streams in the first time you play that emote -- a");
        sb.AppendLine($"  sub-second hitch, once per session. To load all {emotes.Count} at resource start");
        sb.AppendLine("  instead, open client.lua and set:");
        sb.AppendLine();
        sb.AppendLine("      local PRELOAD_ALL = true");
        sb.AppendLine();
        sb.AppendLine("  The trade-off: they stream while players are still spawning, competing");
        sb.AppendLine("  with world streaming, and a requested dictionary is reference-held so the");
        sb.AppendLine("  engine can never evict it. Worth it only for small or constantly-used packs.");
        sb.AppendLine();
        sb.AppendLine("OTHER RESOURCES");
        sb.AppendLine();
        sb.AppendLine($"  exports['{pack}']:Play('{emotes[0].Command}')       -- authored flag");
        sb.AppendLine($"  exports['{pack}']:Play('{emotes[0].Command}', 49)   -- flag override");
        sb.AppendLine($"  exports['{pack}']:Stop()");
        sb.AppendLine($"  exports['{pack}']:List()                  -- table of every emote");
        sb.AppendLine($"  exports['{pack}']:Current()               -- playing emote name, or nil");
        sb.AppendLine();
        sb.AppendLine("  Note the signature: a pack takes the emote NAME first, where a single-emote");
        sb.AppendLine("  FiveOS resource takes just the flag.");
        sb.AppendLine();
        sb.AppendLine("TROUBLESHOOTING");
        sb.AppendLine();
        sb.AppendLine("  Open the F8 console. This pack prints [OK] / [FAIL] lines for everything.");
        sb.AppendLine();
        sb.AppendLine("  * \"[FAIL] N of M anim dictionaries are missing\" at startup");
        sb.AppendLine("      Those .ycd files didn't stream in. Check stream/ actually contains");
        sb.AppendLine("      them, then restart the resource. If you restarted it while already");
        sb.AppendLine("      in-game, streamed assets remount -- run the command once more.");
        sb.AppendLine();
        sb.AppendLine("  * The command works but nothing plays (\"[FAIL] engine rejected\")");
        sb.AppendLine("      The engine refused the clip with that flag. Try the alternatives:");
        sb.AppendLine($"        /{pack} <name> 2        play once, hold the last frame");
        sb.AppendLine($"        /{pack} <name> 49       upper-body overlay");
        sb.AppendLine($"        /{pack} <name> 786433   root motion (the ped travels)");
        sb.AppendLine("      Whichever works, re-export that emote from FiveOS with the matching");
        sb.AppendLine("      playback mode so the flag is baked in.");
        sb.AppendLine();
        sb.AppendLine("  * A root-motion emote plays but the ped doesn't move");
        sb.AppendLine("      The clip has no SKEL_ROOT position track. Re-export it from FiveOS");
        sb.AppendLine("      with Movement set to root motion.");
        sb.AppendLine();
        sb.AppendLine("  * The ped jitters or twists wrong");
        sb.AppendLine("      That emote's source rig wasn't a SKEL_* GTA player skeleton. Re-import");
        sb.AppendLine("      in FiveOS with the GTA Male / Female preset and re-add it to the pack.");
        sb.AppendLine();
        sb.AppendLine("  * A prop stays stuck to the ped");
        sb.AppendLine("      Shouldn't happen -- the pack removes props on stop, on death, when a");
        sb.AppendLine($"      one-shot ends, and when the resource stops. If it does, /{pack} stop");
        sb.AppendLine("      clears it.");
        sb.AppendLine();
        sb.AppendLine("  A pack ships no .ycd.xml source. Exporting a single emote from FiveOS does,");
        sb.AppendLine("  for compiling the same clip in CodeWalker when the binary writer and RAGE");
        sb.AppendLine("  disagree -- use that path if you need to inspect one clip in detail.");
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────
    // helpers
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Body of a single-quoted Lua string.
    ///
    /// The single-emote builder escapes only the apostrophe, which survives
    /// because its label comes from a Windows folder name and so can't contain
    /// a backslash. Pack labels are free text, and here ONE bad label is a
    /// lexer error that takes every emote in the pack down at resource load —
    /// so escape the backslash first, then the quote, and flatten every
    /// control character (a raw newline inside a single-quoted Lua string is
    /// itself a lexer error).
    /// </summary>
    private static string Sq(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            if (ch == '\\') sb.Append("\\\\");
            else if (ch == '\'') sb.Append("\\'");
            else if (ch == '\r') { /* drop */ }
            else if (char.IsControl(ch)) sb.Append(' ');
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Prop placement floats. "0.0######" (not "0.######") so a zero
    /// emits `0.0` — AttachEntityToEntity wants floats, and Lua would read a
    /// bare `0` as an integer.</summary>
    private static string F(float v) =>
        v.ToString("0.0######", CultureInfo.InvariantCulture);

    private static string Fallback(string value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;

    private static string Unique(string candidate, HashSet<string> taken)
    {
        if (taken.Add(candidate)) return candidate;
        for (int n = 2; n < 1000; n++)
        {
            var next = $"{candidate}_{n}";
            if (taken.Add(next)) return next;
        }
        var last = candidate + "_" + Guid.NewGuid().ToString("N")[..4];
        taken.Add(last);
        return last;
    }

    private static string Humanize(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return "Emote";
        var words = slug.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(' ', words);
    }

    private static string Clip(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..(max - 1)] + "…");
}
