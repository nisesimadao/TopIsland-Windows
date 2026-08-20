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

TopIsland is a **top-center Windows overlay** for media, the active app, and lightweight system status. It can float as a Dynamic Island or attach directly to the top edge as a Notch.

The reference appearance is deliberately simple: **one black surface, contextual information density, no dashboard of nested cards**.

<p align="center">
  <img src="docs/screenshots/notch-expanded.png" alt="TopIsland expanded Notch running on Windows" width="100%">
</p>

> Real runtime capture from the WPF app, cropped around TopIsland so the layout is readable on GitHub.

## What it does

- **Dynamic Island + Notch** modes
- Small inverse-radius Notch shoulders instead of stretched Bezier wings
- **Idle → Hover → Peek → Expanded** interaction
- Windows GMTC media title, artist/source, artwork, progress and media controls
- When no media is active, shows the **foreground app title, process and application icon** instead of fake media controls
- CPU, RAM, network, clock and date in larger presentations
- Width presets: Authentic, Compact, Standard, Wide and Full Width
- Information is removed as the surface gets smaller instead of shrinking everything
- Transparent window areas remain click-through
- Clicking TopIsland does not steal focus from the app/game underneath
- System / Light / Dark theme modes

## System tray

TopIsland lives in the Windows notification area as well as on-screen. Right-clicking the tray icon exposes the settings without turning the overlay itself into a settings dashboard:

- Show / Hide
- Expand
- Style: Dynamic Island / Notch
- Width presets
- Material and theme
- Display selection
- Launch at startup
- Quit

The tray actions use the same persisted settings as the overlay.

## Multiple displays and DPI

Display mode can be changed from the tray:

- **Follow active app** — TopIsland follows the monitor containing the foreground window
- **Primary display**
- **Fixed display** — choose a specific connected monitor

The overlay runs as **PerMonitorV2** and resolves each monitor's physical mode and Windows scale factor independently. Full Width and Standard sizing were verified on a 150% primary display and a 125% secondary display.

## Design baseline

TopIsland's layout is currently based on two references:

1. **BoringNotch** — open/closed Notch geometry, 90 px artwork, restrained controls, a black single-surface layout and subtle hover treatment.
2. **Apple's Dynamic Island / Live Activity guidance** — even margins, concentric placement, compact layouts and reducing content instead of squeezing it.

Measured rules and the review checklist live in [`docs/DESIGN-AUDIT.md`](docs/DESIGN-AUDIT.md).

### Current Notch geometry

| State | Top inverse R | Bottom R |
| --- | ---: | ---: |
| Closed | 6 | 14 |
| Expanded | 19 | 24 |

The radii are **independent of width**, so Full Width does not stretch the shoulder shape.

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

- **Authentic Notch:** artwork/app icon + time
- **Compact:** add the primary title
- **Standard:** add subtitle/source when space allows
- **Peek:** may reveal one lower-priority detail without shrinking the existing layout
- **Expanded:** artwork/app icon + primary information + one right-aligned status group

There is no permanent settings card inside the expanded surface. Configuration belongs in the context menu and system tray.

## Materials

Black `Solid` is the default and reference appearance. Optional Mica / Acrylic / Glass / Material Copy appearances remain available, but they are not used to imitate Apple's physical Notch.

Native Windows 11 DWM backdrops are probed, but on TopIsland's transparent shaped WPF host they paint rectangular window bounds. The current renderer therefore prioritizes the custom shape instead of showing a rectangular backdrop around it.

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

## Local install

After publishing:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install-local.ps1
```

This installs TopIsland under `%LOCALAPPDATA%\Programs\TopIsland`, creates a Start Menu shortcut and launches it. It does **not** enable startup automatically; that remains an explicit tray setting.

To remove the local installation while keeping user settings:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/uninstall-local.ps1
```

Settings are stored in `%APPDATA%\TopIsland\settings.json`.

## Status

TopIsland is still an interactive prototype, but the core overlay is functional: media/foreground-app integration, typography and geometry transitions, width presets, click-through and no-activate behavior, system tray settings, settings persistence, multiple-display placement, PerMonitorV2 scaling and optional launch-at-startup are working. The main remaining visual/technical experiment is a compositor-backed shaped blur path that does not sacrifice the Notch/Island geometry.
