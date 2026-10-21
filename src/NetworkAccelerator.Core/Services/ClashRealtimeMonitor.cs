using System.Net.Http.Headers;
using NetworkAccelerator.Core.Models;

namespace NetworkAccelerator.Core.Services;

public sealed class ClashRealtimeMonitor : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly RecentConnectionBuffer _recent = new(100);
    private long _uploadRate;
    private long _downloadRate;
    private long _uploadTotal;
    private long _downloadTotal;
    private IReadOnlyList<ConnectionRow> _live = [];
    private IReadOnlyList<ProxyGroupInfo> _groups = [];
    private int _tcp;
    private int _udp;
    private bool _disposed;

    public event Action<RealtimeSnapshot>? Published;

    public async Task RunAsync(string secret, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var client = CreateClient(secret);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var traffic = ReadTrafficAsync(client, stop.Token);
        var poll = PollAsync(client, stop.Token);
        var publish = PublishAsync(stop.Token);
        try
        {
            await Task.WhenAny(traffic, poll, publish).ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(traffic, poll, publish).ConfigureAwait(false);
            }
            catch
            {
                // The caller reconnects until the monitor is cancelled.
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        Published = null;
        return ValueTask.CompletedTask;
    }

    private async Task ReadTrafficAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("traffic", HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) return;
            if (!ClashDashboardParser.TryParseTraffic(line, out var upload, out var download)) continue;
            lock (_gate)
            {
                _uploadRate = upload;
                _downloadRate = download;
            }
        }
    }

    private async Task PollAsync(HttpClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PollConnectionsAsync(client, cancellationToken).ConfigureAwait(false);
            await PollProxiesAsync(client, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PollConnectionsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(client, "connections", cancellationToken).ConfigureAwait(false);
        if (json is null) return;
        var snapshot = ClashDashboardParser.ParseConnections(json);
        _recent.Observe(snapshot.Connections);
        var tcp = snapshot.Connections.Count(row => row.ProtocolText.Equals("TCP", StringComparison.OrdinalIgnoreCase));
        var udp = snapshot.Connections.Count(row => row.ProtocolText.Equals("UDP", StringComparison.OrdinalIgnoreCase));
        lock (_gate)
        {
            _live = snapshot.Connections;
            _uploadTotal = snapshot.UploadTotal;
            _downloadTotal = snapshot.DownloadTotal;
            _tcp = tcp;
            _udp = udp;
        }
    }

    private async Task PollProxiesAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(client, "proxies", cancellationToken).ConfigureAwait(false);
        if (json is null) return;
        var groups = ClashDashboardParser.ParseProxyGroups(json);
        lock (_gate)
            _groups = groups;
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Publish();
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private void Publish()
    {
        var recent = _recent.Items;
        RealtimeSnapshot snapshot;
        lock (_gate)
        {
            var dns = _live.Concat(recent)
                .Where(row => row.IsDns)
                .GroupBy(row => row.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .Take(100)
                .ToArray();
            snapshot = new RealtimeSnapshot(
                _uploadRate,
                _downloadRate,
                _uploadTotal,
                _downloadTotal,
                _live,
                recent,
                dns,
                _tcp,
                _udp,
                _groups);
        }
        Published?.Invoke(snapshot);
    }

    private static async Task<string?> GetStringAsync(HttpClient client, string relative, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var response = await client.GetAsync(relative, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static HttpClient CreateClient(string secret)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(ClashApiClient.DefaultBaseAddress),
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }
}
