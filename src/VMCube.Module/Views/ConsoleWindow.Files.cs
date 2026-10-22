using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VMCube.Module.Services;

namespace VMCube.Module.Views;

public sealed partial class ConsoleWindow
{
    private CancellationTokenSource? _fileStop;
    private bool _sendingFiles;

    private void WireFileTransfers()
    {
        var spiceSession = session is SpiceDesktopSession;
        fileBar.IsVisible = spiceSession;
        ConsoleSendFiles.IsVisible = spiceSession;
        if (session is not SpiceDesktopSession spice) return;
        ConsoleFileStatus.Text = "连接后可选择文件，或将文件拖入桌面窗口。";
        spice.FileTransferAvailability += (_, hint) => Dispatcher.UIThread.Post(() => { if (!closed && !_sendingFiles) ConsoleFileStatus.Text = hint; });
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

    private async void Upload_Click(object? sender, RoutedEventArgs e) => await ChooseFilesAsync();
    private void CancelFiles_Click(object? sender, RoutedEventArgs e)
    {
        ConsoleCancelFiles.IsEnabled = false;
        ConsoleFileStatus.Text = "正在取消…";
        _fileStop?.Cancel();
    }
    private async void FileHelp_Click(object? sender, RoutedEventArgs e) => await ShowFileHelpAsync();
    private async Task ChooseFilesAsync()
    {
        if (_sendingFiles || session is not SpiceDesktopSession spice) return;
        if (!spice.CanTransferFiles) { await ShowFileHelpAsync(); return; }
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "发送文件到虚拟机", AllowMultiple = true });
            await SendStorageFilesAsync(files);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { if (!closed) ConsoleFileStatus.Text = "无法选择文件：" + e.Message; }
    }
    private async Task SendStorageFilesAsync(IEnumerable<IStorageItem> items)
    {
        if (closed || _sendingFiles || session is not SpiceDesktopSession spice) return;
        if (!spice.CanTransferFiles) { await ShowFileHelpAsync(); return; }
        var files = items.ToArray(); if (files.Length == 0) return;
        if (files.Length > 100) { ConsoleFileStatus.Text = "一次最多发送 100 个文件；较多文件请先压缩。"; return; }
        var paths = files.Select(x => x is IStorageFile ? x.TryGetLocalPath() : null).ToArray();
        if (paths.Any(x => x is null)) { ConsoleFileStatus.Text = "仅支持本机文件；文件夹请先压缩后发送。"; return; }
        _sendingFiles = true; ConsoleSendFiles.IsEnabled = false; ConsoleCancelFiles.IsVisible = true; ConsoleCancelFiles.IsEnabled = true;
        fileProgress.IsVisible = true; fileProgress.Value = 0;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); _fileStop = cancel;
        var sent = 0;
        try
        {
            foreach (var path in paths)
            {
                cancel.Token.ThrowIfCancellationRequested();
                var clock = Stopwatch.StartNew(); var name = Path.GetFileName(path!); var index = sent + 1;
                ConsoleFileStatus.Text = $"{index}/{paths.Length} · {name} · 等待虚拟机接收…"; fileProgress.Value = 0;
                var progress = new Progress<SpiceFileProgress>(p =>
                {
                    if (closed || cancel.IsCancellationRequested || !_sendingFiles || index != sent + 1) return;
                    fileProgress.Value = p.Total > 0 ? 100d * p.Transferred / p.Total : 0;
                    ConsoleFileStatus.Text = $"{index}/{paths.Length} · {name} · {FormatBytes(p.Transferred)} / {FormatBytes(p.Total)} · {FormatBytes((long)(p.Transferred / Math.Max(clock.Elapsed.TotalSeconds, .1)))}/s";
                });
                await spice.SendFileAsync(path!, progress, cancel.Token); sent++;
            }
            fileProgress.Value = 100; ConsoleFileStatus.Text = $"已发送 {sent} 个文件。接收位置由虚拟机中的 SPICE Agent 决定。";
        }
        catch (OperationCanceledException) { if (!closed) ConsoleFileStatus.Text = $"已取消发送 · 已完成 {sent}/{paths.Length} 个文件。"; }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        { if (!closed) ConsoleFileStatus.Text = $"已完成 {sent}/{paths.Length} · {e.Message}"; }
        finally
        {
            _sendingFiles = false; _fileStop = null;
            if (!closed) { ConsoleCancelFiles.IsVisible = false; ConsoleSendFiles.IsEnabled = IsConnected; }
        }
    }
    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.0} KiB" : $"{bytes} B";
    private async Task ShowFileHelpAsync()
    {
        if (closed) return;
        var help = new GuestToolsWindow(feedback);
        AsterDock.UI.Controls.FeedbackWindow.Attach(help, feedback);
        await help.ShowDialog(this);
    }
}
