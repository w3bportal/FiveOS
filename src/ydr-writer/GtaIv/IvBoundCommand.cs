// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Globalization;
using RageLib.Resources.Common;
using RageLib.Resources.GTA4;
using RageLib.Resources.GTA4.PC.Bounds;

namespace YdrWriter.GtaIv;

/// <summary>Debug subcommands for GTA IV collision resources.
///
/// <c>dump-wbn</c> parses a .wbn/.wbd and prints its bound tree;
/// <c>rebuild-wbn</c> parses one and writes it straight back out. The rebuild
/// is the load-bearing test for the vendored writer: if a shipping Rockstar
/// bound survives a parse/serialise round trip with its structure and sizes
/// intact, the block layouts and the page allocator are right.</summary>
internal static class IvBoundCommand
{
    internal static int RunDump(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("usage: ydr-writer dump-wbn <file.wbn>"); return 2; }
        var path = args[0];
        if (!File.Exists(path)) { Console.Error.WriteLine($"not found: {path}"); return 2; }

        var res = new Resource5<IvBoundOwner>();
        res.Load(path);
        Console.WriteLine($"version   {res.Version}");
        Console.WriteLine($"flags     0x{res.Flags:X8}");
        Console.WriteLine($"system    {res.VirtualData.Length:N0} bytes");
        Console.WriteLine($"graphics  {res.PhysicalData.Length:N0} bytes");
        Describe(res.ResourceData?.Bound, 0);
        return 0;
    }

    internal static int RunRebuild(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ydr-writer rebuild-wbn <in.wbn> <out.wbn>");
            return 2;
        }
        var (inPath, outPath) = (args[0], args[1]);
        if (!File.Exists(inPath)) { Console.Error.WriteLine($"not found: {inPath}"); return 2; }

        var res = new Resource5<IvBoundOwner>();
        res.Load(inPath);
        var beforeFlags = res.Flags;
        var beforeSys = res.VirtualData.Length;

        res.Rebuild();
        res.Save(outPath);

        var check = new Resource5<IvBoundOwner>();
        check.Load(outPath);
        Console.WriteLine($"in   version={res.Version} flags=0x{beforeFlags:X8} system={beforeSys:N0}");
        Console.WriteLine($"out  version={check.Version} flags=0x{check.Flags:X8} system={check.VirtualData.Length:N0}");
        Describe(check.ResourceData?.Bound, 0);
        return 0;
    }

    /// <summary>The <c>dump-wpl</c> subcommand: list what a GTA IV placement
    /// file contains. Names come out as hashes unless a matching .ide is
    /// supplied, because a .wpl only ever stores the hash.</summary>
    internal static int RunDumpWpl(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: ydr-writer dump-wpl <file.wpl> [names.ide]");
            return 2;
        }
        if (!File.Exists(args[0])) { Console.Error.WriteLine($"not found: {args[0]}"); return 2; }

        IvWpl wpl;
        try { wpl = IvWpl.Load(args[0]); }
        catch (Exception ex) { Console.Error.WriteLine($"could not read: {ex.Message}"); return 3; }

        // A .wpl stores only hashes, so resolve them through the .ide when one
        // is offered — that is the whole reason the two files travel together.
        var names = new Dictionary<uint, string>();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            try
            {
                foreach (var o in IvIde.Load(args[1]).Objects)
                    names[CodeWalker.GameFiles.JenkHash.GenHash(o.Name.ToLowerInvariant())] = o.Name;
                Console.WriteLine($"names from {Path.GetFileName(args[1])}: {names.Count}");
            }
            catch (Exception ex) { Console.WriteLine($"(could not read the .ide: {ex.Message})"); }
        }

        Console.WriteLine($"version  {wpl.Version}");
        foreach (var (name, count) in wpl.Sections())
            Console.WriteLine($"  {name,-8} {count,6:N0}");

        Console.WriteLine($"\nplaced objects: {wpl.Objects.Count:N0}");
        foreach (var o in wpl.Objects.Take(15))
        {
            var label = names.TryGetValue(o.NameHash, out var n) ? n : $"0x{o.NameHash:X8}";
            Console.WriteLine($"  {label,-28} ({o.Position.X,10:F2},{o.Position.Y,10:F2},{o.Position.Z,8:F2})");
        }
        if (wpl.Objects.Count > 15) Console.WriteLine($"  … {wpl.Objects.Count - 15:N0} more");
        return 0;
    }

    /// <summary>The <c>rebuild-wpl</c> subcommand: load a placement file and
    /// write it straight back out, reporting whether the result is byte
    /// identical.
    ///
    /// This is the load-bearing test for the .wpl reader/writer. Sections the
    /// reader doesn't model are carried through verbatim, so a faithful round
    /// trip of one of Rockstar's own files is what proves both the header
    /// layout (including the swapped blok/sect5 count slots) and the record
    /// sizes are right.</summary>
    internal static int RunRebuildWpl(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ydr-writer rebuild-wpl <in.wpl> <out.wpl>");
            return 2;
        }
        if (!File.Exists(args[0])) { Console.Error.WriteLine($"not found: {args[0]}"); return 2; }

        try
        {
            var wpl = IvWpl.Load(args[0]);
            wpl.Save(args[1]);

            var a = File.ReadAllBytes(args[0]);
            var b = File.ReadAllBytes(args[1]);
            Console.WriteLine($"objects   {wpl.Objects.Count:N0}");
            Console.WriteLine($"in        {a.Length:N0} bytes");
            Console.WriteLine($"out       {b.Length:N0} bytes");

            if (a.Length != b.Length)
            {
                Console.WriteLine("result    SIZE DIFFERS");
                return 4;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    Console.WriteLine($"result    DIFFERS at byte {i}");
                    return 4;
                }
            }
            Console.WriteLine("result    BYTE-IDENTICAL");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"failed: {ex.Message}");
            return 3;
        }
    }

    /// <summary>Render a bound's surface ids. Worth printing because a wrong
    /// id is invisible in a viewer — the geometry still draws — but changes
    /// friction and footstep response in game.</summary>
    private static string Materials(SimpleArray<PhBoundMaterial>? mats)
    {
        // SimpleArray's enumerator is non-generic and its backing field is
        // internal; the indexer is the accessible typed route.
        if (mats == null || mats.Count == 0) return "";
        var ids = new List<byte>(mats.Count);
        for (int i = 0; i < mats.Count; i++) ids.Add(mats[i].MaterialId);
        return $"  surfaceIds=[{string.Join(",", ids)}]";
    }

    private static void Describe(PhBound? bound, int depth)
    {
        var pad = new string(' ', depth * 2);
        if (bound == null) { Console.WriteLine($"{pad}(no bound)"); return; }

        var inv = CultureInfo.InvariantCulture;
        Console.Write($"{pad}{bound.Type}");
        Console.Write($"  margin=({bound.Margin.X.ToString("F3", inv)},{bound.Margin.Y.ToString("F3", inv)},{bound.Margin.Z.ToString("F3", inv)})");
        Console.WriteLine($"  aabb=({bound.BoundingBoxMin.X.ToString("F2", inv)},{bound.BoundingBoxMin.Y.ToString("F2", inv)},{bound.BoundingBoxMin.Z.ToString("F2", inv)})"
                        + $"..({bound.BoundingBoxMax.X.ToString("F2", inv)},{bound.BoundingBoxMax.Y.ToString("F2", inv)},{bound.BoundingBoxMax.Z.ToString("F2", inv)})");

        switch (bound)
        {
            case PhBoundComposite composite:
                var kids = composite.Bounds;
                Console.WriteLine($"{pad}  children: {kids?.Count ?? 0}");
                if (kids != null)
                    foreach (var kid in kids)
                        Describe(kid, depth + 1);
                break;

            // PhBoundBVH derives from PhBoundGeometry, so it has to be matched
            // first or every BVH would report as a plain geometry bound.
            case PhBoundBVH bvh:
                Console.WriteLine($"{pad}  verts={bvh.Vertices?.Count ?? 0}  prims={bvh.Primitives?.Count ?? 0}"
                                + $"  mats={bvh.Materials?.Count ?? 0}");
                break;

            case PhBoundGeometry geometry:
                Console.WriteLine($"{pad}  verts={geometry.Vertices?.Count ?? 0}  prims={geometry.Primitives?.Count ?? 0}"
                                + $"  mats={geometry.Materials?.Count ?? 0}{Materials(geometry.Materials)}");
                break;
        }
    }
}
