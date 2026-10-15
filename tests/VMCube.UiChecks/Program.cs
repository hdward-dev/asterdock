using System.Reflection;
using AsterDock.Contracts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using VMCube.Module.Views;
using VMCube.Module.Services;
using URemote.Core;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Automation;

AppBuilder.Configure<UiCheckApp>().UsePlatformDetect().AfterSetup(builder =>
{
    DispatcherTimer.RunOnce(async () =>
    {
        var life = (IClassicDesktopStyleApplicationLifetime)builder.Instance!.ApplicationLifetime!;
        var root = Path.Combine(Path.GetTempPath(), "vmcube-ui-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var existingSnapshot = args.Contains("--existing-workspace");
        if (existingSnapshot) {
            var source = args[Array.IndexOf(args, "--existing-workspace") + 1];
            var target = Path.Combine(root, "local"); Directory.CreateDirectory(target);
            File.Copy(Path.Combine(source, "local", "machines.json"), Path.Combine(target, "machines.json"));
            foreach (var runtime in Directory.EnumerateFiles(Path.Combine(source, "local"), "runtime.json", SearchOption.AllDirectories)) {
                var destination = Path.Combine(target, Path.GetRelativePath(Path.Combine(source, "local"), runtime));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(runtime, destination);
                if (args.Contains("--existing-console")) {
                    var secret = Path.Combine(Path.GetDirectoryName(runtime)!, "console.secret");
                    var copy = Path.Combine(Path.GetDirectoryName(destination)!, "console.secret");
                    File.Copy(secret, copy);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(copy, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
        }
        var context = new CheckContext(root);
        using var view = new VMCubeView(context);
        var window = new Window { Title = "VMCUBE UI checks", Content = view, Width = 1200, Height = 900 };
        context.WindowService.Owner = window;
        life.MainWindow = window; window.Show();
        try
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            await Task.Delay(300);
            var engineStatus = (TextBlock)typeof(VMCubeView).GetField("_engineStatus", flags)!.GetValue(view)!;
            if (!engineStatus.Text!.Contains("QEMU 已就绪")) throw new Exception("Bundled QEMU was not detected on entering local workspace");
            await (Task)typeof(VMCubeView).GetMethod("CheckOrInstallEngineAsync", flags)!.Invoke(view, null)!;
            var status = (TextBlock)typeof(VMCubeView).GetField("_status", flags)!.GetValue(view)!;
            if (!status.Text!.Contains("无需重复安装")) throw new Exception("Existing engine incorrectly entered download flow");
            var screenshots = Path.GetFullPath(args.FirstOrDefault() ?? "/tmp/vmcube-ui"); Directory.CreateDirectory(screenshots);
            void Capture(string name)
            {
                using var image = new RenderTargetBitmap(new((int)window.Width, (int)window.Height)); image.Render(window); image.Save(Path.Combine(screenshots, name), PngBitmapEncoderOptions.Default);
            }
            var cards = (Grid)typeof(VMCubeView).GetField("_machineCards", flags)!.GetValue(view)!;
            int CardCount() => cards.Children.OfType<Border>().Count(x => x.Tag is string);
            void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            bool HasPreview() => cards.GetVisualDescendants().OfType<Image>().Any(x => x.Source is not null);
            async Task WaitForPreviewAsync(ConsoleWindow console) {
                for (var i = 0; i < 250 && !HasPreview() && !console.Completion.IsCompleted; i++) await Task.Delay(100);
                if (!HasPreview()) throw new Exception("Connected console did not update the VM card preview");
            }
            if (existingSnapshot) {
                if (CardCount() == 0) throw new Exception("Actual workspace was not loaded: " + status.Text);
                ConsoleWindow? actualConsole = null;
                try {
                    if (args.Contains("--existing-console")) {
                        var connect = cards.GetVisualDescendants().OfType<Button>().First(x => x.Name == "VmConnect");
                        if (AutomationProperties.GetName(connect) != "连接桌面") throw new Exception("Existing guest is not running; test will not start it");
                        Click(connect); await Task.Delay(100);
                        actualConsole = context.WindowService.Shown.OfType<ConsoleWindow>().Last();
                        await WaitForPreviewAsync(actualConsole);
                    }
                    Capture("actual-light.png"); builder.Instance.RequestedThemeVariant = ThemeVariant.Dark;
                    await Task.Delay(150); Capture("actual-dark.png");
                    if (actualConsole is not null) { actualConsole.Close(); await actualConsole.Completion; Capture("actual-recent.png"); }
                    Console.WriteLine("Actual workspace UI and connected card preview rendered; existing guest left running.");
                }
                finally { actualConsole?.Close(); }
                window.Close(); life.Shutdown(0); return;
            }
            await (Task)typeof(VMCubeView).GetMethod("DemoAsync", flags)!.Invoke(view, null)!;
            await Task.Delay(150);
            if (CardCount() != 4) throw new Exception("VM cards missing");
            var logo = view.GetVisualDescendants().OfType<Control>().Single(x => x.Name == "VmBrandLogo");
            var brand = (StackPanel)logo.Parent!;
            if (logo.Bounds.Bottom > brand.Children.OfType<TextBlock>().First(x => x.Text == "VMCUBE").Bounds.Top) throw new Exception("Logo is not above VMCUBE");
            foreach (var iconButton in cards.GetVisualDescendants().OfType<Button>().Where(x => x.Name is "VmConnect" or "VmPause" or "VmPower" or "VmDetails"))
                if (iconButton.Content is string || ToolTip.GetTip(iconButton) is null || string.IsNullOrEmpty(AutomationProperties.GetName(iconButton))) throw new Exception("Card icon button lacks tooltip/accessibility name");
            Capture("light.png");
            builder.Instance.RequestedThemeVariant = ThemeVariant.Dark; await Task.Delay(150); Capture("dark.png");
            var search = (TextBox)typeof(VMCubeView).GetField("_search", flags)!.GetValue(view)!;
            search.Text = "windows"; await Task.Delay(80);
            if (CardCount() != 1) throw new Exception("UI search failed");
            search.Text = ""; await Task.Delay(80);
            var filter = (ComboBox)typeof(VMCubeView).GetField("_filter", flags)!.GetValue(view)!;
            filter.SelectedIndex = 1; await Task.Delay(80);
            if (CardCount() != 2) throw new Exception("UI state filter failed");
            Click(view.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "VmViewMode")); await Task.Delay(80);
            if (cards.ColumnDefinitions.Count != 1) throw new Exception("List view did not switch to one column");
            Capture("list.png");
            Click(cards.GetVisualDescendants().OfType<Button>().First(x => x.Name == "VmFavorite")); await Task.Delay(120);
            if (!File.Exists(Path.Combine(root, "favorites.json"))) throw new Exception("Favorites were not persisted");
            var tabs = (List<Button>)typeof(VMCubeView).GetField("_catalogTabs", flags)!.GetValue(view)!;
            Click(tabs[1]); await Task.Delay(80);
            if (CardCount() != 1) throw new Exception("Favorites filter failed");
            var navigation = (List<Button>)typeof(VMCubeView).GetField("_navigation", flags)!.GetValue(view)!;
            Click(navigation[1]); await Task.Delay(100);
            var remote = (Border)typeof(VMCubeView).GetField("_remoteConnection", flags)!.GetValue(view)!;
            if (!remote.IsVisible) throw new Exception("Remote host connection form missing");
            builder.Instance.RequestedThemeVariant = ThemeVariant.Light; await Task.Delay(100); Capture("remote.png");
            Click(navigation[2]); await Task.Delay(80);
            var page = (ContentControl)typeof(VMCubeView).GetField("_page", flags)!.GetValue(view)!;
            Click(page.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "连接桌面"))); await Task.Delay(80);
            if (!page.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("请输入 IP 地址") == true)) throw new Exception("Direct connection validation missing");
            Capture("direct.png");
            Click(navigation[3]); await Task.Delay(80); Capture("history.png");
            Click(navigation[4]); await Task.Delay(80);
            if (!page.GetVisualDescendants().OfType<TextBox>().Any(x => x.PlaceholderText?.Contains("QEMU 引擎目录") == true)) throw new Exception("Engine settings missing");
            Capture("settings.png");
            Click(navigation[0]); await Task.Delay(200);
            await (Task)typeof(VMCubeView).GetMethod("DemoAsync", flags)!.Invoke(view, null)!;
            filter.SelectedIndex = 0; Click(tabs[0]);
            Click(view.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "VmViewMode"));
            var details = (StackPanel)typeof(VMCubeView).GetField("_details", flags)!.GetValue(view)!;
            Click(details.GetVisualDescendants().OfType<Button>().Single(x => AutomationProperties.GetName(x) == "正常关机")); await Task.Delay(60);
            if (!details.GetVisualDescendants().OfType<Button>().Any(x => Equals(x.Content, "确认关机"))) throw new Exception("Shutdown confirmation missing");
            Click(details.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "取消")));
            window.Width = 720; await Task.Delay(200); Capture("narrow.png");
            window.Width = 1200; await Task.Delay(100);
            if (TopLevel.GetTopLevel(details) is null || CardCount() != 4) throw new Exception("Responsive detail panel failed to reattach");
            var importDialog = (Window)typeof(VMCubeView).GetMethod("BuildImportDialog", flags)!.Invoke(view, null)!;
            importDialog.Show(window); await Task.Delay(100);
            if (!importDialog.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("UEFI / TPM / QEMU") == true)) throw new Exception("Import firmware controls missing");
            var submit = importDialog.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "ImportSubmit");
            submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
            if (!importDialog.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("文件不存在或路径无效") == true)) throw new Exception("Import validation error was not shown");
            using (var image = new RenderTargetBitmap(new((int)importDialog.Width, (int)importDialog.Height)))
            { image.Render(importDialog); image.Save(Path.Combine(screenshots, "import.png"), PngBitmapEncoderOptions.Default); }
            importDialog.Close();
            if (args.Contains("--console-qemu"))
            {
                using var service = new LocalVmService(root) { EngineDirectory = Path.GetDirectoryName(Path.GetFullPath(args[Array.IndexOf(args, "--console-qemu") + 1])) };
                var vm = await service.CreateAsync("SPICE UI", 1, 256, 1, null, ConsoleProtocol.Spice, default);
                ConsoleWindow? console = null;
                try
                {
                    await service.StartAsync(vm.Id, default);
                    await (Task)typeof(VMCubeView).GetMethod("ConnectAsync", flags)!.Invoke(view, null)!;
                    var connectButton = cards.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "VmConnect");
                    Click(connectButton); await Task.Delay(100);
                    console = context.WindowService.Shown.OfType<ConsoleWindow>().Last();
                    await WaitForPreviewAsync(console);
                    Capture("spice-preview.png");
                    var bitmapField = typeof(ConsoleWindow).GetField("bitmap", flags)!;
                    for (var i = 0; i < 100 && bitmapField.GetValue(console) is null && !console.Completion.IsCompleted; i++) await Task.Delay(100);
                    if (bitmapField.GetValue(console) is null) throw new Exception("SPICE window did not paint a frame");
                    using var image = new RenderTargetBitmap(new((int)console.Width, (int)console.Height)); image.Render(console); image.Save(Path.Combine(screenshots, "spice-console.png"), PngBitmapEncoderOptions.Default);
                    search.Text = "no match"; search.Text = ""; await Task.Delay(60);
                    if (!HasPreview()) throw new Exception("Preview was lost when cards were rebuilt");
                    console.Close(); await console.Completion;
                    if (!HasPreview() || !cards.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.StartsWith("最近画面") == true)) throw new Exception("Disconnect did not retain the last preview with correct status");
                    Capture("spice-preview-disconnected.png");
                    Click(cards.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "VmConnect")); await Task.Delay(100);
                    console = context.WindowService.Shown.OfType<ConsoleWindow>().Last();
                    for (var i = 0; i < 100 && !cards.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.StartsWith("实时预览") == true); i++) await Task.Delay(100);
                    if (!cards.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.StartsWith("实时预览") == true)) throw new Exception("Reconnect did not restore live preview");
                    console.Close(); await console.Completion;
                    Console.WriteLine("Real SPICE desktop painted in owned window and VM card; preview survived search, disconnect and reconnect.");
                }
                finally
                {
                    console?.Close();
                    try { await service.ActAsync(vm.Id, "destroy", default); } catch (InvalidOperationException) { }
                }
            }
            Console.WriteLine("UI checks passed: cards/list, search/filter, persistent favorites, navigation, direct connection validation, shutdown confirmation, light/dark and responsive layout. " + screenshots);
            window.Close(); life.Shutdown(0);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); window.Close(); life.Shutdown(1); }
        finally { Directory.Delete(root, recursive: true); }
    }, TimeSpan.FromMilliseconds(100));
}).StartWithClassicDesktopLifetime(args);

public sealed class UiCheckApp : Application { public override void Initialize() => AvaloniaXamlLoader.Load(this); }
internal sealed class CheckContext(string directory) : IApplicationContext
{
    public string ApplicationId => "vmcube";
    public string DataDirectory => directory;
    public CheckWindows WindowService { get; } = new();
    public IWindowService Windows => WindowService;
    public IApplicationShell Shell => throw new NotSupportedException();
    public ISystemMetricsService SystemMetrics => throw new NotSupportedException();
}

internal sealed class CheckWindows : IWindowService
{
    public List<Window> Shown { get; } = [];
    public Window? Owner { get; set; }
    public void Show(Window window, bool owned = true) { Shown.Add(window); if (owned && Owner is not null) window.Show(Owner); else window.Show(); }
    public Window ShowOrActivate(string key, Func<Window> factory, bool owned = true) { var window = factory(); Show(window, owned); return window; }
    public Task<TResult> ShowDialogAsync<TResult>(Window window) => throw new NotSupportedException();
    public void CloseAll() { foreach (var window in Shown) window.Close(); }
}
