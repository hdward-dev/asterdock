using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NetworkAccelerator.Core.Models;
using Fluid.Avalonia.Acrylic;
using NetworkAccelerator.Module.Controls;
using NetworkAccelerator.Module.ViewModels;
using NetworkAccelerator.Module.Views;
using Semi.Avalonia;
using Ursa.Themes.Semi;

static class ModuleInteractionChecks
{
    public static void RunMotionClockChecks(Action<bool, string> check)
    {
        var previous = MotionClock.Manual;
        MotionClock.Manual = true;
        try
        {
            var calls = 0;
            void Tick(TimeSpan _) => calls++;
            MotionClock.Subscribe(Tick);
            MotionClock.Unsubscribe(Tick);
            MotionClock.Subscribe(Tick);
            MotionClock.Advance(TimeSpan.FromMilliseconds(16));
            MotionClock.Advance(TimeSpan.FromMilliseconds(16));
            check(calls == 2, "unsubscribe and resubscribe in one tick keeps the subscription");
            MotionClock.Unsubscribe(Tick);
            var after = calls;
            MotionClock.Advance(TimeSpan.FromMilliseconds(16));
            check(calls == after, "unsubscribe removes the subscriber immediately");

            var nested = 0;
            void Nested(TimeSpan _)
            {
                nested++;
                if (nested == 1)
                {
                    MotionClock.Unsubscribe(Nested);
                    MotionClock.Subscribe(Nested);
                }
            }

            MotionClock.Subscribe(Nested);
            MotionClock.Advance(TimeSpan.FromMilliseconds(16));
            MotionClock.Advance(TimeSpan.FromMilliseconds(16));
            check(nested == 2, "unsubscribe and resubscribe inside a tick keeps the subscription");
            MotionClock.Unsubscribe(Nested);
            var skipped = 0;
            void Later(TimeSpan _) => skipped++;
            MotionClock.Subscribe(Later);
            MotionClock.Unsubscribe(Later);
            MotionClock.Advance(TimeSpan.FromMilliseconds(16));
            check(skipped == 0, "a subscriber removed before the tick is not invoked");
        }
        finally
        {
            MotionClock.Manual = previous;
        }
    }

    public static void RunInteractionChecks(Action<bool, string> check)
    {
        var reduceMotion = DashboardMotion.ReduceMotion;
        var manual = MotionClock.Manual;
        AppBuilder.Configure<InteractionApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();
        try
        {
            DashboardMotion.ReduceMotion = false;
            MotionClock.Manual = true;
            CheckNavGlassChoice(check);
            CheckPageSurvivesDetach(check);
            CheckSwitchSurvivesDetach(check);
            CheckCustomHitTargets(check);
            CheckRefreshIconsCentered(check);
            CheckNavChromeShadow(check);
            CheckClosedViewCanBeCollected(check);
        }
        finally
        {
            DashboardMotion.ReduceMotion = reduceMotion;
            MotionClock.Manual = manual;
        }
    }

    private static void CheckNavGlassChoice(Action<bool, string> check)
    {
        var light = LiquidGlassBar.CreateAcrylicTint(false);
        var dark = LiquidGlassBar.CreateAcrylicTint(true);
        check(light == Colors.Transparent && dark == Colors.Transparent && light.A == 0,
            "acrylic tint is transparent so Fluid does not hue-wash a solid plate");
        check(LiquidGlassBar.AcrylicRefractionHeight >= 32 && LiquidGlassBar.AcrylicRefractionAmount >= 48,
            "acrylic refraction is stronger than the library 12/24 edge");

        var previousTheme = Application.Current?.RequestedThemeVariant;
        Window? window = null;
        Window? follow = null;
        try
        {
            if (Application.Current is not null)
                Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            var view = new NetworkAcceleratorView { Width = 1100, Height = 820 };
            window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
                WindowDecorations = WindowDecorations.None
            };
            window.Show();
            Settle(window);
            var glass = view.FindControl<LiquidGlassBar>("NavGlass")!;
            var surface = glass.GetVisualDescendants().OfType<AcrylicInteractiveSurface>().Single();
            check(glass.SnapshotNote.Contains("acrylic", StringComparison.Ordinal)
                  && !glass.SnapshotNote.Contains("approximate", StringComparison.Ordinal),
                "nav glass installs Fluid acrylic and does not describe a degraded bar");
            check(surface.BlurRadius == LiquidGlassBar.AcrylicBlurRadius
                  && Math.Abs(surface.Vibrancy - LiquidGlassBar.AcrylicVibrancy) < 0.001
                  && Math.Abs(surface.Brightness - LiquidGlassBar.AcrylicBrightness) < 0.001,
                "acrylic nav uses the Fluid demo frost");
            check(surface.RevealBorderEnabled
                  && surface.DepthEffect
                  && !surface.ChromaticAberration
                  && surface.RefractionHeight == LiquidGlassBar.AcrylicRefractionHeight
                  && surface.RefractionAmount == LiquidGlassBar.AcrylicRefractionAmount,
                "acrylic nav keeps a reveal rim and a deep refraction edge");
            check(surface.HighlightEnabled && surface.ShadowEnabled
                  && Math.Abs(surface.HighlightOpacity - LiquidGlassBar.AcrylicHighlightRest) < 0.001
                  && Math.Abs(surface.HighlightWidth - 0.5) < 0.001
                  && surface.TintColor.A == 0
                  && surface.SurfaceColor.A == 0,
                "acrylic nav keeps Fluid highlight and shadow with no tint fill");
            var scroll = view.FindControl<ScrollViewer>("PageScroll")!;
            var chrome = view.FindControl<Border>("NavChrome")!;
            var scrollRight = scroll.TranslatePoint(new Point(scroll.Bounds.Width, 0), window)?.X;
            var barLeft = chrome.TranslatePoint(default, window)?.X;
            check(scrollRight is double pageRight && barLeft is double navLeft && pageRight > navLeft + 8,
                "page scroll extends under the floating nav");
            var body = view.FindControl<StackPanel>("PageBody")!;
            var bodyRight = body.TranslatePoint(new Point(body.Bounds.Width, 0), window)?.X;
            check(bodyRight is double contentRight && barLeft is double navEdge && contentRight > navEdge + 24,
                "page content runs under the nav so acrylic samples it");
            check(chrome.BoxShadow.Count == 0,
                "nav chrome does not stack a second shadow over Fluid's");
            check(surface.CornerRadius == new CornerRadius(LiquidGlassBar.Corner), "acrylic nav keeps the 24 corner");
            check(AcrylicBackdrop.GetIsExcludedFromCapture(surface)
                  && AcrylicBackdrop.GetIsExcludedFromCapture(glass)
                  && AcrylicBackdrop.GetIsExcludedFromCapture(chrome),
                "acrylic nav is excluded from its own backdrop capture");
            var overview = NamedButton(view, "概览");
            foreach (var button in view.FindControl<StackPanel>("SectionNavigation")!.Children.OfType<Button>())
            {
                var name = AutomationProperties.GetName(button);
                check(!string.IsNullOrEmpty(name) && ToolTip.GetTip(button) is null,
                    name + " keeps its accessible name and has no hover tip");
            }

            check(overview.Focusable && overview.GetVisualAncestors().OfType<AcrylicSurface>().Any(),
                "section buttons stay focusable inside the acrylic surface");
            check(Hits(window, overview, Center(overview)), "section button still hit-tests through acrylic");
            overview.Focus();
            Settle(window);
            check(overview.IsFocused, "section button takes keyboard focus through acrylic");
            DashboardMotion.ReduceMotion = true;
            var hoverAt = chrome.TranslatePoint(new Point(chrome.Bounds.Width / 2, 36), window);
            if (hoverAt is { } hoverPoint)
                window.MouseMove(hoverPoint);
            Settle(window);
            var hoverScale = chrome.RenderTransform?.Value.M11 ?? 1;
            check(hoverScale > 1.06, "acrylic hover scales the capsule enough to see");
            check(surface.HighlightOpacity > 0.9, "acrylic hover strengthens the Fluid highlight");
            check(surface.ShadowColor.A > LiquidGlassBar.AcrylicShadowRestAlpha && chrome.BoxShadow.Count == 0,
                "acrylic hover deepens Fluid's own shadow without a second drop");
            check(surface.IsInteractive, "hovering a section button arms Fluid's press deformation");
            check(Hits(window, overview, Center(overview)), "section button still hit-tests while the capsule is hovered");
            window.MouseMove(new Point(4, 4));
            Settle(window);
            check(!surface.IsInteractive, "leaving the nav lets clicks pass through the capsule");

            if (Application.Current is not null)
                Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Settle(window);
            check(surface.TintColor == Colors.Transparent && surface.SurfaceColor.A == 0,
                "acrylic nav stays untinted in the dark theme");
            check(glass.GetVisualDescendants().OfType<AcrylicSurface>().Any(),
                "a slow frame does not retire the nav to a solid bar");

            var section = NamedButton(view, "代理");
            window.Content = null;
            Settle(window);
            window.Content = view;
            Settle(window);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            check(view.FindControl<LiquidGlassBar>("NavGlass")!.GetVisualDescendants().OfType<AcrylicSurface>().Any()
                  && Hits(window, section, Center(section)),
                "acrylic nav keeps section buttons after detach and reattach");
            window.Close();
            window = null;

            var again = new NetworkAcceleratorView { Width = 1100, Height = 820 };
            follow = new Window
            {
                Width = 1100,
                Height = 820,
                Content = again,
                WindowDecorations = WindowDecorations.None
            };
            follow.Show();
            Settle(follow);
            var followGlass = again.FindControl<LiquidGlassBar>("NavGlass")!;
            check(followGlass.GetVisualDescendants().OfType<AcrylicInteractiveSurface>().Any()
                  && followGlass.SnapshotNote.Contains("acrylic", StringComparison.Ordinal),
                "a later nav still uses Fluid acrylic");
            CheckAcrylicSamplesContrast(check, follow);
            follow.Close();
            follow = null;
        }
        finally
        {
            window?.Close();
            follow?.Close();
            if (Application.Current is not null)
                Application.Current.RequestedThemeVariant = previousTheme;
        }
    }

    private static void CheckAcrylicSamplesContrast(Action<bool, string> check, Window window)
    {
        var chrome = window.GetVisualDescendants().OfType<Border>().First(item => item.Classes.Contains("nav-float"));
        var grid = chrome.Parent as Panel ?? throw new InvalidOperationException("nav chrome has no parent");
        var band = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Width = 280,
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#E11D48"), 0),
                    new GradientStop(Color.Parse("#F59E0B"), 0.35),
                    new GradientStop(Color.Parse("#0EA5E9"), 0.7),
                    new GradientStop(Color.Parse("#6D28D9"), 1)
                }
            }
        };
        grid.Children.Insert(grid.Children.IndexOf(chrome), band);
        var glass = window.GetVisualDescendants().OfType<LiquidGlassBar>().First();
        var surface = glass.GetVisualDescendants().OfType<AcrylicInteractiveSurface>().Single();
        Settle(window);
        for (var tick = 0; tick < 4; tick++)
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        var frosted = CaptureNavSample(window, chrome);
        chrome.Opacity = 0;
        Settle(window);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        var raw = CaptureNavSample(window, chrome);
        chrome.Opacity = 1;
        surface.RefractionHeight = 0;
        surface.RefractionAmount = 0;
        Settle(window);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        var flat = CaptureNavSample(window, chrome);
        surface.RefractionHeight = LiquidGlassBar.AcrylicRefractionHeight;
        surface.RefractionAmount = LiquidGlassBar.AcrylicRefractionAmount;

        var frostTop = frosted.Edge[0];
        var frostBottom = frosted.Edge[^1];
        var rawTop = raw.Edge[0];
        var rawBottom = raw.Edge[^1];
        check(ChannelDistance(frostTop, rawTop) + 8 < ChannelDistance(frostTop, rawBottom)
              && ChannelDistance(frostBottom, rawBottom) + 8 < ChannelDistance(frostBottom, rawTop),
            "acrylic gutter shows the contrast behind it instead of a flat plate");
        check(Saturation(frostTop) > 18 && Saturation(frostBottom) > 18
              && ChannelDistance(frostTop, new Rgb(245, 247, 250)) > 80
              && ChannelDistance(frostBottom, new Rgb(245, 247, 250)) > 80,
            "acrylic gutter is not a solid white wash");
        var edgeShift = 0;
        for (var i = 0; i < frosted.Edge.Length; i++)
            edgeShift += ChannelDistance(frosted.Edge[i], flat.Edge[i]);
        var centerShift = ChannelDistance(frosted.Center, flat.Center);
        check(edgeShift > 24 && edgeShift > centerShift,
            "refraction warps the capsule edge more than its center (edge " + edgeShift + ", center " + centerShift + ")");
    }

    private readonly record struct Rgb(byte R, byte G, byte B);

    private readonly record struct NavSample(Rgb[] Edge, Rgb Center);

    private static NavSample CaptureNavSample(Window window, Border chrome)
    {
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("nav frame missing");
        var scale = frame.PixelSize.Width / window.Bounds.Width;
        var origin = chrome.TranslatePoint(default, window) ?? throw new InvalidOperationException("nav has no origin");
        var pixels = CopyFrame(frame);
        var height = chrome.Bounds.Height;
        double[] ys = [24, 110, 200, 290, 380, Math.Max(24, height - 24)];
        var edge = new Rgb[ys.Length];
        for (var i = 0; i < ys.Length; i++)
            edge[i] = Read(pixels, frame.PixelSize.Width, frame.PixelSize.Height, origin.X + 4, origin.Y + ys[i], scale);
        var center = Read(pixels, frame.PixelSize.Width, frame.PixelSize.Height, origin.X + chrome.Bounds.Width / 2, origin.Y + 82, scale);
        return new NavSample(edge, center);
    }

    private static byte[] CopyFrame(Avalonia.Media.Imaging.Bitmap frame)
    {
        var size = frame.PixelSize;
        var dips = frame.Size;
        var dpi = new Vector(96d * size.Width / dips.Width, 96d * size.Height / dips.Height);
        using var copy = new RenderTargetBitmap(size, dpi);
        using (var context = copy.CreateDrawingContext())
            context.DrawImage(frame, new Rect(dips), new Rect(dips));
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            copy.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), pin.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            pin.Free();
        }

        return pixels;
    }

    private static Rgb Read(byte[] pixels, int width, int height, double dipX, double dipY, double scale)
    {
        var x = Math.Clamp((int)Math.Round(dipX * scale), 0, width - 1);
        var y = Math.Clamp((int)Math.Round(dipY * scale), 0, height - 1);
        var index = ((y * width) + x) * 4;
        return new Rgb(pixels[index + 2], pixels[index + 1], pixels[index]);
    }

    private static int ChannelDistance(Rgb left, Rgb right) =>
        Math.Abs(left.R - right.R) + Math.Abs(left.G - right.G) + Math.Abs(left.B - right.B);

    private static int Saturation(Rgb color) =>
        Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));

    private static void CheckPageSurvivesDetach(Action<bool, string> check)
    {
        var view = new NetworkAcceleratorView
        {
            Width = 1360,
            Height = 900
        };
        var window = new Window
        {
            Width = 1360,
            Height = 900,
            Content = view,
            WindowDecorations = WindowDecorations.None
        };
        window.Show();
        Settle(window);
        view.PreviewNavigate("Settings");
        var settings = view.GetVisualDescendants().OfType<ToggleSwitch>()
            .Single(control => AutomationProperties.GetName(control) == "TUN 模式");
        check(!Hits(window, settings, Center(settings)), "mid-transition settings switch is not hit-testable yet");
        window.Content = null;
        Settle(window);
        window.Content = view;
        Settle(window);
        check(Hits(window, settings, Center(settings)), "returning mid-transition leaves the selected page clickable");
        view.PreviewNavigate("Overview");
        window.Content = null;
        Settle(window);
        window.Content = view;
        Settle(window);
        var proxy = view.GetVisualDescendants().OfType<AccentSwitch>()
            .Single(control => AutomationProperties.GetName(control) == "系统代理");
        var tun = view.GetVisualDescendants().OfType<AccentSwitch>()
            .Single(control => AutomationProperties.GetName(control) == "虚拟网卡");
        var lan = view.GetVisualDescendants().OfType<AccentSwitch>()
            .Single(control => AutomationProperties.GetName(control) == "局域网代理");
        var power = view.GetVisualDescendants().OfType<PowerButton>().Single();
        check(power.Focusable && AutomationProperties.GetName(power) == "开始或停止加速", "start button is focusable and named");
        check(Hits(window, power, Center(power)), "overview start button centre resolves to the button");
        var outsideCircle = new Point(1, 1);
        check(OutsideCircle(power, outsideCircle), "start button corner sample is outside the face");
        check(!Hits(window, power, outsideCircle), "overview start button corner does not hit the button");
        var starts = 0;
        power.Click += (_, _) => starts++;
        Click(window, power);
        check(starts == 1, "overview start button click raises Click");
        Click(window, power, outsideCircle);
        check(starts == 1, "overview start button ignores a point outside the circle");
        foreach (var toggle in new[] { proxy, tun, lan })
        {
            var name = AutomationProperties.GetName(toggle);
            check(toggle.Focusable, name + " switch is focusable");
            check(Hits(window, toggle, Center(toggle)), name + " switch centre is hit-testable");
            var outsideTrack = new Point(0.5, 0.5);
            check(!Hits(window, toggle, outsideTrack), name + " switch corner outside the track is not hit-testable");
        }

        var before = proxy.IsChecked == true;
        Click(window, proxy);
        check(proxy.IsChecked == true != before, "overview system proxy toggles after the interrupted transition");
        window.Close();
        view.DisposeResources();
    }

    private static void CheckSwitchSurvivesDetach(Action<bool, string> check)
    {
        var toggle = new AccentSwitch { IsChecked = false };
        var window = new Window
        {
            Width = 200,
            Height = 120,
            Content = toggle,
            WindowDecorations = WindowDecorations.None
        };
        window.Show();
        Settle(window);
        Click(window, toggle);
        check(toggle.IsChecked == true, "accent switch toggles on click");
        window.Content = null;
        Settle(window);
        check(Math.Abs(toggle.ThumbPosition - 1) < 0.001, "detached switch snaps the thumb to the checked end");
        window.Content = toggle;
        Settle(window);
        Click(window, toggle);
        check(toggle.IsChecked == false, "accent switch toggles after detach and reattach");
        MotionClock.Advance(TimeSpan.FromMilliseconds(400));
        check(Math.Abs(toggle.ThumbPosition) < 0.001, "reattached switch thumb finishes at the off end");
        window.Close();
    }

    private static void CheckCustomHitTargets(Action<bool, string> check)
    {
        var button = new PowerButton();
        var toggle = new AccentSwitch { IsChecked = false };
        var root = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        root.Children.Add(button);
        root.Children.Add(toggle);
        var window = new Window
        {
            Width = 240,
            Height = 200,
            Content = root,
            WindowDecorations = WindowDecorations.None
        };
        window.Show();
        Settle(window);
        var outsideCircle = new Point(1, 1);
        check(button.Focusable && button.IsTabStop, "standalone start button accepts keyboard focus");
        check(AutomationProperties.GetName(button) == "开始或停止加速", "standalone start button has an accessible name");
        check(Hits(window, button, Center(button)), "standalone start button centre is hit-testable");
        check(OutsideCircle(button, outsideCircle) && !Hits(window, button, outsideCircle), "standalone start button corner is outside the hit circle");
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        Click(window, button);
        check(clicks == 1, "mouse down and up on the start circle raises Click");
        Click(window, button, outsideCircle);
        check(clicks == 1, "mouse down and up outside the start circle does not raise Click");
        check(button.Focus(), "start button takes focus");
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.None, null!);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.None, null!);
        check(clicks == 2, "space activates the focused start button");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null!);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null!);
        check(clicks == 3, "enter activates the focused start button");

        check(Hits(window, toggle, Center(toggle)), "accent switch centre is hit-testable");
        check(!Hits(window, toggle, new Point(0.5, 0.5)), "accent switch corner outside the track is not hit-testable");
        Click(window, toggle);
        check(toggle.IsChecked == true, "accent switch centre click toggles");
        Click(window, toggle, new Point(0.5, 0.5));
        check(toggle.IsChecked == true, "accent switch corner does not toggle");
        check(toggle.Focus(), "accent switch takes focus");
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.None, null!);
        check(toggle.IsChecked == false, "space toggles the focused accent switch");
        window.Close();
    }

    private static void CheckClosedViewCanBeCollected(Action<bool, string> check)
    {
        var weak = CreateClosedView();
        for (var tick = 0; tick < 3; tick++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle);
        }

        Collect();
        check(!weak.IsAlive, "a closed network accelerator view can be collected");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateClosedView()
    {
        var view = new NetworkAcceleratorView { Width = 1360, Height = 900 };
        var window = new Window
        {
            Width = 1360,
            Height = 900,
            Content = view,
            WindowDecorations = WindowDecorations.None
        };
        window.Show();
        Settle(window);
        view.PreviewNavigate("Proxies");
        window.Content = null;
        window.Close();
        view.DisposeResources();
        return new WeakReference(view);
    }

    private static Point Center(Control control) => new(control.Bounds.Width / 2, control.Bounds.Height / 2);

    private static bool OutsideCircle(Control control, Point local)
    {
        var radius = Math.Min(control.Bounds.Width, control.Bounds.Height) / 2;
        var dx = local.X - control.Bounds.Width / 2;
        var dy = local.Y - control.Bounds.Height / 2;
        return new Rect(control.Bounds.Size).Contains(local) && dx * dx + dy * dy > radius * radius;
    }

    private static void Click(Window window, Control control) => Click(window, control, Center(control));

    private static void CheckRefreshIconsCentered(Action<bool, string> check)
    {
        var root = Directory.CreateTempSubdirectory("na-refresh-icon");
        var model = new NetworkAcceleratorViewModel(root.FullName, root.FullName);
        Window? window = null;
        try
        {
            model.PreviewSetConnection(true, false);
            model.Dash.ApplyRealtime(new RealtimeSnapshot(0, 0, 0, 0, [], [], [], 0, 0, []));
            var view = new OverviewDashboardView
            {
                DataContext = model,
                Width = 1100,
                Height = 820
            };
            window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
                WindowDecorations = WindowDecorations.None
            };
            window.Show();
            Settle(window);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

            var service = NamedButton(view, "重新测试服务");
            var subscription = NamedButton(view, "更新订阅");
            check(service.GetVisualDescendants().OfType<Viewbox>().Any(box => box.Stretch == Stretch.Uniform),
                "service refresh glyph sits in a uniform viewbox");
            check(subscription.GetVisualDescendants().OfType<Viewbox>().Any(box => box.Stretch == Stretch.Uniform),
                "subscription refresh glyph sits in a uniform viewbox");
            var spinner = subscription.GetVisualDescendants().OfType<AsterDock.UI.Controls.AdLoadingIndicator>().Single();
            check(!spinner.IsVisible, "subscription refresh hides its spinner until an update starts");
            CheckInkCentered(check, service, "service refresh icon is centered in its button");
            CheckInkCentered(check, subscription, "subscription refresh icon is centered in its button");
        }
        finally
        {
            window?.Close();
            model.Dispose();
            root.Delete(recursive: true);
        }
    }

    private static void CheckNavChromeShadow(Action<bool, string> check)
    {
        var reduceMotion = DashboardMotion.ReduceMotion;
        DashboardMotion.ReduceMotion = true;
        var previousTheme = Application.Current?.RequestedThemeVariant;
        Window? window = null;
        try
        {
            if (Application.Current is not null)
                Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            var view = new NetworkAcceleratorView { Width = 1100, Height = 820 };
            window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
                WindowDecorations = WindowDecorations.None
            };
            window.Show();
            Settle(window);
            var chrome = view.FindControl<Border>("NavChrome")!;
            var glass = view.FindControl<LiquidGlassBar>("NavGlass")!;
            var surface = glass.GetVisualDescendants().OfType<AcrylicInteractiveSurface>().Single();
            check(chrome.ClipToBounds == false && chrome.Margin.Right >= 8 && chrome.Margin.Bottom >= 8,
                "nav chrome keeps shadow room outside the capsule");
            check(chrome.BoxShadow.Count == 0
                  && surface.ShadowEnabled
                  && surface.ShadowRadius == 24
                  && surface.ShadowOffset == new Vector(0, 4)
                  && surface.ShadowColor.A == LiquidGlassBar.AcrylicShadowRestAlpha,
                "nav rest shadow is Fluid's drop, not a second chrome shadow");

            var point = chrome.TranslatePoint(new Point(chrome.Bounds.Width / 2, 36), window);
            check(point is not null, "nav chrome has a window point");
            if (point is { } hoverPoint)
            {
                window.MouseMove(hoverPoint);
                Settle(window);
            }

            var hoverScale = chrome.RenderTransform?.Value.M11 ?? 1;
            check(surface.ShadowColor.A == LiquidGlassBar.AcrylicShadowHoverAlpha && chrome.BoxShadow.Count == 0,
                "nav hover raises Fluid shadow alpha without a chrome drop");
            check(hoverScale > 1.06, "nav hover scales the capsule");

            chrome.Classes.Add("down");
            Settle(window);
            var pressScale = chrome.RenderTransform?.Value.M11 ?? 1;
            check(surface.ShadowColor.A == LiquidGlassBar.AcrylicShadowPressAlpha
                  && surface.ShadowRadius == 24
                  && surface.ShadowOffset.Y == 4,
                "nav press raises Fluid shadow alpha further");
            check(pressScale > hoverScale + 0.02, "nav press scales past the hover");
            chrome.Classes.Remove("down");
            window.MouseMove(new Point(0, 0));
            Settle(window);

            if (Application.Current is not null)
                Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Settle(window);
            check(chrome.BoxShadow.Count == 0
                  && surface.ShadowColor.A == LiquidGlassBar.AcrylicShadowRestAlpha
                  && surface.TintColor.A == 0,
                "nav dark theme keeps Fluid's rest shadow and no tint plate");
            check(glass.Bounds.Width > 1 && surface.BlurRadius == LiquidGlassBar.AcrylicBlurRadius,
                "styled nav glass stays on the Fluid acrylic surface");
        }
        finally
        {
            window?.Close();
            DashboardMotion.ReduceMotion = reduceMotion;
            if (Application.Current is not null)
                Application.Current.RequestedThemeVariant = previousTheme;
        }
    }

    private static Button NamedButton(Control root, string name) =>
        root.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);

    private static void CheckInkCentered(Action<bool, string> check, Control control, string message)
    {
        var width = Math.Max(1, (int)Math.Round(control.Bounds.Width));
        var height = Math.Max(1, (int)Math.Round(control.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(control);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), pin.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            pin.Free();
        }

        double weight = 0;
        double x = 0;
        double y = 0;
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var alpha = pixels[(row * stride) + (column * 4) + 3];
                if (alpha < 24) continue;
                weight += alpha;
                x += column * alpha;
                y += row * alpha;
            }
        }

        var centerX = (width - 1) / 2d;
        var centerY = (height - 1) / 2d;
        check(weight > 0
              && Math.Abs(x / weight - centerX) <= 2
              && Math.Abs(y / weight - centerY) <= 2,
            message);
    }

    private static void Click(Window window, Control control, Point local)
    {
        var point = control.TranslatePoint(local, window) ?? local;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static bool Hits(Window window, Control control, Point local)
    {
        var point = control.TranslatePoint(local, window);
        if (point is null) return false;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
        var hit = window.InputHitTest(point.Value);
        return hit is Visual found && (ReferenceEquals(found, control) || control.IsVisualAncestorOf(found));
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }
}

file sealed class InteractionApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new SemiTheme());
        Styles.Add(new UrsaSemiTheme());
        Styles.Add(new StyleInclude(new Uri("avares://AsterDock.UI/"))
        {
            Source = new Uri("avares://AsterDock.UI/Themes/AsterDockTheme.axaml")
        });
    }
}
