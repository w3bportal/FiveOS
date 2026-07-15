// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.Windows.Controls;

namespace FiveOS.Plugins;

public enum PluginKind { Dll, Html }

/// <summary>
/// Result of successfully loading a plugin folder. Carries the metadata
/// the UI needs to render an addons-list row + a rail entry, plus a lazy
/// factory that builds the hosted view on first activation.
/// </summary>
public sealed class PluginRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }

    /// <summary>Absolute path to the plugin's directory. Used by the
    /// "Open plugins folder" affordance and to resolve relative assets.</summary>
    public required string Directory { get; init; }

    /// <summary>Absolute path to an icon PNG inside the plugin dir (if the
    /// manifest declared one and the file exists), else null.</summary>
    public string? IconPath { get; init; }

    public required PluginKind Kind { get; init; }
    public required string EntryPath { get; init; }

    /// <summary>Builds the WPF view shown in the main pane when this plugin
    /// is activated. Called at most once per plugin per session — the host
    /// caches the returned <see cref="UserControl"/>.</summary>
    public required Func<UserControl> ViewFactory { get; init; }

    /// <summary>True when the manifest's <c>minHostVersion</c> is greater
    /// than the running host. Loadable but flagged in the addons UI so
    /// the user understands why a freshly-dropped plugin doesn't appear.</summary>
    public bool IsIncompatible { get; init; }

    /// <summary>Reason the plugin is unloadable (incompatible host
    /// version, broken manifest, missing entry). Empty string when the
    /// plugin is fine to load.</summary>
    public string IncompatibilityReason { get; init; } = "";
}
