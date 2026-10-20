using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// Neutral panel anchored to the status card's right edge.
/// The left edge is one smooth curve through a traced outline: a broad upper bulge,
/// a nearly straight middle, and a single turn into the bottom-right corner.
/// </summary>
public class StatusBlob : Control
{
    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<StatusBlob, IBrush?>(nameof(FillBrush));

    // Edge x at fraction of card width, y at fraction of card height from the top.
    private static readonly Point[] Edge =
    [
        new(0.650, 0.00),
        new(0.652, 0.09),
        new(0.640, 0.17),
        new(0.622, 0.26),
        new(0.631, 0.35),
        new(0.650, 0.43),
        new(0.648, 0.54),
        new(0.655, 0.61),
        new(0.664, 0.65),
        new(0.692, 0.69),
        new(0.710, 0.72),
        new(0.723, 0.80),
        new(0.745, 0.87),
        new(0.827, 0.91),
        new(0.844, 0.94),
        new(0.970, 0.98)
    ];

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
        if (FillBrush is null && TryGetResource("AppPageBrush", ActualThemeVariant, out var fill) && fill is IBrush fillBrush)
            _fill = fillBrush;
        InvalidateVisual();
    }

    private static StreamGeometry CreateShape(double width, double height)
    {
        var points = new Point[Edge.Length];
        for (var i = 0; i < Edge.Length; i++)
            points[i] = new Point(Edge[i].X * width, Edge[i].Y * height);

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(new Point(width + 3, -3), true);
        ctx.LineTo(new Point(points[0].X, -3));
        ctx.LineTo(points[0]);
        // Uniform Catmull-Rom, converted to cubics. End knots are repeated so the
        // curve leaves the top edge and arrives at the bottom without an extra bulge.
        for (var i = 0; i < points.Length - 1; i++)
        {
            var p0 = points[Math.Max(i - 1, 0)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(i + 2, points.Length - 1)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6d, p1.Y + (p2.Y - p0.Y) / 6d);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6d, p2.Y - (p3.Y - p1.Y) / 6d);
            ctx.CubicBezierTo(c1, c2, p2);
        }

        ctx.LineTo(new Point(points[^1].X, height + 3));
        ctx.LineTo(new Point(width + 3, height + 3));
        ctx.EndFigure(true);
        return geometry;
    }
}
