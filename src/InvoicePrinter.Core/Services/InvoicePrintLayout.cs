using InvoicePrinter.Core.Models;

namespace InvoicePrinter.Core.Services;

public static class InvoicePrintLayout
{
    public static IReadOnlyList<InvoicePrintSheet> CreateSheets(
        IReadOnlyList<InvoicePage> invoices,
        int invoicesPerPage,
        bool keepFullPageInvoicesSeparate)
    {
        if (invoicesPerPage is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(invoicesPerPage), "每页发票数量只能是 1 或 2");

        var sheets = new List<InvoicePrintSheet>();
        int? pendingInvoiceIndex = null;

        for (var index = 0; index < invoices.Count; index++)
        {
            var usesFullPage = invoicesPerPage == 1 ||
                               keepFullPageInvoicesSeparate && invoices[index].OccupiesFullPage;
            if (usesFullPage)
            {
                FlushPendingInvoice(sheets, ref pendingInvoiceIndex);
                sheets.Add(new InvoicePrintSheet(index, UsesFullPage: true));
                continue;
            }

            if (pendingInvoiceIndex is null)
            {
                pendingInvoiceIndex = index;
                continue;
            }

            sheets.Add(new InvoicePrintSheet(pendingInvoiceIndex.Value, index));
            pendingInvoiceIndex = null;
        }

        FlushPendingInvoice(sheets, ref pendingInvoiceIndex);
        return sheets;
    }

    private static void FlushPendingInvoice(List<InvoicePrintSheet> sheets, ref int? pendingInvoiceIndex)
    {
        if (pendingInvoiceIndex is null) return;
        sheets.Add(new InvoicePrintSheet(pendingInvoiceIndex.Value));
        pendingInvoiceIndex = null;
    }
}
