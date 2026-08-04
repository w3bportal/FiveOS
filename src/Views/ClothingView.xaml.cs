// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FiveOS.Services;

namespace FiveOS.Views;

/// <summary>
/// The Clothing workspace: an unrigged garment mesh in, a wearable GTA
/// clothing drawable out. The work itself is one call into
/// <see cref="ClothingBuilder"/>; this exists to choose the file, the body and
/// the component slot, to show progress (a badly-placed garment can take
/// minutes), and to surface the warnings — a garment can export cleanly and
/// still be wrong, and the warnings are where that shows.
/// </summary>
public partial class ClothingView : UserControl
{
    private bool _viewerReady;
    private string? _pendingPreview;

    public ClothingView() => InitializeComponent();

    private async void OnViewLoaded(object sender, RoutedEventArgs e) => await InitViewerAsync();

    /// <summary>
    /// Hosts the shared three.js viewer on its own virtual host, mirroring the
    /// Emotes and Props workspaces. The garment is shown against the freemode
    /// body so orientation and placement can be judged BEFORE exporting —
    /// numbers in a warning panel are no substitute for looking at it.
    /// </summary>
    private async Task InitViewerAsync()
    {
        if (_viewerReady) return;
        try
        {
            ViewportMessage.Text = "Starting the 3D preview…";
            var userDataDir = Path.Combine(Path.GetTempPath(), "FiveOS", "WebView2-Clothing");
            Directory.CreateDirectory(userDataDir);
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataDir);
            await Viewport.EnsureCoreWebView2Async(env);

            // Same content-keyed session folder trick the Emotes viewer uses:
            // identical content reuses the cache, changed content gets a clean
            // reload without staleness.
            var bundled = RuntimeAssets.ViewerDir;
            var key = Path.GetFileName(bundled.TrimEnd('\\', '/'));
            var srcHtml = Path.Combine(bundled, "viewer.html");
            long stamp = File.Exists(srcHtml) ? File.GetLastWriteTimeUtc(srcHtml).Ticks : 0L;
            var sessionDir = Path.Combine(Path.GetTempPath(), "FiveOS", $"ViewerCloth-{key}-{stamp:x}");
            if (!File.Exists(Path.Combine(sessionDir, "viewer.html")))
            {
                Directory.CreateDirectory(sessionDir);
                CopyDirectory(bundled, sessionDir);
            }

            Viewport.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "cloth-viewer.local", sessionDir,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            Viewport.NavigationCompleted += async (_, _) =>
            {
                _viewerReady = true;
                ViewportOverlay.Visibility = Visibility.Collapsed;
                // Use the viewer's REFERENCE ped, not loadGtaSkeleton — the
                // latter enters pose mode (an Emotes state) and then fights
                // any model loaded after it. The reference is exactly the
                // scale-comparison ghost this workspace wants.
                await Eval("window.setReferenceVisible && window.setReferenceVisible(true)");
                if (_pendingPreview is not null) { var p = _pendingPreview; _pendingPreview = null; await PreviewAsync(p); }
            };
            Viewport.Source = new Uri($"https://cloth-viewer.local/viewer.html?v={stamp:x}");
        }
        catch (Exception ex)
        {
            ViewportMessage.Text = "The 3D preview could not start: " + ex.Message;
            FosLogger.Warn("clothing", "viewer init failed", ex);
        }
    }

    private async Task<string> Eval(string js)
    {
        if (Viewport?.CoreWebView2 is null) return "";
        try { return await Viewport.CoreWebView2.ExecuteScriptAsync(js); }
        catch { return ""; }
    }

    /// <summary>Loads the garment into the viewer alongside the body.</summary>
    private async Task PreviewAsync(string meshPath)
    {
        if (!_viewerReady) { _pendingPreview = meshPath; return; }
        try
        {
            ViewportMessage.Text = "Loading the garment…";
            ViewportOverlay.Visibility = Visibility.Visible;

            // Convert to .glb with Assimp rather than handing the raw file to
            // the viewer. Two reasons: the browser-side FBX loader chokes on
            // plenty of real-world exports, and — more importantly — this
            // previews exactly what the rigging pipeline reads, since it uses
            // the same importer. A preview that disagrees with the exporter is
            // worse than none.
            meshPath = await Task.Run(() => ToPreviewGlb(meshPath));

            // The viewer fetches by URL, so the mesh's folder is mapped to its
            // own virtual host. Remapping the same name is how a second garment
            // from a different folder replaces the first.
            var dir = Path.GetDirectoryName(Path.GetFullPath(meshPath))!;
            try { Viewport.CoreWebView2.ClearVirtualHostNameToFolderMapping("cloth-model.local"); } catch { }
            Viewport.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "cloth-model.local", dir,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

            var url = "https://cloth-model.local/" + Uri.EscapeDataString(Path.GetFileName(meshPath));
            await Eval($"window.loadModel && window.loadModel('{url.Replace("'", "\\'")}')");
            // Keep the body visible after the load — clearModel() inside
            // loadModel hides it.
            await Eval("window.setReferenceVisible && window.setReferenceVisible(true)");
            ViewportOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ViewportMessage.Text = "Couldn't preview this mesh: " + ex.Message;
            ViewportOverlay.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Re-exports any supported mesh as .glb for the viewer. Returns
    /// the original path if conversion is not needed or not possible.</summary>
    private static string ToPreviewGlb(string meshPath)
    {
        if (meshPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) return meshPath;
        try
        {
            var outDir = Path.Combine(Path.GetTempPath(), "FiveOS", "ClothPreview");
            Directory.CreateDirectory(outDir);
            var outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(meshPath) + ".glb");

            using var ctx = new Assimp.AssimpContext();
            var scene = ctx.ImportFile(meshPath,
                Assimp.PostProcessSteps.Triangulate | Assimp.PostProcessSteps.PreTransformVertices);
            if (scene is null || scene.MeshCount == 0) return meshPath;
            // Materials are irrelevant here — this is a shape check — and a
            // missing texture reference is a common reason the browser-side
            // load fails outright.
            scene.Materials.Clear();
            scene.Materials.Add(new Assimp.Material());
            foreach (var m in scene.Meshes) m.MaterialIndex = 0;
            return ctx.ExportFile(scene, outPath, "glb2") ? outPath : meshPath;
        }
        catch (Exception ex)
        {
            FosLogger.Warn("clothing", "preview conversion failed, using the original file", ex);
            return meshPath;
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), true);
    }

    private string Variant => BodyFemale.IsChecked == true ? "female" : "male";

    private async void OnBrowseMesh(object sender, RoutedEventArgs e)
    {
        var file = await StaFileDialogs.OpenAsync(Window.GetWindow(this), d =>
        {
            d.Title = "Choose the garment mesh";
            d.Filter = "3D models (*.fbx;*.glb;*.gltf;*.obj;*.dae)|*.fbx;*.glb;*.gltf;*.obj;*.dae|All files (*.*)|*.*";
        });
        if (file is null) return;
        MeshBox.Text = file;

        // Guess the slot from the file name — leaving the default (a top) in
        // place loads the garment onto the wrong component in game.
        var guess = GuessComponent(Path.GetFileNameWithoutExtension(file));
        if (guess is not null && LooksLikeDefault(ComponentBox.Text))
        {
            ComponentBox.Text = guess;
            StatusText.Text = $"Guessed component '{guess}' from the file name — change it if that's wrong.";
        }
        await PreviewAsync(file);
    }

    private static bool LooksLikeDefault(string? current)
        => string.IsNullOrWhiteSpace(current) || current!.EndsWith("_000_u", StringComparison.OrdinalIgnoreCase);

    private static string? GuessComponent(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("backpack") || n.Contains("rucksack") || n.Contains("satchel")
            || n.Contains("bag") || n.Contains("parachute")) return "hand_000_u";
        if (n.Contains("vest") || n.Contains("armor") || n.Contains("armour")) return "task_000_u";
        if (n.Contains("trouser") || n.Contains("pant") || n.Contains("jean")
            || n.Contains("short") || n.Contains("skirt")) return "lowr_000_r";
        if (n.Contains("shoe") || n.Contains("boot") || n.Contains("sneaker")) return "feet_000_u";
        if (n.Contains("mask") || n.Contains("bandana")) return "berd_000_u";
        if (n.Contains("scarf") || n.Contains("tie") || n.Contains("chain")
            || n.Contains("necklace")) return "teef_000_u";
        if (n.Contains("undershirt") || n.Contains("tshirt") || n.Contains("t-shirt")) return "accs_000_u";
        if (n.Contains("hoodie") || n.Contains("jacket") || n.Contains("coat")
            || n.Contains("shirt") || n.Contains("top") || n.Contains("jumper")
            || n.Contains("sweater")) return "jbib_000_u";
        return null;
    }

    /// <summary>The two rotations that fix nearly every bad export: -90 about X
    /// for a Y-up mesh, 180 about Z for one facing backwards.</summary>
    private void OnQuickRotate(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;
        switch (tag)
        {
            case "X-90": RotX.Text = "-90"; break;
            case "Z180": RotZ.Text = RotZ.Text?.Trim() == "180" ? "0" : "180"; break;
            case "reset": RotX.Text = RotY.Text = RotZ.Text = "0";
                          OffX.Text = OffY.Text = OffZ.Text = "0"; ScaleBox.Text = "1"; break;
        }
    }

    private static double Num(System.Windows.Controls.Control box, double fallback = 0)
    {
        var text = (box as Wpf.Ui.Controls.TextBox)?.Text?.Trim();
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private async void OnRig(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var mesh = MeshBox.Text?.Trim() ?? "";
        var component = ComponentBox.Text?.Trim() ?? "";
        if (!File.Exists(mesh)) { AppDialog.Warn("Pick a garment mesh first.", "Clothing", owner); return; }
        if (string.IsNullOrWhiteSpace(component))
        { AppDialog.Warn("Give the component a name, e.g. jbib_042_u.", "Clothing", owner); return; }

        var save = await StaFileDialogs.SaveAsync(owner, d =>
        {
            d.Title = "Save the clothing drawable";
            d.Filter = "GTA drawable dictionary (*.ydd)|*.ydd";
            d.FileName = component + ".ydd";
            d.DefaultExt = ".ydd";
        });
        if (save is null) return;

        // Read every control HERE, on the UI thread — WPF controls have thread
        // affinity and touching them inside the Task.Run below throws.
        var variant = Variant;
        bool wantLods = LodsBox.IsChecked == true;
        bool autoReduce = ReduceBox.IsChecked == true;
        if (!int.TryParse(BudgetBox.Text?.Trim(), out int budget) || budget < 500) budget = 20000;
        budget = Math.Min(budget, ushort.MaxValue);
        var rotation = new g3.Vector3d(Num(RotX), Num(RotY), Num(RotZ));
        var offset = new g3.Vector3d(Num(OffX), Num(OffY), Num(OffZ));
        double scaleMul = Num(ScaleBox, 1.0);
        if (scaleMul <= 0) scaleMul = 1.0;

        RigBtn.IsEnabled = false;
        StatusText.Text = "";
        ResultPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressHint.Visibility = Visibility.Collapsed;
        ProgressBarCtl.Value = 0;
        ProgressStage.Text = "starting";
        ProgressPercent.Text = "0%";

        var started = DateTime.UtcNow;
        var lastPost = DateTime.MinValue;
        void Report(double fraction, string what)
        {
            var now = DateTime.UtcNow;
            if (now - lastPost < TimeSpan.FromMilliseconds(60) && fraction < 0.999) return;
            lastPost = now;
            bool slow = now - started > TimeSpan.FromSeconds(25);
            Dispatcher.BeginInvoke(() =>
            {
                ProgressBarCtl.Value = Math.Clamp(fraction * 100.0, 0, 100);
                ProgressPercent.Text = $"{fraction * 100:F0}%";
                ProgressStage.Text = what;
                if (slow) ProgressHint.Visibility = Visibility.Visible;
            });
        }

        try
        {
            var report = await Task.Run(() => ClothingBuilder.BuildToFile(
                new ClothingBuilder.Request
                {
                    MeshPath = mesh,
                    ComponentName = component,
                    Variant = variant,
                    GenerateLods = wantLods,
                    AutoDecimate = autoReduce,
                    MaxVertices = budget,
                    Rotation = rotation,
                    Offset = offset,
                    ScaleMultiplier = scaleMul,
                    OnProgress = Report,
                }, save));

            var lods = string.Join(", ", report.Lods.Select(l => $"{l.triangles:N0} tris"));
            ResultTitle.Text = $"Wrote {Path.GetFileName(save)} — {report.Lods.Count} detail level(s): {lods}";

            double matchedPct = 100.0 * report.MatchedVertices / Math.Max(report.SourceVertices, 1);
            var detail = $"{report.SourceVertices:N0} vertices in. {matchedPct:F0}% weighted directly from the body, " +
                         "the rest solved.";
            if (matchedPct < 40)
                detail += "\n\nThat match rate is low — the garment is probably not sitting on the ped, or is at the " +
                          "wrong scale. Weights will be guesswork until that is fixed.";
            if (report.Warnings.Count > 0)
                detail += "\n\n" + string.Join("\n", report.Warnings.Select(w => "• " + w));
            detail += "\n\nNormal and specular maps are not embedded yet, so it will look flat in game. " +
                      "Check the shoulders and armpits on a moving ped before shipping it.";
            ResultDetail.Text = detail;
            ResultPanel.Visibility = Visibility.Visible;
            StatusText.Text = $"Done in {(DateTime.UtcNow - started).TotalSeconds:F0}s.";
        }
        catch (Exception ex)
        {
            ResultTitle.Text = "Couldn't rig this garment.";
            // Measured diagnostics matter far more than the failure itself —
            // they say WHY, and the fix is nearly always in the 3D tool.
            var diag = (ex as ClothingFitException)?.Diagnostics;
            ResultDetail.Text = diag is { Count: > 0 }
                ? ex.Message + "\n\n" + string.Join("\n", diag.Select(d => "• " + d))
                : ex.Message;
            ResultPanel.Visibility = Visibility.Visible;
            StatusText.Text = "Failed.";
            FosLogger.Warn("clothing", "auto-rig failed", ex);
        }
        finally
        {
            RigBtn.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }
}
