using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NetworkAccelerator.Module.Controls;

/// <summary>Four-lobed squircle used for the country mark.</summary>
public class CloverBadge : Decorator
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<CloverBadge, IBrush?>(nameof(Fill));

    private StreamGeometry? _shape;
    private Size _builtFor;

    static CloverBadge()
    {
        AffectsRender<CloverBadge>(FillProperty);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2) return;
        if (_shape is null || _builtFor != size)
        {
            _builtFor = size;
            _shape = Create(size.Width, size.Height);
        }

        context.DrawGeometry(Fill ?? Brushes.Transparent, null, _shape);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is null) return finalSize;
        var insetX = finalSize.Width * 0.22;
        var insetY = finalSize.Height * 0.18;
        Child.Arrange(new Rect(insetX, insetY, Math.Max(0, finalSize.Width - insetX * 2), Math.Max(0, finalSize.Height - insetY * 2)));
        return finalSize;
    }

    private static StreamGeometry Create(double width, double height)
    {
        const int count = 12;
        var points = new Point[count];
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)count * Math.PI * 2;
            var lobe = 1 + 0.09 * Math.Cos(4 * t);
            var c = Math.Cos(t);
            var s = Math.Sin(t);
            var x = Math.Sign(c) * Math.Pow(Math.Abs(c), 0.55) * lobe;
            var y = Math.Sign(s) * Math.Pow(Math.Abs(s), 0.55) * lobe;
            points[i] = new Point((x * 0.46 + 0.5) * width, (y * 0.46 + 0.5) * height);
        }

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(points[0], true);
        for (var i = 0; i < points.Length; i++)
        {
            var p0 = points[(i - 1 + points.Length) % points.Length];
            var p1 = points[i];
            var p2 = points[(i + 1) % points.Length];
            var p3 = points[(i + 2) % points.Length];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6d, p1.Y + (p2.Y - p0.Y) / 6d);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6d, p2.Y - (p3.Y - p1.Y) / 6d);
            ctx.CubicBezierTo(c1, c2, p2);
        }

        ctx.EndFigure(true);
        return geometry;
    }
}
