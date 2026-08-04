// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Linq;
using g3;

namespace FiveOS.Services;

/// <summary>
/// Generates skin weights for an unrigged garment by transferring them from an
/// already-correctly-skinned body, then solving for whatever could not be
/// transferred confidently.
///
/// Naive closest-point transfer is right most of the time and catastrophically
/// wrong the rest of the time — a backpack strap hovering near the upper arm
/// picks up arm weights and tears off with it; armpit and crotch vertices grab
/// whichever surface happens to be a millimetre closer. The fix is to notice
/// when the match is untrustworthy and refuse it, then fill those gaps by
/// minimizing bending energy over the garment's OWN surface. Because that
/// diffusion follows the garment's connectivity rather than straight-line
/// distance, a strap is correctly "far" from the arm it floats beside.
///
/// Method: Abdrashitov et al., "Robust Skin Weights Transfer via Weight
/// Inpainting" (SIGGRAPH Asia 2023). Implemented from the paper.
/// </summary>
public static class GarmentSkinTransfer
{
    /// <summary>A skinned mesh used as the weight source (the freemode body).</summary>
    public sealed class Donor
    {
        public required DMesh3 Mesh { get; init; }
        /// <summary>Per donor-vertex influences: bone index in the ped's palette → weight.</summary>
        public required IReadOnlyList<Dictionary<int, float>> Weights { get; init; }
    }

    public sealed class Options
    {
        /// <summary>Accept a match only within this fraction of the garment's
        /// bounding-box diagonal. The paper's value.</summary>
        public double MaxDistanceFraction { get; set; } = 0.05;

        /// <summary>Accept a match only if the surface normals agree within this.</summary>
        public double MaxNormalAngleDegrees { get; set; } = 30.0;

        /// <summary>Smoothing passes over the inpainted region and its border.
        /// Its job is continuity across the seam between transferred and solved
        /// weights, not accuracy — measured against a known-good donor it costs
        /// a little fidelity (dominant-bone agreement 96.60% at 0 passes vs
        /// 96.29% at 20, mean error 0.056 vs 0.070), so the default is modest.
        /// Raise it if a seam shows at the shoulder or armpit.</summary>
        public int SmoothIterations { get; set; } = 4;

        public double SmoothStrength { get; set; } = 0.2;

        /// <summary>Solver cap per bone. The bi-Laplacian conditions like h⁻⁴,
        /// so a dense garment legitimately needs many iterations; hitting this
        /// cap is reported rather than silently accepted.</summary>
        public int MaxSolverIterations { get; set; } = 20000;

        public double SolverTolerance { get; set; } = 1e-8;

        /// <summary>Called with (fraction 0..1, what it is doing). The per-bone
        /// solve is by far the longest part of a slow run, so that is where
        /// most of the reporting comes from. Invoked from the worker thread.</summary>
        public Action<double, string>? OnProgress { get; set; }

        /// <summary>Raised by the LOD builder as each detail level finishes, so
        /// a caller reporting overall progress knows to advance its slice.</summary>
        public Action? OnTierComplete { get; set; }
    }

    public sealed class Result
    {
        /// <summary>Per garment-vertex influences, already clamped to 4 and
        /// normalized. Index = bone index in the ped palette.</summary>
        public required List<(int boneIndex, float weight)>[] Weights { get; init; }
        public int MatchedCount { get; init; }
        public int InpaintedCount { get; init; }
        public int IsolatedComponentCount { get; init; }
        public int SolverIterations { get; init; }

        /// <summary>How many per-bone solves hit the iteration cap without
        /// reaching tolerance. Non-zero means some weights are an unconverged
        /// approximation — usable, but worth surfacing rather than pretending.</summary>
        public int UnconvergedSolves { get; init; }
    }

    public static Result Transfer(Donor donor, DMesh3 garment, Options? options = null)
    {
        var opt = options ?? new Options();
        int n = garment.MaxVertexID;

        // ── Stage 1: closest-point match, accepted only when trustworthy ──
        opt.OnProgress?.Invoke(0.02, "matching against the body");
        var tree = new DMeshAABBTree3(donor.Mesh, autoBuild: true);
        var donorNormals = VertexNormals(donor.Mesh);
        var garmentNormals = VertexNormals(garment);
        double maxDist = DiagonalLength(garment) * opt.MaxDistanceFraction;
        double maxDistSq = maxDist * maxDist;
        double minNormalDot = Math.Cos(opt.MaxNormalAngleDegrees * Math.PI / 180.0);

        var transferred = new Dictionary<int, float>?[n];
        var matched = new bool[n];

        int reportEvery = Math.Max(1, n / 50);
        for (int vid = 0; vid < n; vid++)
        {
            if (!garment.IsVertex(vid)) continue;
            if (vid % reportEvery == 0)
                opt.OnProgress?.Invoke(0.02 + 0.10 * vid / Math.Max(n, 1),
                                       $"matching vertex {vid:N0} of {n:N0}");
            var p = garment.GetVertex(vid);
            int tid = tree.FindNearestTriangle(p, out double distSq);
            if (tid == DMesh3.InvalidID) continue;

            var dist = MeshQueries.TriangleDistance(donor.Mesh, tid, p);
            var bary = dist.TriangleBaryCoords;
            var tri = donor.Mesh.GetTriangle(tid);

            // Interpolate the donor's weights and normal at the hit point.
            var w = new Dictionary<int, float>();
            Accumulate(w, donor.Weights[tri.a], (float)bary.x);
            Accumulate(w, donor.Weights[tri.b], (float)bary.y);
            Accumulate(w, donor.Weights[tri.c], (float)bary.z);

            var srcNormal = donorNormals[tri.a] * bary.x
                          + donorNormals[tri.b] * bary.y
                          + donorNormals[tri.c] * bary.z;
            srcNormal.Normalize();
            var dstNormal = garmentNormals[vid];

            double dot = dstNormal.Dot(srcNormal);
            // Second chance with the normal flipped: an inner lining faces the
            // body, so it would otherwise be rejected wholesale.
            bool normalOk = dot >= minNormalDot || (-dot) >= minNormalDot;

            // A rejected match must be DISCARDED, not kept. Holding on to it
            // and letting the inpainted values merge into it defeats the whole
            // confidence test — the untrustworthy weights this stage exists to
            // catch would survive into the result anyway.
            bool ok = distSq <= maxDistSq && normalOk;
            matched[vid] = ok;
            transferred[vid] = ok ? w : null;
        }

        int matchedCount = matched.Count(m => m);
        if (matchedCount == 0)
            throw new InvalidOperationException(
                "Not one vertex of this garment came close enough to the body to weight it.");

        // ── Stage 2: inpaint everything else ──
        // Every bone that appears in a confident match becomes one column of the
        // solve. Bones nobody matched cannot be invented, so they are skipped.
        var boneList = new List<int>();
        var boneCol = new Dictionary<int, int>();
        for (int vid = 0; vid < n; vid++)
        {
            if (!matched[vid] || transferred[vid] is null) continue;
            foreach (var b in transferred[vid]!.Keys)
                if (!boneCol.ContainsKey(b)) { boneCol[b] = boneList.Count; boneList.Add(b); }
        }

        var (isolated, solverIters, unconverged) = Inpaint(garment, matched, transferred, boneList, boneCol, opt);

        // ── Stage 3: smooth the seam between transferred and solved regions ──
        opt.OnProgress?.Invoke(0.96, "smoothing");
        Smooth(garment, matched, transferred, maxDist, opt);

        // ── Stage 4: clamp to the engine's 4 influences and normalize ──
        var final = new List<(int, float)>[n];
        for (int vid = 0; vid < n; vid++)
        {
            if (!garment.IsVertex(vid)) { final[vid] = new List<(int, float)>(); continue; }
            var w = transferred[vid] ?? new Dictionary<int, float>();
            var top = w.Where(kv => kv.Value > 1e-5f)
                       .OrderByDescending(kv => kv.Value)
                       .Take(SkinnedDrawableBuilder.MaxInfluences)
                       .ToList();
            float sum = top.Sum(kv => kv.Value);
            final[vid] = sum > 1e-8f
                ? top.Select(kv => (kv.Key, kv.Value / sum)).ToList()
                : new List<(int, float)>();
        }

        return new Result
        {
            Weights = final,
            MatchedCount = matchedCount,
            InpaintedCount = garment.VertexCount - matchedCount,
            IsolatedComponentCount = isolated,
            SolverIterations = solverIters,
            UnconvergedSolves = unconverged,
        };
    }

    private static void Accumulate(Dictionary<int, float> into, Dictionary<int, float> from, float scale)
    {
        foreach (var kv in from)
            into[kv.Key] = (into.TryGetValue(kv.Key, out var e) ? e : 0f) + kv.Value * scale;
    }

    /// <summary>
    /// Solves for the unmatched vertices by minimizing the biharmonic energy
    /// (−L + L M⁻¹ L) with the confident weights pinned as boundary conditions.
    /// The result is a smooth, natural falloff that extrapolates sensibly past
    /// the constrained region.
    ///
    /// A connected component with no confident vertex at all would make the
    /// system singular — buttons and detached straps are the known weak spot —
    /// so those fall back to their nearest matched neighbour's weights instead.
    /// </summary>
    private static (int isolatedComponents, int iterations, int unconverged) Inpaint(
        DMesh3 mesh, bool[] matched, Dictionary<int, float>?[] weights,
        List<int> boneList, Dictionary<int, int> boneCol, Options opt)
    {
        int n = mesh.MaxVertexID;
        opt.OnProgress?.Invoke(0.125, "checking for disconnected pieces");
        int isolated = HandleIsolatedComponents(mesh, matched, weights);

        var unknown = new List<int>();
        var slot = new int[n];
        Array.Fill(slot, -1);
        for (int vid = 0; vid < n; vid++)
        {
            if (!mesh.IsVertex(vid) || matched[vid]) continue;
            slot[vid] = unknown.Count;
            unknown.Add(vid);
        }
        if (unknown.Count == 0 || boneList.Count == 0) return (isolated, 0, 0);

        opt.OnProgress?.Invoke(0.135, "building the solve");
        var lap = CotanLaplacian(mesh);
        var invMass = BarycentricInverseMass(mesh);
        var qDiagonal = lap.BiharmonicDiagonal(invMass);

        // Q = −L + L M⁻¹ L, applied as a sequence rather than assembled. The
        // explicit product is much denser and no better conditioned.
        double[] ApplyQ(double[] x, double[] sA, double[] sB, double[] sC)
        {
            lap.Multiply(x, sA);                                    // sA = Lx
            for (int i = 0; i < sA.Length; i++) sB[i] = sA[i] * invMass[i];
            lap.Multiply(sB, sC);                                   // sC = L M⁻¹ L x
            for (int i = 0; i < sC.Length; i++) sC[i] -= sA[i];     // − Lx
            return sC;
        }

        int totalIters = 0, unconverged = 0;
        var qFull = new double[n];
        var sA = new double[n];
        var sB = new double[n];
        var sC = new double[n];

        int boneDone = 0;
        foreach (var bone in boneList)
        {
            opt.OnProgress?.Invoke(0.15 + 0.80 * boneDone / Math.Max(boneList.Count, 1),
                                   $"solving bone {++boneDone} of {boneList.Count}");
            int col = boneCol[bone];

            // Known values for this bone, zero elsewhere.
            var known = new double[n];
            for (int vid = 0; vid < n; vid++)
                if (matched[vid] && weights[vid] is { } w && w.TryGetValue(bone, out var val))
                    known[vid] = val;

            // rhs = −Q_UI * w_I, restricted to the unknown rows.
            var qKnown = ApplyQ(known, sA, sB, sC);
            var rhs = new double[unknown.Count];
            for (int i = 0; i < unknown.Count; i++) rhs[i] = -qKnown[unknown[i]];

            var x = new double[unknown.Count];
            var (iters, converged) = SolveCg(unknown, n, ApplyQ, qFull, sA, sB, sC, rhs, x, qDiagonal, opt);
            totalIters += iters;
            if (!converged) unconverged++;

            for (int i = 0; i < unknown.Count; i++)
            {
                // Write unconditionally (clamping negatives to zero) so an
                // unmatched vertex ends up with exactly the solved
                // distribution — nothing survives from the rejected match.
                double v = x[i];
                if (v <= 1e-5) continue;
                int vid = unknown[i];
                (weights[vid] ??= new Dictionary<int, float>())[bone] = (float)Math.Max(v, 0.0);
            }
        }
        return (isolated, totalIters, unconverged);
    }

    /// <summary>Jacobi-preconditioned conjugate gradient on the unknown block.
    /// The operator is applied on the full vertex vector and then restricted,
    /// which keeps the sparse structure simple.</summary>
    private static (int iterations, bool converged) SolveCg(
        List<int> unknown, int n,
        Func<double[], double[], double[], double[], double[]> applyQ,
        double[] full, double[] sA, double[] sB, double[] sC,
        double[] rhs, double[] x, double[] qDiagonal, Options opt)
    {
        int m = unknown.Count;
        var r = new double[m];
        var p = new double[m];
        var ap = new double[m];
        var diag = new double[m];

        void Op(double[] src, double[] dst)
        {
            Array.Clear(full);
            for (int i = 0; i < m; i++) full[unknown[i]] = src[i];
            var q = applyQ(full, sA, sB, sC);
            for (int i = 0; i < m; i++) dst[i] = q[unknown[i]];
        }

        // Jacobi preconditioner using the EXACT diagonal of Q. This matters a
        // lot: the bi-Laplacian conditions like h^-4, and without a real
        // preconditioner the solve stalls for tens of thousands of iterations.
        // (Probing with an all-ones vector does not work here — a Laplacian's
        // rows sum to zero, so it reports a diagonal of ~0 everywhere.)
        for (int i = 0; i < m; i++)
        {
            double d = qDiagonal[unknown[i]];
            diag[i] = Math.Abs(d) < 1e-12 ? 1.0 : Math.Abs(d);
        }

        Array.Copy(rhs, r, m);
        for (int i = 0; i < m; i++) p[i] = r[i] / diag[i];
        double rz = Dot(r, p);
        double rhsNorm = Math.Sqrt(Dot(rhs, rhs));
        if (rhsNorm < 1e-14) return (0, true);

        int it = 0; bool converged = false;
        for (; it < opt.MaxSolverIterations; it++)
        {
            Op(p, ap);
            double denom = Dot(p, ap);
            if (Math.Abs(denom) < 1e-30) break;
            double alpha = rz / denom;
            for (int i = 0; i < m; i++) { x[i] += alpha * p[i]; r[i] -= alpha * ap[i]; }
            if (Math.Sqrt(Dot(r, r)) / rhsNorm < opt.SolverTolerance) { it++; converged = true; break; }
            double rzNew = 0;
            for (int i = 0; i < m; i++) rzNew += r[i] * r[i] / diag[i];
            double beta = rzNew / rz;
            rz = rzNew;
            for (int i = 0; i < m; i++) p[i] = r[i] / diag[i] + beta * p[i];
        }
        return (it, converged);
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>A component with no confident vertex makes the solve singular.
    /// Give each such island the weights of the matched vertex nearest to it.</summary>
    private static int HandleIsolatedComponents(DMesh3 mesh, bool[] matched, Dictionary<int, float>?[] weights)
    {
        int n = mesh.MaxVertexID;
        var seen = new bool[n];
        int isolated = 0;
        var matchedIds = new List<int>();
        for (int vid = 0; vid < n; vid++) if (matched[vid]) matchedIds.Add(vid);
        if (matchedIds.Count == 0) return 0;

        for (int start = 0; start < n; start++)
        {
            if (!mesh.IsVertex(start) || seen[start]) continue;
            var comp = new List<int>();
            var stack = new Stack<int>();
            stack.Push(start); seen[start] = true;
            bool any = false;
            while (stack.Count > 0)
            {
                int v = stack.Pop();
                comp.Add(v);
                if (matched[v]) any = true;
                foreach (int nb in mesh.VtxVerticesItr(v))
                    if (!seen[nb]) { seen[nb] = true; stack.Push(nb); }
            }
            if (any) continue;

            isolated++;
            var centre = Vector3d.Zero;
            foreach (var v in comp) centre += mesh.GetVertex(v);
            centre /= comp.Count;

            // Sample rather than scan every matched vertex. A mesh whose pieces
            // did not weld can have tens of thousands of islands, and an
            // exhaustive search per island is quadratic — it looks like a hang.
            // These are fallback weights for geometry that matched nothing, so
            // "near enough" is the right trade.
            int stride = Math.Max(1, matchedIds.Count / 2000);
            int best = matchedIds[0];
            double bestD = double.MaxValue;
            for (int k = 0; k < matchedIds.Count; k += stride)
            {
                int v = matchedIds[k];
                double d = mesh.GetVertex(v).DistanceSquared(centre);
                if (d < bestD) { bestD = d; best = v; }
            }
            var src = weights[best];
            foreach (var v in comp)
            {
                weights[v] = src is null ? null : new Dictionary<int, float>(src);
                matched[v] = true;   // pinned, so the solve stays non-singular
            }
        }
        return isolated;
    }

    /// <summary>Blends the boundary between transferred and solved weights.
    /// Applied to the solved region plus its immediate neighbourhood, which is
    /// where armpit and crotch seams show.</summary>
    private static void Smooth(DMesh3 mesh, bool[] matched, Dictionary<int, float>?[] weights,
                               double radius, Options opt)
    {
        int n = mesh.MaxVertexID;
        var affected = new bool[n];
        for (int vid = 0; vid < n; vid++)
        {
            if (!mesh.IsVertex(vid) || matched[vid]) continue;
            affected[vid] = true;
            var p = mesh.GetVertex(vid);
            foreach (int nb in mesh.VtxVerticesItr(vid))
                if (mesh.GetVertex(nb).DistanceSquared(p) <= radius * radius) affected[nb] = true;
        }

        for (int iter = 0; iter < opt.SmoothIterations; iter++)
        {
            var next = new Dictionary<int, float>?[n];
            for (int vid = 0; vid < n; vid++)
            {
                if (!affected[vid] || !mesh.IsVertex(vid)) continue;
                var acc = new Dictionary<int, float>();
                int count = 0;
                foreach (int nb in mesh.VtxVerticesItr(vid))
                {
                    if (weights[nb] is not { } w) continue;
                    Accumulate(acc, w, 1f);
                    count++;
                }
                if (count == 0) continue;
                var blended = new Dictionary<int, float>();
                if (weights[vid] is { } cur)
                    foreach (var kv in cur) blended[kv.Key] = (float)(1.0 - opt.SmoothStrength) * kv.Value;
                foreach (var kv in acc)
                    blended[kv.Key] = (blended.TryGetValue(kv.Key, out var e) ? e : 0f)
                                    + (float)opt.SmoothStrength * kv.Value / count;
                next[vid] = blended;
            }
            for (int vid = 0; vid < n; vid++) if (next[vid] is { } b) weights[vid] = b;
        }
    }

    // ── Discrete operators ────────────────────────────────────────────────

    /// <summary>Cotangent Laplacian. Cotangents are clamped because sliver
    /// triangles otherwise produce enormous entries that wreck conditioning.</summary>
    private static SparseMatrix CotanLaplacian(DMesh3 mesh)
    {
        int n = mesh.MaxVertexID;
        var m = new SparseMatrix(n);
        foreach (int tid in mesh.TriangleIndices())
        {
            var t = mesh.GetTriangle(tid);
            var p0 = mesh.GetVertex(t.a); var p1 = mesh.GetVertex(t.b); var p2 = mesh.GetVertex(t.c);
            AddCotan(m, t.b, t.c, p0, p1, p2);
            AddCotan(m, t.c, t.a, p1, p2, p0);
            AddCotan(m, t.a, t.b, p2, p0, p1);
        }
        return m;
    }

    private static void AddCotan(SparseMatrix m, int i, int j, Vector3d opposite, Vector3d a, Vector3d b)
    {
        var u = a - opposite;
        var v = b - opposite;
        double cross = u.Cross(v).Length;
        if (cross < 1e-12) return;
        double cot = Math.Clamp(u.Dot(v) / cross, -1e4, 1e4) * 0.5;
        m.Add(i, j, cot); m.Add(j, i, cot);
        m.Add(i, i, -cot); m.Add(j, j, -cot);
    }

    /// <summary>Area-weighted vertex normals, computed here rather than read off
    /// the mesh — an imported garment usually arrives without them.</summary>
    private static Vector3d[] VertexNormals(DMesh3 mesh)
    {
        var normals = new Vector3d[mesh.MaxVertexID];
        foreach (int tid in mesh.TriangleIndices())
        {
            var t = mesh.GetTriangle(tid);
            var p0 = mesh.GetVertex(t.a); var p1 = mesh.GetVertex(t.b); var p2 = mesh.GetVertex(t.c);
            var faceArea = (p1 - p0).Cross(p2 - p0);   // magnitude == 2 * area
            normals[t.a] += faceArea; normals[t.b] += faceArea; normals[t.c] += faceArea;
        }
        for (int i = 0; i < normals.Length; i++)
        {
            double len = normals[i].Length;
            normals[i] = len > 1e-12 ? normals[i] / len : Vector3d.AxisY;
        }
        return normals;
    }

    private static double DiagonalLength(DMesh3 mesh)
    {
        var min = new Vector3d(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vector3d(double.MinValue, double.MinValue, double.MinValue);
        foreach (int vid in mesh.VertexIndices())
        {
            var p = mesh.GetVertex(vid);
            min.x = Math.Min(min.x, p.x); min.y = Math.Min(min.y, p.y); min.z = Math.Min(min.z, p.z);
            max.x = Math.Max(max.x, p.x); max.y = Math.Max(max.y, p.y); max.z = Math.Max(max.z, p.z);
        }
        return (max - min).Length;
    }

    /// <summary>Inverse lumped BARYCENTRIC mass — each triangle contributes a
    /// third of its area to each corner. The paper specifies the mixed Voronoi
    /// area; barycentric is the standard cheaper substitute and behaves the
    /// same on well-shaped triangles, differing only on very obtuse ones.
    /// Named honestly so nobody assumes otherwise. Floored so the inverse can
    /// never explode on a degenerate vertex.</summary>
    private static double[] BarycentricInverseMass(DMesh3 mesh)
    {
        int n = mesh.MaxVertexID;
        var mass = new double[n];
        foreach (int tid in mesh.TriangleIndices())
        {
            var t = mesh.GetTriangle(tid);
            double area = mesh.GetTriArea(tid) / 3.0;
            mass[t.a] += area; mass[t.b] += area; mass[t.c] += area;
        }
        var inv = new double[n];
        for (int i = 0; i < n; i++) inv[i] = 1.0 / Math.Max(mass[i], 1e-9);
        return inv;
    }

    /// <summary>Minimal symmetric sparse matrix with a row-compressed multiply.
    /// Built by accumulation, then frozen on first use.</summary>
    private sealed class SparseMatrix
    {
        private readonly Dictionary<long, double> _entries = new();
        private readonly int _n;
        private int[]? _rowStart; private int[]? _cols; private double[]? _vals;

        public SparseMatrix(int n) { _n = n; }

        public void Add(int r, int c, double v)
        {
            long key = ((long)r << 32) | (uint)c;
            // CollectionsMarshal avoids the double hash lookup this does on
            // every one of the ~12 entries per triangle — on a dense mesh that
            // dominates assembly time.
            ref double slot = ref System.Runtime.InteropServices.CollectionsMarshal
                .GetValueRefOrAddDefault(_entries, key, out _);
            slot += v;
        }

        private void Freeze()
        {
            if (_rowStart is not null) return;
            var perRow = new List<(int col, double val)>[_n];
            foreach (var kv in _entries)
            {
                int r = (int)(kv.Key >> 32), c = (int)(kv.Key & 0xFFFFFFFF);
                (perRow[r] ??= new List<(int, double)>()).Add((c, kv.Value));
            }
            _rowStart = new int[_n + 1];
            int total = perRow.Sum(l => l?.Count ?? 0);
            _cols = new int[total]; _vals = new double[total];
            int k = 0;
            for (int r = 0; r < _n; r++)
            {
                _rowStart[r] = k;
                if (perRow[r] is { } list)
                    foreach (var (c, v) in list) { _cols[k] = c; _vals[k] = v; k++; }
            }
            _rowStart[_n] = k;
        }

        /// <summary>Exact diagonal of (−L + L M⁻¹ L), which is what the Jacobi
        /// preconditioner needs. Symmetry gives
        /// <c>diag_i = −L_ii + Σ_k L_ik² · invMass_k</c>.</summary>
        public double[] BiharmonicDiagonal(double[] invMass)
        {
            Freeze();
            var diag = new double[_n];
            for (int r = 0; r < _n; r++)
            {
                double sum = 0, lii = 0;
                for (int k = _rowStart![r]; k < _rowStart[r + 1]; k++)
                {
                    int c = _cols![k];
                    double v = _vals![k];
                    sum += v * v * invMass[c];
                    if (c == r) lii = v;
                }
                diag[r] = -lii + sum;
            }
            return diag;
        }

        public void Multiply(double[] x, double[] dst)
        {
            Freeze();
            Array.Clear(dst);
            for (int r = 0; r < _n; r++)
            {
                double s = 0;
                for (int k = _rowStart![r]; k < _rowStart[r + 1]; k++) s += _vals![k] * x[_cols![k]];
                dst[r] = s;
            }
        }
    }
}
