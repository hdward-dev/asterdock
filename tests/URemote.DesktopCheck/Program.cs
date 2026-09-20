using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AsterDock.Host;
using AsterDock.Host.Views;

var preview = args.FirstOrDefault();
var moduleUi = args.Contains("--module-ui");
if (moduleUi) Environment.SetEnvironmentVariable("UREMOTE_NO_AUTO_START", "1");
AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().AfterSetup(builder =>
{
    if (args.Contains("--controller-window")) DispatcherTimer.RunOnce(async () =>
    {
        if (builder.Instance?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime life) return;
        var state = URemote.Host.DesktopHostSession.ReadIdentity(Environment.GetEnvironmentVariable("UREMOTE_IDENTITY")!).State;
        using var api = new URemote.Core.UuMacHostApi(state);
        var device = (await api.GetDevicesAsync()).First(x => !x.IsCurrent && x.Online && x.Controllable && x.ControlledSupport && x.Platform == 1);
        var window = new URemote.Module.RemoteDesktopWindow(device, state, Environment.GetEnvironmentVariable("UREMOTE_FFMPEG")!);
        window.Show();
        await Task.Delay(10000);
        var bitmap = typeof(URemote.Module.RemoteDesktopWindow).GetField("bitmap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window);
        Console.WriteLine(bitmap is null ? "FAIL: remote window did not paint video" : "PASS: independent native remote window painted real remote video");
        window.Close(); await window.Completion;
        if (life.MainWindow is MainWindow main) main.PrepareForShutdown();
        life.Shutdown(bitmap is null ? 1 : 0);
    }, TimeSpan.FromSeconds(2));
    if (moduleUi && !args.Contains("--controller-window")) DispatcherTimer.RunOnce(() =>
    {
        if (builder.Instance?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime life || life.MainWindow is not MainWindow win) return;
        var dir = Path.Combine(Path.GetTempPath(), "uremote-ui-review"); Directory.CreateDirectory(dir);
        var view = new URemote.Module.URemoteView(dir);
        win.MinWidth = 0; win.MinHeight = 0; win.Content = view; win.Width = args.Contains("--narrow") ? 640 : 1440; win.Height = 880;
        if (args.Contains("--dark")) builder.Instance.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        var type = typeof(URemote.Module.URemoteView);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        if (!args.Contains("--empty"))
        {
            type.GetField("devices", flags)!.SetValue(view, new URemote.Core.UuDevice[] {
                new("fixture-linux", "aster-nixos", "desktop", 4, "CONNECTED", true, true, "4.41.0", true),
                new("fixture-win", "工作室 Windows", "desktop", 1, "CONNECTED", true, true, "4.41.0", false),
                new("fixture-mac", "MacBook Pro", "desktop", 4, "DISCONNECTED", true, true, "4.41.0", false),
                new("fixture-ios", "iPhone", "mobile", 3, "DISCONNECTED", false, false, "4.41.0", false) });
            type.GetMethod("RenderDevices", flags)!.Invoke(view, null);
            var filter = (Avalonia.Controls.ComboBox)type.GetField("filter", flags)!.GetValue(view)!;
            var rows = (Avalonia.Controls.StackPanel)type.GetField("deviceRows", flags)!.GetValue(view)!;
            filter.SelectedIndex = 1;
            if (rows.Children.Count != 2) throw new Exception("Online filter failed.");
            filter.SelectedIndex = 0;
            var search = (Avalonia.Controls.TextBox)type.GetField("search", flags)!.GetValue(view)!;
            search.Text = "MacBook";
            type.GetMethod("RenderDevices", flags)!.Invoke(view, null);
            if (rows.Children.Count != 1) throw new Exception("Device search failed.");
            search.Text = "";
            type.GetMethod("RenderDevices", flags)!.Invoke(view, null);
            ((Avalonia.Controls.TextBlock)type.GetField("catalogHint", flags)!.GetValue(view)!).Text = "演示设备 · 4 台设备 · 2 台在线";
            Console.WriteLine("PASS: native device list online filter and name search");
        }
        var selected = args.Contains("--settings") ? 2 : args.Contains("--host") ? 1 : 0;
        type.GetMethod("ShowPage", flags)!.Invoke(view, [selected]);
    }, TimeSpan.FromSeconds(2));
    if (!args.Contains("--controller-window")) DispatcherTimer.RunOnce(() =>
    {
        if (builder.Instance?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime || lifetime.MainWindow is not MainWindow window) return;
        if (preview is not null)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
            bitmap.Render(window); bitmap.Save(preview, PngBitmapEncoderOptions.Default);
            Console.WriteLine("AsterDock window rendered.");
            window.PrepareForShutdown(); lifetime.Shutdown();
        }
    }, TimeSpan.FromSeconds(14));
}).StartWithClassicDesktopLifetime([]);
