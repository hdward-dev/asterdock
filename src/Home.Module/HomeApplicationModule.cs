using AsterDock.Contracts;
using Avalonia.Controls;
using Home.Module.Views;

namespace Home.Module;

public sealed class HomeApplicationModule : IApplicationModule, IApplicationContextAware
{
    private IApplicationContext? _context;
    private HomeView? _view;

    public void Initialize(IApplicationContext context) => _context = context;

    public Control CreateView()
    {
        if (_context is null) throw new InvalidOperationException("主页应用尚未初始化");
        return _view ??= new HomeView(_context);
    }

    public void Dispose()
    {
        _view?.DisposeResources();
        _view = null;
        _context = null;
    }
}
