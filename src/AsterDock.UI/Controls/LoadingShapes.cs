namespace AsterDock.UI.Controls;

internal static class LoadingShapes
{
    public const int Samples = 160;
    public const double MorphDurationMs = 650;
    public const double RotationPeriodSeconds = 2;

    private static readonly float[] CookieRadii = Sample(Cookie);
    private static readonly float[] CloverRadii = Sample(Clover);
    private static readonly float[] PentagonRadii = Sample(RoundedPentagon);
    private static readonly float[] CircleRadii = Sample(_ => 1);

    public static IReadOnlyList<float[]> Frames { get; } = [CookieRadii, CloverRadii, PentagonRadii, CircleRadii];

    public static void Interpolate(TimeSpan elapsed, bool reduceMotion, float[] destination)
    {
        if (reduceMotion)
        {
            Array.Copy(CircleRadii, destination, Samples);
            return;
        }

        var cycle = Frames.Count * MorphDurationMs;
        var position = elapsed.TotalMilliseconds % cycle;
        if (position < 0) position += cycle;
        var index = (int)(position / MorphDurationMs);
        var local = (position - (index * MorphDurationMs)) / MorphDurationMs;
        var eased = Motion.EmphasizedEasing.Ease(local);
        var from = Frames[index];
        var to = Frames[(index + 1) % Frames.Count];
        var blend = (float)eased;
        for (var i = 0; i < Samples; i++)
            destination[i] = from[i] + ((to[i] - from[i]) * blend);
    }

    public static double Rotation(TimeSpan elapsed, bool reduceMotion)
        => reduceMotion ? 0 : elapsed.TotalSeconds / RotationPeriodSeconds * Math.Tau;

    public static double PulseOpacity(TimeSpan elapsed)
    {
        var wave = 0.5 - (0.5 * Math.Cos(elapsed.TotalSeconds / 1.6 * Math.Tau));
        return 0.58 + (0.42 * wave);
    }

    private static float[] Sample(Func<double, double> radius)
    {
        var values = new float[Samples];
        var max = 0d;
        for (var i = 0; i < Samples; i++)
        {
            var theta = i * Math.Tau / Samples;
            values[i] = (float)Math.Max(0.05, radius(theta));
            if (values[i] > max) max = values[i];
        }

        if (max > 0)
        {
            for (var i = 0; i < Samples; i++) values[i] = (float)(values[i] / max);
        }

        return values;
    }

    private static double Cookie(double theta) => 1 + (0.28 * Math.Cos(12 * theta));

    private static double Clover(double theta)
    {
        var lobe = 0.5 + (0.5 * Math.Cos(4 * theta));
        return 0.30 + (0.70 * Math.Pow(lobe, 0.62));
    }

    private static double RoundedPentagon(double theta)
    {
        const double sides = 5;
        var sector = Math.Tau / sides;
        var angle = theta + (Math.PI / 2);
        var wrapped = angle % sector;
        if (wrapped < 0) wrapped += sector;
        var distToVertex = Math.Min(wrapped, sector - wrapped);
        var phi = (sector / 2) - distToVertex;
        var raw = Math.Cos(Math.PI / sides) / Math.Cos(phi);
        var edge = Math.Cos(Math.PI / sides);
        var normalized = Math.Clamp((raw - edge) / (1 - edge), 0, 1);
        var sharp = Math.Pow(normalized, 0.72);
        var radius = 0.56 + (0.44 * sharp);
        var vertex = Math.Pow(normalized, 6);
        return radius * (1 - (0.045 * vertex));
    }
}
