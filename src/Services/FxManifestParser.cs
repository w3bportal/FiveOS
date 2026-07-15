// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FiveOS.Services;

/// <summary>
/// Minimal parser for a FiveM resource manifest (<c>fxmanifest.lua</c> /
/// <c>__resource.lua</c>). Phase 2 of the RPF converter reads the
/// <c>data_file</c> directives (e.g. <c>data_file 'PED_METADATA_FILE'
/// 'peds.meta'</c>) and the <c>files { }</c> list to know which loose meta
/// files exist and how the resource declares them — that maps onto the SP
/// <c>content.xml</c> <c>&lt;dataFile&gt;</c> entries.
///
/// This is a deliberately small lexical parser, not a Lua interpreter: it
/// strips comments, then regex-scans for the handful of directives we care
/// about. Manifests that build paths dynamically (string concat, loops) are
/// out of scope — those are rare for streamed-asset resources, which list
/// files literally.
/// </summary>
public static class FxManifestParser
{
    public sealed record DataFileEntry(string Type, string Path);

    public sealed record FxManifest(
        IReadOnlyList<DataFileEntry> DataFiles,
        IReadOnlyList<string> Files,
        bool IsMap,
        string? RawText)
    {
        public static FxManifest Empty { get; } =
            new(Array.Empty<DataFileEntry>(), Array.Empty<string>(), false, null);
    }

    private static readonly string[] ManifestNames = { "fxmanifest.lua", "__resource.lua" };

    // data_file 'TYPE' 'path'   (quotes may be ' or "; spacing flexible).
    // NB: .NET numbers named groups AFTER unnamed ones, so the closing
    // backreferences are \1 (first quote) and \2 (second quote) — not \3/\4.
    private static readonly Regex DataFileRx = new(
        @"data_file\s+(['""])(?<type>.*?)\1\s+(['""])(?<path>.*?)\2",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // files { '...', '...' }  — capture the brace body, then quoted strings.
    private static readonly Regex FilesBlockRx = new(
        @"files\s*\{(?<body>[^}]*)\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // a bare  file 'x'  directive (less common)
    private static readonly Regex SingleFileRx = new(
        @"(?<![\w_])file\s+(['""])(?<path>.*?)\1",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedRx = new(
        @"(['""])(?<v>.*?)\1", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex MapRx = new(
        @"this_is_a_map\s+(['""])\s*yes\s*\1", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Find and parse the manifest in <paramref name="folder"/>, or
    /// <see cref="FxManifest.Empty"/> if none is present / readable.</summary>
    public static FxManifest Load(string folder)
    {
        foreach (var name in ManifestNames)
        {
            var p = Path.Combine(folder, name);
            if (File.Exists(p))
            {
                try { return Parse(File.ReadAllText(p)); }
                catch { return FxManifest.Empty; }
            }
        }
        return FxManifest.Empty;
    }

    public static FxManifest Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return FxManifest.Empty;
        var clean = StripComments(text);

        var dataFiles = new List<DataFileEntry>();
        foreach (Match m in DataFileRx.Matches(clean))
            dataFiles.Add(new DataFileEntry(m.Groups["type"].Value.Trim(), m.Groups["path"].Value.Trim()));

        var files = new List<string>();
        foreach (Match block in FilesBlockRx.Matches(clean))
            foreach (Match q in QuotedRx.Matches(block.Groups["body"].Value))
            {
                var v = q.Groups["v"].Value.Trim();
                if (v.Length > 0) files.Add(v);
            }
        foreach (Match m in SingleFileRx.Matches(clean))
        {
            var v = m.Groups["path"].Value.Trim();
            if (v.Length > 0) files.Add(v);
        }

        var isMap = MapRx.IsMatch(clean);

        return new FxManifest(
            dataFiles,
            files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            isMap,
            text);
    }

    /// <summary>Strip Lua line (<c>--</c>) and block (<c>--[[ ]]</c>) comments
    /// so commented-out directives don't get picked up.</summary>
    private static string StripComments(string s)
    {
        // Block comments first: --[[ ... ]] and --[==[ ... ]==]
        s = Regex.Replace(s, @"--\[(=*)\[.*?\]\1\]", "", RegexOptions.Singleline);
        // Then line comments: -- to end of line.
        s = Regex.Replace(s, @"--[^\n]*", "");
        return s;
    }
}
