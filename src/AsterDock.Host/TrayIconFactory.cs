using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;

namespace AsterDock.Host;

internal static class TrayIconFactory
{
    private const int IconSize = 32;
    private const int Transparent = 0x00000000;
    private const int AppBlue = unchecked((int)0xFF1267E8);
    private const int White = unchecked((int)0xFFFFFFFF);

    public static WriteableBitmap CreateApplicationIcon()
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(IconSize, IconSize),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using var framebuffer = bitmap.Lock();
        for (var y = 0; y < IconSize; y++)
        {
            for (var x = 0; x < IconSize; x++)
            {
                var color = IsInsideRoundedSquare(x, y) ? AppBlue : Transparent;
                if (IsLogoCell(x, y)) color = White;
                Marshal.WriteInt32(IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes + x * 4), color);
            }
        }

        return bitmap;
    }

    private static bool IsInsideRoundedSquare(int x, int y)
    {
        const int left = 2;
        const int right = 29;
        const int top = 2;
        const int bottom = 29;
        const int radius = 6;
        if (x < left || x > right || y < top || y > bottom) return false;

        var nearestX = Math.Clamp(x, left + radius, right - radius);
        var nearestY = Math.Clamp(y, top + radius, bottom - radius);
        var deltaX = x - nearestX;
        var deltaY = y - nearestY;
        return deltaX * deltaX + deltaY * deltaY <= radius * radius;
    }

    private static bool IsLogoCell(int x, int y)
    {
        var leftColumn = x is >= 7 and <= 13;
        var rightColumn = x is >= 18 and <= 24;
        var topRow = y is >= 7 and <= 13;
        var bottomRow = y is >= 18 and <= 24;
        return (leftColumn || rightColumn) && (topRow || bottomRow);
    }
}
