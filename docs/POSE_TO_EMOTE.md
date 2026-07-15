# Pose → Emote — make a FiveM emote from scratch

Pose a character, hit Export, and you get a ready-to-use FiveM emote. No 3D experience needed — about 5 minutes.

![The Emotes tab](images/guide-emotes.png)

You'll need FiveOS and a FiveM server with **dpemotes** (most servers already have it). You don't need GTA V installed — FiveOS comes with its own character to pose.

## Steps

1. Open FiveOS and click **Emotes** on the left. Stay on **Pose → Emote**.

   ![rail entry](images/01-rail-entry.png)

2. Click **GTA Male** to load the built-in character (the easy path).

   ![load options](images/02-load-options.png)

3. A figure appears with colored dots on every joint. Click a dot to grab that joint.

   ![loaded rig](images/03-gta-male-loaded.png)

4. Drag the colored rings that pop up to rotate the joint. Repeat until the pose looks right. **Mirror L→R** copies a left-arm pose to the right. **Reset** undoes everything.

5. Click **Export**, pick a short lowercase name like `mywave`, and save. That name is what players type in-game: `/e mywave`.

6. Install it — see **[EXPORT_TO_FIVEM.md](EXPORT_TO_FIVEM.md)**.

## Want it to move?

1. Pose the character, then click **+ KF** in the timeline at the bottom.
2. Drag the slider forward, change the pose, and click **+ KF** again.
3. Hit **Play** to preview. Add as many poses as you like, then Export.

## Tips
- The dots are color-coded by body part (green = left arm, orange = right arm, and so on).
- Hotkeys: **R** rotate, **W** move, **E** scale.
- Need a prop (phone, beer, etc.)? Use the **Prop** section in the sidebar to attach one before you export.
