using AsterDock.Contracts;
using Avalonia.Controls;
using Home.Module.ViewModels;
using Home.Module.Views;

namespace Home.Module;

public sealed class HomeApplicationModule : IApplicationModule, IApplicationContextAware
{
    private IApplicationContext? _context;
    private HomeViewModel? _model;

    public void Initialize(IApplicationContext context) => _context = context;

    public Control CreateView()
    {
        if (_context is null) throw new InvalidOperationException("主页应用尚未初始化");
        _model ??= new HomeViewModel(_context);
        return new HomeView(_model);
    }

    public void Dispose()
    {
        _model?.Dispose();
        _model = null;
        _context = null;
    }
}
