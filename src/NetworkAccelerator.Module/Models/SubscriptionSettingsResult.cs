using NetworkAccelerator.Core.Models;

namespace NetworkAccelerator.Module.Models;

public sealed record SubscriptionSettingsResult(
    IReadOnlyList<SubscriptionConfiguration> Configurations,
    string ActiveConfigurationId);
