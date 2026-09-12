using System.Diagnostics;
using AndroidScreen.Module.Models;

namespace AndroidScreen.Module.Services;

public sealed class AndroidBridge(string executable)
{
    public Process Start(params string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("无法启动设备连接服务。");
    }

    public async Task<string> RunAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = Start(arguments);
        using var registration = timeout.Token.Register(() => Kill(process));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var text = await output.ConfigureAwait(false);
        var errors = await error.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(errors) ? text.Trim() : errors.Trim());
        return text.Trim();
    }

    public async Task<IReadOnlyList<AndroidDevice>> DevicesAsync(CancellationToken cancellationToken) =>
        ParseDevices(await RunAsync(cancellationToken, "devices", "-l").ConfigureAwait(false));

    public static IReadOnlyList<AndroidDevice> ParseDevices(string output)
    {
        var devices = new List<AndroidDevice>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] == "List" || parts[0] == "*") continue;
            if (parts[1] is not ("device" or "offline" or "unauthorized")) continue;
            var model = parts.FirstOrDefault(x => x.StartsWith("model:", StringComparison.Ordinal));
            devices.Add(new(parts[0], model?[6..].Replace('_', ' ') ?? parts[0], parts[1]));
        }
        return devices;
    }

    public static void Kill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
