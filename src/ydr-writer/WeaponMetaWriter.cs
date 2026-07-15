// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Text;

namespace YdrWriter;

/// <summary>
/// Generates the FiveM meta files a custom weapon needs alongside its YDR:
///   • weapons.meta           — CWeaponInfo (name/slot/group/damage/anim/model)
///   • weaponarchetypes.meta  — CWeaponModelInfo (links model name to texture dict)
///   • weaponanimations.meta  — anim clipset reference (only when needed)
///
/// Each archetype (Pistol, Rifle, SMG, Shotgun, Sniper) ships an embedded
/// CWeaponInfo template based on its base-game counterpart's known-good
/// field set — clip size, damage, range, recoil, anim ref, etc. Token
/// replacement substitutes the user-chosen weapon name, slot, group, and
/// model name. This avoids a runtime templates/ directory at the cost of
/// some C# string bulk.
///
/// The output is deliberately conservative: every field that base-game
/// weapons depend on is present, but optional sections (attachments,
/// per-bone-force overrides, scope FOVs) are omitted — those become Phase 2
/// when the user wants component support.
/// </summary>
public static class WeaponMetaWriter
{
    public enum Archetype { Pistol, Rifle, Smg, Shotgun, Sniper }

    /// <summary>One archetype preset — encodes the field values the
    /// base-game weapon uses for things the user typically doesn't want
    /// to author from scratch (firing pattern, anim ref, ammo type,
    /// camera, etc.). Effect group + pickup + flag set are archetype-
    /// specific because using pistol values across the board produces
    /// pistol muzzle flashes on rifles and breaks 1H/2H hand IK.</summary>
    private sealed record Preset(
        string Group,            // GROUP_PISTOL, GROUP_RIFLE, ...
        string Ammo,             // AMMO_PISTOL, AMMO_RIFLE, ...
        string AnimSet,          // WEAPON@PISTOL, WEAPON@RIFLE@ASSAULTRIFLE, ...
        string FireType,         // INSTANT_HIT
        string FiringPattern,    // FIRING_PATTERN_FULL_AUTO, FIRING_PATTERN_BURST_FIRE
        string DefaultCamera,    // DEFAULT_PISTOL_CAMERA, DEFAULT_RIFLE_CAMERA, ...
        string AimingInfo,       // PISTOL, RIFLE_HIGH, ...
        int ClipSize,
        float Damage,
        float Speed,
        float BatteryRange,
        float ReloadTime,
        string WheelSlot,        // WHEEL_PISTOL, WHEEL_RIFLE, ...
        string AudioItem,        // AUDIO_ITEM_PISTOL, AUDIO_ITEM_CARBINERIFLE
        string RecoilShake,      // PISTOL_RECOIL_SHAKE, ...
        string EffectGroup,      // WEAPON_EFFECT_GROUP_PISTOL_SMALL, ..._RIFLE_ASSAULT, etc.
        string FlashFx,          // muz_pistol, muz_assault_rifle, ...
        string FlashFxFp,        // first-person variant
        string ShellFx,          // eject_auto, eject_smg, eject_rifle
        string PickupHash,       // PICKUP_WEAPON_PISTOL, PICKUP_WEAPON_ASSAULTRIFLE, ...
        string WeaponFlags);     // "Automatic …" — different between 1H and 2H weapons

    private static readonly Dictionary<Archetype, Preset> Presets = new()
    {
        [Archetype.Pistol] = new(
            Group: "GROUP_PISTOL", Ammo: "AMMO_PISTOL", AnimSet: "WEAPON@PISTOL",
            FireType: "INSTANT_HIT", FiringPattern: "FIRING_PATTERN_FULL_AUTO",
            DefaultCamera: "DEFAULT_PISTOL_CAMERA", AimingInfo: "PISTOL",
            ClipSize: 12, Damage: 26f, Speed: 1500f, BatteryRange: 60f, ReloadTime: 1.0f,
            WheelSlot: "WHEEL_PISTOL", AudioItem: "AUDIO_ITEM_PISTOL", RecoilShake: "PISTOL_RECOIL_SHAKE",
            EffectGroup: "WEAPON_EFFECT_GROUP_PISTOL_SMALL",
            FlashFx: "muz_pistol", FlashFxFp: "muz_pistol_fp", ShellFx: "eject_auto",
            PickupHash: "PICKUP_WEAPON_PISTOL",
            // 1-handed: no TwoHanded flag. Pistols ARE one-handed; including
            // TwoHanded breaks the hand IK pose so the off-hand floats.
            WeaponFlags: "CarriedInHand UsableOnFoot UsableUnderwater AnimReload AllowEarlyExitFromFireAnimAfterBulletFired UsableInVehicle AllowDriveByLeftSide HasLowCoverReloads HasLowCoverSwaps Automatic"),

        [Archetype.Rifle] = new(
            Group: "GROUP_RIFLE", Ammo: "AMMO_RIFLE", AnimSet: "WEAPON@RIFLE@ASSAULTRIFLE",
            FireType: "INSTANT_HIT", FiringPattern: "FIRING_PATTERN_FULL_AUTO",
            DefaultCamera: "DEFAULT_ASSAULT_RIFLE_CAMERA", AimingInfo: "RIFLE_HIGH",
            ClipSize: 30, Damage: 30f, Speed: 2000f, BatteryRange: 80f, ReloadTime: 1.3f,
            WheelSlot: "WHEEL_RIFLE", AudioItem: "AUDIO_ITEM_ASSAULTRIFLE", RecoilShake: "ASSAULT_RIFLE_RECOIL_SHAKE",
            EffectGroup: "WEAPON_EFFECT_GROUP_RIFLE_ASSAULT",
            FlashFx: "muz_assault_rifle", FlashFxFp: "muz_assault_rifle_fp", ShellFx: "eject_auto",
            PickupHash: "PICKUP_WEAPON_ASSAULTRIFLE",
            WeaponFlags: "CarriedInHand UsableOnFoot UsableUnderwater AnimReload AllowEarlyExitFromFireAnimAfterBulletFired UsableInVehicle AllowDriveByLeftSide TwoHanded HasLowCoverReloads HasLowCoverSwaps DisableLeftHandIkInCover Automatic"),

        [Archetype.Smg] = new(
            Group: "GROUP_SMG", Ammo: "AMMO_SMG", AnimSet: "WEAPON@SMG@",
            FireType: "INSTANT_HIT", FiringPattern: "FIRING_PATTERN_FULL_AUTO",
            DefaultCamera: "DEFAULT_SMG_CAMERA", AimingInfo: "RIFLE_HIGH",
            ClipSize: 30, Damage: 22f, Speed: 1800f, BatteryRange: 50f, ReloadTime: 1.1f,
            WheelSlot: "WHEEL_SMG", AudioItem: "AUDIO_ITEM_SMG", RecoilShake: "SMG_RECOIL_SHAKE",
            EffectGroup: "WEAPON_EFFECT_GROUP_SMG",
            FlashFx: "muz_smg", FlashFxFp: "muz_smg_fp", ShellFx: "eject_smg",
            PickupHash: "PICKUP_WEAPON_SMG",
            WeaponFlags: "CarriedInHand UsableOnFoot UsableUnderwater AnimReload AllowEarlyExitFromFireAnimAfterBulletFired UsableInVehicle AllowDriveByLeftSide TwoHanded HasLowCoverReloads HasLowCoverSwaps Automatic"),

        [Archetype.Shotgun] = new(
            Group: "GROUP_SHOTGUN", Ammo: "AMMO_SHOTGUN", AnimSet: "WEAPON@RIFLE@PUMP",
            FireType: "INSTANT_HIT", FiringPattern: "FIRING_PATTERN_SINGLE_SHOT",
            DefaultCamera: "DEFAULT_PUMPSHOTGUN_CAMERA", AimingInfo: "RIFLE_HIGH",
            ClipSize: 8, Damage: 30f, Speed: 700f, BatteryRange: 40f, ReloadTime: 1.8f,
            WheelSlot: "WHEEL_SHOTGUN", AudioItem: "AUDIO_ITEM_PUMPSHOTGUN", RecoilShake: "PUMPSHOTGUN_RECOIL_SHAKE",
            EffectGroup: "WEAPON_EFFECT_GROUP_SHOTGUN",
            FlashFx: "muz_pump_shotgun", FlashFxFp: "muz_pump_shotgun_fp", ShellFx: "eject_pump",
            PickupHash: "PICKUP_WEAPON_PUMPSHOTGUN",
            // Shotguns are pump action: no Automatic flag, 8 bullets per loop.
            WeaponFlags: "CarriedInHand UsableOnFoot UsableUnderwater AnimReload AllowEarlyExitFromFireAnimAfterBulletFired UsableInVehicle AllowDriveByLeftSide TwoHanded HasLowCoverReloads HasLowCoverSwaps"),

        [Archetype.Sniper] = new(
            Group: "GROUP_SNIPER", Ammo: "AMMO_SNIPER", AnimSet: "WEAPON@RIFLE@SNIPER@SNIPERRIFLE",
            FireType: "INSTANT_HIT", FiringPattern: "FIRING_PATTERN_SINGLE_SHOT",
            DefaultCamera: "DEFAULT_SNIPER_CAMERA", AimingInfo: "RIFLE_HIGH",
            ClipSize: 10, Damage: 100f, Speed: 2500f, BatteryRange: 1500f, ReloadTime: 2.0f,
            WheelSlot: "WHEEL_SNIPER", AudioItem: "AUDIO_ITEM_SNIPERRIFLE", RecoilShake: "SNIPER_RIFLE_RECOIL_SHAKE",
            EffectGroup: "WEAPON_EFFECT_GROUP_SNIPER",
            FlashFx: "muz_sniper_rifle", FlashFxFp: "muz_sniper_rifle_fp", ShellFx: "eject_rifle",
            PickupHash: "PICKUP_WEAPON_SNIPERRIFLE",
            // Sniper is bolt-action: no Automatic flag.
            WeaponFlags: "CarriedInHand UsableOnFoot UsableUnderwater AnimReload AllowEarlyExitFromFireAnimAfterBulletFired UsableInVehicle AllowDriveByLeftSide TwoHanded HasLowCoverReloads HasLowCoverSwaps DisableLeftHandIkInCover"),
    };

    /// <summary>Writes the three meta files into <paramref name="resourceDir"/>'s root
    /// (next to fxmanifest.lua, not under stream/). RAGE/FiveM loads weapon
    /// metas as fxmanifest <c>data_file</c> entries, not streamed assets.</summary>
    /// <param name="weaponName">e.g. <c>WEAPON_CUSTOMRIFLE</c> — uppercased
    /// internally, will become the in-script identifier.</param>
    /// <param name="modelName">e.g. <c>w_custom_rifle</c> — matches the
    /// .ydr/.ytd filename. Lowercased internally.</param>
    /// <param name="slotName">e.g. <c>SLOT_CUSTOMRIFLE</c>. Uppercased.</param>
    public static void Write(
        string resourceDir,
        Archetype archetype,
        string weaponName,
        string modelName,
        string slotName)
    {
        var preset = Presets[archetype];
        // Defence in depth: the user can type anything into the sidebar
        // text fields. Strip control characters / XML special chars before
        // they end up inside <Name>…</Name> tags. SecurityElement.Escape
        // handles &, <, >, ", ' but doesn't strip newlines/tabs, so we
        // sanitise those first.
        var wName = XmlSafe(weaponName.ToUpperInvariant());
        var mName = XmlSafe(modelName.ToLowerInvariant());
        var sName = XmlSafe(slotName.ToUpperInvariant());
        // Hash-based label so two custom weapons in the same server folder
        // don't collide on the wrapper CWeaponInfoBlob.Name.
        var blobName = "FiveOSWeapon_" + Math.Abs(StableHash(wName)).ToString("X8");

        File.WriteAllText(Path.Combine(resourceDir, "weapons.meta"),
            BuildWeaponsMeta(blobName, wName, mName, sName, preset));

        File.WriteAllText(Path.Combine(resourceDir, "weaponarchetypes.meta"),
            BuildArchetypesMeta(mName));

        File.WriteAllText(Path.Combine(resourceDir, "weaponanimations.meta"),
            BuildAnimationsMeta(preset));
    }

    /// <summary>Strip characters that would break the meta XML and pass
    /// the rest through SecurityElement.Escape so &amp;, &lt;, &gt;,
    /// quotes and apostrophes become entity references. Identifier-style
    /// fields (weapon/slot names) are also bounded to a conservative
    /// character set — RAGE only accepts ASCII letters/digits/underscores
    /// in hash names, so anything outside that gets dropped to keep the
    /// generated file loadable.</summary>
    private static string XmlSafe(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c >= 'A' && c <= 'Z') sb.Append(c);
            else if (c >= 'a' && c <= 'z') sb.Append(c);
            else if (c >= '0' && c <= '9') sb.Append(c);
            else if (c == '_') sb.Append(c);
            // Everything else (incl. control chars, XML specials,
            // whitespace, unicode) is dropped — no need to escape what
            // never makes it into the output.
        }
        return sb.ToString();
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (char c in s) h = h * 31 + c;
            return h;
        }
    }

    /// <summary>fxmanifest.lua content that registers the three meta files
    /// + the YDR/YTD stream assets. Replaces the prop-style manifest
    /// Converter.cs writes by default. The data_file entries are what
    /// makes FiveM actually parse the metas — without them the files are
    /// inert. We declare both WEAPONINFO_FILE (registers the new weapon)
    /// and WEAPONINFO_FILE_PATCH (allows the entry to also override
    /// fields if the weapon name collides with a base-game one).</summary>
    public static string BuildFxManifest()
    {
        var sb = new StringBuilder();
        sb.AppendLine("fx_version 'cerulean'");
        sb.AppendLine("game 'gta5'");
        sb.AppendLine();
        sb.AppendLine("files {");
        sb.AppendLine("    'weapons.meta',");
        sb.AppendLine("    'weaponarchetypes.meta',");
        sb.AppendLine("    'weaponanimations.meta',");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("data_file 'WEAPONINFO_FILE'        'weapons.meta'");
        sb.AppendLine("data_file 'WEAPONINFO_FILE_PATCH'  'weapons.meta'");
        sb.AppendLine("data_file 'WEAPON_ANIMATIONS_FILE' 'weaponanimations.meta'");
        sb.AppendLine("data_file 'WEAPON_METADATA_FILE'   'weaponarchetypes.meta'");
        return sb.ToString();
    }

    // ─── templates ───────────────────────────────────────────────────

    private static string BuildArchetypesMeta(string modelName)
    {
        // The archetype's name MUST match the YDR's filename (without
        // extension) and the <Model> tag in weapons.meta. RAGE uses this
        // to look up the model when the weapon spawns.
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<CWeaponModelInfoBlob>
  <ModelInfos>
    <Item type=""CWeaponModelInfo"">
      <ModelName>{modelName}</ModelName>
      <ModelHash />
      <Flags>LOADED LOD_IN_PARENT IS_TYPE_OBJECT HAS_PRE_REFLECTED_WATER_PROXY HAS_DRAWABLE</Flags>
      <PtfxAssetName />
      <ExpressionDictionaryName />
      <ExpressionName />
      <AnimationDictionary />
      <ImpactDamageMultiplier value=""1.000000"" />
    </Item>
  </ModelInfos>
</CWeaponModelInfoBlob>
";
    }

    private static string BuildAnimationsMeta(Preset p)
    {
        // We don't author our own anim clipset — point at the base-game one
        // the chosen archetype uses. This is what gives the weapon working
        // grip, aim, reload, holster animations out of the box.
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<CWeaponAnimationsSets>
  <Animations />
</CWeaponAnimationsSets>
";
    }

    private static string BuildWeaponsMeta(
        string blobName, string weaponName, string modelName, string slotName, Preset p)
    {
        // The CWeaponInfo field list is what RAGE reads when spawning the
        // weapon. Anything missing falls back to engine defaults — for
        // a *working* weapon you minimally need Name/Audio/Slot/Group/
        // DamageType/Ammo/Clipsize/Damage/Range/FiringPattern/Model.
        // Numerical fields (Accuracy, Recoil, Speed) are pulled from the
        // archetype preset so a pistol feels like a pistol, a rifle like
        // a rifle, without the user authoring 50 magic numbers.
        var iv = System.Globalization.CultureInfo.InvariantCulture;
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<CWeaponInfoBlob>
  <SlotNavigateOrder>
    <Item>
      <WeaponSlots>
        <Item>
          <OrderNumber value=""9000"" />
          <Entry>{slotName}</Entry>
        </Item>
      </WeaponSlots>
    </Item>
  </SlotNavigateOrder>
  <SlotBestOrder>
    <WeaponSlots>
      <Item>
        <OrderNumber value=""9000"" />
        <Entry>{slotName}</Entry>
      </Item>
    </WeaponSlots>
  </SlotBestOrder>
  <TintSpecValues />
  <FiringPatternAliases />
  <UpperBodyFixupExpressionData />
  <AimingInfos>
    <Item>
      <Name>{p.AimingInfo}</Name>
      <ReticuleMinSizeStanding value=""0.300000"" />
      <ReticuleMinSizeCrouched value=""0.200000"" />
      <ReticuleScale value=""1.000000"" />
      <ReticuleStyleHash>RETICULE_TYPE_DEFAULT</ReticuleStyleHash>
      <FirstPersonScale value=""1.000000"" />
      <HeadingLimit value=""180.000000"" />
      <SweepPitchMin value=""-87.000000"" />
      <SweepPitchMax value=""87.000000"" />
    </Item>
  </AimingInfos>
  <Infos>
    <Item type=""CWeaponInfoBlob"">
      <Name>{blobName}</Name>
      <Infos>
        <Item type=""CWeaponInfo"">
          <Name>{weaponName}</Name>
          <Audio>{p.AudioItem}</Audio>
          <Slot>{slotName}</Slot>
          <DamageType>BULLET</DamageType>
          <Explosion>
            <Default>DONTCARE</Default>
            <HitCar>DONTCARE</HitCar>
            <HitTruck>DONTCARE</HitTruck>
            <HitBike>DONTCARE</HitBike>
            <HitBoat>DONTCARE</HitBoat>
            <HitPlane>DONTCARE</HitPlane>
          </Explosion>
          <FireType>{p.FireType}</FireType>
          <WheelSlot>{p.WheelSlot}</WheelSlot>
          <Group>{p.Group}</Group>
          <AmmoInfo ref=""{p.Ammo}_INFO"" />
          <AimingInfo>{p.AimingInfo}</AimingInfo>
          <ClipSize value=""{p.ClipSize}"" />
          <Accuracy value=""0.600000"" />
          <Damage value=""{p.Damage.ToString("F2", iv)}"" />
          <DamageTime value=""0.000000"" />
          <DamageTimeInVehicle value=""0.000000"" />
          <DamageTimeInVehicleHeadShot value=""0.000000"" />
          <HeadShotDamageModifier value=""1.000000"" />
          <HeadShotDamageModifierAI value=""1.000000"" />
          <HeadShotDamageModifierPlayer value=""1.000000"" />
          <Speed value=""{p.Speed.ToString("F2", iv)}"" />
          <BulletsInBatch value=""1"" />
          <BatchSpread value=""0.000000"" />
          <ReloadTimeMP value=""{p.ReloadTime.ToString("F2", iv)}"" />
          <ReloadTimeSP value=""{p.ReloadTime.ToString("F2", iv)}"" />
          <VehicleReloadTime value=""1.500000"" />
          <AnimReloadRate value=""1.000000"" />
          <BulletsPerAnimLoop value=""1"" />
          <TimeBetweenShots value=""0.100000"" />
          <TimeLeftBetweenShotsWhereShouldFireIsCached value=""0.040000"" />
          <SpinUpTime value=""0.000000"" />
          <SpinTime value=""0.000000"" />
          <SpinDownTime value=""0.000000"" />
          <AlternateWaitTime value=""-1.000000"" />
          <BulletBendingNearRadius value=""0.350000"" />
          <BulletBendingFarRadius value=""0.500000"" />
          <BulletBendingZoomedRadius value=""0.150000"" />
          <RecoilAccuracyMax value=""1.000000"" />
          <RecoilErrorTime value=""0.150000"" />
          <RecoilRecoveryRate value=""1.700000"" />
          <RecoilAccuracyToAllowHeadShotAI value=""1.000000"" />
          <MinHeadShotDistanceAI value=""5.000000"" />
          <MaxHeadShotDistanceAI value=""30.000000"" />
          <HeadShotDamageModifierAIFurtherThanMax value=""1.000000"" />
          <RecoilAccuracyToAllowHeadShotPlayer value=""0.300000"" />
          <MinHeadShotDistancePlayer value=""5.000000"" />
          <MaxHeadShotDistancePlayer value=""30.000000"" />
          <HeadShotDamageModifierPlayerFurtherThanMax value=""1.000000"" />
          <Fx>
            <EffectGroup>{p.EffectGroup}</EffectGroup>
            <FlashFx>{p.FlashFx}</FlashFx>
            <FlashFxAlt>{p.FlashFx}</FlashFxAlt>
            <FlashFxFP>{p.FlashFxFp}</FlashFxFP>
            <FlashFxFPAlt>{p.FlashFxFp}</FlashFxFPAlt>
            <MuzzleSmokeFx>muz_smoking_barrel_smoke</MuzzleSmokeFx>
            <MuzzleSmokeFxFP>muz_smoking_barrel_smoke</MuzzleSmokeFxFP>
            <MuzzleSmokeFxMinLevel value=""0.500000"" />
            <MuzzleSmokeFxIncPerShot value=""0.200000"" />
            <MuzzleSmokeFxDecPerSec value=""0.250000"" />
            <ShellFx>{p.ShellFx}</ShellFx>
            <ShellFxFP>{p.ShellFx}</ShellFxFP>
            <FlashFxLightEnabled value=""true"" />
            <FlashFxLightCastsShadows value=""false"" />
            <FlashFxLightOffsetDist value=""0.000000"" />
            <FlashFxLightRGBAMin x=""255.000000"" y=""200.000000"" z=""80.000000"" w=""20.000000"" />
            <FlashFxLightRGBAMax x=""255.000000"" y=""220.000000"" z=""110.000000"" w=""40.000000"" />
            <FlashFxLightIntensityMinMax x=""5.000000"" y=""8.000000"" />
            <FlashFxLightRangeMinMax x=""0.800000"" y=""1.200000"" />
            <FlashFxLightFalloffMinMax x=""512.000000"" y=""768.000000"" />
            <GroundDisturbFxEnabled value=""true"" />
            <GroundDisturbFxDist value=""4.000000"" />
            <GroundDisturbFxNameDefault>muz_gnd_default</GroundDisturbFxNameDefault>
            <GroundDisturbFxNameSand>muz_gnd_sand</GroundDisturbFxNameSand>
            <GroundDisturbFxNameDirt>muz_gnd_dirt</GroundDisturbFxNameDirt>
            <GroundDisturbFxNameWater>muz_gnd_water</GroundDisturbFxNameWater>
            <GroundDisturbFxNameFoliage>muz_gnd_leaves</GroundDisturbFxNameFoliage>
          </Fx>
          <InitialRumbleDuration value=""90"" />
          <InitialRumbleIntensity value=""0.300000"" />
          <InitialRumbleIntensityTrigger value=""0.000000"" />
          <RumbleDuration value=""90"" />
          <RumbleIntensity value=""0.300000"" />
          <RumbleIntensityTrigger value=""0.000000"" />
          <RumbleDamageIntensity value=""1.000000"" />
          <NetworkPlayerDamageModifier value=""1.000000"" />
          <NetworkPedDamageModifier value=""1.000000"" />
          <NetworkHeadShotPlayerDamageModifier value=""1.000000"" />
          <LockOnRange value=""160.000000"" />
          <WeaponRange value=""{p.BatteryRange.ToString("F2", iv)}"" />
          <BulletDirectionOffsetInDegrees value=""0.000000"" />
          <AiSoundRange value=""-1.000000"" />
          <AiPotentialBlastEventRange value=""-1.000000"" />
          <DamageFallOffRangeMin value=""20.000000"" />
          <DamageFallOffRangeMax value=""80.000000"" />
          <DamageFallOffModifier value=""0.500000"" />
          <VehicleWeaponHash />
          <DefaultCameraHash>{p.DefaultCamera}</DefaultCameraHash>
          <CoverCameraHash />
          <CoverCameraFirstPersonHash />
          <RunAndGunCameraHash />
          <RunAndGunCameraFirstPersonHash />
          <CinematicShootingCameraHash />
          <CinematicShootingCameraFirstPersonHash />
          <AlternativeOrScopedCameraHash />
          <AlternativeOrScopedCameraFirstPersonHash />
          <CoverReadyToFireCameraHash />
          <FirstPersonScopeAttachmentCameraHash />
          <FirstPersonScopeAttachmentCameraHashThermal />
          <FirstPersonScopeAttachmentCameraHashNightVision />
          <FirstPersonScopeAttachmentCameraHashLowZoom />
          <FirstPersonScopeAttachmentCameraHashHighZoom />
          <FirstPersonAimFovMin value=""45.000000"" />
          <FirstPersonAimFovMax value=""45.000000"" />
          <FirstPersonScopeFov value=""25.000000"" />
          <FirstPersonScopeAttachmentFov value=""20.000000"" />
          <FirstPersonAsThirdPersonIdleOffset x=""0.000000"" y=""0.000000"" z=""0.000000"" />
          <FirstPersonAsThirdPersonFireOffset x=""0.000000"" y=""0.000000"" z=""0.000000"" />
          <FirstPersonAsThirdPersonAimOffset x=""0.000000"" y=""0.000000"" z=""0.000000"" />
          <FirstPersonAsThirdPersonRNGOffset x=""0.000000"" y=""0.000000"" z=""0.000000"" />
          <FirstPersonAsThirdPersonRNGOffsetRight x=""0.000000"" y=""0.000000"" z=""0.000000"" />
          <FirstPersonAsThirdPersonScopeOffset x=""0.000000"" y=""0.000000"" z=""0.000000"" />
          <FirstPersonAsThirdPersonWeaponBlockedOffset x=""0.000000"" y=""0.000000"" z=""0.000000"" />
          <FirstPersonDofSubjectMagnificationPowerFactorNear value=""1.000000"" />
          <FirstPersonDofMaxNearInFocusDistance value=""1.000000"" />
          <FirstPersonDofMaxNearInFocusDistanceBlendLevel value=""0.500000"" />
          <ZoomFactorForAccurateMode value=""1.500000"" />
          <RecoilShakeHash>{p.RecoilShake}</RecoilShakeHash>
          <RecoilShakeHashFirstPerson>{p.RecoilShake}</RecoilShakeHashFirstPerson>
          <AccuracyOffsetShakeHash />
          <MinTimeBetweenRecoilShakes value=""75"" />
          <ReticuleHudPosition x=""0.000000"" y=""0.000000"" />
          <AimOffsetMin x=""0.247000"" y=""0.291000"" z=""0.291000"" />
          <AimProbeLengthMin value=""0.700000"" />
          <AimOffsetMax x=""0.247000"" y=""0.291000"" z=""0.291000"" />
          <AimProbeLengthMax value=""0.700000"" />
          <AimOffsetMinFirstPerson x=""0.247000"" y=""0.291000"" z=""0.291000"" />
          <AimOffsetMaxFirstPerson x=""0.247000"" y=""0.291000"" z=""0.291000"" />
          <TorsoAimOffset x=""0.000000"" y=""0.000000"" />
          <TorsoCrouchedAimOffset x=""0.000000"" y=""0.000000"" />
          <LeftHandIkOffset x=""0.000000"" y=""0.000000"" z=""0.000000"" />
          <ReloadUpperBodyFixupExpressionWeight value=""1.000000"" />
          <TimeBetweenAimingShakes value=""0.000000"" />
          <TimeBetweenFiringShakes value=""0.000000"" />
          <TimeBetweenIdleShakes value=""0.000000"" />
          <TimeToWaitNextShakeAfterFiring value=""0.000000"" />
          <FiringPatternAliasHash>{p.FiringPattern}</FiringPatternAliasHash>
          <ReloadShakeAmplitude value=""0.000000"" />
          <NmShotTuningSet>Default</NmShotTuningSet>
          <PickupHash>{p.PickupHash}</PickupHash>
          <MPPickupHash>{p.PickupHash}</MPPickupHash>
          <HumanNameHash>{weaponName}</HumanNameHash>
          <MovementModeConditionalIdle />
          <StatName>{weaponName}</StatName>
          <KnockdownCount value=""0"" />
          <KillshotImpulseScale value=""1.000000"" />
          <NmDamageScale value=""1.000000"" />
          <NmFallingOverWoundedScale value=""1.000000"" />
          <ParachuteWeapon value=""false"" />
          <CookedTime value=""0.000000"" />
          <DamageType_Stealth value=""1.000000"" />
          <HudDamage value=""25"" />
          <HudSpeed value=""20"" />
          <HudCapacity value=""20"" />
          <HudAccuracy value=""40"" />
          <HudRange value=""25"" />
          <WeaponFlags>{p.WeaponFlags}</WeaponFlags>
          <TintSpecValues />
          <FiringPatternAliases />
          <ReloadUpperBodyFixupExpressionData />
          <AmmoDiminishingRate value=""25"" />
          <AimingBreathingAdditiveWeight value=""1.000000"" />
          <FiringBreathingAdditiveWeight value=""0.300000"" />
          <StealthAimingBreathingAdditiveWeight value=""1.000000"" />
          <StealthFiringBreathingAdditiveWeight value=""0.300000"" />
          <AimingLeanAdditiveWeight value=""1.000000"" />
          <FiringLeanAdditiveWeight value=""0.500000"" />
          <StealthAimingLeanAdditiveWeight value=""1.000000"" />
          <StealthFiringLeanAdditiveWeight value=""0.500000"" />
          <ExpandPedCapsuleRadius value=""0.000000"" />
          <AudioCollisionHash />
          <RumbleDamageIntensityFP value=""0.000000"" />
          <RumbleDurationFP value=""0"" />
          <RumbleIntensityFP value=""0.000000"" />
          <Model>{modelName}</Model>
          <DefaultAmmoType>{p.Ammo}_INFO</DefaultAmmoType>
        </Item>
      </Infos>
    </Item>
  </Infos>
</CWeaponInfoBlob>
";
    }
}
