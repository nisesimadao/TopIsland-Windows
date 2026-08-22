using System.Windows.Media.Animation;

namespace TopIsland.Services;

public static class MotionProfile
{
    public const double EasePower = 2.2;

    public static double Ease(double progress)
    {
        var t = Math.Clamp(progress, 0.0, 1.0);
        return 1.0 - Math.Pow(1.0 - t, EasePower);
    }

    public static IEasingFunction CreateWpfEasing() =>
        new PowerEase
        {
            Power = EasePower,
            EasingMode = EasingMode.EaseOut
        };

    public static int ScaleDuration(int baseDurationMs, double widthDelta, double heightDelta)
    {
        var distanceFactor = Math.Clamp(Math.Max(Math.Abs(widthDelta) / 900.0, Math.Abs(heightDelta) / 320.0), 0.0, 1.0);
        var durationScale = 0.52 + 0.48 * Math.Sqrt(distanceFactor);
        return Math.Max(65, (int)Math.Round(baseDurationMs * durationScale));
    }
}
