// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using System.IO.Compression;
using Assimp;
using CodeWalker;                  // FbxConverter
using CodeWalker.GameFiles;        // YdrFile

namespace YdrWriter;

public static class Converter
{
    public static int Run(ConvertOptions opts)
    {
        Log($"input:  {opts.InputPath}");
        Log($"output: {opts.OutputDir}");
        Log($"name:   {opts.AssetName}");

        if (!File.Exists(opts.InputPath))
        {
            Console.Error.WriteLine($"input not found: {opts.InputPath}");
            return 2;
        }

        // Stage 1: Import the source mesh via Assimp. Assimp handles glTF/GLB/
        // OBJ/FBX/DAE/PLY/STL natively; we don't need format-specific code.
        //
        // PreTransformVertices is load-bearing: every Assimp Mesh stores
        // vertices in NODE-LOCAL space, and DirectFbxBuilder writes them out
        // raw with every Model connected directly to the FBX root (no
        // Lcl Translation/Rotation/Scaling on the Models). Without this
        // flag, a multi-part source — humanoid with head/torso/limbs each
        // under their own node, a car with separate wheel nodes — comes out
        // of the YDR as every submesh stacked at origin instead of at its
        // authored position. The drawable then renders in-game as a small
        // fragmented blob even though the viewer (three.js, respects the
        // node hierarchy) shows the model intact. The collision path
        // (YbnBuilder.CollectGeometry) walks nodes manually for the same
        // reason; this is the equivalent fix on the drawable side.
        Log("[1/3] Importing mesh via Assimp...");
        using var ai = new AssimpContext();
        // Keep same-material meshes SEPARATE through PreTransformVertices.
        // By default PTV joins every mesh that shares a material into one —
        // which fuses e.g. a mirror's glass pane and its wooden frame (both
        // on one atlas material) into a single mesh, making it impossible to
        // give only the glass a glass.sps shader. AI_CONFIG_PP_PTV_KEEP_HIERARCHY
        // still bakes vertices to world space (parts stay at their authored
        // positions — the reason PTV is load-bearing) but leaves each source
        // mesh as its own entry, so per-mesh shader assignment (glass, emissive)
        // can target one part without touching its neighbours.
        ai.SetConfig(new Assimp.Configs.BooleanPropertyConfig("PP_PTV_KEEP_HIERARCHY", true));
        const PostProcessSteps steps =
            PostProcessSteps.Triangulate
            | PostProcessSteps.GenerateNormals
            | PostProcessSteps.JoinIdenticalVertices
            | PostProcessSteps.ImproveCacheLocality
            | PostProcessSteps.GenerateUVCoords
            | PostProcessSteps.PreTransformVertices;
        var scene = ai.ImportFile(opts.InputPath, steps);
        if (scene == null || !scene.HasMeshes)
        {
            Console.Error.WriteLine("import produced no meshes");
            return 3;
        }
        Log($"  meshes: {scene.MeshCount}, materials: {scene.MaterialCount}, embedded textures: {scene.TextureCount}");

        // Stage 1b: Bake the user's gizmo transform (Scale → Rotate → Translate)
        // from the viewer into vertex coordinates. Authored against the
        // viewer's display space (three.js Y-up, XYZ Euler in degrees), so
        // it has to run BEFORE the Y→Z swap. Without this, every gizmo
        // adjustment the user makes in the preview is silently dropped at
        // export — peds rotated upright in the viewer ship sideways.
        ApplyUserTransform(scene, opts);

        // Stage 1c: Axis correction. GTA/RAGE expects Z-up; most glTF/OBJ
        // assets are Y-up. FBX is mixed — Blender exports Y-up by default
        // but most game pipelines (Maya/3ds Max) export Z-up. Honor the
        // user's UI choice; "auto" reads FBX metadata, falls back to Y-up
        // for everything else.
        var sourceUp = ResolveSourceUpAxis(opts, scene);
        Log($"  source up axis: {sourceUp}");
        if (sourceUp == SourceUp.YUp)
        {
            ApplyYupToZupTransform(scene);
            Log("  applied Y-up -> Z-up rotation (rot X -90)");
        }
        else
        {
            Log("  source already Z-up — no rotation applied");
        }

        // Stage 2: Build the FBX directly from the Assimp scene using CW.Core's
        // own writer. We don't go through AssimpNet's FBX exporter because it
        // deduplicates UVs in a way that loses per-vertex distinction whenever
        // two verts share a 3D position with different UVs (typical for
        // low-poly assets where UV islands meet at shared corners — e.g. a
        // laptop's screen front and lid back share the lid's edge verts).
        // DirectFbxBuilder emits one UV per polygon-vertex with the trivial
        // identity UVIndex, so CW reads exactly the UVs Assimp loaded.
        Log("[2/3] Building FBX (direct, no Assimp export)...");
        var workDir = Path.Combine(Path.GetTempPath(), "ydr-writer", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        var fbxPath = Path.Combine(workDir, $"{opts.AssetName}.fbx");
        try
        {
            if (opts.ExcludeMeshes.Count > 0)
                Log($"  excluding {opts.ExcludeMeshes.Count} mesh(es) per layers panel: {string.Join(", ", opts.ExcludeMeshes)}");
            var fbxBytes = DirectFbxBuilder.Build(scene, opts.ExcludeMeshes);
            File.WriteAllBytes(fbxPath, fbxBytes);
            Log($"  FBX size: {fbxBytes.Length:N0} bytes");

            // Stage 3: Hand to CodeWalker.Core's FbxConverter -> YdrFile -> bytes.
            Log("[3/3] Converting FBX -> YDR via CodeWalker.Core...");
            var converter = new FbxConverter
            {
                // DirectFbxBuilder already writes glTF/D3D top-origin V into
                // the FBX (it undoes Assimp's import-time V flip). So CW's
                // InvertTexcoordV must be off — leaving it on would re-flip V
                // and store negative V in the YDR vertex buffer, which RAGE
                // ends up sampling at the texture's top edge (mostly-black
                // wallpaper) instead of the desktop content.
                InvertTexcoordV = false,
            };
            YdrFile ydr;
            try
            {
                ydr = converter.ConvertToYdr(opts.AssetName, fbxBytes);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FbxConverter.ConvertToYdr failed: {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                return 5;
            }
            if (ydr == null)
            {
                Console.Error.WriteLine("FbxConverter returned null");
                return 5;
            }

            // Reject NaN / Infinity / degenerate bounds BEFORE they reach the
            // .ydr bounding sphere and the .ytyp archetype. A NaN or Inf bound
            // (from corrupt source vertex positions) is a known RAGE streaming /
            // frustum-cull crash — world-add does sphere+box arithmetic on it,
            // and the sphere-expansion guard below silently no-ops on NaN
            // (NaN comparisons are always false). A zero-volume box means the
            // whole mesh collapsed to a point. Better to fail loudly than ship
            // an asset that crashes the client streamer.
            {
                var db = ydr.Drawable;
                static bool BadF(float f) => float.IsNaN(f) || float.IsInfinity(f);
                var mn = db.BoundingBoxMin; var mx = db.BoundingBoxMax; var c = db.BoundingCenter;
                if (BadF(mn.X) || BadF(mn.Y) || BadF(mn.Z) ||
                    BadF(mx.X) || BadF(mx.Y) || BadF(mx.Z) ||
                    BadF(c.X)  || BadF(c.Y)  || BadF(c.Z)  || BadF(db.BoundingSphereRadius))
                {
                    Console.Error.WriteLine("[ydr-writer] ABORT: drawable bounds contain NaN/Infinity — " +
                        "the source mesh has invalid vertex positions. Shipping this would crash the game streamer.");
                    return 6;
                }
                if ((mx.X - mn.X) <= 0f && (mx.Y - mn.Y) <= 0f && (mx.Z - mn.Z) <= 0f)
                {
                    Console.Error.WriteLine("[ydr-writer] ABORT: drawable bounding box is zero-volume (degenerate mesh).");
                    return 6;
                }
            }

            // Expand BoundingSphereRadius to fully enclose the bounding
            // box. CW.FbxConverter computes the sphere from vertex
            // distances to BoundingCenter, which is a tighter fit than
            // the box's half-diagonal — for any geometry that isn't
            // roughly spherical, the resulting sphere doesn't fully
            // enclose the AABB. RAGE's frustum cull uses the sphere as
            // the entity's broad-phase bound: when the sphere falls
            // outside the view frustum, the entity is culled even if
            // parts of the actual mesh would still be visible. Symptom
            // is "stand close to the prop, rotate camera slightly, the
            // prop pops out of view". Force the sphere radius to at
            // least the box's half-diagonal so the sphere ALWAYS
            // encloses every drawable vertex.
            {
                var d = ydr.Drawable;
                var halfExtent = (d.BoundingBoxMax - d.BoundingBoxMin) * 0.5f;
                var diagRadius = halfExtent.Length();
                if (d.BoundingSphereRadius < diagRadius)
                {
                    Log($"  expanding BoundingSphereRadius {d.BoundingSphereRadius:F3} -> {diagRadius:F3} to enclose AABB");
                    d.BoundingSphereRadius = diagRadius;
                }
            }

            // Stage 4: Bake the embedded TextureDictionary. CW.FbxConverter
            // produces shader sampler slots but doesn't fill them with textures
            // from FBX-embedded media. We pull the source textures out of the
            // Assimp scene, encode them as DDS, and bind them to the shader
            // params here.
            // Bake when ExtractTextures is on OR the host supplied per-part
            // diffuse overrides (Add Missing Textures). Overrides alone must
            // still produce a TXD even if Assimp found nothing on disk.
            if ((opts.ExtractTextures || opts.PartDiffuseTextures.Count > 0)
                && scene.MaterialCount > 0)
            {
                Log("[4/4] Building embedded TextureDictionary + binding shaders...");
                var sourceDir = Path.GetDirectoryName(Path.GetFullPath(opts.InputPath)) ?? "";
                var matTexs = TextureBaker.Bake(
                    scene, workDir, opts.AssetName, sourceDir, opts.PartDiffuseTextures);
                if (matTexs.Any(m => m.Diffuse != null || m.Normal != null))
                {
                    // Restrict the embedded TXD to textures actually used by
                    // surviving meshes. Without this, a layer-split pass that
                    // keeps one mesh out of 28 still embeds all 28 source
                    // textures, defeating the point of splitting. The shader
                    // binding below stays correct because matTexs is left
                    // intact — only the dictionary contents are filtered.
                    var keptMatIdx = new HashSet<int>();
                    for (int mi = 0; mi < scene.MeshCount; mi++)
                    {
                        var mname = scene.Meshes[mi].Name;
                        if (opts.ExcludeMeshes.Count > 0 && !string.IsNullOrEmpty(mname)
                            && opts.ExcludeMeshes.Contains(mname))
                            continue;
                        int srcMidx = scene.Meshes[mi].MaterialIndex;
                        if (srcMidx >= 0) keptMatIdx.Add(srcMidx);
                    }
                    var keptMatTexs = matTexs
                        .Select((m, idx) => (m, idx))
                        .Where(t => keptMatIdx.Count == 0 || keptMatIdx.Contains(t.idx))
                        .Select(t => t.m)
                        .ToList();

                    var td = TextureBaker.BuildDictionary(keptMatTexs);
                    ydr.Drawable.ShaderGroup.TextureDictionary = td;
                    // Glass appearance slider (see-through → reflective) — set
                    // before binding so the generated glass diffuse + shader
                    // reflection are built at the chosen strength.
                    TextureBaker.GlassOpacity = opts.GlassOpacity;
                    TextureBaker.BindShaderTextures(ydr, scene, matTexs, opts.ExcludeMeshes, opts.PartMaterials);
                    Log($"  embedded {td.Textures.data_items.Length} textures (kept {keptMatTexs.Count}/{matTexs.Count} materials)");
                }
                else
                {
                    Log("  (no source textures found, skipping)");
                }
            }

            // Stage 4b: generate embedded LODs by deep-cloning the High
            // DrawableModels into Med/Low/VLow slots and decimating each
            // clone via g3sharp. Has to run BEFORE the embed-collision
            // resave below, otherwise the second Save() rebuilds page
            // layout without the new LOD models in the reference graph.
            if (opts.GenerateLods)
            {
                Log("[4b/-] Generating embedded LODs...");
                try
                {
                    var cfg = new LodGenerator.Config(
                        opts.LodMedRatio, opts.LodLowRatio, opts.LodVLowRatio,
                        opts.LodDistHigh, opts.LodDistMed, opts.LodDistLow, opts.LodDistVLow);
                    var (med, low, vlow) = LodGenerator.AddLods(ydr.Drawable, cfg, m => Log("  " + m));
                    Log($"  LODs: med={med:N0} low={low:N0} vlow={vlow:N0} tris");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ydr-writer] LOD generation failed (non-fatal): {ex.GetType().Name}: {ex.Message}");
                }
            }
            else
            {
                // No LOD generation, but still respect the user's draw-distance
                // choice — set LodDistHigh to the outer-most configured value
                // so RAGE renders the High model all the way out instead of
                // culling early at whatever default FbxConverter left there.
                // Med/Low/Vlow stay at the configured fall-off points so the
                // archetype-side cull lines up cleanly.
                ydr.Drawable.LodDistHigh = opts.LodDistVLow;
                ydr.Drawable.LodDistMed  = opts.LodDistVLow;
                ydr.Drawable.LodDistLow  = opts.LodDistVLow;
                ydr.Drawable.LodDistVlow = opts.LodDistVLow;
            }

            byte[] ydrBytes = ydr.Save();
            Log($"  YDR size: {ydrBytes.Length:N0} bytes");

            // Layout: <output>/<name>_resource/{stream/<name>.{ydr,ybn,ytyp}, fxmanifest.lua}
            var resourceDir = Path.Combine(opts.OutputDir, $"{opts.AssetName}_resource");
            var streamDir = Path.Combine(resourceDir, "stream");
            Directory.CreateDirectory(streamDir);
            var ydrOut = Path.Combine(streamDir, $"{opts.AssetName}.ydr");
            File.WriteAllBytes(ydrOut, ydrBytes);
            Log($"  wrote: {ydrOut}");

            // Stage 5a: collision. BoundComposite > BoundBVH built from
            // the same Z-up scene we exported the drawable from. Without this
            // the prop is non-solid in-game (peds/vehicles pass through it).
            //
            // Two output paths:
            //   • EmbedCollision=true → composite goes into Drawable.Bound
            //     before the YDR is saved. No external .ybn.
            //   • EmbedCollision=false (legacy) → composite serialised as a
            //     sibling .ybn file under stream/.
            bool wroteYbn = false;
            bool embeddedYbn = false;
            if (opts.IncludeCollision)
            {
                if (opts.EmbedCollision)
                {
                    Log("[5/6] Building collision (embedded into YDR)...");
                    try
                    {
                        var comp = YbnBuilder.BuildComposite(scene, opts.CollisionMaterial, opts.ExcludeMeshes, opts.PartMaterials, opts.BreakableGlass);
                        ydr.Drawable.Bound = comp;
                        // The drawable is already on disk at this point — re-save
                        // with the bound attached so the YDR carries its own
                        // collision and no .ybn is needed.
                        ydrBytes = ydr.Save();
                        File.WriteAllBytes(ydrOut, ydrBytes);
                        embeddedYbn = true;
                        Log($"  embedded into {Path.GetFileName(ydrOut)} ({ydrBytes.Length:N0} bytes total, mat={opts.CollisionMaterial})");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[ydr-writer] collision embed failed (non-fatal): {ex.GetType().Name}: {ex.Message}");
                    }
                }
                else
                {
                    Log("[5/6] Building collision (.ybn)...");
                    try
                    {
                        var ybnBytes = YbnBuilder.Build(scene, opts.CollisionMaterial, opts.ExcludeMeshes, opts.PartMaterials, opts.BreakableGlass);
                        var ybnOut = Path.Combine(streamDir, $"{opts.AssetName}.ybn");
                        File.WriteAllBytes(ybnOut, ybnBytes);
                        Log($"  wrote: {ybnOut} ({ybnBytes.Length:N0} bytes, mat={opts.CollisionMaterial})");
                        wroteYbn = true;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[ydr-writer] collision build failed (non-fatal): {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            else Log("[5/6] collision: skipped (--no-collision)");

            // Stage 5b: archetype metadata (.ytyp). Without this FiveM streams
            // the .ydr but the model can't be placed in a .ymap or referenced
            // by archetype-name spawn APIs — only by drawable name.
            bool wroteYtyp = false;
            if (opts.IncludeYtyp)
            {
                Log("[6/6] Building archetype metadata (.ytyp)...");
                try
                {
                    var d = ydr.Drawable;
                    // ytyp.lodDist is the entity-level cull radius — RAGE
                    // stops drawing the entity past this distance regardless
                    // of how many LOD tiers the drawable has. Use the same
                    // value as the LodDistVlow on the drawable so the
                    // outer-most YDR tier and the archetype agree on when
                    // the prop disappears entirely.
                    var ytypBytes = YtypBuilder.Build(
                        opts.AssetName,
                        d.BoundingBoxMin, d.BoundingBoxMax,
                        d.BoundingCenter, d.BoundingSphereRadius,
                        lodDist: opts.LodDistVLow);
                    var ytypOut = Path.Combine(streamDir, $"{opts.AssetName}.ytyp");
                    File.WriteAllBytes(ytypOut, ytypBytes);
                    Log($"  wrote: {ytypOut} ({ytypBytes.Length:N0} bytes)");
                    wroteYtyp = true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ydr-writer] ytyp build failed (non-fatal): {ex.GetType().Name}: {ex.Message}");
                }
            }
            else Log("[6/6] ytyp: skipped (--no-ytyp)");

            // Stage 6b: animation export. If the source mesh ships with
            // an animation track (most rigged FBX/GLB exports do) we
            // re-load the scene WITHOUT PreTransformVertices so the bone
            // hierarchy + NodeAnimationChannels survive, then sample the
            // first clip at 30 FPS and write it out as a sibling .ycd.
            // Bone names are resolved to RAGE 16-bit tags via
            // GtaBoneTags — channels that don't match a GTA player
            // skeleton bone get skipped without failing the export.
            //
            // The static .ydr remains the bind-pose drawable: a custom
            // skinned drawable would need a Skeleton attached and skinned
            // vertex buffers, which is a much bigger change. The .ycd
            // alone is enough for the common case of "play this animation
            // on a player ped via TaskPlayAnim".
            bool wroteYcd = false;
            string? ycdClipName = null;
            bool animatedProp = false;

            if (opts.AnimatedProp || opts.AutoSpin)
            {
                // Animated-prop path: attach a Skeleton + bind each model to
                // a bone (rigid) so the prop moves in-game. --auto-spin
                // SYNTHESIZES a 360° spin for a model with no animation of its
                // own; otherwise the clip is imported from the source rig.
                // The static drawable already on disk is re-saved afterwards
                // because the skeleton/bone-binding is applied to it in memory.
                Log(opts.AutoSpin
                    ? "[7/-] Building ANIMATED PROP (auto-spin)..."
                    : "[7/-] Building ANIMATED PROP (rigid bone-binding)...");
                try
                {
                    var ap = opts.AutoSpin
                        ? AnimatedPropBuilder.BuildAutoSpin(ydr, opts, streamDir)
                        : AnimatedPropBuilder.Build(ydr, opts.InputPath, opts, streamDir);
                    if (ap.Success)
                    {
                        // The skeleton-bearing YDR was already persisted inside
                        // the builder (before the .ycd/.ytyp), so there is no
                        // re-save here — that avoids a second write that could
                        // fail after the .ycd/.ytyp are committed and leave the
                        // resource internally inconsistent.
                        wroteYcd = true;
                        ycdClipName = ap.ClipName;
                        animatedProp = true;
                        Log("  animated prop ready — YDR saved with skeleton");
                    }
                    else
                    {
                        Log($"  animated-prop build skipped ({ap.Message}) — shipping a static prop instead.");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ydr-writer] animated-prop build failed (non-fatal): {ex.GetType().Name}: {ex.Message}");
                    Console.Error.WriteLine(ex.StackTrace);
                }
            }
            else
            {
                // Stage 6b: legacy ped-clip export. If the source ships an
                // animation track we re-load the scene WITHOUT
                // PreTransformVertices so the bone hierarchy +
                // NodeAnimationChannels survive, then sample the first clip
                // at 30 FPS and write a sibling .ycd whose tracks are mapped
                // to RAGE player-skeleton tags via GtaBoneTags — for playing
                // on a player ped via TaskPlayAnim.
                try
                {
                    Log("[7/-] Probing source for animation channels...");
                    var animScene = ImportForAnimation(opts.InputPath);
                    if (animScene is not null && animScene.HasAnimations)
                    {
                        var sampled = AnimationSampler.SampleFirstClip(animScene, fps: 30);
                        if (sampled is null)
                        {
                            Log("  no rotation channels mapped to GTA bones (skipping .ycd)");
                        }
                        else
                        {
                            ycdClipName = YcdAnimationBuilder.SanitizeClipName(
                                string.IsNullOrWhiteSpace(sampled.ClipName)
                                    ? opts.AssetName
                                    : sampled.ClipName);
                            var ycdBytes = YcdAnimationBuilder.Build(
                                ycdClipName, sampled.Tracks, sampled.Frames, sampled.Fps);
                            var ycdOut = Path.Combine(streamDir, ycdClipName + ".ycd");
                            File.WriteAllBytes(ycdOut, ycdBytes);
                            wroteYcd = true;
                            Log($"  clip '{sampled.ClipName}' -> {Path.GetFileName(ycdOut)}");
                            Log($"  {sampled.Tracks.Count} bone(s) mapped, {sampled.UnmappedChannels} unmapped, " +
                                $"{sampled.Frames} frame(s) @ {sampled.Fps} fps " +
                                $"({sampled.DurationSeconds:F2}s, {ycdBytes.Length:N0} bytes)");
                        }
                    }
                    else
                    {
                        Log("  no animations in source (static mesh)");
                    }
                }
                catch (Exception ex)
                {
                    // Animation export is best-effort — a malformed
                    // animation channel shouldn't prevent the .ydr from
                    // shipping. Log + continue.
                    Console.Error.WriteLine($"[ydr-writer] animation export failed (non-fatal): {ex.GetType().Name}: {ex.Message}");
                }
            }

            {
                if (animatedProp && ycdClipName != null)
                {
                    File.WriteAllText(Path.Combine(resourceDir, "fxmanifest.lua"),
                        AnimatedPropManifest(opts.AssetName, wroteYtyp, ycdClipName));
                    File.WriteAllText(Path.Combine(resourceDir, "client.lua"),
                        AnimatedPropClientScript(opts.AssetName, ycdClipName));
                    File.WriteAllText(Path.Combine(resourceDir, "README.md"),
                        AnimatedPropReadme(opts.AssetName, ycdClipName));
                }
                else
                {
                    File.WriteAllText(Path.Combine(resourceDir, "fxmanifest.lua"),
                        FxManifest(opts.AssetName, wroteYbn, wroteYtyp, wroteYcd, ycdClipName));
                }
            }

            Console.WriteLine();
            Console.WriteLine(new string('=', 60));
            Console.WriteLine($"  Resource folder ready: {resourceDir}");
            Console.WriteLine(new string('=', 60));
            return 0;
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("YDR_WRITER_KEEP_TEMP") == "1")
                Log($"  (kept work dir: {workDir})");
            else
                try { Directory.Delete(workDir, recursive: true); } catch { /* swallow */ }
        }
    }

    private static void Log(string s) => Console.WriteLine($"[ydr-writer] {s}");

    /// <summary>Up-axis convention of the source mesh — drives whether we
    /// rotate Y-up → Z-up before handing to CodeWalker.</summary>
    private enum SourceUp { YUp, ZUp }

    /// <summary>Resolve the source up axis from the user's CLI choice plus
    /// (for "auto") the file extension and FBX metadata. Default is Y-up
    /// because that's what Assimp normalises glTF/OBJ/DAE/PLY to. FBX gets
    /// special treatment: the file header carries an explicit UpAxis enum
    /// (0=X, 1=Y, 2=Z) which Assimp surfaces via scene root metadata.</summary>
    private static SourceUp ResolveSourceUpAxis(ConvertOptions opts, Scene scene)
    {
        var up = (opts.Up ?? "auto").ToLowerInvariant();
        if (up == "y_up") return SourceUp.YUp;
        if (up == "z_up") return SourceUp.ZUp;

        // auto: only FBX is ambiguous — game-pipeline FBX (Maya/Max) is
        // typically Z-up, Blender's default FBX export is Y-up. The FBX
        // header tells us; we read it from Assimp's scene metadata.
        var ext = Path.GetExtension(opts.InputPath).ToLowerInvariant();
        if (ext == ".fbx" && scene.RootNode?.Metadata != null
            && scene.RootNode.Metadata.TryGetValue("UpAxis", out var entry)
            && entry.Data is int axis)
        {
            // FBX UpAxis: 0=X, 1=Y, 2=Z. Anything other than Y is Z-up
            // for our purposes (FBX X-up is essentially never seen and
            // would need a different rotation anyway).
            return axis == 1 ? SourceUp.YUp : SourceUp.ZUp;
        }
        return SourceUp.YUp;
    }


    /// <summary>Bake the user's gizmo Scale/Rotation/Position into vertex
    /// coords so the exported YDR matches the preview. Composition order
    /// mirrors three.js: M = T · R · S, with R as XYZ-Euler in radians
    /// (the viewer hands us degrees). Normals/tangents/bitangents get the
    /// rotation only (uniform scale preserves direction; translation
    /// doesn't apply to directions). Skips itself when all three are
    /// identity to avoid pointless float churn on every export.</summary>
    private static void ApplyUserTransform(Scene scene, ConvertOptions opts)
    {
        // Per-axis scale. Identity (1,1,1) skips the multiply entirely.
        // Non-uniform values require the inverse-scale + renormalize trick
        // on normals/tangents below — multiplying a normal by the same
        // matrix that stretched the vertex would tilt it the wrong way.
        double scaleX = opts.Scale.X, scaleY = opts.Scale.Y, scaleZ = opts.Scale.Z;
        if (scaleX <= 0) scaleX = 1.0;
        if (scaleY <= 0) scaleY = 1.0;
        if (scaleZ <= 0) scaleZ = 1.0;
        bool hasScale = Math.Abs(scaleX - 1.0) > 1e-9 ||
                        Math.Abs(scaleY - 1.0) > 1e-9 ||
                        Math.Abs(scaleZ - 1.0) > 1e-9;
        bool nonUniformScale = hasScale &&
                               (Math.Abs(scaleX - scaleY) > 1e-9 ||
                                Math.Abs(scaleY - scaleZ) > 1e-9);

        double rxDeg = opts.Rotation.X, ryDeg = opts.Rotation.Y, rzDeg = opts.Rotation.Z;
        bool hasRot = Math.Abs(rxDeg) > 1e-9 || Math.Abs(ryDeg) > 1e-9 || Math.Abs(rzDeg) > 1e-9;

        double tx = opts.Position.X, ty = opts.Position.Y, tz = opts.Position.Z;
        bool hasTrans = Math.Abs(tx) > 1e-9 || Math.Abs(ty) > 1e-9 || Math.Abs(tz) > 1e-9;

        if (!hasScale && !hasRot && !hasTrans) return;

        double rx = rxDeg * Math.PI / 180.0;
        double ry = ryDeg * Math.PI / 180.0;
        double rz = rzDeg * Math.PI / 180.0;
        double cx = Math.Cos(rx), sx = Math.Sin(rx);
        double cy = Math.Cos(ry), sy = Math.Sin(ry);
        double cz = Math.Cos(rz), sz = Math.Sin(rz);

        // three.js XYZ-Euler: M = Rx · Ry · Rz, expanded so we can apply
        // it to each vertex with one mat-vec instead of three.
        double r00 = cy * cz,                  r01 = -cy * sz,                 r02 = sy;
        double r10 = cx * sz + sx * sy * cz,   r11 = cx * cz - sx * sy * sz,   r12 = -sx * cy;
        double r20 = sx * sz - cx * sy * cz,   r21 = sx * cz + cx * sy * sz,   r22 = cx * cy;

        // Inverse-scale factors for normal transforms. With diag(sx,sy,sz)
        // the inverse-transpose is diag(1/sx,1/sy,1/sz); renormalize after
        // applying because the magnitudes shift unevenly.
        double invSx = 1.0 / scaleX, invSy = 1.0 / scaleY, invSz = 1.0 / scaleZ;

        Log($"  applying user transform: scale=({scaleX:F4},{scaleY:F4},{scaleZ:F4}) rot=({rxDeg:F2},{ryDeg:F2},{rzDeg:F2})° pos=({tx:F3},{ty:F3},{tz:F3})");

        foreach (var mesh in scene.Meshes)
        {
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                var p = mesh.Vertices[i];
                double x = p.X, y = p.Y, z = p.Z;
                if (hasScale) { x *= scaleX; y *= scaleY; z *= scaleZ; }
                if (hasRot)
                {
                    double nx = r00 * x + r01 * y + r02 * z;
                    double ny = r10 * x + r11 * y + r12 * z;
                    double nz = r20 * x + r21 * y + r22 * z;
                    x = nx; y = ny; z = nz;
                }
                if (hasTrans) { x += tx; y += ty; z += tz; }
                mesh.Vertices[i] = new Vector3D((float)x, (float)y, (float)z);
            }
            // Normals: apply inverse-scale (only needed for non-uniform
            // scale; uniform scale preserves direction so we can skip it
            // and save the renormalize). Then apply the rotation.
            if ((hasRot || nonUniformScale) && mesh.HasNormals)
            {
                for (int i = 0; i < mesh.Normals.Count; i++)
                {
                    var n = mesh.Normals[i];
                    double nx = n.X, ny = n.Y, nz = n.Z;
                    if (nonUniformScale)
                    {
                        nx *= invSx; ny *= invSy; nz *= invSz;
                        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                        if (len > 1e-12) { nx /= len; ny /= len; nz /= len; }
                    }
                    if (hasRot)
                    {
                        double rxv = r00 * nx + r01 * ny + r02 * nz;
                        double ryv = r10 * nx + r11 * ny + r12 * nz;
                        double rzv = r20 * nx + r21 * ny + r22 * nz;
                        nx = rxv; ny = ryv; nz = rzv;
                    }
                    mesh.Normals[i] = new Vector3D((float)nx, (float)ny, (float)nz);
                }
            }
            // Tangents + bitangents follow vertex direction, not normal
            // direction, so they use the FORWARD scale (not inverse).
            if ((hasRot || nonUniformScale) && mesh.HasTangentBasis)
            {
                for (int i = 0; i < mesh.Tangents.Count; i++)
                {
                    var t = mesh.Tangents[i];
                    double tx2 = t.X, ty2 = t.Y, tz2 = t.Z;
                    if (nonUniformScale)
                    {
                        tx2 *= scaleX; ty2 *= scaleY; tz2 *= scaleZ;
                        double len = Math.Sqrt(tx2 * tx2 + ty2 * ty2 + tz2 * tz2);
                        if (len > 1e-12) { tx2 /= len; ty2 /= len; tz2 /= len; }
                    }
                    if (hasRot)
                    {
                        double rxv = r00 * tx2 + r01 * ty2 + r02 * tz2;
                        double ryv = r10 * tx2 + r11 * ty2 + r12 * tz2;
                        double rzv = r20 * tx2 + r21 * ty2 + r22 * tz2;
                        tx2 = rxv; ty2 = ryv; tz2 = rzv;
                    }
                    mesh.Tangents[i] = new Vector3D((float)tx2, (float)ty2, (float)tz2);
                }
                for (int i = 0; i < mesh.BiTangents.Count; i++)
                {
                    var b = mesh.BiTangents[i];
                    double bx = b.X, by = b.Y, bz = b.Z;
                    if (nonUniformScale)
                    {
                        bx *= scaleX; by *= scaleY; bz *= scaleZ;
                        double len = Math.Sqrt(bx * bx + by * by + bz * bz);
                        if (len > 1e-12) { bx /= len; by /= len; bz /= len; }
                    }
                    if (hasRot)
                    {
                        double rxv = r00 * bx + r01 * by + r02 * bz;
                        double ryv = r10 * bx + r11 * by + r12 * bz;
                        double rzv = r20 * bx + r21 * by + r22 * bz;
                        bx = rxv; by = ryv; bz = rzv;
                    }
                    mesh.BiTangents[i] = new Vector3D((float)bx, (float)by, (float)bz);
                }
            }
        }
    }

    /// <summary>Y-up (glTF/OBJ) -> Z-up (GTA): map (x, y, z) -> (x, -z, y),
    /// matching converter/mesh.py:transform_to_gta_space.
    ///
    /// Important: we bake the transform directly into vertex positions
    /// and normals on every Mesh. Assimp's FBX exporter ignores
    /// scene.RootNode.Transform — meshes are written in their local-space
    /// vertex coordinates, with the node hierarchy emitted as metadata
    /// that CodeWalker.FbxConverter doesn't currently apply.</summary>
    private static void ApplyYupToZupTransform(Scene scene)
    {
        foreach (var mesh in scene.Meshes)
        {
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                var p = mesh.Vertices[i];
                mesh.Vertices[i] = new Vector3D(p.X, -p.Z, p.Y);
            }
            if (mesh.HasNormals)
            {
                for (int i = 0; i < mesh.Normals.Count; i++)
                {
                    var n = mesh.Normals[i];
                    mesh.Normals[i] = new Vector3D(n.X, -n.Z, n.Y);
                }
            }
            if (mesh.HasTangentBasis)
            {
                for (int i = 0; i < mesh.Tangents.Count; i++)
                {
                    var t = mesh.Tangents[i];
                    mesh.Tangents[i] = new Vector3D(t.X, -t.Z, t.Y);
                }
                for (int i = 0; i < mesh.BiTangents.Count; i++)
                {
                    var b = mesh.BiTangents[i];
                    mesh.BiTangents[i] = new Vector3D(b.X, -b.Z, b.Y);
                }
            }
        }
    }

    /// <summary>Second Assimp pass used by the animation exporter. Keeps
    /// the node hierarchy + bone channels intact (PreTransformVertices
    /// would flatten them) so <see cref="AnimationSampler"/> can walk
    /// <c>scene.Animations[0].NodeAnimationChannels</c>. Returns null if
    /// the import fails so the caller can downgrade to "no animation
    /// detected" without aborting the .ydr write.</summary>
    private static Scene? ImportForAnimation(string path)
    {
        try
        {
            using var ai = new AssimpContext();
            // Triangulate is still useful (animation pass doesn't touch
            // mesh data but the import would fail on n-gon-only files).
            // PreTransformVertices is deliberately omitted — that's the
            // whole reason for the second pass.
            return ai.ImportFile(path, PostProcessSteps.Triangulate);
        }
        catch
        {
            return null;
        }
    }

    private static string FxManifest(string name, bool ybn, bool ytyp, bool ycd = false, string? ycdClipName = null)
    {
        // `this_is_a_map 'yes'` is the load-bearing line: without it FiveM
        // ignores any .ymap dropped into stream/ — the archetype registers
        // (so CreateObject works from script) but the placement entities a
        // user authors in CodeWalker after the fact never appear in the
        // world. Enabling the map flag costs nothing for resources that
        // don't ship a .ymap (the .ydr/.ytyp still load as before) and
        // means "open in CodeWalker, place, save .ymap" just works.
        //
        // We don't emit a CONTENT_UNLOCKING_META / _manifest.ymf line here
        // because we don't generate the .ymf — referencing a missing file
        // is a hard resource-load error. CodeWalker writes a _manifest.ymf
        // alongside any .ymap it saves; when a user does that, they need
        // to add the data_file line themselves (or rerun this export and
        // copy the .ymap/.ymf in afterwards).
        var sb = new System.Text.StringBuilder();
        sb.Append("fx_version 'cerulean'\n");
        sb.Append("game 'gta5'\n\n");
        sb.Append("this_is_a_map 'yes'\n\n");
        if (ytyp)
        {
            sb.Append("files {\n");
            sb.Append($"    'stream/{name}.ytyp',\n");
            sb.Append("}\n\n");
            sb.Append($"data_file 'DLC_ITYP_REQUEST' 'stream/{name}.ytyp'\n");
        }
        if (ycd && !string.IsNullOrEmpty(ycdClipName))
        {
            // .ycd files under stream/ load automatically — no
            // `files`/`data_file` line needed. The block below is a
            // copy-paste hint for the user: RequestAnimDict + TaskPlayAnim
            // plays the bundled clip on the local player.
            sb.Append('\n');
            sb.Append($"-- Animation clip detected in the source mesh. The .ycd ships under\n");
            sb.Append($"-- stream/ and is auto-loaded by FiveM. To play it from a client script:\n");
            sb.Append($"--\n");
            sb.Append($"--   RequestAnimDict('{ycdClipName}')\n");
            sb.Append($"--   while not HasAnimDictLoaded('{ycdClipName}') do Wait(10) end\n");
            sb.Append($"--   TaskPlayAnim(PlayerPedId(), '{ycdClipName}', '{ycdClipName}',\n");
            sb.Append($"--     8.0, 8.0, -1, 49, 0, false, false, false)\n");
        }
        return sb.ToString();
    }

    /// <summary>fxmanifest for an animated prop: streams the .ydr/.ytyp/.ycd
    /// AND ships a client script that spawns the object and plays the clip
    /// on it. `this_is_a_map` registers the archetype so CreateObject works.</summary>
    private static string AnimatedPropManifest(string name, bool ytyp, string clipName)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("fx_version 'cerulean'\n");
        sb.Append("game 'gta5'\n\n");
        sb.Append("author 'FiveOS'\n");
        sb.Append($"description 'Animated prop {name} (clip: {clipName})'\n\n");
        sb.Append("this_is_a_map 'yes'\n\n");
        if (ytyp)
        {
            sb.Append("files {\n");
            sb.Append($"    'stream/{name}.ytyp',\n");
            sb.Append("}\n\n");
            sb.Append($"data_file 'DLC_ITYP_REQUEST' 'stream/{name}.ytyp'\n\n");
        }
        // The demo spawner. Remove/replace with your own logic once you've
        // confirmed the prop animates.
        sb.Append("client_script 'client.lua'\n");
        return sb.ToString();
    }

    /// <summary>Demo client script: spawns the animated prop in front of the
    /// player and drives the embedded clip with PlayEntityAnim (the object
    /// path — NOT TaskPlayAnim, which is for peds).</summary>
    private static string AnimatedPropClientScript(string name, string clipName)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("-- Animated prop generated by FiveOS.\n");
        sb.Append("-- Spawns the prop ~2m in front of you and loops its clip.\n");
        sb.Append("-- Use /").Append(name).Append("anim to (re)spawn it.\n\n");
        sb.Append($"local MODEL = `{name}`\n");
        sb.Append($"local ANIM_DICT = '{clipName}'\n");
        sb.Append($"local ANIM_NAME = '{clipName}'\n\n");
        sb.Append("local spawned = nil\n\n");
        sb.Append("local function spawnAnimatedProp()\n");
        sb.Append("    RequestModel(MODEL)\n");
        sb.Append("    local t = GetGameTimer()\n");
        sb.Append("    while not HasModelLoaded(MODEL) do\n");
        sb.Append("        Wait(10)\n");
        sb.Append("        if GetGameTimer() - t > 10000 then print('[").Append(name).Append("] model failed to load'); return end\n");
        sb.Append("    end\n");
        sb.Append("    RequestAnimDict(ANIM_DICT)\n");
        sb.Append("    t = GetGameTimer()\n");
        sb.Append("    while not HasAnimDictLoaded(ANIM_DICT) do\n");
        sb.Append("        Wait(10)\n");
        sb.Append("        if GetGameTimer() - t > 10000 then print('[").Append(name).Append("] anim dict failed to load'); return end\n");
        sb.Append("    end\n\n");
        sb.Append("    local ped = PlayerPedId()\n");
        sb.Append("    local pos = GetOffsetFromEntityInWorldCoords(ped, 0.0, 2.0, 0.0)\n");
        sb.Append("    if spawned and DoesEntityExist(spawned) then DeleteEntity(spawned) end\n");
        sb.Append("    spawned = CreateObject(MODEL, pos.x, pos.y, pos.z, true, true, false)\n");
        sb.Append("    SetEntityHeading(spawned, GetEntityHeading(ped) + 180.0)\n");
        sb.Append("    -- PlayEntityAnim(entity, animName, animDict, speed, loop, stayInAnim, p6, delta, bitset)\n");
        sb.Append("    PlayEntityAnim(spawned, ANIM_NAME, ANIM_DICT, 1000.0, true, true, false, 0.0, 0)\n");
        sb.Append("    SetModelAsNoLongerNeeded(MODEL)\n");
        sb.Append("    RemoveAnimDict(ANIM_DICT)\n");
        sb.Append($"    print('[{name}] spawned + playing clip ").Append(clipName).Append("')\n");
        sb.Append("end\n\n");
        sb.Append($"RegisterCommand('{name}anim', function() spawnAnimatedProp() end, false)\n");
        return sb.ToString();
    }

    private static string AnimatedPropReadme(string name, string clipName)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"# {name} — animated prop (FiveOS)\n\n");
        sb.Append("Rigid bone-bound animated prop. The `.ydr` carries an embedded\n");
        sb.Append($"skeleton; `stream/{clipName}.ycd` holds the clip; the `.ytyp` has\n");
        sb.Append("the **Has Anim (YCD)** + **Auto Start Anim** flags + clip-dictionary\n");
        sb.Append("reference (the Sollumz/CodeWalker method). When placed in a **.ymap**\n");
        sb.Append("it is set up to auto-play with no script — but confirm this in-game,\n");
        sb.Append("auto-start behaviour can depend on your placement path/build. The\n");
        sb.Append("bundled `client.lua` (`PlayEntityAnim`) is the guaranteed fallback\n");
        sb.Append("and a spawn-to-test helper.\n\n");
        sb.Append("## Install\n\n");
        sb.Append($"1. Drop the `{name}_resource` folder into your server `resources/`.\n");
        sb.Append($"2. `ensure {name}_resource` in `server.cfg` (rename the folder if you like).\n");
        sb.Append($"3. In-game run `/{name}anim` — the prop spawns ~2m in front of you and loops.\n\n");
        sb.Append("## Play it yourself\n\n");
        sb.Append("```lua\n");
        sb.Append($"local obj = CreateObject(`{name}`, x, y, z, true, true, false)\n");
        sb.Append($"RequestAnimDict('{clipName}')\n");
        sb.Append($"while not HasAnimDictLoaded('{clipName}') do Wait(0) end\n");
        sb.Append($"PlayEntityAnim(obj, '{clipName}', '{clipName}', 1000.0, true, true, false, 0.0, 0)\n");
        sb.Append("```\n\n");
        sb.Append("## Doesn't move in-game?\n\n");
        sb.Append("Rigid binding animates whole parts, so the model needs its moving\n");
        sb.Append("pieces as **separate meshes** in the source. A single one-piece\n");
        sb.Append("skinned mesh won't articulate this way. Tell FiveOS and it can try\n");
        sb.Append("per-vertex skinning instead.\n");
        return sb.ToString();
    }
}
