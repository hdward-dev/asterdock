using System.Text.Json;
using System.Text.Json.Nodes;
using NetworkAccelerator.Core.Models;
using NetworkAccelerator.Core.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
Check(TrafficFormatter.FormatRate(0) == "0 B/s", "zero rate");
Check(TrafficFormatter.FormatRate(1023) == "1023 B/s", "byte rate");
Check(TrafficFormatter.FormatRate(1024) == "1 KB/s", "kilobyte boundary");
Check(TrafficFormatter.FormatRate(1536) == "1.5 KB/s", "fractional kilobytes");
Check(TrafficFormatter.FormatRate(1024 * 1024) == "1 MB/s", "megabyte boundary");
Check(TrafficFormatter.FormatRate(1024L * 1024 * 1024) == "1 GB/s", "gigabyte boundary");
Check(TrafficFormatter.FormatRate(-20) == "0 B/s", "negative rate");
Check(TrafficFormatter.FormatTotal(33) == "33 B", "byte total");
Check(TrafficFormatter.FormatTotal(2048) == "2 KB", "kilobyte total");
Check(TrafficFormatter.FormatDuration(TimeSpan.FromSeconds(12)) == "12 s", "seconds");
Check(TrafficFormatter.FormatDuration(TimeSpan.FromSeconds(90)) == "1 m 30 s", "minutes");
Check(TrafficFormatter.FormatDuration(TimeSpan.FromSeconds(3700)) == "1 h 1 m", "hours");
Check(TrafficFormatter.FormatRelativeUpdate(now.AddHours(-6), now) == "更新于 6 小时前", "relative hours");
Check(TrafficFormatter.FormatRelativeUpdate(now.AddSeconds(-20), now) == "刚刚更新", "just updated");
Check(TrafficFormatter.FormatRelativeUpdate(now.AddMinutes(-90), now) == "更新于 1 小时前", "relative one hour");
Check(TrafficFormatter.DescribeSource("https://example.com/sub") == "远程", "remote source");
Check(TrafficFormatter.DescribeSource("/tmp/subscription.json") == "本地", "local source");
Check(TrafficFormatter.DescribeSource("  ") == "—", "empty source");
Console.WriteLine("PASS traffic formatter and subscription labels");

var series = new TrafficSeriesBuffer();
series.Push(100);
series.Push(200);
Check(series.Count == 2 && series[0] == 100 && series[1] == 200, "series append");
for (var i = 0; i < TrafficSeriesBuffer.Capacity; i++) series.Push(i);
Check(series.Count == TrafficSeriesBuffer.Capacity && series[0] == 0 && series[^1] == TrafficSeriesBuffer.Capacity - 1, "series capacity");
series.SeedWave(1000);
Check(series.Count == TrafficSeriesBuffer.Capacity && series[0] > 400 && series[16] > series[0] && Math.Abs(series[0] - series[^1]) < 0.01, "series wave");
series.Clear();
Check(series.Count == 0, "series clear");
Console.WriteLine("PASS traffic series buffer");

Check(ClashDashboardParser.TryParseTraffic("{\"up\":1536,\"down\":1048576}", out var upload, out var download) && upload == 1536 && download == 1048576, "traffic line");
Check(!ClashDashboardParser.TryParseTraffic("not-json", out _, out _), "invalid traffic");

const string connectionsJson = """
{
  "uploadTotal": 33,
  "downloadTotal": 128,
  "connections": [
    {
      "id": "dns-1",
      "metadata": {
        "network": "udp",
        "type": "DNS",
        "sourceIP": "",
        "sourcePort": "",
        "destinationIP": "1.1.1.1",
        "destinationPort": "53",
        "host": ""
      },
      "upload": 33,
      "download": 0,
      "start": "2026-10-20T11:59:48Z",
      "chains": ["tuic-tunnel-us", "节点选择"]
    },
    {
      "id": "web-1",
      "metadata": {
        "network": "tcp",
        "type": "HTTP",
        "sourceIP": "127.0.0.1",
        "sourcePort": 52310,
        "destinationIP": "8.8.8.8",
        "destinationPort": 443,
        "host": "example.com"
      },
      "upload": 10,
      "download": 20,
      "start": "2026-10-20T11:59:00Z",
      "chains": ["节点选择"]
    }
  ]
}
""";
var connections = ClashDashboardParser.ParseConnections(connectionsJson);
Check(connections.UploadTotal == 33 && connections.DownloadTotal == 128, "connection totals");
Check(connections.Connections.Count == 2, "connection count");
var dns = connections.Connections[0];
Check(dns.IsDns && dns.SourceText == "?" && dns.TargetText == "1.1.1.1:53" && dns.ProtocolText == "UDP" && dns.OutboundText == "tuic-tunnel-us", "dns row");
var web = connections.Connections[1];
Check(!web.IsDns && web.SourceText == "127.0.0.1:52310" && web.TargetText == "example.com:443" && web.ProtocolText == "TCP" && web.Download == 20, "tcp row");
Check(ClashDashboardParser.ParseConnections("{").Connections.Count == 0, "invalid connections");

const string proxiesJson = """
{
  "proxies": {
    "DIRECT": { "name": "DIRECT", "type": "Direct", "now": "" },
    "节点选择": { "name": "节点选择", "type": "Selector", "now": "香港 01", "all": ["自动选择", "香港 01"] },
    "香港 01": { "name": "香港 01", "type": "Shadowsocks", "now": "" },
    "自动选择": { "type": "URLTest", "now": "香港 01", "all": ["香港 01"] }
  }
}
""";
var groups = ClashDashboardParser.ParseProxyGroups(proxiesJson);
Check(groups.Count == 2, "only selector and urltest");
Check(groups[0].Name == "节点选择" && groups[0].TypeText == "选择" && groups[0].Now == "香港 01" && groups[0].Badge == "节", "selector group");
Check(groups[1].Name == "自动选择" && groups[1].TypeText == "测速" && groups[1].Now == "香港 01", "urltest group");
Check(ClashDashboardParser.ParseProxyGroups("[]").Count == 0, "invalid proxies");

var buffer = new RecentConnectionBuffer(2);
buffer.Observe(connections.Connections);
Check(buffer.Items.Count == 0, "open connections are not recent");
buffer.Observe([web]);
Check(buffer.Items.Count == 1 && buffer.Items[0].Id == "dns-1", "closed connection enters the ring");
buffer.Observe([]);
Check(buffer.Items.Count == 2 && buffer.Items[0].Id == "web-1", "newest closed connection is first");
var third = dns with { Id = "third" };
buffer.Observe([third]);
buffer.Observe([]);
Check(buffer.Items.Count == 2 && buffer.Items[0].Id == "third" && buffer.Items.All(row => row.Id != "dns-1"), "ring drops the oldest id");
Console.WriteLine("PASS connections, proxies and recent-request parsing");

const string ipApi = """
{"status":"success","countryCode":"us","regionName":"加利福尼亚","city":"洛杉矶","isp":"EGI","org":"EGIHosting","as":"AS12345 EGIHosting","query":"142.252.100.8","hosting":true,"mobile":false}
""";
Check(ExitIpParser.TryParseIpApi(ipApi, out var exit) && exit is { Ip: "142.252.100.8", CountryCode: "US", Organization: "EGIHosting", Region: "加利福尼亚", NetworkKind: "机房" }, "ip-api");
Check(ExitIpParser.TryParseIpApi("""{"status":"fail","message":"private"}""", out _) == false, "ip-api failure");
const string ipInfo = """{"ip":"1.2.3.4","city":"Tokyo","region":"Tokyo","country":"JP","org":"AS1 Example"}""";
Check(ExitIpParser.TryParseIpInfo(ipInfo, out var fallback) && fallback is { Ip: "1.2.3.4", CountryCode: "JP", Organization: "AS1 Example", NetworkKind: "—" }, "ipinfo");
Console.WriteLine("PASS exit IP parsers");

var root = Path.Combine(Path.GetTempPath(), "na-dashboard-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var outbound = new JsonObject
    {
        ["type"] = "socks",
        ["tag"] = "node-a",
        ["server"] = "127.0.0.1",
        ["server_port"] = 1
    };
    var profile = new SubscriptionProfile(
        "测试订阅",
        [new ProxyNode("node-a", "socks", "127.0.0.1", 1, outbound)],
        DateTimeOffset.UnixEpoch);
    var service = new SingBoxConfigurationService(root);
    var lanSettings = new NetworkAcceleratorSettings
    {
        AllowLan = true,
        TunEnabled = false,
        Mode = ProxyMode.Rule,
        ApiSecret = "secret",
        SelectedNode = "node-a"
    };
    var lanPath = await service.WriteAsync(profile, lanSettings);
    using (var lan = JsonDocument.Parse(await File.ReadAllTextAsync(lanPath)))
    {
        var inbound = lan.RootElement.GetProperty("inbounds").EnumerateArray().Single();
        Check(inbound.GetProperty("listen").GetString() == "0.0.0.0", "allow-LAN listens on every interface");
        Check(inbound.GetProperty("listen").GetString() == SingBoxConfigurationService.MixedListenAddress(true), "listen helper");
        Check(inbound.GetProperty("listen_port").GetInt32() == SingBoxConfigurationService.MixedProxyPort, "mixed port");
        Check(inbound.GetProperty("set_system_proxy").GetBoolean(), "system proxy stays available");
        Check(lan.RootElement.GetProperty("route").GetProperty("rules").GetArrayLength() == 4, "rule mode route count");
        Check(SingBoxConfigurationService.CountGeneratedRouteRules(ProxyMode.Rule) == 4, "rule count helper");
    }

    var localSettings = new NetworkAcceleratorSettings
    {
        AllowLan = false,
        TunEnabled = true,
        Mode = ProxyMode.Global,
        ApiSecret = "secret",
        SelectedNode = "node-a"
    };
    var localPath = await service.WriteAsync(profile, localSettings);
    using (var local = JsonDocument.Parse(await File.ReadAllTextAsync(localPath)))
    {
        var inbounds = local.RootElement.GetProperty("inbounds").EnumerateArray().ToArray();
        Check(inbounds.Length == 2 && inbounds[0].GetProperty("listen").GetString() == "127.0.0.1", "loopback listen");
        Check(inbounds[0].GetProperty("set_system_proxy").GetBoolean() == false, "TUN does not set the system proxy");
        Check(local.RootElement.GetProperty("route").GetProperty("rules").GetArrayLength() == 3, "global mode route count");
    }

    var store = new SettingsStore(Path.Combine(root, "settings"));
    await store.SaveAsync(new NetworkAcceleratorSettings { AllowLan = true, ApiSecret = "persisted" });
    var loaded = await store.LoadAsync();
    Check(loaded.AllowLan && loaded.ApiSecret == "persisted", "allow-LAN persists");
    await File.WriteAllTextAsync(Path.Combine(root, "settings", "settings.json"), """{"TunEnabled":true,"ApiSecret":"old"}""");
    Check((await store.LoadAsync()).AllowLan == false, "older settings keep LAN closed");
    Console.WriteLine("PASS allow-LAN config generation and settings");
}
finally
{
    Directory.Delete(root, recursive: true);
}

Console.WriteLine("PASS network accelerator dashboard checks");
