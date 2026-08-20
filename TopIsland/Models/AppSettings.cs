namespace TopIsland.Models;

public enum IslandStyle
{
    DynamicIsland,
    Notch
}

public enum AppThemeMode
{
    System,
    Light,
    Dark
}

public enum SurfaceMaterial
{
    Solid,
    Mica,
    Acrylic,
    Glass,
    MaterialCopy
}

public enum WidthPreset
{
    Authentic,
    Compact,
    Standard,
    Wide,
    FullWidth,
    Custom
}

public sealed class AppSettings
{
    public IslandStyle Style { get; set; } = IslandStyle.DynamicIsland;
    public AppThemeMode Theme { get; set; } = AppThemeMode.System;
    public SurfaceMaterial Material { get; set; } = SurfaceMaterial.Solid;
    public WidthPreset WidthPreset { get; set; } = WidthPreset.Standard;
    public double CustomWidth { get; set; } = 560;
    public double SideMargin { get; set; } = 20;
    public bool EnableHoverPeek { get; set; } = true;
    public int HoverPeekDelayMs { get; set; } = 650;
    public bool StartExpanded { get; set; }
}