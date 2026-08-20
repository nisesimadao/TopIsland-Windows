<p align="center">
  <img src="assets/banner.svg" alt="TopIsland — A compact top surface for Windows" width="100%">
</p>

<p align="center">
  <a href="README.ja.md">日本語</a> · English
</p>

<p align="center">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white">
  <img alt="WPF" src="https://img.shields.io/badge/UI-WPF-0C54C2?style=flat-square">
  <img alt="Windows" src="https://img.shields.io/badge/Windows-10%2B-0078D4?style=flat-square&logo=windows11&logoColor=white">
  <img alt="Media controls" src="https://img.shields.io/badge/media-GMTC-333?style=flat-square">
</p>

# TopIsland

TopIsland is a **top-center Windows overlay** for media and lightweight system status. It can behave like a floating Dynamic Island or attach directly to the screen edge as a Notch.

The default design is deliberately simple: **one black surface, contextual information density, no dashboard of nested cards**.

<p align="center">
  <img src="docs/screenshots/notch-expanded.png" alt="TopIsland expanded Notch running on Windows" width="100%">
</p>

> Real runtime capture from the WPF app, cropped around TopIsland so the layout is readable on GitHub.

## What it does

- **Dynamic Island + Notch** modes
- Small inverse-radius Notch shoulders instead of stretched Bezier wings
- **Idle → Hover → Peek → Expanded** interaction
- Windows GMTC media title, artist/source, artwork, progress, Previous / Play-Pause / Next
- CPU, RAM, network, clock and date in larger presentations
- Width presets: Authentic, Compact, Standard, Wide and Full Width
- Information is removed as the surface gets smaller instead of shrinking everything
- Transparent window areas remain click-through
- Clicking the overlay does not steal focus from the app/game underneath
- Right-click context menu for Style / Width / Material / Theme
- Settings stored locally in `%APPDATA%\TopIsland\settings.json`

## Design baseline

TopIsland's layout is currently based on two references:

1. **BoringNotch** — its open/closed notch geometry, 90 px artwork, 30/40 px media controls, black single-surface layout and restrained hover treatment.
2. **Apple's Dynamic Island / Live Activity guidance** — even margins, concentric placement, compact layouts and reducing content instead of squeezing it.

The measured rules and review checklist are kept in [`docs/DESIGN-AUDIT.md`](docs/DESIGN-AUDIT.md).

### Current geometry

| State | Top inverse R | Bottom R |
| --- | ---: | ---: |
| Closed Notch | 6 | 14 |
| Expanded Notch | 19 | 24 |

Those radii are **independent of width**, so Full Width does not stretch the shoulder shape.

## Screenshots

<table>
  <tr>
    <td align="center"><b>Dynamic Island · Expanded</b></td>
    <td align="center"><b>Notch · Authentic</b></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/dynamic-expanded.png" alt="TopIsland Dynamic Island expanded"></td>
    <td><img src="docs/screenshots/notch-authentic.png" alt="TopIsland Authentic Notch"></td>
  </tr>
</table>

## Density rules

- **Authentic Notch:** artwork + time
- **Compact:** add media title
- **Standard:** add subtitle
- **Idle at 520 dip+ / Peek at 430 dip+:** compact CPU/RAM can appear
- **Expanded:** artwork + media controls + a single right-aligned status group

There is no permanent settings card inside the expanded surface. Configuration lives in the context menu instead.

## Materials

Black `Solid` is the default and the reference appearance for both styles. Optional Mica / Acrylic / Glass / Material Copy appearances are still available, but they are not used to imitate the physical Apple notch.

Native Windows 11 DWM backdrops are probed, but on TopIsland's transparent shaped WPF host they paint the rectangular window bounds. The current renderer therefore keeps the custom shape intact instead of showing a rectangular backdrop around it.

## Build

Requirements:

- Windows 10 or newer
- .NET 10 SDK

```powershell
dotnet build TopIsland.slnx -c Release
```

Publish Windows x64:

```powershell
dotnet publish TopIsland/TopIsland.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish
```

## Status

TopIsland is still an interactive prototype. Media integration, geometry transitions, width presets, click-through behavior, focus preservation, material switching and settings persistence are working. Primary-monitor DPI-aware positioning and hit testing are working. Multi-monitor / per-monitor DPI transitions and a compositor-backed shaped blur path are still in progress.
