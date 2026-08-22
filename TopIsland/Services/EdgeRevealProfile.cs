namespace TopIsland.Services;

public static class EdgeRevealProfile
{
    public static double EaseReveal(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var inverse = 1 - t;
        return 1 - inverse * inverse * inverse;
    }

    public static double EaseHide(double t)
    {
        t = Math.Clamp(t, 0, 1);
        // A smooth reverse that starts moving immediately but avoids snapping
        // the inverse shoulders shut before the body has begun to retract.
        return 1 - (t * t * (3 - 2 * t));
    }

    public static double ShoulderScale(double revealProgress)
    {
        revealProgress = Math.Clamp(revealProgress, 0, 1);
        // Let the body lead very slightly so the inverse-R shoulders visibly
        // grow at the screen edge instead of appearing at full radius at once.
        return Math.Pow(revealProgress, 1.18);
    }
}
