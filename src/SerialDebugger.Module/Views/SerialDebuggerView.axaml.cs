using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SerialDebugger.Module.Models;
using SerialDebugger.Module.ViewModels;
using System.Collections.Specialized;

namespace SerialDebugger.Module.Views;

public partial class SerialDebuggerView : UserControl
{
    private double _workspaceHeight = 600;
    private readonly SerialDebuggerViewModel _viewModel;
    private bool _disposed;
    private SerialPortSessionViewModel? _resizingSession;
    private double _resizeStartX;
    private double _resizeStartWidth;

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
            foreach (SerialPortSessionViewModel session in e.OldItems)
            {
                if (ReferenceEquals(_resizingSession, session)) _resizingSession = null;
                DetachSession(session);
            }
        if (e.NewItems is not null)
        {
            foreach (SerialPortSessionViewModel session in e.NewItems)
            {
                AttachSession(session);
                session.WorkspaceHeight = _workspaceHeight;
            }
            if (_viewModel.IsTileLayout)
                Dispatcher.UIThread.Post(TileScrollViewer.ScrollToEnd, DispatcherPriority.Background);
        }
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

    private void ResizeHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: SerialPortSessionViewModel session } handle) return;
        _resizingSession = session;
        _resizeStartX = e.GetPosition(this).X;
        _resizeStartWidth = session.TileWidth;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void ResizeHandle_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_resizingSession is null || sender is not Border { Tag: SerialPortSessionViewModel session } ||
            !ReferenceEquals(_resizingSession, session)) return;
        session.TileWidth = _resizeStartWidth + e.GetPosition(this).X - _resizeStartX;
        e.Handled = true;
    }

    private void ResizeHandle_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizingSession is null) return;
        _resizingSession = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void ResizeHandle_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { Tag: SerialPortSessionViewModel session })
            session.TileWidth = SerialPortSessionViewModel.DefaultTileWidth;
    }

    private void FocusedWorkspace_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid grid || grid.Children[1] is not Border sidebar) return;
        var sideBySide = e.NewSize.Width >= 940;
        Grid.SetColumn(sidebar, sideBySide ? 1 : 0);
        Grid.SetRow(sidebar, sideBySide ? 0 : 1);
        sidebar.Width = sideBySide ? 280 : double.NaN;
        sidebar.Margin = sideBySide ? new Avalonia.Thickness(12, 0, 0, 0) : new Avalonia.Thickness(0, 12, 0, 0);
        sidebar.Height = sideBySide && grid.DataContext is SerialPortSessionViewModel session ? session.WorkspaceHeight : 300;
    }

    private void Workspace_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _workspaceHeight = Math.Max(540, e.NewSize.Height - 72);
        foreach (var session in _viewModel.Sessions) session.WorkspaceHeight = _workspaceHeight;
    }

    private void SendHexMode_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSession(sender) is { } session) session.IsSendHexMode = true;
    }

    private void SendTextMode_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSession(sender) is { } session) session.IsSendHexMode = false;
    }

    private async void SendInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) return;
        if (sender is not TextBox { Tag: SerialPortSessionViewModel session } || !session.CanSend) return;
        e.Handled = true;
        await _viewModel.SendFromAsync(session);
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

    private void LoadQuickCommand_Click(object? sender, RoutedEventArgs e) => GetSession(sender)?.LoadQuickCommand();

    private async void SaveQuickCommand_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSession(sender) is { } session && session.SaveQuickCommand()) await _viewModel.SaveAsync();
    }

    private async void RemoveQuickCommand_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSession(sender) is { } session && session.RemoveQuickCommand()) await _viewModel.SaveAsync();
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
                .FirstOrDefault(textBox => textBox.IsEffectivelyVisible && textBox.Classes.Contains("console") && ReferenceEquals(textBox.Tag, session));
            if (console is not null) console.CaretIndex = console.Text?.Length ?? 0;
        }, DispatcherPriority.Background);
    }
}
