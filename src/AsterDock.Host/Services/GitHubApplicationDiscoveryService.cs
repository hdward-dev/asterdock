using AsterDock.Contracts;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AsterDock.Host.Services;

public sealed record DiscoverableApplication(
    string Id,
    string Name,
    string Description,
    string Category,
    string Version,
    string ReleaseTag,
    string AssetName);

internal sealed class GitHubApplicationDiscoveryService : IDisposable
{
    private const string CatalogUrl = "https://raw.githubusercontent.com/hdward-dev/asterdock/main/app-catalog.json";
    private const string ReleaseByTagUrl = "https://api.github.com/repos/hdward-dev/asterdock/releases/tags/";
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient = new(new HttpClientHandler { MaxAutomaticRedirections = 5 })
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public GitHubApplicationDiscoveryService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AsterDock-AppDiscovery/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<IReadOnlyList<DiscoverableApplication>> GetCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var text = await _httpClient.GetStringAsync(CatalogUrl, cancellationToken).ConfigureAwait(false);
        var catalog = JsonSerializer.Deserialize<ApplicationCatalog>(text, JsonOptions)
            ?? throw new InvalidDataException("轻应用目录内容为空");
        if (catalog.SchemaVersion != 1) throw new InvalidDataException("不支持的轻应用目录版本");

        var applications = catalog.Applications ?? [];
        foreach (var application in applications) ValidateCatalogEntry(application);
        return applications
            .GroupBy(application => application.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(application => ParseVersion(application.Version)).First())
            .OrderBy(application => application.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string> DownloadAsync(
        DiscoverableApplication application,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateCatalogEntry(application);
        var releaseUrl = ReleaseByTagUrl + Uri.EscapeDataString(application.ReleaseTag);
        using var metadataResponse = await _httpClient.GetAsync(releaseUrl, cancellationToken).ConfigureAwait(false);
        metadataResponse.EnsureSuccessStatusCode();
        var metadataText = await metadataResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var release = JsonNode.Parse(metadataText)?.AsObject()
            ?? throw new InvalidDataException("GitHub Release 信息格式无效");
        if (release["draft"]?.GetValue<bool>() == true)
            throw new InvalidDataException("轻应用 Release 尚未发布");

        var asset = (release["assets"]?.AsArray() ?? [])
            .OfType<JsonObject>()
            .FirstOrDefault(candidate => string.Equals(
                candidate["name"]?.GetValue<string>(), application.AssetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Release 中没有找到 {application.AssetName}");
        if (!string.Equals(asset["state"]?.GetValue<string>(), "uploaded", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("轻应用包尚未上传完成");
        var digest = asset["digest"]?.GetValue<string>();
        if (digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidDataException("GitHub Release 未提供可信的 SHA-256 摘要");
        var expectedHash = digest["sha256:".Length..];
        var downloadText = asset["browser_download_url"]?.GetValue<string>();
        if (!Uri.TryCreate(downloadText, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("轻应用包下载地址无效");

        var downloadDirectory = Path.Combine(ApplicationPaths.ProductDataDirectory, "Downloads");
        Directory.CreateDirectory(downloadDirectory);
        var destination = Path.Combine(downloadDirectory, application.AssetName);
        var temporary = destination + ".download";
        try
        {
            using var response = await _httpClient.GetAsync(
                downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            if (length is > MaximumPackageBytes) throw new InvalidDataException("轻应用包超过 512 MB 限制");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                total += count;
                if (total > MaximumPackageBytes) throw new InvalidDataException("轻应用包超过 512 MB 限制");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                if (length is > 0) progress?.Report((double)total / length.Value);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);

            await using (var package = File.OpenRead(temporary))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(package, cancellationToken).ConfigureAwait(false));
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("轻应用包 SHA-256 校验失败");
            }

            ValidatePackageManifest(temporary, application);
            File.Move(temporary, destination, overwrite: true);
            progress?.Report(1);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static void ValidatePackageManifest(string packagePath, DiscoverableApplication expected)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = archive.GetEntry("app.json") ?? throw new InvalidDataException("轻应用包根目录缺少 app.json");
        if (entry.Length > 1024 * 1024) throw new InvalidDataException("轻应用清单超过 1 MB 限制");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<AppManifest>(stream, JsonOptions)
            ?? throw new InvalidDataException("轻应用清单内容为空");
        if (!manifest.Id.Equals(expected.Id, StringComparison.OrdinalIgnoreCase) ||
            !manifest.Version.Equals(expected.Version, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("轻应用包清单与发现目录中的 id 或版本不一致");
    }

    private static void ValidateCatalogEntry(DiscoverableApplication application)
    {
        if (string.IsNullOrWhiteSpace(application.Id) ||
            application.Id.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw new InvalidDataException("轻应用目录包含无效的应用 id");
        if (string.IsNullOrWhiteSpace(application.Name) || !Version.TryParse(application.Version, out _))
            throw new InvalidDataException($"轻应用 {application.Id} 的名称或版本无效");
        if (string.IsNullOrWhiteSpace(application.ReleaseTag) ||
            !application.AssetName.EndsWith(".appbundle", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(application.AssetName) != application.AssetName)
            throw new InvalidDataException($"轻应用 {application.Id} 的发布信息无效");
    }

    private static Version ParseVersion(string value) => Version.TryParse(value, out var version) ? version : new Version();

    private sealed class ApplicationCatalog
    {
        public int SchemaVersion { get; init; }
        public List<DiscoverableApplication>? Applications { get; init; }
    }
}
