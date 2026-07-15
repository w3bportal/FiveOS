// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

namespace FiveOS.Plugins;

/// <summary>
/// Services the host exposes to plugins via <see cref="IFiveOSPlugin.Initialize"/>.
/// The interface is intentionally small — every method is a stable
/// affordance plugins can rely on across host versions. New capabilities
/// should be added as new methods/properties (never break existing ones).
/// </summary>
public interface IFiveOSHost
{
    /// <summary>Semver string of the running host (e.g. "0.2.3"). Plugins
    /// can compare against their declared <c>minHostVersion</c> if they
    /// want to gracefully degrade on older hosts.</summary>
    string HostVersion { get; }

    /// <summary>Absolute path to this plugin's own folder under
    /// <c>%AppData%\FiveOS\plugins\&lt;name&gt;\</c>. Use it to resolve
    /// bundled assets (PNGs, JSON tables, etc.) without hard-coding paths.</summary>
    string PluginDirectory { get; }

    /// <summary>Read a plugin-scoped string setting. Keys are namespaced by
    /// the plugin id internally so two plugins can use the same key name
    /// without collision. Returns null if the key was never set.</summary>
    string? GetSetting(string key);

    /// <summary>Persist a plugin-scoped string setting. Pass null to clear.</summary>
    void SetSetting(string key, string? value);

    /// <summary>Show a non-blocking toast / status update to the user. The
    /// host decides where it surfaces (status bar, transient overlay,
    /// etc.) — plugins should treat this as fire-and-forget.</summary>
    void Toast(string message);
}
