using System.Windows.Media.Animation;

namespace TopIsland.Services;

public static class MotionProfile
{
    public static double Ease(double progress)
    {
        var t = Math.Clamp(progress, 0.0, 1.0);
        // Smoothstep has zero velocity at both endpoints. Unlike the previous
        // power ease-out it does not launch each transition at maximum speed,
        // which was a major source of the visible "kick" on hover/reversal.
        return t * t * (3.0 - 2.0 * t);
    }

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

        // Keep short reversals responsive without collapsing them into a 60 ms
        // snap. Every property in a surface transition uses this same duration.
        var durationScale = 0.78 + 0.22 * Math.Sqrt(distanceFactor);
        return Math.Max(110, (int)Math.Round(baseDurationMs * durationScale));
    }
}
