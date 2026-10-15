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
    public async Task<ConsoleEndpoint> GetConsoleAsync(string uri, string id, CancellationToken token, ConsoleProtocol? preferred = null)
    {
        if (!Guid.TryParse(id, out _)) throw new ArgumentException("无效的虚拟机 UUID");
        return ConsoleEndpoint.FromDomainXml(await RunAsync(uri, ["dumpxml", id], token), preferred);
    }
    public static ProcessStartInfo BuildStartInfo(string uri, string[] args)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("qemu" or "qemu+ssh") ||
            (parsed.Scheme == "qemu" && !string.IsNullOrEmpty(parsed.Host)) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment) ||
            parsed.AbsolutePath is not ("/system" or "/session"))
            throw new ArgumentException("请输入 qemu:///system 或 qemu+ssh://user@host/system（支持 /session，不支持查询参数）");
        if (parsed.Scheme == "qemu")
        {
            var local = NewProcess("virsh"); local.Environment["LC_ALL"] = "C";
            foreach (var arg in new[] { "--connect", uri }.Concat(args)) local.ArgumentList.Add(arg);
            return local;
        }
        // Only a fixed grammar reaches the remote shell. No URI or free-form user text is executed there.
        if (args.Length < 1 || args[0] is not ("list" or "dominfo" or "dumpxml" or "start" or "shutdown" or "suspend" or "resume") ||
            (args[0] == "list" ? !args.SequenceEqual(new[] { "list", "--all", "--uuid" }) : args.Length != 2 || !Guid.TryParse(args[1], out _)))
            throw new ArgumentException("无效的远程虚拟机操作");
        var ssh = NewProcess("ssh");
        foreach (var arg in SshArguments(parsed)) ssh.ArgumentList.Add(arg);
        ssh.ArgumentList.Add("LC_ALL=C virsh --connect qemu:///" + parsed.AbsolutePath.TrimStart('/') + " " + string.Join(' ', args));
        return ssh;
    }
    private static ProcessStartInfo NewProcess(string name) => new(name) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
    public static IReadOnlyList<string> SshArguments(Uri uri)
    {
        var user = Uri.UnescapeDataString(uri.UserInfo);
        if (string.IsNullOrEmpty(uri.Host) || user.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('_' or '-' or '.')))
            throw new ArgumentException("SSH 主机或用户名无效；请使用密钥认证，不要在 URI 中填写密码。");
        var host = uri.IdnHost.Trim('[', ']');
        if (host.StartsWith('-')) throw new ArgumentException("无效的 SSH 主机名");
        var result = new List<string> { "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=15", "-p", (uri.Port > 0 ? uri.Port : 22).ToString() };
        if (user.Length > 0) { result.Add("-l"); result.Add(user); }
        result.Add(host); return result;
    }
    private static async Task<string> RunAsync(string uri, string[] args, CancellationToken token)
    {
        var start = BuildStartInfo(uri, args);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException(start.FileName == "ssh" ? "未找到 OpenSSH 客户端，请将 ssh 加入 PATH；远程管理不需要本机 virsh。" : "本地 KVM 管理需要 virsh；远程主机请使用 qemu+ssh 连接，本机无需 virsh。"); }
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
