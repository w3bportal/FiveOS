// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using Assimp;
using CodeWalker;

namespace YdrWriter;

/// <summary>
/// Builds an in-memory FbxDocument directly from an Assimp scene, then
/// serializes it via CodeWalker.Core's own FbxBinaryWriter.
///
/// Why this exists: AssimpNet 5.0-beta1's FBX exporter loses per-vertex UVs
/// when multiple verts share a 3D position with distinct UVs (typical for
/// low-poly assets where UV islands meet at shared corners — e.g. a laptop
/// where the screen front and the lid back share the lid's edge verts).
/// The exporter writes only ~12 of the expected 144 screen-UV slots into
/// LayerElementUV/UVIndex, so the lid back ends up sampling the screen
/// texture instead of the dark color it was meant to.
///
/// We side-step the bug by emitting per-polygon-vertex UVs with the trivial
/// identity UVIndex [0..n) — every polygon-vertex carries its own UV, no
/// dedup needed. The FBX is larger than Assimp's output but CodeWalker's
/// FbxConverter reads it byte-for-byte the same way.
/// </summary>
public static class DirectFbxBuilder
{
    /// <param name="excludeMeshNames">Optional set of Assimp mesh names to
    /// skip emitting. Used by the layers panel to drop user-hidden parts
    /// from the converted YDR. Matching is exact + case-insensitive on
    /// <see cref="Mesh.Name"/>.</param>
    public static byte[] Build(Scene scene, IReadOnlySet<string>? excludeMeshNames = null)
    {
        var doc = new FbxDocument { Version = FbxVersion.v7_4 };

        // FbxBinaryWriter computes a footer signature from the timestamp at
        // FBXHeaderExtension/CreationTimeStamp — without it the writer throws
        // before emitting anything. Values are arbitrary; CW only uses them
        // to seed the footer hash.
        var hdr = new FbxNode { Name = "FBXHeaderExtension" };
        var ts = new FbxNode { Name = "CreationTimeStamp" };
        ts.Nodes.Add(Leaf("Version", 1000));
        ts.Nodes.Add(Leaf("Year", 2026));
        ts.Nodes.Add(Leaf("Month", 1));
        ts.Nodes.Add(Leaf("Day", 1));
        ts.Nodes.Add(Leaf("Hour", 0));
        ts.Nodes.Add(Leaf("Minute", 0));
        ts.Nodes.Add(Leaf("Second", 0));
        ts.Nodes.Add(Leaf("Millisecond", 0));
        hdr.Nodes.Add(ts);
        doc.Nodes.Add(hdr);

        var objects = new FbxNode { Name = "Objects" };
        var connections = new FbxNode { Name = "Connections" };
        doc.Nodes.Add(objects);
        doc.Nodes.Add(connections);

        long nextId = 1000000;
        long NewId() => unchecked(nextId++);

        // One Material node per Assimp material. CW.FbxConverter only reads
        // the material's name (for shader naming) — actual texture binding
        // is done later by TextureBaker, so a minimal node is enough.
        var matIds = new long[scene.MaterialCount];
        for (int i = 0; i < scene.MaterialCount; i++)
        {
            var srcMat = scene.Materials[i];
            var name = string.IsNullOrEmpty(srcMat.Name) ? $"mat{i}" : srcMat.Name;
            matIds[i] = NewId();
            objects.Nodes.Add(BuildMaterial(matIds[i], name));
        }

        // One Geometry + Model per Assimp mesh. CW maps shaders to mesh order
        // (one shader per mesh, not per material), so this preserves the
        // mapping TextureBaker.BindShaderTextures relies on.
        for (int mi = 0; mi < scene.MeshCount; mi++)
        {
            var mesh = scene.Meshes[mi];
            // Skip user-hidden parts (layers panel). The match is exact +
            // case-insensitive on the Assimp mesh name. Note: the user's
            // displayed names come from three.js's currentModel.children
            // iteration, which usually maps to the same names Assimp sees,
            // but exotic exporters may diverge — when that happens nothing
            // gets skipped here, which is the safe default.
            if (excludeMeshNames != null && !string.IsNullOrEmpty(mesh.Name) &&
                excludeMeshNames.Contains(mesh.Name))
                continue;
            var geomId = NewId();
            var modelId = NewId();
            objects.Nodes.Add(BuildGeometry(geomId, mesh));
            objects.Nodes.Add(BuildModel(modelId, mesh.Name ?? $"mesh{mi}"));

            // Connect: Model -> root, Geometry -> Model, Material -> Model.
            // FbxDocument.GetSceneNodes uses C[1]=child, C[2]=parent.
            connections.Nodes.Add(Conn("OO", modelId, 0));
            connections.Nodes.Add(Conn("OO", geomId, modelId));
            int matIdx = mesh.MaterialIndex;
            if (matIdx >= 0 && matIdx < matIds.Length)
                connections.Nodes.Add(Conn("OO", matIds[matIdx], modelId));
        }

        using var ms = new MemoryStream();
        var w = new FbxBinaryWriter(ms);
        // Disable per-array zlib compression — CW.Core's writer-reader pair
        // has a checksum mismatch on larger compressed arrays (verified on
        // 20-mesh deadnaut.glb: read fails with "Compressed data has invalid
        // checksum"). The uncompressed path round-trips cleanly.
        w.CompressionThreshold = int.MaxValue;
        w.Write(doc);
        return ms.ToArray();
    }

    private static FbxNode BuildMaterial(long id, string name)
    {
        var n = new FbxNode { Name = "Material" };
        n.Properties.Add(id);
        // CW.FbxConverter strips the trailing "Material" sentinel when
        // reading — Assimp's FBX export uses the same convention. We follow
        // it so existing CW heuristics that key on the cleaned name still work.
        n.Properties.Add(name + "\x00\x01Material");
        n.Properties.Add("");
        n.Nodes.Add(Leaf("Version", 102));
        n.Nodes.Add(Leaf("ShadingModel", "phong"));
        n.Nodes.Add(Leaf("MultiLayer", 0));
        return n;
    }

    private static FbxNode BuildModel(long id, string name)
    {
        var n = new FbxNode { Name = "Model" };
        n.Properties.Add(id);
        n.Properties.Add(name + "\x00\x01Model");
        n.Properties.Add("Mesh");
        n.Nodes.Add(Leaf("Version", 232));
        // Skip Shading (bool) — CW.Core's FbxBinaryWriter has a known cast
        // bug: it does `(byte)(char)obj` on bool values which throws. CW's
        // FbxConverter doesn't read Shading anyway.
        n.Nodes.Add(Leaf("Culling", "CullingOff"));
        return n;
    }

    private static FbxNode BuildGeometry(long id, Mesh mesh)
    {
        var n = new FbxNode { Name = "Geometry" };
        n.Properties.Add(id);
        n.Properties.Add("\x00\x01Geometry");
        n.Properties.Add("Mesh");

        // Vertices: one entry per unique vertex position in the Assimp mesh.
        // We do NOT dedup — Assimp already gave us a flat per-vertex array
        // and CW's reader doesn't care about position uniqueness.
        var verts = new double[mesh.VertexCount * 3];
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            var p = mesh.Vertices[i];
            verts[i*3 + 0] = p.X;
            verts[i*3 + 1] = p.Y;
            verts[i*3 + 2] = p.Z;
        }
        n.Nodes.Add(Leaf("Vertices", verts));

        // PolygonVertexIndex: int[] with the LAST index of each polygon
        // bitwise-NOT'd (encoded as -i-1) to mark the polygon boundary.
        //
        // Some source meshes (e.g. this laptop's lid) author face winding
        // opposite to RAGE's expected convention — the front faces end up
        // CCW from the camera POV, so RAGE culls them and we see only the
        // strip-textured back. We detect this per-mesh by comparing each
        // face's geometric normal (from winding) against the stored vertex
        // normal: if they consistently point opposite directions, the whole
        // mesh's winding needs flipping. Doing it per-mesh avoids breaking
        // correctly-wound meshes (the body keyboard) while fixing the lid.
        // No face filtering — we keep all source triangles. Earlier attempts
        // to dedup or filter "back-facing" triangles caused regressions on
        // legitimate concave geometry. The doubled-geometry rendering issue
        // is addressed by per-vertex normal handling instead (CW.FbxConverter
        // dedups the FbxVertex tuples on its own).
        var keepFace = new bool[mesh.FaceCount];
        for (int i = 0; i < keepFace.Length; i++) keepFace[i] = true;

        bool flipWinding = ShouldFlipWinding(mesh);
        var polyIdx = new int[mesh.FaceCount * 3];
        int totalSlots = 0;
        for (int fi = 0; fi < mesh.FaceCount; fi++)
        {
            if (!keepFace[fi]) continue;
            var f = mesh.Faces[fi];
            if (f.IndexCount != 3) continue;
            int i0 = f.Indices[0];
            int i1 = flipWinding ? f.Indices[2] : f.Indices[1];
            int i2 = flipWinding ? f.Indices[1] : f.Indices[2];
            polyIdx[totalSlots + 0] = i0;
            polyIdx[totalSlots + 1] = i1;
            polyIdx[totalSlots + 2] = -i2 - 1;
            totalSlots += 3;
        }
        if (totalSlots != polyIdx.Length)
        {
            var trimmed = new int[totalSlots];
            Array.Copy(polyIdx, trimmed, totalSlots);
            polyIdx = trimmed;
        }
        n.Nodes.Add(Leaf("PolygonVertexIndex", polyIdx));
        n.Nodes.Add(Leaf("GeometryVersion", 124));

        // LayerElementNormal: ByPolygonVertex Direct. Each polygon-vertex slot
        // gets its own normal pulled from the source vertex. Order matches
        // the (possibly flipped) PolygonVertexIndex above.
        if (mesh.HasNormals)
        {
            var normals = new double[totalSlots * 3];
            int idx = 0;
            int[] cornerOrder = flipWinding ? new[] { 0, 2, 1 } : new[] { 0, 1, 2 };
            for (int fi = 0; fi < mesh.FaceCount; fi++)
            {
                if (!keepFace[fi]) continue;
                var f = mesh.Faces[fi];
                if (f.IndexCount != 3) continue;
                for (int ci = 0; ci < 3; ci++)
                {
                    int c = cornerOrder[ci];
                    var nrm = mesh.Normals[f.Indices[c]];
                    normals[idx++] = nrm.X;
                    normals[idx++] = nrm.Y;
                    normals[idx++] = nrm.Z;
                }
            }
            var normLayer = new FbxNode { Name = "LayerElementNormal" };
            normLayer.Properties.Add(0);
            normLayer.Nodes.Add(Leaf("Version", 101));
            normLayer.Nodes.Add(Leaf("Name", ""));
            normLayer.Nodes.Add(Leaf("MappingInformationType", "ByPolygonVertex"));
            normLayer.Nodes.Add(Leaf("ReferenceInformationType", "Direct"));
            normLayer.Nodes.Add(Leaf("Normals", normals));
            n.Nodes.Add(normLayer);
        }

        // LayerElementUV: ByPolygonVertex IndexToDirect with a 1:1 UVIndex
        // [0,1,2,...] so every polygon-vertex carries its own UV. This is the
        // whole point of this builder — no dedup means no UV collisions.
        if (mesh.HasTextureCoords(0))
        {
            var uvs = mesh.TextureCoordinateChannels[0];
            var uvArr = new double[totalSlots * 2];
            var uvIdx = new int[totalSlots];
            int idx = 0;
            int slot = 0;
            int[] cornerOrder = flipWinding ? new[] { 0, 2, 1 } : new[] { 0, 1, 2 };
            for (int fi = 0; fi < mesh.FaceCount; fi++)
            {
                if (!keepFace[fi]) continue;
                var f = mesh.Faces[fi];
                if (f.IndexCount != 3) continue;
                for (int ci = 0; ci < 3; ci++)
                {
                    int c = cornerOrder[ci];
                    var t = uvs[f.Indices[c]];
                    uvArr[idx++] = t.X;
                    // Flip V back to glTF/D3D top-origin. Assimp's GLB import
                    // converts V to bottom-origin (1-V); we undo that here so
                    // CW writes positive [0..1] V values into the YDR vertex
                    // buffer. Pairing this with InvertTexcoordV=false on the
                    // FbxConverter means no further flips downstream.
                    uvArr[idx++] = 1.0 - t.Y;
                    uvIdx[slot] = slot;
                    slot++;
                }
            }
            var uvLayer = new FbxNode { Name = "LayerElementUV" };
            uvLayer.Properties.Add(0);
            uvLayer.Nodes.Add(Leaf("Version", 101));
            uvLayer.Nodes.Add(Leaf("Name", ""));
            uvLayer.Nodes.Add(Leaf("MappingInformationType", "ByPolygonVertex"));
            uvLayer.Nodes.Add(Leaf("ReferenceInformationType", "IndexToDirect"));
            uvLayer.Nodes.Add(Leaf("UV", uvArr));
            uvLayer.Nodes.Add(Leaf("UVIndex", uvIdx));
            n.Nodes.Add(uvLayer);
        }

        // LayerElementMaterial: AllSame with a single index 0. CW resolves
        // the actual material via the Model->Material connection, so the
        // index value here is symbolic — every polygon belongs to mat 0.
        var matLayer = new FbxNode { Name = "LayerElementMaterial" };
        matLayer.Properties.Add(0);
        matLayer.Nodes.Add(Leaf("Version", 101));
        matLayer.Nodes.Add(Leaf("Name", ""));
        matLayer.Nodes.Add(Leaf("MappingInformationType", "AllSame"));
        matLayer.Nodes.Add(Leaf("ReferenceInformationType", "IndexToDirect"));
        matLayer.Nodes.Add(Leaf("Materials", new int[] { 0 }));
        n.Nodes.Add(matLayer);

        return n;
    }

    /// <summary>Decides whether a mesh's face winding needs flipping for RAGE.
    /// Compares each triangle's geometric normal (from vertex order via cross
    /// product) to the average of its three vertex normals. If the dot product
    /// is consistently negative across the mesh, the winding is "inverted" and
    /// flipping it brings the front faces onto RAGE's expected side. Some
    /// glTF assets author meshes with this convention (the laptop's lid is one
    /// such case — its front face is CCW from the screen-side, opposite to the
    /// body's keyboard which is CCW from the keyboard-side).</summary>
    private static bool ShouldFlipWinding(Mesh mesh)
    {
        if (!mesh.HasNormals) return false;
        int agreeCount = 0, disagreeCount = 0;
        foreach (var f in mesh.Faces)
        {
            if (f.IndexCount != 3) continue;
            var p0 = mesh.Vertices[f.Indices[0]];
            var p1 = mesh.Vertices[f.Indices[1]];
            var p2 = mesh.Vertices[f.Indices[2]];
            // Geometric normal from CCW winding (p1-p0) x (p2-p0)
            var ax = p1.X - p0.X; var ay = p1.Y - p0.Y; var az = p1.Z - p0.Z;
            var bx = p2.X - p0.X; var by = p2.Y - p0.Y; var bz = p2.Z - p0.Z;
            var gnx = ay*bz - az*by;
            var gny = az*bx - ax*bz;
            var gnz = ax*by - ay*bx;
            // Average vertex normal
            var n0 = mesh.Normals[f.Indices[0]];
            var n1 = mesh.Normals[f.Indices[1]];
            var n2 = mesh.Normals[f.Indices[2]];
            var vnx = (n0.X + n1.X + n2.X) / 3f;
            var vny = (n0.Y + n1.Y + n2.Y) / 3f;
            var vnz = (n0.Z + n1.Z + n2.Z) / 3f;
            var dot = gnx*vnx + gny*vny + gnz*vnz;
            if (dot > 0) agreeCount++;
            else if (dot < 0) disagreeCount++;
        }
        // Flip if the majority of faces have geometric/vertex normals pointing
        // opposite directions. The threshold is a strict majority — borderline
        // meshes (mixed winding from doubleSided geometry) keep their original
        // order to avoid making things worse than they were.
        return disagreeCount > agreeCount * 2;
    }

    /// <summary>Hash a triangle by its three vertex positions, ignoring
    /// vertex order. Two triangles with the same 3 spatial points (in any
    /// permutation) hash identically, which lets us detect pre-baked
    /// doubleSided pairs without false positives on legitimate adjacent
    /// faces (those share at most 2 verts, not all 3).</summary>
    private static long TriPositionHash(Mesh mesh, int i0, int i1, int i2)
    {
        const float q = 10000f; // 0.1 mm grid — well below any sensible mesh detail
        var p0 = mesh.Vertices[i0];
        var p1 = mesh.Vertices[i1];
        var p2 = mesh.Vertices[i2];
        long k0 = QuantPos(p0, q);
        long k1 = QuantPos(p1, q);
        long k2 = QuantPos(p2, q);
        // Sort so winding doesn't affect the hash.
        if (k0 > k1) (k0, k1) = (k1, k0);
        if (k1 > k2) (k1, k2) = (k2, k1);
        if (k0 > k1) (k0, k1) = (k1, k0);
        unchecked { return k0 * 73856093L ^ k1 * 19349663L ^ k2 * 83492791L; }
    }

    private static long QuantPos(Assimp.Vector3D p, float q)
    {
        long ix = (long)Math.Round(p.X * q);
        long iy = (long)Math.Round(p.Y * q);
        long iz = (long)Math.Round(p.Z * q);
        return ((ix & 0x1FFFFF) << 42) | ((iy & 0x1FFFFF) << 21) | (iz & 0x1FFFFF);
    }

    private static FbxNode Leaf(string name, object value)
    {
        var n = new FbxNode { Name = name };
        n.Properties.Add(value);
        return n;
    }

    private static FbxNode Conn(string type, long child, long parent)
    {
        var n = new FbxNode { Name = "C" };
        n.Properties.Add(type);
        n.Properties.Add(child);
        n.Properties.Add(parent);
        return n;
    }
}
