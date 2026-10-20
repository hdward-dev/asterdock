using System.Globalization;
using AsterDock.UI.Motion;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace AsterDock.UI.Controls;

public class AdLoadingIndicator : TemplatedControl, ILoadingClockClient
{
    public static readonly StyledProperty<AdLoadingSize> SizeProperty =
        AvaloniaProperty.Register<AdLoadingIndicator, AdLoadingSize>(nameof(Size), AdLoadingSize.Medium);

    public static readonly StyledProperty<double> DiameterProperty =
        AvaloniaProperty.Register<AdLoadingIndicator, double>(nameof(Diameter), double.NaN);

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<AdLoadingIndicator, string?>(nameof(Text));

    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<AdLoadingIndicator, double>(nameof(Progress), double.NaN, coerce: CoerceProgress);

    public static readonly StyledProperty<bool> ShowContainerProperty =
        AvaloniaProperty.Register<AdLoadingIndicator, bool>(nameof(ShowContainer), true);

    private readonly float[] _radii = new float[LoadingShapes.Samples];
    private readonly Point[] _points = new Point[LoadingShapes.Samples];
    private readonly StreamGeometry _shape = new();
    private readonly StreamGeometry _arc = new();
    private IBrush? _accent;
    private IBrush? _container;
    private Pen? _trackPen;
    private Pen? _progressPen;
    private Color _penColor;
    private double _penThickness = -1;
    private FormattedText? _formatted;
    private string? _formattedSource;
    private double _formattedSize = -1;
    private bool _subscribed;

    public AdLoadingIndicator()
    {
        FontSize = 13;
        Focusable = false;
    }

    public AdLoadingSize Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double Diameter
    {
        get => GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public bool ShowContainer
    {
        get => GetValue(ShowContainerProperty);
        set => SetValue(ShowContainerProperty, value);
    }

    public bool IsDeterminate => !double.IsNaN(Progress);

    private double EffectiveDiameter =>
        !double.IsNaN(Diameter) && Diameter > 0
            ? Diameter
            : Size switch
            {
                AdLoadingSize.Small => 20,
                AdLoadingSize.Large => 56,
                _ => 36
            };

    protected override Size MeasureOverride(Size available)
    {
        var diameter = EffectiveDiameter;
        var text = BuildText(diameter);
        var textWidth = text?.Width ?? 0;
        var textHeight = text?.Height ?? 0;
        var gap = textHeight > 0 ? 8 : 0;
        return new Size(Math.Max(diameter, textWidth), diameter + gap + textHeight);
    }

    public override void Render(DrawingContext context)
    {
        var diameter = EffectiveDiameter;
        if (diameter <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        EnsureRunning();
        EnsureBrushes();
        if (_accent is null) return;

        var now = LoadingClock.Now;
        var reduce = MotionPreferences.ReduceMotion;
        var centerX = Bounds.Width / 2;
        var centerY = diameter / 2;
        var opacity = reduce && !IsDeterminate ? LoadingShapes.PulseOpacity(now) : 1;
        using (context.PushOpacity(opacity))
        {
            if (ShowContainer && _container is not null)
                context.DrawEllipse(_container, null, new Point(centerX, centerY), diameter / 2, diameter / 2);

            var shapeRadius = diameter * (ShowContainer ? 0.30 : 0.46);
            if (IsDeterminate)
            {
                DrawCircle(centerX, centerY, shapeRadius);
                context.DrawGeometry(_accent, null, _shape);
                DrawProgress(context, centerX, centerY, diameter);
            }
            else
            {
                LoadingShapes.Interpolate(now, reduce, _radii);
                DrawPolar(centerX, centerY, shapeRadius, LoadingShapes.Rotation(now, reduce));
                context.DrawGeometry(_accent, null, _shape);
            }
        }

        var text = BuildText(diameter);
        if (text is null) return;
        var origin = new Point((Bounds.Width - text.Width) / 2, diameter + 8);
        context.DrawText(text, origin);
    }

    bool ILoadingClockClient.OnClock(TimeSpan now)
    {
        if (VisualRoot is null || !IsEffectivelyVisible || !WantsFrames)
        {
            _subscribed = false;
            return false;
        }

        InvalidateVisual();
        return true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MotionPreferences.ReduceMotionChanged += OnReduceMotionChanged;
        ActualThemeVariantChanged += OnThemeChanged;
        _accent = null;
        EnsureRunning();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        MotionPreferences.ReduceMotionChanged -= OnReduceMotionChanged;
        ActualThemeVariantChanged -= OnThemeChanged;
        LoadingClock.Unsubscribe(this);
        _subscribed = false;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == FontSizeProperty || change.Property == FontFamilyProperty
            || change.Property == FontWeightProperty || change.Property == ForegroundProperty)
        {
            _formatted = null;
        }

        if (change.Property == SizeProperty || change.Property == DiameterProperty || change.Property == TextProperty
            || change.Property == ProgressProperty || change.Property == ShowContainerProperty || change.Property == IsVisibleProperty)
        {
            UpdateClockSubscription();
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _accent = null;
        _formatted = null;
        InvalidateVisual();
    }

    private void OnReduceMotionChanged(object? sender, EventArgs e)
    {
        UpdateClockSubscription();
        InvalidateVisual();
    }

    private bool WantsFrames => !IsDeterminate;

    private void EnsureRunning()
    {
        if (_subscribed || !WantsFrames || VisualRoot is null || !IsEffectivelyVisible) return;
        _subscribed = true;
        LoadingClock.Subscribe(this);
    }

    private void UpdateClockSubscription()
    {
        if (WantsFrames && VisualRoot is not null && IsEffectivelyVisible) EnsureRunning();
        else
        {
            LoadingClock.Unsubscribe(this);
            _subscribed = false;
        }
    }

    private void EnsureBrushes()
    {
        IBrush accent = Foreground ?? Brushes.DodgerBlue;
        if (TryGetResource("AppAccentBrush", ActualThemeVariant, out var accentValue) && accentValue is IBrush accentBrush)
            accent = accentBrush;
        if (!ReferenceEquals(_accent, accent))
        {
            _accent = accent;
            _penThickness = -1;
        }

        if (TryGetResource("AppAccentSurfaceBrush", ActualThemeVariant, out var surface) && surface is IBrush container)
            _container = container;
    }

    private void DrawPolar(double centerX, double centerY, double maxRadius, double rotation)
    {
        for (var i = 0; i < LoadingShapes.Samples; i++)
        {
            var theta = rotation + (i * Math.Tau / LoadingShapes.Samples);
            var radius = _radii[i] * maxRadius;
            _points[i] = new Point(centerX + (Math.Cos(theta) * radius), centerY + (Math.Sin(theta) * radius));
        }

        FillPolygon(_shape, close: true);
    }

    private void DrawCircle(double centerX, double centerY, double radius)
    {
        for (var i = 0; i < LoadingShapes.Samples; i++)
        {
            var theta = i * Math.Tau / LoadingShapes.Samples;
            _points[i] = new Point(centerX + (Math.Cos(theta) * radius), centerY + (Math.Sin(theta) * radius));
        }

        FillPolygon(_shape, close: true);
    }

    private void FillPolygon(StreamGeometry geometry, bool close)
    {
        using var ctx = geometry.Open();
        ctx.BeginFigure(_points[0], isFilled: true);
        for (var i = 1; i < LoadingShapes.Samples; i++) ctx.LineTo(_points[i]);
        ctx.EndFigure(close);
    }

    private void DrawProgress(DrawingContext context, double centerX, double centerY, double diameter)
    {
        var color = (_accent as ISolidColorBrush)?.Color ?? Colors.DodgerBlue;
        var thickness = Math.Clamp(diameter * 0.07, 2, 4);
        if (_trackPen is null || _penColor != color || Math.Abs(_penThickness - thickness) > 0.01)
        {
            _penColor = color;
            _penThickness = thickness;
            var track = Color.FromArgb(64, color.R, color.G, color.B);
            _trackPen = new Pen(new SolidColorBrush(track), thickness, lineCap: PenLineCap.Round);
            _progressPen = new Pen(_accent, thickness, lineCap: PenLineCap.Round);
        }

        var radius = (diameter / 2) - (thickness / 2) - 0.5;
        if (radius <= 0 || _trackPen is null || _progressPen is null) return;
        var center = new Point(centerX, centerY);
        context.DrawEllipse(null, _trackPen, center, radius, radius);
        var progress = Math.Clamp(Progress, 0, 1);
        if (progress <= 0) return;
        if (progress >= 0.999)
        {
            context.DrawEllipse(null, _progressPen, center, radius, radius);
            return;
        }

        var sweep = progress * Math.Tau;
        var start = -Math.PI / 2;
        var steps = Math.Max(12, (int)Math.Ceiling(sweep / Math.Tau * 96));
        using (var ctx = _arc.Open())
        {
            ctx.BeginFigure(PointOn(center, radius, start), isFilled: false);
            for (var i = 1; i <= steps; i++)
                ctx.LineTo(PointOn(center, radius, start + (sweep * i / steps)));
            ctx.EndFigure(false);
        }

        context.DrawGeometry(null, _progressPen, _arc);
    }

    private FormattedText? BuildText(double diameter)
    {
        if (string.IsNullOrWhiteSpace(Text)) return null;
        var size = FontSize > 0 ? FontSize : 13;
        if (Size == AdLoadingSize.Small && Math.Abs(size - 13) < 0.1) size = 11;
        if (_formatted is not null && _formattedSource == Text && Math.Abs(_formattedSize - size) < 0.1) return _formatted;
        _formattedSource = Text;
        _formattedSize = size;
        var brush = Foreground;
        if (brush is null && TryGetResource("AppMutedBrush", ActualThemeVariant, out var muted) && muted is IBrush mutedBrush)
            brush = mutedBrush;
        _formatted = new FormattedText(
            Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight),
            size,
            brush);
        return _formatted;
    }

    private static Point PointOn(Point center, double radius, double angle)
        => new(center.X + (Math.Cos(angle) * radius), center.Y + (Math.Sin(angle) * radius));

    private static double CoerceProgress(AvaloniaObject sender, double value)
        => double.IsNaN(value) ? value : Math.Clamp(value, 0, 1);
}
