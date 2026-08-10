using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
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
        var (archiveUrl, expectedHash) = await GetReleaseAssetAsync(package, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(_coreDirectory);
        var archivePath = Path.Combine(_coreDirectory, package + ".download");
        var executableName = OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box";
        var executablePath = Path.Combine(_coreDirectory, executableName);
        var temporaryExecutable = executablePath + ".new";

        try
        {
            await DownloadAsync(archiveUrl, archivePath, cancellationToken).ConfigureAwait(false);
            var actualHash = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("sing-box 下载包校验失败，文件可能不完整");

            if (File.Exists(temporaryExecutable)) File.Delete(temporaryExecutable);
            if (package.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                await ExtractFromZipAsync(archivePath, temporaryExecutable, executableName, cancellationToken).ConfigureAwait(false);
            else
                await ExtractFromTarGzipAsync(archivePath, temporaryExecutable, executableName, cancellationToken).ConfigureAwait(false);

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporaryExecutable,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            await VerifyAsync(temporaryExecutable, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryExecutable, executablePath, overwrite: true);
            return executablePath;
        }
        finally
        {
            TryDelete(archivePath);
            TryDelete(temporaryExecutable);
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

    private static async Task ExtractFromZipAsync(string archivePath, string destination, string executableName, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.FirstOrDefault(candidate =>
            Path.GetFileName(candidate.FullName).Equals(executableName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("sing-box 下载包中未找到核心文件");
        await using var input = entry.Open();
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExtractFromTarGzipAsync(string archivePath, string destination, string executableName, CancellationToken cancellationToken)
    {
        await using var archiveStream = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(archiveStream, CompressionMode.Decompress);
        using var reader = new TarReader(gzip, leaveOpen: false);
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            if (!Path.GetFileName(entry.Name).Equals(executableName, StringComparison.Ordinal) || entry.DataStream is null) continue;
            await using var output = File.Create(destination);
            await entry.DataStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            return;
        }
        throw new InvalidDataException("sing-box 下载包中未找到核心文件");
    }

    private static async Task VerifyAsync(string executable, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("version");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法验证 sing-box 核心");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidDataException("下载的 sing-box 核心无法运行");
    }

    private static string GetPackageName()
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" :
            throw new PlatformNotSupportedException("当前只支持在 Windows 或 macOS 安装 sing-box 核心");
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"不支持的处理器架构：{RuntimeInformation.ProcessArchitecture}")
        };
        var extension = OperatingSystem.IsWindows() ? "zip" : "tar.gz";
        return $"sing-box-{StableVersion}-{os}-{architecture}.{extension}";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
