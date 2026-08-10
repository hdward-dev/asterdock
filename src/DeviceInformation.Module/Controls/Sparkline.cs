using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DeviceInformation.Module.Controls;

public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke), Brushes.DodgerBlue);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(StrokeThickness), 1.8);

    static Sparkline() => AffectsRender<Sparkline>(ValuesProperty, StrokeProperty, StrokeThicknessProperty);

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Values is not { Count: > 1 } values || Bounds.Width <= 0 || Bounds.Height <= 0 || Stroke is null) return;

        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            for (var index = 0; index < values.Count; index++)
            {
                var x = index * Bounds.Width / (values.Count - 1);
                var y = Bounds.Height - Math.Clamp(values[index], 0, 100) * Bounds.Height / 100;
                var point = new Point(x, y);
                if (index == 0) geometryContext.BeginFigure(point, false);
                else geometryContext.LineTo(point);
            }
        }
        context.DrawGeometry(null, new Pen(Stroke, StrokeThickness, lineCap: PenLineCap.Round), geometry);
    }
}
