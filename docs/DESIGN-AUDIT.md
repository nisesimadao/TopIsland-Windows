# TopIsland design audit

This document is the visual baseline for TopIsland. It exists to prevent future changes from drifting back into generic dashboard / glass-card UI.

## Reference 1 - BoringNotch

Source: https://github.com/TheBoredTeam/boring.notch

Measured directly from the current public source:

- Open notch size: `640 x 190`
- Window shadow padding: `20`
- Global spacing constant: `16`
- Open notch radii: `top = 19`, `bottom = 24`
- Closed notch radii: `top = 6`, `bottom = 14`
- Album artwork: `90 x 90`
- Open artwork radius: `13`
- Music-player section spacing: usually `4-15`
- Regular media-control target: `30 x 30`
- Play/Pause target: `40 x 40`
- Media buttons: transparent at rest, gray around 20% only on hover
- Main notch surface: black, not a stack of translucent cards
- Settings are not embedded as a permanent dashboard column in the media surface

The most important geometry detail is that the inverse top corner is a small radius independent of the notch width. It must not scale into a long Bezier shoulder as the surface becomes wider.

## Reference 2 - Apple Dynamic Island / Live Activities

Sources:

- https://developer.apple.com/design/human-interface-guidelines/live-activities/
- https://developer.apple.com/videos/play/wwdc2023/10194/

Rules carried into TopIsland:

- Keep margins even and visually concentric with the outer shape.
- Do not shrink a dense desktop dashboard into a rounded container.
- Use only the space required for the current information.
- Reduce information density in smaller presentations instead of squeezing every label in.
- Keep key text readable at a glance; small secondary text is secondary, not the primary hierarchy.
- Nested rounded rectangles should be rare. If used, their radius and inset need to relate geometrically to the parent shape.

## TopIsland rules

### Surface

1. The main overlay is one surface.
2. Black is the reference appearance for both Notch and Dynamic Island.
3. No permanent three-card / four-card dashboard layout inside the surface.
4. Mica/Acrylic/Glass remain optional appearances, not the basis of the Apple-inspired design.
5. No decorative gradients, fake rim lights, or arbitrary glass highlights in the default appearance.

### Spacing

Use this scale unless a component has a measured reason not to:

- `4` - tightly related text
- `8` - control grouping
- `12` - inner edge clearance / small section separation
- `16` - standard component separation
- `24` - major content separation

Expanded Notch follows the BoringNotch relationship:

- inverse top radius = `19`
- additional content clearance from the vertical wall = `12`
- content begins around `31` px inside the unclipped notch rect

### Density

- Authentic Notch: artwork/app icon + one compact status, currently time
- Compact: add primary title
- Standard: add subtitle/source when space allows
- Idle >= 520 dip: allow compact CPU/RAM
- Peek >= 430 dip: allow compact CPU/RAM
- Expanded: artwork/app icon + primary information + one right-aligned system/time group

Do not scale typography down to preserve content. Remove lower-priority content instead.

When there is no media session, foreground-app information replaces media information. Do not show disabled media controls or pretend the foreground app is a media session.

### Controls

- Use vector/system-style symbols, never emoji glyphs as UI icons.
- Regular icon buttons: `30 x 30`
- Primary Play/Pause: `40 x 40`
- Transparent at rest
- Background only appears on hover/press

### Notch geometry

Closed:

- top inverse radius = `6`
- bottom radius = `14`

Expanded:

- top inverse radius = `19`
- bottom radius = `24`

The radii do not scale with width.

### Review checklist

Before merging visual changes:

- Are left/right clearances symmetrical?
- Are top/bottom gaps intentionally different, or accidentally different?
- Does every text baseline align to a clear row/column?
- Is a new container actually necessary?
- Would removing an element improve the layout?
- Does Authentic mode remove information instead of shrinking it?
- Does the Notch shoulder still look like a radius rather than a stretched curve?
- Are all icons vector/system icons?
- Does the UI remain readable over a busy background without adding fake glass decoration?

## Typography and motion

- Use `Segoe UI Variable` on supported Windows installations; do not bundle or imitate SF Pro.
- Titles are the strongest text but should stay at `SemiBold`, not heavy display weights.
- Secondary/source text is smaller and quieter; it must not compete with the title.
- Clock and changing numeric telemetry use tabular numeral alignment so updates do not shift the layout.
- Expanded content fades and moves only a few device-independent pixels.
- Avoid large slides, bounce, scale pulses, and decorative spring overshoot.
- Compact and Expanded are alternate presentations of the same surface; do not show both information layouts at once.
- Hover should feel like a subtle increase in presence, not a separate card appearing.
- Peek may reveal one lower-priority detail without reflowing or shrinking the main information.

## DPI and coordinates

TopIsland uses WPF device-independent pixels (DIPs) for layout while Windows monitor placement is ultimately physical-pixel based.

- Do not mix physical monitor pixels with WPF `Width`, geometry, or content measurements.
- Each target monitor is described from its physical display mode plus its Windows scale factor.
- Centering and Full Width sizing are calculated against the selected monitor, not `SystemParameters.PrimaryScreenWidth`.
- The WPF host is `PerMonitorV2` through the application manifest.
- A monitor change updates both the destination physical position and the layout DIP width using the destination scale factor.
- Win32 hit testing converts `ScreenToClient` device pixels through WPF `TransformFromDevice` before checking the surface geometry.
- Visual center and input center must resolve to the same point at 100%, 125%, 150%, and other DPI scales.

Verified development setup:

- Primary display: `3840 x 2160 @ 150%`
- Secondary display: `2560 x 1440 @ 125%`
- Standard and Full Width physical sizes were checked on both displays.

## Wide and Full Width layouts

A wider shell does not justify stretching its content.

- Full Width extends the outer surface while preserving safe margins.
- The media information/progress/control column has a readable maximum width (`520 dip` in the current layout).
- Status information may remain anchored to the right edge.
- Empty space between content groups is preferable to stretching progress bars, text blocks, or artwork to fill the shell.
- Width changes may reveal additional information, but should not distort component proportions.

## System tray and settings ownership

The overlay is an information surface, not the settings window.

- Persistent configuration belongs in the system tray or context menu, not as a permanent card inside Expanded.
- The tray exposes Show/Hide, Expand, Style, Width, Material, Theme, Display, launch-at-startup, and Quit.
- Display choices are Follow active app, Primary display, or an explicit connected display.
- Tray and overlay context-menu actions write to the same persisted settings model.
- Launch-at-startup is always opt-in; installation must not silently enable it.
