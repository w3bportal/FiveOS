// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.IO;
using Sentry;

namespace FiveOS.Services;

/// <summary>
/// Thin wrapper around the Sentry SDK so the rest of the app talks to
/// a single, safe-to-call entry point. Sentry only initializes when the
/// <c>FIVEOS_SENTRY_DSN</c> environment variable is set — OSS builds,
/// forks, and dev runs without that var stay completely silent (no
/// network, no telemetry, no startup cost). When the var is missing,
/// <see cref="Capture"/> is a cheap no-op so the existing
/// <c>CrashLog.Record</c> + <c>SentryReporter.Capture</c> twin-call
/// pattern in <c>App.xaml.cs</c> doesn't need an env-var check.
/// </summary>
public static class SentryReporter
{
    private static bool s_initialized;

    /// <summary>Reads <c>FIVEOS_SENTRY_DSN</c> and brings up the SDK if
    /// present. Safe to call multiple times — only the first call has
    /// effect. Never throws: a busted DSN, no network, or any other
    /// init failure logs a warning and leaves the rest of the app
    /// running normally.</summary>
    public static void Init()
    {
        if (s_initialized) return;
        s_initialized = true;

        var dsn = Environment.GetEnvironmentVariable("FIVEOS_SENTRY_DSN");
        if (string.IsNullOrWhiteSpace(dsn))
        {
            FosLogger.Info("sentry", "no DSN configured — crash reporting disabled");
            return;
        }

        try
        {
            var ver = typeof(SentryReporter).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            SentrySdk.Init(o =>
            {
                o.Dsn = dsn;
                o.Release = $"fiveos@{ver}";
                o.AutoSessionTracking = true;
                // Privacy defaults: don't ship usernames, IPs, or any
                // request bodies. Stack frames + breadcrumbs only.
                o.SendDefaultPii = false;
                o.AttachStacktrace = true;
                o.MaxBreadcrumbs = 50;
                // We don't run a profiler / replay / tracing on a
                // desktop tool — crash and exception events only.
                o.TracesSampleRate = 0.0;
            });
            FosLogger.Info("sentry", $"crash reporting enabled (release fiveos@{ver})");
            SendOneTimeBootPing(ver);
        }
        catch (Exception ex)
        {
            // Init failures must never bring the app down.
            s_initialized = false;
            FosLogger.Warn("sentry", "init failed", ex);
        }
    }

    /// <summary>One-shot "hello from FiveOS" event so the dashboard lights
    /// up the first time a user enables Sentry — without this the
    /// onboarding page sits on "Waiting for error" until a real crash
    /// happens. Marker file under <c>%AppData%\FiveOS\</c> means we only
    /// send this once per install, so it doesn't burn the user's 5k/mo
    /// free-tier quota on every launch.</summary>
    private static void SendOneTimeBootPing(string ver)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FiveOS");
            Directory.CreateDirectory(dir);
            var marker = Path.Combine(dir, "sentry-pinged.marker");
            if (File.Exists(marker)) return;
            SentrySdk.CaptureMessage($"boot ok — fiveos@{ver}", SentryLevel.Info);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            FosLogger.Info("sentry", "first-time boot ping sent");
        }
        catch
        {
            // Marker / ping failure is non-fatal — Sentry stays initialized
            // and will still report real crashes; we just retry the ping
            // next launch.
        }
    }

    /// <summary>Forwards an exception to Sentry. No-op when Sentry
    /// wasn't initialized (missing DSN, init failure, etc.). Designed
    /// to sit alongside <see cref="CrashLog.Record"/> in the global
    /// unhandled-exception handlers — both can be called unconditionally,
    /// the one without a backend just drops the event.</summary>
    public static void Capture(string source, Exception? ex, string? extra = null)
    {
        if (!s_initialized || ex == null) return;
        try
        {
            SentrySdk.CaptureException(ex, scope =>
            {
                scope.SetTag("source", source ?? "?");
                if (!string.IsNullOrEmpty(extra))
                    scope.SetExtra("note", extra);
            });
        }
        catch
        {
            // Never let the reporter crash the crash-handler.
        }
    }

    /// <summary>Flushes pending events before process exit. Called from
    /// <c>App.OnExit</c> so a crash that fires during shutdown still
    /// has a chance to upload before the runtime tears down.</summary>
    public static void Shutdown()
    {
        if (!s_initialized) return;
        try { SentrySdk.Flush(TimeSpan.FromSeconds(2)); } catch { }
        try { SentrySdk.Close(); } catch { }
        s_initialized = false;
    }
}
