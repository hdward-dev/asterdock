using NetworkAccelerator.Core.Models;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetworkAccelerator.Core.Services;

public sealed class SingBoxConfigurationService
{
    public const int MixedProxyPort = 2080;
    private readonly string _dataDirectory;

    public SingBoxConfigurationService(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
    }

    public async Task<string> WriteAsync(
        SubscriptionProfile profile,
        NetworkAcceleratorSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (profile.Nodes.Count == 0) throw new InvalidOperationException("没有可用节点");
        var selected = profile.Nodes.Any(node => node.Tag == settings.SelectedNode)
            ? settings.SelectedNode
            : profile.Nodes[0].Tag;
        settings.SelectedNode = selected;

        var nodeTags = new JsonArray(profile.Nodes.Select(node => JsonValue.Create(node.Tag)).ToArray());
        var selectorTargets = new JsonArray(JsonValue.Create("自动选择"));
        foreach (var tag in profile.Nodes.Select(node => node.Tag)) selectorTargets.Add(tag);

        var outbounds = new JsonArray();
        foreach (var node in profile.Nodes) outbounds.Add(node.Outbound.DeepClone());
        outbounds.Add(new JsonObject
        {
            ["type"] = "urltest",
            ["tag"] = "自动选择",
            ["outbounds"] = nodeTags,
            ["url"] = "https://www.gstatic.com/generate_204",
            ["interval"] = "3m",
            ["tolerance"] = 50,
            ["idle_timeout"] = "30m",
            ["interrupt_exist_connections"] = settings.InterruptExistingConnections
        });
        outbounds.Add(new JsonObject
        {
            ["type"] = "selector",
            ["tag"] = "节点选择",
            ["outbounds"] = selectorTargets,
            ["default"] = selected,
            ["interrupt_exist_connections"] = settings.InterruptExistingConnections
        });
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "直连" });
        outbounds.Add(new JsonObject { ["type"] = "block", ["tag"] = "拦截" });

        var finalOutbound = settings.Mode == ProxyMode.Direct ? "直连" : "节点选择";
        var proxyDnsServer = new JsonObject
        {
            ["type"] = "https",
            ["tag"] = "代理 DNS",
            ["server"] = "1.1.1.1",
            ["server_port"] = 443,
            ["path"] = "/dns-query",
            ["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = "cloudflare-dns.com"
            }
        };
        if (settings.Mode != ProxyMode.Direct)
            proxyDnsServer["detour"] = finalOutbound;

        var inbounds = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "本地代理",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = MixedProxyPort,
                ["set_system_proxy"] = !settings.TunEnabled
            }
        };
        if (settings.TunEnabled)
        {
            inbounds.Add(new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "TUN",
                ["interface_name"] = "apphub-tun",
                ["address"] = new JsonArray("172.19.0.1/30"),
                ["mtu"] = 9000,
                ["auto_route"] = true,
                ["strict_route"] = true,
                ["stack"] = "system"
            });
        }

        var rules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            new JsonObject
            {
                ["ip_cidr"] = new JsonArray(
                    "10.0.0.0/8",
                    "100.64.0.0/10",
                    "127.0.0.0/8",
                    "169.254.0.0/16",
                    "172.16.0.0/12",
                    "192.168.0.0/16",
                    "::1/128",
                    "fc00::/7",
                    "fe80::/10"),
                ["action"] = "route",
                ["outbound"] = "直连"
            }
        };
        var ruleSets = new JsonArray();
        var dnsRules = new JsonArray
        {
            new JsonObject
            {
                ["domain_suffix"] = new JsonArray(".lan", ".local", ".home.arpa"),
                ["action"] = "route",
                ["server"] = "直连 DNS"
            }
        };
        if (settings.Mode == ProxyMode.Rule)
        {
            rules.Add(new JsonObject
            {
                ["rule_set"] = new JsonArray("geosite-cn", "geoip-cn"),
                ["action"] = "route",
                ["outbound"] = "直连"
            });
            dnsRules.Add(new JsonObject
            {
                ["rule_set"] = new JsonArray("geosite-cn"),
                ["action"] = "route",
                ["server"] = "直连 DNS"
            });
            ruleSets.Add(CreateRemoteRuleSet("geosite-cn", "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-cn.srs"));
            ruleSets.Add(CreateRemoteRuleSet("geoip-cn", "https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-cn.srs"));
        }

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "udp",
                        ["tag"] = "直连 DNS",
                        ["server"] = "223.5.5.5",
                        ["server_port"] = 53
                    },
                    proxyDnsServer),
                ["rules"] = dnsRules,
                ["final"] = settings.Mode == ProxyMode.Direct ? "直连 DNS" : "代理 DNS",
                ["strategy"] = "prefer_ipv4"
            },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["rules"] = rules,
                ["rule_set"] = ruleSets,
                ["final"] = finalOutbound,
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = new JsonObject
                {
                    ["server"] = "直连 DNS",
                    ["strategy"] = "prefer_ipv4"
                }
            },
            ["experimental"] = new JsonObject
            {
                ["cache_file"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["path"] = Path.Combine(_dataDirectory, "cache.db")
                },
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = "127.0.0.1:19090",
                    ["secret"] = settings.ApiSecret,
                    ["default_mode"] = settings.Mode switch
                    {
                        ProxyMode.Global => "Global",
                        ProxyMode.Direct => "Direct",
                        _ => "Rule"
                    }
                }
            }
        };

        var path = Path.Combine(_dataDirectory, "config.json");
        await File.WriteAllTextAsync(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken)
            .ConfigureAwait(false);
        return path;
    }

    private static JsonObject CreateRemoteRuleSet(string tag, string url) => new()
    {
        ["type"] = "remote",
        ["tag"] = tag,
        ["format"] = "binary",
        ["url"] = url,
        ["download_detour"] = "直连",
        ["update_interval"] = "1d"
    };
}
