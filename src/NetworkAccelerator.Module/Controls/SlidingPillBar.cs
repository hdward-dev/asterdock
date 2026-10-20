using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// Paints equal segment tracks and a selected pill that slides in 250ms.
/// Place it behind the segment buttons; it does not host them.
/// </summary>
public class SlidingPillBar : Control
{
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<SlidingPillBar, int>(nameof(SelectedIndex));

    public static readonly StyledProperty<int> SegmentCountProperty =
        AvaloniaProperty.Register<SlidingPillBar, int>(nameof(SegmentCount), 3);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<SlidingPillBar, double>(nameof(Gap), 6);

    public static readonly StyledProperty<double> PillOffsetProperty =
        AvaloniaProperty.Register<SlidingPillBar, double>(nameof(PillOffset));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SlidingPillBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<SlidingPillBar, IBrush?>(nameof(AccentBrush));

    private static readonly IBrush LightTrack = new SolidColorBrush(Color.Parse("#F5F7FA"));
    private static readonly IBrush DarkTrack = new SolidColorBrush(Color.Parse("#161A20"));
    private IBrush _accent = Brushes.DodgerBlue;
    private int _placedIndex = -1;
    private double _placedTarget = double.NaN;
    private double _slideFrom;
    private double _slideTo;
    private double _slideElapsed;
    private bool _sliding;

    static SlidingPillBar()
    {
        AffectsRender<SlidingPillBar>(PillOffsetProperty, SelectedIndexProperty, SegmentCountProperty, GapProperty, TrackBrushProperty, AccentBrushProperty);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public int SegmentCount
    {
        get => GetValue(SegmentCountProperty);
        set => SetValue(SegmentCountProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public double PillOffset
    {
        get => GetValue(PillOffsetProperty);
        set => SetValue(PillOffsetProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += (_, _) => CacheBrushes();
        CacheBrushes();
        MovePill();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedIndexProperty || change.Property == GapProperty || change.Property == SegmentCountProperty || change.Property == BoundsProperty)
            MovePill();
    }

    public override void Render(DrawingContext context)
    {
        var count = Math.Max(1, SegmentCount);
        var gap = Math.Max(0, Gap);
        var segment = Math.Max(0, (Bounds.Width - gap * (count - 1)) / count);
        var height = Bounds.Height;
        if (segment <= 0 || height <= 0) return;
        var radius = height / 2;
        for (var i = 0; i < count; i++)
        {
            var slot = new RoundedRect(new Rect(i * (segment + gap), 0, segment, height), radius);
            context.DrawRectangle(ResolveTrack(), null, slot);
        }

        var pill = new RoundedRect(new Rect(PillOffset, 0, segment, height), radius);
        context.DrawRectangle(AccentBrush ?? _accent, null, pill);
    }

    private void MovePill()
    {
        var count = Math.Max(1, SegmentCount);
        var gap = Math.Max(0, Gap);
        var segment = Math.Max(0, (Bounds.Width - gap * (count - 1)) / count);
        if (segment <= 0) return;
        var index = Math.Clamp(SelectedIndex, 0, count - 1);
        var target = index * (segment + gap);
        var first = _placedIndex < 0;
        var resized = _placedIndex == index;
        if (!first && resized && Math.Abs(_placedTarget - target) < 0.5) return;
        var snap = first || resized || DashboardMotion.ReduceMotion;
        _placedIndex = index;
        _placedTarget = target;
        if (snap)
        {
            StopSlide();
            PillOffset = target;
            return;
        }

        _slideFrom = PillOffset;
        _slideTo = target;
        _slideElapsed = 0;
        if (_sliding) return;
        _sliding = true;
        MotionClock.Subscribe(Slide);
    }

    private void Slide(TimeSpan elapsed)
    {
        _slideElapsed += elapsed.TotalMilliseconds;
        var t = Math.Clamp(_slideElapsed / DashboardMotion.Segment.TotalMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        PillOffset = _slideFrom + (_slideTo - _slideFrom) * eased;
        if (t < 1) return;
        PillOffset = _slideTo;
        StopSlide();
    }

    private void StopSlide()
    {
        if (!_sliding) return;
        _sliding = false;
        MotionClock.Unsubscribe(Slide);
    }

    private void CacheBrushes()
    {
        _accent = Brush("AppAccentBrush", Brushes.DodgerBlue);
        InvalidateVisual();
    }

    private IBrush ResolveTrack()
    {
        if (TrackBrush is { } brush) return brush;
        return ActualThemeVariant == ThemeVariant.Dark ? DarkTrack : LightTrack;
    }

    private IBrush Brush(string key, IBrush fallback) =>
        TryGetResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
}
