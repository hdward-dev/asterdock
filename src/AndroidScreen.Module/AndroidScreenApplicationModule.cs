using AsterDock.Contracts;
using AndroidScreen.Module.ViewModels;
using AndroidScreen.Module.Views;
using Avalonia.Controls;

namespace AndroidScreen.Module;

public sealed class AndroidScreenApplicationModule : IApplicationModule, IApplicationContextAware
{
    private IApplicationContext? _context;
    private AndroidScreenViewModel? _model;

    public void Initialize(IApplicationContext context) => _context = context;

    public Control CreateView()
    {
        var context = _context ?? throw new InvalidOperationException("Android 投屏应用尚未完成容器初始化");
        // One scrcpy session. A second window binds to it instead of starting another.
        _model ??= new AndroidScreenViewModel(
            context.DataDirectory,
            Path.GetDirectoryName(typeof(AndroidScreenApplicationModule).Assembly.Location)!,
            context.Feedback);
        return new AndroidScreenView(_model);
    }

    public void Dispose()
    {
        _model?.Dispose();
        _model = null;
        _context = null;
    }
}
