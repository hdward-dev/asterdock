using System.Diagnostics;
using Avalonia.Threading;

namespace AsterDock.Host;

/// <summary>
/// Last-resort guard for a layout invalidation that still reaches the dispatcher.
/// Only this exception is swallowed. Everything else still crashes the host.
/// </summary>
internal static class LayoutManagerGuard
{
    private static int _installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        Dispatcher.UIThread.UnhandledExceptionFilter += (_, args) =>
        {
            if (IsWrongLayoutManager(args.Exception)) args.RequestCatch = true;
        };
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            if (!IsWrongLayoutManager(args.Exception)) return;
            Trace.TraceError("布局管理器异常已拦截，宿主继续运行: " + args.Exception.Message);
            args.Handled = true;
        };
    }

    public static bool IsWrongLayoutManager(Exception exception)
        => exception is ArgumentException
           && exception.Message.Contains("wrong LayoutManager", StringComparison.Ordinal);
}
