using System.Net;
using System.Text;
using NetworkAccelerator.Core.Models;

namespace NetworkAccelerator.Core.Services;

public static class ExitIpLookup
{
    private const int MaximumResponseBytes = 64 * 1024;

    public static async Task<ExitIpInfo?> LookupAsync(
        bool throughProxy,
        int proxyPort,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(throughProxy, proxyPort);
        var ipApi = await TryDownloadAsync(
            client,
            "http://ip-api.com/json?lang=zh-CN&fields=status,message,countryCode,regionName,city,isp,org,as,query,hosting,mobile",
            cancellationToken).ConfigureAwait(false);
        if (ipApi is not null && ExitIpParser.TryParseIpApi(ipApi, out var info)) return info;

        var ipInfo = await TryDownloadAsync(client, "https://ipinfo.io/json", cancellationToken).ConfigureAwait(false);
        if (ipInfo is not null && ExitIpParser.TryParseIpInfo(ipInfo, out info)) return info;
        return null;
    }

    private static HttpClient CreateClient(bool throughProxy, int proxyPort)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = throughProxy,
            Proxy = throughProxy ? new WebProxy($"http://127.0.0.1:{proxyPort}") : null,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            AutomaticDecompression = DecompressionMethods.All
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AsterDock-NetworkAccelerator/1.2");
        return client;
    }

    private static async Task<string?> TryDownloadAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[2048];
            var builder = new StringBuilder();
            while (builder.Length < MaximumResponseBytes)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                builder.Append(buffer, 0, read);
            }
            return builder.ToString();
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
}
