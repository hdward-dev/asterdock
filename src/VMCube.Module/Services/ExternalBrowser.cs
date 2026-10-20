using System.ComponentModel;
using System.Diagnostics;

namespace VMCube.Module.Services;

public sealed record BrowserLaunchResult(bool Requested, string Message);

public static class ExternalBrowser
{
    public static async Task<BrowserLaunchResult> OpenAsync(Uri uri, Func<Uri, Task<bool>> platformLauncher)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http")) throw new ArgumentException("仅支持网页链接。", nameof(uri));
        // Desktop commands must use system libraries, not the host's bundled SPICE/Qt/GLib libraries.
        // On Linux this also avoids an unavailable OpenURI portal silently returning false.
        if (OperatingSystem.IsLinux() && await TryDesktopCommandsAsync(uri)) return Requested();
        try
        {
            if (await platformLauncher(uri).WaitAsync(TimeSpan.FromSeconds(5))) return Requested();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        if (!OperatingSystem.IsLinux())
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                if (process is not null) return Requested();
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        return new(false, "未能打开本机浏览器。请复制下方链接，在浏览器中访问。");
    }
    private static BrowserLaunchResult Requested() => new(true, "已请求在本机浏览器打开。若未显示，请复制下方链接手动访问。");
    private static async Task<bool> TryDesktopCommandsAsync(Uri uri)
    {
        foreach (var command in new[] { "xdg-open", "gio" })
        {
            var info = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true };
            if (command == "gio") info.ArgumentList.Add("open");
            info.ArgumentList.Add(uri.AbsoluteUri);
            foreach (var variable in new[] { "LD_LIBRARY_PATH", "LD_PRELOAD", "QT_PLUGIN_PATH", "QT_QPA_PLATFORM_PLUGIN_PATH", "QML2_IMPORT_PATH", "QML_IMPORT_PATH",
                "GI_TYPELIB_PATH", "GIO_EXTRA_MODULES", "GTK_PATH", "GTK_EXE_PREFIX", "GDK_PIXBUF_MODULE_FILE", "GST_PLUGIN_SYSTEM_PATH_1_0", "GST_PLUGIN_PATH_1_0", "GST_PLUGIN_SCANNER_1_0" })
                info.Environment.Remove(variable);
            try
            {
                using var process = Process.Start(info);
                if (process is null) continue;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try { await process.WaitForExitAsync(deadline.Token); }
                // Some browser launchers remain running for the lifetime of the browser. Do not kill them.
                catch (OperationCanceledException) { return true; }
                if (process.ExitCode == 0) return true;
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        return false;
    }
}
