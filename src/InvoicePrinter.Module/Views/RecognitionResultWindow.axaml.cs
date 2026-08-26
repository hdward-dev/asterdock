using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using InvoicePrinter.Core.Models;
using InvoicePrinter.Module.Models;

namespace InvoicePrinter.Module.Views;

public partial class RecognitionResultWindow : Window
{
    public ObservableCollection<RecognitionRow> Rows { get; } = [];

    public RecognitionResultWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public RecognitionResultWindow(IReadOnlyList<InvoiceRecognition> recognitions) : this()
    {
        foreach (var recognition in recognitions) Rows.Add(ToRow(recognition));
        var resolved = Rows.Count(row => row.Status == "已识别");
        SummaryText.Text = $"共识别 {Rows.Count} 张小票，其中 {resolved} 张解析出关键字段";
    }

    private async void Export_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出识别结果",
            SuggestedFileName = $"小票识别-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            FileTypeChoices =
            [
                new FilePickerFileType("CSV 文件") { Patterns = ["*.csv"] }
            ]
        });
        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("文件,类型,金额,编号,状态");
        foreach (var row in Rows)
            await writer.WriteLineAsync($"{Escape(row.File)},{Escape(row.Category)},{Escape(row.Amount)},{Escape(row.Serial)},{Escape(row.Status)}");
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private static RecognitionRow ToRow(InvoiceRecognition recognition)
    {
        var category = recognition.Category ?? "未知";
        var amount = recognition.Amount is { } value ? value.ToString("N2", CultureInfo.CurrentCulture) + " 元" : "—";
        var serial = string.IsNullOrEmpty(recognition.SerialNumber) ? "—" : recognition.SerialNumber;
        var status = recognition.Message ?? "已识别";
        return new RecognitionRow(recognition.DisplayName, category, amount, serial, status);
    }

    private static string Escape(string value)
    {
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
