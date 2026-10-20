using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// Scalloped cookie behind the status button. A 12-lobe radial curve,
/// r(θ) = R(1 + 0.06·cos(12θ)), fitted to the reference mask and clipped
/// by the card. The center sits in the upper right; the card keeps the
/// bottom-right corner.
/// </summary>
public class StatusBlob : Control
{
    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<StatusBlob, IBrush?>(nameof(FillBrush));

    // Card fractions. Axes are a circle on the reference card (764×270).
    private const int Lobes = 12;
    private const int SamplesPerLobe = 8;
    private const double Amplitude = 0.06;
    private const double CenterX = 0.8621;
    private const double CenterY = 0.277;
    private const double RadiusX = 0.2308;
    private const double RadiusY = 0.6496;
    private const double Phase = 0.525;

    private StreamGeometry? _shape;
    private Size _builtFor;
    private IBrush _fill = Brushes.Transparent;

    static StatusBlob()
    {
        AffectsRender<StatusBlob>(FillBrushProperty);
    }

    public StatusBlob()
    {
        IsHitTestVisible = false;
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += (_, _) => CacheBrush();
        CacheBrush();
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2) return;
        if (_shape is null || _builtFor != size)
        {
            _builtFor = size;
            _shape = CreateShape(size.Width, size.Height);
        }

        context.DrawGeometry(FillBrush ?? _fill, null, _shape);
    }

    private void CacheBrush()
    {
        if (FillBrush is null && TryGetResource("AppCookieBrush", ActualThemeVariant, out var fill) && fill is IBrush fillBrush)
            _fill = fillBrush;
        InvalidateVisual();
    }

    private static StreamGeometry CreateShape(double width, double height)
    {
        var count = Lobes * SamplesPerLobe;
        var points = new Point[count];
        for (var i = 0; i < count; i++)
        {
            var theta = -Math.PI + 2d * Math.PI * i / count;
            var rho = 1d + Amplitude * Math.Cos(Lobes * (theta + Phase));
            points[i] = new Point(
                (CenterX + RadiusX * rho * Math.Cos(theta)) * width,
                (CenterY + RadiusY * rho * Math.Sin(theta)) * height);
        }

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(points[0], true);
        for (var i = 0; i < count; i++)
        {
            var p0 = points[(i - 1 + count) % count];
            var p1 = points[i];
            var p2 = points[(i + 1) % count];
            var p3 = points[(i + 2) % count];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6d, p1.Y + (p2.Y - p0.Y) / 6d);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6d, p2.Y - (p3.Y - p1.Y) / 6d);
            ctx.CubicBezierTo(c1, c2, p2);
        }

        ctx.EndFigure(true);
        return geometry;
    }
}
