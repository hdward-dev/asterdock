using AsterDock.Contracts;
using AsterDock.Host.Services;
using Avalonia.Controls;
using Avalonia.Media;

namespace AsterDock.Host.Modules;

public sealed class LoadedApplication : IDisposable
{
    private readonly IApplicationModule _module;
    private readonly AppModuleLoadContext _loadContext;
    private Control? _view;
    private Geometry? _iconGeometry;
    private ApplicationContext? _context;

    internal LoadedApplication(AppManifest manifest, string directory, IApplicationModule module, AppModuleLoadContext loadContext)
    {
        Manifest = manifest;
        Directory = directory;
        _module = module;
        _loadContext = loadContext;
    }

    public AppManifest Manifest { get; }
    public string Directory { get; }
    public string Name => Manifest.Name;
    public string Description => Manifest.Description;
    public string Version => Manifest.Version;
    public string Category => Manifest.Category;
    public IApplicationNavigationProvider? Navigation => _module as IApplicationNavigationProvider;
    public Geometry IconGeometry => _iconGeometry ??= Geometry.Parse(Manifest.Icon switch
    {
        "printer" => "M6,3 H18 V8 H20 A2,2 0 0 1 22,10 V17 H18 V22 H6 V17 H2 V10 A2,2 0 0 1 4,8 H6 Z M8,15 V20 H16 V15 Z M8,5 V8 H16 V5 Z",
        "monitor" => "M3,4 H21 V17 H3 Z M5,6 V15 H19 V6 Z M9,20 H15 M12,17 V20",
        "home" => "M3,11 L12,3 L21,11 V21 H14 V15 H10 V21 H3 Z",
        "network" => "M4.9,9.4 A11.8,11.8 0 0 1 19.1,9.4 M7.8,13 A7.1,7.1 0 0 1 16.2,13 M10.5,16.3 A2.6,2.6 0 0 1 13.5,16.3 M12,20 L12,20.1",
        _ => "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z"
    });
    public Control GetOrCreateView(Window owner, ISystemMetricsService systemMetrics)
    {
        if (_view is not null) return _view;
        EnsureInitialized(owner, systemMetrics);
        return _view = _module.CreateView();
    }

    public IReadOnlyList<ApplicationQuickAction> GetQuickActions(Window owner, ISystemMetricsService systemMetrics)
    {
        EnsureInitialized(owner, systemMetrics);
        return _module is IApplicationQuickActionProvider provider
            ? provider.GetQuickActions()
            : [];
    }

    public void Dispose()
    {
        _view = null;
        try
        {
            _module.Dispose();
        }
        finally
        {
            _context?.Dispose();
            _context = null;
            _loadContext.Unload();
        }
    }

    private void EnsureInitialized(Window owner, ISystemMetricsService systemMetrics)
    {
        if (_context is not null) return;
        if (owner is not IApplicationShell shell)
            throw new InvalidOperationException("宿主窗口未提供应用导航服务");
        var context = new ApplicationContext(Manifest.Id, owner, shell, systemMetrics);
        try
        {
            if (_module is IApplicationContextAware contextAware) contextAware.Initialize(context);
            _context = context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }
}
