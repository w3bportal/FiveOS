// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Assimp;
using Quaternion = System.Numerics.Quaternion;

namespace FiveOS.Services;

/// <summary>
/// Animation → Emote: read a .glb/.gltf/.fbx/.dae/.bvh that carries a baked
/// animation clip, resample its per-bone rotation tracks at a fixed FPS,
/// map bone names to RAGE tags via <see cref="GtaBoneTags"/>, and hand the
/// tracks to <see cref="YcdAnimationBuilder"/> for .ycd baking.
///
/// .bvh mocap (e.g. the CMU library retargeted onto a Character Creator
/// CC_Base_* rig) flows through the FOREIGN-rig retarget path unchanged:
/// Assimp reports a real TicksPerSecond for BVH (~60), so the .glb/.gltf
/// millisecond-clock override below deliberately does NOT apply to it.
///
/// The correctness rule this implements (verified against how Sollumz /
/// AnimKit workflows and retargeting engines behave):
/// • Clips authored ON the GTA skeleton (SKEL_* bone names — what a
///   Blender+Sollumz export or a FiveOS pose session produces) can be copied
///   rotation-for-rotation. Local rotations are already in GTA bone space.
/// • Clips on a FOREIGN rig (Mixamo's mixamorig:*, generic Hips/Spine rigs)
///   must NOT be raw-copied — rotations are relative to that rig's bind
///   pose, which differs from GTA's, and raw copy twists the limbs. For
///   those we apply a per-bone local bind-delta retarget:
///       q_out = bind_gta_local * inverse(bind_src_local) * q_src(t)
///   using the shipped freemode reference skeleton for the GTA bind. This
///   is the standard simplified local-space retarget — good for matching
///   humanoids, imperfect for fingers/twist bones; surfaced as a warning.
/// </summary>
public sealed class AnimEmoteImporter
{
    public enum RigKind { GtaRig, Mixamo, Generic }

    public sealed record Result(
        bool Success,
        string ClipName,
        int Frames,
        int Fps,
        double DurationSeconds,
        RigKind Rig,
        bool Retargeted,
        IReadOnlyList<PosedBoneTrack> Tracks,
        IReadOnlyList<string> MappedBones,
        IReadOnlyList<string> UnmappedBones,
        IReadOnlyList<string> Warnings,
        string? Error,
        // Per-frame SKEL_ROOT ground travel (GTA units), empty if none / not extracted.
        IReadOnlyList<System.Numerics.Vector3> RootMotion)
    {
        public static Result Fail(string error) => new(
            false, "", 0, 0, 0, RigKind.Generic, false,
            System.Array.Empty<PosedBoneTrack>(), System.Array.Empty<string>(),
            System.Array.Empty<string>(), System.Array.Empty<string>(), error,
            System.Array.Empty<System.Numerics.Vector3>());
    }

    /// <summary>Import the FIRST animation clip in the file. FPS defaults to 30
    /// but is raised to match baked source density (Cascadeur often exports 60).</summary>
    public Result Import(string path, int fps = 30)
    {
        var warnings = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return Result.Fail($"File not found: {path}");

            using var ctx = new AssimpContext();
            // FBX exporters (Mixamo especially) split every bone's transform
            // across helper "_$AssimpFbx$_" pivot nodes. Left in place, each
            // animation channel carries only a FRAGMENT of the bone's motion
            // and the named bone node has an identity bind — which contorts
            // the retarget completely. Collapsing pivots bakes the full local
            // transform back onto the real bone node (bind pose) and into a
            // single channel per bone. (Harmless for glTF, which has none.)
            bool preservePivots = System.Environment.GetEnvironmentVariable("FIVEOS_PIVOT_PRESERVE") == "1";
            ctx.SetConfig(new Assimp.Configs.FBXPreservePivotsConfig(preservePivots));
            var scene = ctx.ImportFile(path, PostProcessSteps.None);
            if (scene is null || !scene.HasAnimations || scene.AnimationCount == 0)
                return Result.Fail("No animation clip found in this file. Export the model WITH its animation baked (e.g. Mixamo 'with skin', or Blender glTF with animations enabled).");

            var anim = scene.Animations[0];
            if (anim.NodeAnimationChannelCount == 0)
                return Result.Fail("The clip has no bone channels.");
            if (scene.AnimationCount > 1)
                warnings.Add($"File contains {scene.AnimationCount} clips — only the first ('{anim.Name}') is converted.");

            double tps = anim.TicksPerSecond > 0 ? anim.TicksPerSecond : 25.0;

            // Assimp's glTF/GLB importer emits animation key times in
            // MILLISECONDS but reports a bogus TicksPerSecond (e.g. 15.5),
            // which makes a 2 s clip read as ~129 s (and then play ~60x too
            // slow). glTF sampler input is spec'd in seconds and Assimp's ms
            // scaling is consistent, so force the ms clock for these formats.
            // (FBX carries a real TicksPerSecond, so it's left untouched.)
            if (Path.GetExtension(path).ToLowerInvariant() is ".glb" or ".gltf")
                tps = 1000.0;

            // Duration: trust the actual key range over the header (importers
            // fudge DurationInTicks), and sanity-check the time unit — Assimp's
            // glTF reader scales key times to MILLISECONDS while reporting a
            // bogus TicksPerSecond, which reads a 1 s clip as ~500 s. If the
            // naive duration is absurd but a ms interpretation is sane, use ms.
            double maxKeyTicks = 0;
            foreach (var c in anim.NodeAnimationChannels)
                if (c?.RotationKeyCount > 0)
                    maxKeyTicks = System.Math.Max(maxKeyTicks, c.RotationKeys[^1].Time);
            double durTicks = System.Math.Max(anim.DurationInTicks, maxKeyTicks);
            if (durTicks <= 0) durTicks = tps; // 1 second fallback

            double durationSec = durTicks / tps;
            if (durationSec > 150.0 && durTicks / 1000.0 <= 150.0)
            {
                tps = 1000.0;
                durationSec = durTicks / tps;
                warnings.Add("Importer reported an implausible clip length — key times were interpreted as milliseconds (common with .glb/.gltf).");
            }
            if (durationSec > 120.0)
            {
                warnings.Add($"Clip is {durationSec:F0}s — truncated to 120s (emote-length cap).");
                durationSec = 120.0;
            }

            if (int.TryParse(Environment.GetEnvironmentVariable("FIVEOS_IMPORT_FPS"), out var envFps) && envFps > 0)
                fps = Math.Clamp(envFps, 1, 120);
            else
            {
                int inferred = InferImportFps(anim, durationSec, fps);
                if (inferred != fps)
                    warnings.Add($"Clip key density suggests {inferred} fps — importing at {inferred} fps (set FIVEOS_IMPORT_FPS=30 to force 30).");
                fps = inferred;
            }

            int frames = System.Math.Max(2, (int)System.Math.Round(durationSec * fps) + 1);

            var rig = DetectRig(anim);
            bool retarget = rig != RigKind.GtaRig;

            List<PosedBoneTrack> tracks;
            List<string> mapped, unmapped;
            System.Numerics.Vector3[] rootMotion = System.Array.Empty<System.Numerics.Vector3>();

            if (retarget)
            {
                // Foreign rig (Mixamo/generic): aim-retarget onto the GTA
                // skeleton. This orients each GTA bone to point where the source
                // bone points in world space — bind-independent, so it handles
                // Mixamo's T-pose bind vs the freemode A-pose bind (a plain
                // bind-delta offsets every limb by that difference).
                var rt = AnimRetarget.Retarget(scene, anim, tps, frames, fps, out mapped, out unmapped, out rootMotion, warnings);
                if (rt != null && rt.Count > 0)
                {
                    tracks = rt;
                    warnings.Add(rig == RigKind.Mixamo
                        ? "Mixamo rig detected — retargeted onto the GTA skeleton (world-delta + T-pose match). Body reads well; finger/twist bones are approximate."
                        : "Non-GTA rig detected — retargeted onto the GTA skeleton (world-delta + T-pose match). Verify in-game.");
                }
                else
                {
                    // Reference skeleton missing/unusable — last-resort raw copy.
                    warnings.Add("Retarget unavailable — rotations copied WITHOUT retargeting; a non-GTA rig will look wrong in-game.");
                    tracks = RawCopyTracks(anim, frames, fps, tps, out mapped, out unmapped);
                }
            }
            else
            {
                // Clip authored on the GTA skeleton (SKEL_* names): local
                // rotations are already in GTA bone space — copy them exactly.
                tracks = RawCopyTracks(anim, frames, fps, tps, out mapped, out unmapped);
            }

            if (tracks.Count == 0)
                return Result.Fail(
                    "No bone channel could be mapped to the GTA skeleton. " +
                    "Use a GTA-named rig (SKEL_*) or a Mixamo/humanoid rig. " +
                    (unmapped.Count > 0 ? $"Unmapped bones: {string.Join(", ", unmapped.Take(8))}…" : ""));

            if (retarget && rootMotion.Length > 2)
                SmoothRootMotion(rootMotion, passes: 1);

            var clipName = string.IsNullOrWhiteSpace(anim.Name) ? Path.GetFileNameWithoutExtension(path) : anim.Name;
            return new Result(true, clipName, frames, fps, durationSec, rig, retarget,
                              tracks, mapped, unmapped, warnings, null, rootMotion);
        }
        catch (System.Exception ex)
        {
            return Result.Fail($"Import failed: {ex.Message}");
        }
    }

    private static RigKind DetectRig(Animation anim)
    {
        bool anyGta = false, anyMixamo = false;
        foreach (var ch in anim.NodeAnimationChannels)
        {
            var n = ch?.NodeName ?? "";
            if (n.StartsWith("SKEL_", System.StringComparison.OrdinalIgnoreCase)) anyGta = true;
            if (n.Contains("mixamorig", System.StringComparison.OrdinalIgnoreCase)) anyMixamo = true;
        }
        // Any SKEL_* presence wins: a GTA-skeleton export is authoritative
        // even if helper nodes carry other names.
        return anyGta ? RigKind.GtaRig : anyMixamo ? RigKind.Mixamo : RigKind.Generic;
    }

    // ── GTA reference bind pose (shipped freemode skeleton) ─────────────

    /// <summary>GTA tag → bind-pose local rotation, read from the freemode
    /// reference .glb FiveOS already ships for the pose viewport. Null when
    /// the file can't be found/parsed (caller falls back to raw copy).</summary>
    private static Dictionary<ushort, Quaternion>? LoadGtaBindPose(List<string> warnings)
    {
        try
        {
            var glb = Path.Combine(RuntimeAssets.ViewerDir, "reference", "freemode_male.glb");
            if (!File.Exists(glb))
                glb = Path.Combine(RuntimeAssets.ViewerDir, "reference", "freemode_female.glb");
            if (!File.Exists(glb)) return null;

            using var ctx = new AssimpContext();
            var scene = ctx.ImportFile(glb, PostProcessSteps.None);
            if (scene?.RootNode == null) return null;

            var map = new Dictionary<ushort, Quaternion>();
            void Walk(Node n)
            {
                if (GtaBoneTags.TryResolve(n.Name ?? "", out var tag) && !map.ContainsKey(tag))
                {
                    n.Transform.Decompose(out _, out var rot, out _);
                    map[tag] = Quaternion.Normalize(new Quaternion(rot.X, rot.Y, rot.Z, rot.W));
                }
                for (int i = 0; i < n.ChildCount; i++) Walk(n.Children[i]);
            }
            Walk(scene.RootNode);
            return map.Count > 0 ? map : null;
        }
        catch (System.Exception ex)
        {
            warnings.Add($"Couldn't read the GTA reference skeleton: {ex.Message}");
            return null;
        }
    }

    /// <summary>Source rig's bind-pose local rotation for a node (its static
    /// node transform), or null if the node isn't in the hierarchy.</summary>
    private static Quaternion? FindBindLocalRotation(Node root, string name)
    {
        var node = Find(root, name);
        if (node == null) return null;
        node.Transform.Decompose(out _, out var rot, out _);
        return Quaternion.Normalize(new Quaternion(rot.X, rot.Y, rot.Z, rot.W));

        static Node? Find(Node n, string target)
        {
            if (string.Equals(n.Name, target, System.StringComparison.Ordinal)) return n;
            for (int i = 0; i < n.ChildCount; i++)
            {
                var hit = Find(n.Children[i], target);
                if (hit != null) return hit;
            }
            return null;
        }
    }

    // ── GTA-rig raw copy: local rotations are already in GTA bone space ──
    private static List<PosedBoneTrack> RawCopyTracks(
        Animation anim, int frames, int fps, double tps, out List<string> mapped, out List<string> unmapped)
    {
        var tracks = new List<PosedBoneTrack>();
        mapped = new List<string>(); unmapped = new List<string>();
        var seenTags = new HashSet<ushort>();
        foreach (var ch in anim.NodeAnimationChannels)
        {
            if (ch is null || ch.RotationKeyCount == 0) continue;
            var name = ch.NodeName ?? "";
            int pivotAt = name.IndexOf("_$AssimpFbx$_", System.StringComparison.Ordinal);
            if (pivotAt >= 0) name = name.Substring(0, pivotAt);
            if (!GtaBoneTags.TryResolve(name, out var tag)) { unmapped.Add(name); continue; }
            if (!seenTags.Add(tag)) continue;
            var samples = SampleRotations(ch, frames, fps, tps);
            if (samples == null) continue;
            tracks.Add(new PosedBoneTrack(tag, samples));
            mapped.Add(name);
        }
        return tracks;
    }

    // ── channel resampling (same convention as ydr-writer's sampler) ────

    private static Quaternion[]? SampleRotations(NodeAnimationChannel ch, int frames, int fps, double tps)
    {
        var keys = ch.RotationKeys;
        if (keys is null || keys.Count == 0) return null;
        var sorted = keys.OrderBy(k => k.Time).ToList();

        var output = new Quaternion[frames];
        for (int f = 0; f < frames; f++)
        {
            double ticks = (double)f / fps * tps;
            output[f] = SampleAt(sorted, ticks);
        }
        return output;
    }

    private static Quaternion SampleAt(IList<QuaternionKey> keys, double ticks)
    {
        if (keys.Count == 1 || ticks <= keys[0].Time) return ToNumerics(keys[0].Value);
        if (ticks >= keys[^1].Time) return ToNumerics(keys[^1].Value);

        for (int i = 1; i < keys.Count; i++)
        {
            if (keys[i].Time < ticks) continue;
            var a = keys[i - 1]; var b = keys[i];
            var span = b.Time - a.Time;
            var t = span <= 0 ? 0f : (float)((ticks - a.Time) / span);
            return Quaternion.Normalize(Quaternion.Slerp(ToNumerics(a.Value), ToNumerics(b.Value), t));
        }
        return ToNumerics(keys[^1].Value);
    }

    private static Quaternion ToNumerics(Assimp.Quaternion q) =>
        Quaternion.Normalize(new Quaternion(q.X, q.Y, q.Z, q.W));

    /// <summary>Match import FPS to baked key density (Cascadeur/Blender often bake 30 or 60).</summary>
    private static int InferImportFps(Animation anim, double durationSec, int fallback)
    {
        if (durationSec <= 1e-6) return fallback;
        int maxKeys = 0;
        foreach (var ch in anim.NodeAnimationChannels)
            if (ch?.RotationKeyCount > maxKeys) maxKeys = ch.RotationKeyCount;
        if (maxKeys < 2) return fallback;
        double rate = maxKeys / durationSec;
        if (rate >= 55) return 60;
        if (rate >= 45) return 48;
        if (rate >= 27) return 30;
        if (rate >= 22) return 24;
        return fallback;
    }

    /// <summary>Light 3-tap smooth on root travel to tame retarget noise (Cascadeur FBX).</summary>
    private static void SmoothRootMotion(Vector3[] root, int passes = 1)
    {
        if (root.Length < 3 || passes < 1) return;
        for (int p = 0; p < passes; p++)
        {
            var tmp = (Vector3[])root.Clone();
            for (int i = 1; i < root.Length - 1; i++)
                root[i] = (tmp[i - 1] + tmp[i] + tmp[i + 1]) / 3f;
        }
    }
}
