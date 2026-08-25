// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Numerics;
using CodeWalker.GameFiles;

namespace YdrWriter.GtaIv;

/// <summary>Produces the placement files a converted prop needs in order to
/// actually exist in the world: an <c>.ide</c> that declares the archetype and
/// a <c>.wpl</c> that puts an instance of it somewhere.
///
/// Without these a .wdr is just a file sitting in an .img — the game has no
/// idea it exists. The exporter used to leave the .ide line for the user to
/// hand-write; this writes it from bounds already computed during export.</summary>
internal static class IvPlacementWriter
{
    /// <summary>Flags on the overwhelming majority of Rockstar's own map
    /// objects — 644 of the ~850 entries in manhat01.ide carry 0xC00000, with
    /// plain 0 the only other common value. Matching theirs keeps a converted
    /// prop behaving like the stock geometry around it.</summary>
    internal const uint DefaultObjectFlags = 12582912;   // 0xC00000

    /// <summary>Draw distance in metres. Rockstar's own values run from 60 to
    /// 700 depending on how big the object is; 100 suits a prop.</summary>
    internal const float DefaultDrawDistance = 100f;

    /// <summary>Write <paramref name="name"/>.ide declaring the archetype.</summary>
    /// <param name="txdName">Texture dictionary, or "null" when the textures
    /// are embedded in the .wdr — which is what this exporter produces unless
    /// asked for a separate .wtd.</param>
    internal static string WriteIde(
        string outDir, string name, IvDrawable drawable, string txdName,
        float drawDistance = DefaultDrawDistance, uint flags = DefaultObjectFlags)
    {
        var (centre, radius, min, max) = drawable.Bounds();

        var ide = new IvIde();
        ide.Objects.Add(new IvIde.ObjectDef
        {
            Name = name,
            TxdName = string.IsNullOrWhiteSpace(txdName) ? "null" : txdName,
            DrawDistance = drawDistance,
            Flags = flags,
            Unknown = 0,
            BoundsMin = (min.X, min.Y, min.Z),
            BoundsMax = (max.X, max.Y, max.Z),
            Centre = (centre.X, centre.Y, centre.Z),
            Radius = radius,
            WddName = "null",
        });

        var path = Path.Combine(outDir, name + ".ide");
        ide.Save(path);
        return path;
    }

    /// <summary>Write <paramref name="name"/>.wpl placing one instance at
    /// <paramref name="position"/>, unrotated.
    ///
    /// A .wpl stores only the Jenkins hash of the model name, so this and the
    /// .ide have to agree on that name exactly — both hash the same lower-cased
    /// string.</summary>
    internal static string WriteWpl(string outDir, string name, Vector3 position)
    {
        var wpl = new IvWpl();
        wpl.Objects.Add(NewInstance(JenkHash.GenHash(name.ToLowerInvariant()),
                                    position, new Vector4(0, 0, 0, 1)));

        var path = Path.Combine(outDir, name + ".wpl");
        wpl.Save(path);
        return path;
    }

    /// <summary>A placement record with the four opaque fields filled in the
    /// way GIMS IV fills them for a plain object.
    ///
    /// Their meaning was checked against Rockstar's manhat01.wpl (855 records):
    ///   unk1  384 on 692 of them, 385/386 on the rest — 384 is the norm
    ///   unk2  a LOD parent index, always valid in a city map; GIMS uses -1
    ///         for "this object has no LOD", which is the case for a prop
    ///   unk3  small group ids (1..24); GIMS writes 0 and ships working maps
    ///   unk4  a per-instance draw-distance override — -1 on 808 of 855, with
    ///         100/70/60/30 making up the rest, so -1 means "use the .ide"</summary>
    private static IvWpl.Instance NewInstance(uint hash, Vector3 position, Vector4 rotation)
        => new()
        {
            Position = position,
            Rotation = rotation,
            NameHash = hash,
            Unknown1 = 384,
            Unknown2 = -1,
            Unknown3 = 0,
            Unknown4 = -1f,
        };

    internal sealed record YmapResult(IvWpl Wpl, List<string> Notes, int Unnamed, int Skipped);

    /// <summary>Translate a GTA V map placement file into a GTA IV one.
    ///
    /// Both games place an entity as a position plus a rotation quaternion, in
    /// metres, on a Z-up right-handed axis — so the geometry is a direct copy
    /// and no unit or axis conversion is involved.
    ///
    /// Two things do NOT carry over. First the archetype: GTA V resolves an
    /// entity through a .ytyp by archetype hash, GTA IV through a name declared
    /// in an .ide. Both hash names the same way (Jenkins one-at-a-time), so an
    /// entity whose name is recoverable lines up with an .ide entry of the same
    /// name; entities whose name CodeWalker could not resolve keep their
    /// original hash and are counted, not dropped, because a silently missing
    /// placement is far harder to notice than a reported one.
    ///
    /// Second, LOD structure: GTA V's parent/child LOD links are indices into
    /// the same .ymap, and GTA IV's lodm section works differently enough that
    /// carrying them across would produce dangling references. Every converted
    /// entity is written as a standalone placement.</summary>
    /// <param name="flipRotation">Conjugate every rotation on the way out. Off
    /// by default — see the convention note on <see cref="IvPlacementCommand"/>
    /// for why that is the right way round.</param>
    internal static YmapResult FromYmap(string ymapPath, bool flipRotation = false)
    {
        var notes = new List<string>();
        var ymap = new YmapFile();
        ymap.Load(File.ReadAllBytes(ymapPath));

        var wpl = new IvWpl();
        var entities = ymap.AllEntities;
        if (entities == null || entities.Length == 0)
        {
            notes.Add("that .ymap contains no entities (it may hold only occlusion or "
                    + "instanced-grass data, which GTA IV has no equivalent for)");
            return new YmapResult(wpl, notes, 0, 0);
        }

        int unnamed = 0, skipped = 0;
        foreach (var e in entities)
        {
            if (e == null) { skipped++; continue; }

            // GTA V's stored hash is already a Jenkins hash of the archetype
            // name, computed the same way GTA IV hashes .ide names — so when
            // the name is recoverable, rehash it (that also normalises case);
            // otherwise carry the hash straight across.
            //
            // CodeWalker falls back to the hash rendered as a decimal string
            // when it has no dictionary entry for a name, so a purely numeric
            // "name" means unresolved, not a model called 1286535678.
            uint hash = e.EntityHash;
            var nm = e.Name;
            if (!string.IsNullOrWhiteSpace(nm) && !IsHashPlaceholder(nm))
                hash = JenkHash.GenHash(nm.ToLowerInvariant());
            else
                unnamed++;

            // CodeWalker already conjugates on load — .ymap stores the inverse
            // rotation and Orientation is the true world rotation, which is
            // what a .wpl stores. So this copies rather than flipping; see the
            // convention note on IvPlacementCommand.
            var o = e.Orientation;
            var rot = flipRotation
                ? new Vector4(-o.X, -o.Y, -o.Z, o.W)
                : new Vector4(o.X, o.Y, o.Z, o.W);

            wpl.Objects.Add(NewInstance(
                hash, new Vector3(e.Position.X, e.Position.Y, e.Position.Z), rot));
        }

        notes.Add($"converted {wpl.Objects.Count:N0} placement(s)");
        if (unnamed > 0)
            notes.Add($"{unnamed:N0} entity name(s) could not be resolved from the hash — those placements "
                    + "keep their original hash, so the .ide entries you write for them must use the "
                    + "same names the GTA V archetypes had");
        return new YmapResult(wpl, notes, unnamed, skipped);
    }

    /// <summary>True when CodeWalker gave back a hash rendered as text instead
    /// of a real name — either "0x…" or a bare decimal.</summary>
    internal static bool IsHashPlaceholder(string name)
    {
        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var c in name) if (!char.IsAsciiDigit(c)) return false;
        return name.Length > 0;
    }
}
