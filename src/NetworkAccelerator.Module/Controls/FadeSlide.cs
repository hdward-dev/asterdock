using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NetworkAccelerator.Module.Controls;

/// <summary>Applies a row's fade and vertical shift without allocating a transform per frame.</summary>
public class FadeSlide : Decorator
{
    public static readonly StyledProperty<double> FadeProperty =
        AvaloniaProperty.Register<FadeSlide, double>(nameof(Fade), 1);

    public static readonly StyledProperty<double> ShiftProperty =
        AvaloniaProperty.Register<FadeSlide, double>(nameof(Shift));

    private readonly TranslateTransform _shift = new();

    public FadeSlide()
    {
        RenderTransform = _shift;
        ClipToBounds = false;
    }

    public double Fade
    {
        get => GetValue(FadeProperty);
        set => SetValue(FadeProperty, value);
    }

    public double Shift
    {
        get => GetValue(ShiftProperty);
        set => SetValue(ShiftProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FadeProperty) Opacity = Fade;
        else if (change.Property == ShiftProperty) _shift.Y = Shift;
    }
}
