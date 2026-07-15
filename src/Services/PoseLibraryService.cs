// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace FiveOS.Services;

/// <summary>
/// Per-user library of saved pose snapshots. Stored as plain JSON files
/// under %APPDATA%\FiveOS\poses\ so they roam with the user's profile
/// without a database dependency. Each file is one pose; the file name
/// (sans extension) is the human-readable label.
///
/// The schema matches what window.getPose() emits from the viewer so a
/// round-trip is trivial: save = write the blob; load = read + call
/// window.applyPose. No format normalisation in between.
/// </summary>
public static class PoseLibraryService
{
    /// <summary>On-disk schema version stamped into every pose file.
    /// Bump when the file shape changes in a backwards-incompatible
    /// way; add a corresponding migration step in <see cref="Migrate"/>.
    /// v0 = pre-versioning files (no schema_version key); v1 = current.</summary>
    public const int PoseFileSchemaVersion = 1;

    /// <summary>Snapshot listed in the sidebar: file name (display label),
    /// last-write timestamp, source rig name, and the raw JSON path.</summary>
    public record PoseEntry(
        string Slug,
        string DisplayName,
        DateTime SavedAt,
        string? SourceRig,
        int BoneCount,
        string FilePath);

    private static string LibraryRoot
    {
        get
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(roaming, "FiveOS", "poses");
        }
    }

    /// <summary>Ensure the library directory exists. Called by every
    /// read/write so the user never has to "initialise" the library —
    /// the first save just creates it.</summary>
    private static void EnsureLibraryDir()
    {
        Directory.CreateDirectory(LibraryRoot);
    }

    /// <summary>Discover saved poses. Returns an empty list if the
    /// library has never been used. Sorted by most-recently-saved first
    /// so the user's recent work surfaces at the top of the grid.</summary>
    public static List<PoseEntry> List()
    {
        EnsureLibraryDir();
        var result = new List<PoseEntry>();
        foreach (var file in Directory.EnumerateFiles(LibraryRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var fi = new FileInfo(file);
                var slug = Path.GetFileNameWithoutExtension(file);
                var display = SlugToDisplay(slug);
                // Peek at the JSON for source-rig and bone count metadata.
                // Tolerate malformed files — we just skip them silently rather
                // than blocking the whole library on one bad entry.
                string? sourceRig = null;
                int boneCount = 0;
                try
                {
                    using var fs = File.OpenRead(file);
                    using var doc = JsonDocument.Parse(fs);
                    if (doc.RootElement.TryGetProperty("source_rig", out var srEl))
                        sourceRig = srEl.GetString();
                    if (doc.RootElement.TryGetProperty("bone_count", out var bcEl))
                        boneCount = bcEl.GetInt32();
                    else if (doc.RootElement.TryGetProperty("bones", out var bnEl) && bnEl.ValueKind == JsonValueKind.Array)
                        boneCount = bnEl.GetArrayLength();
                }
                catch { /* malformed — still list it so user can delete */ }

                result.Add(new PoseEntry(slug, display, fi.LastWriteTime, sourceRig, boneCount, file));
            }
            catch { /* ignore unreadable */ }
        }
        result.Sort((a, b) => b.SavedAt.CompareTo(a.SavedAt));
        return result;
    }

    /// <summary>Persist a pose JSON blob (the verbatim output of
    /// window.getPose) under the given display name. The label is
    /// slug-encoded for the file name; collisions are resolved by
    /// appending a numeric suffix rather than overwriting (users get
    /// confused when "Save" silently replaces).</summary>
    public static PoseEntry Save(string displayName, string poseJson, string? sourceRig = null)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name cannot be empty.", nameof(displayName));
        if (string.IsNullOrWhiteSpace(poseJson))
            throw new ArgumentException("Pose JSON cannot be empty.", nameof(poseJson));

        EnsureLibraryDir();
        var baseSlug = DisplayToSlug(displayName);
        if (baseSlug.Length == 0) baseSlug = "pose";

        var slug = baseSlug;
        var path = Path.Combine(LibraryRoot, slug + ".json");
        int suffix = 2;
        while (File.Exists(path))
        {
            slug = baseSlug + "_" + suffix;
            path = Path.Combine(LibraryRoot, slug + ".json");
            suffix++;
            if (suffix > 9999) throw new IOException("Too many name collisions; give the pose a unique name.");
        }

        // Re-serialise to inject a source_rig hint + savedAt timestamp
        // without losing any fields the viewer emitted. We round-trip
        // through JsonDocument rather than parsing into a typed shape so
        // any future viewer-side schema changes stay backward-compatible.
        using var inDoc = JsonDocument.Parse(poseJson);
        using var outStream = File.Create(path);
        using var w = new Utf8JsonWriter(outStream, new JsonWriterOptions { Indented = true });

        w.WriteStartObject();
        // SchemaVersion stamp: bump when the pose-file shape changes
        // (e.g. adding new metadata fields, renaming bones map). Read
        // path consults this and migrates forward; absent / 0 means
        // "pre-versioning file" and goes through the legacy path.
        // Stays in sync with PoseFileSchemaVersion below.
        w.WriteNumber("schema_version", PoseFileSchemaVersion);
        // Stamp the metadata FIRST so it's visible at the top of the file
        // when humans open it in a text editor.
        w.WriteString("display_name", displayName);
        if (!string.IsNullOrWhiteSpace(sourceRig))
            w.WriteString("source_rig", sourceRig);
        w.WriteString("saved_at", DateTime.Now.ToString("O"));
        // Now copy every property from the viewer's blob.
        foreach (var prop in inDoc.RootElement.EnumerateObject())
        {
            // Don't double-write fields we just stamped (the version
            // stamp belongs to the wrapper, not the viewer payload).
            if (prop.NameEquals("display_name") || prop.NameEquals("source_rig")
                || prop.NameEquals("saved_at") || prop.NameEquals("schema_version"))
                continue;
            prop.WriteTo(w);
        }
        w.WriteEndObject();
        w.Flush();

        return new PoseEntry(slug, displayName, DateTime.Now, sourceRig,
            inDoc.RootElement.TryGetProperty("bone_count", out var bcEl) ? bcEl.GetInt32() : 0,
            path);
    }

    /// <summary>Read the raw JSON contents for handing back to the viewer
    /// via window.applyPose. Runs schema migration when an older file
    /// shape is detected (rewrites the file in place with the upgraded
    /// schema so subsequent loads skip the migration). The viewer
    /// itself stays the schema authority for the bone payload — we
    /// only touch wrapper-level metadata here.</summary>
    public static string Load(string slug)
    {
        var path = Path.Combine(LibraryRoot, slug + ".json");
        if (!File.Exists(path)) throw new FileNotFoundException("Pose not found.", path);
        var text = File.ReadAllText(path, Encoding.UTF8);
        // Peek at the schema_version field. Absent / 0 means a
        // pre-versioning file; route through Migrate to bring it up
        // to date and rewrite. Future versions newer than ours are
        // accepted unchanged (down-grade tolerance).
        try
        {
            using var doc = JsonDocument.Parse(text);
            var ver = doc.RootElement.TryGetProperty("schema_version", out var vEl) && vEl.ValueKind == JsonValueKind.Number
                ? vEl.GetInt32() : 0;
            if (ver < PoseFileSchemaVersion)
            {
                text = Migrate(text, fromVersion: ver);
                try { File.WriteAllText(path, text, Encoding.UTF8); } catch { /* read-only path is fine */ }
                FosLogger.Info("pose", $"migrated pose '{slug}' v{ver}->{PoseFileSchemaVersion}");
            }
        }
        catch (Exception ex)
        {
            // Malformed file: let the caller see the raw bytes and the
            // viewer error out. Migration shouldn't gate Load.
            FosLogger.Warn("pose", $"migration-probe failed for '{slug}'", ex);
        }
        return text;
    }

    /// <summary>Forward-migrate a pose file from <paramref name="fromVersion"/>
    /// to the current schema. Each `if` handles ONE version bump and
    /// ends by stamping the next number — cascading brings a v0 file
    /// all the way to today's shape. Add cases for future bumps; never
    /// remove old cases (users on long-stale installs still need them).</summary>
    private static string Migrate(string json, int fromVersion)
    {
        // v0 → v1: introduces the schema_version field itself. No
        // data shape change; just stamp the version.
        if (fromVersion < 1)
        {
            using var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("schema_version", 1);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("schema_version")) continue;
                    prop.WriteTo(w);
                }
                w.WriteEndObject();
            }
            json = Encoding.UTF8.GetString(ms.ToArray());
            fromVersion = 1;
        }
        return json;
    }

    /// <summary>Permanently remove a saved pose. Silent no-op if it's
    /// already gone (idempotent for the UI's delete button).</summary>
    public static void Delete(string slug)
    {
        var path = Path.Combine(LibraryRoot, slug + ".json");
        if (File.Exists(path))
        {
            try { File.Delete(path); }
            catch (IOException) { /* file locked — surface to caller next op */ }
        }
    }

    /// <summary>Expose the library root for the UI's "Show in Explorer"
    /// affordance. Doesn't create the directory — pure path math.</summary>
    public static string GetLibraryDirectory() => LibraryRoot;

    /// <summary>"My Cool Pose" -> "my_cool_pose". Drops non-alphanumeric
    /// chars except underscores so the file name is portable across
    /// filesystems (NTFS / ext / APFS all happy).</summary>
    private static string DisplayToSlug(string display)
    {
        var sb = new StringBuilder(display.Length);
        bool lastWasUnderscore = false;
        foreach (var ch in display)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasUnderscore = false;
            }
            else if ((ch == ' ' || ch == '-' || ch == '_') && !lastWasUnderscore && sb.Length > 0)
            {
                sb.Append('_');
                lastWasUnderscore = true;
            }
        }
        // Strip trailing underscore left over from "name " -> "name_".
        while (sb.Length > 0 && sb[^1] == '_') sb.Length--;
        return sb.ToString();
    }

    /// <summary>Best-effort reverse of DisplayToSlug for entries that
    /// were saved without an explicit display_name field (legacy /
    /// hand-placed files).</summary>
    private static string SlugToDisplay(string slug)
    {
        if (string.IsNullOrEmpty(slug)) return "(unnamed)";
        var parts = slug.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
