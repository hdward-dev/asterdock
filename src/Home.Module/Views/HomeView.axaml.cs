using AsterDock.Contracts;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Home.Module.Models;
using Home.Module.ViewModels;

namespace Home.Module.Views;

public partial class HomeView : UserControl
{
    private HomeViewModel? _viewModel;
    private readonly bool _ownsModel;
    private bool _observing;

    public HomeView()
    {
        InitializeComponent();
    }

    public HomeView(IApplicationContext context)
        : this(new HomeViewModel(context), ownsModel: true)
    {
    }

    public HomeView(HomeViewModel viewModel, bool ownsModel = false)
        : this()
    {
        _viewModel = viewModel;
        _ownsModel = ownsModel;
        DataContext = viewModel;
        AttachedToVisualTree += (_, _) =>
        {
            if (_observing || _viewModel is null) return;
            _observing = true;
            _ = _viewModel.StartAsync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (!_observing || _viewModel is null) return;
            _observing = false;
            _viewModel.Stop();
        };
    }

    public void DisposeResources()
    {
        if (_observing && _viewModel is not null)
        {
            _observing = false;
            _viewModel.Stop();
        }

        if (_ownsModel) _viewModel?.Dispose();
    }

    private void OpenInvoicePrinter_Click(object? sender, RoutedEventArgs e) => _viewModel?.OpenInvoicePrinter();
    private void OpenDeviceInformation_Click(object? sender, RoutedEventArgs e) => _viewModel?.OpenDeviceInformation();
    private void BrowseApplications_Click(object? sender, RoutedEventArgs e) => _viewModel?.BrowseApplications();
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
