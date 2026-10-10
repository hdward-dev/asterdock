using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using URemote.Core;
namespace VMCube.Module.Services;

/// <summary>Owns a per-console SSH tunnel, bound only to local loopback.</summary>
public sealed class ConsoleTunnel : IAsyncDisposable
{
    private Process? _ssh;
    private Task<string>? _errors;
    public string Host { get; private set; } = "127.0.0.1";
    public int Port { get; private set; }

    public static IReadOnlyList<string> BuildArguments(string connectionUri, ConsoleEndpoint endpoint, int localPort)
    {
        var uri = new Uri(connectionUri);
        if (uri.Scheme != "qemu+ssh" || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("无效的 SSH 连接 URI");
        var user = Uri.UnescapeDataString(uri.UserInfo);
        if (user.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('_' or '-' or '.')))
            throw new ArgumentException("SSH URI 用户名无效，请使用 SSH 密钥认证。");
        new LocalDesktopOptions(LocalDesktopProtocol.Vnc, endpoint.Host, endpoint.Port).Validate();
        if (localPort is < 1 or > 65535) throw new ArgumentException("无效的本地端口");
        var target = endpoint.Host.Contains(':') ? $"[{endpoint.Host}]" : endpoint.Host;
        var host = uri.IdnHost.Trim('[', ']');
        if (host.StartsWith('-')) throw new ArgumentException("无效的 SSH 主机名");
        var args = new List<string> { "-N", "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ExitOnForwardFailure=yes",
            "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=2", "-p", (uri.Port > 0 ? uri.Port : 22).ToString(),
            "-L", $"127.0.0.1:{localPort}:{target}:{endpoint.Port}" };
        if (user.Length > 0) { args.Add("-l"); args.Add(user); }
        args.Add(host); return args;
    }

    public static async Task<ConsoleTunnel> OpenAsync(string connectionUri, ConsoleEndpoint endpoint, CancellationToken token)
    {
        var tunnel = new ConsoleTunnel();
        try
        {
            if (new Uri(connectionUri).Scheme == "qemu") { tunnel.Host = endpoint.Host; tunnel.Port = endpoint.Port; return tunnel; }
            using (var listener = new TcpListener(IPAddress.Loopback, 0)) { listener.Start(); tunnel.Port = ((IPEndPoint)listener.LocalEndpoint).Port; }
            var info = new ProcessStartInfo("ssh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (var arg in BuildArguments(connectionUri, endpoint, tunnel.Port)) info.ArgumentList.Add(arg);
            tunnel._ssh = new Process { StartInfo = info };
            try { tunnel._ssh.Start(); }
            catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("未找到 ssh，请安装 OpenSSH 客户端并配置主机密钥信任及登录密钥。"); }
            tunnel._ssh.StandardInput.Close(); tunnel._errors = tunnel._ssh.StandardError.ReadToEndAsync();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (tunnel._ssh.HasExited) throw new InvalidOperationException("SSH 隧道连接失败：" + (await tunnel._errors).Trim());
                using var probe = new TcpClient();
                try { await probe.ConnectAsync(IPAddress.Loopback, tunnel.Port, deadline.Token); break; }
                catch (SocketException) { await Task.Delay(100, deadline.Token); }
            }
            if (tunnel._ssh.HasExited) throw new InvalidOperationException("SSH 隧道已退出");
            return tunnel;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { await tunnel.DisposeAsync(); throw new TimeoutException("SSH 隧道连接超时，请检查网络、密钥及主机信任配置。"); }
        catch { await tunnel.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        var ssh = Interlocked.Exchange(ref _ssh, null);
        if (ssh is null) return;
        try
        {
            try { if (!ssh.HasExited) ssh.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await ssh.WaitForExitAsync();
            if (_errors is not null) await _errors;
        }
        catch (InvalidOperationException) { }
        finally { ssh.Dispose(); }
    }
}
