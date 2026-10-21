namespace NetworkAccelerator.Module.Controls;

public enum NavGlassPath
{
    Pending,
    Acrylic,
    Approximate
}

/// <summary>
/// One probe of the machine the nav glass is about to run on.
/// SoftwareRenderer is true for software Skia, false for a GPU context, and null when the lease was unavailable.
/// </summary>
public readonly record struct NavGlassProbe(bool? SoftwareRenderer, double SnapshotMilliseconds, bool Failed)
{
    public static NavGlassProbe SlowSoftware { get; } = new(true, 80, false);

    public static NavGlassProbe FastSoftware { get; } = new(true, 12, false);
}

/// <summary>
/// Picks Fluid acrylic versus the snapshot blur. A failed probe, or a slow snapshot when the
/// renderer is software or unknown, stays on the approximate glass. A known GPU still tries
/// acrylic; sustained frame cost is tracked separately and does not bounce back.
/// </summary>
public static class NavGlassChoice
{
    public const double SlowMilliseconds = 40;
    public const int SlowFramesBeforeFallback = 3;
    public const int StableFramesBeforeLock = 8;

    public static bool UseApproximate(NavGlassProbe probe)
    {
        if (probe.Failed) return true;
        if (probe.SoftwareRenderer == false) return false;
        return probe.SnapshotMilliseconds > SlowMilliseconds;
    }

    public static bool SustainedSlow(int consecutiveSlowFrames) =>
        consecutiveSlowFrames >= SlowFramesBeforeFallback;
}
