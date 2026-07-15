// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;

namespace FiveOS.Services;

/// <summary>
/// Groups GTA V animation dictionaries by the name prefixes Rockstar / the
/// community already use (move_m@, amb@, veh@, …). Used by the Animation
/// Library sidebar filter — not a gameplay taxonomy, just a browse aid.
/// </summary>
public static class AnimDictCategories
{
    public const string All = "All";

    /// <summary>Display order for the category combo (All first, Other last).</summary>
    public static readonly IReadOnlyList<string> Ordered =
    [
        All,
        "Movement",
        "Ambient",
        "Gestures",
        "Combat",
        "Weapons",
        "Cover",
        "Vehicle",
        "Climb / Swim",
        "Multiplayer",
        "Minigames",
        "Mission",
        "Cutscenes",
        "Reactions",
        "Social",
        "Creatures",
        "Facial / Clothing",
        "Other",
    ];

    /// <summary>Classify a dictionary name (no extension) into a browse category.</summary>
    public static string Classify(string? dictName)
    {
        if (string.IsNullOrWhiteSpace(dictName)) return "Other";
        var n = dictName.Trim().ToLowerInvariant();

        // Movement / locomotion
        if (Starts(n, "move_", "move@", "clip_move", "anim@move_", "anim@move@",
                "locomotion", "walk_", "run_", "sprint_"))
            return "Movement";

        // Ambient world / scenarios
        if (Starts(n, "amb@", "ambient@", "anim@amb@", "anim@amb_", "world_human",
                "scenario@", "anim@scenario"))
            return "Ambient";

        // Gestures / pointing
        if (Starts(n, "gestures@", "gest@", "gestic@", "anim@gestures", "anim@mp_point",
                "cellphone@"))
            return "Gestures";

        // Melee / combat moves (before generic weapons)
        if (Starts(n, "melee@", "combat@", "anim@melee", "anim@combat",
                "guard@", "cop@", "police@"))
            return "Combat";

        if (Starts(n, "weapons@", "weapon@", "anim@weapons", "anim@weapon", "gun@",
                "firearms@"))
            return "Weapons";

        if (Starts(n, "cover@", "anim@cover", "blindfire@"))
            return "Cover";

        // Vehicles
        if (Starts(n, "veh@", "vehicles@", "vehicle@", "anim@veh@", "anim@vehicle",
                "boats@", "bike@", "avion@", "planes@", "trains@", "lowrider@"))
            return "Vehicle";

        // Climb / swim / dive
        if (Starts(n, "climb@", "clim@", "swimming@", "swim@", "diver@", "diving@",
                "anim@move_swim", "anim@swimming"))
            return "Climb / Swim";

        // Multiplayer
        if (Starts(n, "mp_", "anim@mp_", "anim@mp@", "oddjobs@assassinate",
                "oddjobs@basejump", "oddjobs@hunter"))
            return "Multiplayer";

        // Minigames / arcade
        if (Starts(n, "mini@", "minigame@", "arcade@", "darts@", "tennis@", "golf@",
                "anim@arena@", "anim@arcade"))
            return "Minigames";

        // Mission packs
        if (Starts(n, "miss", "heist", "anim@heists", "anim@heist", "random@",
                "fbi@", "franklin@", "michael@", "trevor@", "lester@", "lamar@",
                "family@", "prologue@"))
            return "Mission";

        // Cutscenes / switches
        if (Starts(n, "cutscene@", "cut_", "anim@cutscene", "switch@", "anim@switch",
                "player_transition", "respawn@"))
            return "Cutscenes";

        // Damage / reactions / ragdoll
        if (Starts(n, "reaction@", "damage@", "injured@", "dead@", "dying@", "ragdoll@",
                "get_up@", "nm@"))
            return "Reactions";

        // Social / strip / nightclub / couples
        if (Starts(n, "friends@", "couple@", "special_ped@", "stripper@", "nightclub@",
                "anim@amb@nightclub", "anim@amb@clubhouse", "safe@franklin@",
                "safe@michael@", "safe@trevor@", "timetable@", "savem@", "savef@"))
            return "Social";

        // Animals / creatures
        if (Starts(n, "creatures@", "animal@", "anim@creatures", "dog@", "cat@", "bird@"))
            return "Creatures";

        // Facial / clothing / props attach
        if (Starts(n, "facials@", "facial@", "face_", "clothing", "cloth_", "props@",
                "anim@clothing", "anim@facial"))
            return "Facial / Clothing";

        return "Other";
    }

    private static bool Starts(string name, params string[] prefixes)
    {
        foreach (var p in prefixes)
        {
            if (name.StartsWith(p, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Count dicts per category (includes All = total).</summary>
    public static Dictionary<string, int> CountByCategory(IEnumerable<AnimDictEntry> dicts)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Ordered) counts[c] = 0;
        int total = 0;
        foreach (var d in dicts)
        {
            total++;
            var cat = d.Category ?? Classify(d.Name);
            if (!counts.ContainsKey(cat)) counts[cat] = 0;
            counts[cat]++;
        }
        counts[All] = total;
        return counts;
    }
}
