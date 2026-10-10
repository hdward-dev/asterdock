using AsterDock.Contracts;
using Avalonia.Controls;
using VMCube.Module.Views;
namespace VMCube.Module;
public sealed class VMCubeApplicationModule : IApplicationModule, IApplicationContextAware
{
    private IApplicationContext? _context;
    private VMCubeView? _view;
    public void Initialize(IApplicationContext context) => _context = context;
    public Control CreateView() => _view ??= new VMCubeView(_context ?? throw new InvalidOperationException("应用尚未初始化"));
    public void Dispose() { _view?.Dispose(); _view = null; _context = null; }
}
