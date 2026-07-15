// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using CodeWalker.GameFiles;
using SharpDX;

namespace YdrWriter;

/// <summary>
/// Emits a minimal binary .ytyp declaring a single drawable archetype that
/// references our streamed .ydr by name. Without this metadata FiveM/RAGE has
/// no <c>CBaseArchetypeDef</c> for the model — it streams the .ydr but never
/// spawns it as a prop, which is why a drag-only .ydr-only resource can be
/// referenced by code (CreateObject) but not placed in a .ymap.
/// </summary>
public static class YtypBuilder
{
    /// <param name="clipDictionaryName">When <paramref name="animated"/> is
    /// true, the name of the clip dictionary (= the sibling .ycd's stem) the
    /// archetype should reference. The game plays the matching clip on any
    /// placed entity of this archetype.</param>
    /// <param name="animated">When true, adds the RAGE archetype flags that
    /// make a placed prop auto-play its bone animation — no script needed:
    /// <c>Has Anim (YCD)</c> (bit 9 = 512) + <c>Auto Start Anim</c>
    /// (bit 19 = 524288), and points <c>clipDictionary</c> at the .ycd.
    /// This is the ytyp side of the Sollumz/CodeWalker animated-prop
    /// workflow, and it works on a plain drawable (.ydr) — no fragment
    /// required (confirmed by the FiveM map-animation docs).</param>
    public static byte[] Build(string assetName, Vector3 bbMin, Vector3 bbMax,
                               Vector3 bsCentre, float bsRadius, float lodDist,
                               string? clipDictionaryName = null, bool animated = false)
    {
        var ytyp = new YtypFile();
        var nameHash = (MetaHash)JenkHash.GenHash(assetName);
        ytyp.NameHash = nameHash;
        ytyp.CMapTypes = new CMapTypes { name = nameHash };

        // 32 = static OBJECT prop (the default). For an animated prop we OR in
        // Has Anim (YCD)=512 + Auto Start Anim=524288 so RAGE self-starts the
        // clip on placement, and reference the clip dictionary by name.
        uint flags = 32u;
        var clipDict = (MetaHash)0;
        if (animated)
        {
            flags |= 512u | 524288u;
            if (!string.IsNullOrEmpty(clipDictionaryName))
                clipDict = (MetaHash)JenkHash.GenHash(clipDictionaryName);
        }

        var def = new CBaseArchetypeDef
        {
            lodDist = lodDist,
            flags = flags,
            specialAttribute = 0u,
            bbMin = bbMin,
            bbMax = bbMax,
            bsCentre = bsCentre,
            bsRadius = bsRadius,
            hdTextureDist = 100f,
            name = nameHash,
            assetName = nameHash,
            assetType = rage__fwArchetypeDef__eAssetType.ASSET_TYPE_DRAWABLE,
            textureDictionary = (MetaHash)0,
            drawableDictionary = (MetaHash)0,
            clipDictionary = clipDict,
            // physicsDictionary MUST be set to the archetype's own name
            // hash even when the bound is embedded inside the YDR — this
            // is the field RAGE reads to identify which physics data to
            // associate with placed entities. Setting it to 0 leaves the
            // entity without a physics archetype binding and the player
            // walks through, even though the YDR's pointer table contains
            // a valid Drawable.Bound. Solution sourced from cfx.re forum
            // "Cannot get collision working on props imported from GTA IV"
            // — same symptom (visible model, no collision), same fix.
            physicsDictionary = nameHash,
        };

        var arch = new Archetype();
        arch.Init(ytyp, ref def);
        ytyp.AddArchetype(arch);

        return ytyp.Save();
    }
}
