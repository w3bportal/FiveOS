# Automatic skin weights for custom clothing — feasibility and scope

Research dossier, 2026-08-01. Question: can FiveOS take an unrigged garment mesh
(hoodie, jacket, backpack) and automatically produce game-correct skin weights
against the GTA freemode skeleton, shipped in the free/open tier?

**Answer: yes. The algorithm is a solved problem with a permissive reference
implementation, every primitive it needs is already in the binary, and the
differentiating work is one that no competing tool does at all. The real cost is
not the weighting — it is that the exporter cannot write skinned drawables yet.**

---

## 1. The algorithm

**Robust Skin Weights Transfer via Weight Inpainting** — Abdrashitov, Raichstat,
Monsen, Hill (Epic Games), SIGGRAPH Asia 2023.
Paper: https://dl.acm.org/doi/10.1145/3610543.3626180 ·
Reference code (**MIT**): https://github.com/rin-23/RobustSkinWeightsTransferCode

It was written for precisely our case: copying weights from a correctly-skinned
body onto a loose garment of unrelated topology. The insight is that naive
closest-point transfer is right most of the time and catastrophically wrong the
rest of the time — and you can *detect which is which* instead of guessing.

**Stage 1 — match, with a confidence test.** For each garment vertex, find the
closest point on the donor body, and interpolate both its weights and its normal
barycentrically. Accept the match only if

- distance ≤ `D`, where `D = 0.05 × garment bounding-box diagonal`, and
- angle between the garment normal and the donor normal ≤ ~30°.

The normal test is what earns its keep. A backpack strap hovering 2 cm from the
upper arm has a normal unrelated to the arm, so it is *rejected* rather than
silently welded to the wrong limb. Armpit and crotch vertices — where two
geodesically distant body parts are Euclidean-adjacent — get rejected for the
same reason. A second pass with flipped normals catches jacket linings, whose
normals point inward.

**Stage 2 — inpaint the remainder.** Everything rejected is *solved for*:

```
argmin_W  trace( Wᵀ (−L + L M⁻¹ L) W )
subject to  W(k,·) = s_k  for every confident vertex k
```

`L` is the cotangent Laplacian of the *garment*, `M` the Voronoi mass matrix.
The `L M⁻¹ L` term is discrete thin-plate energy, so the fill is smooth and
extrapolates sensibly past the constrained region. Because it diffuses across the
garment's own connectivity rather than through the air, it inherits the
"geodesic, not Euclidean" property that makes voxel binding good — without
voxelizing anything.

Solve by partitioning into known/unknown and factorizing once:
`Q_UU · W_U = −Q_UI · W_I`. Every bone is just another right-hand side against the
same factorization, so **128 bones cost barely more than one**.

**Stage 3 — smooth the seam**, then clamp to 4 influences and re-normalize.

### Why not the alternatives

| Method | Verdict |
|---|---|
| **Heat diffusion** (Blender's Automatic Weights, Baran & Popović 2007) | Needs a closed interior for its visibility test. A jacket is an open shell with cuffs and a hem. This is exactly the geometry that produces Blender's *"Bone Heat Weighting: failed to find solution"*. Also throws the donor away. |
| **Bounded Biharmonic Weights** (Jacobson 2011) | Needs a tetrahedral mesh of a volume the garment doesn't have, plus a commercial QP solver. Reported 4 ms–12 s *per handle*; at 128 bones that is minutes. |
| **Geodesic Voxel Binding** (Maya's default) | Right idea, wrong discretization: thin straps and fabric shells fall below voxel size and merge or vanish. Also requires joints inside the mesh volume, false by construction for a garment. |
| **Neural auto-rigging** (RigNet, NeuroSkinning, UniRig) | Predicts a skeleton we already know, needs a GPU and gigabyte checkpoints, and wants the mesh remeshed to 1–5k vertices. Structurally wrong: they infer from geometry what we already possess exactly. |

The decisive argument for the top pick is that it is the **only** method that
exploits the fact that we ship ground-truth engine weights.

---

## 2. Licensing — clean, with zero new dependencies

The consuming code is GPL-3.0 and this ships in the free tier.

- Reference implementation is **MIT**. Usable directly.
- Every primitive is already in **geometry3Sharp**, which FiveOS already
  references (`FiveOS.csproj:109`, `Geometry3Sharp.DotNet8.Unofficial 1.0.0-d8`).
  Verified present in the shipped DLL: `DMeshAABBTree3.FindNearestTriangle`,
  `MeshWeights.CotanCentroid`, `MeshWeights.VoronoiArea`,
  `SymmetricSparseMatrix`, `SparseSymmetricCGMultipleRHS`.
- **No libigl, no native interop, no P/Invoke, no new NuGet package.**
  Estimated 400–600 lines of C# in one file.

**Traps avoided:**

- **TetGen is AGPL-3.0** and poisons the classic biharmonic route — but the
  inpainting solve is on the *surface* Laplacian, so we never touch it.
- **libigl scans as GPL-3.0** (its repo root carries both `LICENSE.GPL` and
  `LICENSE.MPL2`) even though `include/igl/` proper is MPL-2.0. Irrelevant if we
  don't vendor it, but worth knowing if a compliance scan ever runs.
- **CSparse.NET is LGPL-2.1+**, not MIT. Only needed if the iterative solver
  stalls. Legal for the free tier; would weld that code out of any paid tier.

**Correction to make:** `credits.json` lists geometry3Sharp as MIT. Upstream is
**Boost Software License 1.0**. Both are permissive and GPL-3.0-compatible, so
there is no exposure — the attribution is just wrong.

**Numerical risk to plan for:** the system matrix `−L + L M⁻¹ L` is a
bi-Laplacian, whose conditioning degrades as ~h⁻⁴. Never form `Q` explicitly —
apply `L`, `M⁻¹`, `L` in sequence inside the matvec, and add a Jacobi
preconditioner. Fall back to a direct sparse factorization only if it stalls.

---

## 3. The actual blocker: FiveOS cannot write skinned drawables

This is the finding that sizes the project, and it is not the algorithm.

**Skinned export does not exist today — not partially, not at all.**

- `DirectFbxBuilder` emits `Geometry`, `Model`, `Material`, `Connections` and
  nothing else. There is no `Deformer`/`SubDeformer`/`Cluster` node — the FBX
  handed downstream is structurally incapable of carrying skin.
- CodeWalker's `FbxConverter.GetVertexDeclaration` was disassembled: 109 bytes of
  IL that hardcodes exactly **two** unskinned declarations (`Default` stride 36,
  `DefaultEx` stride 52), chosen on a single shader-name hash. Semantic bits 1
  and 2 — BlendWeights and BlendIndices — are clear in both. No path through it
  can ever emit skin.
- Every write to `HasSkin` in the repo sets it to **0**
  (`AnimatedPropBuilder.cs:222`, `:314`, `:395`). `DrawableGeometry.BoneIds` is
  never authored, only cloned for LODs.
- The code says so out loud (`Converter.cs:390-394`): *"a custom skinned drawable
  would need a Skeleton attached and skinned vertex buffers, which is a much
  bigger change."*

**But the hard part is already solved in the vendored library.** CodeWalker.Core
has complete skinned support that the app simply never calls — 17 `PBB*` skinned
vertex types, `VertexTypeGTAV1.BlendWeights/BlendIndices`, `SetUByte4`,
`DrawableGeometry.BoneIds`, `DrawableModel.HasSkin`, full `Skeleton` handling,
and an XML round-trip importer (`XmlYdr.GetYdr`) that natively carries blend
columns.

This was verified live, not from signatures — constructing a `PBBNCT`
declaration produced the correct stride 44 / 6 components with BlendWeights at
offset 12 and BlendIndices at 16, and `SetUByte4` wrote into the right rows.

**So this is a producer-side problem, not format reverse-engineering.** Two
routes, both viable:

1. Construct `DrawableGeometry`/`VertexBuffer`/`VertexData` directly with a
   `PBB*` declaration, bypassing `FbxConverter`.
2. Emit `.ydr.xml` and call `XmlYdr.GetYdr`. **There is already precedent for
   this exact pattern** — `AnimatedPropBuilder.cs:227-230` hand-authors Skeleton
   XML and lets CodeWalker parse it.

---

## 4. The donor is already in the binary

`src/Assets/Viewer/reference/freemode_male.glb` and `freemode_female.glb`
contain **real game weights**:

- 128-joint skin with verbatim GTA bone names (`SKEL_ROOT`, `SKEL_Pelvis`,
  `SKEL_L_Thigh`, `RB_L_ArmRoll`, …) plus inverse-bind matrices.
- `JOINTS_0` as UNSIGNED_BYTE, `WEIGHTS_0` as FLOAT, and **no `JOINTS_1`** —
  i.e. max 4 influences, matching RAGE exactly.
- Male body 7,237 v / 11,933 tri; female 7,550 v / 11,730 tri. Consistent with a
  straight export of the real `mp_m/f_freemode_01` bodies.

**Four caveats for whoever writes the loader:**

1. Each file contains **multiple skins**. Naive "take skin 0" gets the Blender
   `control_rig`, not the deform rig. The viewer already fights this by
   preferring the mesh under `GAME_RIG`.
2. **The female file has no `GAME_RIG` node** — its deform skin is `head_000_r`,
   so the male heuristic does not transfer.
3. Assets are in **Y-up display space**, and the male is rotated by π in the
   viewer. Transfer must account for the same Y→Z conversion the exporter applies.
4. The body is **one welded mesh**, not split by component, so a torso garment
   needs the donor restricted spatially rather than by mesh name.

---

## 5. Format constraints the writer must honour

Verified against real Rockstar `.ydd` files and the vendored library.

- **Exactly 4 influences per vertex.** `BlendWeights` and `BlendIndices` are each
  4 × `uint8`.
- **Weights must sum to exactly 255**, not 256. Measured across 4,485 real
  vertices from `mp_m_freemode_01\uppr_000_r.ydd`: zero deviations. Pack as
  `round(w·255)` and push the residual into the largest slot.
- **`BlendIndices` → `BoneIds[b]` → skeleton bone *index*, not tag.** Skinning
  uses indices; animation uses tags. Conflating them is the classic bug.
- **Real clothing uses an identity 128-entry palette.** 36/36 sampled geometries.
  Emit `ushort[128]{0..127}` and let blend indices be raw bone indices — it is
  also CodeWalker's renderer fast path.
- **Vertex layouts:** High = flags `0x40FF`, stride **72** (2 UV sets + tangent);
  Med/Low = flags `0x7F`, stride **48**. Ped texcoords are `Float2`, not `Half2`.
- **`VertexBuffer.Data1`, `Data2` and `geom.VertexData` must be the same object**
  or the runtime sees zero vertices while the resource report looks fine
  (already documented at `DrawableLodBuilder.cs:146-148`).
- **Vertex colours are required, not cosmetic.** Colour0 ≈ `#FF8000`, Colour1
  black with zero alpha. Wrong values produce jittering or permanently wet-looking
  garments.
- **LOD distances are all `9998`** for clothing — ped LOD is driven by the ped
  system, not per-drawable distances. Do not copy map-asset values.
- Clothing needs **High/Med/Low** (no VLow). Ship only High and the garment
  vanishes at distance — the most common amateur-pack symptom.

---

## 6. What this does *not* solve

Worth stating plainly in the UI, or it generates support load.

1. **Poke-through.** Weights control how the garment follows the skeleton, not
   where it sits. Elbows, shoulders and knees will still clip at pose extremes.
   The standard fix is cutting the body geometry underneath, not weighting.
2. **The `uppr` pairing.** A `jbib` top needs a matching component-3 arms
   drawable, authored without the parts the jacket covers. GTA has no general
   "hide component N" flag — the mechanism is `forcedComponents` in the shop
   meta, which most FiveM servers bypass entirely by setting components directly.
   A tool should emit the pairing as script-consumable data, not only in the meta.
3. **Disconnected pieces.** Buttons and separate panels are the paper's
   acknowledged weak spot: a component with zero confident matches makes the
   solve singular. Needs an explicit per-component fallback.
4. **Hoods, coat tails, hanging bags.** Inpainting extrapolates smoothly but
   arbitrarily — it does not know a hood should follow the head.
5. **Rigid accessories.** Buckles and armour plates want hard single-bone
   assignment; energy minimization will smooth-blend them.
6. **Male and female are two garments.** Different proportions, separate packs,
   separate metadata. No shortcut.
7. **The donor's own flaws propagate**, notably in armpit and crotch.

Realistic pitch: *eliminates ~90% of the weight-painting labour and produces a
garment that animates correctly for ordinary locomotion; shoulders, hoods and
loose pieces still want a look.*

---

## 7. Competitive position

Every tool in this space is a **packager**, and none of them rig:

| Tool | License | Rigs? | Packages? |
|---|---|---|---|
| grzyClothTool | GPL-3.0 | ❌ | ✅ ymt, auto 128-split |
| Durty Cloth Tool | proprietary, paid | ❌ | ✅ + texture re-encode |
| atelier | open (verify) | ❌ | ✅ binary ymt |
| Sollumz | MIT | **manual, in Blender** | ❌ |

The Sollumz FAQ's answer to *"my clothing doesn't move"* is *"you didn't
actually rig your mesh."* That is the expertise-gated, entirely manual step, and
it is the one nobody in the GTA space has automated.

Verified directly (2026-08-01), because it is the load-bearing claim:

- **grzyClothTool** README lists texture preview, prop preview, multi-drawable
  preview, hair-shrink and heel-height visualisation, and auto 128-split. No
  mention of rig, skin, weights or bones anywhere.
- **Durty Cloth Tool** advertises 16 capabilities — 3D/animation preview, hats
  and heels, cloth options, mass import, data resolving, tattoos, cloth analysis,
  error list, texture optimization, project management, import/export. Its own
  wording is *"inspect GTA V models, textures, fitting, rigging, LODs, and
  animations before export"* and *"test whether clothes are correctly rigged"* —
  reviewing finished rigging, not producing it. Its FAQ says *"the cloth may need
  to be re-rigged to function properly with a new drawable type"*, i.e. the user
  does that elsewhere.

**One qualifier, outside the GTA space.** *Clothy3D Studio* (beta since Oct 2025,
Windows, free with a 100-export/month cap, paid tier planned) does auto-fit and
rig clothing onto arbitrary characters and explicitly transfers skin weights and
topology. OBJ/FBX/DAE in, OBJ/FBX out; targets Daz, Marvelous Designer, MetaHumans,
Mixamo. It is **not** GTA-aware — no freemode skeleton, no `.ydd`, no component
slots, no ymt — so a user would have to drive it manually with the freemode body
as the target and still hand-finish the GTA export. It does prove the approach is
commercially viable, and it is the closest existing workaround today.

The GTA-native niche is therefore still open; the honest framing is "nobody has
automated this *for GTA*", not "nobody has automated this anywhere."

### What Clothy3D's shipped build tells us

Inspecting the distributed binary's dependency manifest and data layout (bill of
materials only — not its algorithm) independently confirms the architecture
recommended above:

| Shipped component | Role | Corresponds to |
|---|---|---|
| `flann.dll` / `flann_cpp.dll` | approximate nearest-neighbour search | the closest-point correspondence stage |
| `MyUmfPack.dll` (verified: exports `umfpack`/`UMFPACK`) | sparse direct LU | the linear solve |
| `MyAmd.dll` (verified: `amd_order`, "approximate minimum degree") | fill-reducing ordering | pre-factorization ordering |
| `HDataGenerated/L/base/body.fbx` — contains `Deformer`/`SubDeformer`/`Cluster`/`Skin`/`Weights`/`LimbNode` | a **rigged canonical donor body** | our freemode donor |
| `libfbxsdk`, VTK 6.1, Qt 5 | I/O, mesh/render, UI | — |

Two conclusions. First, **nothing exotic is required**: VTK 6.1 and FLANN date
from 2014 and Qt from 2016, so this is mature commodity geometry processing, not
research-grade novelty. Second, they solve with a **sparse direct factorization**
rather than an iterative method — worth noting against the conditioning risk in
§2, and a signal that the direct route is what production quality needs.

**A licensing asymmetry in our favour:** UMFPACK is GPL-licensed, so a
closed-source product must obtain a commercial licence to ship it. FiveOS is
GPL-3.0 and can use SuiteSparse solvers freely. The heavy solver machinery a
proprietary competitor has to pay for is free to us.

**Clean-room boundary:** build from the published paper and the MIT reference
implementation. Do not disassemble or port any part of a proprietary competitor —
it is unnecessary given an MIT implementation exists, and it would contaminate
the GPL codebase.

**Nobody generates skin-aware LODs either** — and FiveOS already has ~80% of that
machinery (`LodGenerator.cs`, `DrawableOptimizer.cs`), already stride-blind so
blend data carries through decimation untouched.

---

## 7b. Phase 1 STATUS: DONE — skinned export works

`src/Services/SkinnedDrawableBuilder.cs` writes a valid skinned clothing `.ydd`
from scratch. Verified by round-trip (31/31 field checks) against a file it
generated and reloaded through the app's own resource loader.

Corrections to earlier assumptions, all measured on ~3400 real clothing files:

- **The declaration is chosen by SHADER, not LOD tier.** The "High = 0x40FF /
  Med+Low = 0x7F" rule is wrong — all five layouts appear at High, and 0x40FF
  appears at Low. What varies is tangent (0x4000), texcoord1 (0x0080) and
  colour1 (0x0020).
- **Weights are not always exactly 255** in shipped assets (255 ×393,925 /
  256 ×6,351 / 254 ×5,766). ±1 is tolerated in game. Normalize to 255 when
  authoring, but do not reject an import over it.
- **Blend channels are declared `Colour` (9), not `UByte4` (8)** — same 4-byte
  layout, and `SetColour`/`SetUByte4` are byte-identical in effect.
- **`Drawable.Skeleton` is null in only 330/500** sampled files; 170 embed one.
  Null is correct for freemode components; the loader must not assume it.
- **Round-trip is NOT byte-stable**, so validation must be structural. `Save()`
  *is* idempotent (300/300), which gives a stable normalized baseline.

Traps that cost real time, now encoded in the builder:

1. `VertexDeclaration.Types` must be set **before** `UpdateCountAndStride()`, or
   you silently get stride 0.
2. `VertexData.Info` must be set **before** `AllocateData()`, or the buffer is
   null and every `Set*` writes into nothing — no exception.
3. `VertexBuffer.Data1`, `Data2` and `DrawableGeometry.VertexData` must be the
   **same instance**. Three instances still pass every CodeWalker round-trip
   check, but RAGE dereferences Data1 and sees zero vertices. Sharing is
   per-geometry; the declaration object can be shared across geometries.
4. `ShaderParametersBlock.Count` must be assigned or **every shader parameter is
   silently lost** on reload — `Write()` emits the block's count, not
   `ShaderFX.ParameterCount`.
5. `Drawable.RenderMaskFlagsHigh` and `DrawableModel.RenderMaskFlags` use
   **opposite byte orders** for the same logical value (0x0000FF01 vs 0x01FF).
   `BuildRenderMasks()` never fills the bucket byte — set it by hand.
6. Mandatory or `Save()` throws: `LightAttributes` (non-null), `ShaderMapping`,
   `BoundsData`, `GeometriesCount1/2/3`, `vb.Info`, `vb.VertexStride`,
   `ib.IndicesCount`, `shader.ParameterSize`/`ParameterDataSize`.

Constants worth not re-deriving: `Types = GTAV1 = 0x7755555555996996`
(2058/2058 geometries) · `Unknown_62h = 3` (2058/2058) · `BoneIds` identity[128]
(2052/2058) · `SkeletonBinding = 0x00000180` (HasSkin=1, low byte is the bound
bone count) · `LodDist* = 9998` (500/500) · ped shader `Name = JenkHash("ped")`,
`FileName = 540746503`, 12 parameters (4 textures first, then 8 vectors),
`ParameterSize = 320`, `ParameterDataSize = 400`.

## 7c. Phase 2 STATUS: DONE — weights generate

`src/Services/GarmentSkinTransfer.cs` implements the three-stage method.
Validated against the real freemode donor (`freemode_male.glb`, 7,237 v / 128
bones), measured not asserted:

| Test | Result |
|---|---|
| **Self-transfer** (donor == garment) | dominant bone reproduced on **99.31%**, mean absolute weight error **0.0134**, 854 ms |
| **Jacket** (torso+arms, 2 cm offset, 1,619 v) | dominant bone **96.54%**, 88% matched directly, **95 ms** |
| **Stress** (40 cm shell — nothing matches, all inpainted) | completes without diverging, 9.4 s |
| **End to end** | transferred weights → a 34,878-byte skinned `.ydd` |

Two findings worth keeping:

1. **The Jacobi preconditioner must use the exact diagonal of Q.** Probing it
   with an all-ones vector — the obvious shortcut — returns ~0 for every
   interior vertex, because a Laplacian's rows sum to zero. That silently
   disables preconditioning: the jacket case burned 200k CG iterations in 37 s.
   Computing `diag_i = −L_ii + Σ_k L_ik²·invMass_k` directly from the sparse
   rows cut it to 26k iterations, and the realistic case to **886 iterations in
   95 ms** — a 7× speedup on the stress case and far more on the real one.
2. **Smoothing trades accuracy for continuity.** Measured against a known-good
   donor it is monotonically *worse* the more you apply (dominant 96.60% at 0
   passes → 96.29% at 20; mean error 0.056 → 0.070). Its purpose is hiding the
   seam between transferred and solved regions, which this metric cannot see, so
   the default was reduced from the paper's 10 to 4 rather than removed.

Note on the whole-body offset shell: it is *not* a fair garment proxy. At 2 cm
the fingers, armpits and inner thighs self-intersect, so closest-point correctly
refuses ~29% of it. The torso-and-arms region is the representative case.

## 7d. Phase 3 STATUS: DONE — LODs generate

`src/Services/GarmentLodBuilder.cs` produces the High/Med/Low tiers. Each tier is
decimated and then **re-weighted against the donor** rather than inheriting
interpolated weights from the tier above — that avoids compounding error and
sidesteps the known weakness of quadric collapse, which has no bone-aware error
metric and distorts skinning past ~70% reduction. Open boundaries (hems, cuffs)
are pinned so the silhouette does not shrink as the player walks away.

Measured on the jacket: **3 tiers in 208 ms** — 2,446 → 1,222 → 654 triangles —
producing a 109,760-byte three-LOD wearable `.ydd`, all tiers skinned, Med/Low
render masks set, VLow correctly absent.

Gotcha: do **not** call `DMesh3.CompactInPlace()` after reduction — g3 throws
(`SmallListSet.MoveTo: list is empty`). Sparse vertex ids are harmless because
everything downstream walks `VertexIndices()` and remaps.

**Full pipeline status: garment mesh in → wearable three-LOD skinned `.ydd` out.**
56 checks green (31 writer, 25 transfer + LOD).

## 7e. Base COMPLETE — one call, file in / wearable file out

`src/Services/ClothingBuilder.cs` is the entry point:

```csharp
var report = ClothingBuilder.Build(new ClothingBuilder.Request {
    MeshPath = "jacket.fbx", ComponentName = "jbib_042_u", Variant = "male" });
File.WriteAllBytes("mp_m_freemode_01^jbib_042_u.ydd", report.Ydd);
```

Import → auto-weight → LODs → skinned `.ydd`, with warnings for the things a
user actually gets wrong (garment far from the body, disconnected pieces,
missing UVs, welded duplicates).

`src/Services/FreemodeDonorLoader.cs` supplies the donor, and it fixes a
correctness trap the prototype had. **Blend indices are positions in the ped's
skeleton, and Assimp enumerates bones in its own order** — using that order
silently points every weight at the wrong bone. The palette is therefore read
from the glTF skin's own `joints` array, and the deform rig is picked by shape
(the 128-joint skin containing `SKEL_ROOT`) because the male file calls it
`skel` and the female `head_000_r`, and both files also contain a 134-joint
Blender control rig that must not be selected.

That the palette is right is now proven rather than assumed — the loaded indices
match the values measured independently from real shipping clothing:
`SKEL_ROOT`=0, `SKEL_Pelvis`=1, `SKEL_L_Thigh`=2, `SKEL_Spine3`=38,
`SKEL_L_Clavicle`=39, `SKEL_L_Hand`=42, `SKEL_Head`=98, and the male and female
palettes agree on every deform bone.

Measured end to end on a 797-vertex jacket: 767 matched, 30 inpainted, 3 tiers
(1,110 → 554 → 432 triangles), **62,473-byte wearable `.ydd`**.

**77 checks green** (31 writer, 25 transfer + LOD, 21 donor + end-to-end).

Still open: no UI; no `.ymt`/shop-meta packaging (deliberate — that is what the
existing packers already do well); and **nothing has been loaded in game yet**.

## 7f. NEXT: 3D preview with a reference ghost (designed 2026-08-03, not built)

Driven by a real failure: a bag auto-fitted and exported, but came out rotated
wrong, and there was no way to see that before export. Numbers in a warning
panel cannot substitute for looking at the thing.

**The workflow the user wants:**

1. Pull a **reference garment** from the grzyClothTool catalog
   (https://grzy.tools/catalog) — a known-good `.ydd` of the same component type.
2. Load it into a 3D preview inside the Clothing workspace.
3. Import the user's own mesh **to replace** it.
4. **Keep the reference visible as a ghost**, so the user can match height,
   rotation and position against something known-correct.
5. Export.
6. Preview on the ped in **grzyClothTool** (https://github.com/grzybeek/grzyClothTool, GPL-3.0).

**Why the ghost is the right idea.** Auto-fit can infer *scale* from the size
ratio, but it cannot infer *orientation* or where on the body a bag hangs. A
correctly-authored reference of the same slot gives the user an unambiguous
target to line up against — it turns an invisible failure into an obvious one.

**What this needs:**

- A WebView2 host in `ClothingView`, following the pattern the Emotes and Props
  workspaces already use (one `viewer.html` served per virtual host — see
  [[fiveos-viewer-instances-hostnames]]). A new hostname for the clothing page.
- Load a `.ydd` into the viewer: `DrawableMeshExtractor` already decodes
  YDR/YDD geometry for preview, so the reference and the freemode body can both
  be shown. It reads only position/normal/uv semantics today.
- Ghost rendering (translucent) for the reference, solid for the user's mesh —
  the sync-emote partner ghost in `viewer.html` is a working precedent.
- **Transform gizmo or numeric fields** for rotation / position / scale, written
  back into the mesh before weighting. This is the actual missing capability —
  auto-fit handles scale, nothing handles rotation.
- grzyClothTool compatibility is mostly already satisfied: it consumes ordinary
  addon `.ydd` files, which is what the pipeline emits. Worth verifying its
  loader accepts ours (it is GPL-3.0, so its reader can be read directly).

**Sequencing note:** build the transform controls and ghost first; the catalog
fetch is a convenience on top and can start as "browse to a reference `.ydd`".

**Phase 1 — skinned export.** Bypass `FbxConverter`; author a `PBB*` drawable
either directly or via `.ydr.xml`. This is the true prerequisite and is valuable
on its own. Validate by round-tripping a real Rockstar `.ydd` byte-for-byte
before generating anything new.

**Phase 2 — weight transfer.** The 400–600 lines above, against the bundled
donor. Ship behind a preview of the weighted mesh in the existing viewport.

**Phase 3 — LODs.** Mostly existing machinery; note the documented caveat that
quadric collapse without bone-aware error metrics degrades past ~70% reduction.

**Phase 4 — packaging.** ymt/shop-meta generation, or deliberately defer and let
users hand off to an existing packer. The scarce resource here is ymt *slots*
(~3–4 free per gender on current builds), so splitting packs is not free.

---

## 9. Incidental bug found

`src/ydr-writer/GtaBoneTags.cs:34` has `SKEL_R_Hand = 6286`. That is
`IK_R_Hand`'s tag; the correct value is `57005`, as in
`src/Services/GtaBoneTags.cs:49`. The stale copy feeds
`AnimationSampler.TryResolve`, live on the converter's animation path
(`Converter.cs:456`), so right-hand channels exported that way are tagged as an
IK helper. The Emotes path uses the correct table and is unaffected. That copy is
also generally stale — 55 entries against 76, with no auxiliary bones.
