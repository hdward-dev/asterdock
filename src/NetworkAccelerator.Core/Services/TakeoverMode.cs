namespace NetworkAccelerator.Core.Services;

public static class TakeoverMode
{
    public static bool UsesSystemProxy(bool systemProxyEnabled, bool tunEnabled) =>
        systemProxyEnabled && !tunEnabled;

    public static (bool SystemProxyEnabled, bool TunEnabled) ApplySystemProxy(
        bool systemProxyEnabled,
        bool tunEnabled,
        bool enableSystemProxy) =>
        enableSystemProxy ? (true, false) : (false, tunEnabled);

    public static (bool SystemProxyEnabled, bool TunEnabled) ApplyTun(
        bool systemProxyEnabled,
        bool tunEnabled,
        bool enableTun) =>
        enableTun ? (false, true) : (systemProxyEnabled, false);
}
