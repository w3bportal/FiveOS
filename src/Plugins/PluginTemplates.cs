// Copyright (c) 2026 FiveOS. Licensed under the GNU GPLv3. See LICENSE.
// https://github.com/w3bportal/FiveOS

using System;
using System.IO;
using System.Linq;

namespace FiveOS.Plugins;

/// <summary>
/// Scaffolds new plugins under <see cref="PluginManager.PluginsDir"/> from
/// built-in starter templates. Two flavours:
///
///   • HTML — drop a folder with <c>plugin.json</c> + <c>index.html</c>.
///     Runs immediately, no compile step. Sandboxed in WebView2.
///   • DLL — drop a folder with <c>plugin.json</c> + a buildable
///     <c>.csproj</c> + sample <c>Plugin.cs</c>. The user runs
///     <c>dotnet build</c> themselves; the manifest already points at the
///     eventual output dll path so the plugin shows up post-build.
///
/// Templates are emitted as raw string literals — keeping them in code
/// (rather than embedded resources) means a `dotnet build` shows compile
/// errors right next to the strings, not at runtime.
/// </summary>
public static class PluginTemplates
{
    /// <summary>Create an HTML starter plugin and return its folder path.
    /// Caller is expected to reveal the folder + refresh discovery.</summary>
    public static string ScaffoldHtmlPlugin()
    {
        var dir = ReserveFolder("my-html-plugin");
        File.WriteAllText(Path.Combine(dir, "plugin.json"), HtmlManifest);
        File.WriteAllText(Path.Combine(dir, "index.html"), HtmlEntry);
        File.WriteAllText(Path.Combine(dir, "README.md"), HtmlReadme);
        return dir;
    }

    /// <summary>Create a DLL starter plugin (csproj + Plugin.cs) and
    /// return its folder path. The plugin is unbuilt — until the user
    /// runs <c>dotnet build</c> the discovery will skip it (entry path
    /// missing) but the folder still appears in Explorer.</summary>
    public static string ScaffoldDllPlugin()
    {
        var dir = ReserveFolder("my-dll-plugin");
        File.WriteAllText(Path.Combine(dir, "plugin.json"), DllManifest);
        File.WriteAllText(Path.Combine(dir, "MyPlugin.csproj"), DllCsproj);
        File.WriteAllText(Path.Combine(dir, "Plugin.cs"), DllPluginCs);
        File.WriteAllText(Path.Combine(dir, "README.md"), DllReadme);
        return dir;
    }

    /// <summary>Pick a fresh folder name based on the requested base. If
    /// the name already exists, append "-2", "-3", etc. so we never
    /// overwrite an existing plugin's contents.</summary>
    private static string ReserveFolder(string baseName)
    {
        Directory.CreateDirectory(PluginManager.PluginsDir);
        var existing = Directory.EnumerateDirectories(PluginManager.PluginsDir)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Select(n => n!.ToLowerInvariant())
            .ToHashSet();

        var candidate = baseName;
        for (int i = 2; existing.Contains(candidate.ToLowerInvariant()); i++)
            candidate = $"{baseName}-{i}";

        var dir = Path.Combine(PluginManager.PluginsDir, candidate);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ─── HTML template ────────────────────────────────────────────────

    private const string HtmlManifest = """
{
  "id": "my-html-plugin",
  "name": "My HTML Plugin",
  "description": "A starter HTML plugin scaffolded from the FiveOS template.",
  "version": "0.1.0",
  "author": "you",
  "entry": "index.html",
  "minHostVersion": "0.2.0"
}
""";

    private const string HtmlEntry = """
<!doctype html>
<html>
<head>
  <meta charset="utf-8" />
  <title>My HTML Plugin</title>
  <style>
    html, body { margin: 0; height: 100%; background: #1a1a1c; color: #e8e8ea;
                 font-family: -apple-system, "Segoe UI", Roboto, sans-serif; }
    body { display: flex; flex-direction: column; align-items: center;
           justify-content: center; gap: 16px; padding: 32px; text-align: center; }
    h1   { margin: 0; font-size: 28px; color: #5BAEFF; font-weight: 600; }
    p    { max-width: 560px; line-height: 1.55; opacity: 0.85; }
    code { background: #26262a; padding: 2px 6px; border-radius: 4px; font-size: 13px; }
    button { padding: 10px 18px; font-size: 14px; border: 1px solid #3a3a3f;
             background: #26262a; color: #e8e8ea; border-radius: 6px; cursor: pointer; }
    button:hover { background: #303035; }
  </style>
</head>
<body>
  <h1>My HTML Plugin</h1>
  <p>This page lives in <code>%AppData%\FiveOS\plugins\my-html-plugin\index.html</code>
     and is hosted in a sandboxed WebView2.</p>
  <p>Edit the file, click <strong>Refresh</strong> in Settings → Addons,
     and FiveOS will reload it. You have full HTML / CSS / JS — fetch
     APIs, canvas, three.js, all of it.</p>
  <button onclick="alert('Hello from the plugin!')">Say hello</button>
</body>
</html>
""";

    private const string HtmlReadme = """
# My HTML Plugin

This is a starter HTML plugin for FiveOS. It runs in a sandboxed WebView2;
file access is scoped to this folder via a per-plugin virtual host.

## Edit
1. Open `index.html` in your editor of choice.
2. Click **Refresh** in FiveOS → Settings → Addons (or restart the app).

## Publish
Zip this folder and share. Recipients drop the unzipped folder into
`%AppData%\FiveOS\plugins\` and refresh.
""";

    // ─── DLL template ─────────────────────────────────────────────────

    private const string DllManifest = """
{
  "id": "my-dll-plugin",
  "name": "My DLL Plugin",
  "description": "A starter .NET plugin scaffolded from the FiveOS template.",
  "version": "0.1.0",
  "author": "you",
  "entry": "bin/Release/net8.0-windows/MyPlugin.dll",
  "minHostVersion": "0.2.0"
}
""";

    private const string DllCsproj = """
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>MyPlugin</AssemblyName>
    <RootNamespace>MyPlugin</RootNamespace>
  </PropertyGroup>

  <!-- Reference the FiveOS plugin SDK. The default points at the
       per-user install on Desktop\FiveOS\sdk\ — change this if your
       FiveOS install lives somewhere else. -->
  <ItemGroup>
    <Reference Include="FiveOS.Plugins.Sdk">
      <HintPath>$(UserProfile)\Desktop\FiveOS\sdk\FiveOS.Plugins.Sdk.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

</Project>
""";

    private const string DllPluginCs = """
using System.Windows;
using System.Windows.Controls;
using FiveOS.Plugins;

namespace MyPlugin;

/// <summary>
/// Minimal FiveOS plugin. Implements IFiveOSPlugin and returns a
/// hand-built UserControl from CreateView.
/// </summary>
public sealed class Plugin : IFiveOSPlugin
{
    private IFiveOSHost? _host;

    public string Id          => "my-dll-plugin";
    public string Name        => "My DLL Plugin";
    public string Description => "A starter .NET plugin from the FiveOS template.";

    public void Initialize(IFiveOSHost host)
    {
        _host = host;
        // Example: read a per-plugin setting that survives across runs.
        var lastRun = host.GetSetting("lastRun") ?? "(first run)";
        host.Toast($"My DLL Plugin loaded. Last run: {lastRun}");
        host.SetSetting("lastRun", System.DateTime.Now.ToString("O"));
    }

    public UserControl CreateView()
    {
        var stack = new StackPanel
        {
            Margin = new Thickness(32),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        stack.Children.Add(new TextBlock
        {
            Text = "Hello from My DLL Plugin",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Edit Plugin.cs and run `dotnet build -c Release`.",
            Margin = new Thickness(0, 12, 0, 0),
            Opacity = 0.75,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var btn = new Button
        {
            Content = "Toast from plugin",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        btn.Click += (_, _) => _host?.Toast("Hello from My DLL Plugin");
        stack.Children.Add(btn);
        return new UserControl { Content = stack };
    }
}
""";

    private const string DllReadme = """
# My DLL Plugin

This is a starter .NET plugin for FiveOS. The plugin runs with full app
permissions — FiveOS will prompt the user for explicit trust the first
time it's enabled.

## Build
From this folder, run:

```
dotnet build -c Release
```

This produces `bin/Release/net8.0-windows/MyPlugin.dll`, which is what
`plugin.json` already points at.

If `FiveOS.Plugins.Sdk.dll` can't be found, update the `<HintPath>` in
`MyPlugin.csproj` to point at where you installed FiveOS — by default
the SDK is shipped in the published `sdk/` folder next to FiveOS.exe.

## Test
1. Build (above).
2. Click **Refresh** in FiveOS → Settings → Addons.
3. Toggle the plugin on (you'll get a one-time trust prompt).
4. Click the puzzle-piece in the activity rail.

## Publish
Zip this folder *with the built dll* and share. Recipients drop the
unzipped folder into `%AppData%\FiveOS\plugins\` and refresh.
""";
}
