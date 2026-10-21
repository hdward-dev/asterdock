using AsterDock.Contracts;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using VMCube.Module.Services;

namespace VMCube.Module.Views;

public sealed partial class GuestToolsWindow : Window
{
    private const string DownloadUrl = "https://www.spice-space.org/download.html";

    private IUserFeedbackService? _feedback;
    public GuestToolsWindow() { InitializeComponent(); _feedback = null; }
    public GuestToolsWindow(IUserFeedbackService? feedback) : this() => _feedback = feedback;

    private async void OpenDownload_Click(object? sender, RoutedEventArgs e)
    {
        GuestToolsOpenDownload.IsEnabled = false;
        GuestToolsDownloadStatus.Text = "正在打开本机浏览器…";
        try { GuestToolsDownloadStatus.Text = (await ExternalBrowser.OpenAsync(new Uri(DownloadUrl), uri => Launcher.LaunchUriAsync(uri))).Message; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            GuestToolsDownloadStatus.Text = "未能打开浏览器。请复制下方链接手动访问。";
            _feedback.Notify(FeedbackKind.Error, GuestToolsDownloadStatus.Text, "打开浏览器");
        }
        finally { GuestToolsOpenDownload.IsEnabled = true; }
    }

    private async void CopyDownload_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is not { } clipboard) { GuestToolsDownloadStatus.Text = "剪贴板不可用，请选中下方网址复制。"; return; }
            await clipboard.SetTextAsync(DownloadUrl);
            GuestToolsDownloadStatus.Text = "下载链接已复制。请在浏览器地址栏粘贴访问。";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            GuestToolsDownloadStatus.Text = "复制失败，请选中下方网址手动复制。";
            _feedback.Notify(FeedbackKind.Error, GuestToolsDownloadStatus.Text, "复制链接");
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
