using System.Diagnostics;

namespace InvoicePrinter.Module.Services;

public sealed record PrinterInfo(string Name, bool IsDefault)
{
    public string DisplayName => IsDefault ? $"{Name}（默认）" : Name;
}

internal static class CupsPrinterListing
{
    // macOS bundles often have a minimal PATH. Linux, including NixOS, must resolve lpstat from PATH.
    public static string LpstatCommand => OperatingSystem.IsMacOS() ? "/usr/bin/lpstat" : "lpstat";

    public static (ProcessStartInfo Destinations, ProcessStartInfo DefaultDestination) CreateDiscoveryStartInfos() =>
        (CreateLpstatStartInfo(LpstatCommand, "-e"), CreateLpstatStartInfo(LpstatCommand, "-d"));

    public static ProcessStartInfo CreateLpstatStartInfo(string command, string arguments)
    {
        var info = new ProcessStartInfo(command, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ApplyCLocale(info.Environment);
        return info;
    }

    public static IReadOnlyList<PrinterInfo> Parse(string destinationNames, string defaultDestinationOutput)
    {
        var defaultName = ParseDefaultName(defaultDestinationOutput);
        return destinationNames.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Select(name => new PrinterInfo(name, string.Equals(name, defaultName, StringComparison.Ordinal)))
            .OrderByDescending(item => item.IsDefault)
            .ThenBy(item => item.Name)
            .ToList();
    }

    // `lpstat -e` prints one destination name per line. `lpstat -d` is a translated sentence;
    // CUPS 2.4 zh_CN uses "系统默认目标：%s" (fullwidth colon).
    public static string? ParseDefaultName(string output)
    {
        var separatorIndex = Math.Max(output.LastIndexOf(':'), output.LastIndexOf('：'));
        if (separatorIndex < 0) return null;
        var name = output[(separatorIndex + 1)..].Trim();
        return name.Length == 0 ? null : name;
    }

    // cupsLangGet treats a C libc locale as unset, then prefers LC_MESSAGES over LC_ALL and LANG.
    private static void ApplyCLocale(IDictionary<string, string?> environment)
    {
        environment["LC_ALL"] = "C";
        environment["LC_MESSAGES"] = "C";
        environment["LANG"] = "C";
        environment["LANGUAGE"] = "C";
    }
}
