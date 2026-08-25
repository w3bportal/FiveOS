// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Numerics;
using Mat4 = System.Numerics.Matrix4x4;
using Assimp;
using RageLib.Numerics;
using RageLib.Resources.Common;
using RageLib.Resources.GTA4;
using RageLib.Resources.GTA4.PC.Bounds;

namespace YdrWriter.GtaIv;

/// <summary>Builds a GTA IV collision resource (.wbn) from the same imported
/// scene the drawable is built from.
///
/// Shape: <c>datOwner&lt;phBound&gt;</c> → <c>phBoundComposite</c> → one
/// <c>phBoundGeometry</c> per collision mesh. Geometry rather than BVH on
/// purpose — Rockstar ships bare geometry bounds for props (int_door.wbd holds
/// 16 boxes and a geometry alongside its BVHs), and a BVH would add a spatial
/// index whose node encoding is the one region still ambiguous across sources.
/// A prop-sized poly soup does not need one.
///
/// The scene arrives already gizmo-transformed and Z-up from
/// <see cref="Converter"/>, and GTA IV is Z-up too, so no further transform.</summary>
internal static class IvBoundBuilder
{
    /// <summary>Every bound's collision margin. 0.04 m is what shipping IV
    /// props use; it is the skin the physics solver keeps between surfaces.</summary>
    private const float Margin = 0.04f;

    /// <summary>Vertices are stored as int16 relative to a quantum, so the
    /// stored box is padded before the quantum is derived from it. Without the
    /// pad a vertex exactly on the box face rounds to 32768, wraps to -32768,
    /// and that corner of the hull inverts.</summary>
    private const float QuantPad = 0.1f;

    /// <summary>Triangle count past which a flat geometry bound is a bad idea.
    /// Shipping GTA IV props sit far below this.</summary>
    private const int HeavyCollisionTriangles = 5000;

    internal sealed record Options(
        IReadOnlySet<string> ExcludeMeshes,
        IReadOnlyDictionary<string, string> PartMaterials,
        string DefaultMaterial);

    /// <summary>Build the resource, or null when the scene has no usable
    /// collision geometry.</summary>
    internal static Resource5<IvBoundOwner>? Build(Scene scene, Options opts)
    {
        var children = new List<PhBound>();

        for (int mi = 0; mi < scene.MeshCount; mi++)
        {
            var mesh = scene.Meshes[mi];
            if (mesh.FaceCount == 0 || mesh.VertexCount == 0) continue;
            if (!string.IsNullOrEmpty(mesh.Name) && opts.ExcludeMeshes.Contains(mesh.Name)) continue;

            var matName = opts.DefaultMaterial;
            if (!string.IsNullOrEmpty(mesh.Name)
                && opts.PartMaterials.TryGetValue(mesh.Name, out var tagged)
                && !string.IsNullOrWhiteSpace(tagged))
            {
                matName = tagged;
            }

            var geom = BuildGeometry(mesh, IvCollisionMaterials.Resolve(matName));
            if (geom != null) children.Add(geom);
        }

        return Assemble(children);
    }

    /// <summary>Wrap the finished hulls in the composite bound and the
    /// RSC5 container. Shared by both front ends so the composite layout,
    /// the identity child matrices and the version-32 container live in one
    /// place.</summary>
    private static Resource5<IvBoundOwner>? Assemble(List<PhBound> children)
    {
        if (children.Count == 0) return null;

        // A geometry bound is a flat poly soup — every triangle is tested. That
        // is the right shape for a prop, but it scales badly: GTA IV puts large
        // collision behind a BVH for exactly this reason. Rockstar's own props
        // sit in the low hundreds of triangles, so say something well before a
        // render mesh gets shipped as physics by accident.
        long triangles = children.OfType<PhBoundGeometry>().Sum(g => (long)(g.Primitives?.Count ?? 0));
        if (triangles > HeavyCollisionTriangles)
        {
            Console.WriteLine(
                $"[ydr-writer]   WARNING: collision has {triangles:N0} triangles. This is a flat hull with no " +
                "spatial index, so the physics cost scales with that count — supply a simplified collision " +
                "mesh, or exclude detail meshes with --exclude-mesh.");
        }

        var composite = new PhBoundComposite
        {
            Type = BoundType.BoundComposite,
            Unknown_06h = 1,
            Unknown_07h = 1,
            Margin = new Vector3(Margin),
            RefCount = 1,
            Bounds = new ResourcePointerArray32<PhBound> { data_items = children },
        };

        // Children sit in the parent's own space, so every matrix is identity.
        var identities = new Mat4[children.Count];
        var childBoxes = new Aabb[children.Count];
        for (int i = 0; i < children.Count; i++)
        {
            identities[i] = Mat4.Identity;
            childBoxes[i] = new Aabb(children[i].BoundingBoxMin, children[i].BoundingBoxMax);
        }
        composite.CurrentMatrices = new SimpleArray<Mat4>(identities);
        composite.LastMatrices = new SimpleArray<Mat4>((Mat4[])identities.Clone());
        composite.ChildBoundingBoxes = new SimpleArray<Aabb>(childBoxes);

        ApplyBounds(composite,
            children.Aggregate(new Vector3(float.MaxValue), (a, c) => Vector3.Min(a, ToV3(c.BoundingBoxMin))),
            children.Aggregate(new Vector3(float.MinValue), (a, c) => Vector3.Max(a, ToV3(c.BoundingBoxMax))));

        var resource = new Resource5<IvBoundOwner>
        {
            Version = 32,
            ResourceData = new IvBoundOwner { Bound = composite },
        };
        return resource;
    }

    /// <summary>How many triangles a finished bound actually carries.
    /// Degenerate faces are dropped during construction, so this is the number
    /// worth reporting rather than the source mesh's own count.</summary>
    internal static long CountTriangles(Resource5<IvBoundOwner>? resource)
    {
        var bound = resource?.ResourceData?.Bound;
        return bound switch
        {
            PhBoundComposite c => c.Bounds?.data_items?.Sum(CountOne) ?? 0,
            null => 0,
            _ => CountOne(bound),
        };

        static long CountOne(PhBound b)
            => b is PhBoundGeometry g ? g.Primitives?.Count ?? 0 : 0;
    }

    /// <summary>Build collision from a converted drawable's own triangles.
    ///
    /// GTA V map assets keep collision in a separate .ybn, and when one sits
    /// beside the .ydr it is translated bound-for-bound — always the better
    /// answer, because an artist authored it. This is the fallback for the
    /// common case where no .ybn shipped with the model: without it a
    /// converted prop is scenery a player walks straight through.
    ///
    /// Derived collision is the render mesh, so it is heavier and more
    /// detailed than hand-authored physics ever is. That is a real trade and
    /// the caller says so rather than hiding it.</summary>
    internal static Resource5<IvBoundOwner>? Build(IvDrawable drawable, Options opts)
    {
        var lod = drawable.Lods[0];
        if (lod == null || lod.Primitives.Count == 0) return null;

        byte material = IvCollisionMaterials.Resolve(opts.DefaultMaterial);
        var children = new List<PhBound>();

        // One hull per primitive, mirroring the one-hull-per-mesh the Assimp
        // path produces: a composite of per-part hulls keeps concave props
        // concave instead of collapsing them into one convex blob.
        foreach (var prim in lod.Primitives)
        {
            if (prim.Vertices.Count == 0 || prim.Indices.Count < 3) continue;

            var positions = new Vector3[prim.Vertices.Count];
            for (int i = 0; i < prim.Vertices.Count; i++) positions[i] = prim.Vertices[i].Position;

            var tris = new List<Tri>(prim.Indices.Count / 3);
            for (int i = 0; i + 2 < prim.Indices.Count; i += 3)
            {
                int a = prim.Indices[i], b = prim.Indices[i + 1], c = prim.Indices[i + 2];
                if (a == b || b == c || a == c) continue;          // degenerate
                if (a >= positions.Length || b >= positions.Length || c >= positions.Length) continue;
                tris.Add(new Tri(a, b, c));
            }

            var geom = BuildGeometry(positions, tris, material);
            if (geom != null) children.Add(geom);
        }

        return Assemble(children);
    }

    private static Vector3 ToV3(Vector4 v) => new(v.X, v.Y, v.Z);

    /// <summary>One collision triangle, as three indices into a position
    /// array. Both front ends reduce to this so the hull construction below is
    /// written once.</summary>
    private readonly record struct Tri(int A, int B, int C);

    /// <summary>Assimp mesh in, hull out.</summary>
    private static PhBoundGeometry? BuildGeometry(Mesh mesh, byte materialId)
    {
        var tris = mesh.Faces
            .Where(f => f.IndexCount == 3)
            .Select(f => new Tri(f.Indices[0], f.Indices[1], f.Indices[2]))
            .ToList();
        var positions = new Vector3[mesh.VertexCount];
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            var p = mesh.Vertices[i];
            positions[i] = new Vector3(p.X, p.Y, p.Z);
        }
        return BuildGeometry(positions, tris, materialId);
    }

    /// <summary>Triangles above which a single hull is replaced by its
    /// bounding box.
    ///
    /// Collision derived from a render mesh is far denser than anything an
    /// artist would author, and a phBoundGeometry is a FLAT poly soup with no
    /// spatial index - every triangle is tested. Rockstar puts anything of size
    /// behind a phBoundBVH for exactly this reason: 1,288 of the 1,974 prop
    /// bounds in the retail game are BVH.
    ///
    /// We do not write BVH yet, so the honest alternative is to stop handing
    /// the physics world meshes it cannot afford. A converted domino table came
    /// out at 11,623 collision triangles and stalled the client long enough to
    /// be dropped for going silent. Boxing the heavy pieces keeps the prop
    /// solid and costs twelve triangles.</summary>
    private const int MaxHullTriangles = 400;

    /// <summary>The eight corners and twelve triangles of a box, wound
    /// outwards. Used in place of a hull that is too dense to afford.</summary>
    private static (Vector3[] Positions, Tri[] Tris) BoxOf(Vector3 min, Vector3 max)
    {
        var p = new[]
        {
            new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
            new Vector3(max.X, max.Y, min.Z), new Vector3(min.X, max.Y, min.Z),
            new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, max.Z),
            new Vector3(max.X, max.Y, max.Z), new Vector3(min.X, max.Y, max.Z),
        };
        var t = new[]
        {
            new Tri(0, 2, 1), new Tri(0, 3, 2),   // bottom
            new Tri(4, 5, 6), new Tri(4, 6, 7),   // top
            new Tri(0, 1, 5), new Tri(0, 5, 4),   // -Y
            new Tri(1, 2, 6), new Tri(1, 6, 5),   // +X
            new Tri(2, 3, 7), new Tri(2, 7, 6),   // +Y
            new Tri(3, 0, 4), new Tri(3, 4, 7),   // -X
        };
        return (p, t);
    }

    /// <summary>The shared hull builder: positions plus triangles in, one
    /// phBoundGeometry out.</summary>
    private static PhBoundGeometry? BuildGeometry(
        IReadOnlyList<Vector3> positions, IReadOnlyList<Tri> tris, byte materialId)
    {
        if (tris.Count == 0 || positions.Count == 0) return null;

        // A primitive addresses its vertices with int16 indices, so a hull
        // cannot reach past 32,767 vertices. Silently wrapping would fold
        // distant corners of the mesh onto each other and produce collision
        // that looks nothing like the model.
        if (positions.Count > short.MaxValue)
        {
            Console.WriteLine(
                $"[ydr-writer]   WARNING: skipping a {positions.Count:N0}-vertex piece of collision - "
                + $"a GTA IV hull indexes vertices with int16 and cannot exceed {short.MaxValue:N0}.");
            return null;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        // Too dense to afford as a flat hull - stand it in with its box.
        if (tris.Count > MaxHullTriangles)
        {
            var (boxPos, boxTris) = BoxOf(min, max);
            positions = boxPos;
            tris = boxTris;
        }

        // Derive the quantum from a padded box (see QuantPad).
        var pad = new Vector3(QuantPad);
        var qMin = min - pad;
        var qMax = max + pad;
        var extent = qMax - qMin;
        var quantum = new Vector3(
            MathF.Max(extent.X, 1e-4f) / 65535f,
            MathF.Max(extent.Y, 1e-4f) / 65535f,
            MathF.Max(extent.Z, 1e-4f) / 65535f);
        var offset = (qMin + qMax) * 0.5f;

        var verts = new BoundVertex[positions.Count];
        var shrunk = new BoundVertex[positions.Count];
        var centre = (min + max) * 0.5f;
        for (int i = 0; i < positions.Count; i++)
        {
            verts[i] = Quantise(positions[i], offset, quantum);
            // Shrunk vertices are the hull pulled in slightly; the solver uses
            // them with the margin to build contact manifolds.
            shrunk[i] = Quantise(centre + (positions[i] - centre) * 0.96f, offset, quantum);
        }

        var prims = new PhBoundPrimitive[tris.Count];
        for (int t = 0; t < tris.Count; t++)
        {
            var f = tris[t];
            int a = f.A, b = f.B, c = f.C;
            var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            float len = n.Length();
            float area = len * 0.5f;
            n = len > 1e-12f ? n / len : new Vector3(0, 0, 1);

            var prim = new PhBoundPrimitive
            {
                Normal = n,
                VertexIndex1 = (short)a,
                VertexIndex2 = (short)b,
                VertexIndex3 = (short)c,
                // A zero fourth index marks the primitive as a triangle rather
                // than a quad.
                VertexIndex4 = 0,
                NeighborIndex1 = -1,
                NeighborIndex2 = -1,
                NeighborIndex3 = -1,
                NeighborIndex4 = -1,
            };
            // Order matters: Area writes the upper 24 bits, Material the low
            // byte, and each preserves the other's bits.
            prim.Area = area;
            prim.Material = 0;
            prims[t] = prim;
        }

        ResolveNeighbours(prims, tris);

        var geom = new PhBoundGeometry
        {
            Type = BoundType.BoundGeometry,
            Unknown_06h = 1,
            Unknown_07h = 1,
            Margin = new Vector3(Margin),
            RefCount = 1,
            Quantum = new Vector4(quantum, 0f),
            Offset = new Vector4(offset, 0f),
            Unknown_BCh = 0xFFFFFFFF,
            VerticesCount = (uint)verts.Length,
            PrimitivesCount = (uint)prims.Length,
            Vertices = new SimpleArray<BoundVertex>(verts),
            ShrunkVertices = new SimpleArray<BoundVertex>(shrunk),
            Primitives = new SimpleArray<PhBoundPrimitive>(prims),
            Materials = new SimpleArray<PhBoundMaterial>(new[]
            {
                new PhBoundMaterial { MaterialId = materialId },
            }),
        };
        ApplyBounds(geom, min, max);
        return geom;
    }

    private static BoundVertex Quantise(Vector3 world, Vector3 offset, Vector3 quantum)
    {
        var q = (world - offset) / quantum;
        return new BoundVertex
        {
            X = (short)Math.Clamp((int)MathF.Round(q.X), short.MinValue, short.MaxValue),
            Y = (short)Math.Clamp((int)MathF.Round(q.Y), short.MinValue, short.MaxValue),
            Z = (short)Math.Clamp((int)MathF.Round(q.Z), short.MinValue, short.MaxValue),
        };
    }

    /// <summary>Fill each primitive's neighbour slots from a shared-edge map.
    ///
    /// Rockstar's own bounds carry real sibling indices (the door bound's first
    /// primitive names 3, 1, 4, 11) and RAGE uses them to cull interior contact
    /// edges. Leaving them all -1 parses fine but makes a ped catch on the seams
    /// between triangles.</summary>
    private static void ResolveNeighbours(PhBoundPrimitive[] prims, IReadOnlyList<Tri> tris)
    {
        var edgeOwner = new Dictionary<(int, int), (int Prim, int Slot)>();

        for (int t = 0; t < tris.Count; t++)
        {
            var f = tris[t];
            Span<int> idx = stackalloc int[3] { f.A, f.B, f.C };
            for (int e = 0; e < 3; e++)
            {
                int v0 = idx[e], v1 = idx[(e + 1) % 3];
                var key = v0 < v1 ? (v0, v1) : (v1, v0);
                if (edgeOwner.TryGetValue(key, out var other))
                {
                    SetSlot(ref prims[t], e, (short)other.Prim);
                    SetSlot(ref prims[other.Prim], other.Slot, (short)t);
                    edgeOwner.Remove(key);
                }
                else
                {
                    edgeOwner[key] = (t, e);
                }
            }
        }

        static void SetSlot(ref PhBoundPrimitive p, int slot, short value)
        {
            switch (slot)
            {
                case 0: p.NeighborIndex1 = value; break;
                case 1: p.NeighborIndex2 = value; break;
                case 2: p.NeighborIndex3 = value; break;
            }
        }
    }

    private static void ApplyBounds(PhBound bound, Vector3 min, Vector3 max)
    {
        var centre = (min + max) * 0.5f;
        float radius = (max - min).Length() * 0.5f;
        bound.BoundingBoxMin = new Vector4(min, 0f);
        bound.BoundingBoxMax = new Vector4(max, 0f);
        bound.CentroidOffset = new Vector4(centre, 0f);
        bound.CGOffset = new Vector4(centre, 0f);
        bound.Radius = radius;
        bound.WorldRadius = radius + centre.Length();
    }
}
