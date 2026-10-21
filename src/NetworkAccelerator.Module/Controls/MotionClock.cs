using Avalonia.Threading;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// One UI-thread timer shared by number tweens and row transitions.
/// Subscribers are not invoked after they unsubscribe, and the timer stops when idle.
/// Unsubscribe and subscribe in the same tick keeps the subscription.
/// </summary>
public static class MotionClock
{
    private static readonly List<Action<TimeSpan>> Subscribers = [];
    private static readonly List<Action<TimeSpan>> PendingRemove = [];
    private static DispatcherTimer? _timer;
    private static long _lastMs;
    private static bool _dispatching;

    public static bool Manual { get; set; }

    public static void Advance(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return;
        Dispatch(elapsed);
    }

    public static void Subscribe(Action<TimeSpan> tick)
    {
        Remove(PendingRemove, tick);
        if (!Contains(Subscribers, tick)) Subscribers.Add(tick);
        if (Manual || _timer is not null) return;
        _lastMs = Environment.TickCount64;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public static void Unsubscribe(Action<TimeSpan> tick)
    {
        if (!Contains(Subscribers, tick))
        {
            Remove(PendingRemove, tick);
            return;
        }

        if (_dispatching)
        {
            if (!Contains(PendingRemove, tick)) PendingRemove.Add(tick);
            return;
        }

        Remove(Subscribers, tick);
        Remove(PendingRemove, tick);
        StopTimerIfIdle();
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        var step = Math.Clamp(now - _lastMs, 1, 100);
        _lastMs = now;
        Dispatch(TimeSpan.FromMilliseconds(step));
    }

    private static void Dispatch(TimeSpan elapsed)
    {
        _dispatching = true;
        try
        {
            for (var i = 0; i < Subscribers.Count; i++)
            {
                var tick = Subscribers[i];
                if (Contains(PendingRemove, tick)) continue;
                tick(elapsed);
            }
        }
        finally
        {
            _dispatching = false;
            if (PendingRemove.Count > 0)
            {
                foreach (var tick in PendingRemove) Remove(Subscribers, tick);
                PendingRemove.Clear();
            }

            StopTimerIfIdle();
        }
    }

    private static void StopTimerIfIdle()
    {
        if (Subscribers.Count > 0) return;
        _timer?.Stop();
        _timer = null;
        _lastMs = 0;
    }

    private static bool Contains(List<Action<TimeSpan>> list, Action<TimeSpan> tick)
    {
        foreach (var item in list)
            if (item == tick) return true;
        return false;
    }

    private static void Remove(List<Action<TimeSpan>> list, Action<TimeSpan> tick)
    {
        for (var i = list.Count - 1; i >= 0; i--)
            if (list[i] == tick) list.RemoveAt(i);
    }
}
