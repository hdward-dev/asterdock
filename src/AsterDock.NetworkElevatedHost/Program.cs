using System.Diagnostics;
using System.Text;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    var options = ParseArguments(args);
    if (!options.TryGetValue("core", out var corePath) ||
        !options.TryGetValue("config", out var configPath) ||
        !options.TryGetValue("stop-signal", out var stopSignalPath) ||
        !options.TryGetValue("ready-signal", out var readySignalPath) ||
        !options.TryGetValue("log", out var logPath) ||
        !options.TryGetValue("parent-pid", out var parentPidText) ||
        !int.TryParse(parentPidText, out var parentPid))
        return 2;

    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
    TryDelete(stopSignalPath);
    TryDelete(readySignalPath);

    var logSync = new object();
    using var writer = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo(corePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(corePath)!
        },
        EnableRaisingEvents = true
    };
    process.StartInfo.ArgumentList.Add("run");
    process.StartInfo.ArgumentList.Add("-c");
    process.StartInfo.ArgumentList.Add(configPath);
    process.OutputDataReceived += (_, eventArgs) => WriteLog(eventArgs.Data);
    process.ErrorDataReceived += (_, eventArgs) => WriteLog(eventArgs.Data);

    try
    {
        if (!process.Start()) throw new InvalidOperationException("无法启动 sing-box 核心");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await Task.Delay(700).ConfigureAwait(false);
        if (process.HasExited) return process.ExitCode;
        await File.WriteAllTextAsync(readySignalPath, process.Id.ToString()).ConfigureAwait(false);

        while (!process.HasExited)
        {
            if (File.Exists(stopSignalPath) || !IsProcessRunning(parentPid))
            {
                process.Kill(entireProcessTree: true);
                break;
            }
            await Task.Delay(200).ConfigureAwait(false);
        }

        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }
    catch (Exception exception)
    {
        WriteLog(exception.GetBaseException().Message);
        return 1;
    }
    finally
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
        }
        TryDelete(readySignalPath);
    }

    void WriteLog(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (logSync) writer.WriteLine(message);
    }
}

static Dictionary<string, string> ParseArguments(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index + 1 < args.Length; index += 2)
    {
        var key = args[index].TrimStart('-');
        if (key.Length > 0) result[key] = args[index + 1];
    }
    return result;
}

static void TryDelete(string path)
{
    try { if (File.Exists(path)) File.Delete(path); }
    catch { }
}

static bool IsProcessRunning(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        return !process.HasExited;
    }
    catch
    {
        return false;
    }
}
