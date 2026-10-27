using System.Globalization;
using System.Text.Json;
using NetworkAccelerator.Core.Models;

namespace NetworkAccelerator.Core.Services;

public static class ClashDashboardParser
{
    public static bool TryParseTraffic(string? line, out long upload, out long download)
    {
        upload = 0;
        download = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!TryReadInt64(json.RootElement, "up", out upload)) return false;
            if (!TryReadInt64(json.RootElement, "down", out download)) return false;
            if (upload < 0) upload = 0;
            if (download < 0) download = 0;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static ConnectionsSnapshot ParseConnections(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ConnectionsSnapshot(0, 0, []);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new ConnectionsSnapshot(0, 0, []);
            TryReadInt64(root, "uploadTotal", out var uploadTotal);
            TryReadInt64(root, "downloadTotal", out var downloadTotal);
            var rows = new List<ConnectionRow>();
            if (root.TryGetProperty("connections", out var connections) && connections.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in connections.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && TryReadConnection(item, out var row))
                        rows.Add(row);
                }
            }
            return new ConnectionsSnapshot(Math.Max(0, uploadTotal), Math.Max(0, downloadTotal), rows);
        }
        catch (JsonException)
        {
            return new ConnectionsSnapshot(0, 0, []);
        }
    }

    public static IReadOnlyList<ProxyGroupInfo> ParseProxyGroups(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];
            if (!root.TryGetProperty("proxies", out var proxies) || proxies.ValueKind != JsonValueKind.Object)
                return [];
            var groups = new List<ProxyGroupInfo>();
            foreach (var property in proxies.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                var type = ReadString(property.Value, "type");
                if (!IsStrategyGroup(type)) continue;
                var name = ReadString(property.Value, "name");
                if (string.IsNullOrWhiteSpace(name)) name = property.Name;
                var now = ReadString(property.Value, "now");
                if (string.IsNullOrWhiteSpace(now)) now = "—";
                groups.Add(new ProxyGroupInfo(name, type, DescribeGroupType(type), now, Badge(name)));
            }
            return groups
                .OrderBy(group => group.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool TryReadConnection(JsonElement item, out ConnectionRow row)
    {
        row = null!;
        var metadata = item.TryGetProperty("metadata", out var metadataElement) && metadataElement.ValueKind == JsonValueKind.Object
            ? metadataElement
            : default;
        var hasMetadata = metadata.ValueKind == JsonValueKind.Object;
        var network = hasMetadata ? ReadString(metadata, "network") : string.Empty;
        var type = hasMetadata ? ReadString(metadata, "type") : string.Empty;
        var sourceIp = hasMetadata ? ReadString(metadata, "sourceIP") : string.Empty;
        var sourcePort = hasMetadata ? ReadString(metadata, "sourcePort") : string.Empty;
        var destinationIp = hasMetadata ? ReadString(metadata, "destinationIP") : string.Empty;
        var destinationPort = hasMetadata ? ReadString(metadata, "destinationPort") : string.Empty;
        var host = hasMetadata ? ReadString(metadata, "host") : string.Empty;
        var source = string.IsNullOrWhiteSpace(sourceIp) ? "?" : sourceIp;
        if (source != "?" && !string.IsNullOrWhiteSpace(sourcePort)) source = $"{source}:{sourcePort}";
        var targetHost = string.IsNullOrWhiteSpace(host) ? destinationIp : host;
        if (string.IsNullOrWhiteSpace(targetHost)) targetHost = "—";
        if (!string.IsNullOrWhiteSpace(destinationPort) && targetHost != "—") targetHost = $"{targetHost}:{destinationPort}";
        var outbound = "—";
        if (item.TryGetProperty("chains", out var chains) && chains.ValueKind == JsonValueKind.Array)
        {
            foreach (var chain in chains.EnumerateArray())
            {
                if (chain.ValueKind == JsonValueKind.String && chain.GetString() is { Length: > 0 } name)
                {
                    outbound = name;
                    break;
                }
            }
        }
        TryReadInt64(item, "upload", out var upload);
        TryReadInt64(item, "download", out var download);
        var start = DateTimeOffset.Now;
        if (item.TryGetProperty("start", out var startElement) &&
            startElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(startElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            start = parsed;
        }
        var id = ReadString(item, "id");
        if (string.IsNullOrWhiteSpace(id))
            id = $"{source}|{targetHost}|{start.ToUnixTimeMilliseconds()}";
        var protocol = string.IsNullOrWhiteSpace(network) ? type : network;
        if (string.IsNullOrWhiteSpace(protocol)) protocol = "—";
        else protocol = protocol.ToUpperInvariant();
        var isDns = destinationPort == "53" ||
                    network.Equals("dns", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("DNS", StringComparison.OrdinalIgnoreCase);
        row = new ConnectionRow(id, source, targetHost, protocol, outbound, Math.Max(0, download), Math.Max(0, upload), start, isDns);
        return true;
    }

    private static bool IsStrategyGroup(string type) =>
        type.Equals("Selector", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("URLTest", StringComparison.OrdinalIgnoreCase);

    private static string DescribeGroupType(string type) =>
        type.Equals("URLTest", StringComparison.OrdinalIgnoreCase) ? "测速" : "选择";

    private static string Badge(string name)
    {
        var character = name.Trim().FirstOrDefault(char.IsLetterOrDigit);
        if (character == default) return "•";
        return char.ToUpperInvariant(character).ToString();
    }

    private static bool TryReadInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number) return false;
        if (property.TryGetInt64(out value)) return true;
        if (property.TryGetDouble(out var number))
        {
            value = (long)number;
            return true;
        }
        return false;
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property)) return string.Empty;
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }
}
