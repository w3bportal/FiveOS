// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using RageLib.Resources;
using RageLib.Resources.GTA4;
using RageLib.Resources.GTA4.PC.Bounds;

namespace YdrWriter.GtaIv;

/// <summary>The <c>wbd</c> subcommand: pack per-prop bounds into one GTA IV
/// bounds dictionary.
///
/// A prop gets its collision from a .wbd keyed by the Jenkins hash of its model
/// name — see <see cref="IvBoundDictionary"/> for why a .wbn cannot do this job.
/// The exporter still writes a .wbn per prop because that is the natural unit to
/// build and inspect; this rolls a set of them into the container the game
/// actually reads.
///
///   ydr-writer wbd out.wbd model1.wbn model2.wbn ...
///
/// The archetype name for each entry is the .wbn's own file stem, which is how
/// the exporter names them, so a set of props converted with matching --name
/// values packs correctly with no extra bookkeeping.</summary>
internal static class IvWbdCommand
{
    internal static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var outPath = args[0];
        var entries = new List<(string Name, PhBound Bound)>();

        foreach (var path in args[1..])
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"not found: {path}");
                return 2;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            try
            {
                var res = new Resource5<IvBoundOwner>();
                res.Load(path);
                var bound = res.ResourceData?.Bound;
                if (bound == null)
                {
                    Console.Error.WriteLine($"{name}: no bound inside - skipped");
                    continue;
                }
                entries.Add((name, bound));
                Log($"{name,-20} {bound.Type,-16} added");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{name}: could not read - {ex.Message}");
                return 3;
            }
        }

        if (entries.Count == 0)
        {
            Console.Error.WriteLine("[ydr-writer] no bounds to pack");
            return 3;
        }

        var dict = IvBoundDictionary.Build(entries);
        dict.Save(outPath);

        // Read it straight back. A dictionary the reader cannot walk is a
        // dictionary the game cannot walk, and that failure is silent in game -
        // the prop simply has no collision, which is exactly the symptom this
        // whole command exists to fix.
        var check = new Resource5<IvBoundDictionary>();
        check.Load(outPath);
        var back = check.ResourceData;
        int count = (int)(back?.Count ?? 0);

        Log($"wrote {Path.GetFileName(outPath)} ({new FileInfo(outPath).Length:N0} bytes)");
        Log($"version   {check.Version}   entries {count}");
        if (back?.Hashes?.Entries != null)
        {
            var hashes = new List<uint>();
            for (int i = 0; i < back.Hashes.Entries.Count; i++) hashes.Add(back.Hashes.Entries[i]);
            bool ascending = hashes.SequenceEqual(hashes.OrderBy(h => h));
            Log($"hashes    {(ascending ? "ascending (binary search will find them)" : "NOT ASCENDING - lookups will fail")}");
            foreach (var (e, h) in entries.OrderBy(e => CodeWalker.GameFiles.JenkHash.GenHash(e.Name.ToLowerInvariant()))
                                          .Zip(hashes))
                Log($"  0x{h:X8}  {e.Name}");
            if (!ascending) return 4;
        }
        if (count != entries.Count)
        {
            Console.Error.WriteLine($"[ydr-writer] wrote {entries.Count} but read back {count}");
            return 4;
        }
        return 0;
    }

    internal const string Usage = """
        usage: ydr-writer wbd <out.wbd> <prop1.wbn> [prop2.wbn ...]
          Packs one bound per prop into a GTA IV bounds dictionary.

          A prop's collision is looked up by the Jenkins hash of its model name
          in a .wbd - a .wbn named after a model does nothing, however valid the
          file is. Each entry is keyed by the .wbn's file stem, so convert your
          props with the model name you want and pass the .wbn files here.
        """;

    private static void Log(string s) => Console.WriteLine($"[ydr-writer] {s}");
}
