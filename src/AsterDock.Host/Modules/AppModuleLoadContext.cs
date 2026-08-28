using AsterDock.Contracts;
using System.Reflection;
using System.Runtime.Loader;

namespace AsterDock.Host.Modules;

internal sealed class AppModuleLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public AppModuleLoadContext(string entryAssemblyPath)
        : base($"AppModule:{Path.GetFileNameWithoutExtension(entryAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsSharedAssembly(assemblyName.Name))
            return Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    private static bool IsSharedAssembly(string? name) =>
        string.Equals(name, typeof(IApplicationModule).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "AsterDock.UI", StringComparison.OrdinalIgnoreCase) ||
        name?.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) == true ||
        string.Equals(name, "HarfBuzzSharp", StringComparison.OrdinalIgnoreCase) ||
        name?.StartsWith("Irihi.", StringComparison.OrdinalIgnoreCase) == true ||
        string.Equals(name, "MicroCom.Runtime", StringComparison.OrdinalIgnoreCase) ||
        name?.StartsWith("Ursa", StringComparison.OrdinalIgnoreCase) == true ||
        name?.StartsWith("Semi.", StringComparison.OrdinalIgnoreCase) == true ||
        string.Equals(name, "SkiaSharp", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Tmds.DBus.Protocol", StringComparison.OrdinalIgnoreCase);
}
