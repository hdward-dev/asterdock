using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// 52 by 32 switch with a 24px thumb. The thumb eases across the track in 200ms.
/// </summary>
public class AccentSwitch : Control
{
    public const double TrackWidth = 52;
    public const double TrackHeight = 32;
    private const double ThumbSize = 24;
    private const double ThumbInset = 4;

    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<AccentSwitch, bool>(nameof(IsChecked));

    public static readonly StyledProperty<double> ThumbPositionProperty =
        AvaloniaProperty.Register<AccentSwitch, double>(nameof(ThumbPosition));

    public static readonly RoutedEvent<RoutedEventArgs> ClickEvent =
        RoutedEvent.Register<AccentSwitch, RoutedEventArgs>(nameof(Click), RoutingStrategies.Bubble);

    private IBrush _off = Brushes.Gray;
    private IBrush _on = Brushes.DodgerBlue;
    private bool _brushesReady;
    private double _thumbFrom;
    private double _thumbTo;
    private double _thumbElapsed;
    private bool _thumbMoving;

    static AccentSwitch()
    {
        FocusableProperty.OverrideDefaultValue<AccentSwitch>(true);
        AffectsRender<AccentSwitch>(IsCheckedProperty, ThumbPositionProperty);
        IsCheckedProperty.Changed.AddClassHandler<AccentSwitch>((control, _) => control.SyncThumb(animate: true));
    }

    public AccentSwitch()
    {
        Width = TrackWidth;
        Height = TrackHeight;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public double ThumbPosition
    {
        get => GetValue(ThumbPositionProperty);
        set => SetValue(ThumbPositionProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? Click
    {
        add => AddHandler(ClickEvent, value);
        remove => RemoveHandler(ClickEvent, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeChanged;
        CacheBrushes();
        SyncThumb(animate: false);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        StopThumb();
        ThumbPosition = IsChecked ? 1 : 0;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsEnabled) return;
        Toggle();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !IsEnabled) return;
        if (e.Key is not (Key.Space or Key.Enter)) return;
        Toggle();
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        if (!_brushesReady) CacheBrushes();
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;
        var radius = height / 2;
        var track = new RoundedRect(new Rect(0, 0, width, height), radius);
        context.DrawRectangle(_off, null, track);
        var position = Math.Clamp(ThumbPosition, 0, 1);
        if (position > 0)
        {
            using (context.PushOpacity(position))
                context.DrawRectangle(_on, null, track);
        }

        var travel = Math.Max(0, width - ThumbSize - ThumbInset * 2);
        var x = ThumbInset + travel * position;
        var y = (height - ThumbSize) / 2;
        context.DrawEllipse(Brushes.White, null, new Rect(x, y, ThumbSize, ThumbSize));
    }

    private void Toggle()
    {
        SetCurrentValue(IsCheckedProperty, !IsChecked);
        RaiseEvent(new RoutedEventArgs(ClickEvent));
    }

    private void SyncThumb(bool animate)
    {
        var target = IsChecked ? 1d : 0d;
        if (!animate || DashboardMotion.ReduceMotion || VisualRoot is null)
        {
            StopThumb();
            ThumbPosition = target;
            return;
        }

        _thumbFrom = ThumbPosition;
        _thumbTo = target;
        _thumbElapsed = 0;
        if (_thumbMoving) return;
        _thumbMoving = true;
        MotionClock.Subscribe(MoveThumb);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => CacheBrushes();

    private void MoveThumb(TimeSpan elapsed)
    {
        _thumbElapsed += elapsed.TotalMilliseconds;
        var t = Math.Clamp(_thumbElapsed / DashboardMotion.Toggle.TotalMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        ThumbPosition = _thumbFrom + (_thumbTo - _thumbFrom) * eased;
        if (t < 1) return;
        ThumbPosition = _thumbTo;
        StopThumb();
    }

    private void StopThumb()
    {
        if (!_thumbMoving) return;
        _thumbMoving = false;
        MotionClock.Unsubscribe(MoveThumb);
    }

    private void CacheBrushes()
    {
        _off = Brush("AppMutedBrush", Brushes.Gray);
        _on = Brush("AppAccentBrush", Brushes.DodgerBlue);
        _brushesReady = true;
        InvalidateVisual();
    }

    private IBrush Brush(string key, IBrush fallback) =>
        TryGetResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
}
