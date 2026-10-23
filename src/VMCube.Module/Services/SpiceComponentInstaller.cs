using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
namespace VMCube.Module.Services;

/// <summary>Installs the current RID's SPICE helper and relocated libraries from a SHA-256 verified release asset.</summary>
public sealed class SpiceComponentInstaller
{
    public const long MaximumArchiveBytes = 512L * 1024 * 1024;
    public static string AssetFileName(string rid) => $"VMCUBE-SPICE-{rid}.zip";
    public static string? GetInstalledDirectory(string dataDirectory)
    {
        var marker = Path.Combine(dataDirectory, "spice-directory.json");
        return File.Exists(marker) ? JsonSerializer.Deserialize<string>(File.ReadAllText(marker)) : null;
    }
    public static string RequireSha256Digest(string? digest)
    {
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidDataException("SPICE 组件发布缺少有效 SHA-256 摘要，已停止安装。");
        return digest;
    }
    public static void RequireReleaseDownloadUrl(string? url)
    {
        if (url is null || !url.StartsWith("https://github.com/hdward-dev/asterdock/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("SPICE 组件下载地址不受信任。");
    }
    public async Task<string> InstallAsync(string dataDirectory, IProgress<string>? progress, CancellationToken token)
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        var name = AssetFileName(rid);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VMCUBE/0.7");
        progress?.Report("正在查找 SPICE 组件…");
        using var release = await client.GetFromJsonAsync<JsonDocument>("https://api.github.com/repos/hdward-dev/asterdock/releases/latest", token) ?? throw new IOException("无法读取 SPICE 组件发布信息。");
        var asset = release.RootElement.GetProperty("assets").EnumerateArray().FirstOrDefault(x => x.GetProperty("name").GetString() == name);
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException($"当前正式发布尚未提供 {rid} 的 SPICE 组件。可更新到包含 native/{rid} 的 VMCUBE 安装包后再试。");
        var digest = RequireSha256Digest(asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null);
        var url = asset.GetProperty("browser_download_url").GetString() ?? "";
        RequireReleaseDownloadUrl(url);
        var root = Path.Combine(dataDirectory, "spice"); Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, ".download-" + Guid.NewGuid());
        Directory.CreateDirectory(temporary);
        var archivePath = Path.Combine(temporary, "spice.zip");
        try
        {
            progress?.Report("正在下载 SPICE 组件…");
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(archivePath);
                var buffer = new byte[128 * 1024]; long bytes = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, token); if (count == 0) break;
                    bytes += count; if (bytes > MaximumArchiveBytes) throw new InvalidDataException("SPICE 组件压缩包超过大小限制。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
            }
            progress?.Report("正在校验和安装 SPICE 组件…");
            var destination = await InstallVerifiedArchiveAsync(dataDirectory, archivePath, digest, token);
            progress?.Report("SPICE 组件已就绪"); return destination;
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
    public static async Task<string> InstallVerifiedArchiveAsync(string dataDirectory, string archivePath, string digest, CancellationToken token)
    {
        digest = RequireSha256Digest(digest);
        var root = Path.Combine(dataDirectory, "spice"); Directory.CreateDirectory(root);
        var stage = Path.Combine(root, ".stage-" + Guid.NewGuid());
        Directory.CreateDirectory(stage);
        try
        {
            await using (var file = File.OpenRead(archivePath))
            {
                if (file.Length > MaximumArchiveBytes) throw new InvalidDataException("SPICE 组件压缩包超过大小限制。");
                if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SPICE 组件 SHA-256 校验失败。");
            }
            QemuEngineInstaller.ExtractVerifiedArchive(archivePath, stage);
            var helperName = SpiceDesktopSession.HelperFileName;
            var helper = Path.Combine(stage, helperName);
            if (!File.Exists(helper)) throw new InvalidDataException("SPICE 组件包缺少 " + helperName + "。");
            SpiceDesktopSession.PrepareExecutable(helper);
            var destination = Path.Combine(root, RuntimeInformation.RuntimeIdentifier + "-" + digest[7..19]);
            if (Directory.Exists(destination) && !File.Exists(Path.Combine(destination, helperName))) Directory.Delete(destination, recursive: true);
            if (!Directory.Exists(destination)) Directory.Move(stage, destination);
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, "spice-directory.json"), JsonSerializer.Serialize(destination), token);
            return destination;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }
}
