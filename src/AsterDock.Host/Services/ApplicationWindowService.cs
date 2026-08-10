using AsterDock.Contracts;
using Avalonia.Controls;

namespace AsterDock.Host.Services;

internal sealed class ApplicationWindowService : IWindowService, IDisposable
{
    private readonly Window _owner;
    private readonly HashSet<Window> _windows = [];
    private readonly Dictionary<string, Window> _keyedWindows = new(StringComparer.OrdinalIgnoreCase);

    public ApplicationWindowService(Window owner) => _owner = owner;

    public void Show(Window window, bool owned = true)
    {
        Track(window);
        try
        {
            if (owned) window.Show(_owner);
            else window.Show();
        }
        catch
        {
            Untrack(window);
            throw;
        }
    }

    public Window ShowOrActivate(string key, Func<Window> windowFactory, bool owned = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(windowFactory);

        if (_keyedWindows.TryGetValue(key, out var existing))
        {
            existing.Activate();
            return existing;
        }

        var window = windowFactory();
        _keyedWindows.Add(key, window);
        window.Closed += (_, _) => _keyedWindows.Remove(key);
        try
        {
            Show(window, owned);
            return window;
        }
        catch
        {
            _keyedWindows.Remove(key);
            throw;
        }
    }

    public async Task<TResult> ShowDialogAsync<TResult>(Window window)
    {
        Track(window);
        try
        {
            return await window.ShowDialog<TResult>(_owner);
        }
        finally
        {
            Untrack(window);
        }
    }

    public void CloseAll()
    {
        foreach (var window in _windows.ToArray())
            window.Close();
        _windows.Clear();
        _keyedWindows.Clear();
    }

    public void Dispose() => CloseAll();

    private void Track(Window window)
    {
        if (!_windows.Add(window)) return;
        window.Closed += Window_Closed;
    }

    private void Untrack(Window window)
    {
        window.Closed -= Window_Closed;
        _windows.Remove(window);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (sender is Window window) Untrack(window);
    }
}
