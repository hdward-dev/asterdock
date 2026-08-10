namespace NetworkAccelerator.Core.Models;

public sealed record SubscriptionProfile(
    string Name,
    IReadOnlyList<ProxyNode> Nodes,
    DateTimeOffset UpdatedAt,
    long? UsedBytes = null,
    long? TotalBytes = null,
    DateTimeOffset? ExpiresAt = null);
