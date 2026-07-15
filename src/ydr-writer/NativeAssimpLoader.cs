// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Runtime.InteropServices;

namespace YdrWriter;

/// <summary>
/// Resolves and pre-loads <c>assimp.dll</c> by absolute path before any
/// AssimpContext is constructed. AssimpNet's UnmanagedLibrary calls
/// raw <c>LoadLibrary("assimp.dll")</c> (no flags), which uses the OS
/// default search order — that does NOT include the directories the
/// .NET single-file host registers via <c>AddDllDirectory</c>. So when
/// the engine runs from a single-file extraction folder, Windows
/// returns <c>ERROR_MOD_NOT_FOUND (0x8007007E)</c> even though the DLL
/// was correctly extracted. Pre-loading by absolute path puts a
/// matching module name in the process so AssimpNet's later
/// <c>LoadLibrary("assimp.dll")</c> resolves to the same image.
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

            // Use NativeLibrary so .NET tracks the handle and unloads
            // cleanly. AssimpNet's later LoadLibrary("assimp.dll") will
            // see the module is already mapped and return its handle
            // without searching disk.
            try
            {
                NativeLibrary.Load(dll);
                return;
            }
            catch
            {
                // Try the next probe path. If none work, fall through
                // and let AssimpNet surface its own error so the user
                // gets the original diagnostic.
            }
        }
    }

    private static IEnumerable<string> ProbeDirectories()
    {
        // 1. The single-file extraction directory. AppContext.BaseDirectory
        //    points here for a self-extracted single-file app.
        yield return AppContext.BaseDirectory;

        // 2. Whatever directories the .NET host registered for native
        //    DLL probing (covers cases where AppContext.BaseDirectory
        //    is the apphost dir instead of the extraction dir).
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeDirs)
        {
            foreach (var p in nativeDirs.Split(Path.PathSeparator))
                yield return p;
        }

        // 3. The directory the running process was launched from
        //    (mainly for non-bundled / dev builds).
        var processDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(processDir))
            yield return processDir;
    }
}
