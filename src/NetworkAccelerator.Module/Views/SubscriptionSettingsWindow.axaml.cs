using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NetworkAccelerator.Core.Models;
using NetworkAccelerator.Module.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NetworkAccelerator.Module.Views;

public partial class SubscriptionSettingsWindow : Window, INotifyPropertyChanged
{
    private ConfigurationRow? _selectedConfiguration;

    public SubscriptionSettingsWindow()
    {
        InitializeComponent();
        ConfigureWindowChrome();
        DataContext = this;
    }

    public SubscriptionSettingsWindow(
        IEnumerable<SubscriptionConfiguration> configurations,
        string? activeConfigurationId) : this()
    {
        foreach (var configuration in configurations)
            Configurations.Add(new ConfigurationRow(configuration, configuration.Id == activeConfigurationId));
        if (Configurations.Count == 0) Configurations.Add(ConfigurationRow.CreateNew(active: true));
        SelectedConfiguration = Configurations.FirstOrDefault(item => item.IsActive) ?? Configurations[0];
        ConfigurationList.SelectedItem = SelectedConfiguration;
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ConfigurationRow> Configurations { get; } = [];

    public ConfigurationRow? SelectedConfiguration
    {
        get => _selectedConfiguration;
        set
        {
            if (ReferenceEquals(_selectedConfiguration, value)) return;
            _selectedConfiguration = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedConfiguration)));
        }
    }

    private void ConfigurationList_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        SelectedConfiguration = ConfigurationList.SelectedItem as ConfigurationRow;

    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        var item = ConfigurationRow.CreateNew(active: Configurations.Count == 0);
        Configurations.Add(item);
        SelectedConfiguration = item;
        ConfigurationList.SelectedItem = item;
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedConfiguration is null) return;
        if (Configurations.Count == 1)
        {
            ShowValidation("至少保留一个配置");
            return;
        }
        var index = Configurations.IndexOf(SelectedConfiguration);
        var wasActive = SelectedConfiguration.IsActive;
        Configurations.Remove(SelectedConfiguration);
        SelectedConfiguration = Configurations[Math.Min(index, Configurations.Count - 1)];
        if (wasActive) SetActive(SelectedConfiguration);
        ConfigurationList.SelectedItem = SelectedConfiguration;
    }

    private void ActiveConfiguration_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedConfiguration is not null) SetActive(SelectedConfiguration);
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in Configurations)
        {
            item.Name = item.Name.Trim();
            item.Source = item.Source.Trim();
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                ShowValidation("每个配置都需要填写名称");
                return;
            }
            if (string.IsNullOrWhiteSpace(item.Source))
            {
                ShowValidation($"配置“{item.Name}”缺少订阅地址或本地文件路径");
                return;
            }
        }
        if (Configurations.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            ShowValidation("配置名称不能重复");
            return;
        }
        var active = Configurations.FirstOrDefault(item => item.IsActive) ?? Configurations[0];
        var result = new SubscriptionSettingsResult(
            Configurations.Select(item => item.ToConfiguration()).ToList(),
            active.Id);
        Dispatcher.UIThread.Post(() => Close(result), DispatcherPriority.Background);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => Close(null), DispatcherPriority.Background);

    private void ConfigureWindowChrome()
    {
        if (OperatingSystem.IsMacOS())
        {
            CustomTitleBar.IsVisible = false;
            WindowRootGrid.RowDefinitions[0].Height = new GridLength(0);
            return;
        }

        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 40;
        if (OperatingSystem.IsWindows())
            TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.Blur];
        base.PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty) UpdateMaximizeGlyph();
        };
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseTitleBar_Click(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => Close(null), DispatcherPriority.Background);

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph()
    {
        MaximizeIcon.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M8,6 H18 V16 M6,8 H16 V18 H6 Z"
            : "M6,6 L18,6 L18,18 L6,18 Z");
        ToolTip.SetTip(MaximizeButton, WindowState == WindowState.Maximized ? "还原" : "最大化");
    }

    private void SetActive(ConfigurationRow active)
    {
        foreach (var item in Configurations) item.IsActive = ReferenceEquals(item, active);
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.IsVisible = true;
    }

    public sealed class ConfigurationRow : INotifyPropertyChanged
    {
        private string _name;
        private string _source;
        private bool _isActive;

        public ConfigurationRow(SubscriptionConfiguration source, bool active)
        {
            Id = source.Id;
            _name = source.Name;
            _source = source.Source;
            CachedSource = source.CachedSource;
            SelectedNode = source.SelectedNode;
            _isActive = active;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public string Id { get; }
        public string CachedSource { get; }
        public string SelectedNode { get; }
        public string Name { get => _name; set => SetField(ref _name, value); }
        public string Source { get => _source; set => SetField(ref _source, value); }
        public bool IsActive { get => _isActive; set => SetField(ref _isActive, value); }

        public static ConfigurationRow CreateNew(bool active) => new(new SubscriptionConfiguration
        {
            Name = "新配置"
        }, active);

        public SubscriptionConfiguration ToConfiguration() => new()
        {
            Id = Id,
            Name = Name,
            Source = Source,
            CachedSource = CachedSource,
            SelectedNode = SelectedNode
        };

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
