using System.Windows;
using System.Windows.Media;
using TopIsland.Models;
using TopIsland.Services;

namespace TopIsland.Controls;

/// <summary>
/// Mutable runtime geometry used by the live surface animation.
/// Unlike IslandGeometryFactory (which is intentionally allocation-friendly for
/// tests/offline rendering), this object owns one geometry per style and only
/// mutates points/radii/transforms from frame to frame.
/// </summary>
internal sealed class RuntimeIslandGeometry
{
    private readonly RectangleGeometry _dynamicIsland = new();
    private readonly TranslateTransform _dynamicTranslate = new();

    private readonly PathGeometry _notch = new();
    private readonly PathFigure _notchFigure = new();
    private readonly QuadraticBezierSegment _topLeft = new();
    private readonly LineSegment _leftSide = new();
    private readonly QuadraticBezierSegment _bottomLeft = new();
    private readonly LineSegment _bottom = new();
    private readonly QuadraticBezierSegment _bottomRight = new();
    private readonly LineSegment _rightSide = new();
    private readonly QuadraticBezierSegment _topRight = new();
    private readonly TranslateTransform _notchTranslate = new();

    public RuntimeIslandGeometry()
    {
        _dynamicIsland.Transform = _dynamicTranslate;

        _notchFigure.IsFilled = true;
        _notchFigure.IsClosed = true;
        _notchFigure.Segments.Add(_topLeft);
        _notchFigure.Segments.Add(_leftSide);
        _notchFigure.Segments.Add(_bottomLeft);
        _notchFigure.Segments.Add(_bottom);
        _notchFigure.Segments.Add(_bottomRight);
        _notchFigure.Segments.Add(_rightSide);
        _notchFigure.Segments.Add(_topRight);
        _notch.Figures.Add(_notchFigure);
        _notch.Transform = _notchTranslate;
    }

    public Geometry Update(
        IslandStyle style,
        Size size,
        double expandedProgress,
        double revealProgress,
        double offsetX,
        double offsetY)
    {
        expandedProgress = Math.Clamp(expandedProgress, 0, 1);
        revealProgress = Math.Clamp(revealProgress, 0, 1);

        return style == IslandStyle.Notch
            ? UpdateNotch(size, expandedProgress, revealProgress, offsetX, offsetY)
            : UpdateDynamicIsland(size, expandedProgress, offsetX, offsetY);
    }

    private Geometry UpdateDynamicIsland(
        Size size,
        double progress,
        double offsetX,
        double offsetY)
    {
        var pad = IslandGeometryFactory.ShadowPadding;
        var width = Math.Max(1, size.Width - pad * 2);
        var height = Math.Max(1, size.Height - pad * 2);
        var compactRadius = height / 2.0;
        var expandedRadius = Math.Min(28, compactRadius);
        var radius = compactRadius + (expandedRadius - compactRadius) * progress;

        _dynamicIsland.Rect = new Rect(pad, pad, width, height);
        _dynamicIsland.RadiusX = radius;
        _dynamicIsland.RadiusY = radius;
        _dynamicTranslate.X = offsetX;
        _dynamicTranslate.Y = offsetY;
        return _dynamicIsland;
    }

    private Geometry UpdateNotch(
        Size size,
        double progress,
        double revealProgress,
        double offsetX,
        double offsetY)
    {
        if (revealProgress <= 0.0001)
        {
            return Geometry.Empty;
        }

        var pad = IslandGeometryFactory.ShadowPadding;
        var left = pad;
        var right = Math.Max(left + 1, size.Width - pad);
        var top = 0.0;
        var fullBottom = Math.Max(1, size.Height - pad);
        var bottom = Math.Max(0.1, fullBottom * revealProgress);

        var shoulderScale = EdgeRevealProfile.ShoulderScale(revealProgress);
        var topRadius = (6.0 + (19.0 - 6.0) * progress) * shoulderScale;
        var bottomRadius = (14.0 + (24.0 - 14.0) * progress) * shoulderScale;
        topRadius = Math.Min(topRadius, bottom / 2.0);
        bottomRadius = Math.Min(bottomRadius, bottom / 2.0);
        bottomRadius = Math.Min(bottomRadius, Math.Max(0.1, (right - left) / 4.0));

        _notchFigure.StartPoint = new Point(left, top);
        _topLeft.Point1 = new Point(left + topRadius, top);
        _topLeft.Point2 = new Point(left + topRadius, top + topRadius);
        _leftSide.Point = new Point(left + topRadius, bottom - bottomRadius);
        _bottomLeft.Point1 = new Point(left + topRadius, bottom);
        _bottomLeft.Point2 = new Point(left + topRadius + bottomRadius, bottom);
        _bottom.Point = new Point(right - topRadius - bottomRadius, bottom);
        _bottomRight.Point1 = new Point(right - topRadius, bottom);
        _bottomRight.Point2 = new Point(right - topRadius, bottom - bottomRadius);
        _rightSide.Point = new Point(right - topRadius, top + topRadius);
        _topRight.Point1 = new Point(right - topRadius, top);
        _topRight.Point2 = new Point(right, top);

        _notchTranslate.X = offsetX;
        _notchTranslate.Y = offsetY;
        return _notch;
    }
}
