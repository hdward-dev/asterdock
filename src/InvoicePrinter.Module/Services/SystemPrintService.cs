using System.Diagnostics;
using System.Text.Json;

namespace InvoicePrinter.Module.Services;

public sealed class SystemPrintService
{
    public async Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            var json = await RunCaptureAsync("powershell.exe", "-NoProfile -Command \"Get-CimInstance Win32_Printer | Select-Object Name,Default | ConvertTo-Json -Compress\"");
            if (string.IsNullOrWhiteSpace(json)) return [];
            using var document = JsonDocument.Parse(json);
            IEnumerable<JsonElement> items = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : [document.RootElement];
            return items.Select(item => new PrinterInfo(item.GetProperty("Name").GetString() ?? "未知打印机", item.TryGetProperty("Default", out var value) && value.GetBoolean())).OrderByDescending(item => item.IsDefault).ThenBy(item => item.Name).ToList();
        }

        // `lpstat -p` status lines are translated ("打印机 …" under zh_CN), so a "printer " prefix lists nothing.
        // `lpstat -e` is one destination name per line. The default sentence is read in the C locale.
        var (destinations, defaultDestination) = CupsPrinterListing.CreateDiscoveryStartInfos();
        var printerOutput = await RunCaptureAsync(destinations);
        var defaultOutput = await RunCaptureAsync(defaultDestination);
        return CupsPrinterListing.Parse(printerOutput, defaultOutput);
    }

    public async Task PrintAsync(string path, string? printerName = null)
    {
        if (OperatingSystem.IsMacOS())
        {
            await RunPrintCommandAsync("/usr/bin/lp", printerName is null ? [path] : ["-d", printerName, path]);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            await RunPrintCommandAsync("lp", printerName is null ? [path] : ["-d", printerName, path]);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            if (string.IsNullOrWhiteSpace(printerName))
                throw new InvalidOperationException("请选择打印机");

            await WindowsPdfPrintService.PrintAsync(path, printerName);
            return;
        }

        throw new PlatformNotSupportedException("当前系统暂不支持直接打印");
    }

    public void OpenDocument(string path)
    {
        if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("open", path) { UseShellExecute = false });
        else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static async Task RunPrintCommandAsync(string command, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动系统打印服务");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "打印服务返回失败" : error.Trim());
    }

    private static Task<string> RunCaptureAsync(string command, string arguments) =>
        RunCaptureAsync(new ProcessStartInfo(command, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

    private static async Task<string> RunCaptureAsync(ProcessStartInfo info)
    {
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法读取打印机列表");
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 ? output : string.Empty;
    }
}
