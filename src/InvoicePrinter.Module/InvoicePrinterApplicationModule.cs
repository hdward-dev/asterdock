using AsterDock.Contracts;
using Avalonia.Controls;
using InvoicePrinter.Module.Views;

namespace InvoicePrinter.Module;

public sealed class InvoicePrinterApplicationModule : IApplicationModule, IApplicationContextAware
{
    private readonly List<InvoicePrinterView> _views = [];
    private IApplicationContext? _context;

    public void Initialize(IApplicationContext context) => _context = context;

    public Control CreateView()
    {
        // Each window is its own invoice list. The loader, PDF, and print services are stateless,
        // so a second page does not start another engine.
        var view = new InvoicePrinterView(_context?.Windows, _context?.Feedback);
        _views.Add(view);
        return view;
    }

    public void Dispose()
    {
        foreach (var view in _views) view.DisposeResources();
        _views.Clear();
        _context = null;
    }
}
