using System.Runtime.InteropServices;

namespace AndroidScreen.Module.Services;

public static class ScreenRuntime
{
    public static string? FindAdb(string moduleDirectory)
    {
        var name = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        var directories = new List<string> { moduleDirectory };
        directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        // Finder-launched macOS applications do not inherit the shell's Homebrew PATH.
        if (OperatingSystem.IsMacOS()) directories.AddRange(["/opt/homebrew/bin", "/usr/local/bin"]);
        foreach (var directory in directories.Distinct())
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static (string Adb, string Server) ResolveCore(string scrcpy)
    {
        var root = Path.GetDirectoryName(scrcpy)!;
        string Find(string name) => Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException($"投屏核心缺少 {name}，请重新安装核心。");
        var systemAdb = OperatingSystem.IsLinux() ? FindOnPath("adb") : null;
        var adb = systemAdb ?? Find(OperatingSystem.IsWindows() ? "adb.exe" : "adb");
        var server = Find("scrcpy-server");
        if (systemAdb is null) MakeExecutable(adb);
        return (adb, server);
    }

    public static string ResolveDecoder(string moduleDirectory)
    {
        // Distribution tools carry the correct loader and dependencies (notably on NixOS).
        if (OperatingSystem.IsLinux() && FindOnPath("ffmpeg") is { } systemDecoder) return systemDecoder;
        var rid = OperatingSystem.IsWindows() ? "win-x64" :
            OperatingSystem.IsMacOS() ? RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64" :
            OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "linux-x64" :
            throw new PlatformNotSupportedException("应用内投屏目前支持 Windows、macOS 和 Linux x64。");
        var path = Path.Combine(moduleDirectory, "ffmpeg", rid, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        if (!File.Exists(path)) throw new FileNotFoundException("应用内视频解码组件缺失，请重新安装 Android 投屏模块。", path);
        MakeExecutable(path);
        return path;
    }

    private static string? FindOnPath(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            if (!OperatingSystem.IsWindows() &&
                (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0) continue;
            return path;
        }
        return null;
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if (!mode.HasFlag(UnixFileMode.UserExecute)) File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
        }
    }
}
