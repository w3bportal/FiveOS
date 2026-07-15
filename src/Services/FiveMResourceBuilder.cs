// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace FiveOS.Services;

/// <summary>
/// Writes a standalone FiveM resource folder for a baked emote. Unlike
/// <see cref="DpemotesPackBuilder"/> (which produces a snippet meant to
/// merge INTO an existing dpemotes install), this builder emits a fully
/// self-contained resource directory:
///
///   &lt;folder&gt;/
///     fxmanifest.lua
///     client.lua
///     stream/&lt;name&gt;.ycd
///     README.txt
///
/// The user drops the folder into their server's <c>resources/</c>
/// directory, adds <c>ensure &lt;folder-name&gt;</c> to server.cfg, and
/// the emote becomes playable via the registered <c>/&lt;name&gt;</c>
/// chat command. No external dependency on dpemotes.
/// </summary>
public static class FiveMResourceBuilder
{
    /// <summary>
    /// Write the resource directly to <paramref name="folderPath"/>.
    /// The folder is created (and any prior contents in the well-known
    /// sub-paths are overwritten) — but other unrelated files in the
    /// target are left alone so existing user-customised manifests
    /// don't get nuked by an unintended re-export to the same path.
    /// </summary>
    public static void BuildFolder(
        string folderPath,
        string emoteName,
        string displayName,
        byte[] ycdBytes,
        bool isLooping,
        EmoteMovement movement,
        DpemotesPackBuilder.PropInfo? prop,
        string? ycdXml = null)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("folderPath must be non-empty.", nameof(folderPath));
        if (ycdBytes is null || ycdBytes.Length == 0)
            throw new ArgumentException("ycdBytes must be non-empty.", nameof(ycdBytes));

        var safe = SanitizeName(emoteName);
        var pretty = string.IsNullOrWhiteSpace(displayName) ? safe : displayName;
        // RAGE convention: stock animation dictionaries are named with an
        // `@` separator (e.g. `amb@world_human_smoking@male@base`). Shipping
        // emote tools (gw-anim, dpemotes-style packs) follow the same
        // pattern with `<name>@anim` for the dict, `<name>` for the clip
        // inside it. FiveM TaskPlayAnim takes (dict, clip) as two args, so
        // they're *separate strings* -- the dict drives the .ycd file name
        // and the RequestAnimDict call, the clip is what the .ycd's
        // internal Clips[].Hash field exposes.
        var dictName = safe + "@anim";
        var clipName = safe;

        Directory.CreateDirectory(folderPath);
        var streamDir = Path.Combine(folderPath, "stream");
        Directory.CreateDirectory(streamDir);

        // 1. The .ycd lives under stream/ so FiveM's resource loader
        //    auto-registers it as a streamed asset. File name == dict
        //    name (sans .ycd) is what RequestAnimDict resolves against.
        File.WriteAllBytes(Path.Combine(streamDir, dictName + ".ycd"), ycdBytes);

        // 1b. Source XML at the resource root (NOT under stream/, where
        //     FiveM would try to ingest it). Lets the user compile the
        //     same clip via CodeWalker.exe and compare — diagnostic
        //     fallback for when our in-process CW.Core binary writer
        //     produces bytes RAGE can't fully parse.
        if (!string.IsNullOrEmpty(ycdXml))
        {
            File.WriteAllText(
                Path.Combine(folderPath, dictName + ".ycd.xml"),
                ycdXml,
                new UTF8Encoding(false));
        }

        // 2. fxmanifest.lua — bare minimum to make the resource boot.
        //    `fx_version 'cerulean'` is the current modern manifest;
        //    `game 'gta5'` scopes it to GTAV. `files` declaration is
        //    deliberately omitted for the .ycd — `stream/` is implicitly
        //    streamed by FiveM, no manifest entry needed.
        File.WriteAllText(
            Path.Combine(folderPath, "fxmanifest.lua"),
            BuildManifest(safe, pretty),
            new UTF8Encoding(false));

        // 3. client.lua — registers a chat command + a console export so
        //    other resources can trigger the emote programmatically.
        File.WriteAllText(
            Path.Combine(folderPath, "client.lua"),
            BuildClient(safe, dictName, clipName, pretty, isLooping, movement, prop),
            new UTF8Encoding(false));

        // 4. README so users know what they're looking at when they
        //    open the folder a month later wondering what it does.
        File.WriteAllText(
            Path.Combine(folderPath, "README.txt"),
            BuildReadme(safe, dictName, clipName, pretty, ycdBytes.Length, isLooping, movement, prop is not null),
            new UTF8Encoding(false));
    }

    private static string BuildManifest(string name, string displayName)
    {
        var escDisplay = displayName.Replace("'", "\\'");
        var sb = new StringBuilder();
        sb.AppendLine("fx_version 'cerulean'");
        sb.AppendLine("game 'gta5'");
        sb.AppendLine();
        sb.AppendLine($"author 'FiveOS'");
        sb.AppendLine($"description 'Custom emote: {escDisplay}'");
        sb.AppendLine($"version '1.0.0'");
        sb.AppendLine();
        sb.AppendLine("client_script 'client.lua'");
        sb.AppendLine();
        sb.AppendLine("-- The .ycd lives under stream/ and is auto-loaded by FiveM's resource");
        sb.AppendLine("-- streamer. No explicit `files` declaration needed for it; the directory");
        sb.AppendLine("-- name `stream` is special-cased by the FiveM runtime.");
        return sb.ToString();
    }

    private static string BuildClient(string name, string dictName, string clipName, string displayName, bool loop, EmoteMovement movement, DpemotesPackBuilder.PropInfo? prop)
    {
        // Flag breakdown (RAGE TaskPlayAnim flag bitmask):
        //   1   = AF_LOOPING (animation loops indefinitely)
        //   2   = AF_HOLD_LAST_FRAME (freezes on last frame when done)
        //   16  = AF_UPPERBODY (upper body only — legs stay free to walk)
        //   32  = AF_SECONDARY (plays on the secondary task slot)
        //   49  = LOOP | UPPERBODY | SECONDARY   (gesture while standing/walking)
        //   51  = LOOP | HOLD | UPPERBODY | SECONDARY  (dpemotes "moving" recipe)
        // The default flag is chosen from the selected playback mode; see
        // EmoteMovement.ToAnimFlag. Users can still override per-call via the
        // /<name> <flag> argument below.
        int defaultFlag = movement.ToAnimFlag(loop);

        var escDisplay = displayName.Replace("'", "\\'");
        var sb = new StringBuilder();
        sb.AppendLine("-- Auto-generated by FiveOS. See README.txt for install + usage notes.");
        sb.AppendLine($"-- Resource : {name}");
        sb.AppendLine($"-- Dict     : {dictName}        (file stream/{dictName}.ycd)");
        sb.AppendLine($"-- Clip     : {clipName}        (internal Hash inside the .ycd)");
        sb.AppendLine();
        sb.AppendLine($"local DICT  = '{dictName}'");
        sb.AppendLine($"local CLIP  = '{clipName}'");
        sb.AppendLine($"local LABEL = '{escDisplay}'");
        sb.AppendLine();
        sb.AppendLine("-- TaskPlayAnim flag combos worth trying if /" + name + " looks off:");
        sb.AppendLine("--   1       = AF_LOOPING                          (basic loop)");
        sb.AppendLine("--   2       = AF_HOLD_LAST_FRAME                  (plays once, holds final frame)");
        sb.AppendLine("--   49      = AF_LOOPING + UPPERBODY + SECONDARY  (overlay on idle / walk)");
        sb.AppendLine("--   786433  = ROOT MOTION, looping   (mover-extraction 524288 + kinematic 262144 + loop 1)");
        sb.AppendLine("--   786436  = ROOT MOTION, one-shot  (mover-extraction + kinematic + reposition-when-finished 4)");
        sb.AppendLine("--   ^ the 786xxx flags make the PED PHYSICALLY MOVE along the clip's baked mover");
        sb.AppendLine("--     (Video → Emote). Needs a SKEL_ROOT position track in the .ycd, which");
        sb.AppendLine("--     FiveOS bakes for video emotes. If the ped won't travel, try /" + name + " 786433.");
        sb.AppendLine($"local DEFAULT_FLAG = {defaultFlag}");
        sb.AppendLine();
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
        sb.AppendLine("-- Preload the dict on resource start so the first /play has zero lag.");
        sb.AppendLine("CreateThread(function()");
        sb.AppendLine("  if not loadAnimDict(DICT) then");
        sb.AppendLine("    print(('[%s] [FAIL] anim dict %q failed to load -- is stream/%s.ycd present?')");
        sb.AppendLine("      :format(GetCurrentResourceName(), DICT, DICT))");
        sb.AppendLine("  end");
        sb.AppendLine("end)");
        sb.AppendLine();

        if (prop is not null)
        {
            // Prop spawn helper. The placement offsets are local to the
            // attach bone — same convention as dpemotes' PropPlacement.
            var p = prop.Placement;
            float[] px = new float[6];
            for (int i = 0; i < 6 && i < p.Length; i++) px[i] = p[i];
            var escProp = prop.ModelName.Replace("'", "\\'");
            sb.AppendLine("-- Prop attached during the emote. Spawns + attaches on /play, removed on /stop.");
            sb.AppendLine("local activeProp = nil");
            sb.AppendLine();
            sb.AppendLine("local function attachProp(ped)");
            sb.AppendLine($"  local model = `{escProp}`");
            sb.AppendLine("  RequestModel(model)");
            sb.AppendLine("  local tries = 0");
            sb.AppendLine("  while not HasModelLoaded(model) and tries < 200 do");
            sb.AppendLine("    Wait(20)");
            sb.AppendLine("    tries = tries + 1");
            sb.AppendLine("  end");
            sb.AppendLine("  if not HasModelLoaded(model) then return end");
            sb.AppendLine("  local x, y, z = table.unpack(GetEntityCoords(ped))");
            sb.AppendLine("  activeProp = CreateObject(model, x, y, z + 0.2, true, true, false)");
            sb.AppendLine($"  AttachEntityToEntity(activeProp, ped, GetPedBoneIndex(ped, {prop.BoneTag}),");
            sb.AppendLine($"    {F(px[0])}, {F(px[1])}, {F(px[2])}, {F(px[3])}, {F(px[4])}, {F(px[5])},");
            sb.AppendLine("    true, true, false, true, 1, true)");
            sb.AppendLine("  SetModelAsNoLongerNeeded(model)");
            sb.AppendLine("end");
            sb.AppendLine();
            sb.AppendLine("local function detachProp()");
            sb.AppendLine("  if activeProp and DoesEntityExist(activeProp) then");
            sb.AppendLine("    DetachEntity(activeProp, true, true)");
            sb.AppendLine("    DeleteEntity(activeProp)");
            sb.AppendLine("  end");
            sb.AppendLine("  activeProp = nil");
            sb.AppendLine("end");
            sb.AppendLine();
        }

        // playEmote() is the workhorse — same pattern as gw-anim's
        // playPose: clear any in-flight task FIRST (otherwise RAGE
        // queues this animation behind the active one and it looks
        // like nothing happened), TaskPlayAnim, then poll
        // IsEntityPlayingAnim after a short Wait to detect "engine
        // rejected the clip" silently. The console log is what tells
        // the user whether to try a different flag.
        sb.AppendLine("local function playEmote(flag)");
        sb.AppendLine("  local ped = PlayerPedId()");
        sb.AppendLine("  local res = GetCurrentResourceName()");
        sb.AppendLine("  flag = flag or DEFAULT_FLAG");
        sb.AppendLine();
        sb.AppendLine("  if not loadAnimDict(DICT) then");
        sb.AppendLine("    print(('[%s] [FAIL] dict %q not loaded -- stream/%s.ycd missing?')");
        sb.AppendLine("      :format(res, DICT, DICT))");
        sb.AppendLine("    return");
        sb.AppendLine("  end");
        sb.AppendLine();
        sb.AppendLine("  ClearPedTasksImmediately(ped)");
        sb.AppendLine("  Wait(50)");
        if (prop is not null)
            sb.AppendLine("  attachProp(ped)");
        sb.AppendLine("  TaskPlayAnim(ped, DICT, CLIP, 8.0, -8.0, -1, flag, 0.0, false, false, false)");
        sb.AppendLine("  Wait(150)");
        sb.AppendLine();
        sb.AppendLine("  if IsEntityPlayingAnim(ped, DICT, CLIP, 3) then");
        sb.AppendLine("    print(('[%s] [OK] playing %s/%s (flag=%d)'):format(res, DICT, CLIP, flag))");
        sb.AppendLine("  else");
        sb.AppendLine($"    print(('[%s] [FAIL] engine rejected %s/%s -- try /{name} 2 , /{name} 49 , or /{name} 786433 (root motion)')");
        sb.AppendLine("      :format(res, DICT, CLIP))");
        sb.AppendLine("  end");
        sb.AppendLine("end");
        sb.AppendLine();

        sb.AppendLine($"-- /{name}        -> default flag ({defaultFlag})");
        sb.AppendLine($"-- /{name} 2      -> try flag 2 (hold last frame)");
        sb.AppendLine($"-- /{name} 49     -> try flag 49 (overlay on walk / idle)");
        sb.AppendLine($"RegisterCommand('{name}', function(_source, args)");
        sb.AppendLine("  local flag = tonumber(args[1]) or DEFAULT_FLAG");
        sb.AppendLine("  playEmote(flag)");
        sb.AppendLine("end, false)");
        sb.AppendLine();

        sb.AppendLine($"-- /stop_{name}   -> clear whatever task the ped is doing");
        sb.AppendLine($"RegisterCommand('stop_{name}', function()");
        sb.AppendLine("  ClearPedTasksImmediately(PlayerPedId())");
        if (prop is not null) sb.AppendLine("  detachProp()");
        sb.AppendLine("  print(('[%s] cleared tasks'):format(GetCurrentResourceName()))");
        sb.AppendLine("end, false)");
        sb.AppendLine();

        sb.AppendLine($"-- /debug_{name}  -> print dict / clip / loaded / playing status");
        sb.AppendLine($"RegisterCommand('debug_{name}', function()");
        sb.AppendLine("  local ped = PlayerPedId()");
        sb.AppendLine("  local res = GetCurrentResourceName()");
        sb.AppendLine("  print(('[%s] === debug ==='):format(res))");
        sb.AppendLine("  print(('[%s] DICT        = %q'):format(res, DICT))");
        sb.AppendLine("  print(('[%s] CLIP        = %q'):format(res, CLIP))");
        sb.AppendLine("  print(('[%s] dict loaded = %s'):format(res, tostring(HasAnimDictLoaded(DICT))))");
        sb.AppendLine("  print(('[%s] playing     = %s'):format(res, tostring(IsEntityPlayingAnim(ped, DICT, CLIP, 3))))");
        sb.AppendLine("end, false)");
        sb.AppendLine();

        sb.AppendLine("-- Programmatic access from other resources:");
        sb.AppendLine($"--   exports['{name}']:Play()        -- default flag");
        sb.AppendLine($"--   exports['{name}']:Play(49)      -- override flag");
        sb.AppendLine($"--   exports['{name}']:Stop()");
        sb.AppendLine("exports('Play', function(flag) playEmote(flag) end)");
        sb.AppendLine($"exports('Stop', function() ExecuteCommand('stop_{name}') end)");
        sb.AppendLine();

        sb.AppendLine("CreateThread(function()");
        sb.AppendLine("  Wait(500)");
        sb.AppendLine("  local res = GetCurrentResourceName()");
        sb.AppendLine("  print(('[%s] ready : %s/%s'):format(res, DICT, CLIP))");
        sb.AppendLine($"  print(('[%s] commands : /{name} [flag]  /stop_{name}  /debug_{name}'):format(res))");
        sb.AppendLine("end)");
        return sb.ToString();
    }

    private static string BuildReadme(string name, string dictName, string clipName, string displayName, int ycdBytes, bool looping, EmoteMovement movement, bool hasProp)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FiveOS emote resource: {displayName} ({name})");
        sb.AppendLine($"  Built {DateTime.Now:yyyy-MM-dd HH:mm}, {dictName}.ycd is {ycdBytes:N0} bytes.");
        sb.AppendLine($"  Dict : {dictName}     (file stream/{dictName}.ycd)");
        sb.AppendLine($"  Clip : {clipName}     (Hash inside the .ycd)");
        sb.AppendLine($"  Mode : {movement.Label()}   ·   Loop : {(looping ? "yes" : "no")}{(hasProp ? "   ·   Prop: yes" : "")}");
        sb.AppendLine();
        sb.AppendLine("Install:");
        sb.AppendLine();
        sb.AppendLine($"1. Copy the entire \"{name}\" folder into your server's resources/ directory:");
        sb.AppendLine($"     <server>/resources/{name}/");
        sb.AppendLine();
        sb.AppendLine("2. Add this line to server.cfg:");
        sb.AppendLine($"     ensure {name}");
        sb.AppendLine();
        sb.AppendLine("3. Restart the server (or `start " + name + "` from rcon).");
        sb.AppendLine();
        sb.AppendLine("In-game:");
        sb.AppendLine();
        sb.AppendLine($"  /{name}       -- play the emote");
        sb.AppendLine($"  /stop_{name}  -- stop it");
        sb.AppendLine();
        sb.AppendLine("Other resources can trigger it programmatically:");
        sb.AppendLine();
        sb.AppendLine($"  exports['{name}']:Play()");
        sb.AppendLine($"  exports['{name}']:Stop()");
        sb.AppendLine();
        sb.AppendLine("Troubleshooting:");
        sb.AppendLine("  * If the command works but the animation doesn't visibly play:");
        sb.AppendLine("    the .ycd may not have streamed in. Check the server console for");
        sb.AppendLine($"    a '[{name}] failed to load anim dict' line — that means the .ycd");
        sb.AppendLine("    didn't make it into the stream cache. Restart the resource once.");
        sb.AppendLine("  * If the ped jitters or twists wrong: the source rig likely wasn't");
        sb.AppendLine("    a SKEL_* GTA player skeleton when authored. Re-import in FiveOS");
        sb.AppendLine("    with the GTA Male / Female preset and re-export.");
        return sb.ToString();
    }

    private static string F(float v) =>
        v.ToString("0.######", CultureInfo.InvariantCulture);

    private static string SanitizeName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ' || ch == '-') sb.Append('_');
        }
        var s = sb.ToString();
        if (s.Length == 0) s = "fiveos_emote";
        if (char.IsDigit(s[0])) s = "p_" + s;
        return s;
    }
}
