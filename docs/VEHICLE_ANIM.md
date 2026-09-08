# Vehicle animation — animate a car's doors, bonnet and bones, export a .ycd

In the **Vehicles** tab, switch the car stage to **Animate**: click a door on the real 3D model, swing it with the gizmo, set keys on the timeline, and FiveOS bakes it into a game-ready `.ycd` — packed as a drop-in FiveM resource or a `dlc.rpf`. No Blender, no 3ds Max.

## How to use it
1. In **Vehicles**, pick a car (click its `.yft` in the files list) and flip the switch above the 3D view from **Preview** to **Animate**. Or just drop any vehicle `.yft` onto the tab — it opens straight in Animate. The sibling `.ytd` is used for textures, and the `_hi.yft` is picked up automatically for full detail.
2. Click a part of the car — a door, the bonnet, a wheel — and its bone is selected. Bone dots mark everything you can animate; the **Bones** list on the right shows the same thing by name.
3. Drag the gizmo. **Rotate** (E) swings a part around its real hinge, **Move** (W) slides it. With **Auto-key** on, every drag writes a key at the playhead. Otherwise press **K** (or **+ Key**) when the pose is right.
4. Move the playhead, pose again, key again. **Space** plays it back; the ruler and rows work like any timeline — drag the playhead, drag keys to move them, double-click a lane to add a key, **Delete** removes selected keys, scroll to zoom.
5. Set the clip name, then **Export ▾**:
   - **FiveM resource** — a folder with `fxmanifest.lua`, `client.lua` and `stream/<name>@vehanim.ycd`. Drop it in `resources/`, `ensure <name>`, then `/<name>` in game plays it on the vehicle you're in (or standing next to). `/<name> stop` ends it.
   - **DLC .rpf** — a `dlc.rpf` streaming the clip for singleplayer (`dlclist.xml`) or the FiveM client `mods/` folder.
   - **.ycd file only** — just the clip dictionary, plus its `.ycd.xml` source.
6. **Save session** writes a `.fvanim` file (model + keys) so you can come back and tweak it later.

## Tips
- Only bones you keyed end up in the clip — everything else is left alone in game. The number boxes under **Selected bone** are offsets from the bone's rest pose in game space (X right, Y forward, Z up); `0 / 0 / 0` is exactly how the file ships it.
- **Length** and **FPS** in the timeline bar set the clip. Loop is baked into the exported script.
- **Ease** picks how motion leaves a key: Smooth (ease in/out), Linear, or Step (snap).
- **Two things stuck together that you want to move separately** (left and right speaker panels on one bone, say): hover the car — pieces light up — and pick what to break off:
  - **Click** a piece to select it, **Shift+click** to add more (build the section piece by piece), Shift+click a selected piece to drop it.
  - **Double-click** grabs the whole part under the cursor in one go; **Grow** spreads the selection to touching pieces (press again to keep spreading).
  Then **Split into new bone** in the bar at the bottom and name it. FiveOS writes the bone into the car's `.yft` and `_hi.yft` (keeping `.bak` copies), re-binds exactly those pieces, and reloads — the new bone animates like any other and scripts can find it by name. Esc clears the selection. (Clicking a piece also selects its bone, so if you don't split, you're ready to animate the whole part.)
- Custom parts (speaker walls, racks, extras) animate too — click the part. If the modder left that bone's pivot somewhere odd, the editor turns the part around its own centre instead and bakes the difference into the clip, so it behaves the same in game.
- A clip animates the bones of the model it was authored on. Other cars only follow it where their bone names match (`door_dside_f`, `bonnet`, `boot`…).
- Playing it on your own: `RequestAnimDict('<name>@vehanim')`, wait for it, then `PlayEntityAnim(vehicle, '<name>', '<name>@vehanim', 1000.0, loop, true, false, 0.0, 0)`. Or `exports['<name>']:play(vehicle)` from the generated resource.

## If it doesn't work
- **Grey car?** Click **GTA** above the viewport and point it at your GTA V folder once — the shared vehicle textures come from there.
- **Doors snap shut in game?** The game's own door logic fights the clip. Shut them with `SetVehicleDoorShut(veh, i, true)` before playing, or animate a bone the game doesn't manage.
- **`/name` says the game rejected the clip?** The `.ycd` was built for a different skeleton — open the car you're actually spawning and export again.
- **Nothing to animate?** Some low-LOD `.yft` files carry no skeleton. Use the model's main or `_hi.yft`.
- **Switched back to Preview?** Your keys are kept — flip to **Animate** again and they're still there. Loading a different car starts fresh (save a session first if you want them).
