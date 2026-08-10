using AsterDock.Contracts;
using System.Reflection;
using System.Text.Json;

namespace AsterDock.Host.Modules;

public sealed record ModuleLoadFailure(string ManifestPath, string Message);
public sealed record ModuleCatalogResult(IReadOnlyList<LoadedApplication> Applications, IReadOnlyList<ModuleLoadFailure> Failures);

public sealed class ModuleCatalog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly List<LoadedApplication> _loaded = [];

    public ModuleCatalogResult Load(IEnumerable<string> appDirectories, string packageCacheDirectory)
    {
        DisposeLoadedApplications();
        var failures = new List<ModuleLoadFailure>();
        var manifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var appsDirectory in appDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(appsDirectory)) continue;
            foreach (var manifestPath in Directory.EnumerateFiles(appsDirectory, "app.json", SearchOption.AllDirectories))
                manifests.Add(manifestPath);

            foreach (var packagePath in Directory.EnumerateFiles(appsDirectory, "*.appbundle", SearchOption.AllDirectories))
            {
                try
                {
                    manifests.Add(AppPackageExtractor.Extract(packagePath, packageCacheDirectory));
                }
                catch (Exception exception)
                {
                    failures.Add(new ModuleLoadFailure(packagePath, exception.GetBaseException().Message));
                }
            }
        }

        var applicationsById = new Dictionary<string, LoadedApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifestPath in manifests)
        {
            try
            {
                var application = LoadOne(manifestPath);
                if (!applicationsById.TryGetValue(application.Manifest.Id, out var existing))
                {
                    applicationsById.Add(application.Manifest.Id, application);
                    continue;
                }

                if (CompareVersions(application.Version, existing.Version) > 0)
                {
                    existing.Dispose();
                    applicationsById[application.Manifest.Id] = application;
                }
                else
                {
                    application.Dispose();
                }
            }
            catch (Exception exception)
            {
                failures.Add(new ModuleLoadFailure(manifestPath, exception.GetBaseException().Message));
            }
        }

        _loaded.AddRange(applicationsById.Values);
        return new ModuleCatalogResult(_loaded.OrderBy(app => app.Manifest.Order).ThenBy(app => app.Name).ToList(), failures);
    }

    private static int CompareVersions(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
            return leftVersion.CompareTo(rightVersion);
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static LoadedApplication LoadOne(string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize<AppManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException("应用清单内容为空");
        ValidateManifest(manifest);

        var appDirectory = Path.GetFullPath(Path.GetDirectoryName(manifestPath)!);
        var entryAssemblyPath = Path.GetFullPath(Path.Combine(appDirectory, manifest.EntryAssembly));
        var directoryPrefix = appDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? appDirectory
            : appDirectory + Path.DirectorySeparatorChar;
        if (!entryAssemblyPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("入口程序集不能位于应用目录之外");
        if (!File.Exists(entryAssemblyPath))
            throw new FileNotFoundException("找不到应用入口程序集", entryAssemblyPath);

        var context = new AppModuleLoadContext(entryAssemblyPath);
        try
        {
            var assembly = context.LoadFromAssemblyPath(entryAssemblyPath);
            var type = assembly.GetType(manifest.EntryType, throwOnError: true)
                ?? throw new TypeLoadException($"找不到入口类型 {manifest.EntryType}");
            if (!typeof(IApplicationModule).IsAssignableFrom(type))
                throw new InvalidDataException($"入口类型必须实现 {nameof(IApplicationModule)}");
            if (Activator.CreateInstance(type) is not IApplicationModule module)
                throw new InvalidOperationException("无法创建应用模块实例");
            return new LoadedApplication(manifest, appDirectory, module, context);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private static void ValidateManifest(AppManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id)) throw new InvalidDataException("应用清单缺少 id");
        if (manifest.Id.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw new InvalidDataException("应用 id 只能包含字母、数字、点、横线和下划线");
        if (string.IsNullOrWhiteSpace(manifest.Name)) throw new InvalidDataException("应用清单缺少 name");
        if (string.IsNullOrWhiteSpace(manifest.Version)) throw new InvalidDataException("应用清单缺少 version");
        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly)) throw new InvalidDataException("应用清单缺少 entryAssembly");
        if (string.IsNullOrWhiteSpace(manifest.EntryType)) throw new InvalidDataException("应用清单缺少 entryType");
    }

    public void Dispose() => DisposeLoadedApplications();

    private void DisposeLoadedApplications()
    {
        foreach (var application in _loaded) application.Dispose();
        _loaded.Clear();
    }
}
