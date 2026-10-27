namespace NetworkAccelerator.Core.Services;

public static class ServiceProbeCatalog
{
    public static readonly IReadOnlyList<ServiceProbeDefinition> All =
    [
        new("Google", "https://www.gstatic.com/generate_204"),
        new("GitHub", "https://github.com"),
        new("YouTube", "https://www.youtube.com/generate_204"),
        new("ChatGPT", "https://chatgpt.com")
    ];
}

public sealed record ServiceProbeDefinition(string Name, string Url);
