using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace NetworkAccelerator.Core.Services;

public sealed class SingBoxInstallerService : IDisposable
{
    public const string StableVersion = "1.13.12";
    private const long MaximumArchiveBytes = 128L * 1024 * 1024;
    private readonly HttpClient _httpClient = new(new HttpClientHandler { MaxAutomaticRedirections = 5 })
    {
        Timeout = TimeSpan.FromMinutes(3)
    };
    private readonly string _coreDirectory;

    public SingBoxInstallerService(string dataDirectory)
    {
        _coreDirectory = Path.Combine(dataDirectory, "core");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AsterDock-NetworkAccelerator/1.0");
    }

    public async Task<string> InstallAsync(CancellationToken cancellationToken = default)
    {
        var package = GetPackageName();
        try
        {
            return await InstallPackageAsync(package, cancellationToken).ConfigureAwait(false);
        }
        catch (LinuxDynamicLoaderException) when (MuslFallbackPackage(package) is { } musl)
        {
            // The plain linux asset is dynamically linked. NixOS without nix-ld cannot start it.
            // The matching official musl asset is a static ELF and does not need a system package.
            return await InstallPackageAsync(musl, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> InstallPackageAsync(string package, CancellationToken cancellationToken)
    {
        var (archiveUrl, expectedHash) = await GetReleaseAssetAsync(package, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(_coreDirectory);
        var archivePath = Path.Combine(_coreDirectory, package + ".download");
        var executableName = OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box";
        var executablePath = Path.Combine(_coreDirectory, executableName);
        var staging = Path.Combine(_coreDirectory, ".staging-" + Guid.NewGuid().ToString("N"));

        try
        {
            await DownloadAsync(archiveUrl, archivePath, cancellationToken).ConfigureAwait(false);
            var actualHash = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("sing-box 下载包校验失败，文件可能不完整");

            Directory.CreateDirectory(staging);
            await SingBoxCoreLayout.ExtractAsync(archivePath, staging, executableName, cancellationToken).ConfigureAwait(false);
            var stagedExecutable = Path.Combine(staging, executableName);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(stagedExecutable, ExecutableMode);

            await VerifyAsync(stagedExecutable, cancellationToken).ConfigureAwait(false);
            File.Move(stagedExecutable, executablePath, overwrite: true);
            InstallCompanions(staging);
            return executablePath;
        }
        finally
        {
            TryDelete(archivePath);
            TryDeleteDirectory(staging);
        }
    }

    private void InstallCompanions(string staging)
    {
        foreach (var name in SingBoxCoreLayout.CompanionFileNames)
        {
            var from = Path.Combine(staging, name);
            var to = Path.Combine(_coreDirectory, name);
            if (!File.Exists(from))
            {
                TryDelete(to);
                continue;
            }

            File.Move(from, to, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(to,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumArchiveBytes)
            throw new InvalidDataException("sing-box 下载包超过 128 MB 限制");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
            if (total > MaximumArchiveBytes) throw new InvalidDataException("sing-box 下载包超过 128 MB 限制");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(string DownloadUrl, string Sha256)> GetReleaseAssetAsync(
        string package,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/SagerNet/sing-box/releases/tags/v{StableVersion}";
        var text = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var root = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException("GitHub Release 信息格式无效");
        foreach (var value in root["assets"]?.AsArray() ?? [])
        {
            if (value is not JsonObject asset || asset["name"]?.GetValue<string>() != package) continue;
            var downloadUrl = asset["browser_download_url"]?.GetValue<string>();
            var digest = asset["digest"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(downloadUrl) || digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) != true)
                throw new InvalidDataException("GitHub Release 未提供可信的 SHA-256 摘要");
            return (downloadUrl, digest["sha256:".Length..]);
        }
        throw new InvalidDataException("GitHub Release 中没有当前平台的 sing-box 下载包");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task VerifyAsync(string executable, CancellationToken cancellationToken)
    {
        if (LinuxExecutableFormat.MissingInterpreter(executable) is { } missing)
            throw new LinuxDynamicLoaderException(missing);

        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("version");
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法验证 sing-box 核心");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            var interpreter = LinuxExecutableFormat.MissingInterpreter(executable) ?? exception.GetBaseException().Message;
            throw new LinuxDynamicLoaderException(interpreter, exception);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode == 0
            && (stdout.Contains("sing-box", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("sing-box", StringComparison.OrdinalIgnoreCase)))
            return;

        var detail = FirstLine(stderr) ?? FirstLine(stdout);
        if (LinuxExecutableFormat.LooksLikeMissingLoader(detail)
            || LinuxExecutableFormat.LooksLikeMissingLoader(stdout)
            || LinuxExecutableFormat.LooksLikeMissingLoader(stderr))
            throw new LinuxDynamicLoaderException(string.IsNullOrWhiteSpace(detail) ? "动态链接器" : detail);
        throw new InvalidDataException("下载的 sing-box 核心无法运行");
    }

    public static string GetPackageName() =>
        GetPackageName(CurrentOperatingSystem(), RuntimeInformation.ProcessArchitecture);

    public static string GetPackageName(string operatingSystem, Architecture architecture)
    {
        var os = operatingSystem switch
        {
            "windows" or "darwin" or "linux" => operatingSystem,
            _ => throw new PlatformNotSupportedException("当前只支持在 Windows、macOS 或 Linux 安装 sing-box 核心")
        };
        var arch = architecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"不支持的处理器架构：{architecture}")
        };
        var extension = os == "windows" ? "zip" : "tar.gz";
        return $"sing-box-{StableVersion}-{os}-{arch}.{extension}";
    }

    public static string? MuslFallbackPackage(string package)
    {
        const string suffix = ".tar.gz";
        if (!package.EndsWith(suffix, StringComparison.Ordinal)
            || !package.Contains("-linux-", StringComparison.Ordinal)
            || package.Contains("-musl", StringComparison.Ordinal))
            return null;
        return package[..^suffix.Length] + "-musl.tar.gz";
    }

    public static string MissingDynamicLoaderMessage(string interpreter)
    {
        var loader = string.IsNullOrWhiteSpace(interpreter) ? "动态链接器" : interpreter.Trim();
        var line = loader.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? loader;
        if (line.Length > 180) line = line[..180];
        return $"下载的 sing-box 核心无法在当前系统直接运行：缺少动态链接器 {line}。NixOS 请设置 programs.nix-ld.enable = true; 后重新构建系统，或将已能运行的 sing-box 放进应用数据目录的 core 文件夹。";
    }

    public static bool IsLinuxLoaderFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is LinuxDynamicLoaderException) return true;
        }

        return false;
    }

    private static string CurrentOperatingSystem() =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "darwin"
        : OperatingSystem.IsLinux() ? "linux"
        : throw new PlatformNotSupportedException("当前只支持在 Windows、macOS 或 Linux 安装 sing-box 核心");

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
    }

    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}

public sealed class LinuxDynamicLoaderException : Exception
{
    public LinuxDynamicLoaderException(string interpreter)
        : this(interpreter, null)
    {
    }

    public LinuxDynamicLoaderException(string interpreter, Exception? inner)
        : base(SingBoxInstallerService.MissingDynamicLoaderMessage(interpreter), inner)
    {
        Interpreter = interpreter;
    }

    public string Interpreter { get; }
}
