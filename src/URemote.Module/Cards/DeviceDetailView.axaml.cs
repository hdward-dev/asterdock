using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace URemote.Module;

public partial class DeviceDetailView : UserControl
{
    public event EventHandler? Dismissed;
    public DeviceDetailView() => InitializeComponent();

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceDetailModel model) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } board)
            {
                await board.SetTextAsync(model.DeviceId);
                copyButton.Content = "已复制";
            }
        }
        catch { copyButton.Content = "复制失败"; }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Dismissed?.Invoke(this, EventArgs.Empty);
}
