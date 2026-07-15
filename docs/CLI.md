# Command line (CLI) — run FiveOS on your server

`fiveos` is the FiveOS engine with no window — you type commands instead of clicking. It's made for **servers and VPS boxes**: optimize your assets, convert models, and pack resources straight over SSH, and drop it into scripts so it runs by itself.

> ⚠️ **Experimental — provided as-is.** FiveOS is an experimental, community-made project. It is **not** an official or endorsed way to create models for GTA V / FiveM, and it comes with **no guarantees**. Some things may not work, may break, or may produce imperfect results depending on your files. Always keep backups, test in-game before going live, and treat it as a helper — not a replacement for professional tools or workflows.

> **Windows now, Linux next.** The Windows build is ready today. A Linux (`linux-x64`) build is in progress.

## Get it running

1. Download **`FiveOS-CLI-win-x64.zip`** from the [Releases page](https://github.com/w3bportal/FiveOS/releases).
2. Unzip it anywhere. Keep `fiveos.exe` and the `Engine\` folder **together** — the `Engine` folder holds the model and texture converters.
3. Open a terminal in that folder and run:

```
fiveos help
```

That's it — nothing else to install (the download is self-contained). To use it from anywhere, add the folder to your `PATH`.

## The commands

Run any command with **no arguments** to see all its flags.

### Optimize — make files smaller

```
fiveos optimize ytd <folder|file.ytd> [--max-size 1024] [--threshold 2048] [--only-oversized] [--backup <dir>]
fiveos optimize drawable <file.ydr|.ydd|.yft> [--ratio 0.5] [--textures-only]
fiveos optimize texture <in.png|.dds> [-o <dir>] [--width 1024] [--format dxt1|dxt5]
fiveos optimize mesh <model.glb|.fbx|.obj|...> [--target-tris 5000] [-o <dir>]
```

- **`optimize ytd`** is the big one for servers: it shrinks the oversized textures behind FiveM's red *"Oversized assets WILL lead to streaming issues"* warning. Point it at a whole `resources/` folder and it walks every `.ytd`. `--max-size 1024` caps textures at 1024px; `--only-oversized` skips files that are already fine. **Files are changed in place — back them up first** (or pass `--backup`).
- **`optimize drawable`** decimates a model (`--ratio 0.5` keeps half the triangles) and can compress its embedded textures.
- **`optimize texture`** / **`optimize mesh`** handle loose images and 3D meshes.

### Convert — turn things into FiveM assets

```
fiveos convert prop <model.glb|.fbx|.obj|...> --name <asset> [--up auto|y_up|z_up] [--scale x,y,z] [--lods] -o <dir>
fiveos convert vehicle <dlc.rpf|modFolder> [--name <resource>] [--single] -o <dir>
```

- **`convert prop`** turns a 3D model into a placeable FiveM prop (`.ydr` + `fxmanifest.lua`), written into the folder you pass with `-o`.
- **`convert vehicle`** turns a singleplayer add-on car (its `dlc.rpf` or mod folder) into a ready FiveM resource. Point it at a folder of several cars to build a pack.

### Pack & build

```
fiveos pack rpf <resourceFolder> [-o <out.rpf>] [--include-all]
fiveos build ped-dlc <resourceFolder> [-o <dir>] [--name <dlc>]
fiveos build replace <resourceFolder> --vanilla <asset_name> [--client] -o <dir>
fiveos build addon <resourceFolder> [--name <newAssetName>] [--pack <packName>] -o <dir>
```

- **`pack rpf`** packs a folder into an open (unencrypted) `.rpf`.
- **`build ped-dlc`** scaffolds a singleplayer ped `dlc.rpf`.
- **`build replace`** builds a resource that replaces a vanilla asset by name.
- **`build addon`** builds a resource that adds your model as a NEW named asset with its own `.ytyp` — spawnable by name, nothing vanilla replaced. `--name` renames a single-model input; omit it to keep the model's own filename.

### Import

```
fiveos import mod-url <url> [-o <dir>]
```

Downloads and extracts a mod archive from a link.

## Example: clean up a server before a wipe

```
# 1. shrink every oversized texture in your resources (back them up first!)
fiveos optimize ytd "C:\fxserver\resources" --max-size 1024 --backup "C:\backups\ytd"

# 2. convert a new prop and drop it straight into a resource
fiveos convert prop new_sign.glb --name mall_sign -o "C:\fxserver\resources\[props]"

# 3. pack a finished resource
fiveos pack rpf "C:\fxserver\resources\[maps]\downtown" -o downtown.rpf
```

Because it's just commands, you can put these in a `.bat` file or a scheduled task and let it run on its own.

## Good to know

- **It never touches your live server unless you point it there.** Convert commands write only to the `-o` folder you give them.
- **`optimize ytd` overwrites in place.** Use `--backup <dir>` or copy your files first.
- **Same engine as the app** — the results match the FiveOS desktop tool exactly.

## If it doesn't work

- **"engine not found":** the `Engine\` folder isn't next to `fiveos.exe`. Unzip the whole download and keep them together.
- **A command "skipped" a file:** it was already small enough — nothing to do. That's normal.
- **`convert prop` can't find the model:** use the full path to the file.
