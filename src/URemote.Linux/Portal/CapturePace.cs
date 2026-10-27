namespace URemote.Linux;

// Target rate is decided by the caller (min of refresh and the controller request).
// A new PipeWire sample means the picture changed. Repeats stay at that target only
// for a short hold, so a damage tick still fills the refresh interval; a quiet
// desktop then drops. The hold is not a reason to discard a sample that already arrived.
internal static class CapturePace
{
    internal static readonly TimeSpan MotionHold = TimeSpan.FromMilliseconds(250);
    internal static bool ContentChanging(TimeSpan sinceNewSample) =>
        sinceNewSample >= TimeSpan.Zero && sinceNewSample <= MotionHold;
}
