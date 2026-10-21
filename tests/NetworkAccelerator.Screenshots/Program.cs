using System.Globalization;
using AsterDock.UI.Controls;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
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
CaptureGlass(output);
if (args.Contains("--glass"))
{
    Console.WriteLine("PASS glass only");
    return;
}
Capture("overview-light-disconnected", ThemeVariant.Light, Disconnected(), 1360, 1180, output);
Capture("overview-light-populated", ThemeVariant.Light, Populated(allowLan: false), 1360, 1280, output);
Capture("overview-dark-disconnected", ThemeVariant.Dark, Disconnected(), 1360, 1180, output);
Capture("overview-dark-populated", ThemeVariant.Dark, Populated(allowLan: true), 1360, 1560, output);
Capture("overview-narrow-populated", ThemeVariant.Light, Populated(allowLan: false), 760, 2200, output);
CaptureCookieSizes(output);
CaptureSubscriptionButtons(output);
if (!args.Contains("--stills"))
{
    CaptureMotion(Path.Combine(output, "overview-motion"));
    CapturePowerClip(Path.Combine(output, "power-button"));
    CaptureNavClip(Path.Combine(output, "nav-and-power"));
}
Console.WriteLine("PASS dashboard screenshots in " + output);

static void CaptureCookieSizes(string output)
{
    // Widths that land the status card at three different sizes. 1360 happens to
    // give the hero the same width as 760, so default is wider than that.
    var sizes = new (string Name, double Width)[] { ("narrow", 760), ("default", 1680), ("wide", 2200) };
    foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
    foreach (var size in sizes)
    {
        var themeName = theme == ThemeVariant.Light ? "light" : "dark";
        DashboardMotion.ReduceMotion = true;
        Application.Current!.RequestedThemeVariant = theme;
        var directory = Path.Combine(Path.GetTempPath(), "na-cookie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var viewModel = new NetworkAcceleratorViewModel(directory, directory);
        Window? window = null;
        try
        {
            viewModel.ShowDashboardPreview(Disconnected());
            var view = new NetworkAcceleratorView
            {
                DataContext = viewModel,
                Width = size.Width,
                Height = 900,
                FontFamily = new FontFamily("WenQuanYi Micro Hei, Droid Sans Fallback, Inter")
            };
            window = new Window
            {
                Width = size.Width,
                Height = 900,
                Content = view,
                Background = Brushes.Transparent,
                WindowDecorations = WindowDecorations.None,
                FontFamily = view.FontFamily
            };
            window.Show();
            Settle(window);
            var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("cookie frame missing");
            var path = Path.Combine(output, $"cookie-{size.Name}-{themeName}.png");
            SaveHeroCard(window, frame, path, zoom: 1);
            LogCookieAlignment(window, $"{size.Name}-{themeName}");
        }
        finally
        {
            window?.Close();
            viewModel.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void LogCookieAlignment(Window window, string name)
{
    var button = window.GetVisualDescendants().OfType<PowerButton>().FirstOrDefault();
    var blob = window.GetVisualDescendants().OfType<StatusBlob>().FirstOrDefault();
    if (button is null || blob is null) throw new InvalidOperationException("cookie or button missing");
    var buttonCenter = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
    var blobCenter = blob.TranslatePoint(new Point(blob.Bounds.Width / 2, blob.Bounds.Height / 2), window);
    if (buttonCenter is null || blobCenter is null) throw new InvalidOperationException("cookie alignment missing");
    var dx = blobCenter.Value.X - buttonCenter.Value.X;
    var dy = blobCenter.Value.Y - buttonCenter.Value.Y;
    var card = HeroCard(window);
    Console.WriteLine(
        $"cookie {name} window={window.Bounds.Width:0} card={card?.Bounds.Width:0}x{card?.Bounds.Height:0} " +
        $"blob={blob.Bounds.Width:0}x{blob.Bounds.Height:0} dx={dx:0.00} dy={dy:0.00}");
    if (Math.Abs(blob.Bounds.Width - StatusBlob.BoxSize) > 0.5 || Math.Abs(blob.Bounds.Height - StatusBlob.BoxSize) > 0.5
        || Math.Abs(dx) > 1 || Math.Abs(dy) > 1)
        throw new InvalidOperationException($"cookie {name} is not a fixed shape centred on the button");
}

static Border? HeroCard(Window window)
{
    Visual? node = window.GetVisualDescendants().OfType<StatusBlob>().FirstOrDefault();
    while (node is not null)
    {
        if (node is Border border && border.Classes.Contains("hero")) return border;
        node = node.GetVisualParent();
    }

    return null;
}

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
                Width = 1100,
                Height = 1400,
                FontFamily = new FontFamily("WenQuanYi Micro Hei, Droid Sans Fallback, Inter")
            };
            window = new Window
            {
                Width = 1100,
                Height = 1400,
                Content = view,
                Background = Brushes.Transparent,
                WindowDecorations = WindowDecorations.None,
                FontFamily = view.FontFamily
            };
            window.Show();
            Settle(window);
            BringUpdateButton(window, icon: true);
            SaveSubscriptionButton(window, Path.Combine(output, $"subscription-icon-{name}-idle.png"), icon: true);
            viewModel.PreviewSubscriptionUpdate(true);
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            LogSubscription(window, $"{name} icon busy");
            SaveSubscriptionButton(window, Path.Combine(output, $"subscription-icon-{name}-busy.png"), icon: true);

            viewModel.PreviewSubscriptionUpdate(false);
            view.PreviewNavigate("Configuration");
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            BringUpdateButton(window, icon: false);
            SaveSubscriptionButton(window, Path.Combine(output, $"subscription-update-{name}-idle.png"), icon: false);
            viewModel.PreviewSubscriptionUpdate(true);
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            LogSubscription(window, $"{name} text busy");
            SaveSubscriptionButton(window, Path.Combine(output, $"subscription-update-{name}-busy.png"), icon: false);
            viewModel.PreviewSubscriptionUpdate(false);
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            var restored = window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "更新订阅" && block.IsVisible);
            var hidden = window.GetVisualDescendants().OfType<AdLoadingIndicator>()
                .Where(mark => mark.Bounds.Width > 10)
                .All(mark => !mark.IsVisible);
            Console.WriteLine($"subscription {name} restored label={restored} marks hidden={hidden}");
        }
        finally
        {
            window?.Close();
            viewModel.Dispose();
            Directory.Delete(directory, recursive: true);
            DashboardMotion.ReduceMotion = true;
        }
    }
}

static void BringUpdateButton(Window window, bool icon)
{
    var button = RequireUpdateButton(window, icon);
    button.BringIntoView();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
}

static void LogSubscription(Window window, string name)
{
    var button = RequireUpdateButton(window, name.Contains("icon", StringComparison.Ordinal));
    var mark = button.GetVisualDescendants().OfType<AdLoadingIndicator>().FirstOrDefault();
    var label = button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(block => block.IsVisible);
    Console.WriteLine(
        $"subscription {name} enabled={button.IsEnabled} mark={(mark is null ? "missing" : mark.IsVisible.ToString())} " +
        $"bounds={mark?.Bounds.Width:0}x{mark?.Bounds.Height:0} variant={mark?.Variant} diameter={mark?.Diameter:0} label={label?.Text}");
}

static Button RequireUpdateButton(Window window, bool icon)
{
    var button = window.GetVisualDescendants().OfType<Button>()
        .FirstOrDefault(candidate =>
            AutomationProperties.GetName(candidate) == "更新订阅" &&
            candidate.Bounds.Width > 20 &&
            (icon ? candidate.Bounds.Width < 80 : candidate.Bounds.Width > 80));
    return button ?? throw new InvalidOperationException(icon ? "更新订阅 icon button was not visible" : "更新订阅 text button was not visible");
}

static void SaveSubscriptionButton(Window window, string path, bool icon)
{
    var button = RequireUpdateButton(window, icon);
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

static void Settle(Window window)
{
    window.UpdateLayout();
    Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
}

static void CaptureGlass(string output)
{
    foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
    foreach (var size in new (string Name, double Width, bool Save)[]
    {
        ("default", 1360, true), ("narrow", 760, true), ("wide", 2200, false)
    })
    {
        var themeName = theme == ThemeVariant.Light ? "light" : "dark";
        DashboardMotion.ReduceMotion = true;
        Application.Current!.RequestedThemeVariant = theme;
        var directory = Path.Combine(Path.GetTempPath(), "na-glass-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var viewModel = new NetworkAcceleratorViewModel(directory, directory);
        Window? window = null;
        try
        {
            viewModel.ShowDashboardPreview(Populated(allowLan: false));
            var view = new NetworkAcceleratorView
            {
                DataContext = viewModel,
                Width = size.Width,
                Height = 980,
                FontFamily = new FontFamily("WenQuanYi Micro Hei, Droid Sans Fallback, Inter")
            };
            window = new Window
            {
                Width = size.Width,
                Height = 980,
                Content = view,
                Background = Brushes.Transparent,
                WindowDecorations = WindowDecorations.None,
                FontFamily = view.FontFamily
            };
            window.Show();
            Settle(window);
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(item => item.Name == "PageScroll");
            RequireClearOfBar(window, $"{themeName} {size.Name} top");
            scroll.Offset = new Vector(0, 720);
            Settle(window);
            RequireClearOfBar(window, $"{themeName} {size.Name} scrolled");
            view.PreviewNavigate("Logs");
            Settle(window);
            RequireClearOfBar(window, $"{themeName} {size.Name} logs");
            view.PreviewNavigate("Connections");
            Settle(window);
            RequireClearOfBar(window, $"{themeName} {size.Name} connections");
            view.PreviewNavigate("Overview");
            scroll.Offset = default;
            Settle(window);
            RequireClearOfBar(window, $"{themeName} {size.Name} overview");
            if (!size.Save) continue;
            var glass = window.GetVisualDescendants().OfType<LiquidGlassBar>().First();
            Console.WriteLine(
                $"glass {themeName} {size.Name} {glass.Bounds.Width:0}x{glass.Bounds.Height:0} " +
                $"blur={glass.BackdropBlurActive} {glass.SnapshotNote}");
            var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("glass frame missing");
            var path = Path.Combine(output, $"glass-{size.Name}-{themeName}.png");
            frame.Save(path, new PngBitmapEncoderOptions());
            SaveGlassCrop(window, frame, Path.Combine(output, $"glass-{size.Name}-{themeName}-bar.png"));
            Console.WriteLine(path);
        }
        finally
        {
            window?.Close();
            viewModel.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    CaptureGlassSwitch(output);
}

static void RequireClearOfBar(Window window, string label)
{
    var chrome = window.GetVisualDescendants().OfType<Border>().First(item => item.Classes.Contains("nav-float"));
    var barLeft = chrome.TranslatePoint(default, window)?.X
        ?? throw new InvalidOperationException(label + " bar has no position");
    var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(item => item.Name == "PageScroll");
    var scrollRight = scroll.TranslatePoint(new Point(scroll.Bounds.Width, 0), window)?.X
        ?? throw new InvalidOperationException(label + " page has no position");
    var gap = barLeft - scrollRight;
    Console.WriteLine($"{label} scrollRight={scrollRight:0.0} barLeft={barLeft:0.0} bar={chrome.Bounds.Width:0} gap={gap:0.0}");
    if (gap < 11)
        throw new InvalidOperationException($"{label} content column is {gap:0.0}px from the bar");
    foreach (var scroller in scroll.GetVisualDescendants().OfType<ScrollBar>())
    {
        if (!scroller.IsEffectivelyVisible || scroller.Bounds.Width < 2) continue;
        var right = scroller.TranslatePoint(new Point(scroller.Bounds.Width, 0), window)?.X ?? 0;
        if (right > barLeft + 0.5)
            throw new InvalidOperationException($"{label} scrollbar ends at {right:0} under the bar");
    }

    foreach (var card in window.GetVisualDescendants().OfType<Border>())
    {
        if (!card.IsEffectivelyVisible || card.Bounds.Width < 1) continue;
        var marked = card.Classes.Contains("dash-card") || card.Classes.Contains("ad-pill-card") || card.Classes.Contains("card");
        if (!marked) continue;
        var right = card.TranslatePoint(new Point(card.Bounds.Width, 0), window)?.X;
        if (right is null || right.Value <= barLeft + 0.5) continue;
        throw new InvalidOperationException($"{label} card ends at {right.Value:0.0} under the bar at {barLeft:0.0}");
    }
}

static void SaveGlassCrop(Window window, Bitmap frame, string path)
{
    var glass = window.GetVisualDescendants().OfType<LiquidGlassBar>().First();
    var origin = glass.TranslatePoint(default, window);
    if (origin is null) return;
    var scale = frame.PixelSize.Width / window.Bounds.Width;
    const int pad = 28;
    var x = (int)Math.Max(0, Math.Floor((origin.Value.X - 180) * scale));
    var y = (int)Math.Max(0, Math.Floor((origin.Value.Y - pad) * scale));
    var w = (int)Math.Min(frame.PixelSize.Width - x, Math.Ceiling((glass.Bounds.Width + 180 + pad) * scale));
    var h = (int)Math.Min(frame.PixelSize.Height - y, Math.Ceiling((glass.Bounds.Height + pad * 2) * scale));
    var zoom = new RenderTargetBitmap(new PixelSize(w * 2, h * 2), new Vector(96, 96));
    using (var context = zoom.CreateDrawingContext())
        context.DrawImage(frame, new Rect(x, y, w, h), new Rect(0, 0, w * 2, h * 2));
    zoom.Save(path, new PngBitmapEncoderOptions());
    Console.WriteLine(path);
}

static void CaptureGlassSwitch(string output)
{
    DashboardMotion.ReduceMotion = false;
    MotionClock.Manual = true;
    var directory = Path.Combine(Path.GetTempPath(), "na-glass-frames-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var temp = Path.Combine(Path.GetTempPath(), "na-glass-switch-" + Guid.NewGuid().ToString("N"));
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
            Width = 1180,
            Height = 860,
            FontFamily = new FontFamily("WenQuanYi Micro Hei, Droid Sans Fallback, Inter")
        };
        window = new Window
        {
            Width = 1180,
            Height = 860,
            Content = view,
            Background = Brushes.Transparent,
            WindowDecorations = WindowDecorations.None,
            FontFamily = view.FontFamily
        };
        window.Show();
        Settle(window);
        var frame = 0;
        void Hold(int count)
        {
            for (var i = 0; i < count; i++)
            {
                MotionClock.Advance(TimeSpan.FromMilliseconds(40));
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                var bitmap = window.CaptureRenderedFrame();
                if (bitmap is null) return;
                var glass = window.GetVisualDescendants().OfType<LiquidGlassBar>().First();
                var origin = glass.TranslatePoint(default, window) ?? new Point(window.Bounds.Width - 160, 80);
                var scale = bitmap.PixelSize.Width / window.Bounds.Width;
                var x = (int)Math.Max(0, Math.Floor((origin.X - 220) * scale));
                var y = 0;
                var w = bitmap.PixelSize.Width - x;
                var h = bitmap.PixelSize.Height;
                var crop = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
                using (var context = crop.CreateDrawingContext())
                    context.DrawImage(bitmap, new Rect(x, y, w, h), new Rect(0, 0, w, h));
                crop.Save(Path.Combine(directory, $"frame_{frame:0000}.png"), new PngBitmapEncoderOptions());
                frame++;
            }
        }

        Hold(4);
        foreach (var section in new[] { "Proxies", "Configuration", "Connections", "Rules", "Overview" })
        {
            view.PreviewNavigate(section);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            Hold(8);
        }

        view.PreviewNavPress(1, true);
        Hold(3);
        view.PreviewNavPress(1, false);
        Hold(3);
        Console.WriteLine("glass switch frames " + frame);
        EncodeGlass(directory, Path.Combine(output, "glass-nav-switch.mp4"));
    }
    finally
    {
        window?.Close();
        viewModel.Dispose();
        Directory.Delete(temp, recursive: true);
        Directory.Delete(directory, recursive: true);
        DashboardMotion.ReduceMotion = true;
        MotionClock.Manual = false;
    }
}

static void EncodeGlass(string frameDirectory, string output)
{
    var process = new System.Diagnostics.Process
    {
        StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }
    };
    process.StartInfo.ArgumentList.Add("-y");
    process.StartInfo.ArgumentList.Add("-framerate");
    process.StartInfo.ArgumentList.Add("25");
    process.StartInfo.ArgumentList.Add("-start_number");
    process.StartInfo.ArgumentList.Add("0");
    process.StartInfo.ArgumentList.Add("-i");
    process.StartInfo.ArgumentList.Add(Path.Combine(frameDirectory, "frame_%04d.png"));
    process.StartInfo.ArgumentList.Add("-vf");
    process.StartInfo.ArgumentList.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
    process.StartInfo.ArgumentList.Add("-c:v");
    process.StartInfo.ArgumentList.Add("libx264");
    process.StartInfo.ArgumentList.Add("-pix_fmt");
    process.StartInfo.ArgumentList.Add("yuv420p");
    process.StartInfo.ArgumentList.Add("-movflags");
    process.StartInfo.ArgumentList.Add("+faststart");
    process.StartInfo.ArgumentList.Add(output);
    if (!process.Start()) throw new InvalidOperationException("ffmpeg did not start");
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException(process.StandardError.ReadToEnd());
    Console.WriteLine(output);
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
        Settle(window);
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

static void SaveHeroCard(Window window, Bitmap frame, string path, int zoom = 2)
{
    var card = HeroCard(window);
    if (card is null) return;
    var origin = card.TranslatePoint(new Point(0, 0), window);
    if (origin is null) return;
    var scale = frame.PixelSize.Width / window.Bounds.Width;
    const int pad = 10;
    var x = (int)Math.Max(0, Math.Floor((origin.Value.X - pad) * scale));
    var y = (int)Math.Max(0, Math.Floor((origin.Value.Y - pad) * scale));
    var w = (int)Math.Min(frame.PixelSize.Width - x, Math.Ceiling((card.Bounds.Width + pad * 2) * scale));
    var h = (int)Math.Min(frame.PixelSize.Height - y, Math.Ceiling((card.Bounds.Height + pad * 2) * scale));
    var bitmap = new RenderTargetBitmap(new PixelSize(w * zoom, h * zoom), new Vector(96, 96));
    using (var context = bitmap.CreateDrawingContext())
        context.DrawImage(frame, new Rect(x, y, w, h), new Rect(0, 0, w * zoom, h * zoom));
    bitmap.Save(path, new PngBitmapEncoderOptions());
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
