using AsterDock.Contracts;

namespace AsterDock.Host.Services;

internal sealed class ApplicationContext : IApplicationContext, IDisposable
{
    private readonly ApplicationWindowService _windowService;

    public ApplicationContext(
        string applicationId,
        Avalonia.Controls.Window owner,
        IApplicationShell shell,
        ISystemMetricsService systemMetrics)
    {
        ApplicationId = applicationId;
        DataDirectory = ApplicationPaths.GetApplicationDataDirectory(applicationId);
        _windowService = new ApplicationWindowService(owner);
        Shell = shell;
        SystemMetrics = systemMetrics;
    }

    public string ApplicationId { get; }
    public string DataDirectory { get; }
    public IWindowService Windows => _windowService;
    public IApplicationShell Shell { get; }
    public ISystemMetricsService SystemMetrics { get; }

    public void Dispose() => _windowService.Dispose();
}
