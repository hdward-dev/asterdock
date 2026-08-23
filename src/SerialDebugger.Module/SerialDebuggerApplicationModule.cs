using AsterDock.Contracts;
using Avalonia.Controls;
using SerialDebugger.Module.Views;

namespace SerialDebugger.Module;

public sealed class SerialDebuggerApplicationModule : IApplicationModule, IApplicationContextAware
{
    private IApplicationContext? _context;
    private SerialDebuggerView? _view;

    public void Initialize(IApplicationContext context) => _context = context;

    public Control CreateView()
    {
        var context = _context ?? throw new InvalidOperationException("串口调试助手尚未完成容器初始化");
        return _view ??= new SerialDebuggerView(context.DataDirectory);
    }

    public void Dispose()
    {
        _view?.DisposeResources();
        _view = null;
        _context = null;
    }
}
