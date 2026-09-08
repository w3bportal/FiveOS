# Animated props — pose the parts of a prop, export a moving .ydr

In **Assets**, switch the stage from **Props** to **Animated**: click a part of your model, swing or slide it with the gizmo, set keys on the timeline, and Convert ships the prop with the motion baked in — a skeleton inside the `.ydr`, a `.ycd` clip next to it, and a `.ytyp` that plays it on its own when the prop is placed. No Blender, no 3ds Max.

Anything that is a separate mesh in your file can move: a door, a drawer, a fan blade, a sign, a barrier arm, the whole prop. A single one-piece mesh that needs to bend at a joint can't — rigid parts only.

## How to use it
1. Load a model as usual (drop an `.fbx` / `.glb` / `.obj` on the stage). Above the viewport, click **Animated**. A file that already ships a clip opens in Animated on its own.
2. Click a part — its bone is selected and the gizmo sits on it. Every part is listed under **BONES** in the sidebar; parts with no geometry of their own (empty groups) show when you search for them.
3. Drag the gizmo. **Rotate** (E) turns the part about its own pivot — the point the modeller gave it, or its centre when the origin sits elsewhere — and **Move** (W) slides it. With **Auto-key** on, every drag writes a key at the playhead; otherwise press **K** (or **+ Key**) when the pose is right.
4. Move the playhead, pose again, key again. **Space** plays it back; drag the playhead, drag keys, double-click a lane to add a key, **Delete** removes selected keys, scroll to zoom. **Length** and **FPS** set the clip; **Ease** picks how motion leaves a key.
5. **Spin 360°** in the BONES panel keys a full turn of the selected part (or the only part) over the clip length — a fan, a wheel, a turntable in one click. Pick the axis (vertical, X or Z) and **reverse** if it turns the wrong way.
6. **Convert** (Ctrl+Enter) as normal. The resource folder now holds `<name>.ydr` (with the skeleton), `stream/<name>.ycd`, the `.ytyp` with *Has Anim* + *Auto Start Anim* set, and a `client.lua` you can use to spawn and play it (`/<name>anim`).

## Tips
- Only parts you keyed end up in the clip; everything else stays exactly as modelled.
- Switching back to **Props** keeps your keys — flip to **Animated** again and they're still there. Loading a different model starts fresh.
- The prop plays its clip wherever it's placed in a `.ymap`. From a script: `RequestAnimDict('<name>')`, wait for it, then `PlayEntityAnim(obj, '<name>', '<name>', 1000.0, true, true, false, 0.0, 0)`.
- Position, rotation and scale from the sidebar still apply — the skeleton is built in the same space as the exported mesh, so the prop animates exactly as you see it on the stage.
- Moving parts live in their bone's space inside the `.ydr` (that's how the game expects rigidly bound geometry). Opening the file in CodeWalker shows each part at its bone — it renders in place once the skeleton is applied.

## If it doesn't work
- **Nothing moves in-game?** The moving piece must be a separate mesh in the source file. Open your model in the Layers panel: if the part you want isn't its own layer, split it in your modelling tool first.
- **Wrong pivot?** The editor uses the part's own origin when it lies on the part, and the part's centre otherwise. Give the part a proper pivot in your modelling tool for a hinge at the edge.
- **`/<name>anim` says the clip didn't load?** The `.ycd` is named after the prop and lives in `stream/` — make sure the whole resource folder was copied, not just the `.ydr`.
