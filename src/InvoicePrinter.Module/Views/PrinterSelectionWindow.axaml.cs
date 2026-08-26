using Avalonia.Controls;
using Avalonia.Interactivity;
using InvoicePrinter.Module.Services;
using System.Collections.ObjectModel;

namespace InvoicePrinter.Module.Views;

public partial class PrinterSelectionWindow : Window
{
    private static readonly TimeSpan PrintSuccessDisplayDuration = TimeSpan.FromSeconds(1);

    private readonly SystemPrintService _service;
    private readonly string _pdfPath;
    public ObservableCollection<PrinterInfo> Printers { get; } = [];

    public PrinterSelectionWindow()
    {
        InitializeComponent(); DataContext = this; _service = new SystemPrintService(); _pdfPath = string.Empty;
    }

    public PrinterSelectionWindow(SystemPrintService service, string pdfPath, int pageCount) : this()
    {
        _service = service; _pdfPath = pdfPath;
        PageCountText.Text = $"共 {pageCount} 页"; Opened += LoadPrintersAsync;
    }

    private async void LoadPrintersAsync(object? sender, EventArgs e)
    {
        try { foreach (var printer in await _service.GetPrintersAsync()) Printers.Add(printer); }
        catch (Exception exception) { StatusText.Text = exception.Message; }
        LoadingProgress.IsVisible = false;
        PrinterComboBox.SelectedItem = Printers.FirstOrDefault(item => item.IsDefault) ?? Printers.FirstOrDefault();
        PrintButton.IsEnabled = PrinterComboBox.SelectedItem is not null;
        if (Printers.Count == 0) StatusText.Text = "没有找到可用打印机";
    }

    private async void Print_Click(object? sender, RoutedEventArgs e)
    {
        if (PrinterComboBox.SelectedItem is not PrinterInfo printer) return;
        PrintButton.IsEnabled = false; StatusText.Text = "正在发送打印任务…";
        try
        {
            await _service.PrintAsync(_pdfPath, printer.Name);
            StatusText.Text = $"已发送到 {printer.DisplayName}";
            await Task.Delay(PrintSuccessDisplayDuration);
            Close(true);
        }
        catch (Exception exception) { StatusText.Text = $"打印失败：{exception.Message}"; PrintButton.IsEnabled = true; }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
