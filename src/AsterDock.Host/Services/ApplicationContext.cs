using AsterDock.Contracts;
using AsterDock.UI.Services;

namespace AsterDock.Host.Services;

internal sealed class ApplicationContext : IApplicationContext, IDisposable
{
    private readonly ApplicationWindowService _windowService;

    public ApplicationContext(
        string applicationId,
        Avalonia.Controls.Window owner,
        IApplicationShell shell,
        ISystemMetricsService systemMetrics,
        ILoadingService loading,
        IUserFeedbackService feedback)
    {
        ApplicationId = applicationId;
        DataDirectory = ApplicationPaths.GetApplicationDataDirectory(applicationId);
        _windowService = new ApplicationWindowService(owner, feedback as UserFeedbackService);
        Shell = shell;
        SystemMetrics = systemMetrics;
        Loading = loading;
        Feedback = feedback;
    }

    public string ApplicationId { get; }
    public string DataDirectory { get; }
    public IWindowService Windows => _windowService;
    public IApplicationShell Shell { get; }
    public ISystemMetricsService SystemMetrics { get; }
    public ILoadingService Loading { get; }
    public IUserFeedbackService Feedback { get; }
    public void SetWindowOwner(Avalonia.Controls.Window owner) => _windowService.SetOwner(owner);

    public void Dispose() => _windowService.Dispose();
}
