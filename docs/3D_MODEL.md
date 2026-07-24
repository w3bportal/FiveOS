# 3D Model — turn a 3D file into a FiveM prop

Have a 3D model (`.glb`, `.fbx`, `.obj`, and more)? Drop it here and FiveOS turns it into a prop you can place in your server.

![The 3D Model tab (Props)](images/guide-3dmodel.png)

## How to use it
1. Drag your model onto the preview area.
2. Move, rotate, or scale it with the handles. Press **W** to move, **E** to rotate, **R** to scale.
3. Check the size numbers. If they turn red, your model is too heavy — see the Optimize tab to shrink it.
4. Type a name at the bottom (or leave it to fill in from the file name).
5. Click **Convert**.

Your finished files go to your Documents folder, under **FiveOS\Output**. Drop them into your server's `resources`.

## Tips
- A person-shaped figure stands next to your model so you can check it's the right size.
- Use the **Match** button to snap your prop to a normal height.
- Toggle **Wireframe** at the bottom-right to see the shape without colors.

## Textures (how the game gets them)

FiveOS props ship **textures embedded inside the `.ydr`** (the drawable’s own texture dictionary). The matching `.ytyp` leaves `textureDictionary` empty, so the game does **not** look for a separate `.ytd` in `stream/`. That matches how most custom props and housing furniture packs work — one file, spawn with `CreateObject` / housing menus, no extra TXD name to wire up.

On Convert (and on **Build recolor pack**), FiveOS:

1. Pulls every usable diffuse / base-color and normal map from the model (embedded in the file, or sitting next to it in a `textures/` folder).
2. Bakes them into the `.ydr` as DDS.
3. Points the shader samplers at those embedded maps only (no dangling names).

Leave **Extract embedded textures** on unless you are deliberately shipping your own `.ytd` and know how to set the archetype’s texture dictionary name.

**Recolor pack:** each library image becomes its own prop with that image embedded as the diffuse; normals from the base convert stay embedded so the variant still looks right in-game.

## If it doesn't work
- **Model is tiny or huge:** it was saved in the wrong units — re-export it from your 3D program in metres.
- **No texture showing:** use **Add textures…** in the TEXTURES panel (or the layer’s Change textures menu) and pick the picture files — they show in the preview and bake into the export on Convert. Textures next to the model (or in a `textures/` folder beside it) are also picked up automatically.
- **Pink / missing in-game but fine in preview:** the export needed embedded maps — turn **Extract embedded textures** on and Convert again (or rebuild the recolor pack).
- **Convert button greyed out:** you haven't loaded a model yet — drag one in first.
