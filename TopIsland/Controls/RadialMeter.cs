using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TopIsland.Controls;

public sealed class RadialMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RadialMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    private static readonly DependencyProperty AnimatedValueProperty = DependencyProperty.Register(
        nameof(AnimatedValue), typeof(double), typeof(RadialMeter),
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

    private double AnimatedValue { get => (double)GetValue(AnimatedValueProperty); set => SetValue(AnimatedValueProperty, value); }

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

    private static void OnValueChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var meter = (RadialMeter)dependencyObject;
        var target = Math.Clamp((double)args.NewValue, 0, 100);
        var from = Math.Clamp(meter.AnimatedValue, 0, 100);

        meter.BeginAnimation(AnimatedValueProperty, null);
        meter.AnimatedValue = from;

        if (!meter.IsLoaded || Math.Abs(target - from) < 0.15)
        {
            meter.AnimatedValue = target;
            return;
        }

        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(meter.IsTachometer ? 260 : 220))
        {
            EasingFunction = new PowerEase { Power = 2.15, EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            meter.BeginAnimation(AnimatedValueProperty, null);
            meter.AnimatedValue = target;
        };
        meter.BeginAnimation(AnimatedValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
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

        var value = Math.Clamp(AnimatedValue, 0, 100);
        if (value > 0.2)
        {
            dc.DrawGeometry(null, progressPen, CreateArc(center, radius, -90, 360 * value / 100));
        }

        DrawValue(dc, dpi, center, 10, value);

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
        // Compact usage gauge: deliberately open on the right. It reads like
        // a dashboard tachometer "(" wrapping the label/value instead of a
        // nearly closed progress ring "( ... )".
        const double startAngle = 154;
        const double sweepAngle = 196;
        var radius = Math.Max(7, Math.Min(ActualHeight - 4, 30) / 2.0);
        var center = new Point(16.8, ActualHeight / 2 + 1.2);
        var trackPen = CreatePen(TrackBrush, 2.15);
        var progressPen = CreatePen(ProgressBrush, 2.9);

        dc.DrawGeometry(null, trackPen, CreateArc(center, radius, startAngle, sweepAngle));

        var tickPen = new Pen(TrackBrush, 0.85)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        for (var index = 1; index < 5; index++)
        {
            var angle = startAngle + sweepAngle * index / 5.0;
            var outer = PointOnCircle(center, radius + 0.1, angle);
            var inner = PointOnCircle(center, radius - 2.1, angle);
            dc.DrawLine(tickPen, inner, outer);
        }

        var value = Math.Clamp(AnimatedValue, 0, 100);
        if (value > 0.2)
        {
            dc.DrawGeometry(null, progressPen, CreateArc(center, radius, startAngle, sweepAngle * value / 100));
        }

        var labelText = new FormattedText(Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            7.0, SecondaryTextBrush, dpi);
        dc.DrawText(labelText, new Point(20.0, 4.2));

        var valueText = new FormattedText($"{value:0}%", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            8.8, TextBrush, dpi);
        dc.DrawText(valueText, new Point(19.6, 13.2));
    }

    private void DrawValue(DrawingContext dc, double dpi, Point center, double fontSize, double value)
    {
        var valueText = new FormattedText($"{value:0}%", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
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
