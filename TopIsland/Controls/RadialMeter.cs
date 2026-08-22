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

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public Brush ProgressBrush { get => (Brush)GetValue(ProgressBrushProperty); set => SetValue(ProgressBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush SecondaryTextBrush { get => (Brush)GetValue(SecondaryTextBrushProperty); set => SetValue(SecondaryTextBrushProperty, value); }
    public bool ShowLabel { get => (bool)GetValue(ShowLabelProperty); set => SetValue(ShowLabelProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(46, 52);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var diameter = Math.Min(ActualWidth, ShowLabel ? Math.Max(0, ActualHeight - 8) : ActualHeight);
        var radius = Math.Max(5, diameter / 2 - 3.2);
        var center = new Point(ActualWidth / 2, ShowLabel ? radius + 2.5 : ActualHeight / 2);
        var trackPen = new Pen(TrackBrush, 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var progressPen = new Pen(ProgressBrush, 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        var value = Math.Clamp(Value, 0, 100);
        if (value > 0.2)
        {
            var startAngle = -90d;
            var endAngle = startAngle + 360d * value / 100d;
            var start = PointOnCircle(center, radius, startAngle);
            var end = PointOnCircle(center, radius, endAngle);
            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, value > 50, SweepDirection.Clockwise, true));
            var geometry = new PathGeometry([figure]);
            dc.DrawGeometry(null, progressPen, geometry);
        }

        var valueText = new FormattedText($"{value:0}%", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 10, TextBrush, dpi);
        dc.DrawText(valueText, new Point(center.X - valueText.Width / 2, center.Y - valueText.Height / 2));

        if (ShowLabel)
        {
            var labelText = new FormattedText(Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 8, SecondaryTextBrush, dpi);
            dc.DrawText(labelText, new Point(ActualWidth / 2 - labelText.Width / 2, Math.Max(0, ActualHeight - labelText.Height)));
        }
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180d;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
