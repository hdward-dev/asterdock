using System.Runtime.InteropServices;

namespace NetworkAccelerator.Core.Services;

public interface ILinuxCommandResolver
{
    string? FindPkexec();

    string? FindSetcap();

    string? FindGetcap();

    bool HasPlainPkexec();
}

public interface ICapabilityXattrReader
{
    byte[]? ReadSecurityCapability(string path);
}

public interface IFileCapabilityPolicy
{
    bool HonorsFileCapabilities(string path);
}

public sealed class LinuxCommandResolver : ILinuxCommandResolver
{
    public string? FindPkexec() =>
        LinuxCommandPaths.ResolveSetuid(LinuxCommandPaths.PkexecPreferred, "pkexec", File.Exists, IsSetuid, PathEntries());

    public string? FindSetcap() => Resolve(LinuxCommandPaths.SetcapPreferred, "setcap");

    public string? FindGetcap() => Resolve(LinuxCommandPaths.GetcapPreferred, "getcap");

    public bool HasPlainPkexec() =>
        LinuxCommandPaths.FoundWithoutSetuid(LinuxCommandPaths.PkexecPreferred, "pkexec", File.Exists, IsSetuid, PathEntries());

    private static string? Resolve(IReadOnlyList<string> preferred, string name) =>
        LinuxCommandPaths.Resolve(preferred, name, File.Exists, PathEntries());

    private static bool IsSetuid(string path)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            return (File.GetUnixFileMode(path) & UnixFileMode.SetUser) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> PathEntries() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
}

public sealed class LinuxCapabilityXattrReader : ICapabilityXattrReader
{
    public byte[]? ReadSecurityCapability(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        return LinuxNativeXattr.Read(path, "security.capability");
    }
}

public sealed class LinuxMountCapabilityPolicy : IFileCapabilityPolicy
{
    public bool HonorsFileCapabilities(string path)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/mounts")) return true;
        var probe = File.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        return !LinuxMountOptions.IsNoSuid(Path.GetFullPath(probe), File.ReadAllLines("/proc/mounts"));
    }
}

internal static class LinuxNativeXattr
{
    public static byte[]? Read(string path, string name)
    {
        var size = getxattr(path, name, null, 0);
        if (size <= 0) return null;
        var buffer = new byte[size];
        var read = getxattr(path, name, buffer, (nuint)buffer.Length);
        if (read <= 0) return null;
        if (read < buffer.Length) Array.Resize(ref buffer, (int)read);
        return buffer;
    }

    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern long getxattr(string path, string name, byte[]? value, nuint size);
}
