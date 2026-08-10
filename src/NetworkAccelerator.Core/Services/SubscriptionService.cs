using NetworkAccelerator.Core.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetworkAccelerator.Core.Services;

public sealed class SubscriptionService : IDisposable
{
    private const int MaximumSubscriptionBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions CacheOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> NonNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "direct", "block", "dns", "selector", "urltest"
    };

    private readonly HttpClient _httpClient = new(new HttpClientHandler { MaxAutomaticRedirections = 5 })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private readonly string _cachePath;
    private readonly string _cacheDirectory;

    public SubscriptionService(string dataDirectory)
    {
        _cachePath = Path.Combine(dataDirectory, "subscription.json");
        _cacheDirectory = Path.Combine(dataDirectory, "subscriptions");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<SubscriptionProfile?> LoadCachedAsync(
        string? configurationId = null,
        CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(configurationId) ? _cachePath : GetCachePath(configurationId);
        if (!File.Exists(path) && !string.IsNullOrWhiteSpace(configurationId) && File.Exists(_cachePath))
            path = _cachePath;
        if (!File.Exists(path)) return null;
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(text);
    }

    public async Task<SubscriptionProfile> UpdateAsync(
        string source,
        string? configurationId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("请先配置订阅地址或本地 JSON 文件");
        string content;
        if (Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            content = await DownloadAsync(uri, cancellationToken).ConfigureAwait(false);
        else
        {
            var sourcePath = Path.GetFullPath(source.Trim());
            if (new FileInfo(sourcePath).Length > MaximumSubscriptionBytes)
                throw new InvalidDataException("订阅内容超过 8 MB 限制");
            content = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }

        if (Encoding.UTF8.GetByteCount(content) > MaximumSubscriptionBytes)
            throw new InvalidDataException("订阅内容超过 8 MB 限制");
        var profile = Parse(content);
        if (profile.Nodes.Count == 0) throw new InvalidDataException("订阅中没有可用的 sing-box 出站节点");
        var cachePath = string.IsNullOrWhiteSpace(configurationId) ? _cachePath : GetCachePath(configurationId);
        await File.WriteAllTextAsync(cachePath, content, cancellationToken).ConfigureAwait(false);
        return profile with { UpdatedAt = DateTimeOffset.Now };
    }

    public static SubscriptionProfile Parse(string content)
    {
        var root = JsonNode.Parse(content)?.AsObject() ?? throw new InvalidDataException("订阅不是有效的 JSON 对象");
        var nodeArray = root["nodes"] as JsonArray ?? root["outbounds"] as JsonArray ?? [];
        var nodes = new List<ProxyNode>();
        var usedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in nodeArray)
        {
            if (value is not JsonObject source) continue;
            var type = source["type"]?.GetValue<string>()?.Trim() ?? string.Empty;
            var tag = source["tag"]?.GetValue<string>()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(tag) || NonNodeTypes.Contains(type)) continue;
            if (!usedTags.Add(tag)) continue;
            var server = source["server"]?.GetValue<string>()?.Trim() ?? string.Empty;
            var port = source["server_port"]?.GetValue<int?>() ?? 0;
            nodes.Add(new ProxyNode(tag, type, server, port, (JsonObject)source.DeepClone()));
        }

        var metadata = root["profile"] as JsonObject ?? root["metadata"] as JsonObject;
        var name = metadata?["name"]?.GetValue<string>()
                   ?? root["name"]?.GetValue<string>()
                   ?? "我的订阅";
        var used = ReadLong(metadata, "used_bytes") ?? ReadLong(metadata, "traffic_used");
        var total = ReadLong(metadata, "total_bytes") ?? ReadLong(metadata, "traffic_total");
        var expires = ReadDate(metadata, "expires_at") ?? ReadDate(metadata, "expire");
        return new SubscriptionProfile(name, nodes, DateTimeOffset.Now, used, total, expires);
    }

    public void Dispose() => _httpClient.Dispose();

    private string GetCachePath(string configurationId)
    {
        var safeId = string.Concat(configurationId.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        if (string.IsNullOrWhiteSpace(safeId))
            safeId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(configurationId)))[..24];
        return Path.Combine(_cacheDirectory, safeId + ".json");
    }

    private async Task<string> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumSubscriptionBytes)
            throw new InvalidDataException("订阅内容超过 8 MB 限制");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > MaximumSubscriptionBytes)
                throw new InvalidDataException("订阅内容超过 8 MB 限制");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static long? ReadLong(JsonObject? source, string name)
    {
        if (source?[name] is not JsonNode node) return null;
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<long>(out var number)) return number;
        return long.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static DateTimeOffset? ReadDate(JsonObject? value, string name)
    {
        if (value?[name] is not JsonNode node) return null;
        if (DateTimeOffset.TryParse(node.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)) return date;
        return long.TryParse(node.ToString(), out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }
}
