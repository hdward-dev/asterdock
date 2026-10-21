using AsterDock.Contracts;
using AsterDock.UI.Controls;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VMCube.Module.Services;

namespace VMCube.Module.Views;

public sealed partial class ImportVmWindow : Window
{
    private static readonly string[] NetworkModels = ["e1000", "e1000e", "virtio-net-pci"];
    private static readonly string[] VideoModels = ["std", "qxl", "virtio"];
    private LocalVmService _local = null!;
    private CancellationToken _token;
    private IUserFeedbackService? _feedback;

    public ImportVmWindow()
    {
        InitializeComponent();
        importProtocol.ItemsSource = new[] { "SPICE", "VNC" };
        importProtocol.SelectedIndex = 0;
        importNetwork.ItemsSource = NetworkModels;
        importNetwork.SelectedIndex = 0;
        importVideo.ItemsSource = VideoModels;
        importVideo.SelectedIndex = 0;
    }

    public ImportVmWindow(LocalVmService local, CancellationToken token, IUserFeedbackService? feedback = null) : this()
    {
        _local = local;
        _token = token;
        _feedback = feedback;
        if (feedback is not null) FeedbackWindow.Attach(this, feedback);
    }

    private async void LoadProfile_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择 VMCUBE 导入配置", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("VMCUBE 配置") { Patterns = ["*.vmcube.json"] }] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            var profile = (await LocalVmImport.ReadProfileAsync(path, _token)).Validate();
            importName.Text = profile.Name; importCpus.Value = profile.Cpus; importMemory.Value = profile.MemoryMiB; importDisk.Text = profile.DiskPath; importQemu.Text = profile.QemuPath;
            importFirmware.Text = profile.FirmwareCodePath; importNvram.Text = profile.NvramPath; importTpm.Text = profile.TpmStateDirectory; importSwtpm.Text = profile.SwtpmPath;
            importSecure.IsChecked = profile.SecureBoot; importHost.IsChecked = profile.CpuHost; importKvm.IsChecked = profile.RequireKvm; importLocaltime.IsChecked = profile.UseLocalTime;
            importNetwork.SelectedIndex = Array.IndexOf(NetworkModels, profile.NetworkModel);
            importVideo.SelectedIndex = Array.IndexOf(VideoModels, profile.VideoModel);
            importMac.Text = profile.MacAddress; importGuestAgent.Text = profile.GuestAgentSocket;
            importSummary.Text = profile.Summary; importError.Text = "";
        }
        catch (Exception ex) { Note(ex); }
    }

    private async void ChooseDisk_Click(object? sender, RoutedEventArgs e) => await PickFileAsync(importDisk, "现有虚拟磁盘", "*.qcow2");
    private async void ChooseQemu_Click(object? sender, RoutedEventArgs e) => await PickFileAsync(importQemu, "原有 QEMU 程序");
    private async void ChooseFirmware_Click(object? sender, RoutedEventArgs e) => await PickFileAsync(importFirmware, "UEFI CODE 固件");
    private async void ChooseNvram_Click(object? sender, RoutedEventArgs e) => await PickFileAsync(importNvram, "原有 UEFI NVRAM");
    private async void ChooseSwtpm_Click(object? sender, RoutedEventArgs e) => await PickFileAsync(importSwtpm, "swtpm 程序");

    private async void ChooseTpm_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择原有 TPM 状态目录", AllowMultiple = false });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) importTpm.Text = path;
        }
        catch (Exception ex) { Note(ex); }
    }

    private async void Import_Click(object? sender, RoutedEventArgs e)
    {
        ImportSubmit.IsEnabled = false; importLoad.IsEnabled = false;
        try
        {
            var profile = new LocalVmImport(importName.Text ?? "", (int)(importCpus.Value ?? 4), (int)(importMemory.Value ?? 8192), importDisk.Text?.Trim() ?? "",
                Optional(importQemu), Optional(importFirmware), Optional(importNvram), Optional(importTpm), Optional(importSwtpm), importSecure.IsChecked == true, importHost.IsChecked == true,
                importKvm.IsChecked == true, importLocaltime.IsChecked == true, (string)importNetwork.SelectedItem!, (string)importVideo.SelectedItem!, Optional(importMac), Optional(importGuestAgent));
            var vm = await _local.ImportAsync(profile, importProtocol.SelectedIndex == 0 ? ConsoleProtocol.Spice : ConsoleProtocol.Vnc, _token);
            Close(vm);
        }
        catch (Exception ex) { Note(ex); }
        finally { ImportSubmit.IsEnabled = true; importLoad.IsEnabled = true; }
    }

    private async Task PickFileAsync(TextBox target, string title, string pattern = "*")
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(title) { Patterns = [pattern] }] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) target.Text = path;
        }
        catch (Exception ex) { Note(ex); }
    }

    private void Note(Exception ex)
    {
        importError.Text = ex.Message;
        _feedback.Notify(FeedbackKind.Error, ex.Message, "导入失败");
    }

    private static string? Optional(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
}
