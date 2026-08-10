using AndroidScreen.Module.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AndroidScreen.Module.Views;

public partial class AndroidScreenView : UserControl
{
    private readonly AndroidScreenViewModel? _viewModel;

    public AndroidScreenView()
    {
        InitializeComponent();
    }

    public AndroidScreenView(string dataDirectory, string moduleDirectory) : this()
    {
        _viewModel = new AndroidScreenViewModel(dataDirectory, moduleDirectory);
        DataContext = _viewModel;
        AttachedToVisualTree += (_, _) => _viewModel.Initialize();
    }

    public void DisposeResources() => _viewModel?.Dispose();

    private async void Toggle_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.ToggleAsync();
    }

    private async void Install_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.InstallAsync();
    }
}
