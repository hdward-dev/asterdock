namespace AsterDock.UI.Motion;

internal static class SpinnerMotion
{
    public const double TurnSeconds = 0.9;

    /// <summary>About 100 degrees. Long enough to read as a spinner, short enough to stay an arc.</summary>
    public const double ArcSweep = 0.28 * Math.Tau;

    public static double Rotation(TimeSpan elapsed, bool reduceMotion)
        => reduceMotion ? -Math.PI / 2 : -Math.PI / 2 + (elapsed.TotalSeconds / TurnSeconds * Math.Tau);

    public static double PulseOpacity(TimeSpan elapsed)
    {
        var wave = 0.5 - (0.5 * Math.Cos(elapsed.TotalSeconds / 1.6 * Math.Tau));
        return 0.45 + (0.55 * wave);
    }
}
