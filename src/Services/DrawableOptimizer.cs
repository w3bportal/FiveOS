// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Diagnostics;
using System.IO;
using CodeWalker.GameFiles;
using g3;

namespace FiveOS.Services;

/// <summary>
/// In-place decimator for FiveM/RAGE drawable resources (.ydr / .ydd / .yft).
/// Reads via CodeWalker.Core, runs g3sharp's Reducer on each geometry's
/// triangle list, rebuilds the vertex buffer by inheriting attributes from
/// each surviving vertex's source row (so UVs, normals, blend weights,
/// vertex colours all carry through without us needing to know the exact
/// FVF layout), then saves the resource back as RSC7.
///
/// Caveats baked in:
/// • Only the high-LOD model set is touched. Med/Low/VLow models stay as-is —
///   they're already low-poly and replacing them risks visible LOD pops
///   during streaming.
/// • Skinning is preserved heuristically — every surviving vertex inherits
///   the blend indices/weights of its pre-decimation source row. Quadric
///   edge collapse without bone-aware error metrics still distorts skinning
///   at high reduction ratios, so for clothing (.ydd) targets above ~70%
///   reduction the deformation will look wrong in-game.
/// </summary>
public sealed class DrawableOptimizer
{
    public sealed record Options(
        double TargetRatio,                  // 0.05..0.95 — fraction of input triangles to keep
        bool PreserveBoundary,
        // Embedded-texture pass — runs on each drawable's ShaderGroup.TextureDictionary
        // after the geometry decimate. The TXD is where the bulk of a YDR's
        // "physical memory" footprint lives (4K+ diffuse/normal maps), so
        // decimating geometry alone won't budge the RAGE oversized-asset
        // warning. Defaults match the YTD tab: downsize halves W/H,
        // threshold = W+H >= 4096 (anything 2K+) gets re-encoded.
        bool OptimizeEmbeddedTextures = true,
        bool TextureDownsize = true,
        bool TextureFormatOptimization = false,
        ushort TextureSizeThreshold = 4096,
        // Textures-only pass: skip the geometry decimate entirely and only run
        // the embedded-TXD optimizer. Drives the "Optimize Embedded (Models)"
        // mode, which compresses a drawable's textures without touching its mesh.
        bool TexturesOnly = false,
        // Resolution ceiling for the embedded-texture pass (longest side, px);
        // 0 = no cap (defer to TextureDownsize). Mirrors the YTD tab's MaxSize.
        int TextureMaxSize = 0);

    public sealed record Result(
        string Path,
        long BytesBefore,
        long BytesAfter,
        int TrianglesBefore,
        int TrianglesAfter,
        TimeSpan Elapsed,
        string? Error,
        int TexturesOptimized = 0,
        // Vertex counts across the High models, before/after the weld+decimate.
        // At high reduction the triangle list can be near-unchanged while the
        // vertex buffer still shrinks a lot (coincident verts welded away), so
        // verts surface savings the triangle delta hides.
        int VerticesBefore = 0,
        int VerticesAfter = 0);

    /// <summary>Flattened High-LOD geometry of a drawable, before and after an
    /// in-memory decimation pass, for the dry-run preview. Positions are raw
    /// RAGE coordinates (Z-up) as 3 floats per vertex; indices are 32-bit
    /// (geometries are concatenated, so the vertex count can exceed 65535).
    /// The viewer does the Z-up→Y-up swap when it builds the BufferGeometry.</summary>
    public sealed record MeshPreviewData(
        float[] BeforePositions, int[] BeforeIndices, int BeforeTris,
        float[] AfterPositions,  int[] AfterIndices,  int AfterTris,
        string? Error);

    /// <summary>Decimate a .ydr file. Saves back to <paramref name="outputPath"/>.</summary>
    public Result OptimizeYdr(string inputPath, string outputPath, Options opts, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        var bytesBefore = new FileInfo(inputPath).Length;
        try
        {
            var ydr = LoadResource<YdrFile>(inputPath);
            int trisBefore = 0, trisAfter = 0, vertsBefore = 0, vertsAfter = 0;
            if (!opts.TexturesOnly)
                (trisBefore, trisAfter, vertsBefore, vertsAfter) = DecimateDrawable(ydr.Drawable, opts, log);
            int texOpt = OptimizeEmbeddedTxd(ydr.Drawable, opts, log);
            File.WriteAllBytes(outputPath, ydr.Save());
            sw.Stop();
            return new Result(outputPath, bytesBefore, new FileInfo(outputPath).Length,
                              trisBefore, trisAfter, sw.Elapsed, null, texOpt, vertsBefore, vertsAfter);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new Result(inputPath, bytesBefore, 0, 0, 0, sw.Elapsed, ex.Message);
        }
    }

    /// <summary>Decimate a .ydd file. Iterates every drawable in the dictionary.</summary>
    public Result OptimizeYdd(string inputPath, string outputPath, Options opts, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        var bytesBefore = new FileInfo(inputPath).Length;
        try
        {
            var ydd = LoadResource<YddFile>(inputPath);
            int trisBefore = 0, trisAfter = 0, vertsBefore = 0, vertsAfter = 0, texOpt = 0;
            if (ydd.Drawables != null)
            {
                int i = 0;
                foreach (var d in ydd.Drawables)
                {
                    if (d == null) continue;
                    log?.Invoke($"  drawable {++i}: {d.Name ?? "(unnamed)"}");
                    if (!opts.TexturesOnly)
                    {
                        var (b, a, vb, va) = DecimateDrawable(d, opts, log);
                        trisBefore += b;
                        trisAfter += a;
                        vertsBefore += vb;
                        vertsAfter += va;
                    }
                    texOpt += OptimizeEmbeddedTxd(d, opts, log);
                }
            }
            File.WriteAllBytes(outputPath, ydd.Save());
            sw.Stop();
            return new Result(outputPath, bytesBefore, new FileInfo(outputPath).Length,
                              trisBefore, trisAfter, sw.Elapsed, null, texOpt, vertsBefore, vertsAfter);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new Result(inputPath, bytesBefore, 0, 0, 0, sw.Elapsed, ex.Message);
        }
    }

    /// <summary>Decimate a .yft file (vehicle / fragment). Touches the main
    /// drawable only — damage variants and the physics LOD group are left
    /// alone so the fragment's break-physics still matches.</summary>
    public Result OptimizeYft(string inputPath, string outputPath, Options opts, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        var bytesBefore = new FileInfo(inputPath).Length;
        try
        {
            var yft = LoadResource<YftFile>(inputPath);
            int trisBefore = 0, trisAfter = 0, vertsBefore = 0, vertsAfter = 0, texOpt = 0;
            var main = yft.Fragment?.Drawable;
            if (main != null)
            {
                if (!opts.TexturesOnly)
                {
                    var (b, a, vb, va) = DecimateDrawable(main, opts, log);
                    trisBefore = b; trisAfter = a; vertsBefore = vb; vertsAfter = va;
                }
                texOpt = OptimizeEmbeddedTxd(main, opts, log);
            }
            File.WriteAllBytes(outputPath, yft.Save());
            sw.Stop();
            return new Result(outputPath, bytesBefore, new FileInfo(outputPath).Length,
                              trisBefore, trisAfter, sw.Elapsed, null, texOpt, vertsBefore, vertsAfter);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new Result(inputPath, bytesBefore, 0, 0, 0, sw.Elapsed, ex.Message);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Embedded TextureDictionary pass
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Run the YTD optimizer's per-texture pass over a drawable's embedded
    /// TextureDictionary so the saved YDR/YDD/YFT has a smaller RSC7
    /// physical-memory footprint. No-op when the drawable uses an external
    /// .ytd (ShaderGroup.TextureDictionary is null).
    /// </summary>
    private static int OptimizeEmbeddedTxd(DrawableBase? drawable, Options opts, Action<string>? log)
    {
        if (!opts.OptimizeEmbeddedTextures) return 0;
        var td = drawable?.ShaderGroup?.TextureDictionary;
        if (td?.Textures?.data_items == null || td.Textures.data_items.Length == 0)
            return 0;

        var ytdOpts = new YtdOptimizer.Options(
            DownSize: opts.TextureDownsize,
            FormatOptimization: opts.TextureFormatOptimization,
            OptimizeSizeThreshold: opts.TextureSizeThreshold,
            OnlyOversized: false,
            BackupRoot: null,
            MaxSize: opts.TextureMaxSize);
        int count = YtdOptimizer.OptimizeTextureDictionary(td, ytdOpts);
        if (count > 0) log?.Invoke($"  embedded textures optimized: {count}");
        return count;
    }

    // ────────────────────────────────────────────────────────────────
    // Drawable-level decimation
    // ────────────────────────────────────────────────────────────────

    private static (int TrisBefore, int TrisAfter, int VertsBefore, int VertsAfter) DecimateDrawable(
        DrawableBase drawable, Options opts, Action<string>? log)
    {
        // Only touch High — lower LODs are already low-poly. All of High's
        // geometries are decimated TOGETHER (merged) so panel borders reduce
        // without tearing.
        var highModels = drawable.DrawableModels?.High;
        if (highModels == null) return (0, 0, 0, 0);
        int vBefore = CountVertices(highModels);
        var (b, a) = DecimateModelsMerged(highModels, opts);
        int vAfter = CountVertices(highModels);
        log?.Invoke($"  decimated {b:N0} → {a:N0} tris · {vBefore:N0} → {vAfter:N0} verts");
        return (b, a, vBefore, vAfter);
    }

    /// <summary>Sum the vertex-buffer counts across a model set's geometries.
    /// Reflects post-weld counts when called after a merged decimate, so it
    /// captures vertex savings that the triangle delta alone can hide.</summary>
    private static int CountVertices(DrawableModel[]? models)
    {
        if (models == null) return 0;
        int n = 0;
        foreach (var m in models)
        {
            if (m?.Geometries == null) continue;
            foreach (var g in m.Geometries)
                if (g?.VertexData != null) n += g.VertexData.VertexCount;
        }
        return n;
    }

    /// <summary>
    /// Decimate ALL geometries of a LOD's models TOGETHER as one welded mesh.
    /// RAGE drawables are split into many separate geometries (panels); the
    /// border between two panels is coincident vertices in different geometry
    /// buffers. Decimating each geometry alone leaves those borders as open
    /// mesh boundaries — pinning them (to avoid gaps) blocks nearly all
    /// reduction, and not pinning them tears gaps. Welding every geometry into
    /// one mesh turns panel borders into INTERIOR edges, so the reducer
    /// collapses across them (real reduction) while the panels stay stitched.
    /// Each triangle is tagged with its source geometry (a face group); only
    /// the cross-panel border verts are pinned (so each panel keeps its own
    /// UVs); after one reduce we split the result back per geometry. Mutates
    /// the geometry buffers in place. Returns (beforeTris, afterTris).
    /// </summary>
    internal static (int Before, int After) DecimateModelsMerged(DrawableModel[]? models, Options opts)
    {
        if (models == null) return (0, 0);

        var geoms = new List<DrawableGeometry>();
        foreach (var m in models)
        {
            if (m?.Geometries == null) continue;
            foreach (var g in m.Geometries)
                if (g?.VertexData?.VertexBytes != null && g.IndexBuffer?.Indices != null
                    && g.VertexData.VertexStride > 0 && g.VertexData.VertexCount > 0
                    && g.IndexBuffer.Indices.Length >= 3)
                    geoms.Add(g);
        }
        int gn = geoms.Count;
        if (gn == 0) return (0, 0);

        int totalTris = 0;
        foreach (var g in geoms) totalTris += g.IndexBuffer.Indices.Length / 3;
        if (totalTris < 32) return (totalTris, totalTris);
        int target = (int)Math.Round(totalTris * Math.Clamp(opts.TargetRatio, 0.05, 0.95));
        if (target >= totalTris) return (totalTris, totalTris);

        var strides = new int[gn];
        var raws = new byte[gn][];
        var orig2weld = new int[gn][];
        var gReps = new Dictionary<int, int>[gn];   // welded id -> representative original row in this geom
        for (int gi = 0; gi < gn; gi++)
        {
            var vd = geoms[gi].VertexData;
            strides[gi] = vd.VertexStride;
            raws[gi] = vd.VertexBytes;
            orig2weld[gi] = new int[vd.VertexCount];
            gReps[gi] = new Dictionary<int, int>(vd.VertexCount);
        }

        // Weld all geometries by position into one DMesh3.
        var weldMap = new Dictionary<(int, int, int), int>(totalTris);
        var seamVerts = new HashSet<int>();   // welded ids that are a UV/normal seam WITHIN a single layer
        var dmesh = new DMesh3();
        dmesh.EnableTriangleGroups();
        const double q = 1e5;
        for (int gi = 0; gi < gn; gi++)
        {
            var raw = raws[gi]; int stride = strides[gi]; int vc = geoms[gi].VertexData.VertexCount;
            for (int i = 0; i < vc; i++)
            {
                int o = i * stride;
                float x = BitConverter.ToSingle(raw, o + 0), y = BitConverter.ToSingle(raw, o + 4), z = BitConverter.ToSingle(raw, o + 8);
                var key = ((int)Math.Round(x * q), (int)Math.Round(y * q), (int)Math.Round(z * q));
                if (!weldMap.TryGetValue(key, out var w))
                {
                    w = dmesh.AppendVertex(new Vector3d(x, y, z));
                    weldMap[key] = w;
                }
                orig2weld[gi][i] = w;
                // 2nd+ vertex of THIS geom at the same position = a UV/normal
                // seam within the layer — pin it so the texture isn't smeared
                // by collapsing the two sides of the seam together.
                if (!gReps[gi].TryAdd(w, i)) seamVerts.Add(w);
            }
        }

        // Add triangles tagged by source geometry. g3's DMesh3 is manifold-only;
        // with overlapping panels it refuses lots of triangles (non-manifold).
        // Keep every refused one (per geometry) so no face is lost.
        var droppedTris = new List<(int Gi, int A, int B, int C)>();
        // Track per-group (per-material) boundary edges. An edge used by just
        // one triangle WITHIN a group is on that material region's border; we
        // pin those so a panel's triangles stay inside its own UV region (no
        // cross-material texture smear) while interiors still collapse.
        var edgeSeen = new HashSet<(int, long)>();
        var edgeBnd = new HashSet<(int, long)>();
        void AddEdge(int gi, int u, int v)
        {
            long e = u < v ? ((long)u << 32) | (uint)v : ((long)v << 32) | (uint)u;
            var k = (gi, e);
            if (!edgeSeen.Add(k)) edgeBnd.Remove(k); else edgeBnd.Add(k);
        }
        for (int gi = 0; gi < gn; gi++)
        {
            var idx = geoms[gi].IndexBuffer.Indices;
            int vc = geoms[gi].VertexData.VertexCount;
            int tc = idx.Length / 3;
            for (int t = 0; t < tc; t++)
            {
                int a = idx[t * 3], b = idx[t * 3 + 1], c = idx[t * 3 + 2];
                if (a < 0 || a >= vc || b < 0 || b >= vc || c < 0 || c >= vc) continue;
                if (a == b || b == c || a == c) continue;
                int wa = orig2weld[gi][a], wb = orig2weld[gi][b], wc = orig2weld[gi][c];
                if (wa == wb || wb == wc || wa == wc) continue;
                if (dmesh.AppendTriangle(wa, wb, wc, gi) < 0) { droppedTris.Add((gi, a, b, c)); continue; }
                AddEdge(gi, wa, wb); AddEdge(gi, wb, wc); AddEdge(gi, wc, wa);
            }
        }
        if (dmesh.TriangleCount < 32) return (totalTris, totalTris);

        var groupBnd = new HashSet<int>();
        foreach (var (gi, e) in edgeBnd) { groupBnd.Add((int)(e >> 32)); groupBnd.Add((int)(e & 0xffffffff)); }

        // No pinning — let it collapse fully (the panels overlap, so pinning
        // their shared verts blocked all reduction). Cross-geometry collapses
        // that leave a panel without attributes for a survivor fall back to
        // that panel's own data (rare; a tiny UV smear, never a hole).
        var reducer = new Reducer(dmesh);
        if (opts.PreserveBoundary) reducer.SetProjectionTarget(MeshProjectionTarget.Auto(dmesh));
        if (seamVerts.Count > 0 || groupBnd.Count > 0)
        {
            var cons = new MeshConstraints();
            var pinned = VertexConstraint.Pinned;
            foreach (var w in seamVerts) cons.SetOrUpdateVertexConstraint(w, pinned);
            foreach (var w in groupBnd) cons.SetOrUpdateVertexConstraint(w, pinned);
            if (cons.HasConstraints) reducer.SetExternalConstraints(cons);
        }
        reducer.ReduceToTriangleCount(target);

        // ── Split back to per-geometry buffers ──
        var geomWeldMap = new Dictionary<int, int>[gn];   // welded id  -> out index
        var geomOrigMap = new Dictionary<int, int>[gn];   // original vert -> out index (dropped tris)
        var geomRep = new List<int>[gn];                  // out index -> source original row (attributes)
        var geomWeld = new List<int>[gn];                 // out index -> welded id (-1 = use original position)
        var geomTris = new List<(int, int, int)>[gn];
        for (int gi = 0; gi < gn; gi++)
        {
            geomWeldMap[gi] = new Dictionary<int, int>();
            geomOrigMap[gi] = new Dictionary<int, int>();
            geomRep[gi] = new List<int>();
            geomWeld[gi] = new List<int>();
            geomTris[gi] = new List<(int, int, int)>();
        }

        // Lazy per-geometry spatial hash — used only when a cross-geometry
        // collapse leaves a panel using a welded vertex it has no original for.
        // We then borrow the NEAREST original vertex's attributes in that panel
        // (it sits at essentially the same spot on the same surface), so the UV
        // is right instead of garbage.
        var gHash = new Dictionary<(int, int, int), List<int>>?[gn];
        var gCell = new double[gn];

        void BuildHash(int gi)
        {
            var raw = raws[gi]; int stride = strides[gi]; int vc = geoms[gi].VertexData.VertexCount;
            double mnx = double.MaxValue, mny = double.MaxValue, mnz = double.MaxValue, mxx = double.MinValue, mxy = double.MinValue, mxz = double.MinValue;
            for (int i = 0; i < vc; i++)
            {
                int o = i * stride;
                double x = BitConverter.ToSingle(raw, o), y = BitConverter.ToSingle(raw, o + 4), z = BitConverter.ToSingle(raw, o + 8);
                if (x < mnx) mnx = x; if (x > mxx) mxx = x;
                if (y < mny) mny = y; if (y > mxy) mxy = y;
                if (z < mnz) mnz = z; if (z > mxz) mxz = z;
            }
            double ext = Math.Max(mxx - mnx, Math.Max(mxy - mny, mxz - mnz));
            double cell = Math.Max(ext / Math.Max(1.0, Math.Cbrt(vc)), 1e-4);
            gCell[gi] = cell;
            var hash = new Dictionary<(int, int, int), List<int>>(vc);
            for (int i = 0; i < vc; i++)
            {
                int o = i * stride;
                double x = BitConverter.ToSingle(raw, o), y = BitConverter.ToSingle(raw, o + 4), z = BitConverter.ToSingle(raw, o + 8);
                var k = ((int)Math.Floor(x / cell), (int)Math.Floor(y / cell), (int)Math.Floor(z / cell));
                if (!hash.TryGetValue(k, out var l)) { l = new List<int>(2); hash[k] = l; }
                l.Add(i);
            }
            gHash[gi] = hash;
        }

        int NearestRep(int gi, double px, double py, double pz)
        {
            if (gHash[gi] == null) BuildHash(gi);
            var hash = gHash[gi]!; double cell = gCell[gi]; var raw = raws[gi]; int stride = strides[gi];
            int cx = (int)Math.Floor(px / cell), cy = (int)Math.Floor(py / cell), cz = (int)Math.Floor(pz / cell);
            int best = -1; double bestD = double.MaxValue;
            for (int r = 0; r <= 12; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                        for (int dz = -r; dz <= r; dz++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != r) continue;
                            if (!hash.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                            foreach (var vi in list)
                            {
                                int o = vi * stride;
                                double x = BitConverter.ToSingle(raw, o), y = BitConverter.ToSingle(raw, o + 4), z = BitConverter.ToSingle(raw, o + 8);
                                double d = (x - px) * (x - px) + (y - py) * (y - py) + (z - pz) * (z - pz);
                                if (d < bestD) { bestD = d; best = vi; }
                            }
                        }
                if (best >= 0 && r >= 1) break;   // found + searched one extra ring for safety
            }
            return best >= 0 ? best : 0;
        }

        int OutWeld(int gi, int w)
        {
            var map = geomWeldMap[gi];
            if (map.TryGetValue(w, out var oi)) return oi;
            int rep;
            if (gReps[gi].TryGetValue(w, out var r)) rep = r;
            else { var pv = dmesh.GetVertex(w); rep = NearestRep(gi, pv.x, pv.y, pv.z); }   // nearest UV, not garbage
            oi = geomRep[gi].Count; map[w] = oi;
            geomRep[gi].Add(rep); geomWeld[gi].Add(w);
            return oi;
        }
        int OutOrig(int gi, int ov)
        {
            var map = geomOrigMap[gi];
            if (map.TryGetValue(ov, out var oi)) return oi;
            oi = geomRep[gi].Count; map[ov] = oi;
            geomRep[gi].Add(ov); geomWeld[gi].Add(-1);   // keep original position
            return oi;
        }

        foreach (var tid in dmesh.TriangleIndices())
        {
            int gi = dmesh.GetTriangleGroup(tid);
            if (gi < 0 || gi >= gn) continue;
            var tri = dmesh.GetTriangle(tid);
            int a = OutWeld(gi, tri.a), b = OutWeld(gi, tri.b), c = OutWeld(gi, tri.c);
            if (a == b || b == c || a == c) continue;
            geomTris[gi].Add((a, b, c));
        }
        foreach (var dt in droppedTris)
        {
            int a = OutOrig(dt.Gi, dt.A), b = OutOrig(dt.Gi, dt.B), c = OutOrig(dt.Gi, dt.C);
            geomTris[dt.Gi].Add((a, b, c));
        }

        int afterTotal = 0;
        for (int gi = 0; gi < gn; gi++)
        {
            var tris = geomTris[gi];
            int vcount = geomRep[gi].Count;
            if (tris.Count == 0 || vcount == 0 || vcount > ushort.MaxValue)
            {
                afterTotal += geoms[gi].IndexBuffer.Indices.Length / 3;  // leave this geom unchanged
                continue;
            }
            int stride = strides[gi]; var raw = raws[gi];
            var newVerts = new byte[vcount * stride];
            for (int oi = 0; oi < vcount; oi++)
            {
                int dst = oi * stride;
                Buffer.BlockCopy(raw, geomRep[gi][oi] * stride, newVerts, dst, stride);
                int w = geomWeld[gi][oi];
                if (w >= 0)
                {
                    var pv = dmesh.GetVertex(w);
                    BitConverter.GetBytes((float)pv.x).CopyTo(newVerts, dst + 0);
                    BitConverter.GetBytes((float)pv.y).CopyTo(newVerts, dst + 4);
                    BitConverter.GetBytes((float)pv.z).CopyTo(newVerts, dst + 8);
                }
            }
            var newIdx = new ushort[tris.Count * 3];
            int ti = 0;
            foreach (var tr in tris) { newIdx[ti++] = (ushort)tr.Item1; newIdx[ti++] = (ushort)tr.Item2; newIdx[ti++] = (ushort)tr.Item3; }

            var g = geoms[gi]; var vd2 = g.VertexData; var ib = g.IndexBuffer;
            vd2.VertexBytes = newVerts; vd2.VertexCount = vcount;
            ib.Indices = newIdx; ib.IndicesCount = (uint)newIdx.Length;
            g.VerticesCount = (ushort)Math.Min(vcount, ushort.MaxValue);
            g.VertexStride = (ushort)stride;
            g.IndicesCount = (uint)newIdx.Length;
            g.TrianglesCount = (uint)tris.Count;
            if (g.VertexBuffer != null) { g.VertexBuffer.VertexCount = (uint)vcount; g.VertexBuffer.VertexStride = (ushort)stride; }
            afterTotal += tris.Count;
        }

        FosLogger.Info("decimate-merged",
            $"geoms={gn} totalTri={totalTris} target={target} reduced={dmesh.TriangleCount} after={afterTotal} dropped={droppedTris.Count} seams={seamVerts.Count} grpBnd={groupBnd.Count}");

        return (totalTris, afterTotal);
    }

    internal static (int Before, int After) DecimateGeometry(
        DrawableGeometry geom, Options opts)
    {
        var vd = geom.VertexData;
        var ib = geom.IndexBuffer;
        if (vd == null || ib == null) return (0, 0);

        var stride = vd.VertexStride;
        var vertCount = vd.VertexCount;
        var rawVerts = vd.VertexBytes;
        var indices = ib.Indices;
        if (rawVerts == null || indices == null || stride <= 0 || vertCount <= 0)
            return (0, 0);

        // Position is always the first 12 bytes (3 floats) of every CW
        // drawable vertex layout — that's a fixed RAGE convention.
        var positions = new Vector3d[vertCount];
        for (int i = 0; i < vertCount; i++)
        {
            int o = i * stride;
            positions[i] = new Vector3d(
                BitConverter.ToSingle(rawVerts, o + 0),
                BitConverter.ToSingle(rawVerts, o + 4),
                BitConverter.ToSingle(rawVerts, o + 8));
        }

        var triCount = indices.Length / 3;
        if (triCount < 32) return (triCount, triCount);  // not worth touching tiny geometries

        var target = (int)Math.Round(triCount * Math.Clamp(opts.TargetRatio, 0.05, 0.95));
        if (target >= triCount) return (triCount, triCount);

        // ── Weld coincident vertices into a manifold for the reducer ──
        // RAGE splits a vertex at every UV / normal / material seam (two+
        // vertices at one position). On the split mesh every seam reads as an
        // open boundary and the quadric reducer tears the two sides apart.
        // Welding by position turns seams into shared interior edges so the
        // reducer COLLAPSES and merges (smooths) the surface instead of
        // ripping it.
        var orig2weld = new int[vertCount];
        var weld2orig = new List<int>(vertCount);   // representative original row per welded vertex
        var weldMap = new Dictionary<(int, int, int), int>(vertCount);
        const double q = 1e5;
        var dmesh = new DMesh3();
        for (int i = 0; i < vertCount; i++)
        {
            var p = positions[i];
            var key = ((int)Math.Round(p.x * q), (int)Math.Round(p.y * q), (int)Math.Round(p.z * q));
            if (!weldMap.TryGetValue(key, out var w))
            {
                w = dmesh.AppendVertex(p);   // sequential id == weld2orig.Count
                weldMap[key] = w;
                weld2orig.Add(i);
            }
            orig2weld[i] = w;
        }

        // Add triangles to the manifold mesh. g3's DMesh3 is manifold-only and
        // silently REFUSES non-manifold triangles (RAGE shoe/cloth meshes have
        // plenty — overlapping panels, touching shells). A refused triangle
        // would vanish = a hole. So we capture every reject and keep it
        // intact, un-decimated, in the output — no face is ever lost.
        var droppedTris = new List<(int A, int B, int C)>();
        for (int t = 0; t < triCount; t++)
        {
            int a = indices[t * 3 + 0];
            int b = indices[t * 3 + 1];
            int c = indices[t * 3 + 2];
            if (a < 0 || a >= vertCount || b < 0 || b >= vertCount || c < 0 || c >= vertCount) continue;
            if (a == b || b == c || a == c) continue;            // genuine degenerate sliver
            int wa = orig2weld[a], wb = orig2weld[b], wc = orig2weld[c];
            if (wa == wb || wb == wc || wa == wc) continue;      // collapsed to a sliver by welding
            if (dmesh.AppendTriangle(wa, wb, wc) < 0)
                droppedTris.Add((a, b, c));                       // non-manifold — keep it as-is
        }
        if (dmesh.TriangleCount < 32) return (triCount, triCount);

        var reducer = new Reducer(dmesh);
        if (opts.PreserveBoundary)
            reducer.SetProjectionTarget(MeshProjectionTarget.Auto(dmesh));

        // Pin every OPEN-BOUNDARY vertex (and any non-manifold-triangle vert).
        // RAGE drawables are split into many separate geometries (panels), and
        // each is decimated on its own. A panel's border is shared with its
        // neighbour panel; if we let that border collapse, the two sides drift
        // apart and a gap opens between the panels (the holes you see). Pinning
        // the boundary keeps adjacent panels stitched. It also catches any UV
        // seam that didn't weld (those are boundary edges too) and true garment
        // edges, so the surface stays closed.
        var cons = new MeshConstraints();
        var pinned = VertexConstraint.Pinned;
        int boundaryPins = 0;
        foreach (var w in dmesh.VertexIndices())
            if (dmesh.IsBoundaryVertex(w)) { cons.SetOrUpdateVertexConstraint(w, pinned); boundaryPins++; }
        foreach (var dt in droppedTris)
        {
            cons.SetOrUpdateVertexConstraint(orig2weld[dt.A], pinned);
            cons.SetOrUpdateVertexConstraint(orig2weld[dt.B], pinned);
            cons.SetOrUpdateVertexConstraint(orig2weld[dt.C], pinned);
        }
        if (cons.HasConstraints)
            reducer.SetExternalConstraints(cons);

        reducer.ReduceToTriangleCount(target);

        // ── Rebuild ──
        // Output = reduced manifold tris (welded survivors) + dropped tris
        // (kept verbatim). A welded survivor gets its REDUCED position from the
        // DMesh (the reducer moves vertices to quadric-optimal spots) plus its
        // representative original row's attributes (UV / normal / weights);
        // dropped-tri vertices keep their full original row. Pinned junction
        // verts don't move, so the two regions stay stitched at coincident
        // positions — no crack.
        var weldOut = new Dictionary<int, int>(dmesh.VertexCount);
        var origOut = new Dictionary<int, int>(droppedTris.Count * 3 + 1);
        int outCounter = 0;
        int WeldOutIdx(int w) { if (!weldOut.TryGetValue(w, out var i)) { i = outCounter++; weldOut[w] = i; } return i; }
        int OrigOutIdx(int oi) { if (!origOut.TryGetValue(oi, out var i)) { i = outCounter++; origOut[oi] = i; } return i; }

        var triList = new List<(int A, int B, int C)>(dmesh.TriangleCount + droppedTris.Count);
        foreach (var tIdx in dmesh.TriangleIndices())
        {
            var tri = dmesh.GetTriangle(tIdx);
            int a = WeldOutIdx(tri.a), b = WeldOutIdx(tri.b), c = WeldOutIdx(tri.c);
            if (a == b || b == c || a == c) continue;
            triList.Add((a, b, c));
        }
        foreach (var dt in droppedTris)
        {
            int a = OrigOutIdx(dt.A), b = OrigOutIdx(dt.B), c = OrigOutIdx(dt.C);
            triList.Add((a, b, c));
        }

        int outCount = outCounter;
        if (outCount > ushort.MaxValue || triList.Count == 0)
            return (triCount, triCount);   // too many verts for uint16 → leave this geom full-res

        var newVertBytes = new byte[outCount * stride];
        foreach (var kv in weldOut)
        {
            int dstOff = kv.Value * stride;
            int src = (kv.Key >= 0 && kv.Key < weld2orig.Count) ? weld2orig[kv.Key] : 0;
            Buffer.BlockCopy(rawVerts, src * stride, newVertBytes, dstOff, stride);     // attributes
            var pv = dmesh.GetVertex(kv.Key);                                            // reduced position
            BitConverter.GetBytes((float)pv.x).CopyTo(newVertBytes, dstOff + 0);
            BitConverter.GetBytes((float)pv.y).CopyTo(newVertBytes, dstOff + 4);
            BitConverter.GetBytes((float)pv.z).CopyTo(newVertBytes, dstOff + 8);
        }
        foreach (var kv in origOut)
            Buffer.BlockCopy(rawVerts, kv.Key * stride, newVertBytes, kv.Value * stride, stride);  // full original

        var newIndices = new ushort[triList.Count * 3];
        int ti = 0;
        foreach (var tr in triList)
        {
            newIndices[ti++] = (ushort)tr.A;
            newIndices[ti++] = (ushort)tr.B;
            newIndices[ti++] = (ushort)tr.C;
        }

        FosLogger.Info("decimate",
            $"v={vertCount} weld={weld2orig.Count} bnd={boundaryPins} tri={triCount} dropped={droppedTris.Count} " +
            $"reduced={dmesh.TriangleCount} final={triList.Count} outV={outCount}");

        // Write back into the live buffers. CW's Save() re-packs them on serialise.
        vd.VertexBytes = newVertBytes;
        vd.VertexCount = outCount;
        ib.Indices = newIndices;
        ib.IndicesCount = (uint)newIndices.Length;

        // Mirror the counts onto the geometry-level fields too — RAGE reads
        // these from multiple places and a mismatch will make the mesh fail
        // to render or crash on stream-in.
        geom.VerticesCount = (ushort)Math.Min(outCount, ushort.MaxValue);
        geom.VertexStride  = (ushort)stride;
        geom.IndicesCount  = (uint)newIndices.Length;
        geom.TrianglesCount = (uint)triList.Count;

        return (triCount, triList.Count);
    }

    // ────────────────────────────────────────────────────────────────
    // Dry-run preview — extract High-LOD geometry before/after an
    // in-memory decimate WITHOUT writing anything to disk. Powers the
    // Optimize tab's "Preview" (before↔after) verification.
    // ────────────────────────────────────────────────────────────────

    /// <summary>Build a before/after mesh preview for a .ydr (no disk write).</summary>
    public MeshPreviewData BuildYdrPreview(string inputPath, Options opts)
    {
        try
        {
            var ydr = LoadResource<YdrFile>(inputPath);
            var drawables = new List<DrawableBase>();
            if (ydr.Drawable != null) drawables.Add(ydr.Drawable);
            return BuildDrawablePreview(drawables, opts);
        }
        catch (Exception ex) { return PreviewError(ex.Message); }
    }

    /// <summary>Build a before/after mesh preview for a .ydd — every drawable
    /// in the dictionary is flattened into one combined mesh (no disk write).</summary>
    public MeshPreviewData BuildYddPreview(string inputPath, Options opts)
    {
        try
        {
            var ydd = LoadResource<YddFile>(inputPath);
            var drawables = new List<DrawableBase>();
            if (ydd.Drawables != null)
                foreach (var d in ydd.Drawables)
                    if (d != null) drawables.Add(d);
            return BuildDrawablePreview(drawables, opts);
        }
        catch (Exception ex) { return PreviewError(ex.Message); }
    }

    /// <summary>Build a before/after mesh preview for a .yft's main fragment
    /// drawable (no disk write).</summary>
    public MeshPreviewData BuildYftPreview(string inputPath, Options opts)
    {
        try
        {
            var yft = LoadResource<YftFile>(inputPath);
            var main = yft.Fragment?.Drawable;
            if (main == null) return PreviewError("Fragment has no drawable to preview.");
            return BuildDrawablePreview(new List<DrawableBase> { main }, opts);
        }
        catch (Exception ex) { return PreviewError(ex.Message); }
    }

    /// <summary>Extract before-geometry, run the SAME decimation a real
    /// optimize run would (so the "after" mesh is byte-identical to what
    /// would be written), then extract after-geometry. Mutates the loaded
    /// drawables in memory only — the caller discards them, nothing is saved.</summary>
    private static MeshPreviewData BuildDrawablePreview(List<DrawableBase> drawables, Options opts)
    {
        var beforePos = new List<float>();
        var beforeIdx = new List<int>();
        foreach (var d in drawables) ExtractHighMesh(d, beforePos, beforeIdx);
        if (beforeIdx.Count == 0)
            return PreviewError("No High-LOD geometry to preview in this file.");

        // Reuse the exact in-place decimation (geometry only — no texture pass).
        foreach (var d in drawables) DecimateDrawable(d, opts, null);

        var afterPos = new List<float>();
        var afterIdx = new List<int>();
        foreach (var d in drawables) ExtractHighMesh(d, afterPos, afterIdx);

        return new MeshPreviewData(
            beforePos.ToArray(), beforeIdx.ToArray(), beforeIdx.Count / 3,
            afterPos.ToArray(),  afterIdx.ToArray(),  afterIdx.Count / 3,
            null);
    }

    /// <summary>Append all High-LOD geometries of a drawable into combined
    /// position/index lists (indices offset by the running vertex base so the
    /// concatenated buffer stays self-consistent).</summary>
    private static void ExtractHighMesh(DrawableBase drawable, List<float> pos, List<int> idx)
    {
        var high = drawable?.DrawableModels?.High;
        if (high == null) return;
        foreach (var model in high)
        {
            if (model?.Geometries == null) continue;
            foreach (var geom in model.Geometries)
            {
                if (geom == null) continue;
                ExtractGeometryMesh(geom, pos, idx);
            }
        }
    }

    /// <summary>Read positions (first 12 bytes / vertex — fixed RAGE layout,
    /// same as <see cref="DecimateGeometry"/>) + indices out of one geometry.</summary>
    private static void ExtractGeometryMesh(DrawableGeometry geom, List<float> pos, List<int> idx)
    {
        var vd = geom.VertexData;
        var ib = geom.IndexBuffer;
        if (vd == null || ib == null) return;
        var stride = vd.VertexStride;
        var vertCount = vd.VertexCount;
        var rawVerts = vd.VertexBytes;
        var indices = ib.Indices;
        if (rawVerts == null || indices == null || stride <= 0 || vertCount <= 0) return;

        int vertBase = pos.Count / 3;
        for (int i = 0; i < vertCount; i++)
        {
            int o = i * stride;
            pos.Add(BitConverter.ToSingle(rawVerts, o + 0));
            pos.Add(BitConverter.ToSingle(rawVerts, o + 4));
            pos.Add(BitConverter.ToSingle(rawVerts, o + 8));
        }
        foreach (var ix in indices)
            idx.Add(vertBase + ix);
    }

    private static MeshPreviewData PreviewError(string msg) =>
        new(Array.Empty<float>(), Array.Empty<int>(), 0,
            Array.Empty<float>(), Array.Empty<int>(), 0, msg);

    // ────────────────────────────────────────────────────────────────
    // Resource I/O — same RSC7 pattern as YtdOptimizer
    // ────────────────────────────────────────────────────────────────

    internal static T LoadResource<T>(string path) where T : class, PackedFile, new()
    {
        var data = File.ReadAllBytes(path);
        var name = new FileInfo(path).Name;
        var fe = CreateResourceEntry(name, path, ref data);
        return RpfFile.GetFile<T>(fe, data);
    }

    private static RpfFileEntry CreateResourceEntry(string name, string path, ref byte[] data)
    {
        uint magic = data?.Length > 4 ? BitConverter.ToUInt32(data, 0) : 0;
        RpfFileEntry e;
        if (magic == 0x37435352)  // "RSC7"
        {
            e = RpfFile.CreateResourceFileEntry(ref data!, 0);
            data = ResourceBuilder.Decompress(data);
        }
        else
        {
            var be = new RpfBinaryFileEntry { FileSize = (uint)(data?.Length ?? 0) };
            be.FileUncompressedSize = be.FileSize;
            e = be;
        }
        e.Name = name;
        e.NameLower = name.ToLowerInvariant();
        e.NameHash = JenkHash.GenHash(e.NameLower);
        e.ShortNameHash = JenkHash.GenHash(Path.GetFileNameWithoutExtension(e.NameLower));
        e.Path = path;
        return e;
    }
}
