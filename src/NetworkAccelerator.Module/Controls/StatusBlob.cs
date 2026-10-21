using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// Fixed 12-lobe cookie behind the start button. The mean circle is 176px
/// across (lobes reach about 187px) and is never scaled with the card.
/// The control sits in a canvas and is moved so its centre matches the button.
/// </summary>
public class StatusBlob : Control
{
    public const double MeanDiameter = 176;
    public const double BoxSize = 190;

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<StatusBlob, IBrush?>(nameof(FillBrush));

    private const int Lobes = 12;
    private const int SamplesPerLobe = 8;
    private const double Amplitude = 0.06;
    private const double Phase = 0.525;

    private readonly StreamGeometry _shape = CreateShape();
    private IBrush _fill = Brushes.Transparent;
    private bool _aligning;

    static StatusBlob()
    {
        AffectsRender<StatusBlob>(FillBrushProperty);
    }

    public StatusBlob()
    {
        IsHitTestVisible = false;
        Width = BoxSize;
        Height = BoxSize;
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
        LayoutUpdated += OnLayoutUpdated;
        CacheBrush();
        AlignToButton();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2) return;
        var origin = new Point(size.Width / 2d, size.Height / 2d);
        using (context.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y)))
            context.DrawGeometry(FillBrush ?? _fill, null, _shape);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => AlignToButton();

    private void AlignToButton()
    {
        if (_aligning || Parent is not Canvas canvas || Bounds.Width < 1) return;
        var button = canvas.GetVisualParent() is not Visual card
            ? null
            : card.GetVisualDescendants().OfType<PowerButton>().FirstOrDefault();
        if (button is null || button.Bounds.Width < 1) return;
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2d, button.Bounds.Height / 2d), canvas);
        if (center is null) return;
        var left = center.Value.X - Bounds.Width / 2d;
        var top = center.Value.Y - Bounds.Height / 2d;
        var currentLeft = Canvas.GetLeft(this);
        var currentTop = Canvas.GetTop(this);
        if (!double.IsNaN(currentLeft) && !double.IsNaN(currentTop)
            && Math.Abs(currentLeft - left) < 0.5 && Math.Abs(currentTop - top) < 0.5)
            return;
        _aligning = true;
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        _aligning = false;
    }

    private void CacheBrush()
    {
        if (FillBrush is null && TryGetResource("AppCookieBrush", ActualThemeVariant, out var fill) && fill is IBrush fillBrush)
            _fill = fillBrush;
        InvalidateVisual();
    }

    private static StreamGeometry CreateShape()
    {
        var count = Lobes * SamplesPerLobe;
        var radius = MeanDiameter / 2d;
        var points = new Point[count];
        for (var i = 0; i < count; i++)
        {
            var theta = -Math.PI + 2d * Math.PI * i / count;
            var rho = 1d + Amplitude * Math.Cos(Lobes * (theta + Phase));
            points[i] = new Point(radius * rho * Math.Cos(theta), radius * rho * Math.Sin(theta));
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
