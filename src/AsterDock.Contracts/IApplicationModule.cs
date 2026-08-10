using Avalonia.Controls;

namespace AsterDock.Contracts;

public interface IApplicationModule : IDisposable
{
    Control CreateView();
}
