using AsterDock.Host.Services;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

foreach (var tag in new[] { "v1.0.123-nightly-20260923-abcdef0", "v1.0.123", "1.0.123" })
    Check(GitHubUpdateService.TryParseVersion(tag, out var version) && version == new Version(1, 0, 123),
        $"Cannot parse published version: {tag}");
Check(!GitHubUpdateService.TryParseVersion("nightly-20260923-abcdef0", out _),
    "A date-only tag must not be compared with assembly versions");

using (var currentRelease = new GitHubUpdateService(new FakeHandler(Encoding.UTF8.GetBytes(
    $"{{\"tag_name\":\"v{GitHubUpdateService.CurrentVersion}-nightly-20260923-abcdef0\"}}"))))
    Check(await currentRelease.CheckAsync() is null, "Current nightly must not offer an update");

var release = JsonSerializer.Serialize(new
{
    tag_name = "v99.0.123-nightly-20260923-abcdef0",
    html_url = "https://github.com/hdward-dev/asterdock/releases/tag/test",
    assets = new[] { "win-x64.msi", "win-arm64.msi", "osx-x64.dmg", "osx-arm64.dmg" }
        .Select(suffix => new { name = "AsterDock-" + suffix,
            browser_download_url = "https://github.com/hdward-dev/asterdock/releases/download/test/AsterDock-" + suffix,
            digest = "sha256:" + new string('a', 64) })
});
using (var newerRelease = new GitHubUpdateService(new FakeHandler(Encoding.UTF8.GetBytes(release))))
{
    var available = await newerRelease.CheckAsync();
    Check(available?.Version == new Version(99, 0, 123), "New nightly must be detected");
    Check(available!.ReleasePage.AbsolutePath.EndsWith("/tag/test"), "Release page must be retained");
    if (OperatingSystem.IsLinux())
        Check(available.DownloadUri is null, "Linux must offer a release page without an incompatible installer");
    else
        Check(available.DownloadUri is not null, "Windows/macOS must offer an installer");
}

var payload = Encoding.UTF8.GetBytes("installer fixture");
var update = new ApplicationUpdate(new Version(1, 0, 123), "v1.0.123-nightly-test", "Test", "",
    new Uri("https://github.com/hdward-dev/asterdock/releases"),
    new Uri("https://github.com/hdward-dev/asterdock/releases/download/test/fixture.msi"),
    "fixture.msi", Convert.ToHexString(SHA256.HashData(payload)));
try
{
    using var service = new GitHubUpdateService(new FakeHandler(payload));
    var path = await service.DownloadAsync(update);
    Check(File.ReadAllBytes(path).SequenceEqual(payload), "Downloaded installer differs");
    Check(!File.Exists(path + ".download"), "Temporary download was not moved");
    await service.DownloadAsync(update); // An existing installer can be replaced on retry.
    try
    {
        await service.DownloadAsync(update with { Sha256 = new string('0', 64) });
        throw new Exception("Corrupted installer was accepted");
    }
    catch (InvalidDataException)
    {
        Check(!File.Exists(path + ".download"), "Failed download was not cleaned up");
        Check(File.ReadAllBytes(path).SequenceEqual(payload), "Failed download replaced verified installer");
    }

    using var packageStream = new MemoryStream();
    using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: true))
    using (var writer = new StreamWriter(archive.CreateEntry("app.json").Open()))
        writer.Write("""{"id":"fixture","name":"Fixture","version":"1.0.0","entryAssembly":"Fixture.dll","entryType":"Fixture.Module"}""");
    var packageBytes = packageStream.ToArray();
    var application = new DiscoverableApplication("fixture", "Fixture", "", "Tools", "1.0.0", "v1.0.36-test", "fixture.appbundle");
    var packageAsset = JsonSerializer.Serialize(new[] { new {
        name = application.AssetName, state = "uploaded", digest = "sha256:" + Convert.ToHexString(SHA256.HashData(packageBytes)),
        browser_download_url = "https://github.com/hdward-dev/asterdock/releases/download/test/fixture.appbundle"
    }});
    using var discovery = new GitHubApplicationDiscoveryService(new RoutedHandler(request =>
        request.RequestUri!.AbsolutePath switch
        {
            "/repos/hdward-dev/asterdock/releases/tags/v1.0.36-test" => Encoding.UTF8.GetBytes("""{"id":123,"draft":false,"assets":[]}"""),
            "/repos/hdward-dev/asterdock/releases/123/assets" => Encoding.UTF8.GetBytes(packageAsset),
            "/hdward-dev/asterdock/releases/download/test/fixture.appbundle" => packageBytes,
            _ => throw new Exception("Unexpected request: " + request.RequestUri)
        }));
    var packagePath = await discovery.DownloadAsync(application);
    Check(File.ReadAllBytes(packagePath).SequenceEqual(packageBytes), "Empty embedded assets must fall back to the release asset list");
    Check(!File.Exists(packagePath + ".download"), "Application download was not finalized");
}
finally
{
    Directory.Delete(ApplicationPaths.ProductDataDirectory, recursive: true);
}
Console.WriteLine("Update version, download, checksum, retry and cleanup checks passed.");

sealed class FakeHandler(byte[] payload) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
}

sealed class RoutedHandler(Func<HttpRequestMessage, byte[]> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response(request)) });
}

namespace AsterDock.Host.Services
{
    internal static class ApplicationPaths
    {
        public static string ProductDataDirectory { get; } = Path.Combine(Path.GetTempPath(), "asterdock-update-checks-" + Guid.NewGuid());
    }
}
