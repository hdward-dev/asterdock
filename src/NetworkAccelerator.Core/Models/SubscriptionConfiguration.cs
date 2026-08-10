namespace NetworkAccelerator.Core.Models;

public sealed class SubscriptionConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "我的配置";
    public string Source { get; set; } = string.Empty;
    public string CachedSource { get; set; } = string.Empty;
    public string SelectedNode { get; set; } = string.Empty;

    public SubscriptionConfiguration Clone() => new()
    {
        Id = Id,
        Name = Name,
        Source = Source,
        CachedSource = CachedSource,
        SelectedNode = SelectedNode
    };
}
