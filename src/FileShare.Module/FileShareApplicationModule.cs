using AsterDock.Contracts;
using Avalonia.Controls;
using FileShareApp.Module.Views;

namespace FileShareApp.Module;

public sealed class FileShareApplicationModule : IApplicationModule, IApplicationContextAware
{
    private readonly List<FileShareView> _views = [];
    private Services.ShareServerManager? _server;
    private IApplicationContext? _context;

    public void Initialize(IApplicationContext context) => _context = context;

    public Control CreateView()
    {
        var context = _context ?? throw new InvalidOperationException("文件共享应用尚未完成容器初始化");
        _server ??= new Services.ShareServerManager(context.DataDirectory);
        var view = new FileShareView(context, _server);
        _views.Add(view);
        return view;
    }

    public void Dispose()
    {
        foreach (var view in _views) view.Release();
        _views.Clear();
        if (_server is { } server)
            Task.Run(server.StopAsync).GetAwaiter().GetResult();
        _server = null;
        _context = null;
    }
}
