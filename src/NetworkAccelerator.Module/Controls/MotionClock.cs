using Avalonia.Threading;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// One UI-thread timer shared by number tweens and row transitions.
/// Subscribers are not invoked after they unsubscribe, and the timer stops when idle.
/// </summary>
public static class MotionClock
{
    private static readonly List<Action<TimeSpan>> Subscribers = [];
    private static readonly List<Action<TimeSpan>> PendingRemove = [];
    private static DispatcherTimer? _timer;
    private static long _lastMs;

    public static bool Manual { get; set; }

    public static void Advance(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero || Subscribers.Count == 0) return;
        for (var i = 0; i < Subscribers.Count; i++) Subscribers[i](elapsed);
        if (PendingRemove.Count == 0) return;
        foreach (var tick in PendingRemove) Subscribers.Remove(tick);
        PendingRemove.Clear();
    }

    public static void Subscribe(Action<TimeSpan> tick)
    {
        if (Subscribers.Contains(tick)) return;
        Subscribers.Add(tick);
        if (Manual || _timer is not null) return;
        _lastMs = Environment.TickCount64;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public static void Unsubscribe(Action<TimeSpan> tick) => PendingRemove.Add(tick);

    private static void OnTick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        var step = Math.Clamp(now - _lastMs, 1, 100);
        _lastMs = now;
        var elapsed = TimeSpan.FromMilliseconds(step);
        for (var i = 0; i < Subscribers.Count; i++) Subscribers[i](elapsed);
        if (PendingRemove.Count > 0)
        {
            foreach (var tick in PendingRemove) Subscribers.Remove(tick);
            PendingRemove.Clear();
        }

        if (Subscribers.Count > 0) return;
        _timer?.Stop();
        _timer = null;
        _lastMs = 0;
    }
}
