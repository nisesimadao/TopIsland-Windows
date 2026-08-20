using System.Windows;
using System.Windows.Media;
using TopIsland.Models;

namespace TopIsland.Controls;

public static class IslandGeometryFactory
{
    public const double ShadowPadding = 16;

    public static Geometry Create(IslandStyle style, Size size, bool expanded)
    {
        return style == IslandStyle.Notch
            ? CreateNotch(size, expanded)
            : CreateDynamicIsland(size, expanded);
    }

    private static Geometry CreateDynamicIsland(Size size, bool expanded)
    {
        var pad = ShadowPadding;
        var width = Math.Max(1, size.Width - pad * 2);
        var height = Math.Max(1, size.Height - pad * 2);

        // Compact Dynamic Island is always a geometrically true pill.
        var radius = expanded ? Math.Min(32, height * 0.24) : height / 2.0;
        return new RectangleGeometry(new Rect(pad, pad, width, height), radius, radius);
    }

    private static Geometry CreateNotch(Size size, bool expanded)
    {
        var pad = ShadowPadding;
        var shoulder = expanded ? 52.0 : 38.0;
        var left = pad + shoulder;
        var right = size.Width - pad - shoulder;
        var bottom = Math.Max(2, size.Height - pad);
        var shoulderDepth = expanded ? 22.0 : 15.0;
        var bottomRadius = expanded ? 30.0 : Math.Min(23.0, bottom * 0.44);

        // The shoulder is a real concave/inverse-radius transition.
        // Its tangent starts horizontal at the screen edge and ends vertical at
        // the notch wall, so there is no tiny kink where ----\ becomes |.
        // Visually the lower boundary flows like:  --------\________/--------
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();

        ctx.BeginFigure(new Point(pad, 0), isFilled: true, isClosed: true);

        ctx.BezierTo(
            new Point(pad + shoulder * 0.52, 0),
            new Point(left, shoulderDepth * 0.40),
            new Point(left, shoulderDepth),
            true,
            false);

        ctx.LineTo(new Point(left, bottom - bottomRadius), true, false);
        ctx.BezierTo(
            new Point(left, bottom - bottomRadius * 0.42),
            new Point(left + bottomRadius * 0.42, bottom),
            new Point(left + bottomRadius, bottom),
            true,
            false);

        ctx.LineTo(new Point(right - bottomRadius, bottom), true, false);
        ctx.BezierTo(
            new Point(right - bottomRadius * 0.42, bottom),
            new Point(right, bottom - bottomRadius * 0.42),
            new Point(right, bottom - bottomRadius),
            true,
            false);

        ctx.LineTo(new Point(right, shoulderDepth), true, false);
        ctx.BezierTo(
            new Point(right, shoulderDepth * 0.40),
            new Point(size.Width - pad - shoulder * 0.52, 0),
            new Point(size.Width - pad, 0),
            true,
            false);

        ctx.LineTo(new Point(pad, 0), true, false);

        geometry.Freeze();
        return geometry;
    }
}
