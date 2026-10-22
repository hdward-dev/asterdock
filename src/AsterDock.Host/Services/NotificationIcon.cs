using Avalonia.Platform;

namespace AsterDock.Host.Services;

internal static class NotificationIcon
{
    private static readonly Uri Source = new("avares://AsterDock.Host/Assets/Brand/AsterDock.png");
    private static string? _path;
    private static bool _failed;

    public static string? TryGetPath()
    {
        if (_failed) return null;
        if (_path is not null && File.Exists(_path)) return _path;
        try
        {
            var path = Path.Combine(ApplicationPaths.ProductDataDirectory, "asterdock-notification.png");
            if (!File.Exists(path))
            {
                using var stream = AssetLoader.Open(Source);
                using var file = File.Create(path);
                stream.CopyTo(file);
            }

            _path = path;
            return path;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _failed = true;
            return null;
        }
    }
}
