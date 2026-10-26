using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HAlign = Avalonia.Layout.HorizontalAlignment;
using VAlign = Avalonia.Layout.VerticalAlignment;

namespace AsterDock.Host.Views;

/// <summary>
/// Platform window shape for the main shell.
/// Linux has no window-corner preference. <see cref="WindowDecorations.BorderOnly"/> makes the X11
/// backend draw a square stroke and drop shadow around the client, so the corners stay square.
/// The Linux path clears window-manager decorations and clips the client to one radius on every corner.
/// The outer X11 silhouette uses that same radius; the caption's top and right insets are a separate distance.
/// </summary>
internal static class MainWindowChrome
{
    public const double CornerRadiusDip = 12;
    public const double ResizeGripDip = 6;

    /// <summary>
    /// Distance from the window top to the caption capsule, and from the window right to the capsule.
    /// This inset is not the corner radius.
    /// </summary>
    public const double TitleBarInsetDip = 4;

    internal readonly record struct Shape(
        WindowDecorations Decorations,
        bool TransparentSurface,
        CornerRadius CornerRadius,
        bool ClipToRoundedBounds,
        bool WindowsCornerPreference);

    public static Shape Resolve(bool linux, bool windows, WindowState state)
    {
        if (!linux)
            return new Shape(WindowDecorations.BorderOnly, false, default, false, windows);

        var square = state is WindowState.Maximized or WindowState.FullScreen;
        return new Shape(
            WindowDecorations.None,
            true,
            square ? default : new CornerRadius(CornerRadiusDip),
            true,
            false);
    }
}

public partial class MainWindow
{
    private Grid? _resizeGrips;

    private void ConfigureWindowChrome()
    {
        if (OperatingSystem.IsLinux())
        {
            CreateLinuxResizeGrips();
            Opened += (_, _) => ApplyOuterWindowShape();
            SizeChanged += (_, _) => ApplyOuterWindowShape();
            ScalingChanged += (_, _) => ApplyOuterWindowShape();
        }

        ApplyWindowChrome();
        ExtendClientAreaToDecorationsHint = !OperatingSystem.IsLinux();
        ExtendClientAreaTitleBarHeightHint = 40;
        if (OperatingSystem.IsWindows())
        {
            TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.Blur];
            Win32Properties.SetWindowCornerPreference(this, Win32Properties.WindowCornerPreference.Round);
        }

        base.PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty)
            {
                UpdateMaximizeGlyph();
                ApplyWindowChrome();
            }

            if (args.Property == WindowStateProperty || args.Property == IsVisibleProperty)
                _feedbackService.RefreshDelivery();
        };
    }

    private void ApplyWindowChrome()
    {
        var shape = MainWindowChrome.Resolve(OperatingSystem.IsLinux(), OperatingSystem.IsWindows(), WindowState);
        WindowDecorations = shape.Decorations;
        WindowChrome.CornerRadius = shape.CornerRadius;
        WindowChrome.ClipToBounds = shape.ClipToRoundedBounds;
        WindowChrome.BorderThickness = default;
        WindowChrome.BorderBrush = null;
        WindowChrome.BoxShadow = default;
        if (shape.TransparentSurface)
        {
            Background = Brushes.Transparent;
            TransparencyBackgroundFallback = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        }

        // Top and right caption insets stay equal. Resize padding must not add a larger right gap.
        var inset = MainWindowChrome.TitleBarInsetDip;
        WindowControlCapsule.VerticalAlignment = VAlign.Top;
        WindowControlCapsule.Margin = new Thickness(0, inset, inset, 0);
        CustomTitleBar.Padding = default;

        if (_resizeGrips is not null)
        {
            var resizing = shape.TransparentSurface && WindowState == WindowState.Normal && CanResize;
            _resizeGrips.IsVisible = resizing;
        }

        ApplyOuterWindowShape();
    }

    private void CreateLinuxResizeGrips()
    {
        // WindowDecorations.None removes the X11 resize border, so the grips call BeginResizeDrag directly.
        var layer = new Grid { Name = "WindowResizeGrips", IsVisible = false };
        WindowChromeHost.Children.Add(layer);
        _resizeGrips = layer;

        void Add(WindowEdge edge, HAlign horizontal, VAlign vertical,
            double width, double height, StandardCursorType cursor)
        {
            var grip = new Border
            {
                Background = Brushes.Transparent,
                HorizontalAlignment = horizontal,
                VerticalAlignment = vertical,
                Cursor = new Cursor(cursor),
                Tag = edge
            };
            if (!double.IsNaN(width)) grip.Width = width;
            if (!double.IsNaN(height)) grip.Height = height;
            grip.PointerPressed += ResizeGrip_PointerPressed;
            layer.Children.Add(grip);
        }

        const double edge = MainWindowChrome.ResizeGripDip;
        const double inset = MainWindowChrome.TitleBarInsetDip;
        Add(WindowEdge.North, HAlign.Stretch, VAlign.Top, double.NaN, inset, StandardCursorType.TopSide);
        Add(WindowEdge.South, HAlign.Stretch, VAlign.Bottom, double.NaN, edge, StandardCursorType.BottomSide);
        Add(WindowEdge.West, HAlign.Left, VAlign.Stretch, edge, double.NaN, StandardCursorType.LeftSide);
        Add(WindowEdge.East, HAlign.Right, VAlign.Stretch, inset, double.NaN, StandardCursorType.RightSide);
        Add(WindowEdge.NorthWest, HAlign.Left, VAlign.Top, edge, inset, StandardCursorType.TopLeftCorner);
        Add(WindowEdge.NorthEast, HAlign.Right, VAlign.Top, inset, inset, StandardCursorType.TopRightCorner);
        Add(WindowEdge.SouthWest, HAlign.Left, VAlign.Bottom, edge, edge, StandardCursorType.BottomLeftCorner);
        Add(WindowEdge.SouthEast, HAlign.Right, VAlign.Bottom, edge, edge, StandardCursorType.BottomRightCorner);
    }

    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal || !CanResize) return;
        if (sender is not Control { Tag: WindowEdge edge }) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginResizeDrag(edge, e);
        e.Handled = true;
    }
}
