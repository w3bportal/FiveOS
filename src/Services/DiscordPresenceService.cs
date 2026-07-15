// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.IO;
using DiscordRPC;
using DiscordRPC.Logging;

namespace FiveOS.Services;

/// <summary>
/// Small wrapper around Lachee's DiscordRPC that shows FiveOS on the user's
/// Discord profile. Call Initialize() once at startup and Shutdown() on exit;
/// the Set* helpers update the current state and re-publish presence.
///
/// Everything is a no-op if the user turned the integration off, or if Discord
/// isn't running (the RPC client keeps retrying the connection on its own).
/// </summary>
public static class DiscordPresenceService
{
    // FiveOS app on the Discord developer portal. It owns the "fiveos_logo"
    // art asset, so don't change this without updating the asset too or the
    // icon vanishes from everyone's cards.
    private const string DiscordAppId = "1503069292189585468";

    private const string LargeImageKey = "fiveos_logo";
    private const string LargeImageText = "FiveOS";

    private static DiscordRpcClient? _client;
    private static readonly object _gate = new();

    // Current UI state. Set* merges into these and re-publishes on any change.
    private static string? _activeTab;
    private static string? _modelFileName;
    private static bool _isConverting;

    /// <summary>True once the RPC client is up (Discord itself may still be offline).</summary>
    public static bool IsRunning => _client is { IsInitialized: true };

    /// <summary>Starts the RPC client. Does nothing if it's already running or the toggle is off.</summary>
    public static void Initialize()
    {
        lock (_gate)
        {
            if (_client != null) return;
            if (!UserSettings.LoadEnableDiscordPresence()) return;

            try
            {
                _client = new DiscordRpcClient(DiscordAppId)
                {
                    Logger = new NullLogger(),
                };
                _client.Initialize();
                Publish();
            }
            catch
            {
                // A flaky pipe shouldn't take the whole app down.
                _client?.Dispose();
                _client = null;
            }
        }
    }

    /// <summary>Stops the RPC client. Safe to call more than once. Runs from
    /// App.OnExit and when the user turns the toggle off at runtime.</summary>
    public static void Shutdown()
    {
        lock (_gate)
        {
            try { _client?.ClearPresence(); } catch { /* ignore */ }
            try { _client?.Dispose(); } catch { /* ignore */ }
            _client = null;
        }
    }

    // State setters, called from the UI side.

    /// <summary>Tab the user is on, e.g. "3D → Prop", "Optimize", "Image → 3D". Null clears it.</summary>
    public static void SetTab(string? tab)
    {
        if (_activeTab == tab) return;
        _activeTab = tab;
        Publish();
    }

    /// <summary>File name of the loaded model, no path. Null when nothing's loaded.
    /// Only really matters on the 3D → Prop tab; the publisher decides what to show.</summary>
    public static void SetLoadedModel(string? fileName)
    {
        if (_modelFileName == fileName) return;
        _modelFileName = fileName;
        Publish();
    }

    /// <summary>Whether a conversion or optimize run is currently going.</summary>
    public static void SetConverting(bool converting)
    {
        if (_isConverting == converting) return;
        _isConverting = converting;
        Publish();
    }

    // Build and send the presence frame.

    /// <summary>Presence is kept deliberately bare: just the logo and "FiveOS", so
    /// Discord shows "Playing FiveOS" and nothing about what the user is doing.</summary>
    private static void Publish()
    {
        DiscordRpcClient? client;
        lock (_gate) client = _client;
        if (client is not { IsInitialized: true }) return;

        var presence = new RichPresence
        {
            Assets = new Assets
            {
                LargeImageKey = LargeImageKey,
                LargeImageText = LargeImageText,
            },
        };

        try { client.SetPresence(presence); }
        catch { /* pipe may have just dropped */ }
    }

    // Convenience for callers that only have a path on hand.
    public static void SetLoadedModelFromPath(string? path)
        => SetLoadedModel(string.IsNullOrEmpty(path) ? null : Path.GetFileName(path));
}
