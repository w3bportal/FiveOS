// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using Assimp;
using Q = System.Numerics.Quaternion;

namespace FiveOS.Services;

/// <summary>
/// Convert native RAGE .ycd bone rotations into the freemode .glb local
/// frames the pose-mode viewer applies. Without this, imported clips map
/// bones but twist into a bind-pose ball because the preview skeleton
/// expects glTF locals, not RAGE locals.
/// </summary>
internal static class YcdBoneSpaceConverter
{
    private static Dictionary<ushort, Q>? _glbBindByTag;
    private static readonly object Lock = new();

    /// <summary>Native RAGE local → freemode glb local for preview.</summary>
    public static Q NativeToGlbPreview(ushort tag, Q qNative)
    {
        if (!GlbBindByTag().TryGetValue(tag, out var qBind))
            return qNative;

        var inv = Q.Inverse(qBind);
        return Q.Normalize(inv * qNative * qBind);
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
            }
            catch (Exception ex)
            {
                FosLogger.Warn("ycd", "Couldn't load freemode bind for YCD preview conversion", ex);
            }

            _glbBindByTag = map;
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
