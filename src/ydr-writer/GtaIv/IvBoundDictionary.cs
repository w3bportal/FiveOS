// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using RageLib.Resources;
using RageLib.Resources.Common;
using RageLib.Resources.GTA4;
using RageLib.Resources.GTA4.PC.Bounds;

namespace YdrWriter.GtaIv;

/// <summary>A GTA IV bounds dictionary (<c>.wbd</c>) — the container that
/// actually gives a prop its collision.
///
/// ⚠ A <c>.wbn</c> named after a model does NOTHING for a prop, however valid
/// the file is. That was established the hard way: converted props rendered
/// perfectly and had no collision, and
/// <c>HasCollisionForModelLoaded(modelHash)</c> never returned true no matter
/// how long it was given.
///
/// The evidence, from decrypting every .img in a retail GTA IV install (52,720
/// entries):
///
///   * All 396 <c>.wbn</c> basenames in the whole game were cross-referenced
///     against all 28,454 model names declared across every .ide. The overlap
///     is ZERO. Every .wbn is named <c>&lt;wplgroup&gt;_&lt;n&gt;</c> —
///     manhat02_4, bronx_e_11 — it is baked static collision for a section of
///     the MAP, never collision for a model.
///   * The prop archives under pc/data/maps/props/ contain no .wbn at all, and
///     one or two .wbd each.
///   * All 1,974 prop-collision bindings in the game are dictionary entries
///     keyed by the Jenkins hash of the model name, resolving to a .wdr in the
///     same archive.
///
/// So a prop's bound is looked up by NAME HASH in a dictionary. That is the
/// only association the engine makes, and it is why nothing else worked.
///
/// The layout matches what every retail .wbd does: hashes strictly ascending
/// (the engine binary-searches them), ParentPointer zero, and count equal to
/// capacity on both lists.</summary>
internal sealed class IvBoundDictionary : PgDictionary32<PhBound>
{
    /// <summary>Build a dictionary from name/bound pairs. Names are hashed
    /// lower-cased, exactly as the .ide archetype name is hashed, and sorted
    /// ascending because the lookup is a binary search — an unsorted table
    /// silently fails to find entries rather than erroring.</summary>
    internal static Resource5<IvBoundDictionary> Build(
        IReadOnlyList<(string Name, PhBound Bound)> entries)
    {
        var ordered = entries
            .Select(e => (Hash: CodeWalker.GameFiles.JenkHash.GenHash(e.Name.ToLowerInvariant()), e.Bound))
            .OrderBy(e => e.Hash)
            .ToList();

        var dict = new IvBoundDictionary
        {
            VFT = 0,
            ParentPointer = 0,
            Count = (uint)ordered.Count,
            Hashes = new SimpleList32<uint>
            {
                Entries = new SimpleArray<uint>(ordered.Select(e => e.Hash).ToArray()),
            },
            Values = new ResourcePointerList32<PhBound>
            {
                Entries = new ResourcePointerArray32<PhBound>
                {
                    data_items = ordered.Select(e => e.Bound).ToList(),
                },
            },
        };

        return new Resource5<IvBoundDictionary>
        {
            Version = 32,            // same resource version as a .wbn
            ResourceData = dict,
        };
    }
}
