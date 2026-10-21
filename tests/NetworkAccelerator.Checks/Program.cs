using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetworkAccelerator.Core.Models;
using NetworkAccelerator.Core.Services;

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
}

var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
TrafficFormatter.SplitRate(0, out var zeroValue, out var zeroUnit);
Check(zeroValue == "0" && zeroUnit == "B/s", "split zero rate");
TrafficFormatter.SplitRate(128 * 1024, out var kiloValue, out var kiloUnit);
Check(kiloValue == "128" && kiloUnit == "KB/s" && TrafficFormatter.FormatRate(128 * 1024) == "128 KB/s", "split kilobyte rate");
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
        Check(lan.RootElement.GetProperty("inbounds").GetArrayLength() == 1, "system proxy has no tun inbound");
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
        Check(inbounds[1].GetProperty("type").GetString() == "tun", "tun inbound present");
        Check(inbounds[1].GetProperty("interface_name").GetString() == SingBoxTun.InterfaceName, "tun interface name");
        var addresses = inbounds[1].GetProperty("address").EnumerateArray().Select(item => item.GetString()).ToArray();
        Check(addresses.SequenceEqual(SingBoxTun.Addresses), "tun addresses come from one constant");
        Check(addresses.All(address => address is not null && !address.Contains("172.19.0.", StringComparison.Ordinal)), "tun address is not 172.19.0.0/30");
        Check(local.RootElement.GetProperty("route").GetProperty("rules").GetArrayLength() == 3, "global mode route count");
    }

    var bothOff = new NetworkAcceleratorSettings
    {
        AllowLan = false,
        TunEnabled = false,
        SystemProxyEnabled = false,
        Mode = ProxyMode.Direct,
        ApiSecret = "secret",
        SelectedNode = "node-a"
    };
    using (var idle = JsonDocument.Parse(await File.ReadAllTextAsync(await service.WriteAsync(profile, bothOff))))
    {
        var inbound = idle.RootElement.GetProperty("inbounds").EnumerateArray().Single();
        Check(inbound.GetProperty("type").GetString() == "mixed", "both-off keeps the mixed inbound");
        Check(inbound.GetProperty("set_system_proxy").GetBoolean() == false, "both-off does not set the system proxy");
        Check(!idle.RootElement.GetProperty("inbounds").EnumerateArray().Any(item => item.GetProperty("type").GetString() == "tun"), "both-off has no tun inbound");
    }

    foreach (var (proxy, tun) in new[] { (true, false), (false, true), (true, true), (false, false) })
    {
        var toggled = new NetworkAcceleratorSettings
        {
            TunEnabled = tun,
            SystemProxyEnabled = proxy,
            Mode = ProxyMode.Rule,
            ApiSecret = "secret",
            SelectedNode = "node-a"
        };
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(await service.WriteAsync(profile, toggled)));
        var generated = document.RootElement.GetProperty("inbounds").EnumerateArray().ToArray();
        var hasTun = generated.Any(item => item.GetProperty("type").GetString() == "tun");
        Check(hasTun == tun, $"generated tun inbound matches toggle {proxy}/{tun}");
        Check(generated[0].GetProperty("set_system_proxy").GetBoolean() == TakeoverMode.UsesSystemProxy(proxy, tun),
            $"generated system proxy matches toggles {proxy}/{tun}");
    }

    var fresh = new NetworkAcceleratorSettings();
    Check(fresh.TunEnabled == false && fresh.SystemProxyEnabled, "new users default to system proxy");
    Check(SingBoxTun.IPv4Address == "172.31.250.1/30" && SingBoxTun.IPv6Address.StartsWith("fd", StringComparison.Ordinal), "tun constants");
    Check(!SingBoxTun.Addresses.Any(address => address.StartsWith("172.19.0.", StringComparison.Ordinal)), "constant avoids 172.19.0.0/30");

    var proxyOff = TakeoverMode.ApplySystemProxy(systemProxyEnabled: true, tunEnabled: false, enableSystemProxy: false);
    Check(!proxyOff.SystemProxyEnabled && !proxyOff.TunEnabled, "turning off system proxy does not enable TUN");
    var tunOn = TakeoverMode.ApplyTun(systemProxyEnabled: true, tunEnabled: false, enableTun: true);
    Check(!tunOn.SystemProxyEnabled && tunOn.TunEnabled, "enabling TUN disables system proxy");
    var tunOff = TakeoverMode.ApplyTun(systemProxyEnabled: false, tunEnabled: true, enableTun: false);
    Check(!tunOff.SystemProxyEnabled && !tunOff.TunEnabled, "disabling TUN does not enable system proxy");
    var stillTun = TakeoverMode.ApplySystemProxy(systemProxyEnabled: false, tunEnabled: true, enableSystemProxy: false);
    Check(!stillTun.SystemProxyEnabled && stillTun.TunEnabled, "turning system proxy off leaves TUN alone");
    var proxyOn = TakeoverMode.ApplySystemProxy(systemProxyEnabled: false, tunEnabled: true, enableSystemProxy: true);
    Check(proxyOn.SystemProxyEnabled && !proxyOn.TunEnabled, "turning system proxy on disables TUN");

    var store = new SettingsStore(Path.Combine(root, "settings"));
    await store.SaveAsync(new NetworkAcceleratorSettings { AllowLan = true, ApiSecret = "persisted" });
    var loaded = await store.LoadAsync();
    Check(loaded.AllowLan && loaded.ApiSecret == "persisted", "allow-LAN persists");
    Check(!loaded.TunEnabled && loaded.SystemProxyEnabled, "saved defaults keep system proxy on");
    await File.WriteAllTextAsync(Path.Combine(root, "settings", "settings.json"), """{"TunEnabled":true,"ApiSecret":"old"}""");
    var migratedTun = await store.LoadAsync();
    Check(migratedTun.AllowLan == false, "older settings keep LAN closed");
    Check(migratedTun.TunEnabled && migratedTun.SystemProxyEnabled == false, "missing system proxy migrates off when TUN was on");
    await File.WriteAllTextAsync(Path.Combine(root, "settings", "settings.json"), """{"TunEnabled":false,"ApiSecret":"legacy"}""");
    var migratedProxy = await store.LoadAsync();
    Check(!migratedProxy.TunEnabled && migratedProxy.SystemProxyEnabled, "missing system proxy migrates on when TUN was off");
    await File.WriteAllTextAsync(Path.Combine(root, "settings", "settings.json"), """{"TunEnabled":false,"SystemProxyEnabled":false}""");
    var explicitBothOff = await store.LoadAsync();
    Check(!explicitBothOff.TunEnabled && !explicitBothOff.SystemProxyEnabled, "explicit both-off is preserved");
    var missingStore = new SettingsStore(Path.Combine(root, "missing-settings"));
    var missing = await missingStore.LoadAsync();
    Check(!missing.TunEnabled && missing.SystemProxyEnabled, "missing settings file uses new defaults");
    Console.WriteLine("PASS allow-LAN config generation and settings");
}
finally
{
    Directory.Delete(root, recursive: true);
}

Check(LinuxTunAuthorization.OutputHasNetAdmin("/opt/sing-box cap_net_admin,cap_net_bind_service=ep"), "getcap output reports net admin");
Check(!LinuxTunAuthorization.OutputHasNetAdmin("/opt/sing-box"), "getcap output without caps");
Check(!LinuxTunAuthorization.OutputHasNetAdmin(null), "missing getcap output");
var capableXattr = new byte[20];
BinaryPrimitives.WriteUInt32LittleEndian(capableXattr, 0x02000000);
BinaryPrimitives.WriteUInt32LittleEndian(capableXattr.AsSpan(4), 1u << LinuxTunAuthorization.CapNetAdminBit);
var bindOnly = new byte[20];
BinaryPrimitives.WriteUInt32LittleEndian(bindOnly, 0x02000000);
BinaryPrimitives.WriteUInt32LittleEndian(bindOnly.AsSpan(4), 1u << 10);
Check(LinuxTunAuthorization.XattrHasNetAdmin(capableXattr), "xattr cap_net_admin bit");
Check(!LinuxTunAuthorization.XattrHasNetAdmin(bindOnly), "xattr without cap_net_admin");
Check(!LinuxTunAuthorization.XattrHasNetAdmin(new byte[] { 1, 2, 3 }), "short xattr");
Check(LinuxTunAuthorization.ClassifyPkexec(0, "", true) == LinuxPkexecOutcome.Success, "pkexec success");
Check(LinuxTunAuthorization.ClassifyPkexec(126, "Request dismissed", true) == LinuxPkexecOutcome.Cancelled, "pkexec dismissed");
Check(LinuxTunAuthorization.ClassifyPkexec(127, "Not authorized", true) == LinuxPkexecOutcome.Cancelled, "pkexec not authorized");
Check(LinuxTunAuthorization.ClassifyPkexec(127, "pkexec: command not found", true) == LinuxPkexecOutcome.Failed, "pkexec missing command");
Check(LinuxTunAuthorization.ClassifyPkexec(1, "", false) == LinuxPkexecOutcome.Failed, "pkexec did not start");
Check(LinuxCommandPaths.Resolve(["/run/wrappers/bin/pkexec"], "pkexec", path => path == "/run/wrappers/bin/pkexec", ["/usr/bin"]) == "/run/wrappers/bin/pkexec", "pkexec prefers NixOS wrapper");
Check(LinuxCommandPaths.Resolve(["/run/wrappers/bin/pkexec"], "pkexec", path => path == "/usr/bin/pkexec", ["/usr/bin"]) == "/usr/bin/pkexec", "pkexec falls back to PATH");
Check(LinuxCommandPaths.Resolve(["/run/current-system/sw/bin/setcap"], "setcap", path => path == "/usr/bin/setcap", ["/usr/bin"]) == "/usr/bin/setcap", "setcap falls back to PATH");
Check(LinuxCommandPaths.Resolve(["/missing/setcap"], "setcap", _ => false, ["/usr/bin"]) is null, "missing setcap");
Check(LinuxMountOptions.IsNoSuid("/home/user/.local/share/AsterDock/AppData/network-accelerator/bin/sing-box",
    ["overlay / overlay rw,nosuid 0 0", "/dev/nvme0n1p2 /home ext4 rw,nosuid,nodev 0 0"]), "home nosuid");
Check(!LinuxMountOptions.IsNoSuid("/home/user/.local/share/app",
    ["overlay / overlay rw 0 0", "/dev/nvme0n1p2 /home ext4 rw,nodev 0 0"]), "home honors file capabilities");

static LinuxTunFacts Facts(bool copy, bool copyCap, bool hash, bool sourceCap, bool setcap, bool pkexec, bool honors = true) =>
    new(copy, copyCap, hash, sourceCap, setcap, pkexec, honors);
Check(LinuxTunAuthorization.Decide(Facts(true, true, true, false, true, true)) == LinuxTunDecision.UseCapableCopy, "reuse capable copy");
Check(LinuxTunAuthorization.Decide(Facts(false, false, false, true, true, true)) == LinuxTunDecision.UseSource, "source already capable");
Check(LinuxTunAuthorization.Decide(Facts(true, true, false, false, true, true)) == LinuxTunDecision.CopyAndSetcap, "hash change reauthorizes");
Check(LinuxTunAuthorization.Decide(Facts(false, false, false, false, true, true)) == LinuxTunDecision.CopyAndSetcap, "copy then setcap");
Check(LinuxTunAuthorization.Decide(Facts(false, false, false, false, false, true)) == LinuxTunDecision.LaunchWithPkexec, "no setcap launches via pkexec");
Check(LinuxTunAuthorization.Decide(Facts(false, false, false, false, true, true, honors: false)) == LinuxTunDecision.LaunchWithPkexec, "nosuid filesystem uses pkexec");
Check(LinuxTunAuthorization.Decide(Facts(false, false, false, false, false, false)) == LinuxTunDecision.PkexecMissing, "no pkexec cannot authorize");

var authRoot = Path.Combine(Path.GetTempPath(), "na-tun-auth-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(authRoot);
try
{
    var source = Path.Combine(authRoot, "sing-box-source");
    await File.WriteAllTextAsync(source, "sing-box-v1");
    var capable = new HashSet<string>(StringComparer.Ordinal);
    var commands = new FakeCommands();
    var runner = new FakeRunner(request =>
    {
        if (request.FileName == commands.GetcapPath)
        {
            var target = request.Arguments[0];
            var text = capable.Contains(target) ? $"{target} cap_net_admin,cap_net_bind_service=ep\n" : target + "\n";
            return new ProcessResult(true, 0, text, string.Empty);
        }

        if (request.Arguments.Count > 0 && request.Arguments[0] == commands.SetcapPath)
        {
            capable.Add(request.Arguments[^1]);
            return new ProcessResult(true, 0, string.Empty, string.Empty);
        }

        return new ProcessResult(false, -1, string.Empty, "unexpected command");
    });
    var scopes = 0;
    var privilege = new LinuxTunPrivilege(Path.Combine(authRoot, "data"), runner, commands, new FakeXattr(), new HonoringFilesystem())
    {
        BeginAuthorization = () =>
        {
            scopes++;
            return new AuthScope();
        }
    };
    var first = await privilege.PrepareAsync(source);
    Check(first.Executable == privilege.CapableCopyPath && !first.ViaPkexec, "first TUN run uses the capable copy");
    Check(scopes == 1, "authorization shows one busy scope");
    Check(runner.Calls.Any(call => call.FileName == commands.PkexecPath && call.Arguments[0] == commands.SetcapPath
        && call.Arguments[1] == LinuxTunAuthorization.CapabilityArguments
        && call.Arguments[2] == privilege.CapableCopyPath), "pkexec setcap arguments");
    Check(File.Exists(privilege.HashSidecarPath), "source hash recorded after setcap");
    var callsAfterFirst = runner.Calls.Count;
    var second = await privilege.PrepareAsync(source);
    Check(second.Executable == privilege.CapableCopyPath && !second.ViaPkexec, "later TUN run reuses the copy");
    Check(runner.Calls.Skip(callsAfterFirst).All(call => call.FileName != commands.PkexecPath), "later run does not prompt");
    Check(scopes == 1, "later run has no authorization scope");

    await File.WriteAllTextAsync(source, "sing-box-v2");
    var recopied = await privilege.PrepareAsync(source);
    Check(recopied.Executable == privilege.CapableCopyPath, "changed binary is copied again");
    Check(scopes == 2, "changed binary asks for authorization again");
    Check((await File.ReadAllTextAsync(privilege.CapableCopyPath)) == "sing-box-v2", "copy matches the new binary");

    commands.SetcapPath = null;
    capable.Clear();
    File.Delete(privilege.HashSidecarPath);
    var pkexecLaunch = await privilege.PrepareAsync(source);
    Check(pkexecLaunch.ViaPkexec && pkexecLaunch.Executable == source && pkexecLaunch.PkexecPath == commands.PkexecPath, "missing setcap launches this run via pkexec");
    Check(!File.Exists(privilege.HashSidecarPath), "pkexec fallback does not record a capability hash");

    commands.SetcapPath = "/usr/bin/setcap";
    runner.Handle = request => request.FileName == commands.GetcapPath
        ? new ProcessResult(true, 0, request.Arguments[0] + "\n", string.Empty)
        : new ProcessResult(true, 126, string.Empty, "Request dismissed");
    File.Delete(privilege.CapableCopyPath);
    var cancelled = false;
    try
    {
        await privilege.PrepareAsync(source);
    }
    catch (TunAuthorizationException exception)
    {
        cancelled = exception.Cancelled && exception.Message == TunAuthorizationException.CancelledMessage;
    }
    Check(cancelled, "dismissed pkexec cancels TUN authorization");
    Check(!File.Exists(privilege.HashSidecarPath), "cancelled authorization does not keep a hash");

    commands.PkexecPath = null;
    var missingPkexec = false;
    try
    {
        await privilege.PrepareAsync(source);
    }
    catch (TunAuthorizationException exception)
    {
        missingPkexec = !exception.Cancelled && exception.Message == TunAuthorizationException.PkexecMissingMessage;
    }
    Check(missingPkexec, "missing pkexec is a TUN authorization error");

    var xattrReader = new FakeXattr
    {
        Read = _ => capableXattr
    };
    commands.GetcapPath = null;
    commands.PkexecPath = "/usr/bin/pkexec";
    var fromXattr = new LinuxTunPrivilege(Path.Combine(authRoot, "xattr"), runner, commands, xattrReader, new HonoringFilesystem());
    var direct = await fromXattr.PrepareAsync(source);
    Check(direct.Executable == source && !direct.ViaPkexec, "security.capability xattr authorizes the source binary");
    Console.WriteLine("PASS linux tun authorization");
}
finally
{
    Directory.Delete(authRoot, recursive: true);
}

ModuleInteractionChecks.RunMotionClockChecks(Check);
ModuleInteractionChecks.RunInteractionChecks(Check);
Console.WriteLine($"PASS network accelerator dashboard checks ({passed})");

sealed class FakeCommands : ILinuxCommandResolver
{
    public string? PkexecPath { get; set; } = "/usr/bin/pkexec";
    public string? SetcapPath { get; set; } = "/usr/bin/setcap";
    public string? GetcapPath { get; set; } = "/usr/bin/getcap";
    public string? FindPkexec() => PkexecPath;
    public string? FindSetcap() => SetcapPath;
    public string? FindGetcap() => GetcapPath;
}

sealed class FakeRunner(Func<ProcessRequest, ProcessResult> handle) : IProcessRunner
{
    public List<ProcessRequest> Calls { get; } = [];
    public Func<ProcessRequest, ProcessResult> Handle { get; set; } = handle;
    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add(request);
        return Task.FromResult(Handle(request));
    }
}

sealed class FakeXattr : ICapabilityXattrReader
{
    public Func<string, byte[]?> Read { get; set; } = _ => null;
    public byte[]? ReadSecurityCapability(string path) => Read(path);
}

sealed class HonoringFilesystem : IFileCapabilityPolicy
{
    public bool HonorsFileCapabilities(string path) => true;
}

sealed class AuthScope : IDisposable
{
    public void Dispose()
    {
    }
}
