namespace TopIsland.Services;

public static class EdgeRevealProfile
{
    public static double ShoulderScale(double revealProgress)
    {
        revealProgress = Math.Clamp(revealProgress, 0, 1);
        // The inverse-R shoulders grow a touch behind the body so they emerge
        // from the physical top edge instead of appearing at full radius.
        return Math.Pow(revealProgress, 1.18);
    }
}
