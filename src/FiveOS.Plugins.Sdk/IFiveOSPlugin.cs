// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.Windows.Controls;

namespace FiveOS.Plugins;

/// <summary>
/// The contract a .NET FiveOS plugin must implement. Plugin DLLs reference
/// <c>FiveOS.Plugins.Sdk.dll</c> (a tiny stable assembly) and expose a single
/// public type implementing this interface — <c>PluginManager</c> reflects
/// over <c>plugin.json:entry</c> and instantiates the first match.
///
/// Lifecycle:
///   1. The host instantiates the plugin via its parameterless constructor.
///   2. The host calls <see cref="Initialize"/> exactly once with a host
///      reference. Plugins should stash this and use it for settings,
///      toasts, and folder lookups.
///   3. The host calls <see cref="CreateView"/> when the plugin's rail
///      entry is first activated. The returned <see cref="UserControl"/>
///      is cached for the session.
/// </summary>
public interface IFiveOSPlugin
{
    /// <summary>Stable identifier (lowercase, dash-separated). Persisted in
    /// UserSettings to remember enable state and per-plugin preferences.</summary>
    string Id { get; }

    /// <summary>Human-readable display name. Shown in the rail tooltip and
    /// in Settings → Addons.</summary>
    string Name { get; }

    /// <summary>One-line description shown under the name in the addons list.</summary>
    string Description { get; }

    /// <summary>Called once before <see cref="CreateView"/>. Plugins should
    /// stash the host reference and use it later for settings, toasts, etc.
    /// Default implementation does nothing — override only if needed.</summary>
    void Initialize(IFiveOSHost host) { }

    /// <summary>Build the WPF view that will be hosted in the main pane when
    /// this plugin is the active rail entry. Called once per session — the
    /// host caches the returned control.</summary>
    UserControl CreateView();
}
