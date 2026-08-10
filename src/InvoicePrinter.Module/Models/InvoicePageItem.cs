using Avalonia.Media.Imaging;
using InvoicePrinter.Core.Models;

namespace InvoicePrinter.Module.Models;

public sealed class InvoicePageItem
{
    public InvoicePage Page { get; }
    public string DisplayName => Page.DisplayName;
    public Bitmap Preview { get; }

    public InvoicePageItem(InvoicePage page)
    {
        Page = page;
        Preview = new Bitmap(new MemoryStream(page.PreviewPng));
    }
}
