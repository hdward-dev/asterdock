namespace InvoicePrinter.Core.Models;

public sealed record InvoiceRecognition(
    string SourcePath,
    string DisplayName,
    string? Category,
    decimal? Amount,
    string? SerialNumber,
    string? Message);
