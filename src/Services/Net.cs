// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System.Net.NetworkInformation;

namespace FiveOS.Services;

/// <summary>Tiny connectivity helper for the online-only features (Sketchfab,
/// AI image-to-3D, gta5-mods link). Used to show a themed "no internet" dialog
/// up front instead of letting the request fail with a raw error.</summary>
public static class Net
{
    /// <summary>True when there's no usable network at all (airplane mode, Wi-Fi
    /// off, cable unplugged). Fast and local — it does NOT prove the internet is
    /// reachable, just that a network exists; a real failure still surfaces its
    /// own error. Loopback / tunnel-only adapters don't count as "online".</summary>
    public static bool LikelyOffline()
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable()) return true;
            // GetIsNetworkAvailable can be true for virtual-only adapters — require
            // at least one real, up interface that isn't loopback/tunnel.
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                return false;   // found a real, up interface
            }
            return true;
        }
        catch
        {
            return false;   // if we can't tell, don't block the user — let the request try
        }
    }
}
