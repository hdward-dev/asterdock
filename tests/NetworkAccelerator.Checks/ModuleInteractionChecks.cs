using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
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
            check(chrome.ClipToBounds == false && chrome.Margin.Right >= 8 && chrome.Margin.Bottom >= 8,
                "nav chrome keeps shadow room outside the capsule");
            var rest = LiquidGlassBar.CreateOuterShadow(false, false, false);
            check(rest.Count == 1 && rest[0].OffsetX == 0 && rest[0].OffsetY == 4 && rest[0].Blur == 24 && rest[0].Color.A == 0x1A,
                "nav rest shadow is black at 10% with blur 24 and offset 4");
            check(SameShadow(chrome.BoxShadow, rest), "nav chrome uses that rest shadow");
            check(LiquidGlassBar.BackdropBlurRadius == 8 && LiquidGlassBar.SlowBackdropBlurRadius == 5,
                "nav snapshot blur starts at 8 and falls back to 5");

            var point = chrome.TranslatePoint(new Point(chrome.Bounds.Width / 2, 36), window);
            check(point is not null, "nav chrome has a window point");
            if (point is { } hoverPoint)
            {
                window.MouseMove(hoverPoint);
                Settle(window);
            }

            var hover = LiquidGlassBar.CreateOuterShadow(false, true, false);
            check(hover.Count == 1 && hover[0].OffsetY == 4 && hover[0].Blur == 24 && hover[0].Color.A > rest[0].Color.A,
                "nav hover raises shadow alpha without changing the drop");
            check(SameShadow(chrome.BoxShadow, hover), "nav chrome uses that hover shadow");
            var hoverScale = chrome.RenderTransform?.Value.M11 ?? 1;
            check(hoverScale > 1.01 && hoverScale < 1.03, "nav hover lifts the chrome slightly");

            chrome.Classes.Add("down");
            Settle(window);
            var pressed = LiquidGlassBar.CreateOuterShadow(false, true, true);
            check(pressed[0].Color.A > hover[0].Color.A && pressed[0].Blur == 24 && pressed[0].OffsetY == 4,
                "nav press raises shadow alpha further");
            check(SameShadow(chrome.BoxShadow, pressed), "nav press uses that stronger shadow");
            var pressScale = chrome.RenderTransform?.Value.M11 ?? 1;
            check(pressScale >= hoverScale - 0.001 && pressScale < 1.03, "nav press keeps a slight scale");
            chrome.Classes.Remove("down");
            window.MouseMove(new Point(0, 0));
            Settle(window);

            if (Application.Current is not null)
                Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Settle(window);
            var dark = chrome.BoxShadow;
            var darkExpected = LiquidGlassBar.CreateOuterShadow(true, false, false);
            check(SameShadow(dark, darkExpected)
                  && dark[0].OffsetY == 4 && dark[0].Blur == 24 && dark[0].Color.A > rest[0].Color.A,
                "nav dark shadow keeps the same drop with a stronger alpha");

            var bar = new LiquidGlassBar { Width = 72, Height = 280 };
            bar.Measure(new Size(72, 280));
            bar.Arrange(new Rect(0, 0, 72, 280));
            using var bitmap = new RenderTargetBitmap(new PixelSize(72, 280), new Vector(96, 96));
            bitmap.Render(bar);
            check(glass.Bounds.Width > 1, "styled nav glass laid out beside the fallback render");
        }
        finally
        {
            window?.Close();
            DashboardMotion.ReduceMotion = reduceMotion;
            if (Application.Current is not null)
                Application.Current.RequestedThemeVariant = previousTheme;
        }
    }

    private static bool SameShadow(BoxShadows actual, BoxShadows expected)
    {
        if (actual.Count != expected.Count) return false;
        for (var i = 0; i < actual.Count; i++)
        {
            var left = actual[i];
            var right = expected[i];
            if (left.IsInset != right.IsInset) return false;
            if (Math.Abs(left.OffsetX - right.OffsetX) > 0.01) return false;
            if (Math.Abs(left.OffsetY - right.OffsetY) > 0.01) return false;
            if (Math.Abs(left.Blur - right.Blur) > 0.01) return false;
            if (Math.Abs(left.Spread - right.Spread) > 0.01) return false;
            if (left.Color != right.Color) return false;
        }

        return true;
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
