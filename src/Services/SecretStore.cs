// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FiveOS.Services;

/// <summary>
/// Per-user encrypted secret storage backed by Windows DPAPI
/// (<see cref="ProtectedData"/>). The encrypted blob lives under
/// %APPDATA%\FiveOS\ and can only be decrypted by the same Windows
/// user account that wrote it.
///
/// We use this for the Sketchfab API token. DPAPI is the right answer
/// for "secret tied to this machine + this user" — it's exactly what
/// Edge / Chrome / Outlook use for the same problem on Windows. No
/// external key management, no plaintext on disk, no need for a master
/// password from the user.
/// </summary>
public static class SecretStore
{
    private static readonly byte[] EntropyV1 =
        Encoding.UTF8.GetBytes("FiveOS:secret-store:v1");

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FiveOS", "secrets");

    private static string PathFor(string key) =>
        Path.Combine(Dir, key + ".dat");

    public static void Save(string key, string value)
    {
        Directory.CreateDirectory(Dir);
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = ProtectedData.Protect(plain, EntropyV1, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(key), cipher);
    }

    public static string? Load(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        try
        {
            var cipher = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(cipher, EntropyV1, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Wrong user/machine (crypto), or the file is transiently locked by AV /
            // another process (IO) or ACL-restricted — treat as absent rather than throw.
            return null;
        }
    }

    public static void Clear(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
    }

    public static bool Has(string key) =>
        File.Exists(PathFor(key));
}
