using System.Net.Http.Json;
using System.Text.Json;

namespace NetworkAccelerator.Core.Services;

public sealed class ClashApiClient : IDisposable
{
    public const string DefaultBaseAddress = "http://127.0.0.1:19090/";
    public const string SelectorTag = "节点选择";

    private readonly HttpClient _client;

    public ClashApiClient(string secret)
    {
        _client = new HttpClient { BaseAddress = new Uri(DefaultBaseAddress), Timeout = TimeSpan.FromSeconds(6) };
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
    }

    public async Task SelectNodeAsync(string nodeTag, CancellationToken cancellationToken = default)
    {
        using var response = await _client.PutAsJsonAsync(
            $"proxies/{Uri.EscapeDataString(SelectorTag)}", new { name = nodeTag }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public Task<int?> MeasureDelayAsync(string nodeTag, CancellationToken cancellationToken = default) =>
        MeasureDelayAsync(nodeTag, "https://www.gstatic.com/generate_204", cancellationToken);

    public async Task<int?> MeasureDelayAsync(string nodeTag, string testUrl, CancellationToken cancellationToken = default)
    {
        var uri = $"proxies/{Uri.EscapeDataString(nodeTag)}/delay?url={Uri.EscapeDataString(testUrl)}&timeout=5000";
        using var response = await _client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return json.RootElement.TryGetProperty("delay", out var delay) ? delay.GetInt32() : null;
    }

    public void Dispose() => _client.Dispose();
}
