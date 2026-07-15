// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using Assimp;
using Q = System.Numerics.Quaternion;

namespace FiveOS.Services;

/// <summary>
/// Helpers for detecting native RAGE .ycd bone space. The actual
/// native→glb compose (<c>qBind * qNative</c>) runs in the viewer with
/// live <c>poseInitialQuats</c> — Assimp bind maps from this class do not
/// match THREE.js bone locals and face-planted the preview when used here.
/// </summary>
internal static class YcdBoneSpaceConverter
{
    private static Dictionary<ushort, Q>? _glbBindByTag;
    private static readonly object Lock = new();

    /// <summary>Native RAGE absolute local → freemode glb absolute local
    /// using Assimp freemode binds. Prefer the viewer path; kept for
    /// diagnostics / LooksLikeNativeRage's bind distance check.</summary>
    public static Q NativeToGlbPreview(ushort tag, Q qNative)
    {
        if (!GlbBindByTag().TryGetValue(tag, out var qBind))
            return Q.Normalize(qNative);
        return Q.Normalize(qBind * qNative);
    }

    /// <summary>
    /// True when the clip's SKEL_ROOT track looks like raw RAGE (near
    /// identity at frame 0) rather than already-glb (near the −90° X bind).
    /// FiveOS exports skip ROOT entirely — those return false (treat as glb).
    /// </summary>
    public static bool LooksLikeNativeRage(IReadOnlyDictionary<ushort, Q[]> rotationTracksByTag)
    {
        if (rotationTracksByTag is null
            || !rotationTracksByTag.TryGetValue(0, out var rootFrames)
            || rootFrames is null
            || rootFrames.Length == 0)
            return false;

        var q0 = Q.Normalize(rootFrames[0]);
        if (!GlbBindByTag().TryGetValue(0, out var qBind))
            return QuatDistSq(q0, Q.Identity) < 0.15f;

        // Native rest ≈ I; glb rest ≈ qBind (−90° X). Whichever is closer wins.
        return QuatDistSq(q0, Q.Identity) + 0.02f < QuatDistSq(q0, qBind);
    }

    public static Q? TryGetGlbBind(ushort tag)
        => GlbBindByTag().TryGetValue(tag, out var q) ? q : null;

    private static float QuatDistSq(Q a, Q b)
    {
        // Quaternions q and −q are the same rotation — take the nearer.
        float d1 = (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)
                 + (a.Z - b.Z) * (a.Z - b.Z) + (a.W - b.W) * (a.W - b.W);
        float d2 = (a.X + b.X) * (a.X + b.X) + (a.Y + b.Y) * (a.Y + b.Y)
                 + (a.Z + b.Z) * (a.Z + b.Z) + (a.W + b.W) * (a.W + b.W);
        return Math.Min(d1, d2);
    }

    private static Dictionary<ushort, Q> GlbBindByTag()
    {
        if (_glbBindByTag is not null) return _glbBindByTag;
        lock (Lock)
        {
            if (_glbBindByTag is not null) return _glbBindByTag;

            var map = new Dictionary<ushort, Q>();
            try
            {
                var glb = Path.Combine(RuntimeAssets.ViewerDir, "reference", "freemode_male.glb");
                if (!File.Exists(glb))
                    glb = Path.Combine(RuntimeAssets.ViewerDir, "reference", "freemode_female.glb");
                if (File.Exists(glb))
                {
                    using var ctx = new AssimpContext();
                    var scene = ctx.ImportFile(glb, PostProcessSteps.None);
                    var gameRig = FindNode(scene.RootNode, "GAME_RIG");
                    var root = FindNode(gameRig ?? scene.RootNode, "SKEL_ROOT");
                    if (root is not null)
                        WalkBind(root, map);
                }
                else
                {
                    FosLogger.Warn("ycd", "freemode reference glb missing under " + RuntimeAssets.ViewerDir
                        + " — native-space detection will retry on next import");
                }
            }
            catch (Exception ex)
            {
                FosLogger.Warn("ycd", "Couldn't load freemode bind for YCD preview conversion", ex);
            }

            // Only cache a USABLE map. An empty result (glb missing mid-extract,
            // Assimp hiccup) used to be cached for the whole session and silently
            // broke native-space detection on every later import — retry instead.
            if (map.Count > 0) _glbBindByTag = map;
            return map;
        }
    }

    private static void WalkBind(Node node, Dictionary<ushort, Q> map)
    {
        var name = node.Name ?? "";
        if (GtaBoneTags.TryResolve(name, out var tag) && !map.ContainsKey(tag))
        {
            node.Transform.Decompose(out _, out var rot, out _);
            map[tag] = Q.Normalize(new Q(rot.X, rot.Y, rot.Z, rot.W));
        }

        for (int i = 0; i < node.ChildCount; i++)
            WalkBind(node.Children[i], map);
    }

    private static Node? FindNode(Node node, string name)
    {
        if (string.Equals(node.Name, name, StringComparison.Ordinal))
            return node;
        for (int i = 0; i < node.ChildCount; i++)
        {
            var hit = FindNode(node.Children[i], name);
            if (hit is not null) return hit;
        }
        return null;
    }
}
