using AsterDock.Contracts;
using Avalonia.Controls;
using VMCube.Module.Views;
namespace VMCube.Module;
public sealed class VMCubeApplicationModule : IApplicationModule, IApplicationContextAware
{
    private IApplicationContext? _context;
    private readonly List<VMCubeView> _views = [];
    private bool _autoConnectClaimed;
    public void Initialize(IApplicationContext context) => _context = context;
    public Control CreateView()
    {
        var context = _context ?? throw new InvalidOperationException("应用尚未初始化");
        // Connection state lives in the view, so a second window cannot share that session object.
        // It still uses the same application data directory, and it does not auto-connect.
        // Pop-out therefore does not start a second QEMU or libvirt engine.
        var connect = !_autoConnectClaimed;
        _autoConnectClaimed = true;
        var view = new VMCubeView(context, connect);
        _views.Add(view);
        return view;
    }
    public void Dispose()
    {
        foreach (var view in _views) view.Dispose();
        _views.Clear();
        _context = null;
    }
}
