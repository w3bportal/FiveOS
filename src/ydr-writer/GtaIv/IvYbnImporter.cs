// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Numerics;
using CW = CodeWalker.GameFiles;
using RageLib.Numerics;
using RageLib.Resources.Common;
using RageLib.Resources.GTA4;
using RageLib.Resources.GTA4.PC.Bounds;
using Mat4 = System.Numerics.Matrix4x4;

namespace YdrWriter.GtaIv;

/// <summary>Translates GTA V collision (.ybn) into GTA IV collision (.wbn).
///
/// Both are phBound hierarchies from the same RAGE lineage and this project
/// has each half already — CodeWalker reads the GTA V side (RSC7, 64-bit) and
/// the vendored rage-toolkit writes the GTA IV side (RSC5 v32, 32-bit) — so
/// this is a re-encode rather than a redesign.
///
/// ⚠ The bound TYPE ENUMS COLLIDE between the two games and a byte copy would
/// silently mislabel everything:
///     GTA V : Sphere 0, Capsule 1, Box 3, Geometry 4, GeometryBVH 8,
///             Composite 10, Disc 12, Cylinder 13, Cloth 15
///     GTA IV: Sphere 0, Capsule 1, Box 3, Geometry 4, BVH 10, Composite 12
/// V's Composite(10) is IV's BVH(10), and V's Disc(12) is IV's Composite(12).
/// Everything here therefore switches on the CodeWalker TYPE, never on the
/// numeric value.
///
/// GTA V bounds also use primitive kinds IV has no record for — boxes,
/// capsules, spheres and cylinders make up roughly 9% of real map collision —
/// so those are tessellated into triangles.</summary>
internal static class IvYbnImporter
{
    private const float Margin = 0.04f;

    /// <summary>Vertices are int16 about a quantum; the stored box is padded
    /// before the quantum is derived from it, or a vertex exactly on a face
    /// rounds past the short and that corner of the hull inverts.</summary>
    private const float QuantPad = 0.1f;

    internal static Resource5<IvBoundOwner>? Convert(string ybnPath, string defaultMaterial, out List<string> notes)
    {
        notes = new List<string>();
        var ybn = new CW.YbnFile();
        ybn.Load(File.ReadAllBytes(ybnPath));
        var src = ybn.Bounds;
        if (src == null) { notes.Add("the .ybn holds no bounds"); return null; }

        var fallback = IvCollisionMaterials.Resolve(defaultMaterial);


        var children = new List<PhBound>();
        Flatten(src, Mat4.Identity, children, notes, fallback);
        if (children.Count == 0) { notes.Add("no convertible collision geometry"); return null; }

        var composite = new PhBoundComposite
        {
            Type = BoundType.BoundComposite,
            Unknown_06h = 1,
            Unknown_07h = 1,
            Margin = new Vector3(Margin),
            RefCount = 1,
            Bounds = new ResourcePointerArray32<PhBound> { data_items = children },
        };

        var identities = new Mat4[children.Count];
        var boxes = new Aabb[children.Count];
        for (int i = 0; i < children.Count; i++)
        {
            identities[i] = Mat4.Identity;
            boxes[i] = new Aabb(children[i].BoundingBoxMin, children[i].BoundingBoxMax);
        }
        composite.CurrentMatrices = new SimpleArray<Mat4>(identities);
        composite.LastMatrices = new SimpleArray<Mat4>((Mat4[])identities.Clone());
        composite.ChildBoundingBoxes = new SimpleArray<Aabb>(boxes);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var c in children)
        {
            min = Vector3.Min(min, new Vector3(c.BoundingBoxMin.X, c.BoundingBoxMin.Y, c.BoundingBoxMin.Z));
            max = Vector3.Max(max, new Vector3(c.BoundingBoxMax.X, c.BoundingBoxMax.Y, c.BoundingBoxMax.Z));
        }
        ApplyBounds(composite, min, max);

        return new Resource5<IvBoundOwner>
        {
            Version = 32,
            ResourceData = new IvBoundOwner { Bound = composite },
        };
    }

    /// <summary>Walk the GTA V bound tree, baking child transforms into world
    /// space and emitting one IV geometry bound per leaf. Flattening rather
    /// than mirroring the hierarchy keeps the output to the one shape we have
    /// verified in game tooling: composite of geometries.</summary>
    private static void Flatten(
        CW.Bounds? b, Mat4 xform, List<PhBound> outList, List<string> notes, byte fallback)
    {
        switch (b)
        {
            case null:
                return;

            case CW.BoundComposite comp:
            {
                var kids = comp.Children?.data_items;
                if (kids == null) return;
                foreach (var child in kids)
                {
                    if (child == null) continue;
                    // Each child carries its own placement; compose it with the
                    // transform inherited from above.
                    var local = Mat4.CreateFromQuaternion(new Quaternion(
                                    child.Orientation.X, child.Orientation.Y,
                                    child.Orientation.Z, child.Orientation.W))
                              * Mat4.CreateTranslation(
                                    child.Position.X, child.Position.Y, child.Position.Z);
                    Flatten(child, local * xform, outList, notes, fallback);
                }
                return;
            }

            case CW.BoundGeometry geom:
            {
                var prim = FromGeometry(geom, xform, notes, fallback);
                if (prim != null) outList.Add(prim);
                return;
            }

            case CW.BoundBox box:
                outList.Add(FromBoxLike(box, xform, fallback));
                notes.Add("a box bound was tessellated into triangles (GTA IV primitives are tri/quad only)");
                return;

            case CW.BoundSphere sph:
                outList.Add(FromBoxLike(sph, xform, fallback));
                notes.Add("a sphere bound was approximated by its bounding box");
                return;

            case CW.BoundCapsule cap:
                outList.Add(FromBoxLike(cap, xform, fallback));
                notes.Add("a capsule bound was approximated by its bounding box");
                return;

            case CW.BoundCylinder cyl:
                outList.Add(FromBoxLike(cyl, xform, fallback));
                notes.Add("a cylinder bound was approximated by its bounding box (GTA IV has no cylinder type)");
                return;

            default:
                notes.Add($"bound type {b.GetType().Name} has no GTA IV equivalent and was skipped");
                return;
        }
    }

    private static Mat4 Mul(Mat4 a, Mat4 b) => Mat4.Multiply(a, b);

    private static Vector3 Xf(Vector3 v, Mat4 m) => Vector3.Transform(v, m);

    /// <summary>Convert a GTA V polygon-soup bound.</summary>
    private static PhBoundGeometry? FromGeometry(CW.BoundGeometry g, Mat4 xform, List<string> notes, byte fallback)
    {
        var verts = g.Vertices;
        var polys = g.Polygons;
        if (verts == null || polys == null || verts.Length == 0) return null;

        // GTA V stores a geometry bound's vertices RELATIVE TO CenterGeom, not
        // in the bound's own space. Skipping that offset silently drops the
        // collision to the origin — on a 17.5 m building it landed 8.7 m below
        // the model, which looks like a floating prop rather than a bad import.
        var centreGeom = new Vector3(g.CenterGeom.X, g.CenterGeom.Y, g.CenterGeom.Z);
        var pos = new List<Vector3>(verts.Length);
        foreach (var v in verts)
            pos.Add(Xf(new Vector3(v.X, v.Y, v.Z) + centreGeom, xform));

        var tris = new List<(int A, int B, int C, byte Mat)>();
        int nonTri = 0;
        foreach (var p in polys)
        {
            if (p is CW.BoundPolygonTriangle t)
            {
                byte mat = fallback;
                tris.Add((t.vertIndex1, t.vertIndex2, t.vertIndex3, mat));
            }
            else nonTri++;
        }
        if (nonTri > 0)
            notes.Add($"{nonTri} non-triangle collision primitive(s) skipped (GTA IV takes tri/quad only)");
        if (tris.Count == 0) return null;

        return Build(pos, tris, notes);
    }

    private static byte MaterialForPoly(CW.BoundGeometry g, CW.BoundPolygon p)
    {
        try
        {
            var idx = g.PolygonMaterialIndices;
            if (idx != null && p.Index >= 0 && p.Index < idx.Length)
            {
                var mats = g.Materials;
                if (mats != null && idx[p.Index] < mats.Length)
                    return IvCollisionMaterials.Resolve(mats[idx[p.Index]].Type.ToString());
            }
        }
        catch { /* fall through */ }
        return IvCollisionMaterials.Default;
    }

    /// <summary>Approximate a primitive bound by its (transformed) box.</summary>
    private static PhBoundGeometry FromBoxLike(CW.Bounds b, Mat4 xform, byte material)
    {
        var mn = new Vector3(b.BoxMin.X, b.BoxMin.Y, b.BoxMin.Z);
        var mx = new Vector3(b.BoxMax.X, b.BoxMax.Y, b.BoxMax.Z);

        var corners = new List<Vector3>
        {
            Xf(new Vector3(mn.X, mn.Y, mn.Z), xform), Xf(new Vector3(mx.X, mn.Y, mn.Z), xform),
            Xf(new Vector3(mx.X, mx.Y, mn.Z), xform), Xf(new Vector3(mn.X, mx.Y, mn.Z), xform),
            Xf(new Vector3(mn.X, mn.Y, mx.Z), xform), Xf(new Vector3(mx.X, mn.Y, mx.Z), xform),
            Xf(new Vector3(mx.X, mx.Y, mx.Z), xform), Xf(new Vector3(mn.X, mx.Y, mx.Z), xform),
        };
        // 12 triangles, outward winding.
        int[,] faces =
        {
            {0,2,1},{0,3,2}, {4,5,6},{4,6,7}, {0,1,5},{0,5,4},
            {1,2,6},{1,6,5}, {2,3,7},{2,7,6}, {3,0,4},{3,4,7},
        };
        var tris = new List<(int, int, int, byte)>();
        for (int i = 0; i < faces.GetLength(0); i++)
            tris.Add((faces[i, 0], faces[i, 1], faces[i, 2], material));

        return Build(corners, tris, new List<string>())!;
    }

    /// <summary>Quantise and assemble an IV geometry bound.</summary>
    private static PhBoundGeometry? Build(
        List<Vector3> pos, List<(int A, int B, int C, byte Mat)> tris, List<string> notes)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in pos) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }

        var pad = new Vector3(QuantPad);
        var qMin = min - pad; var qMax = max + pad;
        var extent = qMax - qMin;
        var quantum = new Vector3(
            MathF.Max(extent.X, 1e-4f) / 65535f,
            MathF.Max(extent.Y, 1e-4f) / 65535f,
            MathF.Max(extent.Z, 1e-4f) / 65535f);
        var offset = (qMin + qMax) * 0.5f;
        var centre = (min + max) * 0.5f;

        var verts = new BoundVertex[pos.Count];
        var shrunk = new BoundVertex[pos.Count];
        for (int i = 0; i < pos.Count; i++)
        {
            verts[i] = Quantise(pos[i], offset, quantum);
            shrunk[i] = Quantise(centre + (pos[i] - centre) * 0.96f, offset, quantum);
        }

        // One material entry per distinct surface, primitives index into it.
        var palette = new List<byte>();
        var prims = new PhBoundPrimitive[tris.Count];
        for (int t = 0; t < tris.Count; t++)
        {
            var (a, b, c, mat) = tris[t];
            int slot = palette.IndexOf(mat);
            if (slot < 0) { palette.Add(mat); slot = palette.Count - 1; }

            var n = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
            float len = n.Length();
            float area = len * 0.5f;
            n = len > 1e-12f ? n / len : new Vector3(0, 0, 1);

            var prim = new PhBoundPrimitive
            {
                Normal = n,
                VertexIndex1 = (short)a, VertexIndex2 = (short)b,
                VertexIndex3 = (short)c, VertexIndex4 = 0,
                NeighborIndex1 = -1, NeighborIndex2 = -1,
                NeighborIndex3 = -1, NeighborIndex4 = -1,
            };
            prim.Area = area;
            prim.Material = (byte)slot;
            prims[t] = prim;
        }
        if (palette.Count == 0) palette.Add(IvCollisionMaterials.Default);

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
            Materials = new SimpleArray<PhBoundMaterial>(
                palette.Select(m => new PhBoundMaterial { MaterialId = m }).ToArray()),
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
