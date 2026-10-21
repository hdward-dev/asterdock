using AsterDock.Contracts;
using AsterDock.UI.Controls;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VMCube.Module.Services;

namespace VMCube.Module.Views;

public sealed partial class CreateVmWindow : Window
{
    private LocalVmService _local = null!;
    private CancellationToken _token;
    private Func<bool> _alive = () => true;
    private IUserFeedbackService? _feedback;

    public CreateVmWindow()
    {
        InitializeComponent();
        createProtocol.ItemsSource = new[] { "SPICE", "VNC" };
        createProtocol.SelectedIndex = 0;
    }

    public CreateVmWindow(LocalVmService local, bool supportsSpice, CancellationToken token, Func<bool> alive, IUserFeedbackService? feedback = null) : this()
    {
        _local = local;
        _token = token;
        _alive = alive;
        _feedback = feedback;
        if (feedback is not null) FeedbackWindow.Attach(this, feedback);
        createProtocol.SelectedIndex = supportsSpice ? 0 : 1;
        protocolCaption.Text = supportsSpice ? "显示协议" : "显示协议（当前引擎支持 VNC）";
    }

    private async void ChooseIso_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择安装镜像", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("ISO 镜像") { Patterns = ["*.iso"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) createIso.Text = path;
    }

    private async void Create_Click(object? sender, RoutedEventArgs e)
    {
        createSubmit.IsEnabled = false;
        try
        {
            var vm = await _local.CreateAsync(createName.Text ?? "", (int)(createCpus.Value ?? 2), (int)(createMemory.Value ?? 2048), (int)(createDisk.Value ?? 32), createIso.Text?.Trim(), createProtocol.SelectedIndex == 0 ? ConsoleProtocol.Spice : ConsoleProtocol.Vnc, _token);
            Close(vm);
        }
        catch (Exception ex) { if (_alive()) { createError.Text = ex.Message; _feedback.Notify(FeedbackKind.Error, ex.Message, "创建失败"); } }
        finally { createSubmit.IsEnabled = true; }
    }
}
