using AsterDock.Contracts;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Home.Module.Models;
using Home.Module.ViewModels;

namespace Home.Module.Views;

public partial class HomeView : UserControl
{
    private HomeViewModel? _viewModel;

    public HomeView()
    {
        InitializeComponent();
    }

    public HomeView(IApplicationContext context)
        : this()
    {
        _viewModel = new HomeViewModel(context);
        DataContext = _viewModel;
        AttachedToVisualTree += (_, _) => _ = _viewModel.StartAsync();
        DetachedFromVisualTree += (_, _) => _viewModel.Stop();
    }

    public void DisposeResources() => _viewModel?.Dispose();

    private void OpenInvoicePrinter_Click(object? sender, RoutedEventArgs e) => _viewModel?.OpenInvoicePrinter();
    private void OpenDeviceInformation_Click(object? sender, RoutedEventArgs e) => _viewModel?.OpenDeviceInformation();
    private void ShowSettings_Click(object? sender, RoutedEventArgs e) => _viewModel?.ShowSettings();
    private void ShowApplicationSwitcher_Click(object? sender, RoutedEventArgs e) => _viewModel?.ShowApplicationSwitcher();
    private void ToggleDeviceWidget_Click(object? sender, RoutedEventArgs e) => _viewModel?.ToggleDeviceWidget();

    private void ApplicationTile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HomeApplicationItem item }) return;
        if (_viewModel is null) return;
        if (item.IsAddTile) _viewModel.ShowSettings();
        else _viewModel.OpenApplication(item.Id);
    }

    private void RecentApplication_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecentApplicationItem item }) _viewModel?.OpenApplication(item.Id);
    }
}
