using AsterDock.Contracts;
using Avalonia.Controls;
using InvoicePrinter.Module.Views;

namespace InvoicePrinter.Module;

public sealed class InvoicePrinterApplicationModule : IApplicationModule, IApplicationContextAware
{
    private InvoicePrinterView? _view;
    private IApplicationContext? _context;

    public void Initialize(IApplicationContext context) => _context = context;

    public Control CreateView() => _view ??= new InvoicePrinterView(_context?.Windows);

    public void Dispose()
    {
        _view?.DisposeResources();
        _view = null;
        _context = null;
    }
}
