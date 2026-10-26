using System.Runtime.InteropServices;

namespace AsterDock.Host.Views;

/// <summary>
/// Outer window silhouette for an X11 / XWayland client.
/// Transparent corners in the client buffer do not change the window rectangle, so the
/// compositor still outlines and shadows that square. The mask uses the same circular
/// radius as the content clip. A zero radius clears the mask and leaves a full rectangle.
/// </summary>
internal static class X11RoundedWindowShape
{
    internal readonly record struct MaskRect(int X, int Y, int Width, int Height);

    public static int DeviceRadius(double radiusDip, double renderScaling)
    {
        if (radiusDip <= 0 || renderScaling <= 0)
            return 0;
        return Math.Max(0, (int)Math.Round(radiusDip * renderScaling, MidpointRounding.AwayFromZero));
    }

    public static (int Width, int Height, int Radius) ForClient(double widthDip, double heightDip, double radiusDip, double renderScaling)
    {
        var scale = renderScaling <= 0 ? 1 : renderScaling;
        var width = Math.Max(0, (int)(widthDip * scale));
        var height = Math.Max(0, (int)(heightDip * scale));
        var radius = DeviceRadius(radiusDip, scale);
        if (width > 0 && height > 0)
            radius = Math.Min(radius, Math.Min(width, height) / 2);
        return (width, height, radius);
    }

    public static bool Contains(int width, int height, int radius, int x, int y)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
            return false;
        if (radius <= 0)
            return true;
        var limit = Math.Min(width, height) / 2;
        if (radius > limit)
            radius = limit;
        if (radius <= 0)
            return true;

        var px = x + 0.5;
        var py = y + 0.5;
        var cx = px < radius ? radius : px > width - radius ? width - radius : px;
        var cy = py < radius ? radius : py > height - radius ? height - radius : py;
        var dx = px - cx;
        var dy = py - cy;
        return dx * dx + dy * dy <= radius * (double)radius + 1e-9;
    }

    public static MaskRect[] Build(int width, int height, int radius)
    {
        if (width <= 0 || height <= 0 || width > short.MaxValue || height > short.MaxValue)
            return [];

        var rows = new List<MaskRect>();
        var y = 0;
        while (y < height)
        {
            var inset = 0;
            while (inset < width && !Contains(width, height, radius, inset, y))
                inset++;
            var span = width - inset * 2;
            if (span <= 0)
            {
                y++;
                continue;
            }

            var rowHeight = 1;
            while (y + rowHeight < height && SameInset(width, height, radius, inset, y + rowHeight))
                rowHeight++;
            rows.Add(new MaskRect(inset, y, span, rowHeight));
            y += rowHeight;
        }

        return rows.ToArray();
    }

    public static void Apply(IntPtr window, int widthPx, int heightPx, int radiusPx)
    {
        if (!OperatingSystem.IsLinux() || window == IntPtr.Zero || widthPx <= 0 || heightPx <= 0 || _unavailable)
            return;

        try
        {
            var display = Display();
            if (display == IntPtr.Zero || !ShapeAvailable(display))
                return;

            radiusPx = Math.Max(0, radiusPx);
            if (widthPx > short.MaxValue || heightPx > short.MaxValue)
                return;
            radiusPx = Math.Min(radiusPx, Math.Min(widthPx, heightPx) / 2);
            if (radiusPx <= 0)
            {
                XShapeCombineMask(display, window, ShapeBounding, 0, 0, IntPtr.Zero, ShapeSet);
                XShapeCombineMask(display, window, ShapeInput, 0, 0, IntPtr.Zero, ShapeSet);
            }
            else
            {
                var rows = Build(widthPx, heightPx, radiusPx);
                if (rows.Length == 0)
                    return;
                var native = new XRectangle[rows.Length];
                for (var i = 0; i < rows.Length; i++)
                {
                    native[i] = new XRectangle
                    {
                        X = (short)rows[i].X,
                        Y = (short)rows[i].Y,
                        Width = (ushort)rows[i].Width,
                        Height = (ushort)rows[i].Height
                    };
                }

                XShapeCombineRectangles(display, window, ShapeBounding, 0, 0, native, native.Length, ShapeSet, Unsorted);
                XShapeCombineRectangles(display, window, ShapeInput, 0, 0, native, native.Length, ShapeSet, Unsorted);
            }

            DeleteRectangularShadow(display, window);
            XSync(display, 0);
        }
        catch (DllNotFoundException)
        {
            _unavailable = true;
        }
        catch (EntryPointNotFoundException)
        {
            _unavailable = true;
        }
    }

    private static bool SameInset(int width, int height, int radius, int inset, int y)
    {
        var next = 0;
        while (next < width && !Contains(width, height, radius, next, y))
            next++;
        return next == inset && inset < width;
    }

    private static IntPtr Display()
    {
        if (_display != IntPtr.Zero || _unavailable)
            return _display;
        _display = XOpenDisplay(IntPtr.Zero);
        if (_display == IntPtr.Zero)
            _unavailable = true;
        return _display;
    }

    private static bool ShapeAvailable(IntPtr display)
    {
        if (_shapeChecked)
            return _shapeAvailable;
        _shapeChecked = true;
        _shapeAvailable = XShapeQueryExtension(display, out _, out _) != 0;
        if (!_shapeAvailable)
            _unavailable = true;
        return _shapeAvailable;
    }

    private static void DeleteRectangularShadow(IntPtr display, IntPtr window)
    {
        if (_shadowAtom == IntPtr.Zero)
            _shadowAtom = XInternAtom(display, "_KDE_NET_WM_SHADOW", 0);
        if (_shadowAtom != IntPtr.Zero)
            XDeleteProperty(display, window, _shadowAtom);
    }

    private const int ShapeBounding = 0;
    private const int ShapeInput = 2;
    private const int ShapeSet = 0;
    private const int Unsorted = 0;

    private static IntPtr _display;
    private static IntPtr _shadowAtom;
    private static bool _unavailable;
    private static bool _shapeChecked;
    private static bool _shapeAvailable;

    [StructLayout(LayoutKind.Sequential)]
    private struct XRectangle
    {
        public short X;
        public short Y;
        public ushort Width;
        public ushort Height;
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XSync(IntPtr display, int discard);

    [DllImport("libX11.so.6")]
    private static extern int XDeleteProperty(IntPtr display, IntPtr window, IntPtr property);

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern IntPtr XInternAtom(IntPtr display, string atomName, int onlyIfExists);

    [DllImport("libXext.so.6")]
    private static extern int XShapeQueryExtension(IntPtr display, out int eventBase, out int errorBase);

    [DllImport("libXext.so.6")]
    private static extern void XShapeCombineRectangles(
        IntPtr display, IntPtr window, int destKind, int xOff, int yOff,
        XRectangle[] rectangles, int count, int operation, int ordering);

    [DllImport("libXext.so.6")]
    private static extern void XShapeCombineMask(
        IntPtr display, IntPtr window, int destKind, int xOff, int yOff, IntPtr source, int operation);
}

public partial class MainWindow
{
    private int _outerShapeWidth;
    private int _outerShapeHeight;
    private int _outerShapeRadius = int.MinValue;
    private IntPtr _outerShapeWindow;

    private void ApplyOuterWindowShape()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var handle = TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero || handle.HandleDescriptor != "XID")
            return;

        var scale = RenderScaling <= 0 ? 1 : RenderScaling;
        var dip = ClientSize.Width > 1 && ClientSize.Height > 1 ? ClientSize : Bounds.Size;
        var shape = X11RoundedWindowShape.ForClient(dip.Width, dip.Height, WindowChrome.CornerRadius.TopLeft, scale);
        if (shape.Width <= 0 || shape.Height <= 0)
            return;
        if (handle.Handle == _outerShapeWindow && shape.Width == _outerShapeWidth
            && shape.Height == _outerShapeHeight && shape.Radius == _outerShapeRadius)
            return;

        X11RoundedWindowShape.Apply(handle.Handle, shape.Width, shape.Height, shape.Radius);
        _outerShapeWindow = handle.Handle;
        _outerShapeWidth = shape.Width;
        _outerShapeHeight = shape.Height;
        _outerShapeRadius = shape.Radius;
    }
}
