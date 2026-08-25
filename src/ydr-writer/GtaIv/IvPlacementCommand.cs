// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Globalization;
using System.Text;
using CodeWalker.GameFiles;

namespace YdrWriter.GtaIv;

/// <summary>The <c>wpl-from-ymap</c> subcommand: turn a GTA V map placement
/// file into a GTA IV one.
///
/// ── Rotation convention ─────────────────────────────────────────────────
/// The one part that had to be established rather than assumed. A .ymap
/// stores an entity's rotation as the INVERSE of its world rotation, and
/// CodeWalker conjugates it on load — <c>--probe-rotation</c> against a real
/// map file shows <c>CEntityDef.rotation</c> and <c>Orientation</c> differing
/// by exactly that sign flip — so <c>Orientation</c> is the true world
/// rotation. A .wpl stores rotations the ordinary way round: GIMS IV reads
/// them as <c>quat(-x,-y,-z,w)</c>, and MAXScript's <c>quat</c> is itself the
/// inverse of the usual convention, so those two flips cancel.
///
/// Both sides therefore speak plain world rotation and the quaternion goes
/// across untouched. Getting this wrong crashes nothing — every rotated
/// object simply faces the wrong way — so <c>--probe-rotation</c> stays
/// available to re-check the numbers, and <c>--flip-rotation</c> conjugates
/// without needing a rebuild.</summary>
internal static class IvPlacementCommand
{
    internal static int Run(string[] args)
    {
        string? input = null, output = null, idePath = null;
        var nameSources = new List<string>();
        bool flip = false, probe = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o": case "--out": if (++i < args.Length) output = args[i]; break;
                case "--ide": if (++i < args.Length) idePath = args[i]; break;
                case "--names": if (++i < args.Length) nameSources.Add(args[i]); break;
                case "--flip-rotation": flip = true; break;
                case "--probe-rotation": probe = true; break;
                default:
                    if (!args[i].StartsWith('-') && input == null) input = args[i];
                    break;
            }
        }

        if (input == null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (!File.Exists(input)) { Console.Error.WriteLine($"not found: {input}"); return 2; }

        output ??= Path.ChangeExtension(input, ".wpl");

        // A .ymap names nothing — it holds archetype hashes, and a hash cannot
        // be turned back into a name. Seed the lookup CodeWalker consults so
        // as many as possible resolve; every one that does is one fewer entry
        // the user has to identify by hand afterwards.
        SeedNames(nameSources, input);

        if (probe) return Probe(input);

        IvPlacementWriter.YmapResult result;
        try { result = IvPlacementWriter.FromYmap(input, flip); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not read {Path.GetFileName(input)}: {ex.Message}");
            return 3;
        }

        foreach (var n in result.Notes) Log(n);
        if (result.Skipped > 0) Log($"{result.Skipped:N0} malformed entity record(s) skipped");
        if (result.Wpl.Objects.Count == 0)
        {
            Console.Error.WriteLine("[ydr-writer] nothing to write");
            return 3;
        }

        result.Wpl.Save(output);
        Log($"wrote {Path.GetFileName(output)} ({new FileInfo(output).Length:N0} bytes)");
        Log($"rotation: {(flip ? "conjugated (--flip-rotation)" : "copied as-is - both formats store world rotation")}");

        if (idePath != null)
        {
            int n = WriteStubIde(input, idePath);
            Log($"wrote {Path.GetFileName(idePath)} — {n:N0} archetype stub(s) to fill in");
        }
        else
        {
            Log("no .ide written — pass --ide <file.ide> for a stub listing every model this "
              + "placement file references");
        }
        return 0;
    }

    /// <summary>Teach CodeWalker's hash-to-name index every model name we can
    /// find, so entities resolve to names instead of bare hashes.
    ///
    /// A .ymap stores archetype hashes and nothing else, and Jenkins is one
    /// way — the only route back to a name is to have hashed that name at some
    /// point. Three sources, cheapest first:
    ///
    ///   * the .ymap's own folder tree, always. A custom map resource ships
    ///     its .ydr/.ydd files next to the .ymap that places them, so their
    ///     file names are exactly the archetype names.
    ///   * any extra folder or newline-separated name list passed with
    ///     --names.
    ///   * a GTA V install passed with --names, whose .rpf archives are walked
    ///     for asset names. That covers .ymaps that place stock props, and is
    ///     the slow path — it reads the whole archive index — so it only runs
    ///     when asked for.</summary>
    private static void SeedNames(List<string> sources, string ymapPath)
    {
        int before = _seeded;

        var own = Path.GetDirectoryName(Path.GetFullPath(ymapPath));
        if (own != null) SeedFromFolder(own);

        foreach (var s in sources)
        {
            if (File.Exists(s)) SeedFromList(s);
            else if (Directory.Exists(s))
            {
                if (LooksLikeGameInstall(s)) SeedFromGameInstall(s);
                else SeedFromFolder(s);
            }
            else Log($"--names: not found, ignoring: {s}");
        }

        if (_seeded > before) Log($"name index: {_seeded:N0} name(s) available for hash lookup");
    }

    private static int _seeded;

    private static readonly string[] AssetExtensions =
        { ".ydr", ".ydd", ".yft", ".ybn", ".ytd", ".ytyp" };

    private static void Learn(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        JenkIndex.Ensure(name.ToLowerInvariant());
        _seeded++;
    }

    private static void SeedFromFolder(string folder)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(f);
                if (Array.IndexOf(AssetExtensions, ext.ToLowerInvariant()) < 0) continue;
                Learn(Path.GetFileNameWithoutExtension(f));
            }
        }
        catch (Exception ex) { Log($"could not scan {folder}: {ex.Message}"); }
    }

    private static void SeedFromList(string file)
    {
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                var n = line.Trim();
                if (n.Length == 0 || n.StartsWith('#')) continue;
                // Tolerate a list of file names as readily as a list of bare
                // model names — both are things people have lying around.
                Learn(Path.GetFileNameWithoutExtension(n));
            }
        }
        catch (Exception ex) { Log($"could not read {file}: {ex.Message}"); }
    }

    private static bool LooksLikeGameInstall(string folder)
        => File.Exists(Path.Combine(folder, "GTA5.exe"))
        || File.Exists(Path.Combine(folder, "GTA5_Enhanced.exe"))
        || Directory.Exists(Path.Combine(folder, "x64"));

    private static void SeedFromGameInstall(string folder)
    {
        Log($"reading asset names from {folder} (this takes a moment)...");
        try
        {
            bool gen9 = File.Exists(Path.Combine(folder, "GTA5_Enhanced.exe"));
            GTA5Keys.LoadFromPath(folder, gen9, null);

            var rpf = new RpfManager();
            // buildIndex:false to match how the rest of FiveOS loads the game —
            // building it costs far more than the names are worth here.
            rpf.Init(folder, gen9, _ => { }, _ => { }, rootOnly: false, buildIndex: false);

            foreach (var kv in rpf.EntryDict)
            {
                if (kv.Value is not RpfFileEntry fe) continue;
                var lower = fe.NameLower ?? "";
                foreach (var ext in AssetExtensions)
                {
                    if (!lower.EndsWith(ext, StringComparison.Ordinal)) continue;
                    Learn(lower[..^ext.Length]);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"could not read the game install: {ex.Message}");
        }
    }

    /// <summary>Print the stored rotation next to the resolved one, for the
    /// first few rotated entities.
    ///
    /// Exists because the rotation convention is the only part of this
    /// conversion that cannot be checked by looking at the output: a wrong
    /// choice still produces a perfectly valid .wpl, just one full of objects
    /// facing the wrong way.</summary>
    private static int Probe(string ymapPath)
    {
        var ymap = new YmapFile();
        ymap.Load(File.ReadAllBytes(ymapPath));
        var entities = ymap.AllEntities;
        if (entities == null || entities.Length == 0) { Console.WriteLine("no entities"); return 0; }

        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine($"{"entity",-30} {"stored (CEntityDef.rotation)",-42} {"Orientation",-42}");
        int shown = 0;
        foreach (var e in entities)
        {
            if (e == null) continue;
            var o = e.Orientation;
            // An identity rotation says nothing about the convention — skip on
            // to one that is actually turned.
            if (Math.Abs(o.X) < 1e-6f && Math.Abs(o.Y) < 1e-6f && Math.Abs(o.Z) < 1e-6f) continue;

            var r = e.CEntityDef.rotation;
            Console.WriteLine($"{Trunc(e.Name ?? $"0x{e.EntityHash:X8}", 30),-30} "
                            + $"{Q(r.X, r.Y, r.Z, r.W),-42} {Q(o.X, o.Y, o.Z, o.W),-42}");
            if (++shown >= 8) break;
        }
        if (shown == 0) Console.WriteLine("(every entity in this .ymap is unrotated)");
        else Console.WriteLine("\nThe columns differing by a sign on x/y/z is the expected result: .ymap stores "
                             + "the inverse rotation and CodeWalker has already undone it, so Orientation "
                             + "is the world rotation a .wpl wants, and it goes across as-is.");
        return 0;

        string Q(float x, float y, float z, float w)
            => $"({x.ToString("F4", inv)}, {y.ToString("F4", inv)}, {z.ToString("F4", inv)}, {w.ToString("F4", inv)})";
        static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
    }

    /// <summary>Write an .ide skeleton naming every distinct model the .ymap
    /// places, so the user gets a checklist instead of a hash hunt.
    ///
    /// Bounds are left at zero deliberately: they belong to the converted
    /// model, and only the .wdr export knows them. Converting each model with
    /// <c>wdr</c> writes a per-model .ide carrying real bounds, whose line can
    /// be pasted over the stub.</summary>
    private static int WriteStubIde(string ymapPath, string idePath)
    {
        var ymap = new YmapFile();
        ymap.Load(File.ReadAllBytes(ymapPath));

        var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in ymap.AllEntities ?? Array.Empty<YmapEntityDef>())
        {
            if (e == null) continue;
            var nm = e.Name;
            // An unresolved name comes back as the hash rendered in decimal, which
            // is useless as an .ide entry - flag it so it stands out in the list.
            seen.Add(string.IsNullOrWhiteSpace(nm) || IvPlacementWriter.IsHashPlaceholder(nm)
                ? $"unknown_{e.EntityHash:x8}"
                : nm.ToLowerInvariant());
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Archetypes referenced by " + Path.GetFileName(ymapPath));
        sb.AppendLine("# Bounds below are placeholders. Convert each model with 'ydr-writer wdr'");
        sb.AppendLine("# and paste in the .ide line it writes, which carries the real bounds.");
        sb.AppendLine("objs");
        foreach (var n in seen)
            sb.AppendLine($"{n}, null, {IvPlacementWriter.DefaultDrawDistance:0.###}, "
                        + $"{IvPlacementWriter.DefaultObjectFlags}, 0, "
                        + "0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null");
        sb.AppendLine("end");
        File.WriteAllText(idePath, sb.ToString(), new UTF8Encoding(false));
        return seen.Count;
    }

    internal const string Usage = """
        usage: ydr-writer wpl-from-ymap <input.ymap> [-o <out.wpl>] [flags]
          Converts a GTA V placement file into a GTA IV one.

          -o, --out <file>     output .wpl (default: alongside the input)
          --ide <file>         also write an .ide stub listing every model referenced
          --names <path>       extra source of model names: a folder of assets, a
                               newline-separated list, or a GTA V install folder
                               (repeatable)
          --flip-rotation      conjugate every rotation (both formats normally agree)
          --probe-rotation     print stored vs. resolved rotations and exit

        Positions and rotations carry across directly — both games use metres on
        a Z-up axis. What does not carry across is the archetype: every model the
        placement file names still has to exist in GTA IV, converted and declared
        in an .ide. Use --ide to get that list.

        A .ymap identifies models by hash, never by name, so names are recovered
        by hashing candidates and matching. The .ymap's own folder is searched
        automatically — which covers a custom map that ships the models it places
        — and --names widens that to stock props.
        """;

    private static void Log(string s) => Console.WriteLine($"[ydr-writer] {s}");
}
