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
    public const double CycleSeconds = 0.7;

    /// <summary>Extra turns per cycle, on top of the arc chasing its own tail.</summary>
    public const double SpinTurns = 0.85;

    public static readonly TimeSpan PeakAt = TimeSpan.FromSeconds(CycleSeconds * 0.24);

    public static SpinnerPose Sample(TimeSpan elapsed, bool reduceMotion)
    {
        if (reduceMotion)
        {
            return new SpinnerPose(-Math.PI / 2, 0.34 * Math.Tau, 0, 0, 1, 1, PulseOpacity(elapsed));
        }

        var turns = elapsed.TotalSeconds / CycleSeconds;
        var u = turns - Math.Floor(turns);
        const double min = 0.07;
        const double headWindow = 0.8;
        const double delay = 0.24;
        var head = min + Rush(u / headWindow);
        var tail = Rush((u - delay) / headWindow);
        var sweepTurns = head - tail;
        var start = -Math.PI / 2 + ((tail + (turns * SpinTurns)) * Math.Tau);
        var sweep = sweepTurns * Math.Tau;
        var gap = Math.Tau - sweep;
        var echo = Math.Min(0.1 * Math.Tau, gap * 0.34);
        if (echo < 0.045 * Math.Tau) echo = 0;
        var fullness = Math.Clamp((sweepTurns - min) / 0.66, 0, 1);
        return new SpinnerPose(
            start,
            sweep,
            start - echo - (0.015 * Math.Tau),
            echo,
            0.94 + (0.06 * fullness),
            0.965 + (0.035 * fullness),
            1);
    }

    public static double PulseOpacity(TimeSpan elapsed)
    {
        var wave = 0.5 - (0.5 * Math.Cos(elapsed.TotalSeconds / 1.6 * Math.Tau));
        return 0.45 + (0.55 * wave);
    }

    private static double Rush(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        var inv = 1 - t;
        return 1 - (inv * inv * inv);
    }
}
