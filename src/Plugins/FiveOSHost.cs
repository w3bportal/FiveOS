// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.IO;
using FiveOS.Plugins;
using FiveOS.Services;

namespace FiveOS.Plugins;

/// <summary>
/// Concrete <see cref="IFiveOSHost"/> implementation handed to each plugin
/// at <see cref="IFiveOSPlugin.Initialize"/> time. Lifetime is per-plugin —
/// the bound plugin id namespaces all settings calls so two plugins can
/// safely use the same key without collision.
/// </summary>
internal sealed class FiveOSHost : IFiveOSHost
{
    private readonly string _pluginId;
    private readonly string _pluginDir;
    private readonly Action<string>? _toaster;

    public FiveOSHost(string pluginId, string pluginDir, Action<string>? toaster)
    {
        _pluginId = pluginId;
        _pluginDir = pluginDir;
        _toaster = toaster;
    }

    public string HostVersion =>
        typeof(FiveOSHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public string PluginDirectory => _pluginDir;

    public string? GetSetting(string key)
        => UserSettings.LoadPluginSetting(_pluginId, key);

    public void SetSetting(string key, string? value)
        => UserSettings.SavePluginSetting(_pluginId, key, value);

    public void Toast(string message)
        => _toaster?.Invoke(message);
}
