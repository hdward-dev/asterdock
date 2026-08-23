using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SerialDebugger.Module.Models;
using SerialDebugger.Module.ViewModels;
using System.Collections.Specialized;

namespace SerialDebugger.Module.Views;

public partial class SerialDebuggerView : UserControl
{
    private readonly SerialDebuggerViewModel _viewModel;
    private bool _disposed;

    public SerialDebuggerView()
        : this(Path.Combine(Path.GetTempPath(), "AsterDock", "SerialDebuggerPreview"))
    {
    }

    public SerialDebuggerView(string dataDirectory)
    {
        InitializeComponent();
        _viewModel = new SerialDebuggerViewModel(dataDirectory);
        DataContext = _viewModel;
        _viewModel.Sessions.CollectionChanged += Sessions_CollectionChanged;
        foreach (var session in _viewModel.Sessions) AttachSession(session);
        _ = _viewModel.InitializeAsync();
    }

    public void DisposeResources()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.Sessions.CollectionChanged -= Sessions_CollectionChanged;
        foreach (var session in _viewModel.Sessions) DetachSession(session);
        _viewModel.Dispose();
    }

    private void Sessions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (SerialPortSessionViewModel session in e.OldItems) DetachSession(session);
        if (e.NewItems is not null)
            foreach (SerialPortSessionViewModel session in e.NewItems) AttachSession(session);
    }

    private static SerialPortSessionViewModel? GetSession(object? sender) =>
        sender is Button { Tag: SerialPortSessionViewModel session } ? session : null;

    private void AddPort_Click(object? sender, RoutedEventArgs e) => _viewModel.AddSession();
    private void TileLayout_Click(object? sender, RoutedEventArgs e) => _viewModel.IsTileLayout = true;
    private void TabLayout_Click(object? sender, RoutedEventArgs e) => _viewModel.IsTileLayout = false;
    private void CollapseAll_Click(object? sender, RoutedEventArgs e) => _viewModel.CollapseAll();
    private void ToggleLink_Click(object? sender, RoutedEventArgs e) => _viewModel.IsLinked = !_viewModel.IsLinked;

    private async void SaveWorkspace_Click(object? sender, RoutedEventArgs e) => await _viewModel.SaveAsync();

    private void DuplicatePort_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSession(sender) is { } session) _viewModel.DuplicateSession(session);
    }

    private void RemovePort_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSession(sender) is { } session) _viewModel.RemoveSession(session);
    }

    private void TogglePortCard_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSession(sender) is { } session) session.IsCollapsed = !session.IsCollapsed;
    }

    private void RefreshPorts_Click(object? sender, RoutedEventArgs e) => GetSession(sender)?.RefreshPorts();

    private void PortComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox { Tag: SerialPortSessionViewModel session }) session.RefreshPorts();
    }

    private void ToggleSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSession(sender) is { } session) session.IsSettingsExpanded = !session.IsSettingsExpanded;
    }

    private async void ToggleConnection_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSession(sender) is { } session) await session.ToggleConnectionAsync();
    }

    private void ToggleReceiveSection_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSession(sender) is { } session) session.IsReceiveExpanded = !session.IsReceiveExpanded;
    }

    private void ToggleSendSection_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSession(sender) is { } session) session.IsSendExpanded = !session.IsSendExpanded;
    }

    private void ToggleQuickCommands_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSession(sender) is { } session) session.IsQuickCommandsExpanded = !session.IsQuickCommandsExpanded;
    }

    private void ToggleProtocolSection_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSession(sender) is { } session) session.IsProtocolExpanded = !session.IsProtocolExpanded;
    }

    private void HexMode_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSession(sender) is { } session) session.IsHexMode = true;
    }

    private void TextMode_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSession(sender) is { } session) session.IsHexMode = false;
    }

    private void ClearLogs_Click(object? sender, RoutedEventArgs e) => GetSession(sender)?.ClearLogs();

    private async void Send_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSession(sender) is { } session) await _viewModel.SendFromAsync(session);
    }

    private async void QuickCommand_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QuickCommand command } button) return;
        var session = button.FindAncestorOfType<ItemsControl>()?.DataContext as SerialPortSessionViewModel;
        if (session is not null) await _viewModel.SendQuickCommandAsync(session, command);
    }

    private void ToggleOrchestration_Click(object? sender, RoutedEventArgs e) =>
        _viewModel.IsOrchestrationExpanded = !_viewModel.IsOrchestrationExpanded;

    private async void RunOrchestration_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.RunOrchestrationAsync();

    private void AttachSession(SerialPortSessionViewModel session) => session.ReceiveTextUpdated += Session_ReceiveTextUpdated;
    private void DetachSession(SerialPortSessionViewModel session) => session.ReceiveTextUpdated -= Session_ReceiveTextUpdated;

    private void Session_ReceiveTextUpdated(object? sender, EventArgs e)
    {
        if (sender is not SerialPortSessionViewModel { AutoScroll: true } session) return;
        Dispatcher.UIThread.Post(() =>
        {
            var console = this.GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault(textBox => textBox.Classes.Contains("console") && ReferenceEquals(textBox.Tag, session));
            if (console is not null) console.CaretIndex = console.Text?.Length ?? 0;
        }, DispatcherPriority.Background);
    }
}
