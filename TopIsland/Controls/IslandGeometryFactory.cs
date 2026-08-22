using System.Windows;
using System.Windows.Media;
using TopIsland.Models;
using TopIsland.Services;

namespace TopIsland.Controls;

public static class IslandGeometryFactory
{
    public const double ShadowPadding = 16;

    public static Geometry Create(IslandStyle style, Size size, bool expanded)
        => Create(style, size, expanded ? 1.0 : 0.0, 1.0);

    public static Geometry Create(IslandStyle style, Size size, double expandedProgress)
        => Create(style, size, expandedProgress, 1.0);

    public static Geometry Create(IslandStyle style, Size size, double expandedProgress, double revealProgress)
    {
        var progress = Math.Clamp(expandedProgress, 0, 1);
        var reveal = Math.Clamp(revealProgress, 0, 1);
        return style == IslandStyle.Notch
            ? CreateNotch(size, progress, reveal)
            : CreateDynamicIsland(size, progress);
    }

    private static Geometry CreateDynamicIsland(Size size, double progress)
    {
        var pad = ShadowPadding;
        var width = Math.Max(1, size.Width - pad * 2);
        var height = Math.Max(1, size.Height - pad * 2);
        var compactRadius = height / 2.0;
        var expandedRadius = Math.Min(28, height / 2.0);
        var radius = compactRadius + (expandedRadius - compactRadius) * progress;
        return new RectangleGeometry(new Rect(pad, pad, width, height), radius, radius);
    }

    private static Geometry CreateNotch(Size size, double progress, double revealProgress)
    {
        if (revealProgress <= 0.0001)
        {
            return Geometry.Empty;
        }

        var pad = ShadowPadding;
        var left = pad;
        var right = Math.Max(left + 1, size.Width - pad);
        var top = 0.0;
        var fullBottom = Math.Max(1, size.Height - pad);
        var bottom = Math.Max(0.1, fullBottom * revealProgress);

        // During top-edge reveal the body grows downward from y=0 while both
        // inverse-R shoulders remain attached to the physical screen edge. The
        // shoulders lag the body very slightly, so they visibly grow instead of
        // popping in at their final radius.
        var shoulderScale = EdgeRevealProfile.ShoulderScale(revealProgress);
        var topRadius = (6.0 + (19.0 - 6.0) * progress) * shoulderScale;
        var bottomRadius = (14.0 + (24.0 - 14.0) * progress) * shoulderScale;
        bottomRadius = Math.Min(bottomRadius, Math.Max(0.1, (right - left) / 4.0));

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();

        ctx.BeginFigure(new Point(left, top), isFilled: true, isClosed: true);

        // Concave / inverse top-left radius.
        ctx.QuadraticBezierTo(
            new Point(left + topRadius, top),
            new Point(left + topRadius, top + topRadius),
            isStroked: true,
            isSmoothJoin: false);

        ctx.LineTo(new Point(left + topRadius, bottom - bottomRadius), true, false);
        ctx.QuadraticBezierTo(
            new Point(left + topRadius, bottom),
            new Point(left + topRadius + bottomRadius, bottom),
            true,
            false);

        ctx.LineTo(new Point(right - topRadius - bottomRadius, bottom), true, false);
        ctx.QuadraticBezierTo(
            new Point(right - topRadius, bottom),
            new Point(right - topRadius, bottom - bottomRadius),
            true,
            false);

        ctx.LineTo(new Point(right - topRadius, top + topRadius), true, false);

        // Concave / inverse top-right radius.
        ctx.QuadraticBezierTo(
            new Point(right - topRadius, top),
            new Point(right, top),
            true,
            false);

        ctx.LineTo(new Point(left, top), true, false);
        geometry.Freeze();
        return geometry;
    }
}
