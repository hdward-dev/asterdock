using InvoicePrinter.Module.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static PrinterInfo[] Listed(string names, string defaultOutput) =>
    CupsPrinterListing.Parse(names, defaultOutput).ToArray();

static void CheckList(PrinterInfo[] actual, params (string Name, bool IsDefault)[] expected)
{
    Check(actual.Length == expected.Length,
        $"expected {expected.Length} printers, got {actual.Length}: {string.Join(", ", actual.Select(item => item.Name))}");
    for (var i = 0; i < expected.Length; i++)
    {
        Check(actual[i].Name == expected[i].Name, $"printer {i} was {actual[i].Name}, expected {expected[i].Name}");
        Check(actual[i].IsDefault == expected[i].IsDefault, $"default flag for {actual[i].Name}");
    }
}

const string queues = "Office_Laser\nEPSON_L4260_Series\n\nEPSON_L4260_Series\n";

CheckList(Listed(queues, "system default destination: EPSON_L4260_Series\n"),
    ("EPSON_L4260_Series", true),
    ("Office_Laser", false));
Check(Listed(queues, "system default destination: EPSON_L4260_Series\n")[0].DisplayName == "EPSON_L4260_Series（默认）",
    "default printer keeps the Chinese display suffix");

// CUPS 2.4 zh_CN: msgid "system default destination: %s" -> "系统默认目标：%s".
CheckList(Listed(queues, "系统默认目标：EPSON_L4260_Series\n"),
    ("EPSON_L4260_Series", true),
    ("Office_Laser", false));
CheckList(Listed("EPSON_L4260_Series/photo\n", "系统默认目标：EPSON_L4260_Series/photo\n"),
    ("EPSON_L4260_Series/photo", true));

CheckList(Listed(queues, "no system default destination\n"),
    ("EPSON_L4260_Series", false),
    ("Office_Laser", false));
CheckList(Listed(queues, "无系统默认目标\n"),
    ("EPSON_L4260_Series", false),
    ("Office_Laser", false));
CheckList(Listed(queues, "system default destination:\n"),
    ("EPSON_L4260_Series", false),
    ("Office_Laser", false));

CheckList(Listed("zeta\r\nHP LaserJet 1020\r\nalpha\r\n", "system default destination: HP LaserJet 1020\r\n"),
    ("HP LaserJet 1020", true),
    ("alpha", false),
    ("zeta", false));
CheckList(Listed("Office_Laser\n", "destino predeterminado del sistema: Missing_Printer\n"),
    ("Office_Laser", false));
Check(Listed("", "system default destination: EPSON_L4260_Series\n").Length == 0, "empty lpstat -e output lists nothing");

// The old Linux parser kept lines that start with "printer ". zh_CN `lpstat -p` does not.
const string chineseStatus = "打印机 EPSON_L4260_Series 目前空闲。从 Tue 27 Oct 2026 08:00:00 AM CST 开始启用\n系统默认目标：EPSON_L4260_Series\n";
Check(!chineseStatus.Split('\n').Any(line => line.StartsWith("printer ", StringComparison.Ordinal)),
    "zh_CN lpstat -p lines do not start with the English printer prefix");

var previous = new Dictionary<string, string?>();
foreach (var name in new[] { "LANG", "LC_ALL", "LC_MESSAGES", "LANGUAGE", "PRINTER" })
    previous[name] = Environment.GetEnvironmentVariable(name);
try
{
    Environment.SetEnvironmentVariable("LANG", "zh_CN.UTF-8");
    Environment.SetEnvironmentVariable("LC_ALL", "zh_CN.UTF-8");
    Environment.SetEnvironmentVariable("LC_MESSAGES", "zh_CN.UTF-8");
    Environment.SetEnvironmentVariable("LANGUAGE", "zh_CN:en");
    Environment.SetEnvironmentVariable("PRINTER", "EPSON_L4260_Series");

    var (destinations, defaultDestination) = CupsPrinterListing.CreateDiscoveryStartInfos();
    Check(destinations.FileName == CupsPrinterListing.LpstatCommand, "destination listing uses the platform lpstat command");
    Check(OperatingSystem.IsMacOS() ? destinations.FileName == "/usr/bin/lpstat" : destinations.FileName == "lpstat",
        "macOS keeps /usr/bin/lpstat; other systems resolve lpstat from PATH");
    Check(destinations.Arguments == "-e" && defaultDestination.Arguments == "-d",
        "discovery must be lpstat -e plus lpstat -d, not localized lpstat -p text");
    Check(defaultDestination.FileName == destinations.FileName, "default query uses the same lpstat binary");
    foreach (var info in new[] { destinations, defaultDestination })
    {
        Check(!info.UseShellExecute && info.RedirectStandardOutput && info.RedirectStandardError, "lpstat stdout must be captured");
        Check(info.Environment["LC_ALL"] == "C", "LC_ALL must be C when the desktop is zh_CN");
        Check(info.Environment["LC_MESSAGES"] == "C", "LC_MESSAGES must be C; CUPS prefers it over LC_ALL");
        Check(info.Environment["LANG"] == "C", "LANG must be C when the desktop is zh_CN");
        Check(info.Environment["LANGUAGE"] == "C", "LANGUAGE must not stay zh_CN");
        Check(info.Environment["PRINTER"] == "EPSON_L4260_Series", "locale override must keep PRINTER");
    }
}
finally
{
    foreach (var (name, value) in previous)
        Environment.SetEnvironmentVariable(name, value);
}

Console.WriteLine("CUPS printer listing checks passed.");
