// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FiveOS.Services;

/// <summary>
/// Tiny per-user JSON-backed prefs blob (%APPDATA%\FiveOS\settings.json).
/// Currently only persists the user-customised top-level tab order; add
/// fields here as more "remember between launches" toggles show up.
/// </summary>
/// <summary>How conversion / optimization output is routed when a server
/// resource folder is configured.</summary>
public enum ServerLayout
{
    /// <summary>The configured folder IS the resource. Stream files all
    /// land in &lt;server&gt;/stream/ and one shared fxmanifest.lua is
    /// kept up to date in the root.</summary>
    Shared = 0,
    /// <summary>Each conversion creates its own &lt;server&gt;/{asset}_resource/
    /// subfolder (mirrors the zip layout, just unzipped).</summary>
    PerAsset = 1,
}

public static class UserSettings
{
    /// <summary>Bump when the on-disk shape changes in a way that
    /// requires a migration step. Read paths consult
    /// <see cref="Blob.SchemaVersion"/>; a mismatch routes through
    /// <see cref="Migrate"/> before the blob is handed back. Unknown
    /// (higher) versions are accepted as-is to avoid clobbering a
    /// future install's settings if the user downgrades.</summary>
    private const int CurrentSchemaVersion = 1;

    internal sealed class Blob
    {
        /// <summary>On-disk schema version. Absent / 0 means "pre-
        /// versioning blob" — treated as v0 and migrated to current
        /// on first read. Stays in sync with
        /// <see cref="CurrentSchemaVersion"/> on every Write.</summary>
        public int SchemaVersion { get; set; }

        public List<string>? TabOrder { get; set; }
        /// <summary>Where single-mode (zip) output lands. Empty / null
        /// falls back to <c>~/Downloads/</c>.</summary>
        public string? SingleOutputFolder { get; set; }
        /// <summary>Optional server resource folder. When non-empty, takes
        /// precedence over <see cref="SingleOutputFolder"/> and writes
        /// loose (un-zipped) files according to <see cref="ServerLayout"/>.</summary>
        public string? ServerResourceFolder { get; set; }
        /// <summary>Folder the Carcols Fixer tab scans. Kept separate from
        /// <see cref="ServerResourceFolder"/> so browsing there never
        /// re-routes conversion output; seeded from it on first use.</summary>
        public string? CarcolsScanFolder { get; set; }
        public ServerLayout ServerLayout { get; set; } = ServerLayout.Shared;
        /// <summary>UTC time of the last update check (ISO-8601), for the
        /// About dialog's "last checked" line. Null = never checked.</summary>
        public string? LastUpdateCheckUtc { get; set; }

        /// <summary>Version tag the user chose "Skip" on in the update
        /// dialog. That version stops auto-prompting on launch (the badge
        /// stays visible); any NEWER release prompts again.</summary>
        public string? SkippedUpdateVersion { get; set; }

        /// <summary>When true, check for updates on startup and prompt if a
        /// newer build is available. When false (default), updates are
        /// manual only via Help → Check for updates.</summary>
        public bool GlobalUpdate { get; set; } = false;

        /// <summary>Path to the reference (scale-comparison) model shown
        /// alongside the user's prop in the 3D preview. Empty / null disables
        /// the reference. Default points at the user-supplied ped_scale.fbx
        /// in their Downloads.</summary>
        /// <summary>GTA V install folder (the one with GTA5.exe). Used by the
        /// Vehicles tab to load shared vehicle textures (vehshare.ytd) so
        /// add-on car bodies render with their real game textures. Empty/null
        /// = auto-detect (Steam / common paths).</summary>
        public string? GtaFolder { get; set; }

        public string? ReferenceModelPath { get; set; }
        public bool ShowReferencePed { get; set; } = true;

        /// <summary>UI complexity tier: 0=Beginner, 1=Standard, 2=Advanced.
        /// Defaults to Beginner (bare-minimum UI) for new users.</summary>
        public int ExperienceLevel { get; set; }
        /// <summary>Set once the user has picked a level (or dismissed the
        /// first-run picker), so we don't prompt again.</summary>
        public bool HasChosenExperienceLevel { get; set; }

        /// <summary>Whether DiscordPresenceService publishes "what's
        /// happening in FiveOS" to the user's Discord profile. Default
        /// on so users get the integration without hunting for it; flip
        /// off in Settings → About if undesired.</summary>
        public bool EnableDiscordPresence { get; set; } = true;

        /// <summary>BCP-47-ish UI language code (e.g. "en", "pt-BR").
        /// Null means "use the OS culture" — resolved on first launch by
        /// <see cref="LocalizationService.ResolveDefaultLanguage"/>.</summary>
        public string? Language { get; set; }

        /// <summary>Whether the activity rail starts in the expanded
        /// (icon + label) state. Default true = labels visible up front so
        /// new users immediately see what each icon means; hover still
        /// expands transiently when unpinned.</summary>
        public bool RailPinned { get; set; } = true;

        /// <summary>Plugin ids the user has enabled. Discovered plugins
        /// in <c>%AppData%\FiveOS\plugins\</c> are listed in Settings →
        /// Addons but only render rail entries / load their views when
        /// their id is in this set.</summary>
        public List<string>? EnabledPluginIds { get; set; }

        /// <summary>Plugin ids the user has explicitly trusted to run as
        /// .NET DLLs. DLL plugins run with full process trust, so the
        /// host pops a one-time confirmation dialog before first enable
        /// and remembers the answer here. HTML plugins skip the gate
        /// because they're sandboxed inside WebView2.
        /// LEGACY field — kept so old installs that trusted plugins
        /// before content-hash verification was added don't lose the
        /// flag. New trust now writes <see cref="TrustedDllPluginHashes"/>;
        /// the legacy bool here only kicks in as a one-time bridge.</summary>
        public List<string>? TrustedDllPluginIds { get; set; }

        /// <summary>Plugin-id → SHA-256 hex of the DLL bytes the user
        /// trusted. Re-verified on every load: if the DLL on disk
        /// hashes to a different value (rebuild, swap, tamper) the
        /// trust is treated as invalid and the prompt re-fires. Closes
        /// the "trust once, owned forever" hole where anyone with
        /// write access to the plugins folder could swap a trusted DLL
        /// for a malicious one and inherit the trust.</summary>
        public Dictionary<string, string>? TrustedDllPluginHashes { get; set; }

        /// <summary>Per-plugin key/value scratch space. Plugins read/write
        /// via <see cref="IFiveOSHost.GetSetting"/>/<c>SetSetting</c>;
        /// each plugin's bucket is its own inner dict so ids namespace
        /// each other automatically.</summary>
        public Dictionary<string, Dictionary<string, string>>? PluginSettings { get; set; }

        /// <summary>Most-recently-opened file paths (models the user picked),
        /// newest first, capped. Feeds the Welcome screen's Recent Files list.</summary>
        public List<string>? RecentFiles { get; set; }

        /// <summary>Whether the Welcome / startup screen shows on launch.
        /// Default true — a Blender-style splash with recent files + quick
        /// tool tiles. Users flip it off via its "Show on startup" checkbox.</summary>
        public bool ShowWelcomeOnStartup { get; set; } = true;

        /// <summary>FiveOS Cloud Motion API base URL (e.g. http://localhost:5216).</summary>
        public string? MotionCloudBaseUrl { get; set; }

        /// <summary>Last email used for FiveOS Cloud Motion login.</summary>
        public string? MotionCloudEmail { get; set; }

        /// <summary>True once the user ticked "don't show these tips again" on the
        /// Motion filming-tips dialog.</summary>
        public bool MotionTipsAcknowledged { get; set; }

        /// <summary>Hidden ops kill-switch for the automatic pre-upload clip
        /// optimization (silently downscale a &gt;1080p/&gt;30fps clip to
        /// 1080p/30 before a Motion upload to shrink the upload and cut our
        /// upstream mocap cost — the user's credit charge is unchanged). Default
        /// true; there is NO UI for this. Flip to false in settings.json only if
        /// a re-encode ever misbehaves.</summary>
        public bool MotionOptimizeUpload { get; set; } = true;
    }

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FiveOS");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static Blob Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return Fresh();
            var loaded = JsonSerializer.Deserialize<Blob>(File.ReadAllText(FilePath)) ?? Fresh();
            // Run forward-migration if the on-disk version is older than
            // what this build knows about. Downgrades (loaded version
            // higher than current) are intentionally left untouched —
            // we'd rather under-read than clobber future fields.
            if (loaded.SchemaVersion < CurrentSchemaVersion)
            {
                var from = loaded.SchemaVersion;
                Migrate(loaded);
                // Persist the upgraded blob immediately so the next Read
                // sees the new schema_version and skips this branch. If
                // we leave it in-memory only, every Read re-migrates and
                // spams the log with the same line.
                Write(loaded);
                FosLogger.Info("settings", $"migrated settings.json schema v{from}->{CurrentSchemaVersion}");
            }
            return loaded;
        }
        catch (Exception ex)
        {
            // Corrupt file (truncated mid-write, edited by hand, etc.)
            // shouldn't take the app down on launch. Bad file stays on
            // disk so the user can recover it manually; in-memory we
            // start fresh.
            FosLogger.Warn("settings", "settings.json unreadable — starting fresh", ex);
            return Fresh();
        }
    }

    private static Blob Fresh() => new Blob { SchemaVersion = CurrentSchemaVersion };

    /// <summary>Forward-migrate an older blob to the current schema.
    /// Each step handles ONE version bump and ends by setting
    /// <see cref="Blob.SchemaVersion"/>; cascading through the chain
    /// brings any legacy file up to today's shape without dropping
    /// data. Add new cases for future bumps; never remove old cases
    /// (users on long-stale installs still go through them).</summary>
    private static void Migrate(Blob b)
    {
        // v0 → v1: introduces the SchemaVersion field itself. No
        // data shape changes; just stamp the version so future bumps
        // have a baseline to migrate from.
        if (b.SchemaVersion < 1) b.SchemaVersion = 1;
    }

    // Serializes concurrent writers so two saves can't interleave.
    private static readonly object _ioGate = new();

    private static void Write(Blob b)
    {
        lock (_ioGate)
        {
            var tmp = FilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Dir);
                // Always stamp current schema on write so an in-memory blob
                // built by Fresh() or freshly migrated lands as a clean
                // self-describing file.
                b.SchemaVersion = CurrentSchemaVersion;
                var json = JsonSerializer.Serialize(b, new JsonSerializerOptions { WriteIndented = true });

                // Atomic write: serialize to a temp file, then swap it in. A
                // crash/power-loss mid-write leaves either the old file or the
                // new one fully intact — never a truncated settings.json that
                // Read() would discard, wiping output paths + plugin-trust hashes.
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
                FosLogger.Info("settings", $"settings.json saved (v{CurrentSchemaVersion})");
            }
            catch (Exception ex)
            {
                // Disk-write failures (read-only roaming profile, AV lock)
                // shouldn't crash the editor — the in-memory blob still
                // reflects the user's edits this session.
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* swallow */ }
                FosLogger.Warn("settings", "settings.json write failed", ex);
            }
        }
    }

    public static List<string>? LoadTabOrder() => Read().TabOrder;

    public static void SaveTabOrder(IEnumerable<string> keys)
    {
        var b = Read();
        b.TabOrder = new List<string>(keys);
        Write(b);
    }

    // ─── Recent files (Welcome screen MRU) ─────────────────────────────
    private const int MaxRecentFiles = 12;

    /// <summary>Recently-opened file paths, newest first (may include paths
    /// that no longer exist — the Welcome screen filters those on render).</summary>
    public static List<string> LoadRecentFiles() => Read().RecentFiles ?? new List<string>();

    /// <summary>Push a path to the front of the recent list (dedup, capped).</summary>
    public static void AddRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var b = Read();
        b.RecentFiles ??= new List<string>();
        b.RecentFiles.RemoveAll(x => string.Equals(x, path, System.StringComparison.OrdinalIgnoreCase));
        b.RecentFiles.Insert(0, path);
        if (b.RecentFiles.Count > MaxRecentFiles)
            b.RecentFiles.RemoveRange(MaxRecentFiles, b.RecentFiles.Count - MaxRecentFiles);
        Write(b);
    }

    public static bool LoadShowWelcomeOnStartup() => Read().ShowWelcomeOnStartup;

    public static void SaveShowWelcomeOnStartup(bool on)
    {
        var b = Read();
        b.ShowWelcomeOnStartup = on;
        Write(b);
    }

    internal static Blob ReadBlob() => Read();
    internal static void WriteBlob(Blob b) => Write(b);

    // ─── Output routing ───────────────────────────────────────────────

    public static string? LoadSingleOutputFolder() => Read().SingleOutputFolder;
    public static string? LoadServerResourceFolder() => Read().ServerResourceFolder;
    public static ServerLayout LoadServerLayout() => Read().ServerLayout;

    /// <summary>Saved GTA V folder, or null to auto-detect.</summary>
    public static string? LoadGtaFolder() => Read().GtaFolder;
    public static void SaveGtaFolder(string? folder)
    {
        var b = Read();
        b.GtaFolder = string.IsNullOrWhiteSpace(folder) ? null : folder;
        Write(b);
    }

    /// <summary>Last update-check time (UTC), or null if never checked.</summary>
    public static DateTime? LoadLastUpdateCheck()
        => DateTime.TryParse(Read().LastUpdateCheckUtc, null,
               System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;

    public static void SaveLastUpdateCheck(DateTime utc)
    {
        var b = Read();
        b.LastUpdateCheckUtc = utc.ToUniversalTime().ToString("o");
        Write(b);
    }

    /// <summary>Version tag the user opted to skip, or null.</summary>
    public static string? LoadSkippedUpdateVersion() => Read().SkippedUpdateVersion;

    public static void SaveSkippedUpdateVersion(string? versionTag)
    {
        var b = Read();
        b.SkippedUpdateVersion = string.IsNullOrWhiteSpace(versionTag) ? null : versionTag;
        Write(b);
    }

    /// <summary>True = prompt for updates on startup; false = manual only.</summary>
    public static bool LoadGlobalUpdate() => Read().GlobalUpdate;

    public static void SaveGlobalUpdate(bool enabled)
    {
        var b = Read();
        b.GlobalUpdate = enabled;
        Write(b);
    }

    public static void SaveSingleOutputFolder(string? path)
    {
        var b = Read();
        b.SingleOutputFolder = string.IsNullOrWhiteSpace(path) ? null : path;
        Write(b);
    }

    public static string? LoadCarcolsScanFolder() => Read().CarcolsScanFolder;

    public static void SaveCarcolsScanFolder(string? path)
    {
        var b = Read();
        b.CarcolsScanFolder = string.IsNullOrWhiteSpace(path) ? null : path;
        Write(b);
    }

    public static void SaveServerResourceFolder(string? path)
    {
        var b = Read();
        b.ServerResourceFolder = string.IsNullOrWhiteSpace(path) ? null : path;
        Write(b);
    }

    public static void SaveServerLayout(ServerLayout layout)
    {
        var b = Read();
        b.ServerLayout = layout;
        Write(b);
    }

    /// <summary>True when a server folder is configured and exists on disk
    /// — output should be routed there as loose files.</summary>
    public static bool IsServerModeActive()
    {
        var p = LoadServerResourceFolder();
        return !string.IsNullOrWhiteSpace(p) && Directory.Exists(p);
    }

    /// <summary>Resolve the single-mode destination, falling back to the
    /// user's Downloads folder when nothing is configured.</summary>
    public static string ResolveSingleOutputFolder()
    {
        var p = LoadSingleOutputFolder();
        if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) return p!;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
    }

    // ─── Reference ped (scale-comparison preview) ─────────────────────

    public static string? LoadReferenceModelPath() => Read().ReferenceModelPath;

    public static void SaveReferenceModelPath(string? path)
    {
        var b = Read();
        b.ReferenceModelPath = string.IsNullOrWhiteSpace(path) ? null : path;
        Write(b);
    }

    public static bool LoadShowReferencePed() => Read().ShowReferencePed;

    public static void SaveShowReferencePed(bool show)
    {
        var b = Read();
        b.ShowReferencePed = show;
        Write(b);
    }

    // ─── Experience level (UI complexity tier) ───────────────────────

    /// <summary>0=Beginner, 1=Standard, 2=Advanced (clamped). Always Advanced.</summary>
    public static int LoadExperienceLevel() => 2;

    public static void SaveExperienceLevel(int level)
    {
        var b = Read();
        b.ExperienceLevel = 2;
        b.HasChosenExperienceLevel = true;
        Write(b);
    }

    public static bool LoadHasChosenExperienceLevel() => Read().HasChosenExperienceLevel;

    // ─── Discord Rich Presence ────────────────────────────────────────

    public static bool LoadEnableDiscordPresence() => Read().EnableDiscordPresence;

    public static void SaveEnableDiscordPresence(bool enable)
    {
        var b = Read();
        b.EnableDiscordPresence = enable;
        Write(b);
    }

    // ─── Third-party plugins ──────────────────────────────────────────

    public static bool LoadPluginEnabled(string id)
        => Read().EnabledPluginIds?.Contains(id) ?? false;

    public static void SavePluginEnabled(string id, bool enable)
    {
        var b = Read();
        b.EnabledPluginIds ??= new List<string>();
        if (enable && !b.EnabledPluginIds.Contains(id))
            b.EnabledPluginIds.Add(id);
        else if (!enable)
            b.EnabledPluginIds.RemoveAll(x => x == id);
        Write(b);
    }

    public static bool LoadPluginTrusted(string id)
        => Read().TrustedDllPluginIds?.Contains(id) ?? false;

    public static void SavePluginTrusted(string id, bool trusted)
    {
        var b = Read();
        b.TrustedDllPluginIds ??= new List<string>();
        if (trusted && !b.TrustedDllPluginIds.Contains(id))
            b.TrustedDllPluginIds.Add(id);
        else if (!trusted)
        {
            b.TrustedDllPluginIds.RemoveAll(x => x == id);
            // Also drop any stored hash so a re-grant requires the full
            // prompt path, not just the legacy bool fallback.
            b.TrustedDllPluginHashes?.Remove(id);
        }
        Write(b);
    }

    /// <summary>Look up the SHA-256 of the DLL the user trusted for
    /// this plugin id. Null when no hash has been stored yet (fresh
    /// install OR a legacy trust that pre-dates hash verification).</summary>
    public static string? LoadPluginTrustedHash(string id)
    {
        var dict = Read().TrustedDllPluginHashes;
        return dict != null && dict.TryGetValue(id, out var h) ? h : null;
    }

    /// <summary>Record the SHA-256 the user explicitly trusted. Pass
    /// null to clear the binding (e.g. on un-trust).</summary>
    public static void SavePluginTrustedHash(string id, string? sha256)
    {
        var b = Read();
        b.TrustedDllPluginHashes ??= new Dictionary<string, string>();
        if (sha256 == null) b.TrustedDllPluginHashes.Remove(id);
        else                b.TrustedDllPluginHashes[id] = sha256;
        Write(b);
    }

    public static string? LoadPluginSetting(string pluginId, string key)
    {
        var dict = Read().PluginSettings;
        if (dict == null) return null;
        return dict.TryGetValue(pluginId, out var bucket) && bucket.TryGetValue(key, out var v) ? v : null;
    }

    public static void SavePluginSetting(string pluginId, string key, string? value)
    {
        var b = Read();
        b.PluginSettings ??= new Dictionary<string, Dictionary<string, string>>();
        if (!b.PluginSettings.TryGetValue(pluginId, out var bucket))
            b.PluginSettings[pluginId] = bucket = new Dictionary<string, string>();
        if (value == null) bucket.Remove(key);
        else bucket[key] = value;
        Write(b);
    }

    // ─── Activity rail (collapsible left nav) ────────────────────────

    public static bool LoadRailPinned() => Read().RailPinned;

    public static void SaveRailPinned(bool pinned)
    {
        var b = Read();
        b.RailPinned = pinned;
        Write(b);
    }

    // ─── UI language ──────────────────────────────────────────────────

    public static string? LoadLanguage() => Read().Language;

    public static void SaveLanguage(string? code)
    {
        var b = Read();
        b.Language = string.IsNullOrWhiteSpace(code) ? null : code;
        Write(b);
    }

    /// <summary>
    /// Resolve the reference model path. Priority:
    ///   1. User-saved override (View → Choose reference model...)
    ///   2. Bundled default at &lt;RuntimeAssets.ViewerDir&gt;/reference/ped_scale.fbx
    ///      — ships inside viewer.zip, extracted on first launch.
    ///
    /// Returns null only when even the bundled fallback isn't on disk
    /// (e.g. someone hand-deleted the extracted folder).
    /// </summary>
    public static string? ResolveReferenceModelPath()
    {
        var saved = LoadReferenceModelPath();
        if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved)) return saved;

        var bundled = Path.Combine(RuntimeAssets.ViewerDir, "reference", "ped_scale.fbx");
        return File.Exists(bundled) ? bundled : null;
    }
}
