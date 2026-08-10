using AsterDock.Contracts;
using DeviceInformation.Core.Services;

namespace AsterDock.Host.Services;

internal sealed class SharedSystemMetricsService : ISystemMetricsService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(8);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _collectorGate = new(1, 1);
    private readonly Dictionary<long, Subscriber> _subscribers = [];
    private DeviceInformationService? _collector;
    private CancellationTokenSource? _pollingCancellation;
    private long _idleGeneration;
    private SystemDeviceDetails? _details;
    private SystemMetricsSnapshot? _current;
    private long _nextSubscriberId;
    private bool _disposed;

    public SystemMetricsSnapshot? Current
    {
        get
        {
            lock (_sync) return _current;
        }
    }

    public async Task<SystemDeviceDetails> GetDeviceDetailsAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_details is not null) return _details;
        }

        await _collectorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_details is not null) return _details;
            }

            var source = await GetCollector().GetDetailsAsync(cancellationToken).ConfigureAwait(false);
            var details = new SystemDeviceDetails(
                source.DeviceName,
                source.DeviceModel,
                source.OperatingSystem,
                source.Architecture,
                source.ProcessorName,
                source.GraphicsName,
                source.TotalMemoryBytes,
                source.SystemDrive);
            lock (_sync) return _details ??= details;
        }
        finally
        {
            _collectorGate.Release();
        }
    }

    public IDisposable Subscribe(Action<SystemMetricsSnapshot> onUpdated, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(onUpdated);
        SystemMetricsSnapshot? current;
        long id;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            id = ++_nextSubscriberId;
            _subscribers.Add(id, new Subscriber(onUpdated, onError));
            current = _current;
            _idleGeneration++;
            if (_pollingCancellation is null)
            {
                _pollingCancellation = new CancellationTokenSource();
                _ = PollAsync(_pollingCancellation.Token);
            }
        }

        if (current is not null) InvokeSafely(onUpdated, current);
        return new Subscription(this, id);
    }

    public void Dispose()
    {
        CancellationTokenSource? polling;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _subscribers.Clear();
            polling = _pollingCancellation;
            _pollingCancellation = null;
            _idleGeneration++;
        }

        polling?.Cancel();
        polling?.Dispose();

        _collectorGate.Wait();
        try
        {
            DeviceInformationService? collector;
            lock (_sync)
            {
                collector = _collector;
                _collector = null;
            }
            collector?.Dispose();
        }
        finally
        {
            _collectorGate.Release();
        }
    }

    private DeviceInformationService GetCollector()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _collector ??= new DeviceInformationService();
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CaptureAndPublishAsync(cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await CaptureAndPublishAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            NotifyError(exception);
        }
    }

    private async Task CaptureAndPublishAsync(CancellationToken cancellationToken)
    {
        await _collectorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SystemMetricsSnapshot snapshot;
        try
        {
            var source = await GetCollector().CaptureAsync(cancellationToken).ConfigureAwait(false);
            snapshot = new SystemMetricsSnapshot(
                source.Timestamp,
                source.CpuUsage,
                source.CpuTemperature,
                source.GpuUsage,
                source.GpuTemperature,
                source.UsedMemoryBytes,
                source.TotalMemoryBytes,
                source.DiskUsage,
                source.DownloadBytesPerSecond,
                source.UploadBytesPerSecond);
        }
        finally
        {
            _collectorGate.Release();
        }

        Subscriber[] subscribers;
        lock (_sync)
        {
            if (_disposed) return;
            _current = snapshot;
            subscribers = _subscribers.Values.ToArray();
        }
        foreach (var subscriber in subscribers) InvokeSafely(subscriber.OnUpdated, snapshot);
    }

    private void NotifyError(Exception exception)
    {
        Subscriber[] subscribers;
        lock (_sync) subscribers = _subscribers.Values.ToArray();
        foreach (var subscriber in subscribers)
        {
            try { subscriber.OnError?.Invoke(exception); }
            catch { }
        }
    }

    private void Unsubscribe(long id)
    {
        lock (_sync)
        {
            if (_disposed || !_subscribers.Remove(id) || _subscribers.Count != 0) return;
            var generation = ++_idleGeneration;
            _ = StopWhenIdleAsync(generation);
        }
    }

    private async Task StopWhenIdleAsync(long generation)
    {
        await Task.Delay(IdleDelay).ConfigureAwait(false);

        CancellationTokenSource? polling;
        lock (_sync)
        {
            if (_disposed || _subscribers.Count != 0 || generation != _idleGeneration) return;
            polling = _pollingCancellation;
            _pollingCancellation = null;
        }
        polling?.Cancel();
        polling?.Dispose();

        await _collectorGate.WaitAsync().ConfigureAwait(false);
        try
        {
            DeviceInformationService? collector = null;
            lock (_sync)
            {
                // A new subscriber may have arrived while the previous polling
                // operation was winding down. In that case it keeps the collector.
                if (!_disposed && _subscribers.Count == 0)
                {
                    collector = _collector;
                    _collector = null;
                }
            }
            collector?.Dispose();
        }
        finally
        {
            _collectorGate.Release();
        }
    }

    private static void InvokeSafely(Action<SystemMetricsSnapshot> callback, SystemMetricsSnapshot snapshot)
    {
        try { callback(snapshot); }
        catch { }
    }

    private sealed record Subscriber(
        Action<SystemMetricsSnapshot> OnUpdated,
        Action<Exception>? OnError);

    private sealed class Subscription(SharedSystemMetricsService owner, long id) : IDisposable
    {
        private SharedSystemMetricsService? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(id);
    }
}
