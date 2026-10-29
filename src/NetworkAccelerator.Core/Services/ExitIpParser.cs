using System.Text.Json;
using NetworkAccelerator.Core.Models;

namespace NetworkAccelerator.Core.Services;

public static class ExitIpParser
{
    public static bool TryParseIpApi(string? json, out ExitIpInfo? info)
    {
        info = null;
        if (!TryOpen(json, out var document)) return false;
        using (document)
        {
            var element = document.RootElement;
            if (element.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String &&
                !string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase))
                return false;
            var ip = Read(element, "query");
            if (string.IsNullOrWhiteSpace(ip)) return false;
            var hosting = ReadBool(element, "hosting");
            var mobile = ReadBool(element, "mobile");
            var region = FirstNonEmpty(Read(element, "regionName"), Read(element, "city"));
            info = new ExitIpInfo(
                ip,
                Country(Read(element, "countryCode")),
                FirstNonEmpty(Read(element, "org"), Read(element, "isp"), Read(element, "as")) ?? "—",
                string.IsNullOrWhiteSpace(region) ? "—" : region,
                mobile ? "移动" : hosting ? "机房" : "住宅");
            return true;
        }
    }

    public static bool TryParseIpInfo(string? json, out ExitIpInfo? info)
    {
        info = null;
        if (!TryOpen(json, out var document)) return false;
        using (document)
        {
            var element = document.RootElement;
            var ip = Read(element, "ip");
            if (string.IsNullOrWhiteSpace(ip)) return false;
            var org = Read(element, "org");
            info = new ExitIpInfo(
                ip,
                Country(Read(element, "country")),
                string.IsNullOrWhiteSpace(org) ? "—" : org,
                FirstNonEmpty(Read(element, "region"), Read(element, "city")) ?? "—",
                "—");
            return true;
        }
    }

    private static bool TryOpen(string? json, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                parsed.Dispose();
                return false;
            }
            document = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Country(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        var code = value.Trim().ToUpperInvariant();
        return code.Length > 3 ? code[..3] : code;
    }

    private static string? FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string Read(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property)) return string.Empty;
        return property.ValueKind == JsonValueKind.String ? property.GetString()?.Trim() ?? string.Empty : string.Empty;
    }

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) &&
        ((property.ValueKind == JsonValueKind.True) ||
         (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out var parsed) && parsed));
}
