using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// 88px accent circle that plays, connects, and stops. Motion is drawn inside a fixed slot
/// so scale, glow, and shake never move surrounding cards.
/// </summary>
public class PowerButton : Panel
{
    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<PowerButton, bool>(nameof(IsRunning));

    public static readonly StyledProperty<bool> IsConnectingProperty =
        AvaloniaProperty.Register<PowerButton, bool>(nameof(IsConnecting));

    public static readonly StyledProperty<int> FaultSignalProperty =
        AvaloniaProperty.Register<PowerButton, int>(nameof(FaultSignal));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<PowerButton, IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<IBrush?> IconBrushProperty =
        AvaloniaProperty.Register<PowerButton, IBrush?>(nameof(IconBrush));

    public static readonly RoutedEvent<RoutedEventArgs> ClickEvent =
        RoutedEvent.Register<PowerButton, RoutedEventArgs>(nameof(Click), RoutingStrategies.Bubble);

    private const double Diameter = DashboardMotion.PowerDiameter;
    private const double GlyphBox = 46;

    // Soft primary glow, stronger below the circle. Avalonia converts
    // BoxShadow.Blur with sigma = 0.288675 * blur + 0.5, so blur 26 is about sigma 8.
    // Key alpha 0.35. Both layers stay outside the circle.
    private const double ShadowKeyOffsetY = 9;
    private const double ShadowKeyBlur = 26;
    private const double ShadowKeyAlpha = 0.35;
    private const double ShadowAmbientOffsetY = 3;
    private const double ShadowAmbientBlur = 14;
    private const double ShadowAmbientAlpha = 0.10;

    private readonly Border _shadowHost;
    private readonly Face _face;
    private readonly ScaleTransform _shadowScale = new(1, 1);
    private readonly TranslateTransform _shake = new();
    private readonly Pen _ringPen = new(Brushes.DodgerBlue, 3, lineCap: PenLineCap.Round);
    private readonly Pen _glowPen = new(Brushes.DodgerBlue, 6, lineCap: PenLineCap.Round);
    private readonly Pen _trackPen = new(Brushes.DodgerBlue, 3);
    private StreamGeometry? _play;
    private StreamGeometry? _stop;
    private StreamGeometry? _arc;
    private double _arcRadius = -1;
    private IBrush _accent = Brushes.DodgerBlue;
    private IBrush _iconBrush = Brushes.White;
    private double _scale = 1;
    private double _scaleFrom = 1;
    private double _scaleTo = 1;
    private double _scaleElapsed;
    private bool _scaleAnimating;
    private double _morph;
    private double _morphFrom;
    private double _morphTo;
    private double _morphElapsed;
    private bool _morphForward = true;
    private bool _morphAnimating;
    private bool _rippleActive;
    private double _ripple;
    private Point _rippleOrigin;
    private double _glowPhase;
    private double _ringTurns;
    private bool _shakeActive;
    private double _shakeElapsed;
    private bool _hovered;
    private bool _pressed;
    private bool _clocked;
    private int _seenFault = -1;
    private bool _attached;

    static PowerButton()
    {
        FocusableProperty.OverrideDefaultValue<PowerButton>(true);
        AffectsRender<PowerButton>(IsRunningProperty, IsConnectingProperty, AccentBrushProperty, IconBrushProperty);
        IsRunningProperty.Changed.AddClassHandler<PowerButton>((button, _) => button.OnRunningChanged());
        FaultSignalProperty.Changed.AddClassHandler<PowerButton>((button, _) => button.OnFaultChanged());
    }

    public PowerButton()
    {
        Width = Diameter;
        Height = Diameter;
        ClipToBounds = false;
        Cursor = new Cursor(StandardCursorType.Hand);
        RenderTransform = _shake;
        _shadowHost = new Border
        {
            CornerRadius = new CornerRadius(Diameter / 2),
            BoxShadow = RestShadow(Colors.DodgerBlue),
            IsHitTestVisible = false,
            ClipToBounds = false,
            RenderTransform = _shadowScale,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative)
        };
        _face = new Face(this);
        Children.Add(_shadowHost);
        Children.Add(_face);
    }

    public bool IsRunning
    {
        get => GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    public bool IsConnecting
    {
        get => GetValue(IsConnectingProperty);
        set => SetValue(IsConnectingProperty, value);
    }

    public int FaultSignal
    {
        get => GetValue(FaultSignalProperty);
        set => SetValue(FaultSignalProperty, value);
    }

    public IBrush? AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public IBrush? IconBrush
    {
        get => GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? Click
    {
        add => AddHandler(ClickEvent, value);
        remove => RemoveHandler(ClickEvent, value);
    }

    /// <summary>Drives the pointer hover scale. Used by the screenshot harness.</summary>
    public void PreviewHover(bool hovered)
    {
        _hovered = hovered;
        if (!_pressed) AnimateScale(hovered ? DashboardMotion.PowerHoverScale : 1);
    }

    /// <summary>Drives the pointer press scale and ripple. Used by the screenshot harness.</summary>
    public void PreviewPress(bool pressed) => PreviewPress(pressed, new Point(Diameter / 2, Diameter / 2));

    private void PreviewPress(bool pressed, Point origin)
    {
        if (pressed)
        {
            _pressed = true;
            _rippleOrigin = origin;
            _ripple = 0;
            _rippleActive = true;
            AnimateScale(DashboardMotion.PowerPressScale);
            EnsureClock();
            return;
        }

        if (!_pressed) return;
        _pressed = false;
        AnimateScale(_hovered ? DashboardMotion.PowerHoverScale : 1);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ActualThemeVariantChanged += (_, _) => CacheBrushes();
        DashboardMotion.Changed += OnMotionSetting;
        CacheBrushes();
        LoadGlyphs();
        _morph = IsRunning ? 1 : 0;
        _seenFault = FaultSignal;
        InvalidateFace();
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsConnectingProperty || change.Property == AccentBrushProperty || change.Property == IconBrushProperty)
        {
            CacheBrushes();
            EnsureClock();
            InvalidateFace();
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var radius = Math.Min(finalSize.Width, finalSize.Height) / 2;
        _shadowHost.CornerRadius = new CornerRadius(radius);
        return base.ArrangeOverride(finalSize);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        PreviewHover(true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        PreviewHover(false);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsConnecting || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        PreviewPress(true, e.GetPosition(this));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        var inside = new Rect(Bounds.Size).Contains(e.GetPosition(this));
        PreviewPress(false);
        e.Pointer.Capture(null);
        if (inside && !IsConnecting)
            RaiseEvent(new RoutedEventArgs(ClickEvent));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsConnecting || e.Key is not (Key.Space or Key.Enter)) return;
        PreviewPress(true, new Point(Diameter / 2, Diameter / 2));
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!_pressed || e.Key is not (Key.Space or Key.Enter)) return;
        PreviewPress(false);
        if (!IsConnecting) RaiseEvent(new RoutedEventArgs(ClickEvent));
        e.Handled = true;
    }

    private void RenderFace(DrawingContext context)
    {
        var center = new Point(_face.Bounds.Width / 2, _face.Bounds.Height / 2);
        var radius = Diameter / 2;
        var drawn = radius * _scale;
        DrawGlow(context, center, drawn);
        var circle = new Rect(center.X - drawn, center.Y - drawn, drawn * 2, drawn * 2);
        context.DrawEllipse(AccentBrush ?? _accent, null, center, drawn, drawn);
        if (_rippleActive)
        {
            var alpha = (1 - Math.Clamp(_ripple, 0, 1)) * 0.34;
            using (context.PushClip(new RoundedRect(circle, drawn)))
            using (context.PushOpacity(alpha))
                context.DrawEllipse(IconBrush ?? _iconBrush, null, _rippleOrigin, _ripple * Diameter, _ripple * Diameter);
        }

        using (context.PushTransform(ScaleAround(center, _scale)))
            DrawIcon(context);
        if (IsConnecting) DrawRing(context, center, radius + 6);
    }

    private void DrawGlow(DrawingContext context, Point center, double radius)
    {
        if (DashboardMotion.ReduceMotion || _morph <= 0.01) return;
        for (var ring = 0; ring < 2; ring++)
        {
            var phase = (_glowPhase + ring * 0.5) % 1;
            var spread = 3 + phase * 22;
            var alpha = (1 - phase) * (1 - phase) * 0.55 * _morph;
            using (context.PushOpacity(alpha))
                context.DrawEllipse(null, _glowPen, center, radius + spread, radius + spread);
        }
    }

    private void DrawIcon(DrawingContext context)
    {
        LoadGlyphs();
        var play = Math.Clamp(1 - _morph, 0, 1);
        var stop = Math.Clamp(_morph, 0, 1);
        if (play > 0.01 && _play is not null) DrawGlyph(context, _play, play);
        if (stop > 0.01 && _stop is not null) DrawGlyph(context, _stop, stop);
    }

    private void DrawGlyph(DrawingContext context, StreamGeometry geometry, double opacity)
    {
        var scale = GlyphBox / 24d;
        var offset = (Diameter - GlyphBox) / 2;
        var matrix = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset, offset);
        using (context.PushOpacity(opacity))
        using (context.PushTransform(matrix))
            context.DrawGeometry(IconBrush ?? _iconBrush, null, geometry);
    }

    private void DrawRing(DrawingContext context, Point center, double radius)
    {
        EnsureArc(radius);
        if (_arc is null) return;
        using (context.PushOpacity(0.18))
            context.DrawEllipse(null, _trackPen, center, radius, radius);
        var angle = DashboardMotion.ReduceMotion ? -Math.PI / 2 : _ringTurns * Math.PI * 2 - Math.PI / 2;
        var transform = Matrix.CreateRotation(angle) * Matrix.CreateTranslation(center.X, center.Y);
        using (context.PushTransform(transform))
            context.DrawGeometry(null, _ringPen, _arc);
    }

    private void EnsureArc(double radius)
    {
        if (_arc is not null && Math.Abs(_arcRadius - radius) < 0.5) return;
        _arcRadius = radius;
        var sweep = 0.72 * Math.PI * 2;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(radius, 0), false);
            ctx.ArcTo(new Point(Math.Cos(sweep) * radius, Math.Sin(sweep) * radius), new Size(radius, radius), 0, true, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }

        _arc = geometry;
    }

    private void OnRunningChanged()
    {
        if (!_attached)
        {
            _morph = IsRunning ? 1 : 0;
            return;
        }

        BeginMorph(IsRunning ? 1 : 0);
    }

    private void OnFaultChanged()
    {
        var next = FaultSignal;
        var previous = _seenFault;
        _seenFault = next;
        if (previous >= 0 && next != previous) BeginShake();
    }

    private void OnMotionSetting()
    {
        if (!DashboardMotion.ReduceMotion) return;
        _scaleAnimating = false;
        _morphAnimating = false;
        _rippleActive = false;
        _shakeActive = false;
        _scale = 1;
        _shadowScale.ScaleX = 1;
        _shadowScale.ScaleY = 1;
        _shake.X = 0;
        _morph = IsRunning ? 1 : 0;
        InvalidateFace();
        EnsureClock();
    }

    private void BeginMorph(double target)
    {
        if (DashboardMotion.ReduceMotion || Math.Abs(_morph - target) < 0.001)
        {
            _morph = target;
            _morphAnimating = false;
            InvalidateFace();
            EnsureClock();
            return;
        }

        _morphFrom = _morph;
        _morphTo = target;
        _morphForward = target > _morphFrom;
        _morphElapsed = 0;
        _morphAnimating = true;
        EnsureClock();
    }

    private void BeginShake()
    {
        if (DashboardMotion.ReduceMotion) return;
        _shakeElapsed = 0;
        _shakeActive = true;
        EnsureClock();
    }

    private void AnimateScale(double target)
    {
        if (DashboardMotion.ReduceMotion)
        {
            _scale = 1;
            _scaleAnimating = false;
            _shadowScale.ScaleX = 1;
            _shadowScale.ScaleY = 1;
            InvalidateFace();
            return;
        }

        _scaleFrom = _scale;
        _scaleTo = target;
        _scaleElapsed = 0;
        _scaleAnimating = Math.Abs(_scale - target) > 0.001;
        if (!_scaleAnimating)
        {
            _shadowScale.ScaleX = _scale;
            _shadowScale.ScaleY = _scale;
        }

        EnsureClock();
        InvalidateFace();
    }

    private void Tick(TimeSpan elapsed)
    {
        var ms = elapsed.TotalMilliseconds;
        if (_scaleAnimating)
        {
            _scaleElapsed += ms;
            var t = Math.Clamp(_scaleElapsed / DashboardMotion.PowerState.TotalMilliseconds, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);
            _scale = _scaleFrom + (_scaleTo - _scaleFrom) * eased;
            if (t >= 1)
            {
                _scale = _scaleTo;
                _scaleAnimating = false;
            }

            _shadowScale.ScaleX = _scale;
            _shadowScale.ScaleY = _scale;
        }

        if (_morphAnimating)
        {
            _morphElapsed += ms;
            var t = Math.Clamp(_morphElapsed / DashboardMotion.PowerMorph.TotalMilliseconds, 0, 1);
            var eased = _morphForward ? EaseOutBack(t) : t * t * t;
            _morph = _morphFrom + (_morphTo - _morphFrom) * eased;
            if (t >= 1)
            {
                _morph = _morphTo;
                _morphAnimating = false;
            }
        }

        if (_rippleActive)
        {
            _ripple += ms / DashboardMotion.PowerRipple.TotalMilliseconds;
            if (_ripple >= 1) _rippleActive = false;
        }

        if (_shakeActive)
        {
            _shakeElapsed += ms;
            var progress = Math.Clamp(_shakeElapsed / DashboardMotion.PowerShake.TotalMilliseconds, 0, 1);
            _shake.X = Math.Sin(progress * Math.PI * 6) * (1 - progress) * 6;
            if (progress >= 1)
            {
                _shakeActive = false;
                _shake.X = 0;
            }
        }

        if (IsConnecting && !DashboardMotion.ReduceMotion)
            _ringTurns = (_ringTurns + ms / DashboardMotion.PowerRing.TotalMilliseconds) % 1;

        if (IsRunning && !DashboardMotion.ReduceMotion)
            _glowPhase = (_glowPhase + ms / DashboardMotion.PowerGlow.TotalMilliseconds) % 1;

        InvalidateFace();
        EnsureClock();
    }

    private bool NeedsClock() =>
        _scaleAnimating || _morphAnimating || _rippleActive || _shakeActive ||
        (!DashboardMotion.ReduceMotion && (IsRunning || IsConnecting));

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
        if (AccentBrush is null && TryGetResource("AppAccentBrush", ActualThemeVariant, out var accent) && accent is IBrush accentBrush)
            _accent = accentBrush;
        if (IconBrush is null && TryGetResource("AppOnAccentBrush", ActualThemeVariant, out var icon) && icon is IBrush iconBrush)
            _iconBrush = iconBrush;
        var paint = AccentBrush ?? _accent;
        if (!ReferenceEquals(_ringPen.Brush, paint)) _ringPen.Brush = paint;
        if (!ReferenceEquals(_glowPen.Brush, paint)) _glowPen.Brush = paint;
        if (!ReferenceEquals(_trackPen.Brush, paint)) _trackPen.Brush = paint;
        var color = paint is ISolidColorBrush solid ? solid.Color : Colors.DodgerBlue;
        _shadowHost.BoxShadow = RestShadow(color);
    }

    private void LoadGlyphs()
    {
        _play ??= FindGlyph("RiPlayFill");
        _stop ??= FindGlyph("RiStopFill");
    }

    private StreamGeometry? FindGlyph(string key)
    {
        // TryGetResource on this control does not walk parents. The icon dictionary
        // lives on the dashboard view, so follow the logical parent chain.
        for (StyledElement? node = this; node is not null; node = node.Parent)
        {
            if (node.TryGetResource(key, ActualThemeVariant, out var value) && value is StreamGeometry geometry)
                return geometry;
        }

        if (Application.Current?.TryFindResource(key, ActualThemeVariant, out var app) == true && app is StreamGeometry appGeometry)
            return appGeometry;
        return null;
    }

    private void InvalidateFace() => _face.InvalidateVisual();

    private static BoxShadows RestShadow(Color color)
    {
        var key = new BoxShadow
        {
            OffsetX = 0,
            OffsetY = ShadowKeyOffsetY,
            Blur = ShadowKeyBlur,
            Spread = 0,
            Color = WithAlpha(color, ShadowKeyAlpha)
        };
        var ambient = new BoxShadow
        {
            OffsetX = 0,
            OffsetY = ShadowAmbientOffsetY,
            Blur = ShadowAmbientBlur,
            Spread = 0,
            Color = WithAlpha(color, ShadowAmbientAlpha)
        };
        return new BoxShadows(key, [ambient]);
    }

    private static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(Math.Round(alpha * 255), 0, 255), color.R, color.G, color.B);

    private static Matrix ScaleAround(Point center, double scale)
    {
        var toOrigin = Matrix.CreateTranslation(-center.X, -center.Y);
        var scaled = Matrix.CreateScale(scale, scale);
        var back = Matrix.CreateTranslation(center.X, center.Y);
        return toOrigin * scaled * back;
    }

    private static double EaseOutBack(double t)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    private sealed class Face : Control
    {
        private readonly PowerButton _owner;

        public Face(PowerButton owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
            ClipToBounds = false;
        }

        public override void Render(DrawingContext context) => _owner.RenderFace(context);
    }
}
