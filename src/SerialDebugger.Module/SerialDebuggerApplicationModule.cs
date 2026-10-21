using AsterDock.Contracts;
using Avalonia.Controls;
using SerialDebugger.Module.ViewModels;
using SerialDebugger.Module.Views;

namespace SerialDebugger.Module;

public sealed class SerialDebuggerApplicationModule : IApplicationModule, IApplicationContextAware
{
    private IApplicationContext? _context;
    private SerialDebuggerViewModel? _model;

    public void Initialize(IApplicationContext context) => _context = context;

    public Control CreateView()
    {
        var context = _context ?? throw new InvalidOperationException("串口调试助手尚未完成容器初始化");
        // One workspace and one set of open ports. Extra windows bind to that same model.
        _model ??= new SerialDebuggerViewModel(context.DataDirectory, context.Feedback);
        return new SerialDebuggerView(_model, context.Feedback);
    }

    public void Dispose()
    {
        _model?.Dispose();
        _model = null;
        _context = null;
    }
}
