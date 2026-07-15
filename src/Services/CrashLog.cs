// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.IO;
using System.Text;

namespace FiveOS.Services;

/// <summary>
/// Rolling append-only crash log under <c>%AppData%\FiveOS\crashes.log</c>.
/// Wired from <see cref="App"/>'s global exception handlers
/// (DispatcherUnhandled / UnobservedTaskException / AppDomain.Unhandled)
/// so the 15+ <c>async void</c> event handlers across the View layer
/// don't silently kill the dispatcher with no trace.
///
/// Intentionally tiny + dependency-free: we only call this AFTER something
/// has already gone wrong, so any extra machinery is itself a failure
/// risk. Every method swallows its own I/O exceptions — the worst
/// outcome is "the crash is logged nowhere," which beats "the
/// crash-logger crashes the crash-handler."
/// </summary>
public static class CrashLog
{
    private const long MaxBytes = 256 * 1024;  // 256 KB rolling cap

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FiveOS", "crashes.log");

    public static void Record(string source, Exception? ex, string? extra = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.Append('[').Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")).Append("] ")
              .Append(source ?? "?").AppendLine();
            if (!string.IsNullOrEmpty(extra)) sb.AppendLine(extra);
            if (ex != null)
            {
                sb.Append(ex.GetType().FullName).Append(": ").AppendLine(ex.Message);
                if (!string.IsNullOrEmpty(ex.StackTrace)) sb.AppendLine(ex.StackTrace);
                var inner = ex.InnerException;
                int depth = 0;
                while (inner != null && depth < 5)
                {
                    sb.Append("  caused by ").Append(inner.GetType().FullName)
                      .Append(": ").AppendLine(inner.Message);
                    if (!string.IsNullOrEmpty(inner.StackTrace)) sb.AppendLine(inner.StackTrace);
                    inner = inner.InnerException;
                    depth++;
                }
            }
            sb.AppendLine(new string('-', 60));

            // Roll if the file got large. A simple "keep the last MaxBytes
            // of content" — we read tail, prepend it back, and append the
            // new entry. Not perfectly atomic but the consequence of a
            // mid-write tear is one missing record, not corruption that
            // breaks the next read.
            string tail = "";
            try
            {
                if (File.Exists(LogPath))
                {
                    var info = new FileInfo(LogPath);
                    if (info.Length > MaxBytes)
                    {
                        using var fs = File.OpenRead(LogPath);
                        var offset = info.Length - (MaxBytes / 2);
                        fs.Seek(offset, SeekOrigin.Begin);
                        using var sr = new StreamReader(fs);
                        tail = sr.ReadToEnd();
                    }
                }
            }
            catch { /* fall through — appending is still better than dropping */ }

            if (tail.Length > 0)
                File.WriteAllText(LogPath, tail + sb.ToString(), Encoding.UTF8);
            else
                File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Crash-logger failures must never propagate; the original
            // exception is what matters.
        }
    }
}
