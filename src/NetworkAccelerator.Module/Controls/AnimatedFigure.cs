using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NetworkAccelerator.Core.Services;

namespace NetworkAccelerator.Module.Controls;

public enum FigureKind
{
    Rate,
    Milliseconds
}

/// <summary>
/// Tweens a numeric figure over 300ms. A rate is a large value with a smaller unit.
/// Negative sentinels snap to a label.
/// </summary>
public class AnimatedFigure : Panel
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<AnimatedFigure, double>(nameof(Value), -2);

    public static readonly StyledProperty<FigureKind> KindProperty =
        AvaloniaProperty.Register<AnimatedFigure, FigureKind>(nameof(Kind));

    private readonly TextBlock _number;
    private readonly TextBlock _unit;
    private double _displayed = double.NaN;
    private double _from;
    private double _to;
    private double _elapsed;
    private bool _running;
    private long _shownRate = long.MinValue;

    static AnimatedFigure()
    {
        ValueProperty.Changed.AddClassHandler<AnimatedFigure>((figure, _) => figure.OnValueChanged());
        KindProperty.Changed.AddClassHandler<AnimatedFigure>((figure, _) => figure.Show(figure._displayed));
    }

    public AnimatedFigure()
    {
        _number = new TextBlock { VerticalAlignment = VerticalAlignment.Bottom };
        _unit = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(4, 0, 0, 5),
            IsVisible = false
        };
        Children.Add(_number);
        Children.Add(_unit);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public FigureKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeChanged;
        ApplyTone();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTone();

    private void ApplyTone()
    {
        var key = Kind == FigureKind.Milliseconds ? "AppMutedBrush" : "AppStrongTextBrush";
        if (TryGetResource(key, ActualThemeVariant, out var value) && value is IBrush brush)
        {
            _number.Foreground = brush;
            _unit.Foreground = brush;
        }
    }

    private void OnValueChanged()
    {
        var next = Value;
        if (double.IsNaN(_displayed) || DashboardMotion.ReduceMotion || next < 0 || _displayed < 0 || VisualRoot is null)
        {
            Stop();
            _displayed = next;
            Show(next);
            return;
        }

        _from = _displayed;
        _to = next;
        _elapsed = 0;
        if (_running) return;
        _running = true;
        MotionClock.Subscribe(Tick);
    }

    private void Tick(TimeSpan elapsed)
    {
        _elapsed += elapsed.TotalMilliseconds;
        var t = Math.Clamp(_elapsed / DashboardMotion.Number.TotalMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        _displayed = _from + (_to - _from) * eased;
        Show(_displayed);
        if (t < 1) return;
        _displayed = _to;
        Show(_to);
        Stop();
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        MotionClock.Unsubscribe(Tick);
    }

    private void Show(double value)
    {
        if (double.IsNaN(value)) return;
        if (Kind == FigureKind.Milliseconds)
        {
            _unit.IsVisible = false;
            _number.FontSize = 12;
            _number.FontWeight = FontWeight.Normal;
            ApplyTone();
            _number.Text = value switch
            {
                <= -1.5 => "—",
                < 0 => "超时",
                _ => Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + " ms"
            };
            InvalidateMeasure();
            return;
        }

        _number.FontSize = 38;
        _number.FontWeight = FontWeight.SemiBold;
        ApplyTone();
        if (value < 0)
        {
            _shownRate = -1;
            _number.Text = "—";
            _unit.IsVisible = false;
            InvalidateMeasure();
            return;
        }

        var rounded = (long)Math.Round(value);
        if (rounded == _shownRate && _unit.IsVisible) return;
        _shownRate = rounded;
        TrafficFormatter.SplitRate(rounded, out var amount, out var unit);
        _number.Text = amount;
        _unit.Text = unit;
        _unit.IsVisible = true;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _number.Measure(availableSize);
        if (!_unit.IsVisible) return _number.DesiredSize;
        _unit.Measure(availableSize);
        return new Size(
            _number.DesiredSize.Width + _unit.DesiredSize.Width,
            Math.Max(_number.DesiredSize.Height, _unit.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var number = _number.DesiredSize;
        var unit = _unit.IsVisible ? _unit.DesiredSize : default;
        var height = Math.Max(number.Height, unit.Height);
        _number.Arrange(new Rect(0, height - number.Height, number.Width, number.Height));
        if (_unit.IsVisible)
            _unit.Arrange(new Rect(number.Width, height - unit.Height, unit.Width, unit.Height));
        return finalSize;
    }
}
