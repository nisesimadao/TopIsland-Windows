using TopIsland.Controls;
using TopIsland.Models;

namespace TopIsland.Services;

public readonly record struct IslandLayout(double Width, double Height, double Top);

public static class IslandLayoutCalculator
{
    public static IslandLayout Resolve(AppSettings settings, SurfaceState state, double screenWidthDip)
    {
        var baseWidth = ResolveBaseSurfaceWidth(settings, screenWidthDip);
        var maxWidth = Math.Max(185, screenWidthDip - settings.SideMargin * 2);
        double surfaceWidth;
        double windowHeight;

        switch (state)
        {
            case SurfaceState.Hover:
                {
                    var hoverGrowth = Math.Clamp(baseWidth * 0.04, 12, 28);
                    surfaceWidth = Math.Min(baseWidth + hoverGrowth, maxWidth);
                    windowHeight = settings.Style == IslandStyle.Notch ? 56 : 72;
                    break;
                }
            case SurfaceState.Peek:
                {
                    var peekGrowth = Math.Clamp(baseWidth * 0.09, 24, 56);
                    surfaceWidth = Math.Min(baseWidth + peekGrowth, maxWidth);
                    windowHeight = settings.Style == IslandStyle.Notch ? 62 : 78;
                    break;
                }
            case SurfaceState.Expanded:
                surfaceWidth = settings.WidthPreset == WidthPreset.FullWidth
                    ? maxWidth
                    : Math.Min(Math.Max(baseWidth, 1320), maxWidth);
                windowHeight = settings.Style == IslandStyle.Notch ? 382 : 398;
                break;
            default:
                surfaceWidth = baseWidth;
                windowHeight = settings.Style == IslandStyle.Notch ? 52 : 68;
                break;
        }

        var minWidth = settings.Style == IslandStyle.Notch ? 185 : 220;
        surfaceWidth = Math.Clamp(surfaceWidth, minWidth, maxWidth);
        var windowWidth = surfaceWidth + IslandGeometryFactory.ShadowPadding * 2;
        var top = settings.Style == IslandStyle.Notch
            ? 0
            : state is SurfaceState.Hover or SurfaceState.Peek ? 10 : 8;

        return new IslandLayout(windowWidth, windowHeight, top);
    }

    public static double ResolveBaseSurfaceWidth(AppSettings settings, double screenWidthDip) =>
        settings.WidthPreset switch
        {
            WidthPreset.Authentic => settings.Style == IslandStyle.Notch ? 185 : 260,
            WidthPreset.Compact => 320,
            WidthPreset.Standard => 560,
            WidthPreset.Wide => screenWidthDip * 0.64,
            WidthPreset.FullWidth => screenWidthDip - settings.SideMargin * 2,
            WidthPreset.Custom => settings.CustomWidth,
            _ => 560
        };
}
