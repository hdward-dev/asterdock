using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using IoPath = System.IO.Path;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using SkiaSharp;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fluid.Avalonia.Acrylic;

namespace NetworkAccelerator.Module.Controls;

/// <summary>
/// Floating capsule with Fluid <see cref="AcrylicSurface"/>.
/// Hover shows a pointer-following rounded-rect liquid lens (BackdropZoom=1, clear center).
/// One inset selected lens expands on hover and continuously follows the pointer.
/// It samples the composited frost and original icons, with a clear 1:1 center.
/// Host layout includes LensPad* so the outer bulge stays inside positive Bounds.
/// Main acrylic supplies the frosted material, a narrow lip, and a soft inner shadow.
/// </summary>
public class LiquidGlassBar : Decorator, ICustomHitTest
{
    public const double Corner = 20;
    public const double AcrylicBlurRadius = 9;
    public const double AcrylicVibrancy = 1;
    public const double AcrylicBrightness = 0;
    public const double AcrylicRefractionHeight = 18;
    public const double AcrylicRefractionAmount = 20;
    /// <summary>Keep the menu material registered to the page behind it.</summary>
    public const double AcrylicBackdropZoom = 1;
    public const double AcrylicBackdropOffsetX = 0;

    public const double AcrylicHighlightRest = 0.32;
    public const double AcrylicHighlightHover = 0.38;
    public const double AcrylicHighlightWidthRest = 0.6;
    public const double AcrylicHighlightWidthHover = 0.7;
    public const double AcrylicHighlightFalloffRest = 1.4;
    public const double AcrylicHighlightFalloffHover = 1.4;
    public const double AcrylicHighlightBlurRest = 0;
    public const double AcrylicHighlightBlurHover = 0;
    public const double AcrylicHighlightAngle = 135;
    public const double AcrylicRevealWidth = 3.5;
    public const double AcrylicRevealIntensity = 1.0;
    public const byte AcrylicShadowRestAlpha = 0x30;
    public const byte AcrylicShadowHoverAlpha = 0x38;
    public const byte AcrylicShadowPressAlpha = 0x40;

    /// <summary>Horizontal inflate past the bar (sticks out of the vertical nav).</summary>
    public const double LensInflateX = 18;
    /// <summary>Symmetric outer pad companion; keep 0 so left/right overhang match.</summary>
    public const double LensLeftBias = 0;
    /// <summary>Uniform enlargement of the selected frame, including its corners.</summary>
    public const double LensScale = 1.35;
    /// <summary>Vertical space for the enlarged end selection and its elastic motion.</summary>
    public const double LensMaxHeight = 112;
    /// <summary>1 = no optical zoom; center stays 1:1 readable through the lens.</summary>
    public const double LensBackdropZoom = 1.0;
    /// <summary>
    /// Fluid Vibrancy maps to saturation. 1.0 = identity (keep real colors).
    /// 0 desaturates the rim to gray — that was the 灰雾 on the refraction ring.
    /// </summary>
    public const double LensVibrancy = 1.0;
    public const double LensBrightness = 0;
    public const double LensBlurRadius = 0;
    /// <summary>Hover edge band in DIP; the inset selection uses a smaller bend.</summary>
    public const double LensRefractionHeight = 10;
    /// <summary>Maximum inward bend at the edge; the center remains at 1:1.</summary>
    public const double LensRefractionAmount = 14;
    // Fluid 1.4 exposes only full-strength dispersion; keep the hover rim achromatic.
    public const bool LensChromaticAberration = false;
    /// <summary>
    /// Full-strength true-color backdrop (no alpha wash). Gray fog came from
    /// re-painting frosted main-bar samples at partial opacity as a thick doughnut.
    /// </summary>
    public const double LensBackdropOpacity = 1.0;
    /// <summary>Restrained specular lip on the pointer-following lens.</summary>
    public const double LensHighlightOpacity = 0.08;
    public const double LensHighlightWidth = 1.0;
    public const double LensHighlightFalloff = 0.35;
    /// <summary>No blur — blur spread the specular into a milky white frame.</summary>
    public const double LensHighlightBlur = 0;
    /// <summary>Rounded-rect corner (not capsule/ellipse). Moderate vs half-height stadium.</summary>
    public const double LensCornerRadius = 18;
    /// <summary>Equal left layout pad (must match LensPadRight) so the outer lens stays in positive host coords and is centered on the frost.</summary>
    public const double LensPadLeft = LensInflateX + LensLeftBias;
    /// <summary>Equal right layout pad (must match LensPadLeft) so the lens bulges past the frost symmetrically.</summary>
    public const double LensPadRight = LensInflateX;
    /// <summary>Reserve the maximum enlarged lens height inside positive host coordinates.</summary>
    public const double LensPadY = LensMaxHeight * 0.5;
    private const int AcrylicAttempts = 5;

    private static readonly string LogPath = IoPath.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "asterdock",
        "nav-glass.log");

    private Border? _chrome;
    private BulgeHostPanel? _host;
    private AcrylicSurface? _acrylic;
    private Border? _menuShadow;
    private Border? _menuRim;
    private AcrylicSurface? _bulge;
    private Border? _lensSilhouette;
    private Control? _payload;
    private TopLevel? _pointerHost;
    private bool _hovered;
    private bool _pressed;
    private bool _pointerOver;
    private bool _pointerDown;
    private bool _attached;
    private int _attempts;
    private int _retrySerial;
    private Point? _previewLocal;
    private Point? _lensLocal;
    private double _lastLensX = double.NaN;
    private double _lastLensY = double.NaN;
    private Button? _selectedButton;
    private Point? _returnTarget;
    private bool _returnClocked;
    private double _lensExpansion;
    private double _expansionTarget;
    private bool _expansionClocked;
    private Point? _motionPoint;
    private ulong _motionTimestamp;
    private double _pointerVelocity;
    private double _motionStretch;
    private double _motionSwell;

    private void TrackPointerMotion(Point point, ulong timestamp)
    {
        if (_motionPoint is { } previous && timestamp > _motionTimestamp)
        {
            var seconds = (timestamp - _motionTimestamp) / 1000.0;
            if (seconds is >= 0.004 and <= 0.15 && !DashboardMotion.ReduceMotion)
            {
                var velocity = Math.Clamp((point.Y - previous.Y) / seconds, -2400, 2400);
                var acceleration = Math.Abs(velocity - _pointerVelocity) / seconds;
                _motionStretch = Math.Max(_motionStretch, Math.Min(Math.Abs(velocity) / 1800, 1));
                _motionSwell = Math.Max(_motionSwell, Math.Min(acceleration / 35000, 1));
                _pointerVelocity = velocity;
            }
            else
                _pointerVelocity = 0;
        }
        _motionPoint = point;
        _motionTimestamp = timestamp;
    }

    /// <summary>Drive timed pointer samples for repeatable motion and screenshot checks.</summary>
    public void PreviewHoverMotion(Point point, ulong timestamp)
    {
        TrackPointerMotion(point, timestamp);
        PreviewHoverBulge(point);
    }

    private void SetLensExpanded(bool expanded)
    {
        _expansionTarget = expanded ? 1 : 0;
        if (DashboardMotion.ReduceMotion)
        {
            _lensExpansion = _expansionTarget;
            _motionStretch = _motionSwell = 0;
            StopExpansion();
        }
        else if (!_expansionClocked && (Math.Abs(_lensExpansion - _expansionTarget) > 0.001
                 || _motionStretch > 0.001 || _motionSwell > 0.001))
        {
            _expansionClocked = true;
            MotionClock.Subscribe(OnExpansionTick);
        }
    }

    private void OnExpansionTick(TimeSpan elapsed)
    {
        _lensExpansion += (_expansionTarget - _lensExpansion)
            * (1 - Math.Exp(-elapsed.TotalMilliseconds / 55));
        _motionStretch *= Math.Exp(-elapsed.TotalMilliseconds / 100);
        _motionSwell *= Math.Exp(-elapsed.TotalMilliseconds / 75);
        if (Math.Abs(_lensExpansion - _expansionTarget) < 0.001
            && _motionStretch < 0.001 && _motionSwell < 0.001)
        {
            _lensExpansion = _expansionTarget;
            _motionStretch = _motionSwell = 0;
            StopExpansion();
        }
        if (_lensLocal is { } point) PlaceLensAt(point);
    }

    private void StopExpansion()
    {
        if (!_expansionClocked) return;
        _expansionClocked = false;
        MotionClock.Unsubscribe(OnExpansionTick);
    }

    public void SelectSection(string section)
    {
        _selectedButton = SectionNavButtons().FirstOrDefault(button => Equals(button.Tag, section));
        UpdateNavigationHighlight(_lensLocal);
        if (!_pointerOver && _previewLocal is null)
            ReturnToSelection();
    }

    private Point? SelectedCenter()
    {
        _selectedButton = SectionNavButtons().FirstOrDefault(button => button.Classes.Contains("selected"))
            ?? _selectedButton;
        if (_selectedButton is not { Bounds.Width: > 0 } button || _host is null) return null;
        var origin = button.TranslatePoint(default, _host);
        return origin is { } point
            ? new Point(point.X + button.Bounds.Width / 2, point.Y + button.Bounds.Height / 2)
            : null;
    }

    private void ReturnToSelection()
    {
        UpdateNavigationHighlight();
        var target = SelectedCenter();
        if (target is null) return;
        _returnTarget = target;
        if (DashboardMotion.ReduceMotion || _lensLocal is null)
        {
            StopReturn();
            PlaceLensAt(target.Value);
            return;
        }
        if (_returnClocked) return;
        _returnClocked = true;
        MotionClock.Subscribe(OnReturnTick);
    }

    private void OnReturnTick(TimeSpan elapsed)
    {
        if (_returnTarget is not { } target || _lensLocal is not { } current)
        {
            StopReturn();
            return;
        }
        var mix = 1 - Math.Exp(-elapsed.TotalMilliseconds / 65);
        var next = new Point(target.X, current.Y + (target.Y - current.Y) * mix);
        if (Math.Abs(next.Y - target.Y) < 0.15)
        {
            next = target;
            StopReturn();
        }
        PlaceLensAt(next);
    }

    private void StopReturn()
    {
        _returnTarget = null;
        if (!_returnClocked) return;
        _returnClocked = false;
        MotionClock.Unsubscribe(OnReturnTick);
    }

    public static Color CreateAcrylicTint(bool dark) => dark
        ? Color.FromArgb(0x14, 0x2D, 0x2F, 0x38)
        : Color.FromArgb(0x80, 0xFB, 0xFA, 0xFF);

    public bool HitTest(Point point) => _host is not null && new Rect(
        LensPadLeft, LensPadY,
        Math.Max(0, _host.Bounds.Width - LensPadLeft - LensPadRight),
        Math.Max(0, _host.Bounds.Height - LensPadY * 2)).Contains(point);

    public string SnapshotNote { get; private set; } = "pending";

    public bool IsBulgeReady => _bulge is not null && _host is not null;

    public void Refresh()
    {
        InvalidateVisual();
        _acrylic?.InvalidateVisual();
        _bulge?.InvalidateVisual();
    }

    /// <summary>
    /// Headless/screenshot helper: show the continuous outer lens at
    /// <paramref name="localPoint"/> (bar coordinates), or clear when null.
    /// </summary>
    public void PreviewHoverBulge(Point? localPoint)
    {
        _previewLocal = localPoint;
        if (localPoint is null)
        {
            _pointerOver = false;
            ReturnToSelection();
            ApplyInteraction();
            return;
        }

        StopReturn();
        _pointerOver = true;
        ApplyInteraction();
        PlaceLensAt(localPoint.Value);
    }

    /// <summary>Compat alias used by older proof scripts.</summary>
    public void PreviewHoverLens(Point? localPoint) => PreviewHoverBulge(localPoint);

    /// <summary>
    /// Headless helper: place continuous lens centered on section-nav item by index
    /// (still free-form position — not a snapped button pill).
    /// </summary>
    public void PreviewHoverBulgeAt(int index)
    {
        var buttons = SectionNavButtons().ToList();
        if (index < 0 || index >= buttons.Count)
        {
            PreviewHoverBulge(null);
            return;
        }

        var item = buttons[index];
        var origin = item.TranslatePoint(default, _host ?? (Visual)this);
        if (origin is null)
        {
            PreviewHoverBulge(null);
            return;
        }

        var cx = origin.Value.X + item.Bounds.Width * 0.5;
        var cy = origin.Value.Y + item.Bounds.Height * 0.5;
        _previewLocal = new Point(cx, cy);
        _pointerOver = true;
        ApplyInteraction();
        WriteLog($"lens preview index={index} at={cx:0.#},{cy:0.#} ready={IsBulgeReady}");
        PlaceLensAt(new Point(cx, cy));
    }

    public LiquidGlassBar()
    {
        ClipToBounds = false;
        SizeChanged += (_, _) =>
        {
            if (_attached && _acrylic is null)
                TryEnterAcrylic();
            else
            {
                Refresh();
                if (!_pointerOver && _previewLocal is null)
                    ReturnToSelection();
                else if (_lensLocal is { } p)
                    PlaceLensAt(p);
            }
        };
        ActualThemeVariantChanged += (_, _) => ApplyInteraction();
    }

    public override void Render(DrawingContext context)
    {
        // A transparent primitive keeps pointer routing alive between button bounds.
        if (_host is not null)
            context.FillRectangle(Brushes.Transparent, new Rect(
                LensPadLeft, LensPadY,
                Math.Max(0, _host.Bounds.Width - LensPadLeft - LensPadRight),
                Math.Max(0, _host.Bounds.Height - LensPadY * 2)));
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
        Dispatcher.UIThread.Post(() =>
        {
            if (_attached)
            {
                Refresh();
                ReturnToSelection();
            }
        }, DispatcherPriority.Background);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _retrySerial++;
        _pointerOver = false;
        _pointerDown = false;
        _hovered = false;
        _pressed = false;
        _previewLocal = null;
        _lensLocal = null;
        DashboardMotion.Changed -= OnMotionChanged;
        StopReturn();
        StopExpansion();
        _lensExpansion = 0;
        _motionStretch = _motionSwell = _pointerVelocity = 0;
        _motionPoint = null;
        _motionTimestamp = 0;
        UnhookPointer();
        ClearLens();
        ReleaseChrome();
        base.OnDetachedFromVisualTree(e);
    }

    private void TryEnterAcrylic()
    {
        if (!_attached || _acrylic is not null) return;
        try
        {
            EnterAcrylic();
            WriteLog("Acrylic rim5+continuous-lens entered " + SnapshotNote);
        }
        catch (Exception ex)
        {
            var message = ex.GetBaseException().Message;
            SnapshotNote = "acrylic failed: " + message;
            Trace.TraceError("Network accelerator nav acrylic failed: {0}", ex);
            WriteLog(SnapshotNote);
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
            _host = new BulgeHostPanel
            {
                ClipToBounds = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            _menuShadow = new Border
            {
                CornerRadius = new CornerRadius(Corner),
                Background = Brushes.Transparent,
                BoxShadow = CreateMenuShadow(ActualThemeVariant == ThemeVariant.Dark, AcrylicShadowRestAlpha),
                ClipToBounds = false,
                IsHitTestVisible = false,
                ZIndex = -1
            };
            _host.Children.Add(_menuShadow);
            var surface = CreateMainSurface();
            if (ReferenceEquals(Child, _payload))
                Child = null;

            // Frost + rim only (no Content). Icons are a sibling so the continuous lens can
            // capture them in the shared backdrop and refract neighboring edges.
            // Transparent payload does not paint edge pixels, so Highlight rim stays visible.
            surface.Content = null;
            surface.ClearValue(WidthProperty);
            surface.ClearValue(HeightProperty);
            surface.ZIndex = 0;
            _acrylic = surface;
            _host.Children.Add(surface);
            _menuRim = new Border
            {
                CornerRadius = new CornerRadius(Corner),
                BorderThickness = new Thickness(0.45),
                IsHitTestVisible = false,
                ZIndex = 2
            };
            _host.Children.Add(_menuRim);

            if (_payload is not null)
            {
                _payload.ZIndex = 1;
                _host.Children.Add(_payload);
            }

            _lensSilhouette = new Border
            {
                IsVisible = false,
                IsHitTestVisible = false,
                ZIndex = 2,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                BoxShadow = new BoxShadows(),
                ClipToBounds = false
            };
            _host.Children.Add(_lensSilhouette);
            _host.OverlayExtra = _lensSilhouette;

            _bulge = CreateLensSurface();
            _bulge.IsVisible = false;
            _bulge.IsHitTestVisible = false;
            _bulge.ZIndex = 3;
            _host.Bulge = _bulge;
            _host.Children.Add(_bulge);

            Child = _host;
        }

        UnclipAncestors();
        ExcludeAcrylicFromCapture();
        // Frost samples only the page. The lens reads the composited canvas later,
        // including the sharp icons, so they need not appear in the blurred backdrop.
        if (_payload is not null)
            AcrylicBackdrop.SetIsExcludedFromCapture(_payload, true);

        _acrylic.TintColor = CreateAcrylicTint(ActualThemeVariant == ThemeVariant.Dark);
        _acrylic.SurfaceColor = Colors.Transparent;
        _acrylic.BackdropZoom = AcrylicBackdropZoom;
        _acrylic.BackdropOffset = new Vector(AcrylicBackdropOffsetX, 0);
        SnapshotNote =
            $"acrylic-rim5-visible-lens blur {AcrylicBlurRadius:0.#} vibrancy {AcrylicVibrancy:0.00} brightness {AcrylicBrightness:0.00} " +
            $"refraction {AcrylicRefractionHeight:0.#}/{AcrylicRefractionAmount:0.#} zoom {AcrylicBackdropZoom:0.00} offsetX {AcrylicBackdropOffsetX:0.#} " +
            $"lens inflateX {LensInflateX:0.#} biasL {LensLeftBias:0.#} scale {LensScale:0.00} maxH {LensMaxHeight:0.#} zoom {LensBackdropZoom:0.00} " +
            $"refraction {LensRefractionHeight:0.#}/{LensRefractionAmount:0.#} " +
            $"highlight op/{AcrylicHighlightRest:0.00} w/{AcrylicHighlightWidthRest:0.##} fall/{AcrylicHighlightFalloffRest:0.00} " +
            $"tint {_acrylic.TintColor}";
        ApplyInteraction();
        InvalidateVisual();
        Refresh();
        MaybeStartLensDemo();
    }

    private void MaybeStartLensDemo()
    {
        var mode = Environment.GetEnvironmentVariable("ASTERDOCK_LENS_DEMO");
        if (string.IsNullOrWhiteSpace(mode)) return;
        var weak = new WeakReference<LiquidGlassBar>(this);
        Dispatcher.UIThread.Post(async () =>
        {
            if (!weak.TryGetTarget(out var bar) || !bar._attached) return;
            await Task.Delay(900);
            if (!weak.TryGetTarget(out bar) || !bar._attached || bar._host is null) return;
            var h = Math.Max(200, bar._host.Bounds.Height);
            var w = Math.Max(40, bar._host.Bounds.Width);
            var cx = w * 0.5;
            const int frames = 20;
            for (var f = 0; f < frames && bar._attached; f++)
            {
                var tt = f / (double)(frames - 1);
                var cy = 24 + tt * (h - 48);
                bar._previewLocal = new Point(cx, cy);
                bar._pointerOver = true;
                bar.ApplyInteraction();
                bar.PlaceLensAt(new Point(cx, cy));
                await Task.Delay(140);
            }
        }, DispatcherPriority.Background);
    }

    private void RememberPayload()
    {
        if (_payload is not null) return;
        if (Child is AcrylicSurface or Panel) return;
        _payload = Child;
    }

    private AcrylicSurface CreateMainSurface()
    {
        var surface = new AcrylicSurface
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
            BackdropZoom = AcrylicBackdropZoom,
            BackdropOffset = new Vector(AcrylicBackdropOffsetX, 0),
            DepthEffect = false,
            ChromaticAberration = false,
            HighlightEnabled = true,
            HighlightOpacity = AcrylicHighlightRest,
            HighlightWidth = AcrylicHighlightWidthRest,
            HighlightFalloff = AcrylicHighlightFalloffRest,
            HighlightBlurRadius = AcrylicHighlightBlurRest,
            HighlightAngle = AcrylicHighlightAngle,
            InnerShadowEnabled = true,
            InnerShadowRadius = 3,
            InnerShadowOffset = new Vector(0, 1),
            InnerShadowOpacity = 0.06,
            InnerShadowColor = Colors.Black,
            // Cast elevation once, from a sibling outside the material's capture.
            ShadowEnabled = false,
            ShadowRadius = 0,
            ShadowOffset = new Vector(0, 0),
            ShadowOpacity = 0,
            RevealBorderEnabled = false,
            RevealBorderWidth = AcrylicRevealWidth,
            RevealBorderIntensity = AcrylicRevealIntensity,
            RevealBorderColor = Colors.White,
            RevealBorderRadius = 220,
            TintColor = Colors.Transparent,
            SurfaceColor = Colors.Transparent,
            ProgressiveBlurEnabled = false,
            AdaptiveLuminanceEnabled = false,
            BackdropOpacity = 1,
            IsHitTestVisible = false,
            ClipToBounds = false
        };
        AcrylicBackdrop.SetIsExcludedFromCapture(surface, true);
        return surface;
    }

    private AcrylicSurface CreateLensSurface()
    {
        var surface = new SameMaterialLensSurface
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            BlurRadius = 0,
            Vibrancy = LensVibrancy,
            Brightness = 0,
            Contrast = 1,
            ExposureEv = 0,
            GammaPower = 1,
            RefractionHeight = LensRefractionHeight,
            RefractionAmount = LensRefractionAmount,
            BackdropZoom = LensBackdropZoom,
            BackdropOffset = new Vector(0, 0),
            DepthEffect = false,
            ChromaticAberration = LensChromaticAberration,
            HighlightEnabled = false,
            // The composited shader draws the single specular lip.
            HighlightOpacity = LensHighlightOpacity,
            HighlightWidth = LensHighlightWidth,
            HighlightFalloff = LensHighlightFalloff,
            HighlightBlurRadius = LensHighlightBlur,
            HighlightAngle = AcrylicHighlightAngle,
            InnerShadowEnabled = false,
            InnerShadowRadius = 0,
            InnerShadowOffset = new Vector(0, 0),
            InnerShadowOpacity = 0,
            InnerShadowColor = Colors.Transparent,
            // Elevation belongs to the menu bar; keep the lens clear.
            ShadowEnabled = false,
            ShadowRadius = 0,
            ShadowOffset = new Vector(0, 0),
            ShadowOpacity = 0,
            ShadowColor = Colors.Transparent,
            RevealBorderEnabled = false,
            RevealBorderWidth = 5.0,
            RevealBorderIntensity = AcrylicRevealIntensity,
            RevealBorderColor = Colors.White,
            RevealBorderRadius = 220,
            // Sample the existing material without another frost pass.
            TintColor = Color.FromArgb(0, 0, 0, 0),
            SurfaceColor = Color.FromArgb(0, 0, 0, 0),
            ProgressiveBlurEnabled = false,
            AdaptiveLuminanceEnabled = false,
            BackdropOpacity = LensBackdropOpacity,
            IsHitTestVisible = false,
            ClipToBounds = true,
            ZIndex = 2
        };
        AcrylicBackdrop.SetIsExcludedFromCapture(surface, true);
        surface.ClearValue(Visual.OpacityMaskProperty);
        surface.ClearValue(Visual.ClipProperty);
        return surface;
    }

    private static BoxShadows CreateMenuShadow(bool dark, byte alpha) => new(
        new BoxShadow
        {
            OffsetY = 10,
            Blur = 24,
            Spread = -2,
            Color = Color.FromArgb(dark ? (byte)(alpha * 2) : alpha, 18, 20, 28)
        },
        [
            new BoxShadow
            {
                OffsetY = 2,
                Blur = 6,
                Color = Color.FromArgb((byte)(alpha * (dark ? 1.3 : 0.65)), 18, 20, 28)
            }
        ]);

    private void ExcludeAcrylicFromCapture()
    {
        // Same-material liquid bulge: INCLUDE main frost so the rim is rim5 family glass
        // (not a page-pastel "selection frame"). Blur=0/Vibrancy=1/Tint=0 keep it from
        // re-frosting into the old milky gray doughnut; icons stay capturable for edge cut.
        if (_acrylic is not null)
            AcrylicBackdrop.SetIsExcludedFromCapture(_acrylic, false);
        if (_bulge is not null)
            AcrylicBackdrop.SetIsExcludedFromCapture(_bulge, true);
        if (_lensSilhouette is not null)
            AcrylicBackdrop.SetIsExcludedFromCapture(_lensSilhouette, true);
        if (_menuRim is not null)
            AcrylicBackdrop.SetIsExcludedFromCapture(_menuRim, true);
        if (_menuShadow is not null)
            AcrylicBackdrop.SetIsExcludedFromCapture(_menuShadow, true);
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
        var point = e.GetPosition(_host ?? (Visual)this);
        if (HitTest(point)) TrackPointerMotion(point, e.Timestamp);
        else
        {
            _motionPoint = null;
            _pointerVelocity = 0;
        }
        FollowPointer(point);
    }

    private void FollowPointer(Point local)
    {
        if (_previewLocal is not null) return;
        var over = _host is not null && new Rect(
            LensPadLeft, LensPadY,
            Math.Max(0, _host.Bounds.Width - LensPadLeft - LensPadRight),
            Math.Max(0, _host.Bounds.Height - LensPadY * 2)).Contains(local);
        var wasOver = _pointerOver;
        if (over != _pointerOver)
        {
            _pointerOver = over;
            ApplyInteraction();
        }

        if (over)
        {
            StopReturn();
            PlaceLensAt(local);
        }
        else if (wasOver)
            ReturnToSelection();
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
        // Only the inset frost/content rect arms hover — layout pads exist so the outer lens
        // can paint, but must not steal pointer-over from page cards under the left pad.
        if (_host is not null && _host.Bounds.Width > 1)
        {
            var p = e.GetPosition(_host);
            var content = new Rect(
                LensPadLeft,
                LensPadY,
                Math.Max(1, _host.Bounds.Width - LensPadLeft - LensPadRight),
                Math.Max(1, _host.Bounds.Height - LensPadY * 2));
            return content.Contains(p);
        }

        var chrome = NavChrome();
        if (chrome is null || chrome.Bounds.Width < 1) return false;
        return new Rect(chrome.Bounds.Size).Contains(e.GetPosition(chrome));
    }

    private IEnumerable<Button> SectionNavButtons()
    {
        if (_payload is null) yield break;
        foreach (var button in _payload.GetVisualDescendants().OfType<Button>())
        {
            if (button.Classes.Contains("section-nav"))
                yield return button;
        }
    }

    private void UpdateNavigationHighlight(Point? lensCenter = null)
    {
        var buttons = SectionNavButtons().ToList();
        var active = buttons.FirstOrDefault(button => button.Classes.Contains("selected")) ?? _selectedButton;
        if ((_pointerOver || _previewLocal is not null) && lensCenter is { } center && _host is not null)
        {
            // The lens also crosses gaps and side padding outside button hit targets.
            active = buttons.MinBy(button =>
            {
                var origin = button.TranslatePoint(default, _host);
                return origin is { } point
                    ? Math.Abs(point.Y + button.Bounds.Height / 2 - center.Y)
                    : double.PositiveInfinity;
            });
        }
        foreach (var button in buttons)
            button.Classes.Set("lens-active", ReferenceEquals(button, active));
    }

    /// <summary>The shader returns the original center pixels and smoothly bends only its edge band.</summary>
    private static void ApplyLensClip(AcrylicSurface lens, double width, double height)
    {
        lens.ClearValue(Visual.OpacityMaskProperty);
        if (width < 8 || height < 8)
        {
            lens.ClearValue(Visual.ClipProperty);
            return;
        }

        var corner = Math.Min(lens.CornerRadius.TopLeft, Math.Min(width, height) * 0.5);
        lens.Clip = CreateRoundedRectGeometry(new Rect(0, 0, width, height), corner);
    }

    /// <summary>Rounded-rect path for Visual.Clip (Avalonia has no RoundedRectangleGeometry).</summary>
    private static StreamGeometry CreateRoundedRectGeometry(Rect rect, double radius)
    {
        radius = Math.Max(0, Math.Min(radius, Math.Min(rect.Width, rect.Height) * 0.5));
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        if (radius < 0.5)
        {
            ctx.BeginFigure(new Point(rect.X, rect.Y), isFilled: true);
            ctx.LineTo(new Point(rect.Right, rect.Y));
            ctx.LineTo(new Point(rect.Right, rect.Bottom));
            ctx.LineTo(new Point(rect.X, rect.Bottom));
            ctx.EndFigure(true);
            return geometry;
        }

        var r = radius;
        var arc = new Size(r, r);
        ctx.BeginFigure(new Point(rect.X + r, rect.Y), isFilled: true);
        ctx.LineTo(new Point(rect.Right - r, rect.Y));
        ctx.ArcTo(new Point(rect.Right, rect.Y + r), arc, 0, false, SweepDirection.Clockwise);
        ctx.LineTo(new Point(rect.Right, rect.Bottom - r));
        ctx.ArcTo(new Point(rect.Right - r, rect.Bottom), arc, 0, false, SweepDirection.Clockwise);
        ctx.LineTo(new Point(rect.X + r, rect.Bottom));
        ctx.ArcTo(new Point(rect.X, rect.Bottom - r), arc, 0, false, SweepDirection.Clockwise);
        ctx.LineTo(new Point(rect.X, rect.Y + r));
        ctx.ArcTo(new Point(rect.X + r, rect.Y), arc, 0, false, SweepDirection.Clockwise);
        ctx.EndFigure(true);
        return geometry;
    }

    /// <summary>
    /// Continuous rounded-rect lens centered on the pointer (bar coords).
    /// Uniformly enlarges the selected frame and follows the pointer without snapping.
    /// No optical zoom (BackdropZoom=1); edge refraction only — no cloned icons.
    /// </summary>
    private void PlaceLensAt(Point localPoint)
    {
        if (_bulge is null || _host is null) return;

        _lensLocal = localPoint;

        // Host Bounds include the overhang; the selected frame is the unscaled shape.
        var hostW = _host.Bounds.Width > 1 ? _host.Bounds.Width : Bounds.Width;
        var hostH = _host.Bounds.Height > 1 ? _host.Bounds.Height : Bounds.Height;
        if (hostW < 1) hostW = 80 + LensPadLeft + LensPadRight;
        if (hostH < 1) hostH = 400 + LensPadY * 2;

        SetLensExpanded(_pointerOver || _previewLocal is not null);
        var restWidth = Math.Max(1, hostW - LensPadLeft - LensPadRight - 8);
        var firstButton = SectionNavButtons().FirstOrDefault();
        var restHeight = firstButton?.Bounds.Height ?? 68;
        // Give every selection the cap-aligned height already used at the ends.
        if (firstButton?.TranslatePoint(default, _host) is { } firstOrigin)
            restHeight = 2 * (firstOrigin.Y + restHeight / 2 - LensPadY - 4);
        restHeight = Math.Clamp(restHeight, 1, Math.Max(1, hostH - LensPadY * 2 - 8));
        var scale = 1 + (LensScale - 1 + 0.05 * _motionStretch + 0.03 * _motionSwell) * _lensExpansion;
        scale = Math.Min(scale, Math.Min(hostW / restWidth, LensMaxHeight / restHeight));
        var width = restWidth * scale;
        var height = restHeight * scale;
        var restRadius = Math.Min(LensCornerRadius, Math.Min(restWidth, restHeight) * 0.5);
        var radius = restRadius * scale;

        // Arrange the same lens inside the frost at rest; only interaction consumes the pads.
        var x = (hostW - width) * 0.5;
        var minY = (LensPadY + 4) * (1 - _lensExpansion);
        var maxY = Math.Max(minY, hostH - minY - height);
        var y = Math.Clamp(localPoint.Y - height * 0.5, minY, maxY);
        UpdateNavigationHighlight(new Point(hostW * 0.5, y + height * 0.5));

        var moved =
            double.IsNaN(_lastLensX)
            || Math.Abs(_lastLensX - x) > 0.25
            || Math.Abs(_lastLensY - y) > 0.25
            || Math.Abs(_bulge.Width - width) > 0.5
            || Math.Abs(_bulge.Height - height) > 0.5;

        _bulge.Width = width;
        _bulge.Height = height;
        _bulge.MinWidth = width;
        _bulge.MinHeight = height;
        _bulge.CornerRadius = new CornerRadius(radius);
        ApplyLensClip(_bulge, _bulge.Width, _bulge.Height);
        UnclipAncestors();

        _host.LensLeft = x;
        _host.LensTop = y;
        if (_lensSilhouette is not null)
        {
            _lensSilhouette.IsVisible = false;
            _lensSilhouette.Opacity = 0;
            _lensSilhouette.Width = width;
            _lensSilhouette.Height = height;
            _lensSilhouette.MinWidth = width;
            _lensSilhouette.MinHeight = height;
            _lensSilhouette.CornerRadius = new CornerRadius(radius);
            _lensSilhouette.Background = Brushes.Transparent;
            _lensSilhouette.BorderBrush = Brushes.Transparent;
        }

        _bulge.ClearValue(MarginProperty);
        _bulge.ClearValue(Visual.RenderTransformProperty);
        _bulge.HorizontalAlignment = HorizontalAlignment.Left;
        _bulge.VerticalAlignment = VerticalAlignment.Top;
        _bulge.BackdropZoom = LensBackdropZoom;
        _bulge.BackdropOffset = new Vector(0, 0);
        _bulge.Content = null;
        _bulge.Tag = null;
        _bulge.IsVisible = true;
        _bulge.Opacity = 1;
        _bulge.ChromaticAberration = LensChromaticAberration;
        _bulge.DepthEffect = false;
        _bulge.RefractionHeight = 3 + (LensRefractionHeight - 3) * _lensExpansion;
        _bulge.RefractionAmount = 2 + (LensRefractionAmount - 2) * _lensExpansion;
        if (_bulge is SameMaterialLensSurface material)
            material.InteractionProgress = _lensExpansion;
        _bulge.TintColor = Color.FromArgb(0, 0, 0, 0);
        _bulge.SurfaceColor = Color.FromArgb(0, 0, 0, 0);
        _bulge.BlurRadius = 0;
        _bulge.Vibrancy = LensVibrancy; // 1.0 identity — never 0 (desaturates rim to gray fog)
        _bulge.Brightness = 0;
        _bulge.Contrast = 1;
        _bulge.ExposureEv = 0;
        _bulge.ShadowEnabled = false;
        _bulge.InnerShadowEnabled = false;
        // Keep the lens lip directional and restrained.
        _bulge.HighlightOpacity = LensHighlightOpacity;
        _bulge.HighlightWidth = LensHighlightWidth;
        _bulge.HighlightFalloff = LensHighlightFalloff;
        _bulge.HighlightBlurRadius = LensHighlightBlur;
        _bulge.BackdropOpacity = LensBackdropOpacity;
        ApplyLensClip(_bulge, _bulge.Width, _bulge.Height);

        _host.InvalidateArrange();
        _host.UpdateLayout();
        _host.InvalidateVisual();
        _bulge.InvalidateVisual();
        _lensSilhouette?.InvalidateVisual();

        if (moved)
        {
            _lastLensX = x;
            _lastLensY = y;
            _bulge.InvalidateVisual();
            WriteLog($"lens place at={localPoint.X:0.#},{localPoint.Y:0.#} box={x:0.#},{y:0.#} size={width:0.#}x{height:0.#} bounds={_bulge.Bounds.Width:0.#}x{_bulge.Bounds.Height:0.#} r={radius:0.#} zoom={LensBackdropZoom:0.00} padL={LensPadLeft:0.#} padR={LensPadRight:0.#} vib={LensVibrancy:0.00} tintA=0 blur=0 expansion={_lensExpansion:0.###} rimH={_bulge.RefractionHeight:0.#} amount={_bulge.RefractionAmount:0.#} ca={LensChromaticAberration} capture=composited-frost highlight={LensHighlightOpacity:0.##}/{LensHighlightWidth:0.#}");
        }
    }

    private void ClearLens()
    {
        _lensLocal = null;
        _lastLensX = double.NaN;
        _lastLensY = double.NaN;
        if (_lensSilhouette is not null)
        {
            _lensSilhouette.IsVisible = false;
            _lensSilhouette.Opacity = 0;
            _lensSilhouette.ClearValue(Visual.RenderTransformProperty);
        }
        if (_host is not null)
        {
            _host.LensTop = 0;
            _host.InvalidateArrange();
        }
        if (_bulge is null) return;
        _bulge.IsVisible = false;
        _bulge.Content = null;
        _bulge.Tag = null;
        _bulge.Opacity = 0;
        _bulge.ClearValue(Visual.RenderTransformProperty);
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
        _chrome.ClipToBounds = false;
        // No whole-capsule RenderTransform — outer continuous lens is the hover effect.
        _chrome.ClearValue(Visual.RenderTransformProperty);
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
        _chrome = null;
        ClearValue(Visual.RenderTransformProperty);
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

        ApplyInteraction();
        if (DashboardMotion.ReduceMotion)
        {
            if (!_pointerOver && _previewLocal is null) ReturnToSelection();
            else if (_lensLocal is { } point) PlaceLensAt(point);
        }
    }

    private void OnChromeClasses(object? sender, NotifyCollectionChangedEventArgs e) => ApplyInteraction();

    private void ApplyInteraction()
    {
        if (_chrome is not null)
        {
            _hovered = _pointerOver || _chrome.Classes.Contains(":pointerover");
            _pressed = _pointerDown || _chrome.Classes.Contains("down");
        }
        else
        {
            _hovered = _pointerOver;
            _pressed = _pointerDown;
        }

        ApplyAcrylicHover();
        if (_chrome is not null)
        {
            _chrome.ClearValue(Border.BoxShadowProperty);
            _chrome.ClearValue(Visual.RenderTransformProperty);
            _chrome.ClipToBounds = false;
        }

        ClearValue(Visual.RenderTransformProperty);
        ClipToBounds = false;
    }

    private void ApplyAcrylicHover()
    {
        if (_acrylic is null) return;
        _acrylic.TintColor = CreateAcrylicTint(ActualThemeVariant == ThemeVariant.Dark);
        _acrylic.SurfaceColor = Colors.Transparent;
        _acrylic.BackdropZoom = AcrylicBackdropZoom;
        _acrylic.BackdropOffset = new Vector(AcrylicBackdropOffsetX, 0);
        var hot = _pressed || _hovered;
        if (_menuRim is not null)
        {
            var dark = ActualThemeVariant == ThemeVariant.Dark;
            _menuRim.BorderBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(dark ? (byte)0x75 : (byte)0xE8, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(dark ? (byte)0x38 : (byte)0xA0, 255, 255, 255), 0.35),
                    new GradientStop(Color.FromArgb(dark ? (byte)0x20 : (byte)0x48, 255, 255, 255), 0.7),
                    new GradientStop(dark
                        ? Color.FromArgb(0x54, 255, 255, 255)
                        : Color.FromArgb(0x30, 88, 90, 102), 1)
                }
            };
        }
        _acrylic.HighlightEnabled = true;
        _acrylic.HighlightOpacity = hot ? AcrylicHighlightHover : AcrylicHighlightRest;
        _acrylic.HighlightWidth = hot ? AcrylicHighlightWidthHover : AcrylicHighlightWidthRest;
        _acrylic.HighlightFalloff = hot ? AcrylicHighlightFalloffHover : AcrylicHighlightFalloffRest;
        _acrylic.HighlightBlurRadius = hot ? AcrylicHighlightBlurHover : AcrylicHighlightBlurRest;
        _acrylic.HighlightAngle = AcrylicHighlightAngle;
        _acrylic.RevealBorderEnabled = false;
        _acrylic.DepthEffect = false;
        var alpha = _pressed ? AcrylicShadowPressAlpha : _hovered ? AcrylicShadowHoverAlpha : AcrylicShadowRestAlpha;
        if (_menuShadow is not null)
            _menuShadow.BoxShadow = CreateMenuShadow(ActualThemeVariant == ThemeVariant.Dark, alpha);
        _acrylic.InnerShadowEnabled = true;
        _acrylic.InnerShadowOpacity = 0.06;

        if (_bulge is not null && _bulge.IsVisible)
        {
            // Keep the same material and a 1:1 center through the expansion.
            _bulge.TintColor = Color.FromArgb(0, 0, 0, 0);
            _bulge.SurfaceColor = Color.FromArgb(0, 0, 0, 0);
            _bulge.BlurRadius = 0;
            _bulge.Vibrancy = LensVibrancy;
            _bulge.Brightness = 0;
            _bulge.Contrast = 1;
            _bulge.ExposureEv = 0;
            _bulge.ShadowEnabled = false;
            _bulge.BackdropZoom = LensBackdropZoom;
            _bulge.BackdropOpacity = LensBackdropOpacity;
            _bulge.HighlightOpacity = LensHighlightOpacity;
            _bulge.HighlightWidth = LensHighlightWidth;
            _bulge.HighlightFalloff = LensHighlightFalloff;
            _bulge.HighlightBlurRadius = LensHighlightBlur;
            _bulge.InnerShadowEnabled = false;
            ApplyLensClip(_bulge, _bulge.Width, _bulge.Height);
            _bulge.ChromaticAberration = LensChromaticAberration;
            _bulge.RefractionHeight = 3 + (LensRefractionHeight - 3) * _lensExpansion;
            _bulge.RefractionAmount = 2 + (LensRefractionAmount - 2) * _lensExpansion;
            _bulge.DepthEffect = false;
            _bulge.RevealBorderEnabled = false;
        }
    }


    private void UnclipAncestors()
    {
        for (Visual? v = this; v is not null; v = v.GetVisualParent())
        {
            if (v is Control c)
                c.ClipToBounds = false;
        }
        if (_chrome is not null)
            _chrome.ClipToBounds = false;
        if (_host is not null)
            _host.ClipToBounds = false;
        ClipToBounds = false;
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

    private static void WriteLog(string note)
    {
        try
        {
            var dir = IoPath.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(
                LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} path=AcrylicRim5VisibleLensLiquidSameMat renderer=fluid note={note}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break the nav.
        }
    }
}

/// <summary>
/// Fluid's shared backdrop excludes every acrylic subscriber, including the main frost.
/// Read the pixels already drawn below this sibling instead, so refraction includes both
/// the main AcrylicSurface and the original icons without a second frost/color pass.
/// </summary>
internal sealed class SameMaterialLensSurface : AcrylicSurface
{
    public double InteractionProgress { get; set; }

    // Optics adapted from Kyant0/AndroidLiquidGlass, Copyright 2025 Kyant, Apache-2.0.
    // Modified for Skia snapshots, a clear center, and progress-driven Avalonia geometry.
    // See THIRD-PARTY-NOTICES.md and AndroidLiquidGlass.LICENSE.txt.
    private static readonly Lazy<SKRuntimeEffect> LensEffect = new(() =>
        SKRuntimeEffect.CreateShader("""
            uniform shader content;
            uniform float2 size;
            uniform float radius;
            uniform float band;
            uniform float amount;
            uniform float progress;
            uniform float dark;
            uniform float lipOpacity;
            uniform float lipWidth;

            half4 main(float2 coord) {
                float2 p = coord - size * 0.5;
                float2 q = abs(p) - (size * 0.5 - radius);
                float2 outside = max(q, 0.0);
                float sd = length(outside) + min(max(q.x, q.y), 0.0) - radius;
                float depth = max(-sd, 0.0);
                float gradRadius = min(radius * 1.5, min(size.x, size.y) * 0.5);
                float2 gradCoord = abs(p) - (size * 0.5 - gradRadius);
                float2 gradOutside = max(gradCoord, 0.0);
                float2 normal;
                if (length(gradOutside) > 0.0001) {
                    normal = sign(p) * normalize(gradOutside);
                } else {
                    float axis = step(gradCoord.y, gradCoord.x);
                    normal = sign(p) * float2(axis, 1.0 - axis);
                }
                float t = clamp(1.0 - depth / max(band, 0.001), 0.0, 1.0);
                float bend = (1.0 - sqrt(max(0.0, 1.0 - t * t))) * amount;
                half4 color = content.eval(coord - normal * bend);
                float edge = 1.0 - smoothstep(0.0, 4.0, depth);
                float restShade = mix(0.075, 0.025, dark);
                float shade = mix(restShade, 0.008, progress) + edge * 0.035;
                color.rgb = mix(color.rgb, half3(dark * color.a), shade);
                float lip = 1.0 - smoothstep(0.0, max(lipWidth, 0.001), depth);
                float light = pow(max(dot(normal, float2(-0.7071, -0.7071)), 0.0), 2.0);
                color.rgb = mix(color.rgb, half3(color.a), lip * light * lipOpacity);
                return color;
            }
            """, out var error) ?? throw new InvalidOperationException(error));

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return;
        context.Custom(new LensDrawOperation(
            Bounds.Size, CornerRadius.TopLeft, RefractionHeight, RefractionAmount,
            InteractionProgress, ActualThemeVariant == ThemeVariant.Dark, HighlightOpacity, HighlightWidth));
    }

    private sealed class LensDrawOperation(
        Size size, double radius, double band, double amount, double progress, bool dark,
        double lipOpacity, double lipWidth) : ICustomDrawOperation
    {
        public Rect Bounds => new(size);
        public bool HitTest(Point point) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null) return;
            using var lease = feature.Lease();
            if (lease.SkSurface is not { } surface)
            {
                System.Diagnostics.Trace.WriteLine("nav-glass: GPU lease has no readable SkSurface");
                return;
            }
            var canvas = lease.SkCanvas;
            var transform = canvas.TotalMatrix;
            if (!transform.TryInvert(out var inverse)) return;

            var device = transform.MapRect(SKRect.Create((float)size.Width, (float)size.Height));
            var margin = (float)(Math.Abs(amount) * Math.Max(
                Math.Abs(transform.ScaleX), Math.Abs(transform.ScaleY)) + 2);
            device.Inflate(margin, margin);
            var clip = canvas.DeviceClipBounds;
            clip.Inflate((int)Math.Ceiling(margin), (int)Math.Ceiling(margin));
            var capture = SKRectI.Intersect(SKRectI.Ceiling(device), clip);
            if (capture.Width < 1 || capture.Height < 1) return;
            using var snapshot = surface.Snapshot(capture);
            if (snapshot is null) return;

            inverse.TransX += inverse.ScaleX * capture.Left + inverse.SkewX * capture.Top;
            inverse.TransY += inverse.SkewY * capture.Left + inverse.ScaleY * capture.Top;
            using var backdrop = SKShader.CreateImage(snapshot,
                SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, inverse);
            var effect = LensEffect.Value;
            using var uniforms = new SKRuntimeEffectUniforms(effect);
            uniforms["size"] = new[] { (float)size.Width, (float)size.Height };
            uniforms["radius"] = (float)radius;
            uniforms["band"] = (float)band;
            uniforms["amount"] = (float)amount;
            uniforms["progress"] = (float)progress;
            uniforms["dark"] = dark ? 1f : 0f;
            uniforms["lipOpacity"] = (float)lipOpacity;
            uniforms["lipWidth"] = (float)lipWidth;
            using var children = new SKRuntimeEffectChildren(effect);
            children["content"] = backdrop;
            using var shader = effect.ToShader(uniforms, children);
            using var paint = new SKPaint { Shader = shader, IsAntialias = true };
            canvas.DrawRoundRect(SKRect.Create((float)size.Width, (float)size.Height),
                (float)radius, (float)radius, paint);
        }
    }
}

/// <summary>
/// Host panel: measured size includes LensPad* so the outer continuous lens stays inside Bounds
/// (positive arrange coords). Main frost + icons are inset; lens/silhouette are absolute overlays.
/// </summary>
internal sealed class BulgeHostPanel : Panel
{
    public AcrylicSurface? Bulge { get; set; }
    public Control? OverlayExtra { get; set; }
    public double LensLeft { get; set; }
    public double LensTop { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0.0;
        var height = 0.0;
        foreach (var child in Children)
        {
            if (ReferenceEquals(child, Bulge) || ReferenceEquals(child, OverlayExtra))
            {
                child.Measure(availableSize);
                continue;
            }

            child.Measure(new Size(
                Math.Max(0, availableSize.Width - LiquidGlassBar.LensPadLeft - LiquidGlassBar.LensPadRight),
                Math.Max(0, availableSize.Height - LiquidGlassBar.LensPadY * 2)));
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        // Expand layout so outer capsule overhang is inside host Bounds (not clipped).
        return new Size(
            width + LiquidGlassBar.LensPadLeft + LiquidGlassBar.LensPadRight,
            height + LiquidGlassBar.LensPadY * 2);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var padL = LiquidGlassBar.LensPadLeft;
        var padR = LiquidGlassBar.LensPadRight;
        var padY = LiquidGlassBar.LensPadY;
        var contentW = Math.Max(1, finalSize.Width - padL - padR);
        var contentH = Math.Max(1, finalSize.Height - padY * 2);
        var contentRect = new Rect(padL, padY, contentW, contentH);

        foreach (var child in Children)
        {
            if (ReferenceEquals(child, Bulge) || ReferenceEquals(child, OverlayExtra))
            {
                // AcrylicSurface/Border often measure to 0 with null Content — Width/Height props
                // alone are not enough; force Measure+Arrange to the lens slot or Bounds stay 0x0.
                var w = child.Width;
                var h = child.Height;
                if (double.IsNaN(w) || w < 1) w = finalSize.Width;
                if (double.IsNaN(h) || h < 1) h = LiquidGlassBar.LensMaxHeight;
                if (w < 1) w = finalSize.Width;
                if (h < 1) h = LiquidGlassBar.LensMaxHeight;
                child.Width = w;
                child.Height = h;
                child.MinWidth = w;
                child.MinHeight = h;
                var top = LensTop;
                if (top + h > finalSize.Height) top = Math.Max(0, finalSize.Height - h);
                if (top < 0) top = 0;
                child.Measure(new Size(w, h));
                child.Arrange(new Rect(LensLeft, top, w, h));
            }
            else
            {
                child.Arrange(contentRect);
            }
        }

        return finalSize;
    }
}

/// <summary>
/// Chrome and clip shell for the nav. Hits fall through to section buttons and to the page behind the capsule.
/// </summary>
public class PassThroughBorder : Border, ICustomHitTest
{
    public bool HitTest(Point point) => Child is LiquidGlassBar glass
        ? glass.HitTest(point)
        : new Rect(Bounds.Size).Contains(point);
}
