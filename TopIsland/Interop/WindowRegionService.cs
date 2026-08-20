using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TopIsland.Interop;

public sealed class WindowRegionService
{
    private const int Alternate = 1;
    private const int RgnOr = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreatePolygonRgn([In] NativePoint[] points, int count, int fillMode);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    public bool Apply(Window window, Geometry geometry)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var source = PresentationSource.FromVisual(window);
        var transform = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var flattened = geometry.GetFlattenedPathGeometry(0.6, ToleranceType.Absolute);
        var combined = CreateRectRgn(0, 0, 0, 0);
        if (combined == IntPtr.Zero)
        {
            return false;
        }

        var hasFigure = false;
        try
        {
            foreach (var figure in flattened.Figures)
            {
                var points = FlattenFigure(figure)
                    .Select(point => transform.Transform(point))
                    .Select(point => new NativePoint
                    {
                        X = (int)Math.Round(point.X),
                        Y = (int)Math.Round(point.Y)
                    })
                    .ToArray();

                if (points.Length < 3)
                {
                    continue;
                }

                var figureRegion = CreatePolygonRgn(points, points.Length, Alternate);
                if (figureRegion == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    _ = CombineRgn(combined, combined, figureRegion, RgnOr);
                    hasFigure = true;
                }
                finally
                {
                    _ = DeleteObject(figureRegion);
                }
            }

            if (!hasFigure)
            {
                return false;
            }

            var result = SetWindowRgn(hwnd, combined, true);
            if (result != 0)
            {
                combined = IntPtr.Zero; // The system owns the region after success.
                return true;
            }

            return false;
        }
        finally
        {
            if (combined != IntPtr.Zero)
            {
                _ = DeleteObject(combined);
            }
        }
    }

    public void Reset(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            _ = SetWindowRgn(hwnd, IntPtr.Zero, true);
        }
    }

    private static IEnumerable<Point> FlattenFigure(PathFigure figure)
    {
        yield return figure.StartPoint;

        foreach (var segment in figure.Segments)
        {
            switch (segment)
            {
                case LineSegment line:
                    yield return line.Point;
                    break;
                case PolyLineSegment poly:
                    foreach (var point in poly.Points)
                    {
                        yield return point;
                    }
                    break;
            }
        }
    }
}