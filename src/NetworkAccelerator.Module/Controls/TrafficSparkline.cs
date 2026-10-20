using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NetworkAccelerator.Core.Services;
using NetworkAccelerator.Module.ViewModels;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// Area chart for one traffic direction. Geometry is rebuilt only when the series or size changes.
/// </summary>
public class TrafficSparkline : Control
{
    public const double MinimumScale = 8 * 1024d;
    private const double StrokeWidth = 2;
    private const double FillOpacity = 0.14;

    public static readonly StyledProperty<TrafficTrace?> TraceProperty =
        AvaloniaProperty.Register<TrafficSparkline, TrafficTrace?>(nameof(Trace));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<TrafficSparkline, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<bool> SoftProperty =
        AvaloniaProperty.Register<TrafficSparkline, bool>(nameof(Soft));

    private TrafficTrace? _trace;
    private int _builtRevision = -1;
    private Size _builtSize;
    private bool _builtSoft;
    private StreamGeometry? _line;
    private StreamGeometry? _area;
    private IPen? _pen;
    private IBrush? _stroke;

    static TrafficSparkline()
    {
        AffectsRender<TrafficSparkline>(StrokeProperty, SoftProperty);
    }

    public TrafficTrace? Trace
    {
        get => GetValue(TraceProperty);
        set => SetValue(TraceProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public bool Soft
    {
        get => GetValue(SoftProperty);
        set => SetValue(SoftProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TraceProperty)
        {
            if (_trace is not null) _trace.Changed -= OnTraceChanged;
            _trace = change.GetNewValue<TrafficTrace?>();
            if (_trace is not null) _trace.Changed += OnTraceChanged;
            _builtRevision = -1;
            InvalidateVisual();
        }
        else if (change.Property == StrokeProperty || change.Property == SoftProperty)
        {
            _builtRevision = -1;
            _pen = null;
        }
    }

    public override void Render(DrawingContext context)
    {
        var trace = _trace;
        var stroke = Stroke;
        if (trace is null || stroke is null || trace.Count < 2 || Bounds.Width <= 1 || Bounds.Height <= 1) return;
        if (_builtRevision != trace.Revision || _builtSize != Bounds.Size || _builtSoft != Soft || _line is null || _area is null)
            Rebuild(trace, stroke);

        if (_area is null || _line is null || _pen is null) return;
        var strokeOpacity = Soft ? 0.62 : 1;
        using (context.PushOpacity(FillOpacity * strokeOpacity))
            context.DrawGeometry(stroke, null, _area);
        using (context.PushOpacity(strokeOpacity))
            context.DrawGeometry(null, _pen, _line);
    }

    private void Rebuild(TrafficTrace trace, IBrush stroke)
    {
        var count = trace.Count;
        var width = Bounds.Width;
        var height = Bounds.Height;
        var step = width / (TrafficSeriesBuffer.Capacity - 1);
        var start = width - (count - 1) * step;
        double max = MinimumScale;
        for (var i = 0; i < count; i++) max = Math.Max(max, trace.SampleAt(i));
        var usable = Math.Max(1, height - StrokeWidth);
        var points = new Point[count];
        for (var i = 0; i < count; i++)
        {
            var y = height - StrokeWidth / 2 - trace.SampleAt(i) / max * usable;
            points[i] = new Point(start + i * step, y);
        }

        _line = BuildCurve(points, close: false, height);
        _area = BuildCurve(points, close: true, height);
        if (!ReferenceEquals(_stroke, stroke))
        {
            _stroke = stroke;
            _pen = new Pen(stroke, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        }

        _builtRevision = trace.Revision;
        _builtSize = Bounds.Size;
        _builtSoft = Soft;
    }

    private static StreamGeometry BuildCurve(Point[] points, bool close, double height)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(points[0], close);
        if (points.Length == 2)
        {
            ctx.LineTo(points[1]);
        }
        else
        {
            for (var i = 1; i < points.Length - 1; i++)
            {
                var next = points[i + 1];
                var mid = new Point((points[i].X + next.X) / 2, (points[i].Y + next.Y) / 2);
                ctx.QuadraticBezierTo(points[i], mid);
            }

            ctx.QuadraticBezierTo(points[^2], points[^1]);
        }

        if (close)
        {
            ctx.LineTo(new Point(points[^1].X, height));
            ctx.LineTo(new Point(points[0].X, height));
            ctx.EndFigure(true);
        }
        else
        {
            ctx.EndFigure(false);
        }

        return geometry;
    }

    private void OnTraceChanged() => InvalidateVisual();
}
