using AsterDock.Contracts;
using Avalonia.Controls;
using NetworkAccelerator.Module.Views;

namespace NetworkAccelerator.Module;

public sealed class NetworkAcceleratorApplicationModule :
    IApplicationModule,
    IApplicationContextAware,
    IApplicationQuickActionProvider
{
    private IApplicationContext? _context;
    private NetworkAcceleratorView? _view;

    public void Initialize(IApplicationContext context) => _context = context;

    public Control CreateView()
    {
        var context = _context ?? throw new InvalidOperationException("网络加速应用尚未完成容器初始化");
        return _view ??= new NetworkAcceleratorView(context, Path.GetDirectoryName(typeof(NetworkAcceleratorApplicationModule).Assembly.Location)!);
    }

    public IReadOnlyList<ApplicationQuickAction> GetQuickActions() =>
    [
        new ApplicationQuickAction("toggle-connection", "启动/停止网络加速", () => _ = _view?.ToggleConnectionAsync())
    ];

    public void Dispose()
    {
        _view?.DisposeResources();
        _view = null;
        _context = null;
    }
}
