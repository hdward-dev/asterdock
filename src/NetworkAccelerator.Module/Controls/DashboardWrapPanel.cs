using Avalonia;
using Avalonia.Controls;

namespace NetworkAccelerator.Module.Controls;

public class DashboardWrapPanel : Panel
{
    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<DashboardWrapPanel, double>(nameof(Gap), 12d);

    public static readonly AttachedProperty<double> WeightProperty =
        AvaloniaProperty.RegisterAttached<DashboardWrapPanel, Control, double>("Weight", 1d);

    public static readonly AttachedProperty<double> MinItemWidthProperty =
        AvaloniaProperty.RegisterAttached<DashboardWrapPanel, Control, double>("MinItemWidth", 220d);

    static DashboardWrapPanel()
    {
        AffectsMeasure<DashboardWrapPanel>(GapProperty);
        WeightProperty.Changed.AddClassHandler<Control>(InvalidateParent);
        MinItemWidthProperty.Changed.AddClassHandler<Control>(InvalidateParent);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public static double GetWeight(Control control) => control.GetValue(WeightProperty);
    public static void SetWeight(Control control, double value) => control.SetValue(WeightProperty, value);
    public static double GetMinItemWidth(Control control) => control.GetValue(MinItemWidthProperty);
    public static void SetMinItemWidth(Control control, double value) => control.SetValue(MinItemWidthProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 960 : Math.Max(0, availableSize.Width);
        return Layout(width, measureOnly: true);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(Math.Max(0, finalSize.Width), measureOnly: false);
        return finalSize;
    }

    private Size Layout(double width, bool measureOnly)
    {
        var rows = Pack(width);
        var gap = Math.Max(0, Gap);
        double y = 0;
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var widths = Widths(row, width, gap);
            double rowHeight = 0;
            for (var i = 0; i < row.Count; i++)
            {
                row[i].Measure(new Size(Math.Max(0, widths[i]), double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, row[i].DesiredSize.Height);
            }

            if (rowIndex > 0) y += gap;
            if (!measureOnly)
            {
                double x = 0;
                for (var i = 0; i < row.Count; i++)
                {
                    row[i].Arrange(new Rect(x, y, Math.Max(0, widths[i]), rowHeight));
                    x += widths[i] + gap;
                }
            }

            y += rowHeight;
        }

        return new Size(width, y);
    }

    private List<List<Control>> Pack(double width)
    {
        var rows = new List<List<Control>>();
        var current = new List<Control>();
        double used = 0;
        var gap = Math.Max(0, Gap);
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            var min = Math.Min(Math.Max(0, GetMinItemWidth(child)), width);
            if (current.Count > 0 && used + gap + min > width + 0.5)
            {
                rows.Add(current);
                current = [];
                used = 0;
            }

            if (current.Count > 0) used += gap;
            current.Add(child);
            used += min;
        }

        if (current.Count > 0) rows.Add(current);
        return rows;
    }

    private static double[] Widths(IReadOnlyList<Control> row, double available, double gap)
    {
        var widths = new double[row.Count];
        if (row.Count == 0) return widths;
        var gaps = gap * (row.Count - 1);
        var mins = new double[row.Count];
        double minSum = 0;
        double weightSum = 0;
        for (var i = 0; i < row.Count; i++)
        {
            mins[i] = Math.Min(Math.Max(0, GetMinItemWidth(row[i])), available);
            minSum += mins[i];
            weightSum += Math.Max(0.01, GetWeight(row[i]));
        }

        var extra = Math.Max(0, available - gaps - minSum);
        double assigned = 0;
        for (var i = 0; i < row.Count; i++)
        {
            if (i == row.Count - 1)
            {
                widths[i] = Math.Max(0, available - gaps - assigned);
            }
            else
            {
                widths[i] = mins[i] + extra * (Math.Max(0.01, GetWeight(row[i])) / weightSum);
                assigned += widths[i];
            }
        }

        return widths;
    }

    private static void InvalidateParent(Control control, AvaloniaPropertyChangedEventArgs change)
    {
        if (control.Parent is DashboardWrapPanel panel) panel.InvalidateMeasure();
    }
}
