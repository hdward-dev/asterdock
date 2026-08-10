using AsterDock.Contracts;
using AndroidScreen.Module.Views;
using Avalonia.Controls;

namespace AndroidScreen.Module;

public sealed class AndroidScreenApplicationModule : IApplicationModule, IApplicationContextAware
{
    private IApplicationContext? _context;
    private AndroidScreenView? _view;

    public void Initialize(IApplicationContext context) => _context = context;

    public Control CreateView()
    {
        var context = _context ?? throw new InvalidOperationException("Android 投屏应用尚未完成容器初始化");
        return _view ??= new AndroidScreenView(
            context.DataDirectory,
            Path.GetDirectoryName(typeof(AndroidScreenApplicationModule).Assembly.Location)!);
    }

    public void Dispose()
    {
        _view?.DisposeResources();
        _view = null;
        _context = null;
    }
}
