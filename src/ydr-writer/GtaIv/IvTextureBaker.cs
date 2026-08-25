// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Diagnostics;
using Assimp;
using CodeWalker.GameFiles;
using CodeWalker.Utils;

namespace YdrWriter.GtaIv;

/// <summary>Turns the GTA V path's baked textures into loose .dds files that
/// GTA IV OpenFormats can reference.
///
/// This deliberately rides on <see cref="TextureBaker.Bake"/> rather than
/// re-implementing texture discovery. That method already resolves the awkward
/// cases — glTF "*N" embedded indices, FBX ".fbm/Image_0.jpg" virtual paths,
/// PBR BaseColor/NormalCamera slots when the legacy diffuse/normal slots are
/// empty, sidecar files next to the model — and getting any of those wrong is
/// how a model ends up untextured. We take its output and write it back out as
/// DDS instead of packing it into a TXD.</summary>
internal static class IvTextureBaker
{
    /// <summary>Texture file names (no directory, no extension stripped) bound
    /// to one material's sampler slots. Null where the material has no map.</summary>
    internal sealed record MaterialMaps(string? Diffuse, string? Normal, string? Specular, bool DiffuseHasAlpha);

    /// <summary>Bake every material's maps to .dds inside
    /// <paramref name="assetDir"/> and report what landed where.</summary>
    /// <param name="assetDir">The model's subfolder — .odr texture references
    /// are relative to the .odr, so files here are addressed as
    /// "&lt;assetName&gt;\&lt;file&gt;.dds" and get embedded into the .wdr.</param>
    internal static List<MaterialMaps> Bake(
        Scene scene, string workDir, string assetName, string sourceDir, string assetDir,
        IReadOnlyDictionary<string, string>? partDiffuseOverrides)
    {
        Directory.CreateDirectory(assetDir);
        var baked = TextureBaker.Bake(scene, workDir, assetName, sourceDir, partDiffuseOverrides);

        var result = new List<MaterialMaps>(baked.Count);
        foreach (var m in baked)
        {
            var diffuse = Emit(m.Diffuse, assetDir, workDir);
            var normal = Emit(m.Normal, assetDir, workDir);
            var spec = Emit(m.Specular, assetDir, workDir);

            // The upstream flag only records that the SOURCE image had an
            // alpha channel — it is set from the PNG colour type, not from any
            // pixel. Committing to it puts every 32-bit-but-opaque texture in
            // GTA IV's alpha draw bucket, which costs depth-write and makes
            // solid props sort against glass. Check the encoded blocks first.
            bool hasAlpha = m.DiffuseHasAlpha;
            if (hasAlpha && diffuse != null
                && IvDds.IsFullyOpaque(Path.Combine(assetDir, diffuse + ".dds")))
            {
                Log($"  '{m.MaterialName}' diffuse has an alpha channel but every texel is opaque — using an opaque shader");
                hasAlpha = false;
            }

            result.Add(new MaterialMaps(diffuse, normal, spec, hasAlpha));
        }
        return result;
    }

    /// <summary>Write one baked texture out as a .dds. Returns the file name
    /// written, or null when there was nothing to write.</summary>
    private static string? Emit(Texture? tex, string assetDir, string workDir)
    {
        if (tex == null) return null;
        var name = string.IsNullOrWhiteSpace(tex.Name) ? "tex" : tex.Name;
        var outPath = Path.Combine(assetDir, name + ".dds");

        byte[] dds;
        try
        {
            dds = DDSIO.GetDDSFile(tex);
        }
        catch (Exception ex)
        {
            Log($"  could not serialise texture '{name}': {ex.Message}");
            return null;
        }
        if (dds.Length == 0) return null;

        File.WriteAllBytes(outPath, dds);

        // The GTA V bake encodes normal maps as BC5 (ATI2 FourCC on the -dx9
        // path). GTA IV predates BC5 and its resource compiler rejects the
        // format outright, so a normal map has to become DXT5.
        //
        // It must NOT get there by transcoding the BC5 file. BC5 keeps only X
        // and Y — Z is meant to be rebuilt in the shader — so decoding it into
        // an RGB target leaves blue at zero, and texconv's -reconstructz does
        // not apply to this conversion. Measured on a flat (128,128,255)
        // normal: BC5 -> DXT5 decodes to B=0, i.e. every normal points into
        // the surface. Going straight from the original image keeps B=255.
        if (NeedsConversion(dds))
        {
            var source = FindSourceImage(workDir, name);
            if (source == null)
            {
                Log($"  dropping map '{name}' — its format is unusable in GTA IV and the source image is gone");
                try { File.Delete(outPath); } catch { /* best effort */ }
                return null;
            }
            var converted = Encode(source, workDir, "DXT5");
            if (converted == null)
            {
                Log($"  dropping map '{name}' — could not encode {Path.GetFileName(source)} to DXT5");
                try { File.Delete(outPath); } catch { /* best effort */ }
                return null;
            }
            File.Copy(converted, outPath, overwrite: true);
        }

        return name;
    }

    /// <summary>Bring a .dds already on disk into a format GTA IV accepts,
    /// rewriting it in place. Returns false when it could not be salvaged.
    ///
    /// Used by the GTA V import path, where the pixels come out of a .ydr's
    /// embedded dictionary and there is no original image to fall back on.
    /// A census of 413 shipping GTA V drawables found 654 DXT1, 373 DXT5,
    /// 34 A8R8G8B8, 13 ATI2 and 1 DXT3 — so about 95% of textures already
    /// arrive in a format IV takes and are left untouched.
    ///
    /// The interesting 5% is BC5/ATI2 normal maps. A single conversion to DXT5
    /// destroys them: BC5 stores only X and Y, so the blue channel decodes to
    /// zero and every normal ends up pointing into the surface (measured on a
    /// real V normal map: blue mean 0.0). Two passes fix it without needing the
    /// source art — expand with -reconstructz to a full RGBA surface first,
    /// which rebuilds Z from X and Y, then block-compress that (same texture:
    /// blue mean 254.1, R/G preserved).</summary>
    internal static bool MakeIvCompatible(string ddsPath, List<string> notes)
    {
        byte[] head;
        try { head = File.ReadAllBytes(ddsPath); } catch { return false; }
        if (head.Length <= 88) return false;

        var fourCc = System.Text.Encoding.ASCII.GetString(head, 84, 4);
        if (fourCc is "DXT1" or "DXT3" or "DXT5") return true;   // already fine

        var work = Path.Combine(Path.GetTempPath(), "ydr-writer", "ivtex-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            bool isTwoChannelNormal = fourCc is "ATI2" or "BC5U";
            string? converted;

            if (isTwoChannelNormal)
            {
                var expanded = RunTexconv(ddsPath, work, "R8G8B8A8_UNORM", reconstructZ: true);
                if (expanded == null)
                {
                    notes.Add($"'{Path.GetFileNameWithoutExtension(ddsPath)}': BC5 normal map could not be expanded — dropped");
                    return false;
                }
                converted = RunTexconv(expanded, Path.Combine(work, "out"), "DXT5", reconstructZ: false);
            }
            else
            {
                // A8R8G8B8, BC7 and friends: a straight block-compress is fine.
                converted = RunTexconv(ddsPath, Path.Combine(work, "out"), "DXT5", reconstructZ: false);
            }

            if (converted == null)
            {
                notes.Add($"'{Path.GetFileNameWithoutExtension(ddsPath)}': format {fourCc.Trim()} is not usable in GTA IV and could not be converted — dropped");
                return false;
            }
            File.Copy(converted, ddsPath, overwrite: true);
            return true;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp */ }
        }
    }

    private static string? RunTexconv(string source, string outDir, string format, bool reconstructZ)
    {
        string texconv;
        try { texconv = TextureBaker.LocateTexconv(); } catch { return null; }
        Directory.CreateDirectory(outDir);

        var args = new List<string> { "-y", "-nologo", "-f", format, "-m", "0", "-dx9" };
        if (reconstructZ) args.Add("-reconstructz");
        args.Add("-o"); args.Add(outDir); args.Add(source);

        var psi = new ProcessStartInfo
        {
            FileName = texconv,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            if (!proc.WaitForExit(30_000)) { try { proc.Kill(true); } catch { } return null; }
            if (proc.ExitCode != 0) return null;
        }
        catch { return null; }

        var outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(source) + ".dds");
        return File.Exists(outPath) ? outPath : null;
    }

    /// <summary>True when a DDS is in a block format GTA IV cannot use.
    ///
    /// Checked as a whitelist rather than by hunting for one bad FourCC: the
    /// encoder labels two-channel output 'ATI2' or 'BC5U' depending on the
    /// path taken, and either would have slipped past a single-value test —
    /// which is exactly what happened, shipping a BC5 normal map that only
    /// looked converted. GTA IV predates everything outside DXT1/3/5.</summary>
    private static bool NeedsConversion(byte[] dds)
    {
        if (dds.Length <= 88) return false;
        var fourCc = System.Text.Encoding.ASCII.GetString(dds, 84, 4);
        return fourCc is not ("DXT1" or "DXT3" or "DXT5");
    }

    /// <summary>Locate the original image the upstream bake decoded before it
    /// encoded the DDS. It writes the source next to its output in the work
    /// directory under the same base name, keeping the image's real extension
    /// (the encoder picks its codec from that, so a JPEG stays .jpg).</summary>
    private static string? FindSourceImage(string workDir, string baseName)
    {
        try
        {
            foreach (var f in Directory.GetFiles(workDir, baseName + ".*"))
                if (!f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    return f;
        }
        catch { /* fall through */ }
        return null;
    }

    /// <summary>Run the bundled texconv over an image to produce a .dds in the
    /// requested block format. Returns the converted file's path, or null on
    /// any failure — callers treat that as "no map" rather than aborting the
    /// export.</summary>
    private static string? Encode(string sourcePath, string workDir, string format)
    {
        string texconv;
        try { texconv = TextureBaker.LocateTexconv(); }
        catch (Exception ex) { Log($"  texconv unavailable: {ex.Message}"); return null; }

        var stage = Path.Combine(workDir, "iv-reencode");
        Directory.CreateDirectory(stage);

        var psi = new ProcessStartInfo
        {
            FileName = texconv,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-y", "-nologo", "-f", format, "-m", "0", "-pow2", "-fl", "11.0", "-dx9", "-o", stage, sourcePath })
            psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            if (!proc.WaitForExit(30_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* */ }
                return null;
            }
            if (proc.ExitCode != 0) return null;
        }
        catch (Exception ex)
        {
            Log($"  texconv re-encode failed: {ex.Message}");
            return null;
        }

        var outPath = Path.Combine(stage, Path.GetFileNameWithoutExtension(sourcePath) + ".dds");
        return File.Exists(outPath) ? outPath : null;
    }

    private static void Log(string s) => Console.WriteLine($"[ydr-writer] {s}");
}
