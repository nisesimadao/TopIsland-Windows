using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TopIsland.Controls;

public sealed class RadialMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RadialMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(RadialMeter),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(
        nameof(ProgressBrush), typeof(Brush), typeof(RadialMeter),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RadialMeter),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(RadialMeter),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryTextBrushProperty = DependencyProperty.Register(
        nameof(SecondaryTextBrush), typeof(Brush), typeof(RadialMeter),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowLabelProperty = DependencyProperty.Register(
        nameof(ShowLabel), typeof(bool), typeof(RadialMeter),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsTachometerProperty = DependencyProperty.Register(
        nameof(IsTachometer), typeof(bool), typeof(RadialMeter),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public Brush ProgressBrush { get => (Brush)GetValue(ProgressBrushProperty); set => SetValue(ProgressBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush SecondaryTextBrush { get => (Brush)GetValue(SecondaryTextBrushProperty); set => SetValue(SecondaryTextBrushProperty, value); }
    public bool ShowLabel { get => (bool)GetValue(ShowLabelProperty); set => SetValue(ShowLabelProperty, value); }
    public bool IsTachometer { get => (bool)GetValue(IsTachometerProperty); set => SetValue(IsTachometerProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => IsTachometer ? new(40, 40) : new(46, 52);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (IsTachometer)
        {
            DrawTachometer(dc);
            return;
        }

        DrawRadialMeter(dc);
    }

    private void DrawRadialMeter(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var diameter = Math.Min(ActualWidth, ShowLabel ? Math.Max(0, ActualHeight - 8) : ActualHeight);
        var radius = Math.Max(5, diameter / 2 - 3.2);
        var center = new Point(ActualWidth / 2, ShowLabel ? radius + 2.5 : ActualHeight / 2);
        var trackPen = CreatePen(TrackBrush, 3.2);
        var progressPen = CreatePen(ProgressBrush, 3.2);
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        var value = Math.Clamp(Value, 0, 100);
        if (value > 0.2)
        {
            dc.DrawGeometry(null, progressPen, CreateArc(center, radius, -90, 360 * value / 100));
        }

        DrawValue(dc, dpi, center, 10, includePercentSign: true);

        if (ShowLabel)
        {
            var labelText = new FormattedText(Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 8, SecondaryTextBrush, dpi);
            dc.DrawText(labelText, new Point(ActualWidth / 2 - labelText.Width / 2, Math.Max(0, ActualHeight - labelText.Height)));
        }
    }

    private void DrawTachometer(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        // Compact meters are intentionally raised and clipped by their parent.
        // A 270-degree sweep reads like an automotive tachometer instead of a
        // circular progress ring: low usage begins at the lower-left and climbs
        // clockwise over the top before reaching the lower-right.
        const double startAngle = 135;
        const double sweepAngle = 270;
        var radius = Math.Max(7, Math.Min(ActualWidth, ActualHeight) / 2 - 3.0);
        var center = new Point(ActualWidth / 2 - 1.0, ActualHeight / 2 - 1.5);
        var trackPen = CreatePen(TrackBrush, 2.35);
        var progressPen = CreatePen(ProgressBrush, 3.1);

        dc.DrawGeometry(null, trackPen, CreateArc(center, radius, startAngle, sweepAngle));

        var tickPen = new Pen(TrackBrush, 1.0);
        for (var index = 0; index <= 6; index++)
        {
            var angle = startAngle + sweepAngle * index / 6.0;
            var outer = PointOnCircle(center, radius + 0.2, angle);
            var inner = PointOnCircle(center, radius - 2.6, angle);
            dc.DrawLine(tickPen, inner, outer);
        }

        var value = Math.Clamp(Value, 0, 100);
        if (value > 0.2)
        {
            dc.DrawGeometry(null, progressPen, CreateArc(center, radius, startAngle, sweepAngle * value / 100));
        }

        DrawValue(dc, dpi, new Point(center.X + 1.0, center.Y + 2.2), 9.2, includePercentSign: true);
    }

    private void DrawValue(DrawingContext dc, double dpi, Point center, double fontSize, bool includePercentSign)
    {
        var value = Math.Clamp(Value, 0, 100);
        var text = includePercentSign ? $"{value:0}%" : $"{value:0}";
        var valueText = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), fontSize, TextBrush, dpi);
        dc.DrawText(valueText, new Point(center.X - valueText.Width / 2, center.Y - valueText.Height / 2));
    }

    private static Pen CreatePen(Brush brush, double thickness) => new(brush, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round
    };

    private static Geometry CreateArc(Point center, double radius, double startAngle, double sweepAngle)
    {
        var clampedSweep = Math.Clamp(sweepAngle, 0, 359.999);
        if (clampedSweep <= 0.001)
        {
            return Geometry.Empty;
        }

        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, startAngle + clampedSweep);
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(
            end,
            new Size(radius, radius),
            0,
            clampedSweep > 180,
            SweepDirection.Clockwise,
            true));
        return new PathGeometry([figure]);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180d;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
