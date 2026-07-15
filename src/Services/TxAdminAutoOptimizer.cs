// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FiveOS.Services;

/// <summary>
/// Drives FiveOS's existing optimizers (<see cref="YtdOptimizer"/> /
/// <see cref="DrawableOptimizer"/>) to bring a SINGLE asset back under the
/// FiveM streaming-warning threshold with the minimum quality loss needed —
/// the "just enough" strategy behind the txAdmin tab.
///
/// For each asset it walks a ladder of increasingly-aggressive settings,
/// gentlest first, re-measuring the RSC7 physical/virtual size after every
/// step and stopping the moment the asset drops under the target. Crucially it
/// restores the pristine original between steps, so the chosen level only ever
/// halves the source ONCE (no compounding blur from stacked passes).
///
/// FiveM warns above 16 MiB and appends the scary "Oversized assets … WILL
/// lead to streaming issues" sentence above 48 MiB
/// (<see cref="MeshThresholds.PhysicalMemWarnBytes"/>). "Just enough" / "Fully
/// clear" aim under 16; "Severe only" just needs under 48.
/// </summary>
public sealed class TxAdminAutoOptimizer
{
    public enum Aggressiveness { JustEnough, FullyClear, SevereOnly }

    /// <param name="Auto">When true, escalate along the ladder until the target
    /// is met. When false, apply exactly one pass using the Manual* knobs.</param>
    public sealed record Plan(
        bool Auto,
        Aggressiveness Level,
        ushort ManualThreshold,
        bool ManualDownsize,
        bool ManualFormatOpt,
        double ManualKeepRatio);

    public sealed record Outcome(
        float BeforeMiB,
        float AfterMiB,
        bool Cleared,
        bool Changed,
        int TexturesOptimized,
        int TrianglesBefore,
        int TrianglesAfter,
        string Detail,
        string? Error);

    private const float WarnMiB = 16f;
    private const float SevereMiB = 48f;

    private readonly YtdOptimizer _ytd = new();
    private readonly DrawableOptimizer _drawable = new();

    /// <summary>Optimize one resolved asset in place. The caller is expected to
    /// have already backed the file up.</summary>
    public Outcome Optimize(string path, string ext, AssetMemKind memKind, Plan plan, CancellationToken cancel = default)
    {
        ext = ext.ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".ytd" => OptimizeYtd(path, plan, cancel),
                ".ydr" or ".ydd" or ".yft" => OptimizeDrawable(path, ext, memKind, plan, cancel),
                _ => Fail($"FiveOS has no optimizer for {ext} files"),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static Outcome Fail(string error) =>
        new(0, 0, false, false, 0, 0, 0, "error", error);

    private static float TargetFor(Plan plan) =>
        plan.Auto && plan.Level == Aggressiveness.SevereOnly ? SevereMiB : WarnMiB;

    // ─── YTD (texture dictionary, physical memory) ──────────────────────

    private Outcome OptimizeYtd(string path, Plan plan, CancellationToken cancel)
    {
        var (_, beforePhys) = YtdOptimizer.ReadRsc7Sizes(path);
        float target = TargetFor(plan);

        if (beforePhys > 0 && beforePhys <= target)
            return new Outcome(beforePhys, beforePhys, true, false, 0, 0, 0, "already under target", null);

        var original = File.ReadAllBytes(path);
        var tempDir = NewTempDir();
        var tempFile = Path.Combine(tempDir, Path.GetFileName(path));
        try
        {
            // Threshold = (texture W+H) trigger: higher touches only the biggest
            // textures, lower reaches smaller ones. JustEnough/SevereOnly stop
            // at 2048 (1024px → 512px) so 512px-and-smaller textures stay sharp;
            // FullyClear is allowed to push 256px-and-up down too.
            ushort[] ladder = plan.Auto
                ? plan.Level == Aggressiveness.FullyClear
                    ? new ushort[] { 8192, 4096, 2048, 1024, 512 }
                    : new ushort[] { 8192, 4096, 2048 }
                : new[] { plan.ManualThreshold };

            byte[]? bestBytes = null;
            float bestPhys = beforePhys;
            int bestTex = 0;
            ushort bestThreshold = 0;
            bool cleared = false;

            foreach (var threshold in ladder)
            {
                cancel.ThrowIfCancellationRequested();
                File.WriteAllBytes(tempFile, original);   // pristine each step

                var opts = new YtdOptimizer.Options(
                    DownSize: plan.Auto || plan.ManualDownsize,
                    FormatOptimization: !plan.Auto && plan.ManualFormatOpt,
                    OptimizeSizeThreshold: threshold,
                    OnlyOversized: false,
                    BackupRoot: null);

                var r = _ytd.Optimize(tempDir, opts, onFile: null, progress: null, cancel: cancel)
                    .FirstOrDefault();
                if (r == null) break;
                if (r.Error != null) return new Outcome(beforePhys, beforePhys, false, false, 0, 0, 0, "ytd error", r.Error);

                if (bestBytes == null || r.PhysicalMbAfter < bestPhys)
                {
                    bestBytes = File.ReadAllBytes(tempFile);
                    bestPhys = r.PhysicalMbAfter;
                    bestTex = r.TexturesOptimized;
                    bestThreshold = threshold;
                }

                if (r.PhysicalMbAfter <= target) { cleared = true; break; }
                if (!plan.Auto) break;
            }

            // Nothing actually shrank within the chosen quality cap.
            bool improved = bestBytes != null && bestPhys < beforePhys - 0.05f;
            if (!improved)
                return new Outcome(beforePhys, beforePhys, false, false, 0, 0, 0,
                    "no textures above the quality cap — try Manual or Fully-clear", null);

            File.WriteAllBytes(path, bestBytes!);
            var detail = cleared
                ? $"downscaled {PxLabel(bestThreshold)} · {bestTex} tex → {bestPhys:F1} MiB"
                : $"downscaled {PxLabel(bestThreshold)} · {bestTex} tex → {bestPhys:F1} MiB (still > {target:F0}, needs Manual)";
            return new Outcome(beforePhys, bestPhys, cleared, true, bestTex, 0, 0, detail, null);
        }
        finally { TryDeleteDir(tempDir); }
    }

    // ─── Drawable (.ydr/.ydd/.yft) ──────────────────────────────────────

    private Outcome OptimizeDrawable(string path, string ext, AssetMemKind memKind, Plan plan, CancellationToken cancel)
    {
        var (beforeVirt, beforePhys) = YtdOptimizer.ReadRsc7Sizes(path);
        float target = TargetFor(plan);
        bool fixingPhysical = memKind == AssetMemKind.Physical;
        float before = fixingPhysical ? beforePhys : beforeVirt;

        if (before > 0 && before <= target)
            return new Outcome(before, before, true, false, 0, 0, 0, "already under target", null);

        var tempDir = NewTempDir();
        var origCopy = Path.Combine(tempDir, "orig" + ext);
        var tempOut = Path.Combine(tempDir, "out" + ext);
        File.Copy(path, origCopy, overwrite: true);
        try
        {
            byte[]? bestBytes = null;
            float bestSize = before;
            int trisBefore = 0, trisAfter = 0, texCount = 0;
            string label = "";
            bool cleared = false;

            if (fixingPhysical)
            {
                // Physical footprint on a drawable lives in its embedded texture
                // dictionary — escalate the embedded-TXD threshold and keep
                // (nearly) all geometry. The decimator always trims ≥5%, which
                // is visually negligible.
                ushort[] texLadder = plan.Auto
                    ? plan.Level == Aggressiveness.FullyClear
                        ? new ushort[] { 8192, 4096, 2048, 1024 }
                        : new ushort[] { 8192, 4096, 2048 }
                    : new[] { plan.ManualThreshold };

                foreach (var t in texLadder)
                {
                    cancel.ThrowIfCancellationRequested();
                    var opts = new DrawableOptimizer.Options(
                        TargetRatio: 1.0,
                        PreserveBoundary: true,
                        OptimizeEmbeddedTextures: true,
                        TextureDownsize: true,
                        TextureFormatOptimization: false,
                        TextureSizeThreshold: t,
                        TexturesOnly: true);   // leave geometry untouched — only the embedded TXD
                    var r = RunDrawable(ext, origCopy, tempOut, opts);
                    if (r.Error != null) return new Outcome(before, before, false, false, 0, 0, 0, "drawable error", r.Error);

                    var (_, ph) = YtdOptimizer.ReadRsc7Sizes(tempOut);
                    if (bestBytes == null || ph < bestSize)
                    {
                        bestBytes = File.ReadAllBytes(tempOut);
                        bestSize = ph; trisBefore = r.TrianglesBefore; trisAfter = r.TrianglesAfter;
                        texCount = r.TexturesOptimized; label = "embedded " + PxLabel(t);
                    }
                    if (ph <= target) { cleared = true; break; }
                    if (!plan.Auto) break;
                }
            }
            else
            {
                // Virtual footprint = geometry. Escalate decimation; leave the
                // textures untouched. JustEnough/SevereOnly floor at 55% kept.
                double[] ratios = plan.Auto
                    ? plan.Level == Aggressiveness.FullyClear
                        ? new[] { 0.85, 0.7, 0.55, 0.4, 0.3 }
                        : new[] { 0.85, 0.7, 0.55 }
                    : new[] { plan.ManualKeepRatio };

                foreach (var ratio in ratios)
                {
                    cancel.ThrowIfCancellationRequested();
                    var opts = new DrawableOptimizer.Options(
                        TargetRatio: ratio,
                        PreserveBoundary: true,
                        OptimizeEmbeddedTextures: false);
                    var r = RunDrawable(ext, origCopy, tempOut, opts);
                    if (r.Error != null) return new Outcome(before, before, false, false, 0, 0, 0, "drawable error", r.Error);

                    var (vt, _) = YtdOptimizer.ReadRsc7Sizes(tempOut);
                    if (bestBytes == null || vt < bestSize)
                    {
                        bestBytes = File.ReadAllBytes(tempOut);
                        bestSize = vt; trisBefore = r.TrianglesBefore; trisAfter = r.TrianglesAfter;
                        label = $"{ratio * 100:F0}% tris";
                    }
                    if (vt <= target) { cleared = true; break; }
                    if (!plan.Auto) break;
                }
            }

            bool improved = bestBytes != null && bestSize < before - 0.05f;
            if (!improved)
                return new Outcome(before, before, false, false, 0, 0, 0,
                    "couldn't reduce within the quality cap — try Manual or Fully-clear", null);

            File.WriteAllBytes(path, bestBytes!);
            var axis = fixingPhysical ? "embedded textures" : "geometry";
            var detail = cleared
                ? $"{axis} {label} → {bestSize:F1} MiB"
                : $"{axis} {label} → {bestSize:F1} MiB (still > {target:F0}, needs Manual)";
            return new Outcome(before, bestSize, cleared, true, texCount, trisBefore, trisAfter, detail, null);
        }
        finally { TryDeleteDir(tempDir); }
    }

    private DrawableOptimizer.Result RunDrawable(string ext, string input, string output, DrawableOptimizer.Options opts) => ext switch
    {
        ".ydr" => _drawable.OptimizeYdr(input, output, opts),
        ".ydd" => _drawable.OptimizeYdd(input, output, opts),
        ".yft" => _drawable.OptimizeYft(input, output, opts),
        _ => throw new InvalidOperationException($"not a drawable: {ext}"),
    };

    // ─── Plumbing ───────────────────────────────────────────────────────

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FiveOS", "txadmin-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }

    /// <summary>Friendly label for a YTD W+H threshold — the smallest square
    /// texture it touches.</summary>
    private static string PxLabel(ushort threshold) => threshold switch
    {
        8192 => "4K+",
        4096 => "2K+",
        2048 => "1K+",
        1024 => "512px+",
        512 => "256px+",
        _ => $"W+H≥{threshold}",
    };
}
