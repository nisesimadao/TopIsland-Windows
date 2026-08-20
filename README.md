# TopIsland for Windows

A top-edge Windows information surface inspired by notch and Dynamic Island interaction patterns, but designed for higher information density on desktop.

## Current prototype

- Dynamic Island style with a true pill compact shape
- Notch style with smooth concave / inverse-radius shoulders into the screen edge
- Idle → Hover → Peek → Expanded interaction states
- Notch widens first, then grows downward when expanded
- Automatic Windows light/dark theme detection plus manual overrides
- Modern translucent surfaces and soft shadows
- Width presets: Authentic, Compact, Standard, Wide, Full Width, Custom
- Responsive information density for small widths
- Distributed layout for wide/full-width modes
- Live CPU, RAM, network throughput, clock, date, and uptime
- Windows Global Media Transport integration
  - title / artist / source app
  - artwork
  - playback progress
  - previous / play-pause / next
  - filters non-media sessions such as launcher noise
- Active-window fallback when there is no useful media session
- Artwork caching so unchanged thumbnails are not decoded every refresh
- Persistent settings in `%AppData%\TopIsland\settings.json`
- Expanded surface auto-collapse after leaving it
- Transparent/shadow margins are click-through at the Win32 hit-test layer
- The overlay is non-activating, so clicking it does not steal focus from the app underneath

## Development

Requirements:

- Windows 11
- .NET 10 SDK

Build:

```powershell
dotnet build .\TopIsland.slnx -c Release
```

Run a debug build:

```powershell
.\TopIsland\bin\Debug\net10.0-windows10.0.19041.0\TopIsland.exe
```

Framework-dependent x64 publish:

```powershell
dotnet publish .\TopIsland\TopIsland.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\publish
```

## Design rules

### Dynamic Island

Compact height always uses a radius equal to half of its visible height, so it stays a true pill rather than a rounded rectangle.

### Notch

The screen-edge shoulder is not a normal rounded rectangle corner. The shape uses a concave cubic Bezier transition whose tangent begins horizontal at the screen edge and ends vertical at the notch wall:

```text
----------------\____________/----------------
```

Hover widens this cutout. Click/expand widens it further before the surface grows downward.

## Status

Interactive prototype under active iteration. Multi-monitor placement, stronger native Windows backdrop integration, richer modules, and packaging/startup UX are the next areas to refine.
