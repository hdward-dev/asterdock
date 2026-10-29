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
            CheckNavHoverColors(check);
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
        var view = new NetworkAcceleratorView { Width = 1100, Height = 820 };
        var window = new Window
        {
            Width = 1100,
            Height = 820,
            Content = view,
            WindowDecorations = WindowDecorations.None
        };
        try
        {
            window.Show();
            Settle(window);
            var glass = view.FindControl<LiquidGlassBar>("NavGlass")!;
            var surfaces = glass.GetVisualDescendants().OfType<AcrylicSurface>().ToList();
            var frost = surfaces.Single(surface => surface.BlurRadius > 0);
            var lens = surfaces.Single(surface => surface.BlurRadius == 0);
            check(frost.BlurRadius == 9 && frost.InnerShadowEnabled,
                "resting nav retains the rim5 frost and inner shadow");
            check(frost.TintColor.A is > 0 and < 255 && frost.SurfaceColor.A == 0 && frost.Vibrancy == 1
                  && frost.InnerShadowOpacity <= 0.06,
                "menu frost keeps its neutral tint translucent and preserves the backdrop");
            var scroll = view.FindControl<ScrollViewer>("PageScroll")!;
            var content = view.FindControl<StackPanel>("PageContent")!;
            var contentRight = content.TranslatePoint(new Point(content.Bounds.Width, 0), window)!.Value.X;
            var glassLeft = glass.TranslatePoint(default, window)!.Value.X;
            var scrollRight = scroll.TranslatePoint(new Point(scroll.Bounds.Width, 0), window)!.Value.X;
            check(scrollRight <= glassLeft && contentRight <= glassLeft - 24,
                "page content and its scrollbar reserve space for the full floating lens");
            check(!AcrylicBackdrop.GetIsExcludedFromCapture(frost),
                "main frost is available to the hover material");
            check(AcrylicBackdrop.GetIsExcludedFromCapture(
                    glass.GetVisualDescendants().OfType<PassThroughBorder>().Single()),
                "frost excludes the menu icons instead of blurring a second copy behind them");
            check(lens.IsVisible && lens.Bounds.X >= frost.Bounds.X + 3
                  && lens.Bounds.Right <= frost.Bounds.Right - 3
                  && lens.Bounds.Y >= frost.Bounds.Y
                  && lens.Bounds.Bottom <= frost.Bounds.Bottom,
                "resting selected lens is fully inside the main frost");
            var sideInset = lens.Bounds.X - frost.Bounds.X;
            check(Math.Abs(lens.Bounds.Y - frost.Bounds.Y - sideInset) < 0.6
                  && Math.Abs(frost.Bounds.Right - lens.Bounds.Right - sideInset) < 0.6,
                "top selection has equal top, left and right insets");
            var overview = NamedButton(view, "概览");
            check(Hits(window, overview, Center(overview)), "section buttons hit-test through glass siblings");
            overview.Focus();
            Settle(window);
            check(overview.IsFocused, "section buttons retain keyboard focus");

            var origin = overview.TranslatePoint(default, glass)!.Value;
            var x = origin.X + overview.Bounds.Width / 2;
            var y = origin.Y + overview.Bounds.Height / 2;
            var restSize = lens.Bounds.Size;
            var restRadius = lens.CornerRadius.TopLeft;
            foreach (var offset in new[] { 0.0, 9.0, 19.0, 37.0 })
            {
                var point = glass.TranslatePoint(new Point(x, y + offset), window)!.Value;
                window.MouseMove(point);
                MotionClock.Advance(TimeSpan.FromSeconds(1));
                Settle(window);
                check(lens.IsVisible && lens.Bounds.Width > frost.Bounds.Width
                      && lens.Bounds.Height > restSize.Height,
                    "hover lens enlarges both dimensions outside the frost");
                CheckUniformScale(restSize, restRadius, "hover preserves the selected frame proportions");
                check(Math.Abs(lens.Bounds.Center.Y - y - offset) < 1,
                    $"lens follows intermediate pointer Y positions without section snapping: actual={lens.Bounds.Center.Y}, expected={y + offset}");
                check(lens.Bounds.X >= 0 && lens.Bounds.Y >= 0
                      && lens.Bounds.Right <= glass.Bounds.Width + 0.5
                      && lens.Bounds.Bottom <= glass.Bounds.Height + 0.5,
                    "lens overhang stays in the padded positive coordinate system");
            }
            check(lens.BackdropZoom == 1 && !lens.ChromaticAberration
                  && !lens.DepthEffect && lens.BlurRadius == 0 && lens.Vibrancy == 1,
                "lens keeps the center at 1:1 and avoids dispersion and a second frost pass");
            check(Hits(window, overview, Center(overview)), "hover lens does not intercept navigation clicks");
            glass.PreviewHoverMotion(new Point(x, y + 100), 1000);
            glass.PreviewHoverMotion(new Point(x, y + 102), 1020);
            Settle(window);
            var slowHeight = lens.Bounds.Height;
            glass.PreviewHoverMotion(new Point(x, y + 122), 1040);
            Settle(window);
            check(lens.Bounds.Height > slowHeight + 2
                  && lens.Bounds.Width <= glass.Bounds.Width
                  && lens.Bounds.X >= 0 && lens.Bounds.Bottom <= glass.Bounds.Height,
                "accelerated movement enlarges the lens while keeping its layout inside the pads");
            CheckUniformScale(restSize, restRadius, "elastic motion preserves the selected frame proportions");
            MotionClock.Advance(TimeSpan.FromSeconds(1));
            Settle(window);
            check(Math.Abs(lens.Bounds.Height / restSize.Height - LiquidGlassBar.LensScale) < 0.02,
                "elastic motion relaxes to the uniform hover scale when pointer samples stop");
            glass.PreviewHoverBulge(null);
            window.MouseMove(new Point(0, 0));
            Settle(window);
            MotionClock.Advance(TimeSpan.FromSeconds(1));
            Settle(window);
            check(lens.IsVisible && Math.Abs(lens.Bounds.Center.Y - y) < 1
                  && lens.Bounds.X >= frost.Bounds.X + 3
                  && lens.Bounds.Right <= frost.Bounds.Right - 3,
                "the same glass shrinks inside the frost and returns to selection after pointer exit");
            view.PreviewNavigate("Settings");
            MotionClock.Advance(TimeSpan.FromSeconds(1));
            Settle(window);
            sideInset = lens.Bounds.X - frost.Bounds.X;
            check(Math.Abs(frost.Bounds.Bottom - lens.Bounds.Bottom - sideInset) < 0.6
                  && Math.Abs(frost.Bounds.Right - lens.Bounds.Right - sideInset) < 0.6,
                "bottom selection has equal bottom, left and right insets");
            view.PreviewNavigate("Proxies");
            MotionClock.Advance(TimeSpan.FromSeconds(1));
            Settle(window);
            var proxy = NamedButton(view, "代理");
            var selectedCenter = proxy.TranslatePoint(Center(proxy), glass)!.Value;
            check(Math.Abs(lens.Bounds.Center.Y - selectedCenter.Y) < 1,
                "selection moves the existing glass rather than adding another pill");

            foreach (var (section, index) in new[]
                     {
                         ("Overview", 0), ("Proxies", 1), ("Configuration", 2), ("Connections", 3),
                         ("Rules", 4), ("Logs", 5), ("Settings", 6)
                     })
            {
                view.PreviewNavigate(section);
                MotionClock.Advance(TimeSpan.FromSeconds(1));
                Settle(window);
                var selectedSize = lens.Bounds.Size;
                var selectedRadius = lens.CornerRadius.TopLeft;
                check(Math.Abs(selectedSize.Width - restSize.Width) < 0.6
                      && Math.Abs(selectedSize.Height - restSize.Height) < 0.6,
                    $"{section}: selected frame has the same size as the end selections");
                glass.PreviewHoverBulgeAt(index);
                MotionClock.Advance(TimeSpan.FromMilliseconds(25));
                Settle(window);
                CheckUniformScale(selectedSize, selectedRadius, $"{section}: expansion preserves proportions");
                MotionClock.Advance(TimeSpan.FromSeconds(1));
                Settle(window);
                CheckUniformScale(selectedSize, selectedRadius, $"{section}: full lens preserves proportions");
                check(Math.Abs(lens.Bounds.Width / selectedSize.Width - LiquidGlassBar.LensScale) < 0.02,
                    $"{section}: hover uses the same enlargement for end and middle selections");
                check(Math.Abs(lens.Bounds.Height / restSize.Height - LiquidGlassBar.LensScale) < 0.02,
                    $"{section}: expanded lens has the same height for every selection");
                glass.PreviewHoverBulge(null);
                MotionClock.Advance(TimeSpan.FromMilliseconds(25));
                Settle(window);
                CheckUniformScale(selectedSize, selectedRadius, $"{section}: collapse preserves proportions");
                MotionClock.Advance(TimeSpan.FromSeconds(1));
                Settle(window);
            }

            window.Content = null;
            Settle(window);
            window.Content = view;
            Settle(window);
            var reattached = overview.TranslatePoint(Center(overview), window)!.Value;
            window.MouseMove(reattached);
            Settle(window);
            check(lens.IsVisible && Hits(window, overview, Center(overview)),
                "glass follows the pointer and keeps buttons after detach and reattach");

            void CheckUniformScale(Size original, double originalRadius, string message)
            {
                var widthScale = lens.Bounds.Width / original.Width;
                var heightScale = lens.Bounds.Height / original.Height;
                var cornerScale = lens.CornerRadius.TopLeft / originalRadius;
                check(Math.Abs(widthScale - heightScale) < 0.02 && Math.Abs(widthScale - cornerScale) < 0.02,
                    $"{message}: width={widthScale:0.000}, height={heightScale:0.000}, corner={cornerScale:0.000}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void CheckNavHoverColors(Action<bool, string> check)
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var reduceMotion = DashboardMotion.ReduceMotion;
            DashboardMotion.ReduceMotion = true;
            var view = new NetworkAcceleratorView { Width = 1100, Height = 820 };
            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
                RequestedThemeVariant = theme
            };
            try
            {
                window.Show();
                foreach (var button in view.GetVisualDescendants().OfType<Button>()
                             .Where(button => button.Classes.Contains("section-nav")))
                    button.Transitions = null;
                window.MouseMove(default);
                Settle(window);
                var overview = NamedButton(view, "概览");
                var proxy = NamedButton(view, "代理");
                check(view.TryFindResource("AppAccentBrush", theme, out var accentResource)
                      && accentResource is ISolidColorBrush, $"{theme}: navigation accent resource is available");
                var accent = ((ISolidColorBrush)accentResource!).Color;
                var muted = ((ISolidColorBrush)proxy.Foreground!).Color;
                CheckInk(overview, accent, $"{theme}: current page is highlighted while the lens rests");

                foreach (var name in new[] { "概览", "代理", "配置", "连接", "规则", "日志", "设置" })
                {
                    var button = NamedButton(view, name);
                    window.MouseMove(button.TranslatePoint(Center(button), window)!.Value);
                    Settle(window);
                    CheckInk(button, accent, $"{theme}: hovering {name} colors its icon and label");
                    CheckInk(overview, button == overview ? accent : muted,
                        $"{theme}: hovering {name} leaves no second highlight on the current page");
                    check(overview.Classes.Contains("selected")
                          && (button == overview || !button.Classes.Contains("selected")),
                        $"{theme}: hovering {name} does not navigate");
                    window.MouseMove(default);
                    Settle(window);
                    CheckInk(button, button == overview ? accent : muted,
                        $"{theme}: leaving {name} clears the floating highlight");
                    CheckInk(overview, accent, $"{theme}: leaving {name} restores the current page highlight");
                }

                var glass = view.FindControl<LiquidGlassBar>("NavGlass")!;
                window.MouseMove(overview.TranslatePoint(
                    new Point(overview.Bounds.Width / 2, overview.Bounds.Height + 6), window)!.Value);
                Settle(window);
                check(!overview.IsPointerOver && !proxy.IsPointerOver,
                    $"{theme}: gap pointer is outside both menu buttons");
                CheckInk(proxy, accent, $"{theme}: lens highlights the nearest menu while crossing a gap");
                CheckInk(overview, muted, $"{theme}: gap lens replaces the resting highlight");

                window.MouseMove(glass.TranslatePoint(new Point(LiquidGlassBar.LensPadLeft + 1,
                    proxy.TranslatePoint(Center(proxy), glass)!.Value.Y), window)!.Value);
                Settle(window);
                check(!proxy.IsPointerOver, $"{theme}: side padding pointer is outside the menu button");
                CheckInk(proxy, accent, $"{theme}: side padding follows the lens rather than button hover");

                glass.PreviewHoverBulgeAt(4);
                Settle(window);
                CheckInk(NamedButton(view, "规则"), accent, $"{theme}: preview lens shares the menu highlight");
                CheckInk(proxy, muted, $"{theme}: preview lens clears the previous floating highlight");
                glass.PreviewHoverBulge(null);
                window.MouseMove(default);
                Settle(window);
                CheckInk(overview, accent, $"{theme}: clearing preview restores the selected page highlight");

                view.PreviewNavigate("Proxies");
                Settle(window);
                CheckInk(proxy, accent, $"{theme}: selecting a page moves the resting highlight");
                CheckInk(overview, muted, $"{theme}: previous selection returns to its idle color");

                var settings = NamedButton(view, "设置");
                var rules = NamedButton(view, "规则");
                window.MouseMove(settings.TranslatePoint(Center(settings), window)!.Value);
                view.PreviewNavigate("Rules");
                Settle(window);
                CheckInk(settings, accent, $"{theme}: navigation during hover keeps the highlight on the lens");
                CheckInk(rules, muted, $"{theme}: new selection waits for the floating lens to leave");
                window.MouseMove(default);
                Settle(window);
                CheckInk(rules, accent, $"{theme}: pointer exit restores the latest selection");
                CheckInk(settings, muted, $"{theme}: pointer exit clears the floating highlight");
            }
            finally
            {
                window.Close();
                DashboardMotion.ReduceMotion = reduceMotion;
            }
        }

        void CheckInk(Button button, Color expected, string message)
        {
            var icon = button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
            var label = button.GetVisualDescendants().OfType<TextBlock>().Single();
            check(button.Foreground is ISolidColorBrush foreground && foreground.Color == expected
                  && icon.Fill is ISolidColorBrush fill && fill.Color == expected
                  && label.Foreground is ISolidColorBrush text && text.Color == expected,
                $"{message}: expected={expected}, button={button.Foreground}, icon={icon.Fill}, label={label.Foreground}");
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
        var view = new NetworkAcceleratorView { Width = 1100, Height = 820 };
        var window = new Window { Width = 1100, Height = 820, Content = view };
        try
        {
            window.Show();
            Settle(window);
            var chrome = view.FindControl<Border>("NavChrome")!;
            var glass = view.FindControl<LiquidGlassBar>("NavGlass")!;
            var shadow = glass.GetVisualDescendants().OfType<Border>().Single(border => border.BoxShadow.Count > 0);
            var frost = glass.GetVisualDescendants().OfType<AcrylicSurface>().Single(surface => surface.BlurRadius > 0);
            check(shadow.IsVisible && shadow.Opacity == 1 && shadow.Bounds == frost.Bounds,
                "the whole menu casts its shadow while idle");
            var button = NamedButton(view, "代理");
            window.MouseMove(button.TranslatePoint(Center(button), window)!.Value);
            MotionClock.Advance(TimeSpan.FromSeconds(1));
            Settle(window);
            check(chrome.RenderTransform is null || chrome.RenderTransform.Value == Matrix.Identity,
                "hover never scales the whole navigation bar");
            check(chrome.BoxShadow.Count == 0 && !chrome.ClipToBounds,
                "padded chrome does not paint a second frame or clip the glass bulge");
            check(shadow.IsVisible && shadow.Opacity == 1 && shadow.Bounds == frost.Bounds
                  && shadow.CornerRadius == frost.CornerRadius && !shadow.ClipToBounds && !frost.ShadowEnabled,
                "one unclipped shadow follows the whole menu rather than the lens");
            check(!shadow.IsHitTestVisible && AcrylicBackdrop.GetIsExcludedFromCapture(shadow)
                  && Hits(window, button, Center(button)),
                "menu elevation preserves the clear backdrop and navigation hit targets");
            window.MouseMove(default);
            MotionClock.Advance(TimeSpan.FromSeconds(1));
            Settle(window);
            check(shadow.IsVisible && shadow.Opacity == 1 && shadow.Bounds == frost.Bounds,
                "the menu remains elevated when the pointer leaves");
        }
        finally
        {
            window.Close();
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
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
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
