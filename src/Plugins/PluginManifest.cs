// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Text.Json.Serialization;

namespace FiveOS.Plugins;

/// <summary>
/// JSON shape of <c>plugin.json</c> sitting in each plugin folder. All
/// fields are nullable so a malformed manifest can still be inspected for
/// what's missing rather than throwing on deserialization.
/// </summary>
public sealed class PluginManifest
{
    [JsonPropertyName("id")]          public string? Id { get; set; }
    [JsonPropertyName("name")]        public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("version")]     public string? Version { get; set; }
    [JsonPropertyName("author")]      public string? Author { get; set; }
    [JsonPropertyName("icon")]        public string? Icon { get; set; }

    /// <summary>Filename inside the plugin folder. Extension picks the loader:
    /// <c>.dll</c> → reflect for <see cref="IFiveOSPlugin"/>; <c>.html</c> /
    /// <c>.htm</c> → host the file in a sandboxed WebView2.</summary>
    [JsonPropertyName("entry")]       public string? Entry { get; set; }

    /// <summary>Optional. If set, the host refuses to load the plugin
    /// when its semver is lower. Lets plugin authors gate against host
    /// versions they were tested against (e.g. "0.2.3").</summary>
    [JsonPropertyName("minHostVersion")] public string? MinHostVersion { get; set; }
}
