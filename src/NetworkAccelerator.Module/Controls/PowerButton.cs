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
public class PowerButton : Control
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

    private const double Diameter = 88;
    private static readonly Point Play0 = new(34, 32);
    private static readonly Point Play1 = new(56, 44);
    private static readonly Point Play2 = new(56, 44);
    private static readonly Point Play3 = new(34, 56);
    private static readonly Point Stop0 = new(36, 36);
    private static readonly Point Stop1 = new(52, 36);
    private static readonly Point Stop2 = new(52, 52);
    private static readonly Point Stop3 = new(36, 52);

    private readonly Pen _ringPen = new(Brushes.DodgerBlue, 3, lineCap: PenLineCap.Round);
    private readonly Pen _glowPen = new(Brushes.DodgerBlue, 6, lineCap: PenLineCap.Round);
    private readonly Pen _trackPen = new(Brushes.DodgerBlue, 3);
    private StreamGeometry? _icon;
    private StreamGeometry? _arc;
    private double _arcRadius = -1;
    private double _iconMorph = -2;
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ActualThemeVariantChanged += (_, _) => CacheBrushes();
        DashboardMotion.Changed += OnMotionSetting;
        CacheBrushes();
        _morph = IsRunning ? 1 : 0;
        _seenFault = FaultSignal;
        InvalidateVisual();
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
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hovered = true;
        if (!_pressed) AnimateScale(DashboardMotion.PowerHoverScale);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = false;
        if (!_pressed) AnimateScale(1);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsConnecting || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = true;
        _rippleOrigin = e.GetPosition(this);
        _ripple = 0;
        _rippleActive = true;
        AnimateScale(DashboardMotion.PowerPressScale);
        e.Pointer.Capture(this);
        e.Handled = true;
        EnsureClock();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);
        AnimateScale(_hovered ? DashboardMotion.PowerHoverScale : 1);
        if (new Rect(Bounds.Size).Contains(e.GetPosition(this)) && !IsConnecting)
            RaiseEvent(new RoutedEventArgs(ClickEvent));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsConnecting || e.Key is not (Key.Space or Key.Enter)) return;
        _pressed = true;
        _rippleOrigin = new Point(Diameter / 2, Diameter / 2);
        _ripple = 0;
        _rippleActive = true;
        AnimateScale(DashboardMotion.PowerPressScale);
        e.Handled = true;
        EnsureClock();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!_pressed || e.Key is not (Key.Space or Key.Enter)) return;
        _pressed = false;
        AnimateScale(1);
        if (!IsConnecting) RaiseEvent(new RoutedEventArgs(ClickEvent));
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Diameter / 2;
        var shake = _shakeActive ? Math.Sin(_shakeElapsed / DashboardMotion.PowerShake.TotalMilliseconds * Math.PI * 6) * (1 - Math.Clamp(_shakeElapsed / DashboardMotion.PowerShake.TotalMilliseconds, 0, 1)) * 6 : 0;
        using (context.PushTransform(Matrix.CreateTranslation(shake, 0)))
        {
            var drawn = radius * _scale;
            DrawGlow(context, center, drawn);
            DrawShadow(context, center, radius);
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
                DrawIcon(context, _morph);
            if (IsConnecting) DrawRing(context, center, radius + 6);
        }
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

    private void DrawShadow(DrawingContext context, Point center, double radius)
    {
        var strength = _pressed ? 0.22 : _hovered ? 0.48 : 0.30;
        var shifted = center + new Vector(0, 6);
        for (var layer = 5; layer >= 1; layer--)
        {
            var t = layer / 5d;
            var spread = t * (8 + strength * 18);
            using (context.PushOpacity(strength * 0.20 * (1 - t)))
                context.DrawEllipse(_accent, null, shifted, radius * _scale + spread, radius * 0.9 * _scale + spread);
        }
    }

    private void DrawIcon(DrawingContext context, double morph)
    {
        if (_icon is null || Math.Abs(_iconMorph - morph) > 0.004)
        {
            _iconMorph = morph;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(Lerp(Play0, Stop0, morph), true);
                ctx.LineTo(Lerp(Play1, Stop1, morph));
                ctx.LineTo(Lerp(Play2, Stop2, morph));
                ctx.LineTo(Lerp(Play3, Stop3, morph));
                ctx.EndFigure(true);
            }

            _icon = geometry;
        }

        context.DrawGeometry(IconBrush ?? _iconBrush, null, _icon);
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
        _morph = IsRunning ? 1 : 0;
        InvalidateVisual();
        EnsureClock();
    }

    private void BeginMorph(double target)
    {
        if (DashboardMotion.ReduceMotion || Math.Abs(_morph - target) < 0.001)
        {
            _morph = target;
            _morphAnimating = false;
            InvalidateVisual();
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
            InvalidateVisual();
            return;
        }

        _scaleFrom = _scale;
        _scaleTo = target;
        _scaleElapsed = 0;
        _scaleAnimating = Math.Abs(_scale - target) > 0.001;
        EnsureClock();
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
            if (_shakeElapsed >= DashboardMotion.PowerShake.TotalMilliseconds) _shakeActive = false;
        }

        if (IsConnecting && !DashboardMotion.ReduceMotion)
            _ringTurns = (_ringTurns + ms / DashboardMotion.PowerRing.TotalMilliseconds) % 1;

        if (IsRunning && !DashboardMotion.ReduceMotion)
            _glowPhase = (_glowPhase + ms / DashboardMotion.PowerGlow.TotalMilliseconds) % 1;

        InvalidateVisual();
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
    }

    private static Matrix ScaleAround(Point center, double scale)
    {
        var toOrigin = Matrix.CreateTranslation(-center.X, -center.Y);
        var scaled = Matrix.CreateScale(scale, scale);
        var back = Matrix.CreateTranslation(center.X, center.Y);
        return toOrigin * scaled * back;
    }

    private static Point Lerp(Point from, Point to, double t) =>
        new(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);

    private static double EaseOutBack(double t)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }
}
