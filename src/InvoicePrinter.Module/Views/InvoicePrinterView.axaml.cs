using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AsterDock.Contracts;
using InvoicePrinter.Core.Services;
using InvoicePrinter.Module.Models;
using InvoicePrinter.Module.Services;
using System.Collections.ObjectModel;

namespace InvoicePrinter.Module.Views;

public partial class InvoicePrinterView : UserControl
{
    private readonly InvoiceLoader _loader = new();
    private readonly PrintPdfService _pdfService = new();
    private readonly SystemPrintService _systemPrint = new();
    private readonly InvoiceOcrService _ocrService = new();
    private readonly IWindowService? _windowService;
    private int _currentPage;

    public ObservableCollection<InvoicePageItem> Pages { get; } = [];

    public InvoicePrinterView() : this(null)
    {
    }

    public InvoicePrinterView(IWindowService? windowService)
    {
        InitializeComponent();
        _windowService = windowService;
        DataContext = this;
        RefreshPreview();
    }

    public void DisposeResources()
    {
        foreach (var item in Pages) item.Preview.Dispose();
        Pages.Clear();
    }

    private async void BrowseFiles_Click(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择发票文件",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("发票文件")
                {
                    Patterns = ["*.pdf", "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tif", "*.tiff"]
                }
            ]
        });
        await AddFilesAsync(files.Select(file => file.Path.LocalPath));
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.Select(file => file.Path.LocalPath) ?? [];
        await AddFilesAsync(files);
    }

    private async Task AddFilesAsync(IEnumerable<string> paths)
    {
        ImportProgress.IsVisible = true;
        try
        {
            foreach (var path in paths)
            {
                if (Pages.Any(page => page.Page.SourcePath == path)) continue;
                try
                {
                    var loadedPages = await Task.Run(() => _loader.Load(path));
                    foreach (var page in loadedPages) Pages.Add(new InvoicePageItem(page));
                }
                catch (Exception exception)
                {
                    await ShowErrorAsync($"{Path.GetFileName(path)}：{exception.Message}");
                }
            }
        }
        finally
        {
            ImportProgress.IsVisible = false;
            RefreshPreview();
        }
    }

    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InvoicePageItem item }) return;
        item.Preview.Dispose();
        Pages.Remove(item);
        RefreshPreview();
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        DisposeResources();
        _currentPage = 0;
        RefreshPreview();
    }

    private void PreviousPage_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentPage <= 0) return;
        _currentPage--;
        RefreshPreview();
    }

    private void NextPage_Click(object? sender, RoutedEventArgs e)
    {
        if ((_currentPage + 1) * 2 >= Pages.Count) return;
        _currentPage++;
        RefreshPreview();
    }

    private async void Print_Click(object? sender, RoutedEventArgs e)
    {
        if (Pages.Count == 0)
        {
            await ShowErrorAsync("请先导入发票文件");
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var output = Path.Combine(Path.GetTempPath(), $"发票打印-{DateTime.Now:yyyyMMdd-HHmmss}.pdf");
        var margin = (float)(MarginInput.Value ?? 10);
        _pdfService.Save(Pages.Select(item => item.Page).ToList(), output, margin, DividerToggle.IsChecked == true);
        var pageCount = (int)Math.Ceiling(Pages.Count / 2d);
        var dialog = new PrinterSelectionWindow(_systemPrint, output, pageCount);
        if (_windowService is not null) await _windowService.ShowDialogAsync<bool>(dialog);
        else await dialog.ShowDialog<bool>(owner);
        if (Pages.Count > 0) await RunRecognitionAsync();
    }

    private async void Recognize_Click(object? sender, RoutedEventArgs e)
    {
        if (Pages.Count == 0)
        {
            await ShowErrorAsync("请先导入发票文件");
            return;
        }
        await RunRecognitionAsync();
    }

    private async Task RunRecognitionAsync()
    {
        RecognizeButton.IsEnabled = false;
        PrintButton.IsEnabled = false;
        PageSummary.Text = "正在识别票面信息…";
        try
        {
            var recognitions = await Task.Run(() => _ocrService.RecognizeAsync(Pages.Select(item => item.Page).ToList()));
            var window = new RecognitionResultWindow(recognitions);
            if (_windowService is not null) await _windowService.ShowDialogAsync<bool>(window);
            else if (TopLevel.GetTopLevel(this) is Window resultOwner) await window.ShowDialog<bool>(resultOwner);
            else window.Show();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync($"识别失败：{exception.Message}");
        }
        finally
        {
            RecognizeButton.IsEnabled = true;
            PrintButton.IsEnabled = true;
            RefreshPreview();
        }
    }

    private void RefreshPreview()
    {
        var count = (int)Math.Ceiling(Pages.Count / 2d);
        _currentPage = Math.Clamp(_currentPage, 0, Math.Max(0, count - 1));
        var index = _currentPage * 2;
        PreviewTop.Source = Pages.ElementAtOrDefault(index)?.Preview;
        PreviewBottom.Source = Pages.ElementAtOrDefault(index + 1)?.Preview;
        ImportedTitle.Text = $"已导入 {Pages.Count} 张发票";
        PageIndicator.Text = count == 0 ? "0 / 0" : $"{_currentPage + 1} / {count}";
        PageSummary.Text = $"共 {count} 页";
        PreviousPageButton.IsEnabled = _currentPage > 0;
        NextPageButton.IsEnabled = _currentPage + 1 < count;
        EmptyState.IsVisible = Pages.Count == 0;
        InvoiceList.IsVisible = Pages.Count > 0;
    }

    private async Task ShowErrorAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new Window
        {
            Title = "提示",
            Width = 480,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var ok = new Button { Content = "确定", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Width = 90 };
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(22),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                ok
            }
        };
        await dialog.ShowDialog(owner);
    }
}
