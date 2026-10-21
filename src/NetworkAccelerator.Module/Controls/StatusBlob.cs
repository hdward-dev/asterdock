using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// Fixed 12-lobe cookie behind the start button. Tip diameter matches FlClash's
/// standard-desktop decoration (290 on a 104px button). The curve's centre stays
/// on the button's horizontal centre and sits 46×88/104 px above it. While the
/// service is running the curve turns one sector and grows to 1.12 about that
/// centre. The button's press scale does not move it.
/// </summary>
public class StatusBlob : Control
{
    /// <summary>FlClash standard-desktop decoration box divided by its start button.</summary>
    public const double OuterToButton = 290d / 104d;

    public const double OuterDiameter = DashboardMotion.PowerDiameter * OuterToButton;
    public const double MeanDiameter = OuterDiameter / (1d + Amplitude);

    /// <summary>How far the cookie centre sits above the button centre. 46px on FlClash's 104px button.</summary>
    public const double CenterRise = 46d * DashboardMotion.PowerDiameter / 104d;

    /// <summary>Paint slot. Large enough for the ease-out-back overshoot past scale 1.12.</summary>
    public const double BoxSize = 284;

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<StatusBlob, IBrush?>(nameof(FillBrush));

    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<StatusBlob, bool>(nameof(IsRunning));

    private const int Lobes = 12;
    private const int SamplesPerLobe = 8;
    private const double Amplitude = 0.06;
    private const double Phase = 0.525;

    private static readonly StreamGeometry Shape = CreateShape();
    private IBrush _fill = Brushes.Transparent;
    private bool _aligning;
    private bool _attached;
    private bool _clocked;
    private bool _animating;
    private double _progress;
    private double _from;
    private double _to;
    private double _elapsed;

    static StatusBlob()
    {
        AffectsRender<StatusBlob>(FillBrushProperty);
    }

    public StatusBlob()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
        UseLayoutRounding = false;
        Width = BoxSize;
        Height = BoxSize;
    }

    /// <summary>0 stopped, 1 running. Ease-out-back may step slightly outside that range.</summary>
    public double Pose => _progress;

    /// <summary>Bounds of the curve around the origin. Empty until the geometry is realized.</summary>
    public static Rect GeometryBounds => Shape.Bounds;

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    /// <summary>True once the service is up. Starting and stopping follow this flag, not the busy spinner.</summary>
    public bool IsRunning
    {
        get => GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsRunningProperty)
            GoTo(IsRunning ? 1 : 0, animate: _attached);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _progress = IsRunning ? 1 : 0;
        _animating = false;
        ActualThemeVariantChanged += (_, _) => CacheBrush();
        LayoutUpdated += OnLayoutUpdated;
        DashboardMotion.Changed += OnMotionSetting;
        CacheBrush();
        AlignToButton();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _animating = false;
        LayoutUpdated -= OnLayoutUpdated;
        DashboardMotion.Changed -= OnMotionSetting;
        ReleaseClock();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2) return;
        var origin = new Point(size.Width / 2d, size.Height / 2d);
        var scale = 1 + DashboardMotion.CookieGrowth * _progress;
        var matrix = Matrix.CreateScale(scale, scale)
            * Matrix.CreateRotation(DashboardMotion.CookieTurn * _progress)
            * Matrix.CreateTranslation(origin.X, origin.Y);
        using (context.PushTransform(matrix))
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
        center = new Point(center.X, center.Y - CenterRise);
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

    private void OnMotionSetting()
    {
        if (!DashboardMotion.ReduceMotion) return;
        _animating = false;
        _progress = IsRunning ? 1 : 0;
        InvalidateVisual();
        ReleaseClock();
    }

    private void GoTo(double target, bool animate)
    {
        if (!animate || DashboardMotion.ReduceMotion || Math.Abs(_progress - target) < 0.0005)
        {
            _progress = target;
            _animating = false;
            InvalidateVisual();
            ReleaseClock();
            return;
        }

        _from = _progress;
        _to = target;
        _elapsed = 0;
        _animating = true;
        EnsureClock();
        InvalidateVisual();
    }

    private void Tick(TimeSpan elapsed)
    {
        if (!_animating)
        {
            ReleaseClock();
            return;
        }

        _elapsed += elapsed.TotalMilliseconds;
        var t = Math.Clamp(_elapsed / DashboardMotion.CookiePose.TotalMilliseconds, 0, 1);
        _progress = _from + (_to - _from) * DashboardMotion.CookieEase(t);
        if (t >= 1)
        {
            _progress = _to;
            _animating = false;
            ReleaseClock();
        }

        InvalidateVisual();
    }

    private void EnsureClock()
    {
        if (!_attached || !_animating || _clocked) return;
        _clocked = true;
        MotionClock.Subscribe(Tick);
    }

    private void ReleaseClock()
    {
        if (!_clocked) return;
        _clocked = false;
        MotionClock.Unsubscribe(Tick);
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
