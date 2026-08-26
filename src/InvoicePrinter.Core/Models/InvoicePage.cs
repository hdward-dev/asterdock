namespace InvoicePrinter.Core.Models;

public sealed record InvoicePage(
    string SourcePath,
    string DisplayName,
    byte[] PreviewPng,
    int PageIndex = 0,
    bool OccupiesFullPage = false);
