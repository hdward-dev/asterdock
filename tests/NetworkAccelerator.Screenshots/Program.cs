using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using NetworkAccelerator.Core.Models;
using NetworkAccelerator.Module.Controls;
using NetworkAccelerator.Module.ViewModels;
using NetworkAccelerator.Module.Views;
using Semi.Avalonia;
using Ursa.Themes.Semi;

var output = args.FirstOrDefault(argument => !argument.StartsWith('-')) ?? "/opt/cursor/artifacts/screenshots";
Directory.CreateDirectory(output);

AppBuilder.Configure<ShotApp>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

DashboardMotion.ReduceMotion = true;
Capture("overview-light-disconnected", ThemeVariant.Light, Disconnected(), 1360, 1180, output);
Capture("overview-light-populated", ThemeVariant.Light, Populated(allowLan: false), 1360, 1280, output);
Capture("overview-dark-disconnected", ThemeVariant.Dark, Disconnected(), 1360, 1180, output);
Capture("overview-dark-populated", ThemeVariant.Dark, Populated(allowLan: true), 1360, 1560, output);
Capture("overview-narrow-populated", ThemeVariant.Light, Populated(allowLan: false), 760, 2200, output);
CaptureMotion(Path.Combine(output, "overview-motion"));
Console.WriteLine("PASS dashboard screenshots in " + output);

static void CaptureMotion(string directory)
{
    DashboardMotion.ReduceMotion = false;
    MotionClock.Manual = true;
    Directory.CreateDirectory(directory);
    var temp = Path.Combine(Path.GetTempPath(), "na-motion-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
    var viewModel = new NetworkAcceleratorViewModel(temp, temp);
    Window? window = null;
    try
    {
        viewModel.ShowDashboardPreview(Populated(allowLan: false));
        var view = new NetworkAcceleratorView
        {
            DataContext = viewModel,
            Width = 1360,
            Height = 1280,
            FontFamily = new FontFamily("WenQuanYi Micro Hei, Droid Sans Fallback, Inter")
        };
        window = new Window
        {
            Width = 1360,
            Height = 1280,
            Content = view,
            Background = Brushes.Transparent,
            WindowDecorations = WindowDecorations.None,
            FontFamily = view.FontFamily
        };
        window.Show();
        var frame = 0;
        for (var i = 0; i < 18; i++) SaveMotion(window, directory, ref frame);
        viewModel.PreviewSetMode(ProxyMode.Global);
        for (var i = 0; i < 10; i++) SaveMotion(window, directory, ref frame);
        viewModel.PreviewSetTun(true);
        viewModel.Dash.NudgeTraffic(320 * 1024, (long)(2.4 * 1024 * 1024));
        viewModel.Dash.Google.Apply(240, true);
        for (var i = 0; i < 12; i++) SaveMotion(window, directory, ref frame);
        viewModel.ShowDashboardPreview(Populated(allowLan: false, liveCount: 2));
        for (var i = 0; i < 10; i++) SaveMotion(window, directory, ref frame);
        Console.WriteLine("motion frames " + frame);
    }
    finally
    {
        window?.Close();
        viewModel.Dispose();
        Directory.Delete(temp, recursive: true);
DashboardMotion.ReduceMotion = true;
        MotionClock.Manual = false;
        }
}

static void SaveMotion(Window window, string directory, ref int frame)
{
    MotionClock.Advance(TimeSpan.FromMilliseconds(40));
    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    var bitmap = window.CaptureRenderedFrame();
    if (bitmap is null) return;
    bitmap.Save(Path.Combine(directory, $"frame-{frame:000}.png"), new PngBitmapEncoderOptions());
    frame++;
}

static void Capture(string name, ThemeVariant theme, DashboardPreview preview, double width, double height, string output)
{
    Application.Current!.RequestedThemeVariant = theme;
    var directory = Path.Combine(Path.GetTempPath(), "na-shot-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var viewModel = new NetworkAcceleratorViewModel(directory, directory);
    Window? window = null;
    try
    {
        viewModel.ShowDashboardPreview(preview);
        var view = new NetworkAcceleratorView
        {
            DataContext = viewModel,
            Width = width,
            Height = height,
            FontFamily = new FontFamily("WenQuanYi Micro Hei, Droid Sans Fallback, Inter")
        };
        window = new Window
        {
            Width = width,
            Height = height,
            Content = view,
            Background = Brushes.Transparent,
            WindowDecorations = WindowDecorations.None,
            FontFamily = view.FontFamily
        };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException(name + " did not render");
        var path = Path.Combine(output, name + ".png");
        frame.Save(path, new PngBitmapEncoderOptions());
        Console.WriteLine(path);
    }
    finally
    {
        window?.Close();
        viewModel.Dispose();
        Directory.Delete(directory, recursive: true);
    }
}

static DashboardPreview Disconnected() => new()
{
    IsConnected = false,
    Mode = ProxyMode.Rule,
    TunEnabled = true,
    AllowLan = false,
    LanAddress = "192.168.40.182",
    SubscriptionName = "未配置订阅",
    HasConfiguration = false
};

static DashboardPreview Populated(bool allowLan, int liveCount = 4) => new()
{
    IsConnected = true,
    Mode = ProxyMode.Rule,
    TunEnabled = false,
    AllowLan = allowLan,
    Duration = "00:12:06",
    SubscriptionName = "357884205792759808",
    Source = "https://example.com/subscribe",
    UpdatedAt = DateTimeOffset.Now.AddHours(-6),
    NodeCount = 2,
    HasConfiguration = true,
    LanAddress = "192.168.40.182",
    Exit = new ExitIpInfo("142.252.100.8", "US", "EGIHosting", "加利福尼亚", "机房"),
    UploadRate = 128 * 1024,
    DownloadRate = (long)(1.2 * 1024 * 1024),
    UploadPeak = 256 * 1024,
    DownloadPeak = (long)(3.4 * 1024 * 1024),
    UploadTotal = 12L * 1024 * 1024,
    DownloadTotal = 860L * 1024 * 1024,
    Tcp = 0,
    Udp = 4,
    Live = SampleConnections().Take(liveCount).ToArray(),
    Recent =
    [
        Sample("recent-1", "10.0.0.8", 443, "api.github.com", "TCP", "香港 01", 4096, 512, false)
    ],
    Groups =
    [
        new ProxyGroupInfo("PROXY", "Selector", "选择", "tuic-tunnel-us", "P"),
        new ProxyGroupInfo("FINAL", "Selector", "选择", "PROXY", "F")
    ],
    ServiceDelays = [720, 746, 810, 722]
};

static IReadOnlyList<ConnectionRow> SampleConnections() =>
[
    Sample("c1", "", 0, "1.1.1.1", "UDP", "tuic-tunnel-us", 0, 33, true),
    Sample("c2", "", 0, "1.1.1.1", "UDP", "tuic-tunnel-us", 0, 33, true),
    Sample("c3", "", 0, "8.8.8.8", "UDP", "tuic-tunnel-us", 0, 33, true),
    Sample("c4", "", 0, "8.8.8.8", "UDP", "tuic-tunnel-us", 0, 33, true)
];

static ConnectionRow Sample(string id, string sourceIp, int sourcePort, string host, string protocol, string outbound, long download, long upload, bool dns)
{
    var source = string.IsNullOrEmpty(sourceIp) ? "?" : $"{sourceIp}:{sourcePort}";
    var port = dns ? 53 : sourcePort == 0 ? 443 : sourcePort;
    return new ConnectionRow(id, source, $"{host}:{port}", protocol, outbound, download, upload, DateTimeOffset.Now.AddSeconds(-12), dns);
}

public sealed class ShotApp : Application
{
    public override void Initialize()
    {
        var chinese = CultureInfo.GetCultureInfo("zh-CN");
        Styles.Add(new SemiTheme { Locale = chinese });
        Styles.Add(new UrsaSemiTheme { Locale = chinese });
        Styles.Add(new StyleInclude(new Uri("avares://AsterDock.UI/"))
        {
            Source = new Uri("avares://AsterDock.UI/Themes/AsterDockTheme.axaml")
        });
    }
}
