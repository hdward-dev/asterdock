namespace NetworkAccelerator.Core.Models;

public sealed class NetworkAcceleratorSettings
{
    // 保留旧字段用于从单订阅版本平滑迁移。
    public string SubscriptionUrl { get; set; } = string.Empty;
    public List<SubscriptionConfiguration> SubscriptionConfigurations { get; set; } = [];
    public string ActiveSubscriptionId { get; set; } = string.Empty;
    public ProxyMode Mode { get; set; } = ProxyMode.Rule;
    public bool TunEnabled { get; set; } = true;
    public string SelectedNode { get; set; } = string.Empty;
    public bool InterruptExistingConnections { get; set; }
    public string ApiSecret { get; set; } = Convert.ToHexString(Guid.NewGuid().ToByteArray());
}
