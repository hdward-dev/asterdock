using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace VMCube.Module.Views;

public partial class MachineCard : Border
{
    public MachineCard() => InitializeComponent();

    public void ShowFrame(Bitmap? bitmap, string caption)
    {
        if (DataContext is MachineCardModel model)
        {
            model.Preview = bitmap;
            model.PreviewCaption = caption;
        }
        previewImage.InvalidateVisual();
    }

    private MachineCardModel? Model => DataContext as MachineCardModel;
    private async void Preview_Click(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.OpenPreview(); }
    private async void Primary_Click(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.Primary(); }
    private async void Pause_Click(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.Pause(); }
    private async void Power_Click(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.Power(); }
    private async void Favorite_Click(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.Favorite(); }
    private void Details_Click(object? sender, RoutedEventArgs e) => Model?.Details();
}
