using System.ComponentModel;
using AndroidScreen.Module.Services;
using AndroidScreen.Module.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace AndroidScreen.Module.Views;

public partial class AndroidScreenView : UserControl
{
    private readonly AndroidScreenViewModel? _viewModel;
    private bool _dragging;
    private (int X, int Y, int Width, int Height)? _lastPosition;
    private bool _copyRequested;
    private bool _narrow;
    private AndroidScreenViewModel? Model => _viewModel ?? DataContext as AndroidScreenViewModel;

    public AndroidScreenView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMode();
        DetachedFromVisualTree += (_, _) => ReleaseTouch();
    }

    public AndroidScreenView(string dataDirectory, string moduleDirectory) : this()
    {
        _viewModel = new(dataDirectory, moduleDirectory); DataContext = _viewModel;
        AttachedToVisualTree += async (_, _) => await _viewModel.InitializeAsync();
        _viewModel.PropertyChanged += ModelChanged;
    }

    public void DisposeResources()
    {
        ReleaseTouch();
        if (_viewModel is not null) { _viewModel.PropertyChanged -= ModelChanged; _viewModel.Dispose(); }
    }

    private void UpdateLayoutMode()
    {
        var narrow = Bounds.Width < 760;
        if (_narrow == narrow) return; _narrow = narrow;
        Workspace.ColumnDefinitions = new(narrow ? "*" : "280,*");
        Workspace.RowDefinitions = new(narrow ? "Auto,520" : "*");
        WorkspaceScroll.VerticalScrollBarVisibility = narrow ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        ConnectionPanel.VerticalScrollBarVisibility = narrow ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        Grid.SetRow(PreviewPanel, narrow ? 1 : 0); Grid.SetColumn(PreviewPanel, narrow ? 0 : 1);
    }

    private async void Toggle_Click(object? sender, RoutedEventArgs e) { if (Model is { } vm) await vm.ToggleAsync(); }
    private async void Install_Click(object? sender, RoutedEventArgs e) { if (Model is { } vm) await vm.InstallAsync(); }
    private async void Refresh_Click(object? sender, RoutedEventArgs e) { if (Model is { } vm) await vm.RefreshAsync(); }
    private async void Wireless_Click(object? sender, RoutedEventArgs e) { if (Model is { } vm) await vm.ConnectWirelessAsync(); }
    private async void Pair_Click(object? sender, RoutedEventArgs e) { if (Model is { } vm) await vm.PairAsync(); }
    private void Key_Click(object? sender, RoutedEventArgs e) { if (sender is Button { Tag: string code } && uint.TryParse(code, out var key)) Model?.SendKey(key); }
    private void Rotate_Click(object? sender, RoutedEventArgs e) { ReleaseTouch(); Model?.Send([11]); }

    public static (int X, int Y, int Width, int Height)? MapPosition(Point point, Size bounds, PixelSize pixels, bool clamp)
    {
        if (pixels.Width <= 0 || pixels.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return null;
        var scale = Math.Min(bounds.Width / pixels.Width, bounds.Height / pixels.Height);
        var x = (point.X - (bounds.Width - pixels.Width * scale) / 2) / scale;
        var y = (point.Y - (bounds.Height - pixels.Height * scale) / 2) / scale;
        if (!clamp && (x < 0 || y < 0 || x >= pixels.Width || y >= pixels.Height)) return null;
        return ((int)Math.Clamp(x, 0, pixels.Width - 1), (int)Math.Clamp(y, 0, pixels.Height - 1), pixels.Width, pixels.Height);
    }

    private (int X, int Y, int Width, int Height)? Position(PointerEventArgs e, bool clamp = false) =>
        Model?.Frame is { } frame ? MapPosition(e.GetPosition(VideoSurface), VideoSurface.Bounds.Size, frame.PixelSize, clamp) : null;
    private void Touch(byte action, (int X, int Y, int Width, int Height) p) => Model?.Send(ScrcpyProtocol.Touch(action, p.X, p.Y, p.Width, p.Height));
    private void Video_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (Position(e) is not { } p) return;
        VideoSurface.Focus();
        if (e.GetCurrentPoint(VideoSurface).Properties.IsRightButtonPressed) { Model?.SendKey(4); e.Handled = true; return; }
        if (!e.GetCurrentPoint(VideoSurface).Properties.IsLeftButtonPressed) return;
        _lastPosition = p; _dragging = true; e.Pointer.Capture(VideoSurface); Touch(0, p); e.Handled = true;
    }
    private void Video_Moved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || Position(e, true) is not { } p) return;
        if (_lastPosition == p) return;
        _lastPosition = p; Touch(2, p); e.Handled = true;
    }
    private void Video_Released(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _lastPosition = Position(e, true) ?? _lastPosition; ReleaseTouch(); e.Pointer.Capture(null); e.Handled = true;
    }
    private void ReleaseTouch()
    {
        if (_dragging && _lastPosition is { } p) Touch(1, p);
        _dragging = false; _lastPosition = null;
    }
    private void Video_CaptureLost(object? sender, PointerCaptureLostEventArgs e) => ReleaseTouch();
    private void Video_Wheel(object? sender, PointerWheelEventArgs e)
    {
        if (Position(e) is not { } p) return;
        Model?.Send(ScrcpyProtocol.Scroll(p.X, p.Y, p.Width, p.Height, e.Delta.X, e.Delta.Y)); e.Handled = true;
    }
    private async void Video_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Model?.IsRunning != true) return;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            if (e.Key == Key.V) { await PasteAsync(); e.Handled = true; }
            return;
        }
        uint code = e.Key switch { Key.Enter => 66, Key.Back => 67, Key.Delete => 112, Key.Tab => 61, Key.Escape => 4,
            Key.Left => 21, Key.Right => 22, Key.Up => 19, Key.Down => 20, Key.Home => 122, Key.End => 123, _ => 0 };
        if (code == 0) return; Model.SendKey(code); e.Handled = true;
    }
    private void Video_TextInput(object? sender, TextInputEventArgs e)
    {
        if (Model?.IsRunning != true || string.IsNullOrEmpty(e.Text)) return;
        try { Model.Send(e.Text.All(x => x < 128) ? ScrcpyProtocol.Text(e.Text) : ScrcpyProtocol.Clipboard(e.Text, true)); e.Handled = true; }
        catch (ArgumentException ex) { Model.ShowMessage(ex.Message); }
    }
    private async Task PasteAsync()
    {
        if (Model?.IsRunning != true || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { var text = await clipboard.TryGetTextAsync(); if (!string.IsNullOrEmpty(text)) Model.Send(ScrcpyProtocol.Clipboard(text, true)); }
        catch (Exception ex) { Model.ShowMessage("粘贴失败：" + ex.Message); }
    }
    private async void Paste_Click(object? sender, RoutedEventArgs e) => await PasteAsync();
    private void Copy_Click(object? sender, RoutedEventArgs e) { _copyRequested = true; Model?.Send([8, 0]); }
    private async void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AndroidScreenViewModel.IsRunning) && Model?.IsRunning != true)
        { _dragging = false; _lastPosition = null; _copyRequested = false; }
        if (e.PropertyName != nameof(AndroidScreenViewModel.ReceivedClipboard) || !_copyRequested || Model?.ReceivedClipboard is not { } text) return;
        _copyRequested = false;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            { await clipboard.SetTextAsync(text); Model.ShowMessage("已复制手机剪贴板。"); }
        }
        catch (Exception ex) { Model?.ShowMessage("复制失败：" + ex.Message); }
    }
    private async void Screenshot_Click(object? sender, RoutedEventArgs e)
    {
        if (Model?.Frame is not { } frame || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        // Freeze the current image before opening a picker; live frames are disposed as they are replaced.
        using var snapshot = new MemoryStream(); frame.Save(snapshot, PngBitmapEncoderOptions.Default); snapshot.Position = 0;
        try
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            { Title = "保存手机截图", SuggestedFileName = $"Android-{DateTime.Now:yyyyMMdd-HHmmss}.png", DefaultExtension = "png",
                FileTypeChoices = [new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] }] });
            if (file is null) return;
            await using var output = await file.OpenWriteAsync(); await snapshot.CopyToAsync(output);
            Model.ShowMessage("截图已保存。");
        }
        catch (Exception ex) { Model?.ShowMessage("截图保存失败：" + ex.Message); }
    }
}
