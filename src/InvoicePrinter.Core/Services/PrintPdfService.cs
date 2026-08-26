using InvoicePrinter.Core.Models;
using SkiaSharp;

namespace InvoicePrinter.Core.Services;

public sealed class PrintPdfService
{
    public const float PageWidth = 595.28f;
    public const float PageHeight = 841.89f;
    private const float DividerHeight = 16f;

    public int Save(
        IReadOnlyList<InvoicePage> invoices,
        string outputPath,
        float marginMillimeters = 10,
        bool showDivider = true,
        int invoicesPerPage = 2,
        bool keepFullPageInvoicesSeparate = true)
    {
        var margin = Math.Clamp(marginMillimeters, 5, 30) / 25.4f * 72f;
        var sheets = InvoicePrintLayout.CreateSheets(invoices, invoicesPerPage, keepFullPageInvoicesSeparate);
        using var stream = File.Create(outputPath);
        using var document = SKDocument.CreatePdf(stream);
        using var linePaint = new SKPaint { Color = new SKColor(152, 162, 179), StrokeWidth = 0.8f, Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash([6, 4], 0) };
        var slotHeight = (PageHeight - margin * 2 - DividerHeight) / 2;

        foreach (var sheet in sheets)
        {
            var canvas = document.BeginPage(PageWidth, PageHeight);
            if (sheet.UsesFullPage)
            {
                DrawInvoice(canvas, invoices[sheet.FirstInvoiceIndex], new SKRect(margin, margin, PageWidth - margin, PageHeight - margin));
            }
            else
            {
                DrawInvoice(canvas, invoices[sheet.FirstInvoiceIndex], new SKRect(margin, margin, PageWidth - margin, margin + slotHeight));
                if (sheet.SecondInvoiceIndex is int secondInvoiceIndex)
                {
                    var dividerY = margin + slotHeight + DividerHeight / 2;
                    if (showDivider) canvas.DrawLine(margin, dividerY, PageWidth - margin, dividerY, linePaint);
                    DrawInvoice(canvas, invoices[secondInvoiceIndex], new SKRect(margin, margin + slotHeight + DividerHeight, PageWidth - margin, PageHeight - margin));
                }
            }
            document.EndPage();
        }
        document.Close();
        return sheets.Count;
    }

    private static void DrawInvoice(SKCanvas canvas, InvoicePage invoice, SKRect bounds)
    {
        using var bitmap = SKBitmap.Decode(invoice.PreviewPng);
        if (bitmap is null) return;
        var scale = Math.Min(bounds.Width / bitmap.Width, bounds.Height / bitmap.Height);
        var width = bitmap.Width * scale;
        var height = bitmap.Height * scale;
        var destination = new SKRect(bounds.MidX - width / 2, bounds.MidY - height / 2, bounds.MidX + width / 2, bounds.MidY + height / 2);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(bitmap, destination, paint);
    }
}
