using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VMCube.Module.Services;

namespace VMCube.Module.Views;

public sealed partial class ConsoleWindow
{
    private Button _upload = null!;
    private readonly TextBlock _fileStatus = new() { Foreground = Brushes.LightGray, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, Name = "ConsoleFileStatus" };
    private readonly ProgressBar _fileProgress = new() { Minimum = 0, Maximum = 100, Height = 4, IsVisible = false };
    private Button _cancelFiles = null!;
    private CancellationTokenSource? _fileStop;
    private bool _sendingFiles;

    private Control BuildFileTransfers()
    {
        var panel = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(16, 6, 16, 0), IsVisible = session is SpiceDesktopSession };
        var details = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { _fileStatus, _fileProgress } };
        panel.Children.Add(details);
        _cancelFiles = VMCubeIcons.Button("取消发送", "cancel"); _cancelFiles.Name = "ConsoleCancelFiles"; _cancelFiles.IsVisible = false;
        _cancelFiles.Click += (_, _) => { _cancelFiles.IsEnabled = false; _fileStatus.Text = "正在取消…"; _fileStop?.Cancel(); };
        Grid.SetColumn(_cancelFiles, 1); panel.Children.Add(_cancelFiles);
        var help = VMCubeIcons.Button("文件传输配置说明", "info"); help.Name = "ConsoleFileHelp";
        help.Click += async (_, _) => await ShowFileHelpAsync(); Grid.SetColumn(help, 2); panel.Children.Add(help);
        if (session is SpiceDesktopSession spice)
        {
            _fileStatus.Text = "连接后可选择文件，或将文件拖入桌面窗口。";
            spice.FileTransferAvailability += (_, hint) => Dispatcher.UIThread.Post(() => { if (!closed && !_sendingFiles) _fileStatus.Text = hint; });
            DragDrop.SetAllowDrop(desktop, true);
            desktop.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            {
                e.DragEffects = connected && !_sendingFiles && e.DataTransfer.TryGetFiles()?.Any() == true ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            });
            desktop.AddHandler(DragDrop.DropEvent, async (_, e) =>
            {
                e.Handled = true;
                if (!_sendingFiles && connected) await SendStorageFilesAsync(e.DataTransfer.TryGetFiles()?.ToArray() ?? []);
            });
        }
        return panel;
    }
    private async Task ChooseFilesAsync()
    {
        if (_sendingFiles || session is not SpiceDesktopSession spice) return;
        if (!spice.CanTransferFiles) { await ShowFileHelpAsync(); return; }
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "发送文件到虚拟机", AllowMultiple = true });
            await SendStorageFilesAsync(files);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { if (!closed) _fileStatus.Text = "无法选择文件：" + e.Message; }
    }
    private async Task SendStorageFilesAsync(IEnumerable<IStorageItem> items)
    {
        if (closed || _sendingFiles || session is not SpiceDesktopSession spice) return;
        if (!spice.CanTransferFiles) { await ShowFileHelpAsync(); return; }
        var files = items.ToArray(); if (files.Length == 0) return;
        if (files.Length > 100) { _fileStatus.Text = "一次最多发送 100 个文件；较多文件请先压缩。"; return; }
        var paths = files.Select(x => x is IStorageFile ? x.TryGetLocalPath() : null).ToArray();
        if (paths.Any(x => x is null)) { _fileStatus.Text = "仅支持本机文件；文件夹请先压缩后发送。"; return; }
        _sendingFiles = true; _upload.IsEnabled = false; _cancelFiles.IsVisible = true; _cancelFiles.IsEnabled = true;
        _fileProgress.IsVisible = true; _fileProgress.Value = 0;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); _fileStop = cancel;
        var sent = 0;
        try
        {
            foreach (var path in paths)
            {
                cancel.Token.ThrowIfCancellationRequested();
                var clock = Stopwatch.StartNew(); var name = Path.GetFileName(path!); var index = sent + 1;
                _fileStatus.Text = $"{index}/{paths.Length} · {name} · 等待虚拟机接收…"; _fileProgress.Value = 0;
                var progress = new Progress<SpiceFileProgress>(p =>
                {
                    if (closed || cancel.IsCancellationRequested || !_sendingFiles || index != sent + 1) return;
                    _fileProgress.Value = p.Total > 0 ? 100d * p.Transferred / p.Total : 0;
                    _fileStatus.Text = $"{index}/{paths.Length} · {name} · {FormatBytes(p.Transferred)} / {FormatBytes(p.Total)} · {FormatBytes((long)(p.Transferred / Math.Max(clock.Elapsed.TotalSeconds, .1)))}/s";
                });
                await spice.SendFileAsync(path!, progress, cancel.Token); sent++;
            }
            _fileProgress.Value = 100; _fileStatus.Text = $"已发送 {sent} 个文件。接收位置由虚拟机中的 SPICE Agent 决定。";
        }
        catch (OperationCanceledException) { if (!closed) _fileStatus.Text = $"已取消发送 · 已完成 {sent}/{paths.Length} 个文件。"; }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        { if (!closed) _fileStatus.Text = $"已完成 {sent}/{paths.Length} · {e.Message}"; }
        finally
        {
            _sendingFiles = false; _fileStop = null;
            if (!closed) { _cancelFiles.IsVisible = false; _upload.IsEnabled = IsConnected; }
        }
    }
    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.0} KiB" : $"{bytes} B";
    private async Task ShowFileHelpAsync()
    {
        if (closed) return;
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
            "发送方式：点击上传图标选择文件，或把文件拖入虚拟机画面。支持多文件，文件夹请先压缩。\n\n" +
            "Windows：在虚拟机中安装 SPICE Guest Tools（含 VirtIO 串口驱动和 SPICE Agent），完成后登录桌面。\n\n" +
            "Linux：安装并运行 spice-vdagent，然后登录图形桌面。\n\n" +
            "虚拟机还需要 com.redhat.spice.0 通道。VMCUBE 新启动的本机 SPICE 虚拟机会自动添加；已有运行中的虚拟机需要正常关机后重新启动。远程主机可在 virt-manager 添加 Spice agent 通道。\n\n" +
            "安装或配置后重新连接。接收位置和重名文件处理由访客端 Agent 决定。" };
        var dialog = new Window { Title = "文件传输配置 · VMCUBE", Width = 560, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        const string downloadUrl = "https://www.spice-space.org/download.html";
        var address = new TextBox { Text = downloadUrl, IsReadOnly = true, Name = "GuestToolsDownloadUrl" };
        var feedback = new TextBlock { Name = "GuestToolsDownloadStatus", TextWrapping = TextWrapping.Wrap,
            Text = "下载页将在本机浏览器打开，增强工具需要安装在 Windows 虚拟机中。" };
        var download = new Button { Content = "在本机浏览器打开下载页", Name = "GuestToolsOpenDownload" };
        download.Click += async (_, _) =>
        {
            download.IsEnabled = false; feedback.Text = "正在打开本机浏览器…";
            try { feedback.Text = (await ExternalBrowser.OpenAsync(new Uri(downloadUrl), uri => dialog.Launcher.LaunchUriAsync(uri))).Message; }
            catch (Exception e) when (e is not OutOfMemoryException) { feedback.Text = "未能打开浏览器。请复制下方链接手动访问。"; }
            finally { download.IsEnabled = true; }
        };
        var copy = new Button { Content = "复制链接", Name = "GuestToolsCopyDownload" };
        copy.Click += async (_, _) =>
        {
            try
            {
                if (dialog.Clipboard is not { } clipboard) { feedback.Text = "剪贴板不可用，请选中下方网址复制。"; return; }
                await clipboard.SetTextAsync(downloadUrl); feedback.Text = "下载链接已复制。请在浏览器地址栏粘贴访问。";
            }
            catch (Exception e) when (e is not OutOfMemoryException) { feedback.Text = "复制失败，请选中下方网址手动复制。"; }
        };
        var links = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Children = { download, copy } };
        var done = new Button { Content = "知道了", HorizontalAlignment = HorizontalAlignment.Right }; done.Click += (_, _) => dialog.Close();
        dialog.Content = new ScrollViewer { MaxHeight = 680, Content = new StackPanel { Margin = new Thickness(24), Spacing = 18, Children = { text, links, feedback, address, done } } };
        await dialog.ShowDialog(this);
    }
}
