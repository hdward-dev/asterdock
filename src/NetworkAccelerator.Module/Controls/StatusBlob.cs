using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// Pale organic shape behind the start button. A reused transform drifts it while running.
/// </summary>
public class StatusBlob : Control
{
    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<StatusBlob, bool>(nameof(IsRunning));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<StatusBlob, IBrush?>(nameof(FillBrush));

    public static readonly StyledProperty<IBrush?> TintBrushProperty =
        AvaloniaProperty.Register<StatusBlob, IBrush?>(nameof(TintBrush));

    private readonly StreamGeometry _shape;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly RotateTransform _rotate = new();
    private readonly TranslateTransform _translate = new();
    private readonly TransformGroup _motion;
    private double _state;
    private double _stateFrom;
    private double _stateTo;
    private double _stateElapsed;
    private bool _stateAnimating;
    private double _drift;
    private bool _clocked;
    private bool _attached;
    private IBrush _fill = Brushes.Transparent;
    private IBrush _tint = Brushes.DodgerBlue;

    static StatusBlob()
    {
        AffectsRender<StatusBlob>(IsRunningProperty, FillBrushProperty, TintBrushProperty);
        IsRunningProperty.Changed.AddClassHandler<StatusBlob>((blob, _) => blob.OnRunningChanged());
    }

    public StatusBlob()
    {
        IsHitTestVisible = false;
        _shape = CreateShape();
        _motion = new TransformGroup { Children = { _scale, _rotate, _translate } };
        RenderTransform = _motion;
        RenderTransformOrigin = new RelativePoint(0.62, 0.48, RelativeUnit.Relative);
    }

    public bool IsRunning
    {
        get => GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public IBrush? TintBrush
    {
        get => GetValue(TintBrushProperty);
        set => SetValue(TintBrushProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ActualThemeVariantChanged += (_, _) => CacheBrushes();
        DashboardMotion.Changed += OnMotionSetting;
        CacheBrushes();
        _state = IsRunning ? 1 : 0;
        ApplyPose();
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
        context.DrawGeometry(FillBrush ?? _fill, null, _shape);
        if (_state > 0.01)
        {
            using (context.PushOpacity(0.10 * Math.Clamp(_state, 0, 1)))
                context.DrawGeometry(TintBrush ?? _tint, null, _shape);
        }
    }

    private void OnRunningChanged()
    {
        if (!_attached)
        {
            _state = IsRunning ? 1 : 0;
            ApplyPose();
            return;
        }

        BeginState(IsRunning ? 1 : 0);
    }

    private void OnMotionSetting()
    {
        if (!DashboardMotion.ReduceMotion) return;
        _stateAnimating = false;
        _state = IsRunning ? 1 : 0;
        _drift = 0;
        ApplyPose();
        EnsureClock();
    }

    private void BeginState(double target)
    {
        if (DashboardMotion.ReduceMotion || Math.Abs(_state - target) < 0.001)
        {
            _state = target;
            _stateAnimating = false;
            ApplyPose();
            InvalidateVisual();
            EnsureClock();
            return;
        }

        _stateFrom = _state;
        _stateTo = target;
        _stateElapsed = 0;
        _stateAnimating = true;
        EnsureClock();
    }

    private void Tick(TimeSpan elapsed)
    {
        var ms = elapsed.TotalMilliseconds;
        if (_stateAnimating)
        {
            _stateElapsed += ms;
            var t = Math.Clamp(_stateElapsed / DashboardMotion.PowerMorph.TotalMilliseconds, 0, 1);
            var eased = _stateTo > _stateFrom ? EaseOutBack(t) : t * t * t;
            _state = _stateFrom + (_stateTo - _stateFrom) * eased;
            if (t >= 1)
            {
                _state = _stateTo;
                _stateAnimating = false;
            }

            InvalidateVisual();
        }

        if (IsRunning && !DashboardMotion.ReduceMotion)
            _drift = (_drift + ms / DashboardMotion.BlobDrift.TotalMilliseconds) % 1;

        ApplyPose();
        EnsureClock();
    }

    private void ApplyPose()
    {
        var wave = IsRunning && !DashboardMotion.ReduceMotion ? Math.Sin(_drift * Math.PI * 2) : 0;
        var cross = IsRunning && !DashboardMotion.ReduceMotion ? Math.Cos(_drift * Math.PI * 2) : 0;
        var settle = Math.Clamp(_state, 0, 1.08);
        _scale.ScaleX = _scale.ScaleY = 1 + 0.08 * settle + 0.012 * wave;
        _rotate.Angle = 8 * settle + 4 * wave;
        _translate.X = 3 * cross * settle;
        _translate.Y = 2 * wave * settle;
    }

    private bool NeedsClock() =>
        _stateAnimating || (IsRunning && !DashboardMotion.ReduceMotion);

    private void EnsureClock()
    {
        var need = _attached && NeedsClock();
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

    private void CacheBrushes()
    {
        if (FillBrush is null && TryGetResource("AppSubtleBrush", ActualThemeVariant, out var fill) && fill is IBrush fillBrush)
            _fill = fillBrush;
        if (TintBrush is null && TryGetResource("AppAccentBrush", ActualThemeVariant, out var tint) && tint is IBrush tintBrush)
            _tint = tintBrush;
        InvalidateVisual();
    }

    private static StreamGeometry CreateShape()
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(new Point(28, 78), true);
        ctx.CubicBezierTo(new Point(6, 48), new Point(18, 8), new Point(72, 16));
        ctx.CubicBezierTo(new Point(112, 2), new Point(168, 10), new Point(186, 48));
        ctx.CubicBezierTo(new Point(206, 78), new Point(188, 118), new Point(140, 112));
        ctx.CubicBezierTo(new Point(96, 132), new Point(48, 118), new Point(28, 78));
        ctx.EndFigure(true);
        return geometry;
    }

    private static double EaseOutBack(double t)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }
}
