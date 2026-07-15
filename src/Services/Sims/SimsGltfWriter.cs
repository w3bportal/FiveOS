// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace FiveOS.Services.Sims;

/// <summary>
/// Writes a minimal glTF 2.0 from a decoded Sims CLIP with quaternion
/// channels (no Euler). Assimp + AnimRetarget then ingest it like Mixamo.
/// <para>
/// Sims adult CLIP stores <b>deltas from bind</b>, with bone length along
/// local +X (not Blender +Y). Bind translations come from the CLIP's own
/// translation keys. Thighs need a ~180° about Z bind so legs hang down
/// after ROOT_bind tips +X → world up. Import must compose
/// <c>rest * channel</c> (see <see cref="SimsEmoteImporter"/>).
/// </para>
/// </summary>
public static class SimsGltfWriter
{
    // Legs: CLIP thigh deltas are small; bind flips bone +X to point down
    // once ROOT_bind maps +X → world +Y.
    private static readonly Quaternion ThighBindFlip =
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);

    public static string Write(string path, SimsClipDecoder.DecodedClip clip)
    {
        var bones = OrderBones(clip.Bones.Keys);
        if (bones.Count == 0)
            throw new InvalidDataException("No bones to write.");

        var indexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < bones.Count; i++) indexOf[bones[i]] = i;

        var children = bones.ToDictionary(b => b, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var b in bones)
        {
            if (!SimsBoneHashes.Parents.TryGetValue(b, out var p) || p == null) continue;
            if (indexOf.ContainsKey(p)) children[p].Add(b);
        }

        var fps = clip.TickLength > 0 ? 1.0 / clip.TickLength : 30.0;
        var frames = Math.Max(1, clip.NumTicks);
        // glTF-2.0 animation sampler input is in SECONDS. Assimp's glTF reader
        // then scales those seconds to its own ms tick clock (×1000, tps=1000),
        // which AnimEmoteImporter divides back out — so a clip's duration comes
        // through correctly ONLY if we write seconds here. Writing milliseconds
        // (the old bug) made Assimp read each time as whole seconds, inflating
        // every Sims clip's duration ~1000× (an 8.7 s pose read as 8667 s), which
        // then hit the emote-length cap and flooded the timeline. Seconds also
        // makes Sims consistent with every other glTF/Mixamo import path.
        var times = new float[frames];
        for (var t = 0; t < frames; t++) times[t] = (float)(t / fps);

        // Per-bone bind rotation (rest) and first-frame clip (for root delta).
        var bindRot = new Quaternion[bones.Count];
        var rootClip0 = Quaternion.Identity;
        for (var bi = 0; bi < bones.Count; bi++)
        {
            bindRot[bi] = BindRotation(bones[bi]);
            if (bi == 0 && clip.Bones.TryGetValue(bones[bi], out var rt) && rt.Rotations.Count > 0)
            {
                rootClip0 = Quaternion.Normalize(rt.Rotations.OrderBy(x => x.Tick).First().Rotation);
                bindRot[bi] = rootClip0; // upright bind; anim stores deltas from this
            }
        }

        using var bin = new MemoryStream();
        void WriteFloats(ReadOnlySpan<float> f)
        {
            Span<byte> tmp = stackalloc byte[4];
            foreach (var v in f)
            {
                BitConverter.TryWriteBytes(tmp, v);
                bin.Write(tmp);
            }
        }

        var timeOffset = 0;
        WriteFloats(times);
        var timeBytes = frames * 4;

        var rotViews = new List<(int byteOffset, int byteLength, int boneIndex)>();
        Span<float> qf = stackalloc float[4];
        for (var bi = 0; bi < bones.Count; bi++)
        {
            var name = bones[bi];
            var start = (int)bin.Length;
            var last = Quaternion.Identity;
            var hasKeys = clip.Bones.TryGetValue(name, out var track);
            var bind = bindRot[bi];
            var bindInv = Quaternion.Inverse(bind);
            for (var t = 0; t < frames; t++)
            {
                if (hasKeys)
                {
                    foreach (var (tick, rq) in track!.Rotations)
                        if (tick <= t) last = rq;
                }
                // Channel is the DELTA that compose(rest, channel) rebuilds the
                // absolute local: rest * channel = absolute.
                // absolute = bind * clip_delta for limbs; for root absolute = clip
                // with rest = clip0 → channel = inv(clip0)*clip.
                Quaternion absolute;
                if (bi == 0)
                    absolute = Quaternion.Normalize(last);
                else
                    absolute = Quaternion.Normalize(bind * last);

                var channel = Quaternion.Normalize(bindInv * absolute);
                qf[0] = channel.X; qf[1] = channel.Y; qf[2] = channel.Z; qf[3] = channel.W;
                WriteFloats(qf);
            }
            rotViews.Add((start, frames * 16, bi));
        }

        // Root translation track (loco / height) when CLIP has multiple keys.
        var posViews = new List<(int byteOffset, int byteLength, int boneIndex)>();
        if (clip.Bones.TryGetValue(bones[0], out var rootTrack) && rootTrack.Translations.Count > 1)
        {
            var start = (int)bin.Length;
            var last = rootTrack.Translations[0].Translation;
            Span<float> pf = stackalloc float[3];
            for (var t = 0; t < frames; t++)
            {
                foreach (var (tick, tp) in rootTrack.Translations)
                    if (tick <= t) last = tp;
                pf[0] = last.X; pf[1] = last.Y; pf[2] = last.Z;
                WriteFloats(pf);
            }
            posViews.Add((start, frames * 12, 0));
        }

        var binBytes = bin.ToArray();
        var binName = Path.GetFileNameWithoutExtension(path) + ".bin";
        var binPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, binName);
        File.WriteAllBytes(binPath, binBytes);

        var nodes = new List<object>();
        for (var i = 0; i < bones.Count; i++)
        {
            var name = bones[i];
            var off = BindTranslation(clip, name);
            var br = bindRot[i];
            var node = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["translation"] = new[] { (double)off.X, (double)off.Y, (double)off.Z },
                ["rotation"] = new[] { (double)br.X, (double)br.Y, (double)br.Z, (double)br.W },
            };
            var ch = children[name];
            if (ch.Count > 0)
                node["children"] = ch.Select(c => indexOf[c]).ToArray();
            nodes.Add(node);
        }

        var accessors = new List<object>
        {
            new
            {
                bufferView = 0,
                componentType = 5126,
                count = frames,
                type = "SCALAR",
                max = new[] { (double)times[^1] },
                min = new[] { (double)times[0] },
            }
        };
        var bufferViews = new List<object>
        {
            new { buffer = 0, byteOffset = timeOffset, byteLength = timeBytes }
        };

        var channels = new List<object>();
        var samplers = new List<object>();
        void AddRotationChannel(int byteOffset, int byteLength, int boneIndex)
        {
            var bvIndex = bufferViews.Count;
            bufferViews.Add(new { buffer = 0, byteOffset, byteLength });
            var accIndex = accessors.Count;
            accessors.Add(new
            {
                bufferView = bvIndex,
                componentType = 5126,
                count = frames,
                type = "VEC4",
            });
            var sampIndex = samplers.Count;
            samplers.Add(new { input = 0, interpolation = "LINEAR", output = accIndex });
            channels.Add(new
            {
                sampler = sampIndex,
                target = new { node = boneIndex, path = "rotation" },
            });
        }

        foreach (var (byteOffset, byteLength, boneIndex) in rotViews)
            AddRotationChannel(byteOffset, byteLength, boneIndex);

        foreach (var (byteOffset, byteLength, boneIndex) in posViews)
        {
            var bvIndex = bufferViews.Count;
            bufferViews.Add(new { buffer = 0, byteOffset, byteLength });
            var accIndex = accessors.Count;
            accessors.Add(new
            {
                bufferView = bvIndex,
                componentType = 5126,
                count = frames,
                type = "VEC3",
            });
            var sampIndex = samplers.Count;
            samplers.Add(new { input = 0, interpolation = "LINEAR", output = accIndex });
            channels.Add(new
            {
                sampler = sampIndex,
                target = new { node = boneIndex, path = "translation" },
            });
        }

        var animName = string.IsNullOrWhiteSpace(clip.Name) ? "clip" : clip.Name.Replace(':', '_');

        var gltf = new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0", generator = "FiveOS Sims CLIP" },
            ["scene"] = 0,
            ["scenes"] = new object[] { new { nodes = new[] { 0 } } },
            ["nodes"] = nodes,
            ["animations"] = new object[]
            {
                new { name = animName, channels, samplers }
            },
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new object[]
            {
                new { byteLength = binBytes.Length, uri = binName }
            },
        };

        var json = JsonSerializer.Serialize(gltf, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static Quaternion BindRotation(string name)
    {
        if (name.Contains("Thigh", StringComparison.OrdinalIgnoreCase))
            return ThighBindFlip;
        return Quaternion.Identity;
    }

    private static Vector3 BindTranslation(SimsClipDecoder.DecodedClip clip, string name)
    {
        if (clip.Bones.TryGetValue(name, out var track) && track.Translations.Count > 0)
            return track.Translations.OrderBy(x => x.Tick).First().Translation;
        return ApproxOffset(name);
    }

    private static List<string> OrderBones(IEnumerable<string> names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        string? root = set.FirstOrDefault(n => n.Equals("b__ROOT__", StringComparison.OrdinalIgnoreCase))
                    ?? set.FirstOrDefault(n => n.Equals("b__ROOT_bind__", StringComparison.OrdinalIgnoreCase))
                    ?? set.FirstOrDefault(n => n.Equals("b__Pelvis__", StringComparison.OrdinalIgnoreCase))
                    ?? set.First();

        var ordered = new List<string>();
        void Visit(string n)
        {
            if (!set.Remove(n)) return;
            ordered.Add(n);
            foreach (var (child, parent) in SimsBoneHashes.Parents)
            {
                if (parent != null && parent.Equals(n, StringComparison.OrdinalIgnoreCase) && set.Contains(child))
                    Visit(child);
            }
        }
        Visit(root!);
        ordered.AddRange(set);
        return ordered;
    }

    /// <summary>Fallback when a bone has no translation channel — Sims bone
    /// length is along local +X.</summary>
    private static Vector3 ApproxOffset(string name)
    {
        var n = name.ToLowerInvariant();
        float len =
            n.Contains("thigh") || n.Contains("calf") ? 0.40f :
            n.Contains("foot") ? 0.10f :
            n.Contains("toe") ? 0.06f :
            n.Contains("spine") ? 0.12f :
            n.Contains("neck") ? 0.08f :
            n.Contains("head") ? 0.12f :
            n.Contains("clavicle") ? 0.08f :
            n.Contains("upperarm") ? 0.28f :
            n.Contains("forearm") ? 0.25f :
            n.Contains("hand") ? 0.08f :
            n.Contains("pelvis") ? 0.05f :
            n.Contains("thumb") || n.Contains("index") || n.Contains("mid") || n.Contains("ring") || n.Contains("pinky") ? 0.03f :
            0.04f;
        float side = n.Contains("l_") ? 0.02f : n.Contains("r_") ? -0.02f : 0f;
        return new Vector3(len, 0, side);
    }
}
