using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AsterDock.Host;

internal static class TrayIconFactory
{
    private static readonly Uri ApplicationIconUri =
        new("avares://AsterDock.Host/Assets/Brand/AsterDock.png");

    public static Bitmap CreateApplicationIcon()
    {
        using var stream = AssetLoader.Open(ApplicationIconUri);
        return new Bitmap(stream);
    }
}
