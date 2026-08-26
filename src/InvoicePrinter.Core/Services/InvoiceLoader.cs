using InvoicePrinter.Core.Models;
using PDFtoImage;
using SkiaSharp;

namespace InvoicePrinter.Core.Services;

public sealed class InvoiceLoader
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    { ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    public IReadOnlyList<InvoicePage> Load(string path)
    {
        if (!File.Exists(path) || !Supported.Contains(Path.GetExtension(path)))
            throw new NotSupportedException("仅支持 PDF、PNG、JPG、BMP 和 TIFF 文件");

        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return [new InvoicePage(path, Path.GetFileName(path), NormalizeImage(path))];
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("当前平台不支持 PDF 渲染");

        var pdf = File.ReadAllBytes(path);
#pragma warning disable CA1416 // Guarded above; PDFtoImage supports Windows, macOS and Linux.
        var count = Conversion.GetPageCount(pdf);
        var result = new List<InvoicePage>(count);
        for (var index = 0; index < count; index++)
        {
            using var bitmap = Conversion.ToImage(pdf, index, options: new RenderOptions(Dpi: 144));
            result.Add(new InvoicePage(path, count > 1 ? $"{Path.GetFileName(path)} · 第 {index + 1} 页" : Path.GetFileName(path), EncodePng(bitmap), index));
        }
#pragma warning restore CA1416
        return result;
    }

    private static byte[] NormalizeImage(string path)
    {
        using var bitmap = SKBitmap.Decode(path) ?? throw new InvalidDataException("图片内容无法解析");
        return EncodePng(bitmap);
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
