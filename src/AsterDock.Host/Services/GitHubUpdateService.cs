using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AsterDock.Host.Services;

internal sealed record ApplicationUpdate(
    Version Version,
    string DisplayVersion,
    string ReleaseName,
    string ReleaseNotes,
    Uri ReleasePage,
    Uri DownloadUri,
    string AssetName,
    string? Sha256);

internal sealed class GitHubUpdateService : IDisposable
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/hdward-dev/asterdock/releases/latest";
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);
    private const long MaximumInstallerBytes = 1024L * 1024 * 1024;
    private readonly HttpClient _httpClient = new(new HttpClientHandler { MaxAutomaticRedirections = 5 })
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public GitHubUpdateService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AsterDock-Updater/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version is { } version
            ? new Version(version.Major, version.Minor, Math.Max(0, version.Build))
            : new Version(1, 0, 0);

    public static bool IsAutomaticCheckDue()
    {
        var marker = GetLastCheckMarkerPath();
        return !File.Exists(marker) || DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) >= AutomaticCheckInterval;
    }

    public static void MarkCheckCompleted()
    {
        var marker = GetLastCheckMarkerPath();
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
    }

    public async Task<ApplicationUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var root = JsonNode.Parse(text)?.AsObject()
            ?? throw new InvalidDataException("GitHub Release 信息格式无效");

        var tag = root["tag_name"]?.GetValue<string>();
        if (!TryParseVersion(tag, out var releaseVersion))
            throw new InvalidDataException("GitHub Release 缺少有效的版本号");
        if (releaseVersion <= CurrentVersion) return null;

        var expectedAssetName = GetAssetName();
        var asset = (root["assets"]?.AsArray() ?? [])
            .OfType<JsonObject>()
            .FirstOrDefault(candidate => string.Equals(
                candidate["name"]?.GetValue<string>(), expectedAssetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"最新版本没有适用于当前平台的安装包（{expectedAssetName}）");
        var downloadUrl = asset["browser_download_url"]?.GetValue<string>();
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("GitHub Release 下载地址无效");

        var releasePageText = root["html_url"]?.GetValue<string>();
        if (!Uri.TryCreate(releasePageText, UriKind.Absolute, out var releasePage))
            releasePage = new Uri("https://github.com/hdward-dev/asterdock/releases");
        var digest = asset["digest"]?.GetValue<string>();
        var sha256 = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
            ? digest["sha256:".Length..]
            : null;
        if (string.IsNullOrWhiteSpace(sha256))
            throw new InvalidDataException("GitHub Release 未提供可信的 SHA-256 摘要");

        return new ApplicationUpdate(
            releaseVersion,
            tag ?? releaseVersion.ToString(),
            root["name"]?.GetValue<string>() ?? tag ?? releaseVersion.ToString(),
            root["body"]?.GetValue<string>() ?? string.Empty,
            releasePage,
            downloadUri,
            expectedAssetName,
            sha256);
    }

    public async Task<string> DownloadAsync(
        ApplicationUpdate update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var updateDirectory = Path.Combine(ApplicationPaths.ProductDataDirectory, "Updates", update.DisplayVersion);
        Directory.CreateDirectory(updateDirectory);
        var destination = Path.Combine(updateDirectory, update.AssetName);
        var temporary = destination + ".download";

        try
        {
            using var response = await _httpClient.GetAsync(
                update.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            if (length is > MaximumInstallerBytes)
                throw new InvalidDataException("更新安装包超过 1 GB 限制");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                total += count;
                if (total > MaximumInstallerBytes)
                    throw new InvalidDataException("更新安装包超过 1 GB 限制");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                if (length is > 0) progress?.Report((double)total / length.Value);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(update.Sha256))
            {
                await using var file = File.OpenRead(temporary);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
                if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新安装包 SHA-256 校验失败");
            }

            File.Move(temporary, destination, overwrite: true);
            progress?.Report(1);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void OpenInstaller(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("更新安装包不存在", path);
        if (OperatingSystem.IsMacOS())
            Process.Start(new ProcessStartInfo("open", path) { UseShellExecute = false });
        else if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
            throw new PlatformNotSupportedException("当前平台不支持自动打开安装包");
    }

    public void Dispose() => _httpClient.Dispose();

    private static string GetAssetName()
    {
        var platform = OperatingSystem.IsWindows() ? "win" :
            OperatingSystem.IsMacOS() ? "osx" :
            throw new PlatformNotSupportedException("更新功能目前仅支持 Windows 和 macOS");
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("更新功能目前仅支持 x64 和 arm64")
        };
        var extension = OperatingSystem.IsWindows() ? "msi" : "dmg";
        return $"AsterDock-{platform}-{architecture}.{extension}";
    }

    private static string GetLastCheckMarkerPath() =>
        Path.Combine(ApplicationPaths.ProductDataDirectory, "Updates", "last-check");

    private static bool TryParseVersion(string? value, out Version version)
    {
        var normalized = value?.Trim().TrimStart('v', 'V');
        var suffixIndex = normalized?.IndexOfAny(['-', '+']) ?? -1;
        if (suffixIndex >= 0) normalized = normalized![..suffixIndex];
        return Version.TryParse(normalized, out version!);
    }
}
