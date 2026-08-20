<p align="center">
  <img src="assets/banner.svg" alt="TopIsland — A compact Windows top surface" width="100%">
</p>

<p align="center">
  <a href="README.ja.md">日本語</a> · English
</p>

<p align="center">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white">
  <img alt="WPF" src="https://img.shields.io/badge/UI-WPF-0C54C2?style=flat-square">
  <img alt="Windows" src="https://img.shields.io/badge/Windows-10%2B-0078D4?style=flat-square&logo=windows11&logoColor=white">
  <img alt="Media controls" src="https://img.shields.io/badge/media-GMTC-568DFF?style=flat-square">
</p>

# TopIsland

TopIsland is a **top-center Windows overlay** that turns the empty space above your apps into a compact surface for media, system information, and quick controls.

It can look like a floating Dynamic Island or a screen-attached Notch, including the concave / inverse-radius shoulders where the notch grows out of the top edge.

<p align="center">
  <img src="docs/screenshots/dynamic-acrylic.png" alt="TopIsland expanded with the Acrylic material" width="100%">
</p>

> The screenshot above is from the real WPF app running over Minecraft. It is cropped around the overlay so the UI is readable on GitHub.

## At a glance

- **Dynamic Island + Notch** — switch live without restarting
- **Inverse-radius notch shoulders** — not just a rounded rectangle attached to the top
- **Idle → Hover → Peek → Expanded** interaction states
- **Material selector** — Solid, Mica, Acrylic, Apple Glass, and Material Copy
- **Live media** — title, artist/source, artwork, timeline, previous/play-pause/next through Windows GMTC
- **Live system stats** — CPU, RAM, network, uptime, clock, and date
- **Focus-safe overlay** — clicking TopIsland does not steal focus from the game/app underneath
- **Shape-aware hit testing** — transparent padding is click-through
- **Six width modes** — Authentic, Compact, Standard, Wide, Full Width, and Custom
- **System / Light / Dark themes**
- **Local settings** in `%APPDATA%\TopIsland\settings.json`

## Surface materials

TopIsland keeps its true pill/notch geometry first, then applies a material palette on top.

| Material | Look |
| --- | --- |
| **Solid** | Opaque, highest contrast |
| **Mica** | Calm, denser Windows-style base surface |
| **Acrylic** | More translucent glass-like surface |
| **Apple Glass** | Brighter layered glass with stronger highlights |
| **Material Copy** | Windows accent-tinted adaptive surface |

Windows 11 exposes native DWM Mica / Desktop Acrylic system backdrops, but those backdrops paint the full rectangular WPF window bounds on the transparent layered host TopIsland needs for its custom shape. TopIsland therefore probes the native capability but currently uses its **shape-safe material renderer** instead of showing a rectangular gray backdrop around the island.

## Screenshots

<table>
  <tr>
    <td align="center"><b>Notch · Apple Glass</b></td>
    <td align="center"><b>Compact · Mica</b></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/notch-apple-glass.png" alt="TopIsland Notch with Apple Glass material"></td>
    <td><img src="docs/screenshots/compact-mica.png" alt="TopIsland compact Mica surface"></td>
  </tr>
</table>

## Interaction

- Move the pointer onto the surface → it softly grows
- Keep hovering → Peek state reveals more information
- Click → Expanded dashboard
- Move away from Expanded → it collapses after a short delay
- Change Style / Material / Width / Theme directly from the expanded surface

The shadow blur/opacity are animated together with the geometry so Hover feels like the surface gains mass instead of just changing size.

## Build

Requirements:

- Windows 10 or newer
- .NET 10 SDK

```powershell
dotnet build TopIsland.slnx -c Release
```

Publish a Windows x64 build:

```powershell
dotnet publish TopIsland/TopIsland.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish
```

## Project structure

```text
TopIsland/
  Controls/      custom Dynamic Island / inverse-radius Notch geometry
  Interop/       overlay hit-testing and Windows backdrop interop
  Models/        persisted surface settings
  Services/      media, theme, stats, foreground-app and settings services
  Assets/        application icon / logo

docs/screenshots/  real runtime screenshots used by this README
assets/            GitHub banner
```

## Current status

TopIsland is an early interactive prototype, not a finished utility yet. The overlay, media integration, material switching, settings persistence, click-through behavior, focus preservation, and the main visual states are working. Multi-monitor / per-monitor DPI work and a compositor-backed true blur path are still being refined.
