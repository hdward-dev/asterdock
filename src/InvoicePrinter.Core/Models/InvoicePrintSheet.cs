namespace InvoicePrinter.Core.Models;

public sealed record InvoicePrintSheet(
    int FirstInvoiceIndex,
    int? SecondInvoiceIndex = null,
    bool UsesFullPage = false);
