using AsterDock.Contracts;
using System.Text.Json;

namespace AsterDock.Host.Modules;

internal static class ApplicationInstaller
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string InstallPackage(string sourcePath, string userAppsDirectory)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("应用包不存在", sourcePath);
        if (!Path.GetExtension(sourcePath).Equals(".appbundle", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 .appbundle 应用包");

        Directory.CreateDirectory(userAppsDirectory);
        var destination = Path.Combine(userAppsDirectory, Path.GetFileName(sourcePath));
        if (PathsEqual(sourcePath, destination)) return destination;
        File.Copy(sourcePath, destination, overwrite: true);
        return destination;
    }

    public static string InstallDirectory(string sourceDirectory, string userAppsDirectory)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var manifestPath = Path.Combine(source, "app.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("所选目录根部没有 app.json");

        var manifest = JsonSerializer.Deserialize<AppManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException("app.json 内容为空");
        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly) ||
            !File.Exists(Path.Combine(source, manifest.EntryAssembly)))
            throw new InvalidDataException("应用目录缺少清单中指定的入口程序集");
        var safeId = SanitizeId(manifest.Id);
        var installedRoot = Path.Combine(userAppsDirectory, "Installed");
        var destination = Path.GetFullPath(Path.Combine(installedRoot, safeId));
        if (PathsEqual(source, destination)) return destination;

        var sourcePrefix = source + Path.DirectorySeparatorChar;
        if (destination.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能把应用安装到它自己的子目录中");

        Directory.CreateDirectory(installedRoot);
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        CopyDirectory(source, destination);
        return destination;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("应用目录不能包含符号链接");
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("应用目录不能包含符号链接");
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string SanitizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("app.json 缺少应用 id");
        var value = new string(id.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.').ToArray());
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("app.json 的应用 id 无效");
        return value;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
