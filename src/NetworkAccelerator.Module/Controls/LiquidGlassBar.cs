using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fluid.Avalonia.Acrylic;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// Floating capsule drawn over the page with Fluid <see cref="AcrylicInteractiveSurface"/>.
/// There is no opaque or snapshot fallback: a failed surface is reported and retried,
/// and the bar never paints a solid plate in its place.
/// </summary>
public class LiquidGlassBar : Decorator, ICustomHitTest
{
    public const double Corner = 24;

    /// <summary>Fluid demo cards with a 24 corner: blur 14, vibrancy 1.4, brightness 0.06.</summary>
    public const double AcrylicBlurRadius = 14;

    public const double AcrylicVibrancy = 1.4;
    public const double AcrylicBrightness = 0.06;

    /// <summary>
    /// Wider and stronger than the library 12/24 so the warp covers this narrow capsule.
    /// Refraction only bends pixels within <see cref="AcrylicRefractionHeight"/> of the edge.
    /// </summary>
    public const double AcrylicRefractionHeight = 36;

    public const double AcrylicRefractionAmount = 52;

    /// <summary>Library highlight at rest. Hover and press raise it so the rim reads.</summary>
    public const double AcrylicHighlightRest = 0.5;

    public const double AcrylicHighlightHover = 1;

    public const byte AcrylicShadowRestAlpha = 0x1A;
    public const byte AcrylicShadowHoverAlpha = 0x66;
    public const byte AcrylicShadowPressAlpha = 0x99;
    public const double AcrylicInteractiveDip = 64;

    private const double AcrylicHoverScale = 1.08;
    private const double AcrylicPressScale = 1.14;
    private const int AcrylicAttempts = 3;

    private Border? _chrome;
    private AcrylicInteractiveSurface? _acrylic;
    private Control? _payload;
    private TopLevel? _pointerHost;
    private bool _hovered;
    private bool _pressed;
    private bool _pointerOver;
    private bool _pointerDown;
    private bool _attached;
    private int _attempts;
    private int _retrySerial;

    /// <summary>
    /// Fluid hue-blends <c>TintColor</c> at full strength whenever alpha is above zero, then
    /// fills again at alpha × 0.75. A pale tint therefore becomes a solid plate. Transparent
    /// skips both passes so the sampled page stays visible.
    /// </summary>
    public static Color CreateAcrylicTint(bool dark) => dark ? Colors.Transparent : Colors.Transparent;

    public bool HitTest(Point point) => false;

    public string SnapshotNote { get; private set; } = "pending";

    /// <summary>Asks the acrylic surface to sample the page again after the visible section changes.</summary>
    public void Refresh()
    {
        InvalidateVisual();
        _acrylic?.InvalidateVisual();
    }

    public LiquidGlassBar()
    {
        SizeChanged += (_, _) =>
        {
            if (_attached && _acrylic is null)
                TryEnterAcrylic();
        };
        ActualThemeVariantChanged += (_, _) => ApplyInteraction();
    }

    public override void Render(DrawingContext context)
    {
        // The acrylic child is the only paint. This control never fills a degraded plate.
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _attempts = 0;
        DashboardMotion.Changed += OnMotionChanged;
        WatchChrome();
        HookPointer();
        TryEnterAcrylic();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _retrySerial++;
        _pointerOver = false;
        _pointerDown = false;
        _hovered = false;
        _pressed = false;
        DashboardMotion.Changed -= OnMotionChanged;
        UnhookPointer();
        ReleaseChrome();
        base.OnDetachedFromVisualTree(e);
    }

    private void TryEnterAcrylic()
    {
        if (!_attached || _acrylic is not null) return;
        try
        {
            EnterAcrylic();
        }
        catch (Exception ex)
        {
            var message = ex.GetBaseException().Message;
            SnapshotNote = "acrylic failed: " + message;
            Trace.TraceError("Network accelerator nav acrylic failed: {0}", ex);
            ScheduleRetry();
        }
    }

    private void ScheduleRetry()
    {
        if (!_attached || _attempts >= AcrylicAttempts) return;
        _attempts++;
        var serial = ++_retrySerial;
        var weak = new WeakReference<LiquidGlassBar>(this);
        Dispatcher.UIThread.Post(() =>
        {
            if (!weak.TryGetTarget(out var bar) || !bar._attached || bar._retrySerial != serial)
                return;
            bar.TryEnterAcrylic();
        }, DispatcherPriority.Background);
    }

    private void EnterAcrylic()
    {
        RememberPayload();
        if (_acrylic is null)
        {
            var surface = CreateAcrylicSurface();
            if (ReferenceEquals(Child, _payload))
                Child = null;
            surface.Content = _payload;
            _acrylic = surface;
            Child = surface;
        }

        ExcludeAcrylicFromCapture();
        _acrylic.TintColor = CreateAcrylicTint(false);
        _acrylic.SurfaceColor = Colors.Transparent;
        SnapshotNote =
            $"acrylic blur {AcrylicBlurRadius:0.#} vibrancy {AcrylicVibrancy:0.0} brightness {AcrylicBrightness:0.00} " +
            $"refraction {AcrylicRefractionHeight:0.#}/{AcrylicRefractionAmount:0.#} tint #00000000";
        ApplyInteraction();
        InvalidateVisual();
    }

    private void RememberPayload()
    {
        if (_payload is not null) return;
        _payload = Child is AcrylicSurface ? null : Child;
    }

    private AcrylicInteractiveSurface CreateAcrylicSurface()
    {
        var surface = new AcrylicInteractiveSurface
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(Corner),
            BlurRadius = AcrylicBlurRadius,
            Vibrancy = AcrylicVibrancy,
            Brightness = AcrylicBrightness,
            RefractionHeight = AcrylicRefractionHeight,
            RefractionAmount = AcrylicRefractionAmount,
            DepthEffect = true,
            ChromaticAberration = false,
            HighlightEnabled = true,
            ShadowEnabled = true,
            ShadowRadius = 24,
            ShadowOffset = new Vector(0, 4),
            ShadowOpacity = 1,
            RevealBorderEnabled = true,
            IsInteractive = false,
            InteractiveMaxScaleDip = AcrylicInteractiveDip,
            TintColor = Colors.Transparent,
            SurfaceColor = Colors.Transparent
        };
        AcrylicBackdrop.SetIsExcludedFromCapture(surface, true);
        return surface;
    }

    private void ExcludeAcrylicFromCapture()
    {
        if (_acrylic is not null)
            AcrylicBackdrop.SetIsExcludedFromCapture(_acrylic, true);
        AcrylicBackdrop.SetIsExcludedFromCapture(this, true);
        var chrome = NavChrome();
        if (chrome is not null)
            AcrylicBackdrop.SetIsExcludedFromCapture(chrome, true);
    }

    private void HookPointer()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || ReferenceEquals(_pointerHost, top)) return;
        UnhookPointer();
        _pointerHost = top;
        top.AddHandler(PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(PointerReleasedEvent, OnWindowPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void UnhookPointer()
    {
        if (_pointerHost is null) return;
        _pointerHost.RemoveHandler(PointerMovedEvent, OnWindowPointerMoved);
        _pointerHost.RemoveHandler(PointerPressedEvent, OnWindowPointerPressed);
        _pointerHost.RemoveHandler(PointerReleasedEvent, OnWindowPointerReleased);
        _pointerHost = null;
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        _pointerOver = PointerOverCapsule(e);
        if (_acrylic is not null)
        {
            var interactive = _pointerOver && PointerOverSectionButton(e);
            if (_acrylic.IsInteractive != interactive)
                _acrylic.IsInteractive = interactive;
        }

        ApplyInteraction();
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!PointerOverCapsule(e)) return;
        _pointerDown = true;
        NavChrome()?.Classes.Add("down");
        ApplyInteraction();
    }

    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pointerDown && NavChrome()?.Classes.Contains("down") != true) return;
        _pointerDown = false;
        NavChrome()?.Classes.Remove("down");
        ApplyInteraction();
    }

    private bool PointerOverCapsule(PointerEventArgs e)
    {
        var chrome = NavChrome();
        if (chrome is null || chrome.Bounds.Width < 1) return false;
        return new Rect(chrome.Bounds.Size).Contains(e.GetPosition(chrome));
    }

    private bool PointerOverSectionButton(PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        foreach (var button in this.GetVisualDescendants().OfType<Button>())
        {
            if (!button.Classes.Contains("section-nav") || button.Bounds.Width < 1) continue;
            var origin = button.TranslatePoint(default, this);
            if (origin is null) continue;
            if (new Rect(origin.Value, button.Bounds.Size).Contains(point))
                return true;
        }

        return false;
    }

    private void WatchChrome()
    {
        var chrome = NavChrome();
        if (ReferenceEquals(_chrome, chrome)) return;
        ReleaseChrome();
        _chrome = chrome;
        if (_chrome is null) return;
        _chrome.Classes.CollectionChanged += OnChromeClasses;
        _chrome.ActualThemeVariantChanged += OnChromeTheme;
        HoldTransitionsForMotion();
        ApplyInteraction();
    }

    private void OnChromeTheme(object? sender, EventArgs e) => ApplyInteraction();

    private void ReleaseChrome()
    {
        if (_chrome is null) return;
        _chrome.Classes.CollectionChanged -= OnChromeClasses;
        _chrome.ActualThemeVariantChanged -= OnChromeTheme;
        _chrome.Classes.Remove("down");
        _chrome.ClearValue(Border.BoxShadowProperty);
        _chrome.ClearValue(Visual.RenderTransformProperty);
        _chrome.ClearValue(Animatable.TransitionsProperty);
        _chrome = null;
    }

    private void OnMotionChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            var weak = new WeakReference<LiquidGlassBar>(this);
            Dispatcher.UIThread.Post(() =>
            {
                if (weak.TryGetTarget(out var bar)) bar.OnMotionChanged();
            });
            return;
        }

        HoldTransitionsForMotion();
    }

    private void HoldTransitionsForMotion()
    {
        if (_chrome is null) return;
        if (DashboardMotion.ReduceMotion)
        {
            if (_chrome.Transitions is { Count: > 0 })
                _chrome.Transitions = new Transitions();
            return;
        }

        _chrome.ClearValue(Animatable.TransitionsProperty);
    }

    private void OnChromeClasses(object? sender, NotifyCollectionChangedEventArgs e) => ApplyInteraction();

    private void ApplyInteraction()
    {
        if (_chrome is not null)
        {
            _hovered = _pointerOver || _chrome.Classes.Contains(":pointerover");
            _pressed = _pointerDown || _chrome.Classes.Contains("down");
        }

        if (_chrome is null)
        {
            ApplyAcrylicHover();
            return;
        }

        HoldTransitionsForMotion();
        // Clear the chrome drop before any BoxShadow transition can play it back.
        // Fluid draws the only shadow; the chrome only eases its hover scale.
        _chrome.Transitions = new Transitions();
        _chrome.ClearValue(Border.BoxShadowProperty);
        if (!DashboardMotion.ReduceMotion)
        {
            _chrome.Transitions = new Transitions
            {
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(160)
                }
            };
        }

        ApplyAcrylicHover();
        var scale = _pressed ? AcrylicPressScale : _hovered ? AcrylicHoverScale : 1;
        _chrome.RenderTransform = new ScaleTransform(scale, scale);
    }

    private void ApplyAcrylicHover()
    {
        if (_acrylic is null) return;
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        _acrylic.TintColor = CreateAcrylicTint(dark);
        _acrylic.SurfaceColor = Colors.Transparent;
        _acrylic.HighlightOpacity = _pressed || _hovered ? AcrylicHighlightHover : AcrylicHighlightRest;
        var alpha = _pressed ? AcrylicShadowPressAlpha : _hovered ? AcrylicShadowHoverAlpha : AcrylicShadowRestAlpha;
        _acrylic.ShadowColor = Color.FromArgb(alpha, 0, 0, 0);
    }

    private Border? NavChrome()
    {
        for (StyledElement? node = this; node is not null; node = node.Parent)
        {
            if (node is Border border && border.Classes.Contains("nav-float"))
                return border;
        }

        return null;
    }
}

/// <summary>
/// Chrome and clip shell for the nav. Hits fall through to section buttons and to the page behind the capsule.
/// </summary>
public class PassThroughBorder : Border, ICustomHitTest
{
    public bool HitTest(Point point) => false;
}
