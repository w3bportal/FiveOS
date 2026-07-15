// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using Assimp;
using CodeWalker.GameFiles;
using SharpDX;
using SDXVector3 = SharpDX.Vector3;

namespace YdrWriter;

/// <summary>
/// Builds a binary .ybn (BoundComposite > BoundBVH) from the same Assimp
/// scene that drives the .ydr export. The scene is already Z-up here (Stage
/// 1b in <see cref="Converter"/> mutates positions in place), so we pull
/// vertex positions and triangle indices straight through with no extra
/// transform.
///
/// This mirrors the Sollumz "convert mesh to bound geometry BVH" workflow:
/// duplicate the visual model's geometry, drop all per-face materials,
/// assign a single collision material (CONCRETE/METAL/WOOD selected in the
/// UI), and wrap it in a BoundComposite. The BVH is the polygon-accurate
/// path; the simpler BoundBox primitive lives in <see cref="BuildBoxComposite_LEGACY"/>
/// as a fallback but is no longer the default.
/// </summary>
public static class YbnBuilder
{
    /// <summary>Build the BoundComposite for the scene. Used by both the
    /// external-.ybn path (wrapped in a YbnFile and serialised) and the
    /// embed-into-YDR path (assigned to <c>Drawable.Bound</c>).
    ///
    /// <paramref name="excludeMeshNames"/>: same set used by
    /// <see cref="DirectFbxBuilder.Build"/> — keeps the YBN in sync with
    /// the visible-meshes set. Without this, hidden parts would still
    /// have collision in-game.</summary>
    public static BoundComposite BuildComposite(Scene scene, string materialName,
        IReadOnlySet<string>? excludeMeshNames = null,
        IReadOnlyDictionary<string, string>? partMaterials = null,
        bool breakableGlass = false)
    {
        byte defaultMatIndex = ResolveMaterialIndex(materialName);

        // meshName -> source material name, so glass auto-detection here matches
        // the visual shader pass (TextureBaker.AutoDetectGlass).
        var meshMat = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var m in scene.Meshes)
            if (!string.IsNullOrEmpty(m.Name) && !meshMat.ContainsKey(m.Name))
                meshMat[m.Name] = (m.MaterialIndex >= 0 && m.MaterialIndex < scene.MaterialCount)
                    ? scene.Materials[m.MaterialIndex].Name : null;

        // Resolve each mesh to its collision material index. A glass part —
        // explicitly tagged (--part-mat GLASS) OR auto-detected by name, the
        // same way the visual pass classifies it — gets GLASS_SHOOT_THROUGH
        // (bullets pass + shatter VFX) ONLY when breakable glass is enabled;
        // otherwise it stays solid on the global material. Non-glass meshes
        // always keep the global pick. The result is a small palette of unique
        // indices we then dedupe into the BVH's Materials array.
        byte ResolveMeshMaterial(string meshName)
        {
            bool isGlass;
            if (partMaterials != null && !string.IsNullOrEmpty(meshName) &&
                partMaterials.TryGetValue(meshName, out var preset))
                isGlass = string.Equals(preset, "GLASS", StringComparison.OrdinalIgnoreCase);
            else
            {
                meshMat.TryGetValue(meshName ?? "", out var mn);
                isGlass = TextureBaker.AutoDetectGlass(meshName, mn, null) != null;
            }
            if (isGlass && breakableGlass)
                return ResolveMaterialIndex("GLASS_SHOOT_THROUGH");
            return defaultMatIndex;
        }

        var (verts, tris, triMatIndex) = CollectGeometry(scene, excludeMeshNames, ResolveMeshMaterial);
        if (verts.Count == 0 || tris.Count == 0)
            throw new InvalidOperationException("scene contained no triangles");

        // GeometryBVH not BoundBox. Verified by dumping bob74_ipl's 13
        // collision YBNs to XML: every working static-map YBN in the
        // wild is `Composite > GeometryBVH`. RAGE doesn't honor
        // BoundBox-at-composite-root for static map collision —
        // we tried it, the player walks through. The XML roundtrip
        // below (introduced for the BoundBox path's missing-defaults
        // bug) is what makes BVH safe to save now: ReadXml runs
        // CalculateQuantum / CalculateVertsShrunk / BuildBVH after
        // taking the centered-verts + CenterGeom from XML, which is
        // the path Sollumz' outputs traverse to produce working binary.
        // Earlier BVH attempts saved without this roundtrip, hitting
        // CW.Core's "CenterGeom resets at save time" symptom — the
        // old memory note documented that dead end.
        var built = BuildBvhComposite(verts, tris, triMatIndex);

        // Roundtrip through CW.Core's XML serializer/deserializer so every
        // field gets populated by the same ReadXml path Sollumz' static-prop
        // exports use. Direct C# construction (`new BoundBox { ... }`) leaves
        // anything we don't explicitly set at its zero default — and CW.Core
        // emits those zeros verbatim. RAGE then either skips the bound
        // (FileVFT=0, Volume=0) or registers it but never actually queries
        // it (missing UnkType or Unknown_3Ch flags). Sollumz never hits this
        // because Sollumz writes XML and lets CW.Core rebuild via ReadXml,
        // which sets FileVFT, Unknown_3Ch=1, and other fields we don't
        // track. Routing our build through the same XML path gives us those
        // defaults without manually mirroring every CW.Core internal.
        return XmlRoundtrip(built);
    }

    /// <summary>Take a constructed BoundComposite, run it through CW.Core's
    /// XML serializer and deserializer, and return the rebuilt composite.
    /// See <see cref="BuildComposite"/> for why this exists. Throws if the
    /// XML produced by WriteXml doesn't round-trip back to a non-null
    /// composite — that would indicate a CW.Core asymmetry that we'd need
    /// to learn about rather than silently emit broken bytes.</summary>
    private static BoundComposite XmlRoundtrip(BoundComposite source)
    {
        var ybn = new YbnFile { Bounds = source };
        var xml = YbnXml.GetXml(ybn);
        var rebuilt = XmlYbn.GetYbn(xml);
        if (rebuilt?.Bounds is not BoundComposite c)
            throw new InvalidOperationException(
                "Bound XML roundtrip lost the composite — CW.Core read/write mismatch");
        return c;
    }

    /// <summary>Build a polygon-accurate BoundComposite&gt;BoundBVH from the
    /// collected geometry. One material, one polygon group, every triangle
    /// pointing at material index 0 — the Sollumz "geometry BVH" idiom.
    ///
    /// The vertex storage in CW.Core's <c>BoundGeometry</c> (which BoundBVH
    /// extends) encodes each vert as <c>int16 = vert / Quantum</c> per axis,
    /// with <c>Quantum = (BoxMax - BoxMin) * 0.5 / 32767</c> recomputed at
    /// save time inside <c>GetReferences()</c>. Any manual Quantum override
    /// gets clobbered. That formula assumes the geometry is symmetric around
    /// the origin (BoxMin = -BoxMax); for anything off-center — a humanoid
    /// with feet at Z=0 and head at Z=1.74, a chair with base at Z=0 — the
    /// encoded int16 overflows, wraps, and the upper half collides as a
    /// flat slab at the wraparound point.
    ///
    /// The fix is to give CW what its formula expects: pre-translate the
    /// verts so the geometric centroid sits at the origin in the BVH's
    /// stored vertex space, then record the centroid in <c>CenterGeom</c>
    /// so RAGE can shift the decoded verts back to entity-local at
    /// collision-query time. This is what Sollumz's <c>ybnexport.center_verts_to_geometry</c>
    /// does verbatim: subtract centroid from every vert, store centroid as
    /// <c>geometry_center</c> (CW's <c>CenterGeom</c>), leave the BVH child's
    /// composite transform at identity. The drawable mesh isn't touched —
    /// it ships at its authored entity-local position; only the BVH child's
    /// internal storage is centered.</summary>
    private static BoundComposite BuildBvhComposite(List<SDXVector3> verts,
        List<(int A, int B, int C)> tris, byte[] triMatIndex)
    {
        // Dedupe the per-triangle material index list down to a small
        // Materials array on the BVH. PolygonMaterialIndices then point
        // into this deduped array per polygon. Single-material scenes
        // collapse to one entry, matching the prior single-byte path.
        var uniqueMats = new List<byte>();
        var matRemap = new Dictionary<byte, byte>();
        for (int i = 0; i < triMatIndex.Length; i++)
        {
            var raw = triMatIndex[i];
            if (!matRemap.ContainsKey(raw))
            {
                matRemap[raw] = (byte)uniqueMats.Count;
                uniqueMats.Add(raw);
            }
        }
        if (uniqueMats.Count == 0) uniqueMats.Add((byte)1); // safety: never empty
        // Compute the entity-local AABB and centroid BEFORE centering.
        // BoxMin/BoxMax stay at these values on the BVH child (CW uses them
        // to derive Quantum, and they describe the actual entity-local
        // collision extent). The composite root inherits the same.
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        foreach (var v in verts)
        {
            if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
            if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
            if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
        }
        var origMin = new SDXVector3(minX, minY, minZ);
        var origMax = new SDXVector3(maxX, maxY, maxZ);
        var centroid = (origMin + origMax) * 0.5f;
        var origRadius = (origMax - origMin).Length() * 0.5f;

        // Center the verts so every centered coordinate is in [-half_extent,
        // +half_extent] per axis. Combined with stock Quantum = half_extent
        // / 32767, every encoded int16 lands in [-32767, +32767] — no
        // overflow, no slab. Centroid lives in CenterGeom below.
        var centered = new SDXVector3[verts.Count];
        for (int i = 0; i < verts.Count; i++) centered[i] = verts[i] - centroid;

        var bvh = new BoundBVH { Type = BoundsType.GeometryBVH };
        // Per-class vtable magic from BoundBVH.ReadXml in CW.Core.
        bvh.FileVFT = 1080228536;
        bvh.Vertices = centered;
        bvh.VertexColours = Array.Empty<BoundMaterialColour>();

        // Bound-base fields that Sollumz always sets on BVH exports and we'd
        // otherwise leave at the C# auto-property defaults (mostly zero).
        // Verified against Sollumz' create_bound_xml/set_bound_mass_properties
        // and CW.Core's Bounds.Read at /tmp/bounds.cs:271-296 for the byte
        // layout. Without these, the engine reads zero for fields it
        // expects non-zero for, and behaviour gets undefined:
        //  • Margin: collision tolerance (also stored into
        //    ChildrenBoundingBoxes[i].Max.W by UpdateChildrenBounds, so it
        //    propagates into composite metadata, not just child-local).
        //    Sollumz uses 0.04 for all BVHs.
        //  • Volume: divisor in some inertia/mass derivations. Sollumz uses
        //    1.0 for BVHs (static collision doesn't actually have mass, but
        //    the engine still reads the field).
        //  • Unknown_60h: this is `inertia` in the Sollumz/Codewalker XML
        //    schema (Vector3 written between Unknown_5Eh and Volume — exact
        //    byte slot Sollumz writes inertia into). Sollumz uses
        //    (1, 1, 1) for BVHs.
        //  • MaterialIndex (base-class): unused for BVHs because the Materials
        //    array carries per-poly material refs, but Sollumz still sets it
        //    to 0 explicitly. Match that.
        // Margin 0.005 matches what bob74_ipl's static-map BVHs ship with
        // (Sollumz' default is 0.025; the IPL set uses 0.005). 0.04 was
        // a guess that doesn't appear in any verified working YBN.
        bvh.Margin = 0.005f;
        bvh.Volume = 1.0f;
        bvh.Unknown_60h = new SDXVector3(1.0f, 1.0f, 1.0f);
        bvh.MaterialIndex = 0;

        // Multi-material BVH: one entry per unique materials.dat index
        // referenced by the scene's triangles. PolygonMaterialIndices then
        // points each polygon at the slot in this array. For a single-
        // material scene this collapses back to one entry — byte-identical
        // to the prior path.
        bvh.Materials = uniqueMats.Select(idx => new BoundMaterial_s
        {
            Type = new BoundsMaterialType { Index = idx },
            ProceduralId = 0,
            RoomId = 0,
            PedDensity = 0,
            Flags = EBoundMaterialFlags.NONE,
            MaterialColorIndex = 0,
            Unk4 = 0,
        }).ToArray();
        bvh.MaterialColours = Array.Empty<BoundMaterialColour>();

        bvh.PolygonMaterialIndices = new byte[tris.Count];
        var polys = new BoundPolygon[tris.Count];
        for (int i = 0; i < tris.Count; i++)
        {
            var tri = (BoundPolygonTriangle)bvh.AddPolygon(BoundPolygonType.Triangle);
            tri.vertIndex1 = tris[i].A;
            tri.vertIndex2 = tris[i].B;
            tri.vertIndex3 = tris[i].C;
            polys[i] = tri;
            bvh.PolygonMaterialIndices[i] = matRemap[triMatIndex[i]];
        }
        bvh.Polygons = polys;

        // Set BoxMin/BoxMax directly to the entity-local AABB we captured
        // before centering. Do NOT call CalculateMinMax — it would recompute
        // from the (now centered) Vertices and produce symmetric bounds,
        // which would describe collision sitting at the entity origin
        // instead of at the prop's actual position. Sollumz keeps these in
        // entity-local space (the input to its center_verts_to_geometry
        // is geom_xml.box_min/box_max, already entity-local) and never
        // re-derives them after centering.
        bvh.BoxMin = origMin;
        bvh.BoxMax = origMax;
        bvh.BoxCenter = centroid;
        bvh.SphereCenter = centroid;
        bvh.SphereRadius = origRadius;

        // CalculateQuantum reads the entity-local BoxMin/BoxMax we just set.
        // Result is (BoxMax - BoxMin) * 0.5 / 32767 = half-extent / 32767.
        // For a vert at the centered extreme (±half-extent), encoded =
        // half-extent / (half-extent / 32767) = ±32767 — exact int16 fit.
        // GetReferences re-runs CalculateQuantum at save time but reads the
        // same entity-local BoxMin/BoxMax, so the value reproduces.
        bvh.CalculateQuantum();

        // CenterGeom carries the centroid that was subtracted from the verts.
        // RAGE (and CW's own GetVertex helper at Bounds.cs:1407 — `vert +
        // CenterGeom`) reads it back at decode time to recover entity-local
        // positions: entity_local = stored * Quantum + CenterGeom. This
        // matches Sollumz's ybnexport.center_verts_to_geometry, which sets
        // geometry_center to exactly the same value. Leave child.Transform
        // at Identity (its default) — the centroid offset lives in
        // CenterGeom, not in the composite child transform.
        bvh.CenterGeom = centroid;

        // Universal constants verified across every static-map YBN
        // in bob74_ipl (5 different city blocks sampled, identical
        // values per BVH). CW.Core writes Unknown_9Ch/Unknown_ACh
        // verbatim — if they're 0 (the C# default) RAGE's collision
        // filter silently rejects the bound. These appear to be the
        // W-components of Quantum and CenterGeom packed alongside
        // the Vector3 data in the binary layout, and RAGE reads
        // them as part of validating the geometry.
        bvh.Unknown_9Ch = 7.6296274e-08f;
        bvh.Unknown_ACh = 0.0025f;

        // CalculateVertsShrunk is a no-op when Type != BoundsType.Geometry
        // (it early-returns for GeometryBVH), so skipping it costs nothing
        // and avoids confusion. The remaining steps build the BVH spatial
        // tree and per-triangle metadata.
        bvh.CalculateOctants();
        bvh.UpdateEdgeIndices();
        bvh.UpdateTriangleAreas();
        bvh.BuildBVH(false);

        // Type/Include flags decide whether the engine actually queues
        // collision pairs against this bound. CW's UpdateChildrenFlags()
        // only resizes the per-child flag arrays — it doesn't populate
        // them with non-zero values, and a BoundComposite with all-NONE
        // flags is a phantom: physics queries skip it entirely and the
        // player walks straight through.
        //
        // The flags live on each child bound's own CompositeFlags1/2 (the
        // BoundComposite.ChildrenFlags1/2 arrays are a synthesized view
        // over those). Type bits declare what kind of map collision this
        // is; Include bits declare which dynamic entity types are tested
        // against it. The masks below match what CodeWalker / Sollumz set
        // for a static map prop: MAP_WEAPON (bullet hits) | MAP_DYNAMIC
        // (physics props) | MAP_ANIMAL (animal AI) | MAP_COVER (cover
        // system) | MAP_VEHICLE (vehicle wheels).
        const EBoundCompositeFlags STATIC_PROP_TYPE =
              EBoundCompositeFlags.MAP_WEAPON | EBoundCompositeFlags.MAP_DYNAMIC
            | EBoundCompositeFlags.MAP_ANIMAL | EBoundCompositeFlags.MAP_COVER
            | EBoundCompositeFlags.MAP_VEHICLE;

        // Flags2 is the INCLUDE mask (which dynamic-entity types test
        // against this static collision). This is the exact bit set
        // bob74_ipl's verified-working YBNs use — confirmed by dumping
        // 13 of them to XML and seeing the same Flags2 across the lot.
        // It deliberately does NOT include the MAP_* bits (those go in
        // Flags1 only) and trims a few of the speculative flags our
        // earlier guesses added (VEHICLE_BOX, OBJECT_ENV_CLOTH, PICKUP,
        // FOLIAGE, UNSMASHED, MAP_STAIRS, MAP_DEEP_SURFACE) — none of
        // them appear in real-world working bounds for static map
        // collision and they may be triggering the wrong code paths
        // in RAGE's collision filter.
        const EBoundCompositeFlags STATIC_PROP_INCLUDE =
              EBoundCompositeFlags.VEHICLE_NOT_BVH | EBoundCompositeFlags.VEHICLE_BVH
            | EBoundCompositeFlags.PED | EBoundCompositeFlags.RAGDOLL
            | EBoundCompositeFlags.ANIMAL | EBoundCompositeFlags.ANIMAL_RAGDOLL
            | EBoundCompositeFlags.OBJECT | EBoundCompositeFlags.PLANT
            | EBoundCompositeFlags.PROJECTILE | EBoundCompositeFlags.EXPLOSION
            | EBoundCompositeFlags.FORKLIFT_FORKS
            | EBoundCompositeFlags.TEST_WEAPON | EBoundCompositeFlags.TEST_CAMERA
            | EBoundCompositeFlags.TEST_AI | EBoundCompositeFlags.TEST_SCRIPT
            | EBoundCompositeFlags.TEST_VEHICLE_WHEEL
            | EBoundCompositeFlags.GLASS;

        var collisionFlags = new BoundCompositeChildrenFlags
        {
            Flags1 = STATIC_PROP_TYPE,
            Flags2 = STATIC_PROP_INCLUDE,
        };

        // Composite wrapper — game collision is always BoundComposite at
        // the archetype level even when there's only one child geometry.
        var comp = new BoundComposite { Type = BoundsType.Composite };
        comp.FileVFT = 1080212136;
        comp.AddChild(bvh);
        comp.UpdateChildrenBounds();
        comp.UpdateChildrenTransformations();
        comp.UpdateChildrenFlags();
        comp.BuildBVH();

        // Set the per-child collision flags AFTER UpdateChildrenFlags().
        // That helper resets/initialises the flags from the children's
        // current values (which were zero at AddChild time) and would
        // clobber any earlier assignment. Setting them now is the only
        // assignment that survives serialisation.
        foreach (var child in comp.Children?.data_items ?? Array.Empty<Bounds>())
        {
            if (child == null) continue;
            child.CompositeFlags1 = collisionFlags;
            child.CompositeFlags2 = collisionFlags;
        }

        // Composite-root bounds are in entity-local space (NOT the BVH
        // child's centered local space). They have to describe where the
        // collision actually sits relative to the entity origin so RAGE's
        // broad-phase early-out tests the right region. Use the original
        // (pre-centering) AABB and centroid we cached at the top.
        comp.BoxMin = origMin;
        comp.BoxMax = origMax;
        comp.BoxCenter = centroid;
        comp.SphereCenter = centroid;
        comp.SphereRadius = origRadius;

        // Composite-root field defaults Sollumz' create_composite_xml sets
        // explicitly. For a single-child composite of a unit-volume BVH,
        // these collapse to the values below:
        //   • Margin: 0 on the composite (it has no polygons of its own).
        //   • Volume: sum of children's volumes = 1.0.
        //   • Unknown_60h (= inertia in the Sollumz schema): for a single
        //     child whose CG coincides with the composite CG, the parallel-
        //     axis term drops out and composite inertia == child inertia
        //     == (1, 1, 1).
        comp.Margin = 0.0f;
        comp.Volume = 1.0f;
        comp.Unknown_60h = new SDXVector3(1.0f, 1.0f, 1.0f);
        return comp;
    }

    /// <summary>AABB primitive path — wraps the geometry in a single
    /// BoundBox. This is the default for embedded collision because the BVH
    /// variant produces YDRs with unresolved CW.Core fixup pointers that
    /// trip RAGE's resource loader. BoundBox is a coarse hitbox but it
    /// saves and loads cleanly through CW.Core's resource serializer.</summary>
    private static BoundComposite BuildBoxComposite(List<SDXVector3> verts, byte matIndex)
    {
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        foreach (var v in verts)
        {
            if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
            if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
            if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
        }
        var bbMin = new SDXVector3(minX, minY, minZ);
        var bbMax = new SDXVector3(maxX, maxY, maxZ);
        var center = (bbMin + bbMax) * 0.5f;
        var radius = (bbMax - bbMin).Length() * 0.5f;

        var box = new BoundBox { Type = BoundsType.Box };
        // FileVFT is the per-class vtable pointer RAGE reads at load time
        // to dispatch the correct bound class. CW.Core writes it verbatim
        // from this field; when CW loads from XML it sets the magic per
        // class, but when we construct via `new BoundBox { ... }` it
        // defaults to 0. A zero VFT means RAGE can't resolve the class
        // and silently skips collision queries against the bound —
        // model loads, no Invalid fixup, and the player walks straight
        // through it. The magic comes from BoundBox.ReadXml in
        // CW.Core/GameFiles/Resources/Bounds.cs: `FileVFT = 1080221016`.
        box.FileVFT = 1080221016;
        box.BoxMin = bbMin;
        box.BoxMax = bbMax;
        box.BoxCenter = center;
        box.SphereCenter = center;
        box.SphereRadius = radius;
        box.MaterialIndex = matIndex;
        // Margin/Volume/Unknown_60h (inertia) match what Sollumz writes on
        // every embedded bound. RAGE uses Volume as a divisor in mass-property
        // and broad-phase calculations; if it stays at the C# default 0.0f the
        // bound reads as zero-volume and physics queries skip it — the
        // collision child is technically present in the YDR but the engine
        // never tests anything against it (player walks straight through).
        // Inertia (Unknown_60h) similarly needs a non-zero default for
        // dynamic-vs-static interaction tests to run. The (1,1,1) numbers
        // come from Sollumz's create_bound_xml/set_bound_mass_properties for
        // static map props — they're not physically derived because static
        // collision doesn't have mass, but RAGE still reads the slot.
        box.Margin = 0.04f;
        box.Volume = 1.0f;
        box.Unknown_60h = new SDXVector3(1.0f, 1.0f, 1.0f);

        var flags = new BoundCompositeChildrenFlags
        {
            Flags1 = EBoundCompositeFlags.MAP_WEAPON | EBoundCompositeFlags.MAP_DYNAMIC
                   | EBoundCompositeFlags.MAP_ANIMAL | EBoundCompositeFlags.MAP_COVER
                   | EBoundCompositeFlags.MAP_VEHICLE,
            Flags2 = (EBoundCompositeFlags)0xE7FFFFFE,
        };

        var c = new BoundComposite { Type = BoundsType.Composite };
        // Composite gets its own vtable magic (different from BoundBox's).
        // From BoundComposite.ReadXml in CW.Core: `FileVFT = 1080212136`.
        c.FileVFT = 1080212136;
        c.AddChild(box);
        c.UpdateChildrenBounds();
        c.UpdateChildrenTransformations();
        c.UpdateChildrenFlags();
        c.BuildBVH();
        foreach (var child in c.Children?.data_items ?? Array.Empty<Bounds>())
            if (child != null) { child.CompositeFlags1 = flags; child.CompositeFlags2 = flags; }
        c.BoxMin = bbMin; c.BoxMax = bbMax; c.BoxCenter = center;
        c.SphereCenter = center; c.SphereRadius = radius;
        // Composite-root physics fields — same rationale as the box-child
        // above but at the composite level. RAGE walks the composite first
        // for the broad-phase, child second for the narrow-phase; if the
        // root reads zero volume the broad-phase culls the entire bound
        // before the engine ever looks at the child.
        c.Margin = 0.0f;
        c.Volume = 1.0f;
        c.Unknown_60h = new SDXVector3(1.0f, 1.0f, 1.0f);
        return c;
    }

    /// <summary>Serialise an external .ybn (BoundComposite wrapped in a
    /// YbnFile). For the embedded-collision path call
    /// <see cref="BuildComposite"/> directly and assign to
    /// <c>Drawable.Bound</c> instead.
    ///
    /// We don't call <c>ybn.Save()</c> on the directly-constructed
    /// composite — instead we round-trip through CW.Core's XML
    /// serializer/deserializer first. Direct C# construction
    /// (<c>new BoundBox { ... }</c>) leaves any field we don't
    /// explicitly assign at its default zero, and CW.Core's writer
    /// emits those zeros verbatim. RAGE then can't resolve the bound
    /// (FileVFT=0) or skips it as zero-volume — model loads, no
    /// errors, player walks through. The exact failure mode varies
    /// by which field we missed.
    ///
    /// Sollumz' static-prop pipeline never hits this because Sollumz
    /// emits XML and lets CW.Core's <c>XmlYbn.GetYbn</c> rebuild the
    /// in-memory tree via each class's <c>ReadXml</c> — and ReadXml
    /// sets <c>FileVFT</c>, <c>Unknown_3Ch</c>, and other fields that
    /// our direct construction doesn't know about. Round-tripping our
    /// build through XML lets CW.Core fill in everything Sollumz
    /// relies on without us tracking every field by hand.</summary>
    public static byte[] Build(Scene scene, string materialName,
        IReadOnlySet<string>? excludeMeshNames = null,
        IReadOnlyDictionary<string, string>? partMaterials = null,
        bool breakableGlass = false)
    {
        // BuildComposite already XML-roundtrips the composite, so the
        // resulting YbnFile.Save() goes out with all the Sollumz-ish
        // defaults intact.
        var comp = BuildComposite(scene, materialName, excludeMeshNames, partMaterials, breakableGlass);
        var ybn = new YbnFile { Bounds = comp };
        return ybn.Save();
    }

    private static (List<SDXVector3> verts, List<(int A, int B, int C)> tris, byte[] triMatIndex) CollectGeometry(
        Scene scene, IReadOnlySet<string>? excludeMeshNames, Func<string, byte> resolveMeshMaterial)
    {
        // Each Assimp Mesh stores vertices in NODE-LOCAL space. The world
        // position of a vertex is the parent node chain's transform applied
        // to that local vert. If we read mesh.Vertices directly without
        // walking nodes, every mesh's verts collapse to a stack at origin —
        // collision ends up as a single flat slab while the drawable (which
        // goes through FBX export, preserving the hierarchy) renders the
        // model correctly. Walk the node tree once and bake each referenced
        // mesh under the node's accumulated world transform.
        var verts = new List<SDXVector3>();
        var tris = new List<(int, int, int)>();
        var triMats = new List<byte>();
        Walk(scene.RootNode, Assimp.Matrix4x4.Identity);

        void Walk(Assimp.Node node, Assimp.Matrix4x4 parentTransform)
        {
            if (node == null) return;
            // Assimp matrices are column-major, applied left-to-right via
            // `vec' = M * vec`. So the accumulated world transform for a node
            // is parent_chain * node.Transform — parent first (closer to root
            // in the math), node-local last. Reversing the order makes the
            // collision mesh apply only the leaf node's transform and ignore
            // the parent chain, which is why a 1.65 m tall figure collapsed
            // into the lower ~0.4 m slab.
            var world = parentTransform * node.Transform;
            foreach (var meshIndex in node.MeshIndices)
            {
                var mesh = scene.Meshes[meshIndex];
                if (excludeMeshNames != null && !string.IsNullOrEmpty(mesh.Name) &&
                    excludeMeshNames.Contains(mesh.Name))
                    continue;
                byte meshMatIndex = resolveMeshMaterial(mesh.Name ?? "");
                int baseIdx = verts.Count;
                for (int i = 0; i < mesh.VertexCount; i++)
                {
                    var v = mesh.Vertices[i];
                    var w = world * new Assimp.Vector3D(v.X, v.Y, v.Z);
                    verts.Add(new SDXVector3(w.X, w.Y, w.Z));
                }
                foreach (var face in mesh.Faces)
                {
                    if (face.IndexCount != 3) continue;
                    tris.Add((baseIdx + face.Indices[0],
                              baseIdx + face.Indices[1],
                              baseIdx + face.Indices[2]));
                    triMats.Add(meshMatIndex);
                }
            }
            foreach (var child in node.Children) Walk(child, world);
        }
        return (verts, tris, triMats.ToArray());
    }

    /// <summary>Map the user's material dropdown choice to a vanilla GTA V
    /// materials.dat index. Numbers come from stock <c>materials.dat</c>
    /// (cross-checked against Sollumz' <c>collision_materials.py</c> — the
    /// array index in that file equals the RAGE material ID) and are stable
    /// across game versions. Unknown names fall back to CONCRETE so the
    /// existing UI dropdown options that previously silently fell back
    /// still hit a sane default.</summary>
    // Indices verified against Sollumz' ybn/collision_materials.py — array
    // index in that file equals the RAGE materials.dat ID. Line 27 in the
    // file is DEFAULT (idx 0), so idx = line - 27.
    private static byte ResolveMaterialIndex(string name) => name?.ToUpperInvariant() switch
    {
        "CONCRETE"           => 1,   // line 28
        "METAL"              => 56,  // metal_solid_medium (line 83)
        "WOOD"               => 70,  // wood_solid_medium (line 97)
        "PLASTIC"            => 86,  // plastic (line 113)
        "RUBBER"             => 93,  // rubber (line 120)
        "ROCK"               => 9,   // rock (line 36)
        "SAND"               => 18,  // sand_loose (line 45)
        "DIRT"               => 35,  // dirt_track (line 62)
        "GRASS"              => 47,  // grass (line 74)
        "FOLIAGE"            => 50,  // bushes (line 77) — closest "foliage" surface
        // Glass family. Plain "GLASS" maps to shoot-through so the existing
        // dropdown's GLASS entry now produces real breakable behavior —
        // bullets pass, glass-shatter VFX + sound trigger on impact. The
        // Glass material preset in the layers panel also routes here.
        "GLASS"              => 112, // glass_shoot_through (line 139)
        "GLASS_SHOOT_THROUGH" => 112,
        "GLASS_BULLETPROOF"  => 113, // line 140
        "GLASS_OPAQUE"       => 114, // line 141
        "EMISSIVE_GLASS"     => 131, // line 158
        _                    => 1,
    };
}
