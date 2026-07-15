# FiveOS

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![Donate](https://img.shields.io/badge/Donate-PayPal-00457C?logo=paypal&logoColor=white)](https://paypal.me/webportal1)
[![Ko-fi](https://img.shields.io/badge/Ko--fi-Support-ff5e5b?logo=ko-fi&logoColor=white)](https://ko-fi.com/webportal)
[![Discord](https://img.shields.io/badge/Discord-Join-5865F2?logo=discord&logoColor=white)](https://discord.gg/GEXUCC6TJ6)

Free Windows toolkit for FiveM mods.

Vehicles, props, optimize, RPF, and emotes — one app.

> ⚠️ **Experimental — provided as-is.** FiveOS is an experimental, community-made project. It is **not** an official or endorsed way to create content for GTA V / FiveM, and it comes with **no guarantees** — some things may not work, may break, or may produce imperfect results depending on your files. Always keep backups and test in-game before going live. Use at your own risk.

Report bugs on [Discord](https://discord.gg/GEXUCC6TJ6) or [GitHub Issues](https://github.com/w3bportal/FiveOS/issues).

## Features

- **Vehicles** — SP add-on cars to FiveM resources; shrink heavy files
- **3D Model** — .fbx / .glb / .obj / etc. to props
- **Optimize** — shrink models & textures for better FPS
- **Mods to RPF** — pack mods, build SP peds, replace stock assets
- **Emotes** — pose / import animation to dpemotes-ready pack

## Download

**[Download FiveOS.exe](https://github.com/w3bportal/FiveOS/releases/download/v1.0.4/FiveOS.exe)**

- **Docs:** https://fiveos.gitbook.io/fiveos-docs
- **Discord:** https://discord.gg/GEXUCC6TJ6
- **Source:** this repository (GPL-3.0)

## Build from source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```powershell
dotnet publish src/FiveOS.csproj -c Release -o publish_out
```

Official release builds may include optional cloud features that are not in this repository.

## License

FiveOS is licensed under the [GNU GPL-3.0](LICENSE).

## Contributors

- [fsg](https://github.com/fsgdev) ([fsgdev](https://github.com/fsgdev/fsgdev))
