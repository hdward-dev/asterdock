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
            LiquidGlassBar.ResetSessionChoice();
            DashboardMotion.ReduceMotion = reduceMotion;
            MotionClock.Manual = manual;
        }
    }

    private static void CheckNavGlassChoice(Action<bool, string> check)
    {
        check(NavGlassChoice.SlowMilliseconds == 40, "nav glass treats a capture over 40ms as slow");
        check(NavGlassChoice.UseApproximate(new NavGlassProbe(true, 41, false)),
            "software rendering with a slow snapshot uses the approximate nav");
        check(!NavGlassChoice.UseApproximate(NavGlassProbe.FastSoftware),
            "a fast software snapshot still tries acrylic");
        check(!NavGlassChoice.UseApproximate(new NavGlassProbe(false, 80, false)),
            "a gpu renderer keeps acrylic when only the cpu snapshot was slow");
        check(NavGlassChoice.UseApproximate(new NavGlassProbe(null, 41, false)),
            "an unknown renderer with a slow snapshot does not guess acrylic");
        check(!NavGlassChoice.UseApproximate(new NavGlassProbe(null, 40, false)),
            "a snapshot of 40ms is still inside the budget");
        check(NavGlassChoice.UseApproximate(new NavGlassProbe(false, 1, true)),
            "a failed probe uses the approximate nav");
        check(!NavGlassChoice.SustainedSlow(2) && NavGlassChoice.SustainedSlow(3),
            "acrylic retires after three slow frames, not two");
        var light = LiquidGlassBar.CreateAcrylicTint(false);
        var dark = LiquidGlassBar.CreateAcrylicTint(true);
        check(light.R == 0xF5 && light.G == 0xF7 && light.B == 0xFA && light.A == LiquidGlassBar.AcrylicLightTintAlpha,
            "acrylic light tint is a thin page wash");
        check(dark.R == 0x2A && dark.G == 0x3A && dark.B == 0x52 && dark.A == LiquidGlassBar.AcrylicDarkTintAlpha,
            "acrylic dark tint is a thin slate wash");

        LiquidGlassBar.ResetSessionChoice();
        LiquidGlassBar.ProbeOverride = NavGlassProbe.FastSoftware;
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
            var surface = glass.GetVisualDescendants().OfType<AcrylicSurface>().Single();
            check(glass.ActivePath == NavGlassPath.Acrylic && LiquidGlassBar.SessionPath == NavGlassPath.Acrylic,
                "a fast probe installs acrylic for the session");
            check(surface.BlurRadius == LiquidGlassBar.AcrylicBlurRadius
                  && Math.Abs(surface.Vibrancy - LiquidGlassBar.AcrylicVibrancy) < 0.001
                  && Math.Abs(surface.Brightness - LiquidGlassBar.AcrylicBrightness) < 0.001,
                "acrylic nav uses the Fluid demo frost");
            check(surface is AcrylicInteractiveSurface { IsInteractive: true, RevealBorderEnabled: true }
                  && surface.RefractionHeight == LiquidGlassBar.AcrylicRefractionHeight
                  && surface.RefractionAmount == LiquidGlassBar.AcrylicRefractionAmount
                  && !surface.ChromaticAberration,
                "acrylic nav is Fluid's interactive surface with refraction and a reveal rim");
            check(surface.HighlightEnabled && surface.ShadowEnabled
                  && Math.Abs(surface.HighlightOpacity - LiquidGlassBar.AcrylicHighlightRest) < 0.001
                  && Math.Abs(surface.HighlightWidth - 0.5) < 0.001,
                "acrylic nav keeps Fluid highlight and drop shadow");
            var scroll = view.FindControl<ScrollViewer>("PageScroll")!;
            var chrome = view.FindControl<Border>("NavChrome")!;
            var scrollRight = scroll.TranslatePoint(new Point(scroll.Bounds.Width, 0), window)?.X;
            var barLeft = chrome.TranslatePoint(default, window)?.X;
            check(scrollRight is double pageRight && barLeft is double navLeft && pageRight > navLeft + 8,
                "page scroll extends under the floating nav");
            var body = view.FindControl<StackPanel>("PageBody")!;
            var bodyRight = body.TranslatePoint(new Point(body.Bounds.Width, 0), window)?.X;
            check(bodyRight is double contentRight && barLeft is double navEdge && contentRight <= navEdge - 11,
                "page content keeps the reserved column clearance beside the nav");
            check(view.FindControl<Border>("NavChrome")!.BoxShadow.Count == 0,
                "nav chrome does not stack a second shadow over Fluid's");
            check(surface.CornerRadius == new CornerRadius(LiquidGlassBar.Corner), "acrylic nav keeps the 24 corner");
            check(surface.TintColor == LiquidGlassBar.CreateAcrylicTint(false), "acrylic nav uses the light tint");
            check(AcrylicBackdrop.GetIsExcludedFromCapture(surface)
                  && AcrylicBackdrop.GetIsExcludedFromCapture(glass)
                  && AcrylicBackdrop.GetIsExcludedFromCapture(view.FindControl<Border>("NavChrome")!),
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
            check(hoverScale > 1.04, "acrylic hover scales the capsule enough to see");
            check(surface.HighlightOpacity > 0.8, "acrylic hover strengthens the Fluid highlight");
            check(surface.ShadowColor.A > 0x1A && chrome.BoxShadow.Count == 0,
                "acrylic hover deepens Fluid's own shadow without a second drop");
            check(Hits(window, overview, Center(overview)), "section button still hit-tests while the capsule is hovered");
            window.MouseMove(new Point(4, 4));
            Settle(window);

            if (Application.Current is not null)
                Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Settle(window);
            check(surface.TintColor == LiquidGlassBar.CreateAcrylicTint(true), "acrylic nav follows the dark tint");

            check(!glass.ReportFrameMilliseconds(50), "one slow acrylic frame keeps the effect");
            check(!glass.ReportFrameMilliseconds(10), "a fast frame clears the slow streak");
            check(!glass.ReportFrameMilliseconds(50) && !glass.ReportFrameMilliseconds(50),
                "two slow frames after a reset stay on acrylic");
            check(glass.ReportFrameMilliseconds(50), "the third consecutive slow frame retires acrylic");
            Settle(window);
            check(glass.ActivePath == NavGlassPath.Approximate
                  && LiquidGlassBar.SessionPath == NavGlassPath.Approximate
                  && !glass.GetVisualDescendants().OfType<AcrylicSurface>().Any(),
                "retired nav glass is the approximate capsule again");
            check(Hits(window, overview, Center(overview)), "section button still hit-tests after the fallback");
            window.Close();
            window = null;

            LiquidGlassBar.ProbeOverride = NavGlassProbe.FastSoftware;
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
            check(followGlass.ActivePath == NavGlassPath.Approximate
                  && !followGlass.GetVisualDescendants().OfType<AcrylicSurface>().Any(),
                "a later nav stays on the approximate glass for the session");
            follow.Close();
            follow = null;

            LiquidGlassBar.ResetSessionChoice();
            var probedView = new NetworkAcceleratorView { Width = 1100, Height = 820 };
            window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = probedView,
                WindowDecorations = WindowDecorations.None
            };
            window.Show();
            Settle(window);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            Settle(window);
            var probed = probedView.FindControl<LiquidGlassBar>("NavGlass")!;
            check(probed.RendererKnown && probed.ActivePath != NavGlassPath.Pending,
                "nav glass reads the Skia lease and chooses acrylic or the approximate fallback");
            if (probed.SoftwareRenderer == true && probed.SnapshotMilliseconds > NavGlassChoice.SlowMilliseconds)
                check(probed.ActivePath == NavGlassPath.Approximate,
                    "a slow software lease stays on the approximate nav");
            if (probed.ActivePath == NavGlassPath.Acrylic)
            {
                var section = NamedButton(probedView, "代理");
                window.Content = null;
                Settle(window);
                window.Content = probedView;
                Settle(window);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                check(probed.ActivePath == NavGlassPath.Acrylic
                      && Hits(window, section, Center(section)),
                    "acrylic nav keeps section buttons after detach and reattach");
            }
        }
        finally
        {
            window?.Close();
            follow?.Close();
            LiquidGlassBar.ResetSessionChoice();
            LiquidGlassBar.ProbeOverride = NavGlassProbe.SlowSoftware;
            if (Application.Current is not null)
                Application.Current.RequestedThemeVariant = previousTheme;
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
