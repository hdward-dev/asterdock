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
    public static readonly TimeSpan BlobDrift = TimeSpan.FromMilliseconds(8000);
    public const double PowerHoverScale = 1.045;
    public const double PowerPressScale = 0.94;
    public const double PowerDiameter = 88;

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
