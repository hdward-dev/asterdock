using AsterDock.Contracts;
using Avalonia.Controls;
namespace URemote.Module;
public sealed class URemoteApplicationModule : IApplicationModule, IApplicationContextAware
{
    private IApplicationContext? context;
    private URemoteView? view;
    public void Initialize(IApplicationContext value) => context = value;
    public Control CreateView()
    {
        var current = context ?? throw new InvalidOperationException();
        return view ??= new URemoteView(current.DataDirectory, current);
    }
    public void Dispose() { view?.Dispose(); view = null; context = null; }
}
