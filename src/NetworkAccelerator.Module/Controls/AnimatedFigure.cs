using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using NetworkAccelerator.Core.Services;

namespace NetworkAccelerator.Module.Controls;

public enum FigureKind
{
    Rate,
    Milliseconds
}

/// <summary>
/// Tweens a numeric figure over 300ms. Negative sentinels snap to a label.
/// </summary>
public class AnimatedFigure : TextBlock
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<AnimatedFigure, double>(nameof(Value), -2);

    public static readonly StyledProperty<FigureKind> KindProperty =
        AvaloniaProperty.Register<AnimatedFigure, FigureKind>(nameof(Kind));

    private double _displayed = double.NaN;
    private double _from;
    private double _to;
    private double _elapsed;
    private bool _running;

    static AnimatedFigure()
    {
        ValueProperty.Changed.AddClassHandler<AnimatedFigure>((figure, _) => figure.OnValueChanged());
        KindProperty.Changed.AddClassHandler<AnimatedFigure>((figure, _) => figure.Show(figure._displayed));
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

    private void OnValueChanged()
    {
        var next = Value;
        if (double.IsNaN(_displayed) || DashboardMotion.ReduceMotion || next < 0 || _displayed < 0)
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
        Text = Kind switch
        {
            FigureKind.Milliseconds when value <= -1.5 => "—",
            FigureKind.Milliseconds when value < 0 => "超时",
            FigureKind.Milliseconds => Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + " ms",
            _ when value < 0 => "—",
            _ => TrafficFormatter.FormatRate((long)Math.Round(value))
        };
    }
}
