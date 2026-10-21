using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
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
CaptureSubscriptionButtons(output);
if (!args.Contains("--stills"))
{
    CaptureMotion(Path.Combine(output, "overview-motion"));
    CapturePowerClip(Path.Combine(output, "power-button"));
    CaptureNavClip(Path.Combine(output, "nav-and-power"));
}
Console.WriteLine("PASS dashboard screenshots in " + output);

static void CaptureSubscriptionButtons(string output)
{
    foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
    {
        var name = theme == ThemeVariant.Light ? "light" : "dark";
        DashboardMotion.ReduceMotion = true;
        Application.Current!.RequestedThemeVariant = theme;
        var directory = Path.Combine(Path.GetTempPath(), "na-sub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var viewModel = new NetworkAcceleratorViewModel(directory, directory);
        Window? window = null;
        try
        {
            viewModel.ShowDashboardPreview(Populated(allowLan: false));
            var view = new NetworkAcceleratorView
            {
                DataContext = viewModel,
                Width = 980,
                Height = 760,
                FontFamily = new FontFamily("WenQuanYi Micro Hei, Droid Sans Fallback, Inter")
            };
            view.PreviewNavigate("Configuration");
            window = new Window
            {
                Width = 980,
                Height = 760,
                Content = view,
                Background = Brushes.Transparent,
                WindowDecorations = WindowDecorations.None,
                FontFamily = view.FontFamily
            };
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            SaveSubscriptionButton(window, Path.Combine(output, $"subscription-update-{name}-idle.png"));

            MotionClock.Manual = true;
            DashboardMotion.ReduceMotion = false;
            viewModel.PreviewSubscriptionUpdate(true);
            MotionClock.Advance(TimeSpan.FromMilliseconds(280));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            foreach (var spin in window.GetVisualDescendants().OfType<RefreshSpin>())
            {
                var angle = spin.RenderTransform is RotateTransform rotate ? rotate.Angle : double.NaN;
                Console.WriteLine($"subscription {name} angle={angle:0.0} spinning={spin.Spinning} width={spin.Bounds.Width:0}");
            }

            SaveSubscriptionButton(window, Path.Combine(output, $"subscription-update-{name}-busy.png"));
            viewModel.PreviewSubscriptionUpdate(false);
            MotionClock.Advance(TimeSpan.FromMilliseconds(16));
            var reset = window.GetVisualDescendants().OfType<RefreshSpin>()
                .Where(spin => spin.Bounds.Width > 10)
                .Select(spin => spin.RenderTransform is RotateTransform rotate ? rotate.Angle : double.NaN)
                .FirstOrDefault();
            Console.WriteLine($"subscription {name} reset angle={reset:0.0}");
        }
        finally
        {
            window?.Close();
            viewModel.Dispose();
            Directory.Delete(directory, recursive: true);
            DashboardMotion.ReduceMotion = true;
            MotionClock.Manual = false;
        }
    }
}

static void SaveSubscriptionButton(Window window, string path)
{
    var button = window.GetVisualDescendants().OfType<Button>()
        .FirstOrDefault(candidate => AutomationProperties.GetName(candidate) == "更新订阅" && candidate.Bounds.Width > 80);
    if (button is null) throw new InvalidOperationException("更新订阅 button was not visible");
    var origin = button.TranslatePoint(new Point(0, 0), window) ?? throw new InvalidOperationException("更新订阅 button has no origin");
    var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("subscription frame missing");
    var scale = frame.PixelSize.Width / window.Bounds.Width;
    const int pad = 18;
    var x = (int)Math.Max(0, Math.Floor((origin.X - pad) * scale));
    var y = (int)Math.Max(0, Math.Floor((origin.Y - pad) * scale));
    var w = (int)Math.Min(frame.PixelSize.Width - x, Math.Ceiling((button.Bounds.Width + pad * 2) * scale));
    var h = (int)Math.Min(frame.PixelSize.Height - y, Math.Ceiling((button.Bounds.Height + pad * 2) * scale));
    var zoom = new RenderTargetBitmap(new PixelSize(w * 2, h * 2), new Vector(96, 96));
    using (var context = zoom.CreateDrawingContext())
        context.DrawImage(frame, new Rect(x, y, w, h), new Rect(0, 0, w * 2, h * 2));
    zoom.Save(path, new PngBitmapEncoderOptions());
    Console.WriteLine(path + $" bounds={button.Bounds.Width:0}x{button.Bounds.Height:0} enabled={button.IsEnabled}");
}

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
        viewModel.ShowDashboardPreview(Disconnected());
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
        for (var i = 0; i < 8; i++) SaveMotion(window, directory, ref frame);
        viewModel.PreviewConnectionFault();
        for (var i = 0; i < 12; i++) SaveMotion(window, directory, ref frame);
        viewModel.PreviewSetConnection(connected: false, busy: true);
        for (var i = 0; i < 16; i++) SaveMotion(window, directory, ref frame);
        viewModel.PreviewSetConnection(connected: true, busy: false);
        for (var i = 0; i < 18; i++) SaveMotion(window, directory, ref frame);
        viewModel.PreviewSetConnection(connected: false, busy: false);
        for (var i = 0; i < 14; i++) SaveMotion(window, directory, ref frame);
        viewModel.ShowDashboardPreview(Populated(allowLan: false));
        for (var i = 0; i < 8; i++) SaveMotion(window, directory, ref frame);
        viewModel.PreviewSetMode(ProxyMode.Global);
        for (var i = 0; i < 10; i++) SaveMotion(window, directory, ref frame);
        viewModel.PreviewSetTun(true);
        viewModel.Dash.NudgeTraffic(280 * 1024, (long)(2.1 * 1024 * 1024));
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
        if (name is "overview-light-disconnected" or "overview-light-populated")
        {
            var running = name == "overview-light-populated";
            SaveButtonCloseup(window, frame, Path.Combine(output, running ? "power-running-2x.png" : "power-stopped-2x.png"));
            SaveHeroCard(window, frame, Path.Combine(output, running ? "status-card-running-2x.png" : "status-card-stopped-2x.png"));
        }
        Console.WriteLine(path);
    }
    finally
    {
        window?.Close();
        viewModel.Dispose();
        Directory.Delete(directory, recursive: true);
    }
}

static void CapturePowerClip(string directory)
{
    DashboardMotion.ReduceMotion = false;
    MotionClock.Manual = true;
    Directory.CreateDirectory(directory);
    var temp = Path.Combine(Path.GetTempPath(), "na-power-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
    var viewModel = new NetworkAcceleratorViewModel(temp, temp);
    Window? window = null;
    try
    {
        viewModel.ShowDashboardPreview(Disconnected());
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
        var button = window.GetVisualDescendants().OfType<PowerButton>().First();
        var frame = 0;
        for (var i = 0; i < 4; i++) SaveMotion(window, directory, ref frame);
        button.PreviewHover(true);
        for (var i = 0; i < 8; i++) SaveMotion(window, directory, ref frame);
        button.PreviewPress(true);
        for (var i = 0; i < 8; i++) SaveMotion(window, directory, ref frame);
        button.PreviewPress(false);
        for (var i = 0; i < 6; i++) SaveMotion(window, directory, ref frame);
        button.PreviewHover(false);
        for (var i = 0; i < 6; i++) SaveMotion(window, directory, ref frame);
        viewModel.PreviewSetConnection(connected: false, busy: true);
        for (var i = 0; i < 14; i++) SaveMotion(window, directory, ref frame);
        viewModel.PreviewSetConnection(connected: true, busy: false);
        for (var i = 0; i < 16; i++) SaveMotion(window, directory, ref frame);
        Console.WriteLine("power frames " + frame);
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

static void CaptureNavClip(string directory)
{
    DashboardMotion.ReduceMotion = false;
    MotionClock.Manual = true;
    Directory.CreateDirectory(directory);
    var temp = Path.Combine(Path.GetTempPath(), "na-nav-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
    var viewModel = new NetworkAcceleratorViewModel(temp, temp);
    Window? window = null;
    try
    {
        viewModel.ShowDashboardPreview(Disconnected());
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
        var shot = window;
        var button = shot.GetVisualDescendants().OfType<PowerButton>().First();
        var frame = 0;
        void Hold(int count)
        {
            for (var i = 0; i < count; i++) SaveMotion(shot, directory, ref frame);
        }

        Hold(3);
        foreach (var section in new[] { "Proxies", "Connections", "Rules", "Settings", "Overview" })
        {
            view.PreviewNavigate(section);
            Hold(8);
        }

        view.PreviewNavPress(2, true);
        Hold(4);
        view.PreviewNavPress(2, false);
        Hold(3);
        button.PreviewHover(true);
        Hold(6);
        button.PreviewPress(true);
        Hold(5);
        button.PreviewPress(false);
        Hold(4);
        viewModel.PreviewSetConnection(connected: false, busy: true);
        Hold(12);
        viewModel.PreviewSetConnection(connected: true, busy: false);
        Hold(10);
        Console.WriteLine("nav frames " + frame);
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

static void SaveHeroCard(Window window, Bitmap frame, string path)
{
    var blob = window.GetVisualDescendants().OfType<StatusBlob>().FirstOrDefault();
    if (blob?.Parent?.Parent is not Visual card) return;
    var origin = card.TranslatePoint(new Point(0, 0), window);
    if (origin is null) return;
    var scale = frame.PixelSize.Width / window.Bounds.Width;
    const int pad = 10;
    var x = (int)Math.Max(0, Math.Floor((origin.Value.X - pad) * scale));
    var y = (int)Math.Max(0, Math.Floor((origin.Value.Y - pad) * scale));
    var w = (int)Math.Min(frame.PixelSize.Width - x, Math.Ceiling((card.Bounds.Width + pad * 2) * scale));
    var h = (int)Math.Min(frame.PixelSize.Height - y, Math.Ceiling((card.Bounds.Height + pad * 2) * scale));
    var zoom = new RenderTargetBitmap(new PixelSize(w * 2, h * 2), new Vector(96, 96));
    using (var context = zoom.CreateDrawingContext())
        context.DrawImage(frame, new Rect(x, y, w, h), new Rect(0, 0, w * 2, h * 2));
    zoom.Save(path, new PngBitmapEncoderOptions());
    Console.WriteLine(path);
}

static void SaveButtonCloseup(Window window, Bitmap frame, string path)
{
    var button = window.GetVisualDescendants().OfType<PowerButton>().FirstOrDefault();
    if (button is null) return;
    var origin = button.TranslatePoint(new Point(0, 0), window);
    if (origin is null) return;
    var scale = frame.PixelSize.Width / window.Bounds.Width;
    const int pad = 40;
    var x = (int)Math.Max(0, Math.Floor((origin.Value.X - pad) * scale));
    var y = (int)Math.Max(0, Math.Floor((origin.Value.Y - pad) * scale));
    var w = (int)Math.Min(frame.PixelSize.Width - x, Math.Ceiling((button.Bounds.Width + pad * 2) * scale));
    var h = (int)Math.Min(frame.PixelSize.Height - y, Math.Ceiling((button.Bounds.Height + pad * 2 + 16) * scale));
    var zoom = new RenderTargetBitmap(new PixelSize(w * 2, h * 2), new Vector(96, 96));
    using (var context = zoom.CreateDrawingContext())
        context.DrawImage(frame, new Rect(x, y, w, h), new Rect(0, 0, w * 2, h * 2));
    zoom.Save(path, new PngBitmapEncoderOptions());
    Console.WriteLine(path);
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
