namespace InvoicePrinter.Module.Models;

public sealed class RecognitionRow(string file, string category, string amount, string serial, string status)
{
    public string File { get; } = file;
    public string Category { get; } = category;
    public string Amount { get; } = amount;
    public string Serial { get; } = serial;
    public string Status { get; } = status;
}
