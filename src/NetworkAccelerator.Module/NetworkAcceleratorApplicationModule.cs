using AsterDock.Contracts;
using Avalonia.Controls;
using NetworkAccelerator.Module.ViewModels;
using NetworkAccelerator.Module.Views;

namespace NetworkAccelerator.Module;

public sealed class NetworkAcceleratorApplicationModule :
    IApplicationModule,
    IApplicationContextAware,
    IApplicationQuickActionProvider
{
    private IApplicationContext? _context;
    private NetworkAcceleratorViewModel? _model;

    public void Initialize(IApplicationContext context) => _context = context;

    public Control CreateView()
    {
        var context = _context ?? throw new InvalidOperationException("网络加速应用尚未完成容器初始化");
        var directory = Path.GetDirectoryName(typeof(NetworkAcceleratorApplicationModule).Assembly.Location)!;
        // One engine for every window. Each call still builds a new, unmeasured page.
        _model ??= new NetworkAcceleratorViewModel(context.DataDirectory, directory, context.Feedback);
        return new NetworkAcceleratorView(context, _model);
    }

    public IReadOnlyList<ApplicationQuickAction> GetQuickActions() =>
    [
        new ApplicationQuickAction("toggle-connection", "启动/停止网络加速", () => _ = _model?.ToggleConnectionAsync())
    ];

    public void Dispose()
    {
        _model?.Dispose();
        _model = null;
        _context = null;
    }
}
