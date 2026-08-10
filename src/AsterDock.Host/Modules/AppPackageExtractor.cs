using System.IO.Compression;
using System.Security.Cryptography;

namespace AsterDock.Host.Modules;

internal static class AppPackageExtractor
{
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const int MaximumEntries = 2_000;

    public static string Extract(string packagePath, string cacheRoot)
    {
        using var packageStream = File.OpenRead(packagePath);
        var fingerprint = Convert.ToHexString(SHA256.HashData(packageStream))[..20];
        var packageName = SanitizeName(Path.GetFileNameWithoutExtension(packagePath));
        var destination = Path.Combine(cacheRoot, $"{packageName}-{fingerprint}");
        var manifestPath = Path.Combine(destination, "app.json");
        if (File.Exists(manifestPath)) return manifestPath;

        Directory.CreateDirectory(cacheRoot);
        var temporary = Path.Combine(cacheRoot, $".{packageName}-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporary);
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            if (archive.Entries.Count > MaximumEntries)
                throw new InvalidDataException("应用包文件数量超过限制");
            if (archive.Entries.Sum(entry => entry.Length) > MaximumExpandedBytes)
                throw new InvalidDataException("应用包解压后超过 512 MB 限制");

            var temporaryPrefix = Path.GetFullPath(temporary) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                var outputPath = Path.GetFullPath(Path.Combine(temporary, entry.FullName));
                if (!outputPath.StartsWith(temporaryPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("应用包包含不安全的文件路径");

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(outputPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                entry.ExtractToFile(outputPath, overwrite: true);
            }

            if (!File.Exists(Path.Combine(temporary, "app.json")))
                throw new InvalidDataException("应用包根目录缺少 app.json");

            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.Move(temporary, destination);
            return manifestPath;
        }
        catch
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            throw;
        }
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "application" : result;
    }
}
