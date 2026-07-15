// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace FiveOS.Plugins;

/// <summary>
/// Discovers third-party FiveOS plugins inside <c>%AppData%\FiveOS\plugins\</c>.
/// Each subdirectory is treated as one plugin candidate; we look for a
/// <c>plugin.json</c> manifest pointing at either a .NET DLL implementing
/// <see cref="IFiveOSPlugin"/> or a self-contained HTML entry point.
///
/// Loading is async — the file I/O + JSON parsing for many plugins would
/// otherwise jank the splash. Each plugin's *view* is still built lazily
/// on first activation; discovery just produces the records.
///
/// Security: DLL plugins run with full process trust, so the manager
/// surfaces a trust-decision callback the UI uses to prompt the user
/// before activation. HTML plugins skip the gate (they're sandboxed
/// inside WebView2).
/// </summary>
public static class PluginManager
{
    /// <summary>Per-user plugins folder. Created lazily so the first call
    /// to <see cref="DiscoverAsync"/> after a fresh install doesn't fail
    /// just because nobody made the folder yet.</summary>
    public static string PluginsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FiveOS", "plugins");

    /// <summary>Semver string of the running host. Compared against each
    /// manifest's <c>minHostVersion</c> to gate incompatible plugins.</summary>
    public static string HostVersion =>
        typeof(PluginManager).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Toaster used by every <see cref="FiveOSHost"/> instance.
    /// Wired by MainViewModel during construction so plugins can surface
    /// status messages without needing a reference to the window.</summary>
    public static Action<string>? Toaster { get; set; }

    /// <summary>Async predicate the manager calls before activating a DLL
    /// plugin for the first time. Wired by the UI to a "trust this
    /// plugin?" dialog. Returning false skips activation.</summary>
    public static Func<PluginRecord, Task<bool>>? RequestDllTrust { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Async wrapper around the (still synchronous) discovery
    /// loop. Hides the file I/O behind <c>Task.Run</c> so callers on the
    /// UI thread can <c>await</c> without blocking.</summary>
    public static Task<IReadOnlyList<PluginRecord>> DiscoverAsync()
        => Task.Run(() => Discover());

    /// <summary>Synchronous discovery — also exposed so app startup can
    /// hydrate before the dispatcher pump comes up.</summary>
    public static IReadOnlyList<PluginRecord> Discover()
    {
        var list = new List<PluginRecord>();
        try { Directory.CreateDirectory(PluginsDir); }
        catch (Exception ex) { FiveOS.Services.FosLogger.Warn("plugin", $"couldn't create {PluginsDir}", ex); }
        if (!Directory.Exists(PluginsDir)) return list;

        foreach (var dir in Directory.EnumerateDirectories(PluginsDir))
        {
            try
            {
                var record = LoadPlugin(dir);
                if (record != null)
                {
                    list.Add(record);
                    FiveOS.Services.FosLogger.Info("plugin", $"discovered '{record.Name}' ({record.Kind}) from {Path.GetFileName(dir)}");
                }
            }
            catch (Exception ex)
            {
                FiveOS.Services.FosLogger.Warn("plugin", Path.GetFileName(dir), ex);
            }
        }

        // Stable order so the rail doesn't shuffle between launches.
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static PluginRecord? LoadPlugin(string dir)
    {
        var manifestPath = Path.Combine(dir, "plugin.json");
        if (!File.Exists(manifestPath)) return null;

        PluginManifest? m;
        try { m = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), JsonOpts); }
        catch (JsonException ex)
        {
            FiveOS.Services.FosLogger.Warn("plugin", $"{Path.GetFileName(dir)}: invalid plugin.json", ex);
            return null;
        }
        if (m == null || string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Name)
                      || string.IsNullOrWhiteSpace(m.Entry))
            return null;

        var entryAbs = Path.Combine(dir, m.Entry);
        if (!File.Exists(entryAbs))
        {
            FiveOS.Services.FosLogger.Warn("plugin", $"{Path.GetFileName(dir)}: entry '{m.Entry}' not found");
            return null;
        }

        var ext = Path.GetExtension(m.Entry).ToLowerInvariant();
        var kind = ext switch
        {
            ".dll" => PluginKind.Dll,
            ".html" or ".htm" => PluginKind.Html,
            _ => (PluginKind?)null,
        };
        if (kind == null) return null;

        var iconAbs = string.IsNullOrWhiteSpace(m.Icon) ? null : Path.Combine(dir, m.Icon);
        if (iconAbs != null && !File.Exists(iconAbs)) iconAbs = null;

        // Min-host-version gate. Plugins built for a newer host load as
        // visible-but-disabled records so the user sees them in Settings
        // → Addons with a clear reason.
        bool incompatible = false;
        string reason = "";
        if (!string.IsNullOrWhiteSpace(m.MinHostVersion)
            && CompareSemver(HostVersion, m.MinHostVersion!) < 0)
        {
            incompatible = true;
            reason = $"Requires FiveOS {m.MinHostVersion}+; this is {HostVersion}.";
        }

        var pluginId = m.Id!;
        Func<UserControl> factory = kind == PluginKind.Dll
            ? () => CreateDllView(entryAbs, pluginId, dir)
            : () => new HtmlPluginView(entryAbs, dir);

        return new PluginRecord
        {
            Id          = pluginId,
            Name        = m.Name!,
            Description = m.Description ?? "",
            Version     = m.Version ?? "0.0.0",
            Author      = m.Author ?? "",
            Directory   = dir,
            IconPath    = iconAbs,
            Kind        = kind.Value,
            EntryPath   = entryAbs,
            ViewFactory = factory,
            IsIncompatible = incompatible,
            IncompatibilityReason = reason,
        };
    }

    /// <summary>SHA-256 of the file at <paramref name="path"/> as
    /// lowercase hex. Used for content-based plugin trust: the same
    /// bytes the user approved must be what we load next time. Returns
    /// empty string if the file can't be read — caller treats that as
    /// "trust mismatch" so a vanished DLL doesn't accidentally pass.</summary>
    private static string Sha256Hex(string path)
    {
        try { using var fs = File.OpenRead(path); return Sha256Hex(fs); }
        catch { return ""; }
    }

    /// <summary>Hash an already-open stream from its current position. Used to
    /// hash the exact bytes behind a handle we hold open across the load, so a
    /// swap can't slip between the trust check and Assembly.LoadFrom.</summary>
    private static string Sha256Hex(Stream stream)
    {
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(stream);
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        catch { return ""; }
    }

    /// <summary>Reflection-load a DLL plugin and instantiate its first
    /// public <see cref="IFiveOSPlugin"/> implementation. Errors here
    /// bubble out of the lazy factory so the plugin host can render an
    /// error card instead of crashing.</summary>
    private static UserControl CreateDllView(string dllPath, string pluginId, string pluginDir)
    {
        // ─── Content-hash trust gate ─────────────────────────────────
        // DLL plugins run with full process trust. The old gate stored
        // a single bool per plugin id, so anyone with write access to
        // the plugins folder could swap a trusted DLL for a malicious
        // one and inherit the trust ("trust once, owned forever").
        //
        // Now we record the SHA-256 of the bytes the user approved and
        // re-verify on every load. A rebuild (legitimate) or a swap
        // (tamper) both change the hash → we re-prompt instead of
        // silently loading the new bytes.
        //
        // Bridge for old installs: TrustedDllPluginIds (legacy bool)
        // is still honoured but ONLY for the first load — we hash the
        // file at that moment and immediately upgrade to hash-based
        // trust, so subsequent loads use the new gate.
        // Open the DLL with a deny-write / deny-delete share BEFORE hashing and
        // keep the handle open through Assembly.LoadFrom below. While we hold it
        // the file can't be replaced/renamed/deleted, so the bytes we hash and
        // the bytes LoadFrom reads are guaranteed identical — closes the
        // trust-check→load TOCTOU without giving up LoadFrom's dep resolution.
        FileStream pinned;
        try { pinned = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Couldn't open plugin DLL at {dllPath}: {ex.Message}");
        }
        var currentHash = Sha256Hex(pinned);
        if (string.IsNullOrEmpty(currentHash))
        {
            pinned.Dispose();
            throw new InvalidDataException(
                $"Couldn't hash plugin DLL at {dllPath} — refusing to load untrusted bytes.");
        }

        var storedHash = FiveOS.Services.UserSettings.LoadPluginTrustedHash(pluginId);
        bool trusted = false;
        if (storedHash != null && string.Equals(storedHash, currentHash, StringComparison.OrdinalIgnoreCase))
        {
            // Hash matches — proceed without prompting.
            FiveOS.Services.FosLogger.Info("plugin", $"{pluginId} hash matches stored trust ({currentHash[..12]}...)");
            trusted = true;
        }
        else if (storedHash == null && FiveOS.Services.UserSettings.LoadPluginTrusted(pluginId))
        {
            // Legacy bool grant from before hash verification existed.
            // Upgrade the record to a hash binding silently and keep
            // the user moving — they already trusted these bytes once,
            // and the file is whatever it was when they last ran.
            FiveOS.Services.UserSettings.SavePluginTrustedHash(pluginId, currentHash);
            FiveOS.Services.FosLogger.Info("plugin", $"{pluginId} upgraded legacy trust -> hash ({currentHash[..12]}...)");
            trusted = true;
        }
        else if (storedHash != null)
        {
            FiveOS.Services.FosLogger.Warn("plugin", $"{pluginId} hash mismatch (stored {storedHash[..12]}..., actual {currentHash[..12]}...) — re-prompting");
        }
        if (!trusted)
        {
            // Either no trust on file, or stored hash differs from the
            // bytes on disk (rebuild / swap / tamper). Re-prompt; user
            // explicitly grants the NEW bytes.
            var promptReason = storedHash == null
                ? "first-time trust"
                : "plugin DLL changed since you last trusted it";
            var trust = RequestDllTrust;
            if (trust == null)
                throw new UnauthorizedAccessException(
                    "DLL plugin not trusted and no trust handler is wired.");
            // Synchronous prompt path — the factory is already invoked on
            // the UI thread (rail click → ContentControl realisation), so
            // a Wait here is fine. The handler is responsible for not
            // deadlocking by dispatching to itself.
            var granted = trust(new PluginRecord
            {
                Id = pluginId,
                Name = Path.GetFileName(pluginDir),
                Description = promptReason,
                Version = "",
                Author = "",
                Directory = pluginDir,
                Kind = PluginKind.Dll,
                EntryPath = dllPath,
                ViewFactory = null!,
            }).GetAwaiter().GetResult();
            if (!granted)
                throw new UnauthorizedAccessException(
                    "DLL plugin not trusted. Open Settings → Addons and enable it explicitly.");
            // Persist BOTH the legacy bool (so older code paths still
            // see the binding) and the canonical content hash.
            FiveOS.Services.UserSettings.SavePluginTrusted(pluginId, true);
            FiveOS.Services.UserSettings.SavePluginTrustedHash(pluginId, currentHash);
        }

        Assembly asm;
        try { asm = Assembly.LoadFrom(dllPath); }
        finally { pinned.Dispose(); }  // bytes are read; safe to release the lock
        Type? pluginType = null;
        try
        {
            pluginType = asm.GetTypes().FirstOrDefault(t =>
                !t.IsAbstract && t.IsClass && typeof(IFiveOSPlugin).IsAssignableFrom(t));
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types may have failed to load (missing deps); use what
            // we did get rather than failing the whole plugin.
            pluginType = ex.Types?.FirstOrDefault(t =>
                t != null && !t.IsAbstract && t.IsClass && typeof(IFiveOSPlugin).IsAssignableFrom(t));
        }
        if (pluginType == null)
            throw new InvalidDataException(
                $"No public IFiveOSPlugin implementation in {Path.GetFileName(dllPath)}");

        var instance = (IFiveOSPlugin)Activator.CreateInstance(pluginType)!;
        var host = new FiveOSHost(pluginId, pluginDir, Toaster);
        try { instance.Initialize(host); FiveOS.Services.FosLogger.Info("plugin", $"{pluginId} initialized"); }
        catch (Exception ex) { FiveOS.Services.FosLogger.Warn("plugin", $"{pluginId} Initialize threw", ex); }
        return instance.CreateView();
    }

    /// <summary>Naive 3-segment dotted-int comparison ("0.2.3" vs "0.3").
    /// Missing segments treated as zero, non-numeric segments compared
    /// lexicographically as a last-ditch fallback. Returns negative when
    /// <paramref name="a"/> &lt; <paramref name="b"/>.</summary>
    private static int CompareSemver(string a, string b)
    {
        var aps = a.Split('.', '-');
        var bps = b.Split('.', '-');
        int n = Math.Max(aps.Length, bps.Length);
        for (int i = 0; i < n; i++)
        {
            string ap = i < aps.Length ? aps[i] : "0";
            string bp = i < bps.Length ? bps[i] : "0";
            if (int.TryParse(ap, out var ai) && int.TryParse(bp, out var bi))
            {
                if (ai != bi) return ai.CompareTo(bi);
            }
            else
            {
                int c = string.Compare(ap, bp, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
        }
        return 0;
    }
}
