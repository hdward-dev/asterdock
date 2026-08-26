using InvoicePrinter.Core.Models;
using PDFtoImage;
using SkiaSharp;

namespace InvoicePrinter.Core.Services;

public sealed class InvoiceLoader
{
    private const byte BackgroundThreshold = 245;
    private const double FullPageContentWidthRatio = 0.6;
    private const double FullPageContentHeightRatio = 0.72;

    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    { ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    public IReadOnlyList<InvoicePage> Load(string path)
    {
        if (!File.Exists(path) || !Supported.Contains(Path.GetExtension(path)))
            throw new NotSupportedException("仅支持 PDF、PNG、JPG、BMP 和 TIFF 文件");

        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var bitmap = LoadImage(path);
            return [CreateInvoicePage(path, Path.GetFileName(path), bitmap)];
        }
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("当前平台不支持 PDF 渲染");

        var pdf = File.ReadAllBytes(path);
#pragma warning disable CA1416 // Guarded above; PDFtoImage supports Windows, macOS and Linux.
        var count = Conversion.GetPageCount(pdf);
        var result = new List<InvoicePage>(count);
        for (var index = 0; index < count; index++)
        {
            using var bitmap = Conversion.ToImage(pdf, index, options: new RenderOptions(Dpi: 144));
            var displayName = count > 1 ? $"{Path.GetFileName(path)} · 第 {index + 1} 页" : Path.GetFileName(path);
            result.Add(CreateInvoicePage(path, displayName, bitmap, index));
        }
#pragma warning restore CA1416
        return result;
    }

    private static SKBitmap LoadImage(string path) =>
        SKBitmap.Decode(path) ?? throw new InvalidDataException("图片内容无法解析");

    private static InvoicePage CreateInvoicePage(string path, string displayName, SKBitmap bitmap, int pageIndex = 0) =>
        new(path, displayName, EncodePng(bitmap), pageIndex, OccupiesFullPage(bitmap));

    private static bool OccupiesFullPage(SKBitmap bitmap)
    {
        var sampleStep = Math.Max(1, Math.Min(bitmap.Width, bitmap.Height) / 500);
        var minX = bitmap.Width;
        var minY = bitmap.Height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < bitmap.Height; y += sampleStep)
        {
            for (var x = 0; x < bitmap.Width; x += sampleStep)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.Alpha == 0 ||
                    color.Red >= BackgroundThreshold &&
                    color.Green >= BackgroundThreshold &&
                    color.Blue >= BackgroundThreshold)
                    continue;

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY) return false;
        var contentWidthRatio = (maxX - minX + sampleStep) / (double)bitmap.Width;
        var contentHeightRatio = (maxY - minY + sampleStep) / (double)bitmap.Height;
        return contentWidthRatio >= FullPageContentWidthRatio &&
               contentHeightRatio >= FullPageContentHeightRatio;
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
