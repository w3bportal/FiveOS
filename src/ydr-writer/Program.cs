// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using YdrWriter;

// Tiny entry-point. The conversion logic lives in Converter.cs.
//
// Usage:
//   ydr-writer <input.glb> -o <out_dir> --name <asset_name>
//             [--up auto|y_up|z_up] [--no-collision] [--no-ytyp]
//             [--collision-mat WOOD] [--scale 1.0] [--pos x,y,z] [--rot x,y,z]
//
// Layout matches the args FiveOS's EngineRunner sends.

if (args.Length < 1 || args[0] == "--help" || args[0] == "-h")
{
    Console.Error.WriteLine(
        "usage: ydr-writer <input> -o <out_dir> --name <name> [flags]\n" +
        "  Inputs: .obj .glb .gltf .fbx .dae .ply .stl\n" +
        "  Debug:  ydr-writer dump-ybn <in.ybn>   # writes XML to stdout");
    return args.Length == 0 ? 2 : 0;
}

// Debug subcommand: load a .ybn through CW.Core and emit its XML
// to stdout. Used to diff our output against known-working YBNs.
if (args[0] == "dump-ybn")
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: dump-ybn <file.ybn>"); return 2; }
    var ybnPath = args[1];
    if (!File.Exists(ybnPath)) { Console.Error.WriteLine($"not found: {ybnPath}"); return 2; }
    var bytes = File.ReadAllBytes(ybnPath);
    var ybn = new CodeWalker.GameFiles.YbnFile();
    ybn.Load(bytes);
    Console.Out.Write(CodeWalker.GameFiles.YbnXml.GetXml(ybn));
    return 0;
}

// Debug subcommand: load a .ydr and print drawable bounding info +
// LOD distances. Used to chase frustum-culling bugs.
if (args[0] == "dump-ydr-info")
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: dump-ydr-info <file.ydr>"); return 2; }
    var ydrPath = args[1];
    if (!File.Exists(ydrPath)) { Console.Error.WriteLine($"not found: {ydrPath}"); return 2; }
    var ydr = new CodeWalker.GameFiles.YdrFile();
    ydr.Load(File.ReadAllBytes(ydrPath));
    var d = ydr.Drawable;
    if (d == null) { Console.Error.WriteLine("no Drawable"); return 0; }
    Console.WriteLine($"Name              {d.Name}");
    Console.WriteLine($"BoundingBoxMin    ({d.BoundingBoxMin.X:F3}, {d.BoundingBoxMin.Y:F3}, {d.BoundingBoxMin.Z:F3})");
    Console.WriteLine($"BoundingBoxMax    ({d.BoundingBoxMax.X:F3}, {d.BoundingBoxMax.Y:F3}, {d.BoundingBoxMax.Z:F3})");
    Console.WriteLine($"BoundingCenter    ({d.BoundingCenter.X:F3}, {d.BoundingCenter.Y:F3}, {d.BoundingCenter.Z:F3})");
    Console.WriteLine($"SphereRadius      {d.BoundingSphereRadius:F3}");
    Console.WriteLine($"LodDist High/M/L/V  {d.LodDistHigh:F1} / {d.LodDistMed:F1} / {d.LodDistLow:F1} / {d.LodDistVlow:F1}");
    var dm = d.DrawableModels;
    Console.WriteLine($"Models High={dm?.High?.Length ?? 0}  Med={dm?.Med?.Length ?? 0}  Low={dm?.Low?.Length ?? 0}  VLow={dm?.VLow?.Length ?? 0}");
    return 0;
}

// Debug subcommand: load a .ydr through CW.Core and dump its
// embedded Drawable.Bound as YBN-style XML. Reports `<NULL/>`
// if no bound is present.
if (args[0] == "dump-ydr-bound")
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: dump-ydr-bound <file.ydr>"); return 2; }
    var ydrPath = args[1];
    if (!File.Exists(ydrPath)) { Console.Error.WriteLine($"not found: {ydrPath}"); return 2; }
    var bytes = File.ReadAllBytes(ydrPath);
    var ydr = new CodeWalker.GameFiles.YdrFile();
    ydr.Load(bytes);
    var bound = ydr.Drawable?.Bound;
    if (bound == null)
    {
        Console.Error.WriteLine("(no embedded Bound on Drawable)");
        return 0;
    }
    var fakeYbn = new CodeWalker.GameFiles.YbnFile { Bounds = bound };
    Console.Out.Write(CodeWalker.GameFiles.YbnXml.GetXml(fakeYbn));
    return 0;
}

// Pack finalize: enumerate every .ydr in a staging stream/ directory
// and produce a single merged .ytyp covering all of them. Called by
// FiveOS's PropPackBuilder when the user clicks Finalize Pack.
if (args[0] == "merge-pack")
{
    return YdrWriter.YtypMerger.Run(args[1..]);
}

// Single-file publish extracts native libs (assimp.dll) to a side
// directory the .NET host adds to the DLL search path via
// AddDllDirectory. AssimpNet bypasses P/Invoke and calls raw
// LoadLibrary("assimp.dll") which only honors the OS default search
// order — that means the extracted dir IS NOT searched and the load
// fails with ERROR_MOD_NOT_FOUND on every machine. Resolve an
// absolute path and pre-load it so AssimpNet uses our handle.
NativeAssimpLoader.Preload();

try
{
    var opts = ConvertOptions.Parse(args);
    return Converter.Run(opts);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[ydr-writer] FATAL {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 1;
}
