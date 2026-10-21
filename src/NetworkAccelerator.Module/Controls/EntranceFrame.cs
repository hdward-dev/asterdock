using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// Fades, lifts, and scales a card in, staggered by <see cref="Order"/>.
/// The transform objects are created once and reused while the values change.
/// </summary>
public class EntranceFrame : Decorator
{
    public static readonly StyledProperty<int> OrderProperty =
        AvaloniaProperty.Register<EntranceFrame, int>(nameof(Order));

    public static readonly StyledProperty<double> OffsetYProperty =
        AvaloniaProperty.Register<EntranceFrame, double>(nameof(OffsetY));

    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<EntranceFrame, double>(nameof(Scale), 1);

    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new();
    private readonly TransformGroup _group;
    private bool _played;
    private double _elapsed = double.NaN;

    static EntranceFrame()
    {
        OffsetYProperty.Changed.AddClassHandler<EntranceFrame>((frame, _) => frame.ApplyMotion());
        ScaleProperty.Changed.AddClassHandler<EntranceFrame>((frame, _) => frame.ApplyMotion());
    }

    public EntranceFrame()
    {
        _group = new TransformGroup { Children = { _scale, _translate } };
        RenderTransformOrigin = new RelativePoint(0.5, 0, RelativeUnit.Relative);
    }

    public int Order
    {
        get => GetValue(OrderProperty);
        set => SetValue(OrderProperty, value);
    }

    public double OffsetY
    {
        get => GetValue(OffsetYProperty);
        set => SetValue(OffsetYProperty, value);
    }

    public double Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_played) return;
        _played = true;
        if (DashboardMotion.ReduceMotion)
        {
            Opacity = 1;
            return;
        }

        Opacity = 0;
        OffsetY = DashboardMotion.EntranceOffset;
        Scale = DashboardMotion.EntranceScale;
        _elapsed = -Math.Max(0, Order) * DashboardMotion.StaggerMilliseconds;
        MotionClock.Subscribe(Tick);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        MotionClock.Unsubscribe(Tick);
        _elapsed = double.NaN;
        Opacity = 1;
        OffsetY = 0;
        Scale = 1;
        base.OnDetachedFromVisualTree(e);
    }

    private void Tick(TimeSpan elapsed)
    {
        _elapsed += elapsed.TotalMilliseconds;
        var t = Math.Clamp(_elapsed / DashboardMotion.Entrance.TotalMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        Opacity = eased;
        OffsetY = DashboardMotion.EntranceOffset * (1 - eased);
        Scale = DashboardMotion.EntranceScale + (1 - DashboardMotion.EntranceScale) * eased;
        if (t < 1) return;
        MotionClock.Unsubscribe(Tick);
    }

    private void ApplyMotion()
    {
        _scale.ScaleX = Scale;
        _scale.ScaleY = Scale;
        _translate.Y = OffsetY;
        if (RenderTransform is null) RenderTransform = _group;
    }
}
