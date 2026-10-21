namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// Durations chosen to match the referenced dashboard's feel.
/// Entrance 320ms stagger 40ms, hover 160ms, segment 250ms, toggle and icon 200ms,
/// numbers and row enter 300ms, row leave 180ms, pulse 1400ms, list throttle 300ms.
/// </summary>
public static class DashboardMotion
{
    public static readonly TimeSpan Entrance = TimeSpan.FromMilliseconds(320);
    public static readonly TimeSpan Hover = TimeSpan.FromMilliseconds(160);
    public static readonly TimeSpan Segment = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan Toggle = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan Number = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan RowEnter = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan RowLeave = TimeSpan.FromMilliseconds(180);
    public static readonly TimeSpan Pulse = TimeSpan.FromMilliseconds(1400);
    public static readonly TimeSpan ConnectionThrottle = TimeSpan.FromMilliseconds(300);
    public const int StaggerMilliseconds = 40;
    public const double EntranceOffset = 12;
    public const double EntranceScale = 0.98;
    public const double HoverScale = 1.012;
    public const double PressScale = 0.98;
    public static readonly TimeSpan PowerState = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan PowerMorph = TimeSpan.FromMilliseconds(450);
    public static readonly TimeSpan PowerRipple = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan PowerRing = TimeSpan.FromMilliseconds(1000);
    public static readonly TimeSpan PowerGlow = TimeSpan.FromMilliseconds(1800);
    public static readonly TimeSpan PowerShake = TimeSpan.FromMilliseconds(420);
    public static readonly TimeSpan NavSlide = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan PageSwap = TimeSpan.FromMilliseconds(200);
    public const double PageSwapShift = 10;
    public const double PowerHoverScale = 1.045;
    public const double PowerPressScale = 0.94;
    public const double PowerDiameter = 88;

    /// <summary>
    /// FlClash's status decoration: TweenAnimationBuilder, 450ms,
    /// Curves.easeOutBack = Cubic(0.175, 0.885, 0.32, 1.275), both directions.
    /// Reduce-motion uses a zero duration there, which we snap.
    /// </summary>
    public static readonly TimeSpan CookiePose = TimeSpan.FromMilliseconds(450);

    public const double CookieGrowth = 0.12;

    /// <summary>One sector of the 12-lobe cookie, in radians.</summary>
    public static readonly double CookieTurn = Math.PI / 6d;

    /// <summary>Flutter cubic-bezier ease-out-back. Input is 0..1; the result overshoots past 1.</summary>
    public static double CookieEase(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        var u = t;
        for (var i = 0; i < 8; i++)
        {
            var x = Cubic(u, 0.175, 0.32);
            var dx = CubicDerivative(u, 0.175, 0.32);
            if (Math.Abs(dx) < 1e-6) break;
            u = Math.Clamp(u - (x - t) / dx, 0, 1);
        }

        return Cubic(u, 0.885, 1.275);
    }

    private static double Cubic(double t, double c1, double c2)
    {
        var u = 1 - t;
        return 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t;
    }

    private static double CubicDerivative(double t, double c1, double c2)
    {
        var u = 1 - t;
        return 3 * u * u * c1 + 6 * u * t * (c2 - c1) + 3 * t * t * (1 - c2);
    }

    public static event Action? Changed;

    private static bool _reduceMotion;

    public static bool ReduceMotion
    {
        get => _reduceMotion;
        set
        {
            if (_reduceMotion == value) return;
            _reduceMotion = value;
            Changed?.Invoke();
        }
    }
}
