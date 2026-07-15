// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using CodeWalker.GameFiles;

namespace FiveOS.Services;

/// <summary>
/// Read .ycd binaries back into the editor's timeline. The writer side
/// (<see cref="YcdAnimationBuilder"/>) emits an XML clip that round-trips
/// through CodeWalker's resource framing; on the read side we lean on
/// the same library but pull tracks out via reflection rather than
/// hard-coding internal type members. CW's internal model has been a
/// moving target across versions — reflective access tolerates renames
/// and lets the importer keep working without a recompile.
///
/// Output is the same JSON payload <c>window.setKeyframes</c> accepts so
/// the host can simply forward it to the viewer.
/// </summary>
public static class YcdImporter
{
    /// <summary>Preview imports cap resolution so WebView2 isn't fed
    /// multi-megabyte per-frame JSON. Export re-samples from the baked
    /// clip at the user's chosen FPS — 30 Hz preview is plenty for scrub.</summary>
    private const int PreviewMaxFps = 30;
    private const int PreviewMaxFrames = 900;

    private static int ComputePreviewFrameCount(int sourceFrames, int sourceFps, float duration)
    {
        int fps = Math.Clamp(Math.Min(sourceFps, PreviewMaxFps), 1, 120);
        int frames = Math.Max(2, sourceFrames);
        if (duration > 0.001f)
            frames = Math.Min(frames, (int)Math.Ceiling(duration * fps) + 1);
        return Math.Clamp(frames, 2, PreviewMaxFrames);
    }

    /// <summary>Result of a successful import: clip name (from the YCD
    /// dictionary), bone tracks resolved to display names, plus the
    /// raw JSON payload ready to ship to the viewer.</summary>
    public record ImportResult(
        string ClipName,
        int KeyframeCount,
        int FrameCount,
        int Fps,
        double Duration,
        int MappedBones,
        int UnmappedTracks,
        string PayloadJson);

    /// <summary>Import the FIRST clip out of a YCD file. Most player
    /// emotes are single-clip; multi-clip handling can layer on top
    /// later (the picker UI is the only missing piece — the reader
    /// already returns the full clip list via <see cref="ListClips"/>).</summary>
    public static ImportResult ImportFirst(string path, IReadOnlyList<string> rigBoneNames)
        => ImportFirstBundled(path, rigBoneNames).Result;

    /// <summary>Load the file once, list clip names, and import the first
    /// clip — avoids parsing the same .ycd twice on large emotes.</summary>
    public static (List<string> Clips, ImportResult Result) ImportFirstBundled(
        string path, IReadOnlyList<string> rigBoneNames)
    {
        var ycd = LoadYcd(path);
        var clipDict = ycd.ClipDictionary
            ?? throw new InvalidDataException("YCD has no clip dictionary.");

        var clips = ListClipsFromDictionary(clipDict);
        var resolved = ResolveFirstClipAndAnimation(clipDict)
            ?? throw new InvalidDataException("YCD clip references an animation that isn't in the dictionary.");

        return (clips, BuildFromAnimation(resolved.name, resolved.anim, rigBoneNames));
    }

    /// <summary>List clip names in the dictionary without importing —
    /// used by the host to drive a "Pick clip" dialog when the file
    /// holds more than one.</summary>
    public static List<string> ListClips(string path)
    {
        var ycd = LoadYcd(path);
        return ListClipsFromDictionary(ycd.ClipDictionary);
    }

    private static List<string> ListClipsFromDictionary(ClipDictionary? clipDict)
    {
        var list = new List<string>();
        if (clipDict is null) return list;

        // ClipMap is the authoritative name list; the Clips pointer array is
        // sparse (mostly null slots) and its enumerator throws anyway.
        if (clipDict.ClipMap is { Count: > 0 })
        {
            foreach (var kv in clipDict.ClipMap)
                list.Add(kv.Key.ToString());
            return list;
        }

        var clips = ReadEnumerable(clipDict, "Clips");
        if (clips is null) return list;
        foreach (var c in clips)
        {
            var name = ReadString(c, "Name") ?? ReadString(c, "ShortName") ?? "(unnamed)";
            list.Add(name);
        }
        return list;
    }

    /// <summary>Import a specific named clip — exposed for the multi-
    /// clip picker once that UI exists.</summary>
    public static ImportResult ImportNamed(string path, string clipName, IReadOnlyList<string> rigBoneNames)
    {
        var ycd = LoadYcd(path);
        var clipDict = ycd.ClipDictionary
            ?? throw new InvalidDataException("YCD has no clip dictionary.");

        if (clipDict.ClipMap is { Count: > 0 })
        {
            foreach (var kv in clipDict.ClipMap)
            {
                if (!string.Equals(kv.Key.ToString(), clipName, StringComparison.OrdinalIgnoreCase)) continue;
                var resolved = ResolveClipMapEntry(clipDict, kv.Key, kv.Value);
                if (resolved is not null)
                    return BuildFromAnimation(resolved.Value.name, resolved.Value.anim, rigBoneNames);
            }
            throw new InvalidDataException("Clip not found: " + clipName);
        }

        object? targetClip = null;
        var clips = EnumerateClips(clipDict);
        if (clips is not null)
        {
            foreach (var c in clips)
            {
                var n = ReadString(c, "Name") ?? ReadString(c, "ShortName");
                if (string.Equals(n, clipName, StringComparison.OrdinalIgnoreCase))
                {
                    targetClip = c;
                    break;
                }
            }
        }
        targetClip ??= FindFirstClipObject(clipDict)
            ?? throw new InvalidDataException("Clip not found: " + clipName);

        var named = ResolveClipObject(clipDict, targetClip)
            ?? throw new InvalidDataException("Couldn't resolve clip's animation in dictionary.");
        return BuildFromAnimation(named.name, named.anim, rigBoneNames);
    }

    // ── Internal: walk the dictionary structure ─────────────────────

    /// <summary>Find the first clip + animation pair using every layout
    /// shipping .ycd files use (ClipMap, AnimMap, sparse Clips[], Animations block).</summary>
    private static (string name, object anim)? ResolveFirstClipAndAnimation(ClipDictionary clipDict)
    {
        if (clipDict.ClipMap is { Count: > 0 })
        {
            foreach (var kv in clipDict.ClipMap)
            {
                var resolved = ResolveClipMapEntry(clipDict, kv.Key, kv.Value);
                if (resolved is not null) return resolved;
            }
        }

        foreach (var clip in EnumerateClips(clipDict) ?? Enumerable.Empty<object>())
        {
            var resolved = ResolveClipObject(clipDict, clip);
            if (resolved is not null) return resolved;
        }

        // Clip/anim hashes often differ (dict name vs internal anim name) — pair
        // when there is exactly one of each, which covers most dpemotes ycds.
        if (clipDict.ClipMap is { Count: 1 } && clipDict.AnimMap is { Count: 1 })
        {
            var clipKv = clipDict.ClipMap.First();
            var animKv = clipDict.AnimMap.First();
            if (animKv.Value.Animation is not null)
                return (ClipLabel(clipKv.Key, clipKv.Value), animKv.Value.Animation);
        }

        foreach (var anim in EnumerateAnimations(clipDict))
            return (anim.Hash.ToString(), anim);

        if (clipDict.AnimMap is { Count: > 0 })
        {
            foreach (var kv in clipDict.AnimMap)
            {
                if (kv.Value.Animation is not null)
                    return (kv.Key.ToString(), kv.Value.Animation);
            }
        }

        return null;
    }

    private static IEnumerable<object>? EnumerateClips(ClipDictionary clipDict)
        => EnumerateCollection(clipDict.Clips);

    private static IEnumerable<Animation> EnumerateAnimations(ClipDictionary clipDict)
    {
        var block = clipDict.Animations?.Animations;
        if (block is null) yield break;

        if (block.data_items is { Length: > 0 } items)
        {
            foreach (var item in items)
            {
                var anim = UnwrapAnimation(item);
                if (anim is not null) yield return anim;
            }
            yield break;
        }

        for (int i = 0; i < block.Count; i++)
        {
            var anim = UnwrapAnimation(block[i]);
            if (anim is not null) yield return anim;
        }
    }

    private static Animation? UnwrapAnimation(object? item)
    {
        if (item is Animation anim) return anim;
        if (item is AnimationMapEntry entry) return entry.Animation;
        return null;
    }

    private static (string name, object anim)? ResolveClipMapEntry(
        ClipDictionary clipDict, MetaHash clipHash, ClipMapEntry entry)
    {
        var anim = ResolveAnimationForClip(clipDict, clipHash, entry);
        if (anim is null) return null;
        return (ClipLabel(clipHash, entry), anim);
    }

    private static string ClipLabel(MetaHash clipHash, ClipMapEntry entry)
        => entry.Clip?.Name ?? entry.Clip?.ShortName ?? clipHash.ToString();

    private static (string name, object anim)? ResolveClipObject(ClipDictionary clipDict, object clip)
    {
        if (clip is ClipMapEntry mapEntry)
            return ResolveClipMapEntry(clipDict, mapEntry.Hash, mapEntry);

        if (clip is ClipAnimation clipAnim)
        {
            var anim = clipAnim.Animation ?? FindAnimationInAnimMap(clipDict, clipAnim.AnimationHash);
            if (anim is not null)
                return (clipAnim.Name ?? clipAnim.ShortName ?? clipAnim.Hash.ToString(), anim);
        }

        return ResolveClipAnimation(clipDict, clip);
    }

    private static object? ResolveAnimationForClip(ClipDictionary clipDict, MetaHash clipHash, ClipMapEntry entry)
    {
        if (clipDict.AnimMap.TryGetValue(clipHash, out var byClipKey) && byClipKey.Animation is not null)
            return byClipKey.Animation;

        if (entry.Clip is ClipAnimation clipAnim)
        {
            if (clipAnim.Animation is not null) return clipAnim.Animation;
            var byAnimHash = FindAnimationInAnimMap(clipDict, clipAnim.AnimationHash);
            if (byAnimHash is not null) return byAnimHash;
        }

        var byEntryHash = FindAnimationInAnimMap(clipDict, entry.Hash);
        if (byEntryHash is not null) return byEntryHash;

        return FindAnimationInAnimMap(clipDict, clipHash);
    }

    private static Animation? FindAnimationInAnimMap(ClipDictionary clipDict, MetaHash hash)
    {
        if (clipDict.AnimMap.TryGetValue(hash, out var direct) && direct.Animation is not null)
            return direct.Animation;

        uint h = hash.Hash;
        foreach (var kv in clipDict.AnimMap)
        {
            if (kv.Key.Hash != h) continue;
            if (kv.Value.Animation is not null) return kv.Value.Animation;
        }

        foreach (var anim in EnumerateAnimations(clipDict))
        {
            if (anim.Hash.Hash == h) return anim;
        }

        return null;
    }

    private static object? FindFirstClipObject(ClipDictionary clipDict)
    {
        if (clipDict.ClipMap is { Count: > 0 })
            return clipDict.ClipMap.First().Value;

        return EnumerateClips(clipDict)?.FirstOrDefault(c => c is not null);
    }

    private static (string name, object anim)? ResolveClipAnimation(ClipDictionary clipDict, object clip)
    {
        if (clip is ClipMapEntry mapEntry)
            return ResolveClipMapEntry(clipDict, mapEntry.Hash, mapEntry);

        var name = ReadString(clip, "Name") ?? ReadString(clip, "ShortName") ?? "imported";

        // ClipAnimation exposes the resolved Animation reference directly —
        // CW already chased the AnimationHash through the dictionary at load
        // time, so we don't need to walk it again.
        var direct = ReadValue(clip, "Animation");
        if (direct is not null) return (name, direct);

        // ClipAnimationList carries multiple ClipAnimationsEntry items, each
        // pointing at its own Animation. Take the first for the importer's
        // "first clip" semantics; the multi-clip picker can extend this.
        var entries = ReadEnumerable(clip, "Animations");
        var firstEntry = entries?.FirstOrDefault();
        if (firstEntry is not null)
        {
            var anim = ReadValue(firstEntry, "Animation");
            if (anim is not null) return (name, anim);
        }

        // Last-ditch fallback: look the clip's animation hash up in the
        // dictionary's AnimMap (Dictionary<MetaHash, AnimationMapEntry>).
        // This path matters only if CW didn't auto-resolve the pointer,
        // which shouldn't happen on Load — kept for forward-compat.
        var hashObj = ReadValue(clip, "AnimationHash") ?? ReadValue(clip, "NameHash") ?? ReadValue(clip, "Hash");
        if (hashObj is MetaHash mh)
        {
            var anim = FindAnimationInAnimMap(clipDict, mh);
            if (anim is not null) return (name, anim);
        }
        return null;
    }

    private static ImportResult BuildFromAnimation(string clipName, object animation, IReadOnlyList<string> rigBoneNames)
    {
        if (animation is Animation cwAnim)
            return BuildFromCodewalkerAnimation(clipName, cwAnim, rigBoneNames);

        // Legacy reflection path — kept for forward-compat if CW's in-memory
        // model changes type names but the property layout stays similar.
        return BuildFromAnimationReflective(clipName, animation, rigBoneNames);
    }

    /// <summary>Read tracks the way CodeWalker does: BoneIds[i] pairs with
    /// Sequences[chunk].Sequences[i] — BoneId is NOT stored on each
    /// AnimSequence until AssignSequenceBoneIds runs.</summary>
    private static ImportResult BuildFromCodewalkerAnimation(
        string clipName, Animation anim, IReadOnlyList<string> rigBoneNames)
    {
        anim.AssignSequenceBoneIds();

        int frameCount = anim.Frames;
        float duration = anim.Duration;
        int fps = (duration > 0.001f && frameCount > 1)
            ? (int)Math.Round((frameCount - 1) / duration)
            : 30;
        fps = Math.Clamp(fps, 1, 120);

        if (frameCount < 2 && duration > 0.001f)
            frameCount = Math.Max(2, (int)Math.Round(duration * fps) + 1);

        const int maxFrames = 12_000;
        if (frameCount > maxFrames)
            frameCount = maxFrames;

        var tagToRigName = BuildTagToRigNameMap(rigBoneNames);
        var rotationTracksByTag = new Dictionary<ushort, Quaternion[]>();
        var positionTracksByTag = new Dictionary<ushort, Vector3[]>();
        int unmapped = 0;
        int safeFrameCount = Math.Max(1, frameCount);
        int previewFrames = ComputePreviewFrameCount(safeFrameCount, fps, duration);

        var boneIds = anim.BoneIds?.data_items;
        if (boneIds is { Length: > 0 } && anim.Sequences?.data_items is { Length: > 0 } seqChunks)
        {
            for (int i = 0; i < boneIds.Length; i++)
            {
                var boneIdItem = boneIds[i];
                ushort boneTag = boneIdItem.BoneId;
                if (!tagToRigName.ContainsKey(boneTag)) { if (boneIdItem.Track is 0 or 1) unmapped++; continue; }

                if (boneIdItem.Track == 1)
                {
                    var perFrame = new Quaternion[previewFrames];
                    for (int f = 0; f < previewFrames; f++)
                    {
                        float t = previewFrames > 1 && duration > 0.001f
                            ? (f / (float)(previewFrames - 1)) * duration
                            : 0f;
                        var fp = anim.GetFramePosition(t);
                        try
                        {
                            var sq = anim.EvaluateQuaternion(fp, i, interpolate: true);
                            perFrame[f] = new Quaternion(sq.X, sq.Y, sq.Z, sq.W);
                        }
                        catch { perFrame[f] = Quaternion.Identity; }
                    }
                    rotationTracksByTag[boneTag] = perFrame;
                }
                else if (boneIdItem.Track == 0)
                {
                    var perFrame = new Vector3[previewFrames];
                    for (int f = 0; f < previewFrames; f++)
                    {
                        float t = previewFrames > 1 && duration > 0.001f
                            ? (f / (float)(previewFrames - 1)) * duration
                            : 0f;
                        var fp = anim.GetFramePosition(t);
                        try
                        {
                            var sv = anim.EvaluateVector4(fp, i, interpolate: true);
                            perFrame[f] = new Vector3(sv.X, sv.Y, sv.Z);
                        }
                        catch { perFrame[f] = Vector3.Zero; }
                    }
                    positionTracksByTag[boneTag] = perFrame;
                }
            }
        }

        return FinishImportResult(clipName, previewFrames, fps, duration, tagToRigName,
            rotationTracksByTag, positionTracksByTag, unmapped);
    }

    private static ImportResult BuildFromAnimationReflective(
        string clipName, object animation, IReadOnlyList<string> rigBoneNames)
    {
        var frameCount = ReadInt(animation, "Frames") ?? ReadInt(animation, "FrameCount") ?? 0;
        var duration   = ReadFloat(animation, "Duration") ?? 1.0f;
        int fps = (duration > 0.001f && frameCount > 1)
            ? (int)Math.Round((frameCount - 1) / duration)
            : 30;
        fps = Math.Clamp(fps, 1, 120);

        if (frameCount < 2 && duration > 0.001f)
            frameCount = Math.Max(2, (int)Math.Round(duration * fps) + 1);

        const int maxFrames = 12_000;
        if (frameCount > maxFrames)
            frameCount = maxFrames;

        var tagToRigName = BuildTagToRigNameMap(rigBoneNames);
        var rotationTracksByTag = new Dictionary<ushort, Quaternion[]>();
        int unmapped = 0;

        // Pair Animation.BoneIds[i] with SequenceData[i] inside the first chunk.
        var boneIdItems = ReadEnumerable(animation, "BoneIds")?.ToList();
        var chunks = ReadEnumerable(animation, "Sequences");
        var firstChunk = chunks?.FirstOrDefault();
        var animSequences = firstChunk is null
            ? null
            : ReadEnumerable(firstChunk, "Sequences")?.ToList();

        if (boneIdItems is not null && animSequences is not null)
        {
            int pairCount = Math.Min(boneIdItems.Count, animSequences.Count);
            for (int i = 0; i < pairCount; i++)
            {
                var boneIdEntry = boneIdItems[i];
                var boneTag = (ushort)(ReadInt(boneIdEntry, "BoneId") ?? 0);
                var trackKind = ReadInt(boneIdEntry, "Track") ?? -1;
                if (trackKind != 1) continue;
                if (!tagToRigName.ContainsKey(boneTag)) { unmapped++; continue; }

                var channels = ReadEnumerable(animSequences[i], "Channels")?.ToList();
                if (channels is null || channels.Count == 0) continue;
                var perFrame = EvaluateRotationChannels(channels, frameCount);
                if (perFrame is null || perFrame.Length == 0) continue;
                rotationTracksByTag[boneTag] = perFrame;
            }
        }

        return FinishImportResult(clipName,
            ComputePreviewFrameCount(frameCount, fps, duration), fps, duration, tagToRigName,
            rotationTracksByTag, new Dictionary<ushort, Vector3[]>(), unmapped);
    }

    private static Dictionary<ushort, string> BuildTagToRigNameMap(IReadOnlyList<string> rigBoneNames)
    {
        var tagToRigName = new Dictionary<ushort, string>();
        foreach (var rn in rigBoneNames)
        {
            if (GtaBoneTags.TryResolve(rn, out var tag) && !tagToRigName.ContainsKey(tag))
                tagToRigName[tag] = rn;
        }
        return tagToRigName;
    }

    private static ImportResult FinishImportResult(
        string clipName,
        int previewFrames,
        int sourceFps,
        float duration,
        Dictionary<ushort, string> tagToRigName,
        Dictionary<ushort, Quaternion[]> rotationTracksByTag,
        Dictionary<ushort, Vector3[]> positionTracksByTag,
        int unmapped)
    {
        int previewFps = Math.Clamp(Math.Min(sourceFps, PreviewMaxFps), 1, 120);
        double clipDuration = duration > 0.001
            ? duration
            : (previewFrames - 1) / (double)Math.Max(1, previewFps);

        var tracks = new List<object>(rotationTracksByTag.Count);
        foreach (var (tag, perFrame) in rotationTracksByTag)
        {
            var name = tagToRigName[tag];
            var qList = new List<float[]>(previewFrames);
            List<float[]>? pList = null;
            if (positionTracksByTag.TryGetValue(tag, out var perPos))
            {
                pList = new List<float[]>(previewFrames);
                for (int f = 0; f < previewFrames; f++)
                {
                    int srcF = previewFrames <= 1 ? 0
                        : (int)Math.Round(f * (perPos.Length - 1) / (double)(previewFrames - 1));
                    srcF = Math.Clamp(srcF, 0, perPos.Length - 1);
                    var p = perPos[srcF];
                    pList.Add(new[] { p.X, p.Y, p.Z });
                }
            }
            for (int f = 0; f < previewFrames; f++)
            {
                int srcF = previewFrames <= 1 ? 0
                    : (int)Math.Round(f * (perFrame.Length - 1) / (double)(previewFrames - 1));
                srcF = Math.Clamp(srcF, 0, perFrame.Length - 1);
                var q = perFrame[srcF];
                qList.Add(new[] { q.X, q.Y, q.Z, q.W });
            }
            tracks.Add(pList is null ? new { name, q = qList } : new { name, q = qList, p = pList });
        }

        // Position-only bones (no rotation track in this clip).
        foreach (var (tag, perPos) in positionTracksByTag)
        {
            if (rotationTracksByTag.ContainsKey(tag)) continue;
            var name = tagToRigName[tag];
            var pList = new List<float[]>(previewFrames);
            for (int f = 0; f < previewFrames; f++)
            {
                int srcF = previewFrames <= 1 ? 0
                    : (int)Math.Round(f * (perPos.Length - 1) / (double)(previewFrames - 1));
                srcF = Math.Clamp(srcF, 0, perPos.Length - 1);
                var p = perPos[srcF];
                pList.Add(new[] { p.X, p.Y, p.Z });
            }
            tracks.Add(new { name, q = new List<float[]>(), p = pList });
        }

        var payload = new
        {
            duration = Math.Round(clipDuration, 3),
            fps = previewFps,
            source = "ycd-import",
            boneSpace = "glb",
            format = "bone-tracks",
            tracks,
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });

        return new ImportResult(
            ClipName: clipName,
            KeyframeCount: previewFrames,
            FrameCount: previewFrames,
            Fps: previewFps,
            Duration: (float)clipDuration,
            MappedBones: rotationTracksByTag.Count,
            UnmappedTracks: unmapped,
            PayloadJson: json);
    }

    /// <summary>Pull per-frame quaternions out of an AnimSequence's
    /// Channels[]. Two shapes appear in shipping YCDs:
    /// (a) one StaticQuaternion channel carrying the whole quat in a
    ///     SharpDX.Quaternion `Value` field — broadcast across all frames;
    /// (b) three float channels (RawFloat / QuantizeFloat / IndirectQuantizeFloat
    ///     / LinearFloat) for qx, qy, qz — W is reconstructed from the
    ///     unit-norm constraint, matching RAGE's runtime behavior.
    /// For (b) we invoke AnimChannel.EvaluateFloat(frame) reflectively so
    /// the same code path handles every compressed encoding without us
    /// having to know which one CW chose for each track.</summary>
    private static Quaternion[]? EvaluateRotationChannels(List<object> channels, int frameCount)
    {
        int safeFrames = Math.Max(1, frameCount);

        // Case A: single StaticQuaternion — Value is a SharpDX.Quaternion
        // struct with X/Y/Z/W fields. We pull the components reflectively
        // so we don't have to take a hard dependency on SharpDX here.
        if (channels.Count == 1)
        {
            var ch = channels[0];
            var typeName = ReadValue(ch, "Type")?.ToString() ?? ch.GetType().Name;
            if (typeName.IndexOf("StaticQuaternion", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var value = ReadValue(ch, "Value");
                if (value is null) return null;
                var x = ReadFloat(value, "X") ?? 0;
                var y = ReadFloat(value, "Y") ?? 0;
                var z = ReadFloat(value, "Z") ?? 0;
                var w = ReadFloat(value, "W") ?? 1;
                return Enumerable.Repeat(new Quaternion(x, y, z, w), safeFrames).ToArray();
            }
            if (typeName.IndexOf("CachedQuaternion", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var eval = ch.GetType().GetMethod("EvaluateQuaternion", BindingFlags.Public | BindingFlags.Instance);
                if (eval is not null)
                {
                    var arr = new Quaternion[safeFrames];
                    for (int f = 0; f < safeFrames; f++)
                    {
                        try
                        {
                            var value = eval.Invoke(ch, new object[] { f });
                            if (value is null) continue;
                            arr[f] = new Quaternion(
                                ReadFloat(value, "X") ?? 0,
                                ReadFloat(value, "Y") ?? 0,
                                ReadFloat(value, "Z") ?? 0,
                                ReadFloat(value, "W") ?? 1);
                        }
                        catch { arr[f] = Quaternion.Identity; }
                    }
                    return arr;
                }
            }
            return null;
        }

        // Case B: 3+ float channels (qx, qy, qz[, qw]). Prefer bulk Values[]
        // arrays; fall back to per-frame EvaluateFloat only when needed.
        if (channels.Count >= 3)
        {
            var valsX = GetChannelFrameValues(channels[0], safeFrames);
            var valsY = GetChannelFrameValues(channels[1], safeFrames);
            var valsZ = GetChannelFrameValues(channels[2], safeFrames);
            if (valsX is null || valsY is null || valsZ is null) return null;

            var valsW = channels.Count >= 4 ? GetChannelFrameValues(channels[3], safeFrames) : null;

            var arr = new Quaternion[safeFrames];
            for (int f = 0; f < safeFrames; f++)
            {
                float x = valsX[f], y = valsY[f], z = valsZ[f];
                float w;
                if (valsW is not null)
                    w = valsW[f];
                else
                {
                    float w2 = 1f - (x * x + y * y + z * z);
                    w = w2 > 0 ? MathF.Sqrt(w2) : 0f;
                }
                arr[f] = Quaternion.Normalize(new Quaternion(x, y, z, w));
            }
            return arr;
        }

        return null;
    }

    /// <summary>All non-static float channel types (RawFloat, QuantizeFloat,
    /// IndirectQuantizeFloat, LinearFloat, plus StaticFloat which simply
    /// returns its constant) implement <c>EvaluateFloat(int frame)</c>.
    /// Bind to it reflectively so the importer stays agnostic to which
    /// encoding CW chose for each track.</summary>
    /// <summary>Per-frame float samples for a channel — reads Values[] in
    /// one shot when CW exposes it, otherwise binds EvaluateFloat.</summary>
    private static float[]? GetChannelFrameValues(object channel, int frameCount)
    {
        var values = ReadFloatArray(channel);
        if (values is { Length: > 0 })
        {
            if (values.Length >= frameCount) return values;
            var expanded = new float[frameCount];
            for (int i = 0; i < frameCount; i++)
                expanded[i] = values[Math.Min(i, values.Length - 1)];
            return expanded;
        }

        var eval = GetFloatEvaluator(channel);
        if (eval is null) return null;
        var arr = new float[frameCount];
        for (int f = 0; f < frameCount; f++)
            arr[f] = eval(f);
        return arr;
    }

    private static Func<int, float>? GetFloatEvaluator(object channel)
    {
        var mi = channel.GetType().GetMethod(
            "EvaluateFloat",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(int) },
            modifiers: null);
        if (mi is not null)
        {
            return frame =>
            {
                try { return Convert.ToSingle(mi.Invoke(channel, new object[] { frame })); }
                catch { return 0f; }
            };
        }
        // Fallback: read the channel's Values[] (RawFloat exposes one).
        var values = ReadFloatArray(channel);
        if (values is null || values.Length == 0) return null;
        return frame => values[Math.Min(frame, values.Length - 1)];
    }

    /// <summary>CW expects an RpfFileEntry alongside the byte buffer
    /// so the resource header parser can fall back to the file name /
    /// size when the embedded header is malformed. For standalone
    /// loose-file imports we don't have a real RPF backing entry, so
    /// we synthesise a minimal stub. CW only reads Name + size from
    /// it; the rest goes untouched.</summary>
    /// <summary>Load a standalone .ycd resource. The RSC7 header carries the
    /// system/graphics page flags CodeWalker needs to map the resource's memory
    /// blocks — a bare stub entry (name+size only) loads an EMPTY ClipDictionary
    /// (the "no clips" bug). CreateResourceFileEntry parses those flags from the
    /// header and Decompress inflates the payload before Load.</summary>
    private static YcdFile LoadYcd(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var entry = RpfFile.CreateResourceFileEntry(ref data, 0);
        entry.Name = Path.GetFileName(path);
        entry.NameLower = entry.Name.ToLowerInvariant();
        data = ResourceBuilder.Decompress(data);
        var ycd = new YcdFile();
        ycd.Load(data, entry);
        return ycd;
    }

    // ── Reflection helpers ──────────────────────────────────────────

    private static object? ReadValue(object? obj, string name)
    {
        if (obj is null) return null;
        var t = obj.GetType();
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p is not null) return p.GetValue(obj);
        var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        return f?.GetValue(obj);
    }

    private static string? ReadString(object? obj, string name)
        => ReadValue(obj, name)?.ToString();

    private static int? ReadInt(object? obj, string name)
    {
        var v = ReadValue(obj, name);
        if (v is null) return null;
        try { return Convert.ToInt32(v); } catch { return null; }
    }

    private static float? ReadFloat(object? obj, string name)
    {
        var v = ReadValue(obj, name);
        if (v is null) return null;
        try { return Convert.ToSingle(v); } catch { return null; }
    }

    private static IEnumerable<object>? ReadEnumerable(object? obj, string name)
    {
        var v = ReadValue(obj, name);
        return EnumerateCollection(v);
    }

    /// <summary>Walk a CodeWalker collection without calling
    /// <c>ResourcePointerArray64&lt;T&gt;.GetEnumerator()</c> — that API throws
    /// <see cref="NotImplementedException"/> even though <c>Count</c>,
    /// <c>data_items</c>, and the indexer work fine.</summary>
    private static IEnumerable<object>? EnumerateCollection(object? v)
    {
        if (v is null || v is string) return null;

        var t = v.GetType();
        if (t.IsGenericType && t.Name.StartsWith("ResourcePointerArray64", StringComparison.Ordinal))
        {
            if (ReadValue(v, "data_items") is Array items && items.Length > 0)
                return items.Cast<object>().Where(x => x is not null)!;

            int count = ReadInt(v, "Count") ?? 0;
            var itemProp = t.GetProperty("Item");
            if (itemProp is null || count <= 0) return Array.Empty<object>();

            var list = new List<object>(count);
            for (int i = 0; i < count; i++)
            {
                var item = itemProp.GetValue(v, new object[] { i });
                if (item is not null) list.Add(item);
            }
            return list;
        }

        if (v is System.Collections.IList ilist)
            return ilist.Cast<object>().Where(x => x is not null)!;

        if (v is System.Collections.IEnumerable e)
        {
            try { return e.Cast<object>().Where(x => x is not null)!.ToList(); }
            catch (NotImplementedException) { return null; }
        }

        return null;
    }

    private static float[]? ReadFloatArray(object? channel)
    {
        // RawFloat channel's per-frame values are usually exposed as
        // either a Values array (float[]) or an enumerable of frames.
        var v = ReadValue(channel, "Values");
        if (v is float[] arr) return arr;
        if (v is System.Collections.IEnumerable e)
        {
            try { return e.Cast<object>().Select(o => Convert.ToSingle(o)).ToArray(); }
            catch { return null; }
        }
        return null;
    }
}
