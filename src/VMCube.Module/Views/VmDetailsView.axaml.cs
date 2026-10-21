using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VMCube.Module.Views;

public partial class VmDetailsView : StackPanel
{
    public VmDetailsView() => InitializeComponent();
    public StackPanel ConsoleHost => consoleHost;
    private VmDetailsModel? Model => DataContext as VmDetailsModel;
    private async void Start_Click(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.Start(); }
    private async void Eject_Click(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.Eject(); }
    private async void Connect_Click(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.Connect(); }
    private async void Pause_Click(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.Pause(); }
    private async void Shutdown_Click(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.Shutdown(); }
    private async void Resume_Click(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.Resume(); }
    private async void Force_Click(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.Force(); }
}
