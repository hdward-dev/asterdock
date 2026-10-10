using System.Diagnostics;
using System.Globalization;
namespace VMCube.Module.Services;

public sealed record VirtualMachine(string Id, string Name, string State, int Cpus, double MemoryGiB)
{
    public string StateLabel => State switch { "running" => "运行中", "shut off" => "已关闭", "paused" => "已暂停", "in shutdown" => "关机中", "crashed" => "已崩溃", "blocked" => "阻塞", "pmsuspended" => "休眠", _ => State };
    public string Summary => $"{StateLabel}   ·   {Cpus} vCPU   ·   {MemoryGiB:0.#} GiB";
}

public sealed class LibvirtService
{
    public async Task<List<VirtualMachine>> ReadAsync(string uri, CancellationToken token)
    {
        var ids = (await RunAsync(uri, ["list", "--all", "--uuid"], token)).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var result = new List<VirtualMachine>();
        foreach (var id in ids)
        {
            if (!Guid.TryParse(id, out _)) throw new InvalidDataException("服务器返回了无效 UUID");
            var info = ParseInfo(await RunAsync(uri, ["dominfo", id], token));
            result.Add(new(id, info.GetValueOrDefault("Name", id), info.GetValueOrDefault("State", "unknown"),
                int.Parse(info["CPU(s)"], CultureInfo.InvariantCulture),
                double.Parse(info["Max memory"].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture) / 1048576));
        }
        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
    public static Dictionary<string, string> ParseInfo(string output) => output.Split('\n').Where(x => x.Contains(':'))
        .Select(x => x.Split(':', 2)).ToDictionary(x => x[0].Trim(), x => x[1].Trim());
    public Task<string> ActAsync(string uri, string action, string id, CancellationToken token)
    {
        if (action is not ("start" or "shutdown" or "suspend" or "resume") || !Guid.TryParse(id, out _))
            throw new ArgumentException("无效的虚拟机操作");
        return RunAsync(uri, [action, id], token);
    }
    public async Task<ConsoleEndpoint> GetConsoleAsync(string uri, string id, CancellationToken token)
    {
        if (!Guid.TryParse(id, out _)) throw new ArgumentException("无效的虚拟机 UUID");
        return ConsoleEndpoint.FromDomainXml(await RunAsync(uri, ["dumpxml", id], token));
    }
    private static async Task<string> RunAsync(string uri, string[] args, CancellationToken token)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("qemu" or "qemu+ssh") || (parsed.Scheme == "qemu" && !string.IsNullOrEmpty(parsed.Host)) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
            throw new ArgumentException("请输入 qemu:///system 或 qemu+ssh://user@host/system（不支持查询参数）");
        var start = new ProcessStartInfo("virsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
        start.Environment["LC_ALL"] = "C";
        foreach (var arg in new[] { "--connect", uri }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("未找到 virsh，请安装 libvirt 客户端并将 virsh 加入 PATH。"); }
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("连接或操作超过 30 秒，请检查网络、SSH 密钥和主机信任配置。");
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "virsh 执行失败" : error.Trim());
        return output.Trim();
    }
}
