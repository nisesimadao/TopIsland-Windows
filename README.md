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
- Persistent settings in `%AppData%\TopIsland\settings.json`
- Auto-collapse after leaving an expanded surface

## Development

Requirements:

- Windows 11
- .NET 10 SDK

Build:

```powershell
dotnet build .\TopIsland.slnx -c Debug
```

Run:

```powershell
.\TopIsland\bin\Debug\net10.0-windows\TopIsland.exe
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

Early interactive prototype. Media sessions, notifications, multi-monitor placement, richer system modules, and stronger native Windows backdrop integration are the next areas to iterate on.
