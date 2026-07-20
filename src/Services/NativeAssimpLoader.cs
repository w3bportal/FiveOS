// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Runtime.InteropServices;

namespace FiveOS.Services;

/// <summary>
/// Pre-loads <c>assimp.dll</c> by absolute path before any AssimpContext
/// is constructed. AssimpNet's UnmanagedLibrary calls raw
/// <c>LoadLibrary("assimp.dll")</c>, which ignores the directories the
/// .NET single-file host registers via <c>AddDllDirectory</c>. Without
/// this, Motion "Add to timeline" and animation import fail with
/// <c>ERROR_MOD_NOT_FOUND (0x8007007E)</c> even though the native DLL
/// was extracted correctly — cloud jobs still show Succeeded.
/// </summary>
internal static class NativeAssimpLoader
{
    public static void Preload()
    {
        if (!OperatingSystem.IsWindows()) return;

        foreach (var dir in ProbeDirectories())
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var dll = Path.Combine(dir, "assimp.dll");
            if (!File.Exists(dll)) continue;

            try
            {
                NativeLibrary.Load(dll);
                FosLogger.Info("boot", "assimp.dll preloaded from " + dll);
                return;
            }
            catch (Exception ex)
            {
                FosLogger.Warn("boot", "assimp.dll preload failed at " + dll + ": " + ex.Message);
            }
        }

        FosLogger.Warn("boot", "assimp.dll not found in probe paths — animation import will fail");
    }

    private static IEnumerable<string> ProbeDirectories()
    {
        yield return AppContext.BaseDirectory;

        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeDirs)
        {
            foreach (var p in nativeDirs.Split(Path.PathSeparator))
                yield return p;
        }

        var processDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(processDir))
            yield return processDir;
    }
}
