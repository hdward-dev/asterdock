using Avalonia;
using Fluid.Avalonia.Acrylic;

namespace AsterDock.Host;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        // Host does not set CompositionOptions or SkiaOptions. This helper is the only writer
        // (region dirty-rect clipping and a GPU resource cap). It replaces earlier options of
        // those types, so any future .With(new CompositionOptions) / .With(new SkiaOptions)
        // must come after it and should keep the dirty-rect clip.
        .UseAcrylicPerformanceDefaults()
        .WithInterFont()
        .LogToTrace();
}
