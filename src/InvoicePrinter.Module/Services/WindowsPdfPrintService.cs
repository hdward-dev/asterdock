using PDFtoImage;
using SkiaSharp;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace InvoicePrinter.Module.Services;

internal static class WindowsPdfPrintService
{
    private const int HorzRes = 8;
    private const int VertRes = 10;
    private const int StretchModeHalftone = 4;
    private const uint DibRgbColors = 0;
    private const uint Srccopy = 0x00CC0020;

    public static Task PrintAsync(string pdfPath, string printerName) =>
        Task.Run(() => Print(pdfPath, printerName));

    private static void Print(string pdfPath, string printerName)
    {
        if (!File.Exists(pdfPath)) throw new FileNotFoundException("待打印文件不存在", pdfPath);

        var printerDc = CreateDC("WINSPOOL", printerName, null, IntPtr.Zero);
        if (printerDc == IntPtr.Zero) ThrowLastWin32("无法连接所选打印机");

        var documentStarted = false;
        try
        {
            var document = new DocInfo
            {
                Size = Marshal.SizeOf<DocInfo>(),
                DocumentName = "发票打印",
                DataType = null
            };

            if (StartDoc(printerDc, ref document) <= 0) ThrowLastWin32("无法创建打印任务");
            documentStarted = true;

            var pdf = File.ReadAllBytes(pdfPath);
#pragma warning disable CA1416 // PDFtoImage supports the desktop platforms targeted by this app.
            var pageCount = Conversion.GetPageCount(pdf);
            for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                using var rendered = Conversion.ToImage(pdf, pageIndex, options: new RenderOptions(Dpi: 300));
                using var bitmap = ToBgraBitmap(rendered);
                PrintPage(printerDc, bitmap);
            }
#pragma warning restore CA1416

            if (EndDoc(printerDc) <= 0) ThrowLastWin32("打印任务提交失败");
            documentStarted = false;
        }
        catch
        {
            if (documentStarted) AbortDoc(printerDc);
            throw;
        }
        finally
        {
            DeleteDC(printerDc);
        }
    }

    private static SKBitmap ToBgraBitmap(SKBitmap source)
    {
        var bitmap = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return bitmap;
    }

    private static void PrintPage(IntPtr printerDc, SKBitmap bitmap)
    {
        if (StartPage(printerDc) <= 0) ThrowLastWin32("无法开始打印页面");

        try
        {
            var printableWidth = GetDeviceCaps(printerDc, HorzRes);
            var printableHeight = GetDeviceCaps(printerDc, VertRes);
            if (printableWidth <= 0 || printableHeight <= 0)
                throw new InvalidOperationException("无法读取打印机的可打印区域");

            var scale = Math.Min((double)printableWidth / bitmap.Width, (double)printableHeight / bitmap.Height);
            var targetWidth = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
            var targetX = (printableWidth - targetWidth) / 2;
            var targetY = (printableHeight - targetHeight) / 2;

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = bitmap.Width,
                    Height = -bitmap.Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    SizeImage = (uint)(bitmap.RowBytes * bitmap.Height)
                }
            };

            SetStretchBltMode(printerDc, StretchModeHalftone);
            var result = StretchDIBits(
                printerDc,
                targetX, targetY, targetWidth, targetHeight,
                0, 0, bitmap.Width, bitmap.Height,
                bitmap.GetPixels(), ref info, DibRgbColors, Srccopy);
            if (result == 0 || result == -1) ThrowLastWin32("页面图像发送失败");
        }
        finally
        {
            if (EndPage(printerDc) <= 0) ThrowLastWin32("无法结束打印页面");
        }
    }

    private static void ThrowLastWin32(string message)
    {
        var error = Marshal.GetLastWin32Error();
        throw new InvalidOperationException(error == 0 ? message : $"{message}：{new Win32Exception(error).Message}");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo
    {
        public int Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string DocumentName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Output;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DataType;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RgbQuad
    {
        public byte Blue;
        public byte Green;
        public byte Red;
        public byte Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public RgbQuad Colors;
    }

    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr initData);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "StartDocW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDoc(IntPtr deviceContext, ref DocInfo documentInfo);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int EndDoc(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int AbortDoc(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int StartPage(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int EndPage(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr deviceContext, int index);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr deviceContext, int mode);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int StretchDIBits(
        IntPtr deviceContext,
        int destinationX, int destinationY, int destinationWidth, int destinationHeight,
        int sourceX, int sourceY, int sourceWidth, int sourceHeight,
        IntPtr bits, ref BitmapInfo bitmapInfo, uint usage, uint rasterOperation);
}
