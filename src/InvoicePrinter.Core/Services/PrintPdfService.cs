using InvoicePrinter.Core.Models;
using SkiaSharp;

namespace InvoicePrinter.Core.Services;

public sealed class PrintPdfService
{
    public const float PageWidth = 595.28f;
    public const float PageHeight = 841.89f;
    private const float DividerHeight = 16f;

    public void Save(IReadOnlyList<InvoicePage> invoices, string outputPath, float marginMillimeters = 10, bool showDivider = true)
    {
        var margin = Math.Clamp(marginMillimeters, 5, 30) / 25.4f * 72f;
        using var stream = File.Create(outputPath);
        using var document = SKDocument.CreatePdf(stream);
        using var linePaint = new SKPaint { Color = new SKColor(152, 162, 179), StrokeWidth = 0.8f, Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash([6, 4], 0) };
        var slotHeight = (PageHeight - margin * 2 - DividerHeight) / 2;

        for (var index = 0; index < invoices.Count; index += 2)
        {
            var canvas = document.BeginPage(PageWidth, PageHeight);
            DrawInvoice(canvas, invoices[index], new SKRect(margin, margin, PageWidth - margin, margin + slotHeight));
            var dividerY = margin + slotHeight + DividerHeight / 2;
            if (showDivider) canvas.DrawLine(margin, dividerY, PageWidth - margin, dividerY, linePaint);
            if (index + 1 < invoices.Count)
                DrawInvoice(canvas, invoices[index + 1], new SKRect(margin, margin + slotHeight + DividerHeight, PageWidth - margin, PageHeight - margin));
            document.EndPage();
        }
        document.Close();
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
