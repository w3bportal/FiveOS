// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FiveOS.Services;

/// <summary>Which memory axis a streaming warning is about. FXServer warns on
/// both per asset: <see cref="Physical"/> = GPU-resident pages dominated by
/// texture pixel data (fix by shrinking textures); <see cref="Virtual"/> =
/// system pages dominated by geometry / drawable structures (fix by decimating
/// polygons).</summary>
public enum AssetMemKind { Physical, Virtual }

/// <summary>One parsed "oversized asset" warning lifted out of a txAdmin /
/// FXServer console log. <see cref="FileName"/> is the bare stream file name
/// (no path); <see cref="Ext"/> is lower-case with a leading dot (".ytd").</summary>
public sealed record TxAdminWarning(
    string ResourceName,
    string FileName,
    string Ext,
    float SizeMiB,
    AssetMemKind MemKind,
    bool OversizedSentence,
    string RawLine);

/// <summary>
/// Parses the streaming-asset warnings FXServer prints to the txAdmin / server
/// console. The engine's format string (citizen-server-impl,
/// ResourceStreamComponent::ValidateSize) is:
///
///   ^%dAsset %s/%s uses %s MiB of %s memory.%s
///
/// i.e. an optional ^N color code, "Asset &lt;resource&gt;/&lt;file&gt; uses
/// &lt;size&gt; MiB of &lt;physical|virtual&gt; memory.", then an OPTIONAL
/// "Oversized assets can and WILL lead to streaming issues …" sentence (only
/// emitted above 48 MiB, with wording that varies across FXServer versions).
/// Lines may also carry a leading "[resources:&lt;name&gt;]" channel tag.
///
/// The warning fires above 16 MiB; the Oversized sentence is appended above
/// 48 MiB (see <see cref="MeshThresholds.PhysicalMemWarnBytes"/>). We tolerate
/// stripped color codes (the txAdmin web console strips them) and the optional
/// channel bracket by anchoring the required match at "Asset …" and treating
/// everything after "memory." as a loose optional group.
/// </summary>
public static class TxAdminLogParser
{
    // Required part stops at "memory."; the trailing Oversized sentence is an
    // optional group so &lt;=48 MiB warnings (which omit it) still match.
    private static readonly Regex WarnRegex = new(
        @"Asset\s+(?<res>[^/\s]+)/(?<file>\S+?\.(?<ext>[A-Za-z0-9]+))\s+uses\s+" +
        @"(?<size>\d+(?:\.\d+)?)\s+MiB\s+of\s+(?<kind>physical|virtual)\s+memory\." +
        @"(?<oversized>\s+Oversized assets can and WILL lead to streaming issues[^\r\n]*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Extract every streaming warning from a pasted log. Identical assets are
    /// de-duplicated per (resource, file, memKind) — a log usually repeats the
    /// same warning on every restart — keeping the largest reported size and
    /// first-seen order.
    /// </summary>
    public static IReadOnlyList<TxAdminWarning> Parse(string? logText)
    {
        var list = new List<TxAdminWarning>();
        if (string.IsNullOrWhiteSpace(logText)) return list;

        // key → index into list, so we can keep first-seen order but update the
        // stored size when a later line reports a bigger number.
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in WarnRegex.Matches(logText))
        {
            var res = m.Groups["res"].Value;
            var file = Path.GetFileName(m.Groups["file"].Value);
            var ext = "." + m.Groups["ext"].Value.ToLowerInvariant();
            var kind = m.Groups["kind"].Value.Equals("virtual", StringComparison.OrdinalIgnoreCase)
                ? AssetMemKind.Virtual
                : AssetMemKind.Physical;
            float.TryParse(m.Groups["size"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size);
            var oversized = m.Groups["oversized"].Success;

            var key = $"{res}/{file}|{kind}".ToLowerInvariant();
            var warning = new TxAdminWarning(res, file, ext, size, kind, oversized, m.Value.Trim());

            if (seen.TryGetValue(key, out var idx))
            {
                // Duplicate — keep whichever reported the larger footprint.
                if (size > list[idx].SizeMiB) list[idx] = warning;
            }
            else
            {
                seen[key] = list.Count;
                list.Add(warning);
            }
        }

        return list;
    }
}
