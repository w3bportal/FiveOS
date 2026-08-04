// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using FiveOS.Services;

namespace FiveOS.Views;

/// <summary>
/// Front end for the auto-skinning pipeline: take an unrigged garment mesh and
/// write a wearable clothing .ydd. The whole job is one call into
/// <see cref="ClothingBuilder"/>; this exists to pick the file, the body and
/// the component slot, and to show the warnings it reports back — those matter
/// more than the success message, because a garment can export cleanly and
/// still be wrong (too far from the body, seams, disconnected pieces).
/// </summary>
public partial class ClothingRigDialog
{
    public ClothingRigDialog() => InitializeComponent();

    private string Variant => BodyFemale.IsChecked == true ? "female" : "male";

    private async void OnBrowseMesh(object sender, RoutedEventArgs e)
    {
        var file = await StaFileDialogs.OpenAsync(this, d =>
        {
            d.Title = "Choose the garment mesh";
            d.Filter = "3D models (*.fbx;*.glb;*.gltf;*.obj;*.dae)|*.fbx;*.glb;*.gltf;*.obj;*.dae|All files (*.*)|*.*";
        });
        if (file is null) return;
        MeshBox.Text = file;
        // Guess the slot from the file name. Getting this wrong loads the
        // garment onto the wrong component in game, and the default (a top)
        // is easy to leave in place by accident.
        var guess = GuessComponent(Path.GetFileNameWithoutExtension(file));
        if (guess is not null && LooksLikeDefault(ComponentBox.Text))
        {
            ComponentBox.Text = guess;
            StatusText.Text = $"Guessed component '{guess}' from the file name — change it if that's wrong.";
        }
    }

    private static bool LooksLikeDefault(string? current)
        => string.IsNullOrWhiteSpace(current) || current!.EndsWith("_000_u", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps a file name to a freemode component slot. Only the slots
    /// a garment realistically lands in; anything unrecognised is left alone
    /// rather than guessed wrongly.</summary>
    private static string? GuessComponent(string name)
    {
        var n = name.ToLowerInvariant();
        // Longest / most specific first so "backpack" doesn't match "pack".
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

    private async void OnRig(object sender, RoutedEventArgs e)
    {
        var mesh = MeshBox.Text?.Trim() ?? "";
        var component = ComponentBox.Text?.Trim() ?? "";
        if (!File.Exists(mesh)) { Warn("Pick a garment mesh first."); return; }
        if (string.IsNullOrWhiteSpace(component)) { Warn("Give the component a name, e.g. jbib_042_u."); return; }

        var save = await StaFileDialogs.SaveAsync(this, d =>
        {
            d.Title = "Save the clothing drawable";
            d.Filter = "GTA drawable dictionary (*.ydd)|*.ydd";
            d.FileName = component + ".ydd";
            d.DefaultExt = ".ydd";
        });
        if (save is null) return;

        // Read every control HERE, on the UI thread. Touching them from inside
        // the Task.Run below throws "the calling thread cannot access this
        // object" — WPF controls have thread affinity.
        var variant = Variant;
        bool wantLods = LodsBox.IsChecked == true;

        RigBtn.IsEnabled = false;
        StatusText.Text = "";
        ResultPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressHint.Visibility = Visibility.Collapsed;
        ProgressBarCtl.Value = 0;
        ProgressStage.Text = "starting";
        ProgressPercent.Text = "0%";

        var started = DateTime.UtcNow;
        // Progress arrives from the worker thread; marshal it back, and throttle
        // so a fast garment does not flood the dispatcher with updates.
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
                    OnProgress = Report,
                }, save));

            var lods = string.Join(", ", report.Lods.Select(l => $"{l.triangles:N0} tris"));
            ResultTitle.Text = $"Wrote {Path.GetFileName(save)} — {report.Lods.Count} detail level(s): {lods}";

            double matchedPct = 100.0 * report.MatchedVertices / Math.Max(report.SourceVertices, 1);
            var detail = $"{report.SourceVertices:N0} vertices in. {matchedPct:F0}% weighted directly from the body, " +
                         $"the rest solved.";
            if (report.Warnings.Count > 0)
                detail += "\n\n" + string.Join("\n", report.Warnings.Select(w => "• " + w));
            // Say this every time — it is the difference between "exported" and
            // "correct", and only the game can settle it.
            detail += "\n\nNormal and specular maps are not embedded yet, so it will look flat in game. " +
                      "Check the shoulders and armpits on a moving ped before shipping it.";
            ResultDetail.Text = detail;
            ResultPanel.Visibility = Visibility.Visible;
            StatusText.Text = $"Done in {(DateTime.UtcNow - started).TotalSeconds:F0}s.";
        }
        catch (Exception ex)
        {
            ResultTitle.Text = "Couldn't rig this garment.";
            ResultDetail.Text = ex.Message;
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

    private void Warn(string message) => AppDialog.Warn(message, "Auto-rig clothing", this);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
