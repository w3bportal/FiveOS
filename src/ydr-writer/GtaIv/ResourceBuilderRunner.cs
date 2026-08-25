// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Diagnostics;

namespace YdrWriter.GtaIv;

/// <summary>Drives the external GTA IV resource compiler that turns
/// OpenFormats text into binary game resources (.odr → .wdr, .otd → .wtd).
///
/// The tool is NOT bundled with FiveOS — it's a third-party redistributable
/// of unclear licence, so we locate a user-supplied copy the same way the
/// Optimize tab handles its external converter: explicit path, then
/// environment, then a conventional folder beside the engine. When none is
/// found the export still succeeds and stops at the OpenFormats stage, which
/// is a perfectly good hand-off into any other IV toolchain.
///
/// Two behaviours of the tool shape this wrapper:
///
///   • It resolves its shader database (Games\IV\OpenFormats\ShaderManager.xml)
///     relative to its OWN directory, not the working directory, so we can
///     invoke it from anywhere — but that folder has to be present beside it.
///   • It has no error handling. Any input it dislikes — a malformed .odr, a
///     missing .mesh, an unsupported texture format, even a path that doesn't
///     exist — terminates the process with STATUS_STACK_BUFFER_OVERRUN
///     (0xC0000409) rather than a message. So we pre-validate everything we
///     can and translate that exit code into a diagnosis instead of letting it
///     surface as a mystery crash.</summary>
internal static class ResourceBuilderRunner
{
    // The tool has no input validation, so bad input surfaces as a raw
    // NTSTATUS crash. Which one you get depends on where the parse went off
    // the rails: a missing referenced file tends to fail fast (0xC0000409),
    // while a short shader-parameter list runs off the end of the token
    // stream and faults (0xC0000005). Both mean the same thing to us.
    private const int StatusStackBufferOverrun = unchecked((int)0xC0000409);
    private const int StatusAccessViolation    = unchecked((int)0xC0000005);

    internal const string ExeName = "ResourceBuilder.exe";
    internal const string EnvVar = "FIVEOS_RESOURCE_BUILDER";

    /// <summary>Relative path of the shader database the tool needs beside it.</summary>
    private const string ShaderDbRelative = @"Games\IV\OpenFormats\ShaderManager.xml";

    internal sealed record Result(bool Ok, string? OutputPath, string Message);

    /// <summary>Find a usable ResourceBuilder.exe, or null.</summary>
    /// <param name="explicitPath">Value of --resource-builder, if given. May
    /// point at the .exe itself or at the folder containing it.</param>
    internal static string? Locate(string? explicitPath)
    {
        foreach (var candidate in Candidates(explicitPath))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var resolved = Normalise(candidate);
            if (resolved != null) return resolved;
        }
        return null;
    }

    private static IEnumerable<string?> Candidates(string? explicitPath)
    {
        yield return explicitPath;
        yield return Environment.GetEnvironmentVariable(EnvVar);
        // Conventional drop-in location next to the shipped engine, so a user
        // can install the tool once and have every export find it.
        var here = AppContext.BaseDirectory;
        yield return Path.Combine(here, "GtaIv", ExeName);
        yield return Path.Combine(here, "Engine", "GtaIv", ExeName);
        yield return Path.Combine(here, "..", "GtaIv", ExeName);
    }

    /// <summary>Wait for the process, bailing out early if it raises a window.
    /// Returns true when it exited on its own; false on timeout or dialog,
    /// with <paramref name="showedDialog"/> distinguishing the two.
    ///
    /// A console tool that opens a window is, for our purposes, hung: this one
    /// only ever does it to report an error nobody can acknowledge.</summary>
    private static bool WaitForExitWithoutDialog(Process proc, int timeoutMs, out bool showedDialog)
    {
        const int PollMs = 100;
        showedDialog = false;
        int waited = 0;
        while (waited < timeoutMs)
        {
            if (proc.WaitForExit(PollMs)) return true;
            waited += PollMs;
            try
            {
                proc.Refresh();
                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    showedDialog = true;
                    return false;
                }
            }
            catch
            {
                // Refresh throws once the process is gone; the next
                // WaitForExit resolves it.
            }
        }
        return proc.WaitForExit(0);
    }

    private static string? Normalise(string candidate)
    {
        try
        {
            if (Directory.Exists(candidate))
            {
                var inDir = Path.Combine(candidate, ExeName);
                return File.Exists(inDir) ? Path.GetFullPath(inDir) : null;
            }
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Compile one OpenFormats file. <paramref name="sourcePath"/> is
    /// an .odr or .otd; the tool writes the compiled resource beside it.</summary>
    internal static Result Compile(string exePath, string sourcePath, int timeoutMs = 120_000)
    {
        if (!File.Exists(sourcePath))
            return new Result(false, null, $"source not found: {sourcePath}");

        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        var outExt = ext switch
        {
            ".odr" => ".wdr",
            ".otd" => ".wtd",
            _ => null,
        };
        if (outExt == null)
            return new Result(false, null, $"not an OpenFormats source: {sourcePath}");

        // The tool dies rather than complaining, so check its own prerequisite
        // first — a missing shader database is otherwise indistinguishable from
        // a malformed model.
        var shaderDb = Path.Combine(Path.GetDirectoryName(exePath) ?? ".", ShaderDbRelative);
        if (!File.Exists(shaderDb))
            return new Result(false, null,
                $"shader database missing: expected {ShaderDbRelative} beside {ExeName}");

        var expected = Path.ChangeExtension(sourcePath, outExt);
        // Delete any previous output so a crashed run can't be mistaken for a
        // successful one by the File.Exists check below.
        try { if (File.Exists(expected)) File.Delete(expected); } catch { /* best effort */ }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            // Not strictly required (the tool resolves its data relative to
            // itself) but keeps any relative paths it does form predictable.
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(Path.GetFullPath(sourcePath));

        int exitCode;
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return new Result(false, null, "could not start " + ExeName);
            // The tool calls pause() on its usage path; closing stdin keeps a
            // misfire from hanging the export forever.
            try { proc.StandardInput.Close(); } catch { /* */ }

            // Some of its error paths raise a MessageBox (it imports
            // MessageBoxW) rather than exiting — e.g. "The software ran into a
            // problem that it couldn't handle. Code: 0x…". FiveOS runs this
            // headless, so nobody is there to click OK: the box would sit
            // there and the export would look frozen until the timeout.
            // Watch for a window appearing and treat it as a hard failure the
            // moment it does.
            if (!WaitForExitWithoutDialog(proc, timeoutMs, out bool showedDialog))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* */ }
                return showedDialog
                    ? new Result(false, null,
                        $"{ExeName} stopped on an error dialog while building " +
                        $"{Path.GetFileName(sourcePath)}. It reports failures through a message box that " +
                        "can't be answered from a background process, so the file was rejected — check " +
                        "the shader presets and that every .mesh and .dds the source references exists.")
                    : new Result(false, null, $"{ExeName} timed out after {timeoutMs / 1000}s");
            }
            exitCode = proc.ExitCode;
        }
        catch (Exception ex)
        {
            return new Result(false, null, $"{ExeName} failed to run: {ex.Message}");
        }

        // Negative exit codes are NTSTATUS crash codes — the process died
        // rather than returning. Every one of them means "it didn't like the
        // input", so give the same actionable diagnosis instead of leaking a
        // bare hex code to the user.
        if (exitCode == StatusStackBufferOverrun || exitCode == StatusAccessViolation || exitCode < 0)
            return new Result(false, null,
                $"{ExeName} rejected {Path.GetFileName(sourcePath)} and crashed (0x{exitCode:X8}); " +
                "it aborts instead of reporting errors. Usual causes: a shader preset whose parameter " +
                "count doesn't match ShaderManager.xml, a .mesh or .dds the source references but that " +
                "isn't on disk, or an unsupported texture format.");

        if (exitCode != 0)
            return new Result(false, null, $"{ExeName} exited with code {exitCode}");

        return File.Exists(expected)
            ? new Result(true, expected, $"built {Path.GetFileName(expected)}")
            : new Result(false, null, $"{ExeName} reported success but produced no {outExt}");
    }
}
