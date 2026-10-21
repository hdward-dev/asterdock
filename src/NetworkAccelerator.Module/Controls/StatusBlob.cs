using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// Fixed 12-lobe cookie behind the start button. The lobe tips span the same
/// multiple of the button as FlClash's idle cookie on the standard desktop
/// card (decoration box 290, button 104). The control sits in a canvas and is
/// placed so the curve's centre, which is the control's centre, is the button's
/// layout centre. A press scale drawn inside the button does not move it.
/// </summary>
public class StatusBlob : Control
{
    /// <summary>FlClash standard-desktop decoration box divided by its start button.</summary>
    public const double OuterToButton = 290d / 104d;

    public const double OuterDiameter = DashboardMotion.PowerDiameter * OuterToButton;
    public const double MeanDiameter = OuterDiameter / (1d + Amplitude);
    public const double BoxSize = 250;

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<StatusBlob, IBrush?>(nameof(FillBrush));

    private const int Lobes = 12;
    private const int SamplesPerLobe = 8;
    private const double Amplitude = 0.06;
    private const double Phase = 0.525;

    private static readonly StreamGeometry Shape = CreateShape();
    private IBrush _fill = Brushes.Transparent;
    private bool _aligning;

    static StatusBlob()
    {
        AffectsRender<StatusBlob>(FillBrushProperty);
    }

    public StatusBlob()
    {
        IsHitTestVisible = false;
        UseLayoutRounding = false;
        Width = BoxSize;
        Height = BoxSize;
    }

    /// <summary>Bounds of the curve around the origin. Empty until the geometry is realized.</summary>
    public static Rect GeometryBounds => Shape.Bounds;

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
            context.DrawGeometry(FillBrush ?? _fill, null, Shape);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => AlignToButton();

    private void AlignToButton()
    {
        if (_aligning || Parent is not Canvas canvas || Bounds.Width < 1) return;
        var button = canvas.GetVisualParent() is not Visual card
            ? null
            : card.GetVisualDescendants().OfType<PowerButton>().FirstOrDefault();
        if (button is null || button.Bounds.Width < 1) return;

        // Layout slots only. The button's press scale and shake are render
        // transforms (or drawn inside the face) and must not drag the cookie.
        var center = LayoutPoint(button, new Point(button.Bounds.Width / 2d, button.Bounds.Height / 2d), canvas);
        var left = center.X - Bounds.Width / 2d;
        var top = center.Y - Bounds.Height / 2d;
        var currentLeft = Canvas.GetLeft(this);
        var currentTop = Canvas.GetTop(this);
        if (double.IsNaN(currentLeft) || double.IsNaN(currentTop)
            || Math.Abs(currentLeft - left) > 0.01 || Math.Abs(currentTop - top) > 0.01)
        {
            _aligning = true;
            Canvas.SetLeft(this, left);
            Canvas.SetTop(this, top);
            _aligning = false;
            return;
        }

        var dx = center.X - (Bounds.X + Bounds.Width / 2d);
        var dy = center.Y - (Bounds.Y + Bounds.Height / 2d);
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
        {
            if (RenderTransform is not null) RenderTransform = null;
            return;
        }

        if (RenderTransform is TranslateTransform shift
            && Math.Abs(shift.X - dx) < 0.01 && Math.Abs(shift.Y - dy) < 0.01)
            return;
        RenderTransform = new TranslateTransform(dx, dy);
    }

    /// <summary>
    /// Maps a point in <paramref name="visual"/> into <paramref name="space"/> using
    /// layout bounds, skipping render transforms on either control.
    /// </summary>
    private static Point LayoutPoint(Visual visual, Point point, Visual space)
    {
        var ancestor = CommonAncestor(visual, space);
        var from = OffsetTo(visual, point, ancestor);
        var origin = OffsetTo(space, new Point(0, 0), ancestor);
        return new Point(from.X - origin.X, from.Y - origin.Y);
    }

    private static Visual CommonAncestor(Visual left, Visual right)
    {
        var seen = new HashSet<Visual>();
        for (Visual? node = left; node is not null; node = node.GetVisualParent())
            seen.Add(node);
        for (Visual? node = right; node is not null; node = node.GetVisualParent())
        {
            if (seen.Contains(node)) return node;
        }

        return left;
    }

    private static Point OffsetTo(Visual from, Point point, Visual ancestor)
    {
        for (var node = from; node != ancestor; node = node.GetVisualParent() ?? ancestor)
            point = new Point(point.X + node.Bounds.X, point.Y + node.Bounds.Y);
        return point;
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
