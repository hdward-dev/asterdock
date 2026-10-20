using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// Neutral scalloped panel anchored to the status card's right edge.
/// The right, top-right, and bottom-right stay pinned; only the inner lobes drift.
/// </summary>
public class StatusBlob : Control
{
    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<StatusBlob, IBrush?>(nameof(FillBrush));

    private const double DriftPeriodMs = 8000;
    private const double DriftAmplitude = 0.008;

    private StreamGeometry? _shape;
    private Size _builtFor;
    private double _builtPhase = -1;
    private double _phase;
    private bool _clocked;
    private bool _attached;
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
        _attached = true;
        ActualThemeVariantChanged += (_, _) => CacheBrush();
        DashboardMotion.Changed += OnMotionSetting;
        CacheBrush();
        EnsureClock();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        DashboardMotion.Changed -= OnMotionSetting;
        if (_clocked)
        {
            _clocked = false;
            MotionClock.Unsubscribe(Tick);
        }

        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2) return;
        var phase = DashboardMotion.ReduceMotion ? 0 : _phase;
        if (_shape is null || _builtFor != size || Math.Abs(_builtPhase - phase) > 0.0008)
        {
            _builtFor = size;
            _builtPhase = phase;
            _shape = CreateShape(size.Width, size.Height, phase);
        }

        context.DrawGeometry(FillBrush ?? _fill, null, _shape);
    }

    private void OnMotionSetting()
    {
        if (!DashboardMotion.ReduceMotion) return;
        _phase = 0;
        InvalidateVisual();
        EnsureClock();
    }

    private void Tick(TimeSpan elapsed)
    {
        _phase = (_phase + elapsed.TotalMilliseconds / DriftPeriodMs) % 1;
        InvalidateVisual();
    }

    private bool NeedsClock() => _attached && !DashboardMotion.ReduceMotion;

    private void EnsureClock()
    {
        var need = NeedsClock();
        if (need && !_clocked)
        {
            _clocked = true;
            MotionClock.Subscribe(Tick);
        }
        else if (!need && _clocked)
        {
            _clocked = false;
            MotionClock.Unsubscribe(Tick);
        }
    }

    private void CacheBrush()
    {
        if (FillBrush is null && TryGetResource("AppPageBrush", ActualThemeVariant, out var fill) && fill is IBrush fillBrush)
            _fill = fillBrush;
        InvalidateVisual();
    }

    private static StreamGeometry CreateShape(double width, double height, double phase)
    {
        // Fractions of the card. The first and last points stay pinned so the
        // top-right and bottom-right corners remain filled. Interior lobes drift.
        var lobes = new (double X, double Y, double Shift)[]
        {
            (0.635, 0.00, 0.0),
            (0.598, 0.10, 1.0),
            (0.735, 0.21, 1.6),
            (0.655, 0.33, 2.2),
            (0.745, 0.43, 2.7),
            (0.628, 0.53, 3.3),
            (0.750, 0.64, 3.8),
            (0.700, 0.74, 4.3),
            (0.770, 0.83, 4.8),
            (0.715, 0.92, 5.2),
            (0.700, 1.00, 0.0)
        };

        var points = new Point[lobes.Length];
        for (var i = 0; i < lobes.Length; i++)
        {
            var lobe = lobes[i];
            var shift = lobe.Shift == 0 ? 0 : Math.Sin(phase * Math.PI * 2 + lobe.Shift) * DriftAmplitude;
            points[i] = new Point((lobe.X + shift) * width, lobe.Y * height);
        }

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(new Point(width + 3, -3), true);
        ctx.LineTo(new Point(points[0].X, -3));
        ctx.LineTo(points[0]);
        for (var i = 0; i < points.Length - 1; i++)
        {
            var p1 = points[i];
            var p2 = points[i + 1];
            var dy = (p2.Y - p1.Y) / 3d;
            ctx.CubicBezierTo(new Point(p1.X, p1.Y + dy), new Point(p2.X, p2.Y - dy), p2);
        }

        ctx.LineTo(new Point(points[^1].X, height + 3));
        ctx.LineTo(new Point(width + 3, height + 3));
        ctx.EndFigure(true);
        return geometry;
    }
}
