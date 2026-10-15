using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
namespace VMCube.Module.Services;

/// <summary>Installs a platform-specific engine from a SHA-256 verified AsterDock release asset.</summary>
public sealed class QemuEngineInstaller
{
    public static string? GetInstalledDirectory(string dataDirectory)
    {
        var marker = Path.Combine(dataDirectory, "engine-directory.json");
        return File.Exists(marker) ? JsonSerializer.Deserialize<string>(File.ReadAllText(marker)) : null;
    }
    public async Task<string> InstallAsync(string dataDirectory, IProgress<string>? progress, CancellationToken token)
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        var name = $"VMCUBE-QEMU-{rid}.zip";
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VMCUBE/0.3");
        progress?.Report("正在查找本机引擎…");
        using var release = await client.GetFromJsonAsync<JsonDocument>("https://api.github.com/repos/hdward-dev/asterdock/releases/latest", token) ?? throw new IOException("无法读取引擎发布信息。");
        var asset = release.RootElement.GetProperty("assets").EnumerateArray().FirstOrDefault(x => x.GetProperty("name").GetString() == name);
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException($"当前正式发布尚未提供 {rid} 的本机引擎。可选择已有 QEMU 的 bin 目录，或使用包含引擎的开发包。");
        var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit)) throw new InvalidDataException("引擎发布缺少有效 SHA-256 摘要，已停止安装。");
        var url = asset.GetProperty("browser_download_url").GetString() ?? "";
        if (!url.StartsWith("https://github.com/hdward-dev/asterdock/releases/download/", StringComparison.Ordinal)) throw new InvalidDataException("引擎下载地址不受信任。");
        var root = Path.Combine(dataDirectory, "engines"); Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, ".download-" + Guid.NewGuid());
        Directory.CreateDirectory(temporary);
        var archivePath = Path.Combine(temporary, "engine.zip"); var stage = Path.Combine(temporary, "files");
        try
        {
            progress?.Report("正在下载本机引擎…");
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(archivePath);
                var buffer = new byte[128 * 1024]; long bytes = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, token); if (count == 0) break;
                    bytes += count; if (bytes > 512L * 1024 * 1024) throw new InvalidDataException("引擎压缩包超过大小限制。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
            }
            progress?.Report("正在校验和安装引擎…");
            await using (var file = File.OpenRead(archivePath))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(digest[7..], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("引擎 SHA-256 校验失败。");
            ExtractVerifiedArchive(archivePath, stage);
            var destination = Path.Combine(root, rid + "-" + digest[7..19]);
            if (!Directory.Exists(destination)) Directory.Move(stage, destination);
            var executable = Path.Combine(destination, "qemu-system-x86_64" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            var imageTool = Path.Combine(destination, "qemu-img" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (!File.Exists(executable) || !File.Exists(imageTool)) throw new InvalidDataException("引擎包缺少 QEMU 或 qemu-img。");
            SpiceDesktopSession.PrepareExecutable(executable); SpiceDesktopSession.PrepareExecutable(imageTool);
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, "engine-directory.json"), JsonSerializer.Serialize(destination), token);
            progress?.Report("本机引擎已就绪"); return destination;
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
    public static void ExtractVerifiedArchive(string archivePath, string directory)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        if (zip.Entries.Count > 2000 || zip.Entries.Sum(x => x.Length) > 512L * 1024 * 1024) throw new InvalidDataException("引擎解压内容超过限制。");
        var prefix = Path.GetFullPath(directory) + Path.DirectorySeparatorChar; Directory.CreateDirectory(directory);
        foreach (var entry in zip.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(directory, entry.FullName));
            if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException("引擎包包含不安全路径。");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path, overwrite: false);
        }
    }
}
