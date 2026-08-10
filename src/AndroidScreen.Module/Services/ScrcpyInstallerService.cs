using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AndroidScreen.Module.Services;

public sealed class ScrcpyInstallerService : IDisposable
{
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private readonly HttpClient _httpClient = new(new HttpClientHandler { MaxAutomaticRedirections = 5 })
    {
        Timeout = TimeSpan.FromMinutes(5)
    };
    private readonly string _installDirectory;

    public ScrcpyInstallerService(string dataDirectory)
    {
        _installDirectory = Path.Combine(dataDirectory, "scrcpy");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AsterDock-AndroidScreen/1.0");
    }

    public string? FindExecutable()
    {
        if (!Directory.Exists(_installDirectory)) return null;
        var fileName = OperatingSystem.IsWindows() ? "scrcpy.exe" : "scrcpy";
        return Directory.EnumerateFiles(_installDirectory, fileName, SearchOption.AllDirectories).FirstOrDefault();
    }

    public async Task<string> InstallAsync(CancellationToken cancellationToken = default)
    {
        var asset = await GetReleaseAssetAsync(cancellationToken).ConfigureAwait(false);
        var archivePath = Path.Combine(Path.GetTempPath(), $"asterdock-scrcpy-{Guid.NewGuid():N}{asset.Extension}");
        var stagingDirectory = _installDirectory + ".new";
        var backupDirectory = _installDirectory + ".old";

        try
        {
            await DownloadAsync(asset.DownloadUrl, archivePath, cancellationToken).ConfigureAwait(false);
            var actualHash = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
            if (!actualHash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("scrcpy 下载包校验失败，文件可能不完整");

            TryDeleteDirectory(stagingDirectory);
            Directory.CreateDirectory(stagingDirectory);
            if (asset.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                await ExtractZipAsync(archivePath, stagingDirectory, cancellationToken).ConfigureAwait(false);
            else
                await ExtractTarGzipAsync(archivePath, stagingDirectory, cancellationToken).ConfigureAwait(false);

            var executable = FindExecutable(stagingDirectory)
                ?? throw new InvalidDataException("scrcpy 下载包中未找到可执行文件");
            if (OperatingSystem.IsMacOS())
                File.SetUnixFileMode(executable,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            await VerifyAsync(executable, cancellationToken).ConfigureAwait(false);

            TryDeleteDirectory(backupDirectory);
            var previousInstallationMoved = false;
            try
            {
                if (Directory.Exists(_installDirectory))
                {
                    Directory.Move(_installDirectory, backupDirectory);
                    previousInstallationMoved = true;
                }
                Directory.Move(stagingDirectory, _installDirectory);
                TryDeleteDirectory(backupDirectory);
                return FindExecutable() ?? throw new InvalidDataException("scrcpy 安装目录无效");
            }
            catch
            {
                if (previousInstallationMoved && !Directory.Exists(_installDirectory) && Directory.Exists(backupDirectory))
                    Directory.Move(backupDirectory, _installDirectory);
                throw;
            }
        }
        finally
        {
            TryDelete(archivePath);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<ReleaseAsset> GetReleaseAssetAsync(CancellationToken cancellationToken)
    {
        const string releaseUrl = "https://api.github.com/repos/Genymobile/scrcpy/releases/latest";
        var text = await _httpClient.GetStringAsync(releaseUrl, cancellationToken).ConfigureAwait(false);
        var root = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException("GitHub Release 信息格式无效");
        var prefix = GetAssetPrefix();
        foreach (var value in root["assets"]?.AsArray() ?? [])
        {
            if (value is not JsonObject asset) continue;
            var name = asset["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var extension = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ".zip" :
                name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz" : null;
            var downloadUrl = asset["browser_download_url"]?.GetValue<string>();
            var digest = asset["digest"]?.GetValue<string>();
            if (extension is not null && !string.IsNullOrWhiteSpace(downloadUrl) &&
                digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
                return new ReleaseAsset(downloadUrl, digest["sha256:".Length..], extension);
        }

        throw new InvalidDataException("GitHub Release 中没有当前平台的 scrcpy 下载包");
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumArchiveBytes)
            throw new InvalidDataException("scrcpy 下载包超过 256 MB 限制");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
            if (total > MaximumArchiveBytes) throw new InvalidDataException("scrcpy 下载包超过 256 MB 限制");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExtractZipAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var target = GetSafeExtractionPath(destination, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = File.Create(target);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExtractTarGzipAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        await using var archiveStream = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(archiveStream, CompressionMode.Decompress);
        using var reader = new TarReader(gzip, leaveOpen: false);
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            var target = GetSafeExtractionPath(destination, entry.Name);
            if (entry.EntryType is TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            if (entry.EntryType is not TarEntryType.RegularFile || entry.DataStream is null) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var output = File.Create(target);
            await entry.DataStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? FindExecutable(string directory)
    {
        var fileName = OperatingSystem.IsWindows() ? "scrcpy.exe" : "scrcpy";
        return Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task VerifyAsync(string executable, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--version");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法验证 scrcpy");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidDataException("下载的 scrcpy 无法运行");
    }

    private static string GetAssetPrefix()
    {
        if (OperatingSystem.IsWindows()) return "scrcpy-win64-";
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("当前只支持在 Windows 或 macOS 安装 scrcpy");
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "scrcpy-macos-x86_64-",
            Architecture.Arm64 => "scrcpy-macos-aarch64-",
            _ => throw new PlatformNotSupportedException($"不支持的处理器架构：{RuntimeInformation.ProcessArchitecture}")
        };
    }

    private static string GetSafeExtractionPath(string destination, string entryName)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(destination, entryName));
        if (!target.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("下载包包含非法文件路径");
        return target;
    }

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

    private sealed record ReleaseAsset(string DownloadUrl, string Sha256, string Extension);
}
