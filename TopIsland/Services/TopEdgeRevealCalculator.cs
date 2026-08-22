using TopIsland.Models;

namespace TopIsland.Services;

public static class TopEdgeRevealCalculator
{
    private const double RevealHeightDip = 14;

    public static bool Contains(MonitorDescriptor monitor, double baseSurfaceWidthDip, int cursorX, int cursorY)
    {
        var scale = Math.Max(0.5, monitor.Scale);
        var zoneMaxDip = Math.Max(220, Math.Min(900, monitor.DipWidth * 0.58));
        var zoneMinDip = Math.Min(300, zoneMaxDip);
        var zoneWidthDip = Math.Clamp(baseSurfaceWidthDip * 1.35 + 80, zoneMinDip, zoneMaxDip);

        var centerX = monitor.Left + monitor.PixelWidth / 2.0;
        var halfWidthPx = zoneWidthDip * scale / 2.0;
        var bottomY = monitor.Top + RevealHeightDip * scale;

        return cursorY >= monitor.Top
               && cursorY <= bottomY
               && cursorX >= centerX - halfWidthPx
               && cursorX <= centerX + halfWidthPx;
    }
}
