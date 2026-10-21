using System.Diagnostics;
using Avalonia.Threading;

namespace AsterDock.UI.Motion;

internal interface ILoadingClockClient
{
    bool OnClock(TimeSpan now);
}

internal static class LoadingClock
{
    private static readonly List<ILoadingClockClient> Subscribers = [];
    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    private static DispatcherTimer? _timer;
    private static TimeSpan? _frozen;
    private static bool _ticking;

    public static TimeSpan Now => _frozen ?? Watch.Elapsed;

    public static void Freeze(TimeSpan time) => _frozen = time;

    public static void Resume() => _frozen = null;

    public static void Subscribe(ILoadingClockClient client)
    {
        if (!Subscribers.Contains(client)) Subscribers.Add(client);
        if (_frozen is not null) return;
        EnsureTimer();
    }

    public static void Unsubscribe(ILoadingClockClient client)
    {
        Subscribers.Remove(client);
        if (Subscribers.Count == 0) _timer?.Stop();
    }

    public static void TickAll() => Dispatch(Now);

    private static void EnsureTimer()
    {
        if (_timer is null)
        {
            // Background priority is starved by software rendering and acrylic glass, which freezes the arc.
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (_, _) => Dispatch(Now);
        }

        if (!_timer.IsEnabled) _timer.Start();
    }

    private static void Dispatch(TimeSpan now)
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            for (var i = Subscribers.Count - 1; i >= 0; i--)
            {
                if (i >= Subscribers.Count) continue;
                var client = Subscribers[i];
                if (client.OnClock(now)) continue;
                var index = Subscribers.IndexOf(client);
                if (index >= 0) Subscribers.RemoveAt(index);
            }
        }
        finally
        {
            _ticking = false;
        }

        if (Subscribers.Count == 0) _timer?.Stop();
    }
}
