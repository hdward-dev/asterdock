using NetworkAccelerator.Core.Models;

namespace NetworkAccelerator.Module.ViewModels;

public sealed class DashboardPreview
{
    public bool IsConnected { get; init; }
    public ProxyMode Mode { get; init; } = ProxyMode.Rule;
    public bool TunEnabled { get; init; } = true;
    public bool AllowLan { get; init; }
    public string Duration { get; init; } = "—";
    public string SubscriptionName { get; init; } = "未配置订阅";
    public string Source { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
    public int NodeCount { get; init; }
    public bool HasConfiguration { get; init; }
    public string LanAddress { get; init; } = "—";
    public string StatusMessage { get; init; } = string.Empty;
    public ExitIpInfo? Exit { get; init; }
    public long UploadRate { get; init; }
    public long DownloadRate { get; init; }
    public long UploadPeak { get; init; }
    public long DownloadPeak { get; init; }
    public long UploadTotal { get; init; }
    public long DownloadTotal { get; init; }
    public int Tcp { get; init; }
    public int Udp { get; init; }
    public IReadOnlyList<ConnectionRow> Live { get; init; } = [];
    public IReadOnlyList<ConnectionRow> Recent { get; init; } = [];
    public IReadOnlyList<ProxyGroupInfo> Groups { get; init; } = [];
    public IReadOnlyList<int?> ServiceDelays { get; init; } = [];
}
