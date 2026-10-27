using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace NetworkAccelerator.Core.Services;

public enum LinuxTunDecision
{
    UseSource,
    UseCapableCopy,
    CopyAndSetcap,
    LaunchWithPkexec,
    PkexecMissing,
    SetuidWrapperMissing
}

public enum LinuxPkexecOutcome
{
    Success,
    Cancelled,
    Failed,
    SetuidWrapperMissing
}

public readonly record struct LinuxTunFacts(
    bool CopyExists,
    bool CopyHasNetAdmin,
    bool HashMatches,
    bool SourceHasNetAdmin,
    bool SetcapAvailable,
    bool PkexecAvailable,
    bool FilesystemHonorsFileCapabilities,
    bool PlainPkexecExists = false);

public static class LinuxTunAuthorization
{
    public const string CapabilityArguments = "cap_net_admin,cap_net_bind_service+ep";
    public const int CapNetAdminBit = 12;

    public static LinuxTunDecision Decide(LinuxTunFacts facts)
    {
        if (facts.CopyExists && facts.CopyHasNetAdmin && facts.HashMatches)
            return LinuxTunDecision.UseCapableCopy;
        if (facts.SourceHasNetAdmin)
            return LinuxTunDecision.UseSource;
        if (!facts.PkexecAvailable)
            return facts.PlainPkexecExists ? LinuxTunDecision.SetuidWrapperMissing : LinuxTunDecision.PkexecMissing;
        if (facts.SetcapAvailable && facts.FilesystemHonorsFileCapabilities)
            return LinuxTunDecision.CopyAndSetcap;
        return LinuxTunDecision.LaunchWithPkexec;
    }

    public static LinuxPkexecOutcome ClassifyPkexec(int exitCode, string? standardError, bool started)
    {
        if (!started) return LinuxPkexecOutcome.Failed;
        if (exitCode == 0) return LinuxPkexecOutcome.Success;
        var error = standardError ?? string.Empty;
        if (RequiresSetuidWrapper(error)) return LinuxPkexecOutcome.SetuidWrapperMissing;
        if (Contains(error, "not found") || Contains(error, "No such file") || Contains(error, "没有那个文件"))
            return LinuxPkexecOutcome.Failed;
        if (exitCode is 126 or 127
            || Contains(error, "dismissed")
            || Contains(error, "not authorized")
            || Contains(error, "cancelled")
            || Contains(error, "canceled")
            || Contains(error, "取消")
            || Contains(error, "拒绝"))
            return LinuxPkexecOutcome.Cancelled;
        return LinuxPkexecOutcome.Failed;
    }

    public static bool OutputHasNetAdmin(string? text) =>
        text?.Contains("cap_net_admin", StringComparison.Ordinal) == true;

    public static bool RequiresSetuidWrapper(string? text) =>
        Contains(text ?? string.Empty, "setuid") || Contains(text ?? string.Empty, "enablePkexecWrapper");

    public static bool XattrHasNetAdmin(ReadOnlySpan<byte> value)
    {
        if (value.Length < 12) return false;
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(value) & 0xFF000000;
        if (revision is not (0x01000000 or 0x02000000 or 0x03000000)) return false;
        var permitted = BinaryPrimitives.ReadUInt32LittleEndian(value[4..]);
        return (permitted & (1u << CapNetAdminBit)) != 0;
    }

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}

public static class LinuxCommandPaths
{
    public static readonly string[] PkexecPreferred = ["/run/wrappers/bin/pkexec"];
    public static readonly string[] SetcapPreferred = ["/run/current-system/sw/bin/setcap"];
    public static readonly string[] GetcapPreferred = ["/run/current-system/sw/bin/getcap"];

    public static string? Resolve(
        IEnumerable<string> preferred,
        string name,
        Func<string, bool> exists,
        IEnumerable<string> pathDirectories)
    {
        foreach (var candidate in preferred)
        {
            if (exists(candidate)) return candidate;
        }

        foreach (var directory in pathDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim(), name);
            if (exists(candidate)) return candidate;
        }

        return null;
    }

    public static string? ResolveSetuid(
        IEnumerable<string> preferred,
        string name,
        Func<string, bool> exists,
        Func<string, bool> isSetuid,
        IEnumerable<string> pathDirectories)
    {
        foreach (var candidate in Candidates(preferred, name, pathDirectories))
        {
            if (exists(candidate) && isSetuid(candidate)) return candidate;
        }

        return null;
    }

    public static bool FoundWithoutSetuid(
        IEnumerable<string> preferred,
        string name,
        Func<string, bool> exists,
        Func<string, bool> isSetuid,
        IEnumerable<string> pathDirectories)
    {
        var saw = false;
        foreach (var candidate in Candidates(preferred, name, pathDirectories))
        {
            if (!exists(candidate)) continue;
            if (isSetuid(candidate)) return false;
            saw = true;
        }

        return saw;
    }

    private static IEnumerable<string> Candidates(
        IEnumerable<string> preferred,
        string name,
        IEnumerable<string> pathDirectories)
    {
        foreach (var candidate in preferred) yield return candidate;
        foreach (var directory in pathDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            yield return Path.Combine(directory.Trim(), name);
        }
    }
}

public static class LinuxMountOptions
{
    public static bool IsNoSuid(string path, IEnumerable<string> mountLines)
    {
        var full = path.EndsWith(Path.DirectorySeparatorChar) ? path.TrimEnd(Path.DirectorySeparatorChar) : path;
        if (full.Length == 0) full = Path.DirectorySeparatorChar.ToString();
        string? best = null;
        var nosuid = false;
        foreach (var line in mountLines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            var mount = Unescape(parts[1]);
            if (!IsUnder(full, mount)) continue;
            if (best is not null && mount.Length <= best.Length) continue;
            best = mount;
            nosuid = parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Any(option => option.Equals("nosuid", StringComparison.Ordinal));
        }

        return nosuid;
    }

    private static bool IsUnder(string path, string mount)
    {
        if (mount is "/" or "") return true;
        mount = mount.TrimEnd('/');
        return path.Equals(mount, StringComparison.Ordinal)
               || path.StartsWith(mount + "/", StringComparison.Ordinal);
    }

    private static string Unescape(string value) =>
        Regex.Replace(value, @"\\([0-7]{3})", match => ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString());
}
