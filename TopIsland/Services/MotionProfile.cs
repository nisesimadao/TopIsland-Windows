using System.Windows.Media.Animation;

namespace TopIsland.Services;

public static class MotionProfile
{
    public static double Ease(double progress)
    {
        var t = Math.Clamp(progress, 0.0, 1.0);
        // Quintic smootherstep is C2-continuous: position, velocity and
        // acceleration all settle at both ends. That removes the small jerk the
        // cubic smoothstep still showed when a shell reached its final size.
        return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
    }

    public static double EaseRange(double value, double start, double end)
    {
        if (end <= start)
        {
            return value >= end ? 1.0 : 0.0;
        }

        return Ease(Math.Clamp((value - start) / (end - start), 0.0, 1.0));
    }


    private static double Range(double value, double start, double end)
    {
        if (end <= start) return value >= end ? 1.0 : 0.0;
        return Math.Clamp((value - start) / (end - start), 0.0, 1.0);
    }

    // ShapeProgress has already gone through the shell easing. Do not ease these
    // ranges a second time: double-easing held content still, then made it jump.
    public static double CompactExit(double expansion) => Range(expansion, 0.02, 0.64);
    public static double ExpandedTopEnter(double expansion) => Range(expansion, 0.02, 0.64);
    public static double ExpandedBottomEnter(double expansion) => Range(expansion, 0.10, 0.82);
    public static double ExpandedTopLeftEnter(double expansion) => Range(expansion, 0.02, 0.62);
    public static double ExpandedTopRightEnter(double expansion) => Range(expansion, 0.04, 0.66);
    public static double ExpandedBottomLeftEnter(double expansion) => Range(expansion, 0.10, 0.78);
    public static double ExpandedBottomMiddleEnter(double expansion) => Range(expansion, 0.14, 0.84);
    public static double ExpandedBottomRightEnter(double expansion) => Range(expansion, 0.18, 0.88);

    public const double CompactTravelY = 44.0;
    public const double ExpandedTopTravelY = 34.0;
    public const double ExpandedBottomTravelY = 52.0;
    public static IEasingFunction CreateWpfEasing() =>
        new SineEase { EasingMode = EasingMode.EaseInOut };

    public static int ScaleDuration(
        int baseDurationMs,
        double widthDelta,
        double heightDelta,
        double visualDelta = 0)
    {
        var distanceFactor = Math.Clamp(
            Math.Max(
                Math.Max(Math.Abs(widthDelta) / 900.0, Math.Abs(heightDelta) / 320.0),
                Math.Abs(visualDelta)),
            0.0,
            1.0);

        // Keep short reversals responsive without collapsing them into a snap.
        var durationScale = 0.76 + 0.24 * Math.Sqrt(distanceFactor);
        return Math.Max(108, (int)Math.Round(baseDurationMs * durationScale));
    }
}
