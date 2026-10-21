namespace AsterDock.UI.Motion;

internal readonly record struct SpinnerPose(
    double Start,
    double Sweep,
    double EchoStart,
    double EchoSweep,
    double RingScale,
    double DiscScale,
    double Opacity);

internal static class SpinnerMotion
{
    public const double CycleSeconds = 0.54;

    /// <summary>Extra turns per cycle on top of the head rushing ahead of the tail.</summary>
    public const double SpinTurns = 1.1;

    public static readonly TimeSpan PeakAt = TimeSpan.FromSeconds(CycleSeconds * 0.45);

    public static SpinnerPose Sample(TimeSpan elapsed, bool reduceMotion)
    {
        if (reduceMotion)
        {
            return new SpinnerPose(-Math.PI / 2, 0.34 * Math.Tau, 0, 0, 1, 1, PulseOpacity(elapsed));
        }

        var turns = elapsed.TotalSeconds / CycleSeconds;
        var u = turns - Math.Floor(turns);
        const double min = 0.055;
        const double travel = 0.68;
        var head = EaseOut(u / 0.55);
        var tail = EaseIn((u - 0.28) / 0.62);
        var sweepTurns = min + (travel * (head - tail));
        var start = -Math.PI / 2 + ((tail + (turns * SpinTurns)) * Math.Tau);
        var sweep = sweepTurns * Math.Tau;
        var dash = 0.11 * Math.Tau;
        var gap = Math.Tau - sweep;
        var fullness = Math.Clamp((sweepTurns - min) / 0.66, 0, 1);
        return new SpinnerPose(
            start,
            sweep,
            start + sweep + ((gap - dash) * 0.5),
            dash,
            0.9 + (0.1 * fullness),
            0.92 + (0.08 * fullness),
            1);
    }

    public static double PulseOpacity(TimeSpan elapsed)
    {
        var wave = 0.5 - (0.5 * Math.Cos(elapsed.TotalSeconds / 1.6 * Math.Tau));
        return 0.45 + (0.55 * wave);
    }

    private static double EaseOut(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        var inv = 1 - t;
        return 1 - (inv * inv * inv);
    }

    private static double EaseIn(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        return t * t * t;
    }
}
