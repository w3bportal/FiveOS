// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
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
    public static byte[] Build(string assetName, Vector3 bbMin, Vector3 bbMax,
                               Vector3 bsCentre, float bsRadius, float lodDist)
    {
        var ytyp = new YtypFile();
        var nameHash = (MetaHash)JenkHash.GenHash(assetName);
        ytyp.NameHash = nameHash;
        ytyp.CMapTypes = new CMapTypes { name = nameHash };

        var def = new CBaseArchetypeDef
        {
            lodDist = lodDist,
            // 32 = OBJECT (default static prop). CW writes this verbatim.
            flags = 32u,
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
            clipDictionary = (MetaHash)0,
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
