// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

namespace YdrWriter.GtaIv;

/// <summary>Maps FiveOS's collision-material names onto GTA IV surface ids.
///
/// The ids are row indices into the game's own
/// <c>common/data/materials/materials.dat</c> — 156 entries, first column, in
/// file order after the version header. Index 0 is DEFAULT.
///
/// ⚠ Do NOT take these from the file's SECOND column. That column is the FX
/// group (GENERIC / VOID / CONCRETE / STONE / …, 41 distinct values) and it is
/// what GIMS IV surfaces in its material picker, so it looks like the right
/// list. It is not an id space. An earlier version of this file used those
/// group names as if their ordinal were the surface id, which put "concrete"
/// at 2 — and 2 is CAR_VOID, a frictionless, car-only surface. Every prop
/// exported before this was fixed carried a bad surface id, and OpenIV cannot
/// catch it because it renders collision regardless.
///
/// The id controls footstep and impact response: audio, decals, particles,
/// tyre grip, and whether a ped can stand on the thing at all. Geometry
/// collides either way, so a wrong id is wrong behaviour rather than no
/// collision — which is exactly why it goes unnoticed.</summary>
internal static class IvCollisionMaterials
{
    /// <summary>DEFAULT — index 0 in materials.dat. Safe, ordinary friction.</summary>
    internal const byte Default = 0;

    /// <summary>Name → GTA IV surface id, verified against a retail
    /// materials.dat. Both the game's own names and FiveOS / GTA V collision
    /// names are accepted, so a project authored for the GTA V exporter
    /// carries over without re-tagging.</summary>
    private static readonly Dictionary<string, byte> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        // --- generic -------------------------------------------------
        ["DEFAULT"] = 0,
        ["GENERIC"] = 0,
        ["NOFRICTION"] = 1,

        // --- stone / masonry -----------------------------------------
        ["CONCRETE"] = 3,
        ["CONCRETE_FREEWAY"] = 4,
        ["PAINTED_ROAD"] = 5,
        ["PLASTER_BOARD"] = 6,
        ["PLASTER"] = 6,
        ["TARMAC"] = 8,
        ["ASPHALT"] = 8,
        ["ROCK"] = 11,
        ["STONE"] = 12,
        ["BRICK_COBBLE"] = 13,
        ["COBBLESTONE"] = 13,
        ["BRICK"] = 14,
        ["BRICK_WALL"] = 14,
        ["MARBLE"] = 15,
        ["CERAMICS"] = 17,
        ["CERAMIC"] = 17,
        ["TILE"] = 17,

        // --- wood ----------------------------------------------------
        ["WOOD"] = 24,
        ["WOOD_SOLID"] = 24,
        ["WOOD_SECTION"] = 24,
        ["WOOD_BOARD"] = 20,
        ["WOOD_COUNTER"] = 21,
        ["WOOD_FENCE"] = 22,
        ["WOOD_HANDRAIL"] = 23,
        ["WOOD_WALL_OLD"] = 25,

        // --- loose ground --------------------------------------------
        ["GRAVEL"] = 29,
        ["SAND"] = 32,
        ["DIRT"] = 34,
        ["DIRT_DRY"] = 34,
        ["MUD"] = 34,
        ["GRASS"] = 37,
        ["FOLIAGE"] = 37,
        ["BUSHES"] = 37,

        // --- metal ---------------------------------------------------
        ["METAL"] = 63,
        ["METAL_SOLID"] = 63,
        ["METAL_AWNING"] = 55,
        ["METAL_GARAGE_DOOR"] = 58,
        ["METAL_GRILL"] = 59,

        // --- glass ---------------------------------------------------
        ["GLASS"] = 74,
        ["GLASS_MEDIUM"] = 74,
        ["GLASS_STRONG"] = 75,
        ["GLASS_WEAK"] = 76,
        ["GLASS_BRICK"] = 73,
        ["GLASS_SHOOT_THROUGH"] = 151,
        ["GLASS_STRONG_SHOOT_THRU"] = 151,

        // --- vehicle -------------------------------------------------
        ["CAR_METAL"] = 79,
        ["CAR_PLASTIC"] = 80,

        // --- soft / synthetic ----------------------------------------
        ["PLASTIC"] = 84,
        ["CARPET"] = 87,
        ["CARPET_SOLID"] = 87,
        ["FABRIC"] = 88,
        ["CLOTH"] = 88,
        ["FABRIC_CLOTH"] = 88,
        ["RUBBER"] = 91,
        ["PAPER"] = 92,
        ["CARDBOARD"] = 93,
        ["WATER"] = 98,
    };

    /// <summary>Resolve a material name to a GTA IV surface id. Unknown names
    /// fall back to DEFAULT (0) rather than to anything exotic — a plain
    /// surface is always a safe answer, whereas a wrong specific one changes
    /// how the prop behaves underfoot.</summary>
    internal static byte Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Default;
        return Ids.TryGetValue(name.Trim(), out var id) ? id : Default;
    }

    /// <summary>Every name this mapper accepts. Used by the CLI to report
    /// what <c>--collision-mat</c> understands.</summary>
    internal static IEnumerable<string> Names
        => Ids.Keys.OrderBy(k => k, StringComparer.Ordinal);
}
