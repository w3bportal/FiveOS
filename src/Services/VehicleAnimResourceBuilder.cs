// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Globalization;
using System.IO;
using System.Text;
using CodeWalker.GameFiles;

namespace FiveOS.Services;

/// <summary>
/// Packages one vehicle clip (.ycd) as a drop-in FiveM resource, or as a
/// singleplayer-style dlc.rpf that streams the clip dictionary.
///
/// FiveM layout:
///     &lt;name&gt;/
///       fxmanifest.lua
///       client.lua          /&lt;name&gt; plays it on the vehicle you're in
///       stream/&lt;dict&gt;.ycd
///       README.txt
///
/// DLC layout (OPEN rpf, mountable via dlclist.xml or a FiveM `mods/` drop):
///     &lt;name&gt;/dlc.rpf
///       setup2.xml, content.xml
///       x64/anim/&lt;name&gt;.rpf/&lt;dict&gt;.ycd
///
/// The dictionary is the file stem (what RequestAnimDict resolves) and the
/// clip is the Hash baked inside the .ycd — two separate strings, exactly
/// like the emote resources FiveOS ships.
/// </summary>
public static class VehicleAnimResourceBuilder
{
    public sealed record Result(string Folder, string DictName, string ClipName, string? RpfPath);

    /// <summary>Dictionary stem for a clip name: <c>&lt;clip&gt;@vehanim</c>.
    /// Kept distinct from the ped-emote <c>@anim</c> suffix so a vehicle clip
    /// and an emote with the same name never collide in FiveM's global
    /// asset namespace.</summary>
    public static string DictFor(string animName) => SanitizeName(animName) + "@vehanim";

    public static string BuildFolder(
        string folderPath, string animName, byte[] ycdBytes, bool loop,
        string? ycdXml = null, string? sourceModel = null)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("folderPath must be non-empty.", nameof(folderPath));
        if (ycdBytes is null || ycdBytes.Length == 0)
            throw new ArgumentException("ycdBytes must be non-empty.", nameof(ycdBytes));

        var clip = SanitizeName(animName);
        var dict = DictFor(animName);
        var utf8 = new UTF8Encoding(false);

        Directory.CreateDirectory(folderPath);
        var streamDir = Path.Combine(folderPath, "stream");
        Directory.CreateDirectory(streamDir);
        // Stale clips from an earlier export under a different name would
        // otherwise pile up in stream/ and register as extra dictionaries.
        foreach (var old in Directory.EnumerateFiles(streamDir, "*.ycd"))
            try { File.Delete(old); } catch { /* best-effort */ }

        File.WriteAllBytes(Path.Combine(streamDir, dict + ".ycd"), ycdBytes);
        if (!string.IsNullOrEmpty(ycdXml))
            File.WriteAllText(Path.Combine(folderPath, dict + ".ycd.xml"), ycdXml, utf8);

        File.WriteAllText(Path.Combine(folderPath, "fxmanifest.lua"), BuildManifest(clip, sourceModel), utf8);
        File.WriteAllText(Path.Combine(folderPath, "client.lua"), BuildClient(clip, dict, loop), utf8);
        File.WriteAllText(Path.Combine(folderPath, "server.lua"), BuildServer(clip), utf8);
        File.WriteAllText(Path.Combine(folderPath, "README.txt"), BuildReadme(clip, dict, loop, ycdBytes.Length, sourceModel), utf8);
        return dict;
    }

    /// <summary>Write <c>&lt;outputDir&gt;/&lt;name&gt;/dlc.rpf</c> carrying the
    /// clip under <c>x64/anim/&lt;name&gt;.rpf</c>. Returns the dlc.rpf path.</summary>
    public static string BuildDlcRpf(string outputDir, string animName, byte[] ycdBytes)
    {
        if (ycdBytes is null || ycdBytes.Length == 0)
            throw new ArgumentException("ycdBytes must be non-empty.", nameof(ycdBytes));
        var name = SanitizeName(animName);
        var dict = DictFor(animName);
        var dlcDir = Path.Combine(Path.GetFullPath(outputDir), name);
        Directory.CreateDirectory(dlcDir);
        var dlcPath = Path.Combine(dlcDir, "dlc.rpf");
        if (File.Exists(dlcPath)) File.Delete(dlcPath);

        var utf8 = new UTF8Encoding(false);
        var changeSet = name.ToUpperInvariant() + "_VEHANIM";
        var animRpfRel = $"dlc_{name}:/%PLATFORM%/anim/{name}.rpf";

        var dlc = RpfFile.CreateNew(dlcDir, "dlc.rpf", RpfEncryption.OPEN);
        RpfPacker.AddFileAtPath(dlc.Root, "setup2.xml", utf8.GetBytes(BuildSetup2Xml(name, changeSet)));
        RpfPacker.AddFileAtPath(dlc.Root, "content.xml", utf8.GetBytes(BuildContentXml(name, changeSet, animRpfRel)));
        var animDir = RpfPacker.GetOrCreateDirChain(dlc.Root, "x64\\anim");
        var nested = RpfFile.CreateNew(animDir, name + ".rpf", RpfEncryption.OPEN);
        RpfPacker.AddFileAtPath(nested.Root, dict + ".ycd", ycdBytes);

        File.WriteAllText(Path.Combine(dlcDir, "README.txt"), BuildDlcReadme(name, dict), utf8);
        return dlcPath;
    }

    /// <summary>Lower-case letters, digits and underscores only — the same
    /// rule the .ycd clip hash uses, so folder, dict and clip always agree.</summary>
    public static string SanitizeName(string raw)
    {
        var sb = new StringBuilder(raw?.Length ?? 0);
        foreach (var ch in raw ?? "")
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ' || ch == '-') sb.Append('_');
        }
        var s = sb.ToString();
        if (s.Length == 0) s = "fiveos_vehanim";
        if (char.IsDigit(s[0])) s = "v_" + s;
        return s;
    }

    // ── FiveM resource ────────────────────────────────────────────────────

    private static string BuildManifest(string name, string? sourceModel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("fx_version 'cerulean'");
        sb.AppendLine("game 'gta5'");
        sb.AppendLine();
        sb.AppendLine("author 'FiveOS'");
        sb.AppendLine($"description 'Vehicle animation: {name}"
            + (string.IsNullOrWhiteSpace(sourceModel) ? "" : $" (built on {Esc(sourceModel!)})") + "'");
        sb.AppendLine("version '1.0.0'");
        sb.AppendLine();
        sb.AppendLine("client_script 'client.lua'");
        sb.AppendLine("server_script 'server.lua'");
        sb.AppendLine();
        sb.AppendLine("-- The .ycd under stream/ is picked up by FiveM's streamer on its own;");
        sb.AppendLine("-- no `files` entry is needed for it.");
        return sb.ToString();
    }

    private static string BuildClient(string name, string dict, bool loop)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- Auto-generated by FiveOS (Vehicle Animation). See README.txt.");
        sb.AppendLine($"-- Dict : {dict}   (file stream/{dict}.ycd -> RequestAnimDict)");
        sb.AppendLine($"-- Clip : {name}   (the Hash inside the .ycd)");
        sb.AppendLine();
        sb.AppendLine($"local DICT = '{dict}'");
        sb.AppendLine($"local CLIP = '{name}'");
        sb.AppendLine($"local LOOP = {(loop ? "true" : "false")}");
        sb.AppendLine();
        sb.AppendLine("local function loadDict()");
        sb.AppendLine("  if HasAnimDictLoaded(DICT) then return true end");
        sb.AppendLine("  RequestAnimDict(DICT)");
        sb.AppendLine("  local tries = 0");
        sb.AppendLine("  while not HasAnimDictLoaded(DICT) and tries < 200 do");
        sb.AppendLine("    Wait(10)");
        sb.AppendLine("    tries = tries + 1");
        sb.AppendLine("  end");
        sb.AppendLine("  return HasAnimDictLoaded(DICT)");
        sb.AppendLine("end");
        sb.AppendLine();
        sb.AppendLine("-- The vehicle you're sitting in, else the closest one within 6 m.");
        sb.AppendLine("local function targetVehicle()");
        sb.AppendLine("  local ped = PlayerPedId()");
        sb.AppendLine("  local veh = GetVehiclePedIsIn(ped, false)");
        sb.AppendLine("  if veh ~= 0 then return veh end");
        sb.AppendLine("  local c = GetEntityCoords(ped)");
        sb.AppendLine("  return GetClosestVehicle(c.x, c.y, c.z, 6.0, 0, 70)");
        sb.AppendLine("end");
        sb.AppendLine();
        sb.AppendLine("local function play(veh, speed)");
        sb.AppendLine("  local res = GetCurrentResourceName()");
        sb.AppendLine("  veh = veh or targetVehicle()");
        sb.AppendLine("  if not veh or veh == 0 or not DoesEntityExist(veh) then");
        sb.AppendLine("    print(('[%s] no vehicle -- sit in one or stand next to it'):format(res))");
        sb.AppendLine("    return false");
        sb.AppendLine("  end");
        sb.AppendLine("  if not loadDict() then");
        sb.AppendLine("    print(('[%s] [FAIL] dict %q did not load -- is stream/%s.ycd present?'):format(res, DICT, DICT))");
        sb.AppendLine("    return false");
        sb.AppendLine("  end");
        sb.AppendLine("  -- PlayEntityAnim(entity, animName, animDict, speed, loop, stayInAnim, p6, delta, bitset)");
        sb.AppendLine("  PlayEntityAnim(veh, CLIP, DICT, 1000.0, LOOP, true, false, 0.0, 0)");
        sb.AppendLine("  if speed and speed > 0 then SetEntityAnimSpeed(veh, DICT, CLIP, speed + 0.0) end");
        sb.AppendLine("  Wait(100)");
        sb.AppendLine("  if IsEntityPlayingAnim(veh, DICT, CLIP, 3) then");
        sb.AppendLine("    print(('[%s] [OK] playing %s/%s on vehicle %d'):format(res, DICT, CLIP, veh))");
        sb.AppendLine("    return true");
        sb.AppendLine("  end");
        sb.AppendLine("  print(('[%s] [FAIL] the game rejected %s/%s -- the clip was built for a different vehicle skeleton?'):format(res, DICT, CLIP))");
        sb.AppendLine("  return false");
        sb.AppendLine("end");
        sb.AppendLine();
        sb.AppendLine("local function stop(veh)");
        sb.AppendLine("  veh = veh or targetVehicle()");
        sb.AppendLine("  if veh and veh ~= 0 and DoesEntityExist(veh) then");
        sb.AppendLine("    StopEntityAnim(veh, CLIP, DICT, 0.0)");
        sb.AppendLine("  end");
        sb.AppendLine("end");
        sb.AppendLine();
        sb.AppendLine("-- Everyone sees it: the command asks the server, the server tells every");
        sb.AppendLine("-- client to play it on the same networked vehicle. Late joiners get the");
        sb.AppendLine("-- currently-running clips replayed when they load in.");
        sb.AppendLine($"-- /{name}          play on your vehicle (for all players)");
        sb.AppendLine($"-- /{name} stop     stop it (for all players)");
        sb.AppendLine($"-- /{name} 0.5      play at half speed");
        sb.AppendLine($"RegisterCommand('{name}', function(_, args)");
        sb.AppendLine("  local veh = targetVehicle()");
        sb.AppendLine("  if not veh or veh == 0 then print(('[%s] no vehicle -- sit in one or stand next to it'):format(GetCurrentResourceName())) return end");
        sb.AppendLine("  local netId = NetworkGetNetworkIdFromEntity(veh)");
        sb.AppendLine("  local a = args[1]");
        sb.AppendLine($"  if a == 'stop' then TriggerServerEvent('{name}:stop', netId) return end");
        sb.AppendLine($"  TriggerServerEvent('{name}:play', netId, tonumber(a))");
        sb.AppendLine("end, false)");
        sb.AppendLine();
        sb.AppendLine($"RegisterNetEvent('{name}:play', function(netId, speed)");
        sb.AppendLine("  if not NetworkDoesNetworkIdExist(netId) then return end");
        sb.AppendLine("  local veh = NetworkGetEntityFromNetworkId(netId)");
        sb.AppendLine("  if veh and veh ~= 0 and DoesEntityExist(veh) then play(veh, speed) end");
        sb.AppendLine("end)");
        sb.AppendLine($"RegisterNetEvent('{name}:stop', function(netId)");
        sb.AppendLine("  if not NetworkDoesNetworkIdExist(netId) then return end");
        sb.AppendLine("  local veh = NetworkGetEntityFromNetworkId(netId)");
        sb.AppendLine("  if veh and veh ~= 0 and DoesEntityExist(veh) then stop(veh) end");
        sb.AppendLine("end)");
        sb.AppendLine();
        sb.AppendLine("-- Other resources: exports['" + name + "']:play(vehicle) / :stop(vehicle) (this client only)");
        sb.AppendLine("-- or TriggerServerEvent('" + name + ":play', NetworkGetNetworkIdFromEntity(vehicle)) for everyone.");
        sb.AppendLine("exports('play', function(veh, speed) return play(veh, speed) end)");
        sb.AppendLine("exports('stop', function(veh) stop(veh) end)");
        sb.AppendLine();
        sb.AppendLine("CreateThread(function() loadDict() end)   -- warm the dict so the first play is instant");
        return sb.ToString();
    }

    /// <summary>Server side: rebroadcast play/stop to every client and remember
    /// which vehicles are playing so late joiners catch up.</summary>
    private static string BuildServer(string name)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- Auto-generated by FiveOS (Vehicle Animation). Keeps the clip in sync for everyone:");
        sb.AppendLine("-- a client asks to play/stop, the server tells ALL clients, and remembers what's");
        sb.AppendLine("-- playing so players who join later see it too.");
        sb.AppendLine("local playing = {}   -- netId -> speed");
        sb.AppendLine();
        sb.AppendLine($"RegisterNetEvent('{name}:play', function(netId, speed)");
        sb.AppendLine("  if type(netId) ~= 'number' then return end");
        sb.AppendLine("  playing[netId] = speed or 1.0");
        sb.AppendLine($"  TriggerClientEvent('{name}:play', -1, netId, speed)");
        sb.AppendLine("end)");
        sb.AppendLine();
        sb.AppendLine($"RegisterNetEvent('{name}:stop', function(netId)");
        sb.AppendLine("  if type(netId) ~= 'number' then return end");
        sb.AppendLine("  playing[netId] = nil");
        sb.AppendLine($"  TriggerClientEvent('{name}:stop', -1, netId)");
        sb.AppendLine("end)");
        sb.AppendLine();
        sb.AppendLine("-- Late joiners: replay whatever is still running once they have loaded in.");
        sb.AppendLine("AddEventHandler('playerJoining', function()");
        sb.AppendLine("  local src = source");
        sb.AppendLine("  SetTimeout(15000, function()");
        sb.AppendLine("    for netId, speed in pairs(playing) do");
        sb.AppendLine($"      TriggerClientEvent('{name}:play', src, netId, speed)");
        sb.AppendLine("    end");
        sb.AppendLine("  end)");
        sb.AppendLine("end)");
        sb.AppendLine();
        sb.AppendLine("-- Forget vehicles that no longer exist so the list doesn't grow forever.");
        sb.AppendLine("CreateThread(function()");
        sb.AppendLine("  while true do");
        sb.AppendLine("    Wait(60000)");
        sb.AppendLine("    for netId in pairs(playing) do");
        sb.AppendLine("      local ent = NetworkGetEntityFromNetworkId(netId)");
        sb.AppendLine("      if not ent or ent == 0 or not DoesEntityExist(ent) then playing[netId] = nil end");
        sb.AppendLine("    end");
        sb.AppendLine("  end");
        sb.AppendLine("end)");
        return sb.ToString();
    }

    private static string BuildReadme(string name, string dict, bool loop, int bytes, string? sourceModel)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{name} — vehicle animation (FiveOS)");
        sb.AppendLine(new string('=', name.Length + 30));
        sb.AppendLine();
        sb.AppendLine($"  Built {DateTime.Now:yyyy-MM-dd HH:mm}, {dict}.ycd is {bytes:N0} bytes.");
        if (!string.IsNullOrWhiteSpace(sourceModel))
            sb.AppendLine($"  Authored on: {sourceModel}");
        sb.AppendLine($"  Dict : {dict}     (file stream/{dict}.ycd)");
        sb.AppendLine($"  Clip : {name}     (Hash inside the .ycd)");
        sb.AppendLine($"  Loop : {(loop ? "yes" : "no — plays once and holds the last frame")}");
        sb.AppendLine();
        sb.AppendLine("INSTALL");
        sb.AppendLine($"  1. Copy this folder into your server's resources/ directory.");
        sb.AppendLine($"  2. Add `ensure {name}` to server.cfg (or restart the resource).");
        sb.AppendLine($"  3. In game, sit in the vehicle (or stand next to it) and type /{name}.");
        sb.AppendLine($"     Every player sees it. /{name} stop ends it for everyone; /{name} 0.5 plays at half speed.");
        sb.AppendLine();
        sb.AppendLine("FROM YOUR OWN SCRIPT");
        sb.AppendLine($"  RequestAnimDict('{dict}')");
        sb.AppendLine($"  while not HasAnimDictLoaded('{dict}') do Wait(0) end");
        sb.AppendLine($"  PlayEntityAnim(vehicle, '{name}', '{dict}', 1000.0, {(loop ? "true" : "false")}, true, false, 0.0, 0)");
        sb.AppendLine($"  -- or: exports['{name}']:play(vehicle)");
        sb.AppendLine();
        sb.AppendLine("NOTES");
        sb.AppendLine("  • The clip animates the bones of the vehicle it was authored on. Other models");
        sb.AppendLine("    only follow it where their bone names match (door_dside_f, bonnet, boot…).");
        sb.AppendLine("  • Doors, bonnet and boot are also driven by the game's own door logic.");
        sb.AppendLine("    If the game fights the clip, stop the doors first:");
        sb.AppendLine("      SetVehicleDoorShut(vehicle, doorIndex, true)  before playing.");
        sb.AppendLine("  • Only bones you keyframed are in the clip — everything else is left alone.");
        return sb.ToString();
    }

    // ── DLC ───────────────────────────────────────────────────────────────

    private static string BuildContentXml(string name, string changeSet, string animRpfRel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<CDataFileMgr__ContentsOfDataFileXml>");
        sb.AppendLine("  <disabledFiles />");
        sb.AppendLine("  <includedXmlFiles />");
        sb.AppendLine("  <includedDataFiles />");
        sb.AppendLine("  <dataFiles>");
        sb.AppendLine("    <Item>");
        sb.AppendLine($"      <filename>{animRpfRel}</filename>");
        sb.AppendLine("      <fileType>RPF_FILE</fileType>");
        sb.AppendLine("      <overlay value=\"false\" />");
        sb.AppendLine("      <disabled value=\"true\" />");
        sb.AppendLine("      <persistent value=\"true\" />");
        sb.AppendLine("    </Item>");
        sb.AppendLine("  </dataFiles>");
        sb.AppendLine("  <contentChangeSets>");
        sb.AppendLine("    <Item>");
        sb.AppendLine($"      <changeSetName>{changeSet}</changeSetName>");
        sb.AppendLine("      <mapChangeSetData />");
        sb.AppendLine("      <filesToInvalidate />");
        sb.AppendLine("      <filesToDisable />");
        sb.AppendLine("      <filesToEnable>");
        sb.AppendLine($"        <Item>{animRpfRel}</Item>");
        sb.AppendLine("      </filesToEnable>");
        sb.AppendLine("      <txdToLoad />");
        sb.AppendLine("      <txdToUnload />");
        sb.AppendLine("      <residentResources />");
        sb.AppendLine("      <unregisterResources />");
        sb.AppendLine("      <requiresLoadingScreen value=\"false\" />");
        sb.AppendLine("    </Item>");
        sb.AppendLine("  </contentChangeSets>");
        sb.AppendLine("  <patchFiles />");
        sb.AppendLine("</CDataFileMgr__ContentsOfDataFileXml>");
        return sb.ToString();
    }

    private static string BuildSetup2Xml(string name, string changeSet)
    {
        var ts = DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<SSetupData>");
        sb.AppendLine($"  <deviceName>dlc_{name}</deviceName>");
        sb.AppendLine("  <datFile>content.xml</datFile>");
        sb.AppendLine($"  <timeStamp>{ts}</timeStamp>");
        sb.AppendLine($"  <nameHash>{name}</nameHash>");
        sb.AppendLine("  <contentChangeSets />");
        sb.AppendLine("  <contentChangeSetGroups>");
        sb.AppendLine("    <Item>");
        sb.AppendLine("      <NameHash>GROUP_STARTUP</NameHash>");
        sb.AppendLine("      <ContentChangeSets>");
        sb.AppendLine($"        <Item>{changeSet}</Item>");
        sb.AppendLine("      </ContentChangeSets>");
        sb.AppendLine("    </Item>");
        sb.AppendLine("  </contentChangeSetGroups>");
        sb.AppendLine("  <startupScript />");
        sb.AppendLine("  <scriptCallstackSize value=\"0\" />");
        sb.AppendLine("  <type>EXTRACONTENT_COMPAT_PACK</type>");
        sb.AppendLine("  <order value=\"30\" />");
        sb.AppendLine("  <minorOrder value=\"0\" />");
        sb.AppendLine("  <isLevelPack value=\"false\" />");
        sb.AppendLine("  <dependencyPackHash />");
        sb.AppendLine("  <requiredVersion />");
        sb.AppendLine("  <subPackCount value=\"0\" />");
        sb.AppendLine("</SSetupData>");
        return sb.ToString();
    }

    private static string BuildDlcReadme(string name, string dict)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{name} — vehicle animation dlc.rpf (FiveOS)");
        sb.AppendLine();
        sb.AppendLine("Singleplayer:");
        sb.AppendLine($"  1. Copy this folder to  mods/update/x64/dlcpacks/{name}/  (OpenIV, mods folder).");
        sb.AppendLine($"  2. Add  <Item>dlcpacks:/{name}/</Item>  to update/update.rpf/common/data/dlclist.xml.");
        sb.AppendLine("FiveM client-side:");
        sb.AppendLine($"  Drop it under FiveM.app/mods/update/x64/dlcpacks/{name}/dlc.rpf.");
        sb.AppendLine();
        sb.AppendLine($"Play it with  PlayEntityAnim(vehicle, '{name}', '{dict}', 1000.0, true, true, false, 0.0, 0)");
        sb.AppendLine($"after RequestAnimDict('{dict}') has loaded.");
        return sb.ToString();
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");
}
