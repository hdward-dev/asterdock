using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// Refresh glyph that turns once per second while a subscription update is running.
/// The angle returns to 0 when the update ends, and it stays still under 减少动画.
/// </summary>
public class RefreshSpin : Avalonia.Controls.Shapes.Path
{
    public static readonly StyledProperty<bool> SpinningProperty =
        AvaloniaProperty.Register<RefreshSpin, bool>(nameof(Spinning));

    private readonly RotateTransform _rotate = new();
    private bool _hooked;

    public RefreshSpin()
    {
        RenderTransform = _rotate;
        RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
    }

    public bool Spinning
    {
        get => GetValue(SpinningProperty);
        set => SetValue(SpinningProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SpinningProperty) UpdateSpin();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_hooked) return;
        _hooked = true;
        DashboardMotion.Changed += OnMotionChanged;
        UpdateSpin();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_hooked)
        {
            _hooked = false;
            DashboardMotion.Changed -= OnMotionChanged;
            MotionClock.Unsubscribe(OnTick);
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnMotionChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnMotionChanged);
            return;
        }

        if (DashboardMotion.ReduceMotion) _rotate.Angle = 0;
        UpdateSpin();
    }

    private void UpdateSpin()
    {
        if (Spinning && !DashboardMotion.ReduceMotion)
        {
            MotionClock.Subscribe(OnTick);
            return;
        }

        MotionClock.Unsubscribe(OnTick);
        _rotate.Angle = 0;
    }

    private void OnTick(TimeSpan elapsed)
    {
        if (!Spinning || DashboardMotion.ReduceMotion)
        {
            _rotate.Angle = 0;
            MotionClock.Unsubscribe(OnTick);
            return;
        }

        _rotate.Angle = (_rotate.Angle + elapsed.TotalSeconds * 360d) % 360d;
    }
}
