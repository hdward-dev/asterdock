using Avalonia.Controls;
using Avalonia.Interactivity;

namespace URemote.Module;

public partial class DeviceCard : Border
{
    public DeviceCard() => InitializeComponent();

    private DeviceCardModel? Model => DataContext as DeviceCardModel;

    private void Preview_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (Model is not { ListView: false } || sender is not Grid visual) return;
        var height = Math.Clamp(e.NewSize.Width * 9 / 16, 130, 220);
        if (Math.Abs(visual.Height - height) > 1) visual.Height = height;
    }

    private void Actions_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (Model is { } model) model.ShowLabels = e.NewSize.Width >= 430;
    }

    private void Remote_Click(object? sender, RoutedEventArgs e) => Model?.RequestRemote?.Invoke();
    private void Files_Click(object? sender, RoutedEventArgs e) => Model?.RequestFiles?.Invoke();
    private void Terminal_Click(object? sender, RoutedEventArgs e) => Model?.RequestTerminal?.Invoke();
    private void Favorite_Click(object? sender, RoutedEventArgs e) => Model?.RequestFavorite?.Invoke();
    private void Details_Click(object? sender, RoutedEventArgs e) => Model?.RequestDetails?.Invoke();
}
