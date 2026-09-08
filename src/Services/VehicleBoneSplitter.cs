// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using CodeWalker.GameFiles;
using SharpDX;

namespace FiveOS.Services;

/// <summary>
/// Gives part of a vehicle bone's geometry its own bone, so it can be
/// animated on its own. A GTA bone moves as one unit, so "animate just the
/// left speaker panel" needs that panel skinned to a bone of its own.
///
/// What it does to a .yft:
///   1. Skeleton: appends a bone (child of the source bone, zero local
///      offset, rotation + translation flags) — built by round-tripping the
///      skeleton through CodeWalker's XML so every derived array (parents,
///      children, tags map, transforms) is rebuilt for us.
///   2. Fragment BoneTransforms: one more entry (the source's, since the new
///      bone sits exactly on it).
///   3. Every LOD's geometry: vertices bound to the source bone whose
///      position falls inside the chosen box are re-bound to the new bone
///      (BlendIndices byte → the new bone's slot in that geometry's BoneIds).
///   4. Same treatment for the damaged drawable when it has one.
/// Writes a <c>.bak</c> next to the file the first time it touches it.
///
/// Bone tag = ELF hash of the UPPERCASED name, % 0xFE8F, + 0x170 — fitted
/// against every vanilla + custom bone of a real car (22/22 matches), so
/// GetEntityBoneIndexByName finds the new bone in game.
/// </summary>
public static class VehicleBoneSplitter
{
    public sealed record Box(Vector3 Min, Vector3 Max)
    {
        /// <summary>Union of piece boxes (RAGE space float triplets).</summary>
        public static Box FromRanges(IEnumerable<(float[] Min, float[] Max)> ranges)
        {
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            foreach (var (a, b) in ranges)
            {
                min = Vector3.Min(min, new Vector3(a[0], a[1], a[2]));
                max = Vector3.Max(max, new Vector3(b[0], b[1], b[2]));
            }
            return new Box(min, max);
        }
        public bool Contains(in Vector3 p, float pad = 0.02f) =>
            p.X >= Min.X - pad && p.X <= Max.X + pad &&
            p.Y >= Min.Y - pad && p.Y <= Max.Y + pad &&
            p.Z >= Min.Z - pad && p.Z <= Max.Z + pad;
    }

    /// <summary>What to move: exact vertex positions picked in the editor
    /// (matched where a geometry has those very vertices — the detail LOD),
    /// with the box as the fallback for LODs whose vertices differ.</summary>
    public sealed record Selection(Box Box, HashSet<(int X, int Y, int Z)>? Positions)
    {
        public static (int X, int Y, int Z) Quantize(in Vector3 p)
            => ((int)Math.Round(p.X * 1e4), (int)Math.Round(p.Y * 1e4), (int)Math.Round(p.Z * 1e4));
        /// <summary>Same, from RAGE-space float triplets.</summary>
        public static Selection FromFloatPositions(IEnumerable<float[]> positions, Box box)
            => FromPositions(positions.Where(p => p.Length >= 3).Select(p => new Vector3(p[0], p[1], p[2])), box);

        public static Selection FromPositions(IEnumerable<Vector3> positions, Box box)
        {
            var set = new HashSet<(int, int, int)>();
            foreach (var p in positions) set.Add(Quantize(p));
            return new Selection(box, set.Count > 0 ? set : null);
        }
    }

    public sealed record FileResult(string Path, string? Backup, int BoneIndex, ushort Tag, int VerticesMoved, int GeometriesTouched, int BonesAfter);
    public sealed record Result(bool Ok, string NewBone, ushort Tag, IReadOnlyList<FileResult> Files, string? Error);

    private const int CompBlendWeights = 1;
    private const int CompBlendIndices = 2;

    /// <summary>RAGE bone id for a vehicle bone name.</summary>
    public static ushort BoneTag(string name)
    {
        uint h = 0;
        foreach (var ch in name.ToUpperInvariant())
        {
            h = (h << 4) + ch;
            uint x = h & 0xF0000000u;
            if (x != 0) h ^= x >> 24;
            h &= ~x;
        }
        return (ushort)((h % 0xFE8Fu) + 0x170u);
    }

    /// <summary>Lower-case letters, digits and underscores — what the game's
    /// name hash and every tool out there expect of a bone name.</summary>
    public static string SanitizeBoneName(string raw)
    {
        var sb = new StringBuilder();
        foreach (var ch in raw ?? "")
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ' || ch == '-') sb.Append('_');
        }
        return sb.ToString();
    }

    /// <summary>Split on the car the editor loaded: both <c>&lt;stem&gt;.yft</c>
    /// and <c>&lt;stem&gt;_hi.yft</c> when present, so the streamed model and
    /// the detail model agree.</summary>
    public static Result SplitVehicle(string anyYftPath, string sourceBone, string newBone, Box box, Action<string>? log = null)
        => SplitVehicle(anyYftPath, sourceBone, newBone, new Selection(box, null), log);

    public static Result SplitVehicle(string anyYftPath, string sourceBone, string newBone, Selection sel, Action<string>? log = null)
    {
        var files = new List<FileResult>();
        newBone = SanitizeBoneName(newBone);
        if (newBone.Length == 0) return new Result(false, newBone, 0, files, "The new bone needs a name.");
        try
        {
            var dir = Path.GetDirectoryName(anyYftPath) ?? "";
            var stem = Path.GetFileNameWithoutExtension(anyYftPath);
            if (stem.EndsWith("_hi", StringComparison.OrdinalIgnoreCase)) stem = stem[..^3];
            var targets = new[] { Path.Combine(dir, stem + ".yft"), Path.Combine(dir, stem + "_hi.yft") }
                .Where(File.Exists).ToList();
            if (targets.Count == 0) targets.Add(anyYftPath);
            ushort tag = 0;
            foreach (var f in targets)
            {
                var r = SplitFile(f, sourceBone, newBone, sel, log);
                files.Add(r);
                tag = r.Tag;
            }
            return new Result(true, newBone, tag, files, null);
        }
        catch (Exception ex)
        {
            FosLogger.Warn("vehanim", "bone split failed", ex);
            return new Result(false, newBone, 0, files, ex.Message);
        }
    }

    public static FileResult SplitFile(string yftPath, string sourceBone, string newBone, Box box, Action<string>? log = null)
        => SplitFile(yftPath, sourceBone, newBone, new Selection(box, null), log);

    public static FileResult SplitFile(string yftPath, string sourceBone, string newBone, Selection sel, Action<string>? log = null)
    {
        var yft = DrawableOptimizer.LoadResource<YftFile>(yftPath);
        var frag = yft.Fragment ?? throw new InvalidDataException("Not a fragment (.yft).");
        var dr = frag.Drawable ?? throw new InvalidDataException("Fragment has no drawable.");
        var skel = dr.Skeleton ?? throw new InvalidDataException("This model has no skeleton — nothing to split.");
        var bones = skel.Bones?.Items ?? throw new InvalidDataException("Skeleton has no bones.");

        int srcIdx = Array.FindIndex(bones, b => string.Equals(b?.Name, sourceBone, StringComparison.OrdinalIgnoreCase));
        if (srcIdx < 0) throw new InvalidDataException($"Bone '{sourceBone}' is not in {Path.GetFileName(yftPath)}.");
        if (bones.Any(b => string.Equals(b?.Name, newBone, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"A bone called '{newBone}' already exists — pick another name.");
        if (bones.Length >= 250) throw new InvalidDataException("This skeleton is at the bone limit (the fragment counts bones in a byte).");
        var tag = BoneTag(newBone);
        if (bones.Any(b => b != null && b.Tag == tag))
            throw new InvalidDataException($"The name '{newBone}' hashes to a tag another bone already uses — pick a different name.");

        int newIdx = bones.Length;

        // ── 1. Skeleton via XML round trip ─────────────────────────────
        var newSkel = AppendBone(skel, srcIdx, newIdx, newBone, tag);
        dr.Skeleton = newSkel;
        log?.Invoke($"{Path.GetFileName(yftPath)}: bone [{newIdx}] {newBone} tag {tag} under {bones[srcIdx].Name}");

        // ── 2. Fragment bone transforms ────────────────────────────────
        var bt = frag.BoneTransforms;
        if (bt?.Items != null)
        {
            var items = bt.Items.ToList();
            items.Add(srcIdx < items.Count ? items[srcIdx] : items[^1]);
            bt.Items = items.ToArray();
            bt.ItemCount1 = (byte)items.Count;
            bt.ItemCount2 = (byte)items.Count;
        }

        // ── 3. Re-bind vertices (every LOD) ────────────────────────────
        int moved = 0, geoms = 0;
        foreach (var model in dr.AllModels ?? Array.Empty<DrawableModel>())
        {
            if (model?.Geometries == null) continue;
            foreach (var geom in model.Geometries)
            {
                int n = RebindGeometry(geom, srcIdx, newIdx, sel);
                if (n > 0) { moved += n; geoms++; }
            }
        }

        // ── 4. Damaged drawable, when it carries its own skeleton/geometry ──
        // CodeWalker names the damaged model differently across versions —
        // find it by name so a rename never silently skips it.
        var dmg = new[] { "DrawableDamaged", "Drawable2", "DamagedDrawable" }
            .Select(n => frag.GetType().GetProperty(n)?.GetValue(frag) as FragDrawable)
            .FirstOrDefault(d => d != null);
        if (dmg != null)
        {
            if (dmg.Skeleton != null && dmg.Skeleton.Bones?.Items?.Length == bones.Length)
                dmg.Skeleton = AppendBone(dmg.Skeleton, srcIdx, newIdx, newBone, tag);
            foreach (var model in dmg.AllModels ?? Array.Empty<DrawableModel>())
            {
                if (model?.Geometries == null) continue;
                foreach (var geom in model.Geometries)
                {
                    int n = RebindGeometry(geom, srcIdx, newIdx, sel);
                    if (n > 0) { moved += n; geoms++; }
                }
            }
        }

        // ── Backup + save ──────────────────────────────────────────────
        string? backup = yftPath + ".bak";
        if (!File.Exists(backup)) File.Copy(yftPath, backup); else backup = null;
        File.WriteAllBytes(yftPath, yft.Save());
        log?.Invoke($"{Path.GetFileName(yftPath)}: moved {moved} vertices in {geoms} geometries");
        return new FileResult(yftPath, backup, newIdx, tag, moved, geoms, newIdx + 1);
    }

    private static Skeleton AppendBone(Skeleton skel, int srcIdx, int newIdx, string newBone, ushort tag)
    {
        var sb = new StringBuilder(64 * 1024);
        sb.Append("<Skeleton>\n");
        skel.WriteXml(sb, 1);
        sb.Append("</Skeleton>");
        var doc = new XmlDocument();
        doc.LoadXml(sb.ToString());
        var bonesEl = doc.DocumentElement!.SelectSingleNode("Bones") as XmlElement
            ?? throw new InvalidDataException("Skeleton XML has no Bones.");
        var items = bonesEl.SelectNodes("Item")!.Cast<XmlElement>().ToList();
        if (srcIdx >= items.Count) throw new InvalidDataException("Skeleton XML is shorter than the bone list.");

        // Last existing child of the source bone gets the new bone as its next sibling.
        XmlElement? lastChild = null;
        foreach (var it in items)
        {
            if (Val(it, "ParentIndex") == srcIdx.ToString(CultureInfo.InvariantCulture) && Val(it, "SiblingIndex") == "-1")
                lastChild = it;
        }
        if (lastChild != null) SetVal(lastChild, "SiblingIndex", newIdx.ToString(CultureInfo.InvariantCulture));

        var src = items[srcIdx];
        var nb = (XmlElement)src.CloneNode(true);
        nb.SelectSingleNode("Name")!.InnerText = newBone;
        SetVal(nb, "Tag", tag.ToString(CultureInfo.InvariantCulture));
        SetVal(nb, "Index", newIdx.ToString(CultureInfo.InvariantCulture));
        SetVal(nb, "ParentIndex", srcIdx.ToString(CultureInfo.InvariantCulture));
        SetVal(nb, "SiblingIndex", "-1");
        var flags = nb.SelectSingleNode("Flags");
        if (flags != null) flags.InnerText = "RotX, RotY, RotZ, TransX, TransY, TransZ";
        SetVec(nb, "Translation", 0, 0, 0);
        SetQuat(nb, "Rotation", 0, 0, 0, 1);
        SetVec(nb, "Scale", 1, 1, 1);
        bonesEl.AppendChild(nb);

        var result = new Skeleton();
        result.ReadXml(doc.DocumentElement!);
        try { result.BuildIndices(); } catch { }
        try { result.BuildBoneTags(); } catch { }
        try { result.BuildBonesMap(); } catch { }
        try { result.BuildTransformations(); } catch { }
        return result;
    }

    /// <summary>Move the source bone's vertices inside <paramref name="box"/>
    /// onto the new bone. Returns how many vertices moved.</summary>
    private static int RebindGeometry(DrawableGeometry geom, int srcIdx, int newIdx, Selection sel)
    {
        var ids = geom.BoneIds;
        var vd = geom.VertexData;
        var decl = vd?.Info;
        if (ids == null || ids.Length == 0 || vd?.VertexBytes == null || decl == null) return 0;
        if (((uint)decl.Flags & (1u << CompBlendIndices)) == 0) return 0;
        int srcSlot = Array.IndexOf(ids, (ushort)srcIdx);
        if (srcSlot < 0) return 0;

        int offI = decl.GetComponentOffset(CompBlendIndices);
        int offW = ((uint)decl.Flags & (1u << CompBlendWeights)) != 0 ? decl.GetComponentOffset(CompBlendWeights) : -1;
        int stride = vd.VertexStride, vc = vd.VertexCount;
        var bytes = vd.VertexBytes;
        if (offI < 0 || stride <= 0 || vc <= 0) return 0;

        // Pass 1: which source-bone vertices are picked (exact position, else box).
        // Pass 2: a triangle moves only when ALL its corners are picked, and it
        // takes its corners with it — coincident layers of the same object come
        // along, a neighbouring panel that merely shares an edge does not tear.
        var slotOf = new int[vc]; Array.Fill(slotOf, -1);
        var byPos = new bool[vc]; var inBox = new bool[vc];
        bool anyPos = false;
        for (int v = 0; v < vc; v++)
        {
            int o = v * stride;
            if (o + 12 > bytes.Length || o + offI + 3 >= bytes.Length) break;
            int best = 0;
            if (offW >= 0 && o + offW + 3 < bytes.Length)
            {
                int bw = bytes[o + offW];
                for (int k = 1; k < 4; k++) if (bytes[o + offW + k] > bw) { bw = bytes[o + offW + k]; best = k; }
            }
            if (bytes[o + offI + best] != srcSlot) continue;
            slotOf[v] = best;
            var p = new Vector3(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8));
            if (sel.Positions != null && sel.Positions.Contains(Selection.Quantize(p))) { byPos[v] = true; anyPos = true; }
            if (sel.Box.Contains(p)) inBox[v] = true;
        }
        var picked = anyPos ? byPos : inBox;
        // Loose pieces (triangle islands welded by position) move as a whole,
        // and only when every vertex of the island is picked — the same rule
        // the viewport applies, so nothing ever tears at a shared edge.
        var move = new bool[vc];
        var tri = geom.IndexBuffer?.Indices;
        if (tri != null && tri.Length >= 3)
        {
            var weld = new int[vc]; var keys = new Dictionary<(int, int, int), int>();
            for (int v = 0; v < vc; v++)
            {
                int o = v * stride;
                var q = Selection.Quantize(new Vector3(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8)));
                if (!keys.TryGetValue(q, out var w)) { w = keys.Count; keys[q] = w; }
                weld[v] = w;
            }
            var parent = new int[keys.Count]; for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int x, int y) { x = Find(x); y = Find(y); if (x != y) parent[x] = y; }
            for (int t = 0; t + 2 < tri.Length; t += 3)
            {
                int ia = tri[t], ib = tri[t + 1], ic = tri[t + 2];
                if (ia >= vc || ib >= vc || ic >= vc) continue;
                Union(weld[ia], weld[ib]); Union(weld[ib], weld[ic]);
            }
            // An island is picked only if every source-bone vertex in it is picked
            // (and it has at least one). Vertices on other bones don't count.
            var islandOk = new Dictionary<int, bool>();
            for (int v = 0; v < vc; v++)
            {
                if (slotOf[v] < 0) continue;
                int root = Find(weld[v]);
                bool ok = picked[v];
                islandOk[root] = islandOk.TryGetValue(root, out var cur) ? (cur && ok) : ok;
            }
            for (int v = 0; v < vc; v++)
                if (slotOf[v] >= 0 && islandOk.TryGetValue(Find(weld[v]), out var ok) && ok) move[v] = true;
        }
        else
        {
            for (int v = 0; v < vc; v++) move[v] = slotOf[v] >= 0 && picked[v];
        }
        var hits = new List<(int v, int k)>();
        for (int v = 0; v < vc; v++) if (move[v]) hits.Add((v, slotOf[v]));
        if (hits.Count == 0) return 0;

        int newSlot = Array.IndexOf(ids, (ushort)newIdx);
        if (newSlot < 0)
        {
            if (ids.Length >= 255) throw new InvalidDataException("A geometry already references 255 bones — cannot add another.");
            var grown = ids.Concat(new[] { (ushort)newIdx }).ToArray();
            geom.BoneIds = grown;
            geom.BoneIdsCount = (ushort)grown.Length;
            newSlot = grown.Length - 1;
        }
        foreach (var (v, k) in hits)
            bytes[v * stride + offI + k] = (byte)newSlot;
        vd.VertexBytes = bytes;   // same array; re-assign so CW re-packs on Save
        return hits.Count;
    }

    private static string Val(XmlElement item, string name)
        => (item.SelectSingleNode(name) as XmlElement)?.GetAttribute("value") ?? "";
    private static void SetVal(XmlElement item, string name, string value)
    {
        if (item.SelectSingleNode(name) is XmlElement e) e.SetAttribute("value", value);
    }
    private static void SetVec(XmlElement item, string name, float x, float y, float z)
    {
        if (item.SelectSingleNode(name) is not XmlElement e) return;
        e.SetAttribute("x", F(x)); e.SetAttribute("y", F(y)); e.SetAttribute("z", F(z));
    }
    private static void SetQuat(XmlElement item, string name, float x, float y, float z, float w)
    {
        if (item.SelectSingleNode(name) is not XmlElement e) return;
        e.SetAttribute("x", F(x)); e.SetAttribute("y", F(y)); e.SetAttribute("z", F(z)); e.SetAttribute("w", F(w));
    }
    private static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
