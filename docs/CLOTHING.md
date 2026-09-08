# Clothing — turn a garment mesh into wearable clothing

Painting skin weights is the part that stops most people making clothing. FiveOS works them out for you: give it a hoodie, jacket, vest or bag modelled around the ped and it weights the mesh against the GTA body and writes a wearable `.ydd` with all three detail levels.

Your mesh needs **no rig and no vertex groups**. It does need to be sitting **on the body, at GTA scale** — that is how the weights get worked out, so a garment floating off to one side has nothing to be compared against.

## How to use it

1. Click **Browse…** and pick your garment — `.fbx`, `.glb`, `.gltf`, `.obj` or `.dae`. It appears on the body in the preview.
2. Pick the **body**: Male or Female. A garment has to be authored and weighted separately for each — their proportions differ enough that one set of weights won't do for both.
3. Set the **component** name, e.g. `jbib_000_u`. This is how the game knows which slot the item fills, and it names the file you get out.
4. Leave **Reduce automatically to** on unless you have a reason not to. Clothing is worn by every player on the server, so it's some of the heaviest geometry in the game.
5. Leave **Generate Med and Low detail levels** on, for the same reason.
6. Click **Rig and export…** and save the `.ydd`.

## Getting it onto the body

Everything depends on the garment actually sitting on the ped, so spend the time here.

Rotation is applied about the garment's **own centre** first, then the offset moves it. So straighten it up, then move it into place, then scale if you still need to.

- Came in lying on its side? That's the usual axis disagreement between 3D programs — click **X-90**.
- Facing backwards? **Z180**.
- **Reset** puts the orientation back to zero.
- **Move m** shifts it in metres and **Scale ×** resizes it.

## If it's taking a long time

A message appears telling you so, and it's worth acting on rather than waiting out. A slow solve nearly always means the garment isn't on the ped — almost none of it matches the body, so every vertex has to be solved the hard way instead of being read off the body underneath.

Stop, check its position and scale, and run it again. It should be quick.

## What you get, and what you don't

You get a skinned `.ydd`, named after the component, with high, medium and low detail levels inside it.

You do **not** get an add-on resource around it — no `.ymt` / `CPedVariationInfo`, no drawable numbering, no shop metadata. That is deliberate: the community packers already do that part well, and the part nobody had automated was the rigging. Take the `.ydd` to whichever packer you already use.

## If it doesn't work

- **It refuses to weight the garment:** it will tell you what it measured. Nearly always the mesh is off the body, at the wrong scale, or rotated — fix the orientation and try again.
- **Holes at the cuffs or collar after optimising:** turn on **Preserve open boundaries** in the [Optimize](OPTIMIZE.md) tab's clothing mode.
- **Very heavy reduction warning:** your mesh was far over budget. It will still build, but a garment authored nearer game budget in the first place will always look better than one crushed down to fit.

As with everything here — test it in game before you ship it.
