// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.IO;
using Assimp;
using g3;

namespace FiveOS.Services;

/// <summary>
/// One-click decimator for *source* user models (.obj / .glb / .gltf / .fbx /
/// .dae / .ply / .stl) — runs BEFORE the YDR conversion. Reads via Assimp,
/// reduces each mesh via g3sharp's quadric-edge-collapse Reducer, writes the
/// optimized model back as GLB to a sibling temp file.
///
/// This is intentionally separate from <see cref="DrawableOptimizer"/>:
/// that one operates on already-converted .ydr / .yft / .ydd resources via
/// CodeWalker.Core. SourceMeshOptimizer is for the import-time UX where we
/// want to preview the optimized mesh in the viewer *before* the user
/// commits to a YDR conversion.
///
/// Caveat: AssimpNet's GLB exporter has historically dropped per-vertex UVs
/// on FBX→FBX round-trips (see DirectFbxBuilder for context). For raw mesh
/// decimation the round-trip is GLB-out which is more reliable, but heavy
/// material binding can still be lossy. We accept that tradeoff because the
/// alternative is teaching every loader path to consume an in-memory mesh,
/// which is a much bigger change.
/// </summary>
public static class SourceMeshOptimizer
{
    /// <summary>
    /// <paramref name="ProjectToOriginalSurface"/> defaults to false: enabling
    /// it builds a per-mesh spatial tree (`MeshProjectionTarget.Auto`) so the
    /// reducer snaps survivors back onto the original surface, which improves
    /// silhouette accuracy at the cost of often-doubling decimation runtime.
    /// For the import-time auto-optimize we prioritise speed over the last
    /// few percent of accuracy.
    /// </summary>
    public sealed record Options(
        int TargetTriangles,
        bool PreserveBoundary = true,
        bool ProjectToOriginalSurface = false);

    public sealed record Result(
        string OutputPath,
        int TrianglesBefore,
        int TrianglesAfter,
        long BytesBefore,
        long BytesAfter,
        System.TimeSpan Elapsed,
        string? Error);

    /// <summary>
    /// Decimate only the meshes that belong to the named node sub-tree.
    /// Wired up to the layer-panel "Optimize Mesh" context-menu action so
    /// the user can pick a target tri count for one part without touching
    /// the rest of the scene.
    ///
    /// The named node is matched against the same identifier the viewer
    /// reports as the part's source name (i.e. <c>scene.RootNode</c>'s
    /// direct child whose Name matches). Node not found ⇒ Error result.
    /// </summary>
    public static Result OptimizePart(string inputPath, string partName, int targetTriangles,
                                      System.Action<string>? progress = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long bytesBefore = new FileInfo(inputPath).Length;
        try
        {
            progress?.Invoke("Reading model...");
            using var importer = new AssimpContext();
            var scene = importer.ImportFile(inputPath,
                PostProcessSteps.Triangulate | PostProcessSteps.EmbedTextures);
            if (scene == null || scene.RootNode == null || !scene.HasMeshes)
                throw new System.InvalidOperationException("Assimp couldn't load the model.");

            // Collect mesh indices belonging to the named node's subtree.
            var meshIndices = CollectMeshIndicesForNamedNode(scene.RootNode, partName);
            if (meshIndices.Count == 0)
                throw new System.InvalidOperationException(
                    $"No meshes found under part '{partName}'. The part name may have been renamed in the panel — try reloading the model.");

            // Sum tris in the targeted subset; distribute the budget the
            // same way the global Optimize does, but only across those.
            int trisBefore = 0;
            foreach (var idx in meshIndices) trisBefore += scene.Meshes[idx].FaceCount;

            int target = System.Math.Max(64, targetTriangles);
            var jobs = new System.Collections.Generic.List<(Mesh Mesh, int Target)>(meshIndices.Count);
            foreach (var idx in meshIndices)
            {
                var m = scene.Meshes[idx];
                if (m.FaceCount < 32) continue;
                double share = (double)m.FaceCount / System.Math.Max(1, trisBefore);
                int meshTarget = System.Math.Max(32, (int)System.Math.Round(target * share));
                if (meshTarget >= m.FaceCount) continue;
                jobs.Add((m, meshTarget));
            }

            progress?.Invoke($"Decimating {jobs.Count} mesh{(jobs.Count == 1 ? "" : "es")} in '{partName}'...");
            int done = 0;
            System.Threading.Tasks.Parallel.ForEach(jobs, job =>
            {
                DecimateMesh(job.Mesh, job.Target, projectToSurface: false);
                int n = System.Threading.Interlocked.Increment(ref done);
                progress?.Invoke($"Decimated {n}/{jobs.Count}...");
            });

            int trisAfter = 0;
            foreach (var idx in meshIndices) trisAfter += scene.Meshes[idx].FaceCount;

            NormalizeFbxUnitScale(scene, inputPath, progress);
            FlipUVsForGltf(scene);

            progress?.Invoke("Writing GLB...");
            var outputPath = OptimizedOutputPath(inputPath);
            importer.ExportFile(scene, outputPath, "glb2");
            GlbTextureEmbedder.EmbedImages(outputPath, scene,
                Path.GetDirectoryName(Path.GetFullPath(inputPath)), progress);

            sw.Stop();
            long bytesAfter = new FileInfo(outputPath).Length;
            return new Result(outputPath, trisBefore, trisAfter, bytesBefore,
                              bytesAfter, sw.Elapsed, null);
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            return new Result(inputPath, 0, 0, bytesBefore, 0, sw.Elapsed, ex.Message);
        }
    }

    /// <summary>
    /// Texture-weight optimize: leave the geometry alone and shrink the
    /// textures instead — downscale anything over
    /// <paramref name="maxTextureDim"/> and re-embed into a self-contained
    /// GLB. This is the banner's one-click action for models whose tri
    /// count is fine but whose file is fat with 4K AI bakes (decimation
    /// can't touch that weight).
    /// </summary>
    public static Result OptimizeTextures(string inputPath, int maxTextureDim,
                                          System.Action<string>? progress = null,
                                          string? outputPath = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long bytesBefore = new FileInfo(inputPath).Length;
        try
        {
            progress?.Invoke("Reading model...");
            using var importer = new AssimpContext();
            var scene = importer.ImportFile(inputPath,
                PostProcessSteps.Triangulate | PostProcessSteps.EmbedTextures);
            if (scene == null || !scene.HasMeshes)
                throw new System.InvalidOperationException("Assimp couldn't load the model.");

            int tris = 0;
            foreach (var m in scene.Meshes) tris += m.FaceCount;

            NormalizeFbxUnitScale(scene, inputPath, progress);
            FlipUVsForGltf(scene);

            progress?.Invoke("Writing GLB...");
            outputPath ??= OptimizedOutputPath(inputPath);
            importer.ExportFile(scene, outputPath, "glb2");
            progress?.Invoke($"Compressing textures to {maxTextureDim}px...");
            GlbTextureEmbedder.EmbedImages(outputPath, scene,
                Path.GetDirectoryName(Path.GetFullPath(inputPath)), progress, maxTextureDim);

            sw.Stop();
            long bytesAfter = new FileInfo(outputPath).Length;
            return new Result(outputPath, tris, tris, bytesBefore, bytesAfter, sw.Elapsed, null);
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            return new Result(inputPath, 0, 0, bytesBefore, 0, sw.Elapsed, ex.Message);
        }
    }

    /// <summary>
    /// assimp 5.0's glTF2 EXPORTER writes texcoords verbatim, but assimp's
    /// internal convention is bottom-left V (FBX/OBJ keep theirs, and the
    /// glTF2 IMPORTER flips incoming top-left UVs down to match). glTF
    /// wants top-left, so every un-flipped export samples its textures
    /// vertically mirrored — the "rainbow-mangled atlas" look. Flip V on
    /// every UV channel right before each glb2 export. (Fixed upstream in
    /// later assimp; our vendored 5.0 needs it done by hand.)
    /// </summary>
    private static void FlipUVsForGltf(Assimp.Scene scene)
    {
        foreach (var m in scene.Meshes)
        {
            for (int ch = 0; ch < m.TextureCoordinateChannels.Length; ch++)
            {
                if (!m.HasTextureCoords(ch)) continue;
                var list = m.TextureCoordinateChannels[ch];
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    list[i] = new Vector3D(t.X, 1f - t.Y, t.Z);
                }
            }
        }
    }

    /// <summary>Sibling output path for the optimized GLB. Strips an
    /// already-present ".fiveos-optimized" stem so re-optimizing an
    /// optimized file doesn't stack suffixes
    /// (foo.fiveos-optimized.fiveos-optimized.glb). Public so the preview
    /// UI can commit a temp-dir preview under the canonical name.</summary>
    public static string OptimizedOutputPath(string inputPath)
    {
        const string suffix = ".fiveos-optimized";
        var stem = Path.ChangeExtension(inputPath, null);
        if (stem.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase))
            stem = stem[..^suffix.Length];
        return stem + suffix + ".glb";
    }

    /// <summary>
    /// FBX files usually store geometry in centimetres; assimp keeps that
    /// and bakes a ~100× unit scale into the root node's transform. The
    /// viewer compensates for raw .fbx loads (its cm→m heuristic), but a
    /// GLB exported from that scene carries the 100× baked in and glTF is
    /// metres-by-convention — so the optimized preview loads a hundred
    /// times bigger than the model the user was just looking at, the
    /// camera reframes to a ~200 m giant, and the host auto-fit then
    /// shrinks the mesh into an invisible speck. Divide the root scale
    /// back out so the exported GLB matches what the viewer showed.
    /// </summary>
    private static void NormalizeFbxUnitScale(Assimp.Scene scene, string inputPath,
                                              System.Action<string>? progress)
    {
        if (!Path.GetExtension(inputPath).Equals(".fbx", System.StringComparison.OrdinalIgnoreCase))
            return;
        if (scene.RootNode == null) return;

        // The importer bakes the cm→m factor into whatever node carries the
        // geometry (often a child of the root, not the root itself), so walk
        // the whole tree and strip every large uniform scale we find. Each
        // strip right-multiplies by S(1/s), which uniformly shrinks that
        // node's entire subtree — child translations included — so nested
        // unit factors compose correctly and no other node needs touching.
        var stack = new System.Collections.Generic.Stack<Node>();
        stack.Push(scene.RootNode);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var c in node.Children) stack.Push(c);

            var t = node.Transform;
            // Per-axis scale = row lengths of the linear 3×3 block.
            double sx = System.Math.Sqrt((double)t.A1 * t.A1 + t.A2 * t.A2 + t.A3 * t.A3);
            double sy = System.Math.Sqrt((double)t.B1 * t.B1 + t.B2 * t.B2 + t.B3 * t.B3);
            double sz = System.Math.Sqrt((double)t.C1 * t.C1 + t.C2 * t.C2 + t.C3 * t.C3);
            double avg = (sx + sy + sz) / 3.0;
            if (avg < 2.0) continue;  // metres-ish already (cm exports land at ~100)
            // Only normalize a *uniform* unit scale — a deliberately
            // non-uniform transform is authored content we shouldn't touch.
            if (System.Math.Abs(sx - avg) / avg > 0.05 ||
                System.Math.Abs(sy - avg) / avg > 0.05 ||
                System.Math.Abs(sz - avg) / avg > 0.05)
                continue;

            float f = (float)(1.0 / avg);
            // Right-multiply by S(1/s): scale only the linear 3×3 block.
            // Translation stays — the node keeps its place while everything
            // beneath it (meshes, child offsets) shrinks uniformly.
            t.A1 *= f; t.A2 *= f; t.A3 *= f;
            t.B1 *= f; t.B2 *= f; t.B3 *= f;
            t.C1 *= f; t.C2 *= f; t.C3 *= f;
            node.Transform = t;
            progress?.Invoke($"Normalized FBX unit scale on '{node.Name}' ({avg:F0}× → 1×)");
        }

        // Some exporters (Cinema 4D among them) bake centimetres into the
        // vertices themselves with identity node scales — nothing above
        // catches that. Mirror the viewer's own display heuristic for raw
        // .fbx (bbox height > 10 ⇒ ×0.01) so the GLB matches what the user
        // was looking at before optimizing.
        var (minY, maxY) = WorldYBounds(scene);
        if (maxY - minY > 10.0)
        {
            var rt = scene.RootNode.Transform;
            const float s = 0.01f;
            rt.A1 *= s; rt.A2 *= s; rt.A3 *= s; rt.A4 *= s;
            rt.B1 *= s; rt.B2 *= s; rt.B3 *= s; rt.B4 *= s;
            rt.C1 *= s; rt.C2 *= s; rt.C3 *= s; rt.C4 *= s;
            rt.D1 *= s; rt.D2 *= s; rt.D3 *= s;
            scene.RootNode.Transform = rt;
            progress?.Invoke($"Normalized FBX unit scale (bbox height {maxY - minY:F0} → cm→m)");
        }
    }

    /// <summary>World-space Y extent of every mesh vertex in the scene,
    /// computed with explicit row-major/column-vector math (translation in
    /// A4/B4/C4, assimp's convention) so we don't depend on AssimpNet's
    /// operator semantics.</summary>
    private static (double MinY, double MaxY) WorldYBounds(Assimp.Scene scene)
    {
        double minY = double.MaxValue, maxY = double.MinValue;

        static Matrix4x4 Mul(in Matrix4x4 p, in Matrix4x4 c) => new(
            p.A1 * c.A1 + p.A2 * c.B1 + p.A3 * c.C1 + p.A4 * c.D1,
            p.A1 * c.A2 + p.A2 * c.B2 + p.A3 * c.C2 + p.A4 * c.D2,
            p.A1 * c.A3 + p.A2 * c.B3 + p.A3 * c.C3 + p.A4 * c.D3,
            p.A1 * c.A4 + p.A2 * c.B4 + p.A3 * c.C4 + p.A4 * c.D4,
            p.B1 * c.A1 + p.B2 * c.B1 + p.B3 * c.C1 + p.B4 * c.D1,
            p.B1 * c.A2 + p.B2 * c.B2 + p.B3 * c.C2 + p.B4 * c.D2,
            p.B1 * c.A3 + p.B2 * c.B3 + p.B3 * c.C3 + p.B4 * c.D3,
            p.B1 * c.A4 + p.B2 * c.B4 + p.B3 * c.C4 + p.B4 * c.D4,
            p.C1 * c.A1 + p.C2 * c.B1 + p.C3 * c.C1 + p.C4 * c.D1,
            p.C1 * c.A2 + p.C2 * c.B2 + p.C3 * c.C2 + p.C4 * c.D2,
            p.C1 * c.A3 + p.C2 * c.B3 + p.C3 * c.C3 + p.C4 * c.D3,
            p.C1 * c.A4 + p.C2 * c.B4 + p.C3 * c.C4 + p.C4 * c.D4,
            p.D1 * c.A1 + p.D2 * c.B1 + p.D3 * c.C1 + p.D4 * c.D1,
            p.D1 * c.A2 + p.D2 * c.B2 + p.D3 * c.C2 + p.D4 * c.D2,
            p.D1 * c.A3 + p.D2 * c.B3 + p.D3 * c.C3 + p.D4 * c.D3,
            p.D1 * c.A4 + p.D2 * c.B4 + p.D3 * c.C4 + p.D4 * c.D4);

        void Walk(Node node, in Matrix4x4 parent)
        {
            var world = Mul(parent, node.Transform);
            if (node.HasMeshes)
            {
                foreach (var mi in node.MeshIndices)
                {
                    var mesh = scene.Meshes[mi];
                    foreach (var v in mesh.Vertices)
                    {
                        double y = world.B1 * v.X + world.B2 * v.Y + world.B3 * v.Z + world.B4;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            foreach (var c in node.Children) Walk(c, world);
        }
        Walk(scene.RootNode, Matrix4x4.Identity);
        return minY > maxY ? (0, 0) : (minY, maxY);
    }

    // Find the named part anywhere in the assimp tree, then collect every
    // mesh index in its subtree. Three.js's GLTFLoader exposes the gltf
    // scene's children as "parts", but Assimp's GLTF importer flattens
    // single-scene/single-root files so the same part can sit at the
    // assimp root or one wrapper deeper — search the full tree instead of
    // assuming a fixed depth.
    private static System.Collections.Generic.List<int> CollectMeshIndicesForNamedNode(
        Node root, string partName)
    {
        var result = new System.Collections.Generic.List<int>();
        var target = FindNodeByName(root, partName);
        if (target == null) return result;

        var stack = new System.Collections.Generic.Stack<Node>();
        stack.Push(target);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.HasMeshes)
                foreach (var i in n.MeshIndices) result.Add(i);
            foreach (var c in n.Children) stack.Push(c);
        }
        return result;
    }

    private static Node? FindNodeByName(Node root, string name)
    {
        var stack = new System.Collections.Generic.Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (string.Equals(n.Name, name, System.StringComparison.Ordinal)) return n;
            foreach (var c in n.Children) stack.Push(c);
        }
        return null;
    }

    /// <summary>
    /// Decimate <paramref name="inputPath"/> down to roughly <paramref name="opts"/>.TargetTriangles
    /// total triangles across all sub-meshes, distributed proportionally to
    /// each sub-mesh's share of the input total.
    /// </summary>
    public static Result Optimize(string inputPath, Options opts, System.Action<string>? progress = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long bytesBefore = new FileInfo(inputPath).Length;
        try
        {
            // Phase timers so we can surface which step is the bottleneck.
            var importSw = System.Diagnostics.Stopwatch.StartNew();
            progress?.Invoke("Reading model...");
            using var importer = new AssimpContext();
            // Triangulate (the reducer requires triangles) + EmbedTextures
            // (pull external texture files referenced by .fbx / .gltf into
            // the scene so the GLB export emits them as embedded bytes
            // instead of dangling URI refs — without this, "optimize" looks
            // like it strips the textures).
            var steps = PostProcessSteps.Triangulate
                      | PostProcessSteps.EmbedTextures;
            var scene = importer.ImportFile(inputPath, steps);
            importSw.Stop();
            if (scene == null || !scene.HasMeshes)
                throw new System.InvalidOperationException("Assimp couldn't load the model.");
            progress?.Invoke($"Read in {importSw.ElapsedMilliseconds} ms — preparing decimation...");

            int trisBefore = 0;
            foreach (var m in scene.Meshes)
                trisBefore += m.FaceCount;

            int target = System.Math.Max(64, opts.TargetTriangles);

            // Decide each mesh's per-mesh triangle target up front (so the
            // parallel pass below has no shared mutable state) — proportional
            // to the mesh's share of the input total. Tiny meshes (<32 tris)
            // are skipped entirely.
            var jobs = new System.Collections.Generic.List<(Mesh Mesh, int Target)>(scene.MeshCount);
            foreach (var m in scene.Meshes)
            {
                if (m.FaceCount < 32) continue;
                double share = (double)m.FaceCount / System.Math.Max(1, trisBefore);
                int meshTarget = System.Math.Max(32, (int)System.Math.Round(target * share));
                if (meshTarget >= m.FaceCount) continue;
                jobs.Add((m, meshTarget));
            }

            // Decimate submeshes in parallel. Each DecimateMesh writes only
            // to its own Mesh's vertex/face arrays — no shared state — so
            // Parallel.ForEach is safe here. Worth it: scenes with multiple
            // big submeshes (vehicles, characters) get a near-linear speedup.
            var decimateSw = System.Diagnostics.Stopwatch.StartNew();
            progress?.Invoke($"Decimating {jobs.Count} mesh{(jobs.Count == 1 ? "" : "es")}...");
            int done = 0;
            System.Threading.Tasks.Parallel.ForEach(jobs, job =>
            {
                DecimateMesh(job.Mesh, job.Target, opts.ProjectToOriginalSurface);
                int n = System.Threading.Interlocked.Increment(ref done);
                progress?.Invoke($"Decimating mesh {n} of {jobs.Count}...");
            });
            decimateSw.Stop();

            int trisAfter = 0;
            foreach (var m in scene.Meshes) trisAfter += m.FaceCount;
            progress?.Invoke($"Decimated in {decimateSw.ElapsedMilliseconds} ms — writing GLB...");
            var exportSw = System.Diagnostics.Stopwatch.StartNew();

            NormalizeFbxUnitScale(scene, inputPath, progress);
            FlipUVsForGltf(scene);

            // Write back as GLB next to the input. We use .glb specifically
            // because it round-trips through three.js's loader cleanly and
            // doesn't need a sibling .bin / texture folder.
            var outputPath = OptimizedOutputPath(inputPath);
            importer.ExportFile(scene, outputPath, "glb2");
            GlbTextureEmbedder.EmbedImages(outputPath, scene,
                Path.GetDirectoryName(Path.GetFullPath(inputPath)), progress);
            exportSw.Stop();

            sw.Stop();
            long bytesAfter = new FileInfo(outputPath).Length;
            progress?.Invoke($"Done — read {importSw.ElapsedMilliseconds} ms · decimate {decimateSw.ElapsedMilliseconds} ms · export {exportSw.ElapsedMilliseconds} ms");
            return new Result(outputPath, trisBefore, trisAfter, bytesBefore,
                              bytesAfter, sw.Elapsed, null);
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            return new Result(inputPath, 0, 0, bytesBefore, 0, sw.Elapsed, ex.Message);
        }
    }

    /// <summary>
    /// Replace <paramref name="m"/>'s vertex+face arrays with the decimated
    /// version. Preserves UVs / normals / colors per surviving vertex by
    /// indexing the original arrays through the reducer's surviving-vertex
    /// indices (g3's Reducer never adds new vertices, so the mapping is
    /// safe).
    /// </summary>
    private static void DecimateMesh(Mesh m, int targetTris, bool projectToSurface)
    {
        // Assimp frequently hands FBX geometry over as raw triangle soup —
        // every triangle owning three private vertices. Fed to the reducer
        // as-is, every edge is a boundary edge and NOT ONE collapse is
        // possible: "optimize" silently returns the input. Weld vertices
        // that agree on position+normal+uv0 (the true indexed mesh) so the
        // reducer sees real topology, and remember one representative
        // original index per welded vertex for the attribute rebuild below.
        var dmesh = new DMesh3();
        var weldMap = new int[m.VertexCount];          // original -> welded id
        var repOf = new System.Collections.Generic.List<int>(m.VertexCount); // welded id -> original
        var weldKeys = new System.Collections.Generic.Dictionary<
            (int, int, int, int, int, int, int, int), int>(m.VertexCount);
        var uv0 = m.HasTextureCoords(0) ? m.TextureCoordinateChannels[0] : null;
        const double posQ = 1e5, nrmQ = 1e3, uvQ = 1e6;
        for (int i = 0; i < m.VertexCount; i++)
        {
            var v = m.Vertices[i];
            var n = (m.HasNormals && i < m.Normals.Count) ? m.Normals[i] : default;
            var t = (uv0 != null && i < uv0.Count) ? uv0[i] : default;
            var key = ((int)System.Math.Round(v.X * posQ), (int)System.Math.Round(v.Y * posQ), (int)System.Math.Round(v.Z * posQ),
                       (int)System.Math.Round(n.X * nrmQ), (int)System.Math.Round(n.Y * nrmQ), (int)System.Math.Round(n.Z * nrmQ),
                       (int)System.Math.Round(t.X * uvQ), (int)System.Math.Round(t.Y * uvQ));
            if (!weldKeys.TryGetValue(key, out var wid))
            {
                wid = dmesh.AppendVertex(new Vector3d(v.X, v.Y, v.Z));
                weldKeys[key] = wid;
                repOf.Add(i);
            }
            weldMap[i] = wid;
        }

        int rejected = 0;
        foreach (var f in m.Faces)
        {
            if (f.IndexCount != 3) continue;
            if (f.Indices[0] < 0 || f.Indices[0] >= m.VertexCount) continue;
            if (f.Indices[1] < 0 || f.Indices[1] >= m.VertexCount) continue;
            if (f.Indices[2] < 0 || f.Indices[2] >= m.VertexCount) continue;
            int a = weldMap[f.Indices[0]], b = weldMap[f.Indices[1]], c = weldMap[f.Indices[2]];
            if (a == b || b == c || a == c) continue;
            // AppendTriangle refuses duplicate & non-manifold triangles
            // (returns a negative id) — AI text/scan meshes have some.
            // Count them so the log can explain a stalled reduction.
            if (dmesh.AppendTriangle(a, b, c) < 0) rejected++;
        }
        if (dmesh.TriangleCount < 32)
        {
            FosLogger.Warn("optimize",
                $"decimate skipped: only {dmesh.TriangleCount} of {m.FaceCount} tris were appendable ({rejected} rejected)");
            return;
        }

        var reducer = new Reducer(dmesh);
        // Spatial-tree projection (snap survivors back to the original
        // surface) is the single biggest cost inside Reducer on dense
        // meshes — easily 50-80% of total runtime — so it's opt-in only.
        if (projectToSurface)
            reducer.SetProjectionTarget(MeshProjectionTarget.Auto(dmesh));

        // Pin vertices that sit on UV / material / normal seams: after the
        // attribute weld these are the welded vertices that still share a
        // position with another (one per side of the seam). The quadric
        // reducer only knows positions, so without this pinning it happily
        // collapses the two sides into one survivor and the texture from
        // one side gets stretched across the seam (garbled / smeared UVs).
        ApplyUvSeamConstraints(reducer, dmesh);

        int trisBeforeReduce = dmesh.TriangleCount;
        reducer.ReduceToTriangleCount(targetTris);

        // Atlas-baked meshes (AI generators) split nearly every vertex on a
        // UV island border, so the seam pinning above can leave the reducer
        // little to collapse and the result lands well short of the target.
        // Finish the remainder unconstrained — visible seam smear at heavy
        // reduction beats an optimize that quietly under-delivers.
        if (dmesh.TriangleCount > targetTris * 1.05)
        {
            int afterConstrained = dmesh.TriangleCount;
            var fallback = new Reducer(dmesh);
            if (projectToSurface)
                fallback.SetProjectionTarget(MeshProjectionTarget.Auto(dmesh));
            fallback.ReduceToTriangleCount(targetTris);
            FosLogger.Info("optimize",
                $"decimate: seam-constrained pass reached {afterConstrained} of target {targetTris} " +
                $"(from {trisBeforeReduce}); unconstrained continuation → {dmesh.TriangleCount}");
        }

        // Rebuild Assimp arrays from the reduced DMesh. g3 keeps original
        // vertex indices for survivors, so attribute lookups against m's
        // pre-decimation arrays remain valid.
        //
        // We preserve ALL UV channels (0..N) and ALL vertex-color channels
        // — not just channel 0 — because PBR materials and lightmaps
        // routinely use UV1+ and dropping them silently breaks textures.
        var uvChannelCount = m.TextureCoordinateChannels.Length;
        var colorChannelCount = m.VertexColorChannels.Length;
        var newVerts = new System.Collections.Generic.List<Vector3D>(dmesh.VertexCount);
        var newNormals = m.HasNormals
            ? new System.Collections.Generic.List<Vector3D>(dmesh.VertexCount) : null;
        var newUvs = new System.Collections.Generic.List<Vector3D>?[uvChannelCount];
        for (int ch = 0; ch < uvChannelCount; ch++)
        {
            if (m.HasTextureCoords(ch))
                newUvs[ch] = new System.Collections.Generic.List<Vector3D>(dmesh.VertexCount);
        }
        var newColors = new System.Collections.Generic.List<Color4D>?[colorChannelCount];
        for (int ch = 0; ch < colorChannelCount; ch++)
        {
            if (m.HasVertexColors(ch))
                newColors[ch] = new System.Collections.Generic.List<Color4D>(dmesh.VertexCount);
        }

        var denseMap = new System.Collections.Generic.Dictionary<int, int>(dmesh.VertexCount);
        int dense = 0;
        foreach (var vi in dmesh.VertexIndices())
        {
            denseMap[vi] = dense++;
            var p = dmesh.GetVertex(vi);
            // Positions come from the (possibly moved) reduced mesh;
            // attributes come from the welded vertex's representative
            // original index — by construction every original vertex that
            // welded into vi carried the same normal/uv0 anyway.
            var rep = repOf[vi];
            newVerts.Add(new Vector3D((float)p.x, (float)p.y, (float)p.z));
            if (newNormals != null && rep < m.Normals.Count) newNormals.Add(m.Normals[rep]);
            for (int ch = 0; ch < uvChannelCount; ch++)
            {
                var src = newUvs[ch];
                if (src != null && rep < m.TextureCoordinateChannels[ch].Count)
                    src.Add(m.TextureCoordinateChannels[ch][rep]);
            }
            for (int ch = 0; ch < colorChannelCount; ch++)
            {
                var src = newColors[ch];
                if (src != null && rep < m.VertexColorChannels[ch].Count)
                    src.Add(m.VertexColorChannels[ch][rep]);
            }
        }

        var newFaces = new System.Collections.Generic.List<Face>(dmesh.TriangleCount);
        foreach (var ti in dmesh.TriangleIndices())
        {
            var t = dmesh.GetTriangle(ti);
            var f = new Face();
            f.Indices.Add(denseMap[t.a]);
            f.Indices.Add(denseMap[t.b]);
            f.Indices.Add(denseMap[t.c]);
            newFaces.Add(f);
        }

        // Strip per-vertex channels we don't rebuild. AssimpNet's GLB
        // exporter assumes every populated per-vertex collection is sized
        // to match Vertices.Count; if Tangents (or unused UV slots, or
        // bones) still hold the pre-decimation count, the writer overflows
        // into a freshly-allocated buffer ("Destination array was not long
        // enough"). Bones in particular reference vertex indices that
        // would now be out of range, so dropping them is also correct
        // semantically — bone-aware decimation isn't on this path yet.
        m.Tangents.Clear();
        m.BiTangents.Clear();
        for (int ch = 0; ch < uvChannelCount; ch++)
            if (newUvs[ch] == null) m.TextureCoordinateChannels[ch].Clear();
        for (int ch = 0; ch < colorChannelCount; ch++)
            if (newColors[ch] == null) m.VertexColorChannels[ch].Clear();
        m.Bones.Clear();

        m.Vertices.Clear(); m.Vertices.AddRange(newVerts);
        m.Faces.Clear(); m.Faces.AddRange(newFaces);

        // Pad-or-trim each rebuilt collection to exactly Vertices.Count.
        // The per-vertex guards above (vi < m.Normals.Count, …) can in
        // pathological cases skip a vert and leave an off-by-one; that
        // would re-trigger the same exporter overflow.
        if (newNormals != null)
        {
            m.Normals.Clear();
            EnsureLength(newNormals, newVerts.Count, new Vector3D(0, 0, 1));
            m.Normals.AddRange(newNormals);
        }
        for (int ch = 0; ch < uvChannelCount; ch++)
        {
            var src = newUvs[ch];
            if (src == null) continue;
            m.TextureCoordinateChannels[ch].Clear();
            EnsureLength(src, newVerts.Count, new Vector3D(0, 0, 0));
            m.TextureCoordinateChannels[ch].AddRange(src);
        }
        for (int ch = 0; ch < colorChannelCount; ch++)
        {
            var src = newColors[ch];
            if (src == null) continue;
            m.VertexColorChannels[ch].Clear();
            EnsureLength(src, newVerts.Count, new Color4D(1, 1, 1, 1));
            m.VertexColorChannels[ch].AddRange(src);
        }
    }

    private static void EnsureLength<T>(System.Collections.Generic.List<T> list, int target, T pad)
    {
        if (list.Count > target) list.RemoveRange(target, list.Count - target);
        while (list.Count < target) list.Add(pad);
    }

    /// <summary>
    /// Identify UV / material seam vertices and add them to the reducer's
    /// constraint set as fixed (non-collapsible) vertices. A "seam vertex"
    /// is one whose XYZ position is shared with at least one other vertex
    /// in the input — that's the textbook symptom of a UV island boundary,
    /// a normal split, or a material change in glTF / FBX, where one
    /// spatial point is duplicated so each side can carry its own
    /// per-vertex attributes.
    ///
    /// Cost: O(n) over input verts, plus a small extra constraint set.
    /// Quality benefit: textures stop smearing across material boundaries.
    /// Reduction-ratio cost: minor — typically only a few percent of verts
    /// sit on seams in well-authored models.
    /// </summary>
    private static void ApplyUvSeamConstraints(Reducer reducer, DMesh3 dmesh)
    {
        // Bucket welded vertex ids by quantized position. Quantization is
        // 1e-5 — tighter than any real-world model precision, looser than
        // float epsilon so genuine duplicates land in the same bucket.
        var buckets = new System.Collections.Generic.Dictionary<(int, int, int),
            System.Collections.Generic.List<int>>(dmesh.VertexCount);
        const double q = 1e5;
        foreach (var vi in dmesh.VertexIndices())
        {
            var v = dmesh.GetVertex(vi);
            var key = ((int)System.Math.Round(v.x * q),
                       (int)System.Math.Round(v.y * q),
                       (int)System.Math.Round(v.z * q));
            if (!buckets.TryGetValue(key, out var list))
            {
                list = new System.Collections.Generic.List<int>(2);
                buckets[key] = list;
            }
            list.Add(vi);
        }

        // Anything in a bucket of size > 1 is a position duplicate — a
        // vertex the attribute weld kept split, i.e. a UV/normal seam.
        // Constrain all of them.
        var constraints = new MeshConstraints();
        var pinned = VertexConstraint.Pinned;
        foreach (var bucket in buckets.Values)
        {
            if (bucket.Count < 2) continue;
            foreach (var vi in bucket)
                constraints.SetOrUpdateVertexConstraint(vi, pinned);
        }
        if (constraints.HasConstraints)
            reducer.SetExternalConstraints(constraints);
    }
}
