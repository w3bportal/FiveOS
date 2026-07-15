// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.IO;
using System.Text;

namespace FiveOS.Services;

/// <summary>
/// Levelled + categorised logger. Rolling 1 MB file at
/// <c>%AppData%\FiveOS\debug.log</c>; mirrored to
/// <see cref="System.Diagnostics.Debug.WriteLine"/> so a dev-mode build
/// surfaces every line in the VS Output window too.
///
/// Pairs with <see cref="CrashLog"/>: CrashLog records unhandled
/// exceptions only (rare events worth retaining); FosLogger records
/// the normal operational trail (frequent, gets recycled).
///
/// Mirrors the JS-side <c>fosTelemetry</c> shape (info / warn / err
/// with a category tag) so log lines from both sides read the same.
/// Categories are free-form — common ones: <c>boot</c>, <c>nav</c>,
/// <c>load</c>, <c>convert</c>, <c>pose</c>, <c>settings</c>,
/// <c>plugin</c>, <c>webview</c>, <c>file</c>.
///
/// Thread-safe: every write acquires a lock around the file
/// roll-and-append. Cheap enough — this isn't on the hot render path.
/// </summary>
public static class FosLogger
{
    private const long MaxBytes = 1024 * 1024;  // 1 MB rolling cap

    private static readonly object s_writeLock = new();

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FiveOS", "debug.log");

    public static void Info(string category, string message) => Write("INFO ", category, message);
    public static void Warn(string category, string message) => Write("WARN ", category, message);
    public static void Err (string category, string message) => Write("ERROR", category, message);

    /// <summary>Convenience for the very common "log + swallowed exception"
    /// pattern: tag the line as a WARN with the exception's message
    /// appended. Use this in place of bare <c>Debug.WriteLine</c> inside
    /// every <c>catch</c> that doesn't re-throw, so we get categorised
    /// triage instead of a stream of unlabelled text.</summary>
    public static void Warn(string category, string message, Exception ex)
        => Warn(category, $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string category, string message)
    {
        var line = $"[{DateTime.UtcNow:HH:mm:ss.fff}] {level} [{category}] {message}";
        // Always mirror to the IDE Output window — useful during dev,
        // costs nothing in Release (debugger detached -> no-op).
        try { System.Diagnostics.Debug.WriteLine(line); } catch { }

        lock (s_writeLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Roll if oversized — keep the most recent half of bytes
                // and append today's line to it. Simple, predictable,
                // doesn't fall over on disk-full or AV locks.
                if (File.Exists(LogPath))
                {
                    var info = new FileInfo(LogPath);
                    if (info.Length > MaxBytes)
                    {
                        try
                        {
                            using var fs = File.OpenRead(LogPath);
                            fs.Seek(info.Length - (MaxBytes / 2), SeekOrigin.Begin);
                            using var sr = new StreamReader(fs);
                            var tail = sr.ReadToEnd();
                            File.WriteAllText(LogPath, tail, Encoding.UTF8);
                        }
                        catch { /* fall through to plain append */ }
                    }
                }
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logger failures must never propagate. The IDE Output
                // mirror above is the fallback when disk is unwritable.
            }
        }
    }
}
