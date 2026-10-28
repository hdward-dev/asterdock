using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using AsterDock.Contracts;
using AsterDock.Host.Modules;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.VisualTree;

var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
if (args.Length == 0 || args[0] != "--worker")
{
    var source = args.Length > 0 ? Path.GetFullPath(args[0]) :
        Path.Combine(repository, "src/FileShare.Module/bin/Release/net10.0");
    var temporary = Path.Combine(Path.GetTempPath(), "asterdock-fileshare-checks-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporary);
    try
    {
        // Windows 在进程退出后才会解除所有程序集映射，由父进程统一清理检查目录。
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (var argument in new[] { Assembly.GetExecutingAssembly().Location, "--worker", source, temporary })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Environment.ExitCode = process.ExitCode;
    }
    finally { Directory.Delete(temporary, recursive: true); }
    return;
}
RunChecks(args[1], args[2]);

static void RunChecks(string source, string temporary)
{
    var package = Path.Combine(temporary, "package");
    Directory.CreateDirectory(package);
    if (File.Exists(source))
    {
        ZipFile.ExtractToDirectory(source, package);
    }
    else
    {
        // 模拟 package-apps.ps1：只保留模块私有文件，不携带宿主 UI 与原生资产。
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("AsterDock.") || name.StartsWith("Avalonia") || name.StartsWith("HarfBuzzSharp") ||
                name.StartsWith("Irihi.") || name.StartsWith("MicroCom.") || name.StartsWith("Semi.") ||
                name.StartsWith("SkiaSharp") || name.StartsWith("Tmds.") || name.StartsWith("Ursa") ||
                name.StartsWith("lib") || name.EndsWith(".pdb")) continue;
            File.Copy(file, Path.Combine(package, name));
        }
    }
    using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "app.json")));
    var entry = Path.Combine(package, manifest.RootElement.GetProperty("entryAssembly").GetString()!);
    var resolver = new AssemblyDependencyResolver(entry);
    foreach (var file in Directory.GetFiles(package, "Microsoft*.dll"))
    {
        var resolved = resolver.ResolveAssemblyToPath(AssemblyName.GetAssemblyName(file));
        // hostpolicy 解析 deps 时会 realpath。macOS 临时目录在 /var，指向 /private/var，
        // 字符串相等会把已经登记的程序集误报为未登记，且只报枚举到的第一个 DLL。
        Require(resolved != null && SamePath(resolved, file), "deps runtime 未登记 " + Path.GetFileName(file));
    }
    Require(Directory.GetFiles(package, "Microsoft.AspNetCore*.dll").Length > 0, "包内缺少 ASP.NET 实现程序集");
    Require(!AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name!.StartsWith("Microsoft.AspNetCore")),
        "检查宿主意外预加载了 ASP.NET");
    var unloaded = VerifyModule(entry, manifest.RootElement.GetProperty("entryType").GetString()!, temporary);
    for (var attempt = 0; unloaded.IsAlive && attempt < 10; attempt++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
    Console.WriteLine("文件共享检查全部通过。");
}

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static WeakReference VerifyModule(string entry, string entryType, string temporary)
{
    var context = new AppModuleLoadContext(entry);
    var unloaded = new WeakReference(context);
    try
    {
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var assembly = context.LoadFromAssemblyPath(entry);
        using var module = (IApplicationModule)Activator.CreateInstance(assembly.GetType(entryType, throwOnError: true)!)!;
        ((IApplicationContextAware)module).Initialize(new CheckContext(Path.Combine(temporary, "settings")));
        var first = module.CreateView();
        var second = module.CreateView();
        Require(!ReferenceEquals(first, second), "CreateView 必须每次返回新页面");
        var settings = first.GetType().GetProperty("Settings")!.GetValue(first)!;
        Require(ReferenceEquals(settings, second.GetType().GetProperty("Settings")!.GetValue(second)), "多窗口必须共用服务设置");
        var defaultRoot = (string)settings.GetType().GetProperty("Root")!.GetValue(settings)!;
        Require(Path.IsPathFullyQualified(defaultRoot) && !string.IsNullOrWhiteSpace(Path.GetFileName(defaultRoot)),
            "默认 Downloads 目录不是有效绝对路径");
        settings.GetType().GetProperty("Root")!.SetValue(settings, " ");
        settings.GetType().GetMethod("Normalize")!.Invoke(settings, null);
        Require((string)settings.GetType().GetProperty("Root")!.GetValue(settings)! == defaultRoot, "空目录未回退到 Downloads");
        var window = new Window { Content = first, Width = 1100, Height = 850 };
        window.Show();
        window.UpdateLayout();
        var toggle = first.GetVisualDescendants().OfType<ToggleButton>().First(t => t.Classes.Contains("share-switch-large"));
        toggle.ApplyTemplate();
        var track = toggle.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Track");
        var knob = toggle.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Knob");
        Require(track.Padding == new Thickness(7) && knob.Padding == new Thickness(0), "大号开关 Track 样式影响了 Knob padding");
        window.Content = second;
        window.UpdateLayout();
        Require(first.Parent == null, "页面切换未释放视觉父级");
        window.Close();
        Console.WriteLine("通过：新建页面、共享设置、Downloads 默认目录、Track/Knob padding");
        VerifyAddressChoice(assembly);

        var manager = assembly.GetType("FileShareApp.Module.FileShareApplicationModule")!
            .GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(module)!;
        var port = Task.Run(() => VerifyServiceAsync(manager, settings, temporary)).GetAwaiter().GetResult();
        module.Dispose();
        VerifyPortReleased(port);
        Require(!AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name!.StartsWith("Microsoft.AspNetCore")),
            "ASP.NET 泄漏到了默认加载上下文");
        Require(context.Assemblies.Any(a => a.GetName().Name == "Microsoft.AspNetCore.Server.Kestrel.Core"),
            "Kestrel 没有从模块隔离上下文加载");
        Console.WriteLine("通过：模块 Dispose 停止服务、释放端口，ASP.NET 保持隔离加载");
    }
    finally { context.Unload(); }
    return unloaded;
}

static async Task<int> VerifyServiceAsync(object manager, object settings, string temporary)
{
    var root = Path.Combine(temporary, "shared");
    Directory.CreateDirectory(root);
    await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "0123456789abcdef");
    Set("Root", root);
    var port = ReservePort();
    Set("Port", port);
    Set("PasswordEnabled", false);
    using var client = new HttpClient(new HttpClientHandler { UseCookies = false })
    { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
    await Start();
    using (var listing = await client.GetAsync("/"))
    {
        Require(listing.StatusCode == HttpStatusCode.OK, "目录页不是 200");
        Require((await listing.Content.ReadAsStringAsync()).Contains("sample.txt"), "嵌入目录模板未正确呈现");
    }
    await Stop();
    VerifyPortReleased(port);
    Set("PasswordEnabled", true);
    Set("Password", "check-access-key");
    await Start();
    using (var unauthorized = await client.GetAsync("/"))
        Require(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "未授权请求不是 401");
    using (var wrong = await client.GetAsync("/?ak=wrong"))
        Require(wrong.StatusCode == HttpStatusCode.Unauthorized, "错误 ak 未被拒绝");
    using (var authorized = await client.GetAsync("/?ak=check-access-key"))
        Require(authorized.StatusCode == HttpStatusCode.OK, "有效 ak 不是 200");
    using (var request = new HttpRequestMessage(HttpMethod.Get, "/sample.txt?ak=check-access-key"))
    {
        request.Headers.Range = new RangeHeaderValue(2, 5);
        using var response = await client.SendAsync(request);
        Require(response.StatusCode == HttpStatusCode.PartialContent, "Range 请求不是 206");
        Require(await response.Content.ReadAsStringAsync() == "2345", "Range 内容不正确");
        Require(response.Content.Headers.ContentRange?.ToString() == "bytes 2-5/16", "Content-Range 不正确");
    }
    using (var upload = await client.PutAsync("/file?name=blocked.txt&ak=check-access-key", new StringContent("blocked")))
        Require(upload.StatusCode == HttpStatusCode.Forbidden, "默认只读共享允许了上传");
    Require(!File.Exists(Path.Combine(root, "blocked.txt")), "被拒绝的上传仍写入了文件");
    await Stop();
    VerifyPortReleased(port);
    Set("AllowUpload", true);
    Require(!(bool)settings.GetType().GetProperty("ReadOnly")!.GetValue(settings)!, "允许上传时仍保持只读");
    await Start();
    using (var page = await client.GetAsync("/?ak=check-access-key"))
    {
        Require(page.StatusCode == HttpStatusCode.OK, "开启上传后目录页不是 200");
        Require((await page.Content.ReadAsStringAsync()).Contains("id=\"pick\""), "开启上传后网页没有上传按钮");
    }
    using (var upload = await client.PutAsync("/?name=uploaded.txt&ak=check-access-key", new StringContent("hello-upload")))
    {
        Require(upload.StatusCode == HttpStatusCode.OK, "开启上传后 PUT 不是 200");
        Require(await upload.Content.ReadAsStringAsync() == "OK 12", "上传响应不正确");
    }
    Require(await File.ReadAllTextAsync(Path.Combine(root, "uploaded.txt")) == "hello-upload", "上传内容不正确");
    await Stop();
    VerifyPortReleased(port);
    await Start();
    Console.WriteLine("通过：独立 ALC + ADR 启动、HTTP 200、鉴权 401、ak 200、Range 206、只读 403、允许上传、停止并重启");
    return port;

    void Set(string name, object value) => settings.GetType().GetProperty(name)!.SetValue(settings, value);
    Task Start() => (Task)manager.GetType().GetMethod("StartAsync")!.Invoke(manager, [settings, CancellationToken.None])!;
    Task Stop() => (Task)manager.GetType().GetMethod("StopAsync")!.Invoke(manager, null)!;
}

static int ReservePort()
{
    using var listener = new TcpListener(IPAddress.Any, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

static void VerifyPortReleased(int port)
{
    using var listener = new TcpListener(IPAddress.Any, port);
    listener.Server.ExclusiveAddressUse = true;
    listener.Start();
}

static void VerifyAddressChoice(Assembly assembly)
{
    var choiceType = assembly.GetType("FileShareApp.Module.Services.LanAddressChoice", throwOnError: true)!;
    var managerType = assembly.GetType("FileShareApp.Module.Services.ShareServerManager", throwOnError: true)!;
    var listType = typeof(List<>).MakeGenericType(choiceType);
    var choices = Activator.CreateInstance(listType)!;
    var add = listType.GetMethod("Add")!;
    add.Invoke(choices, [Choice("198.18.0.1", "FlClash", false)]);
    add.Invoke(choices, [Choice("192.168.50.13", "WLAN", true)]);
    var chosen = managerType.GetMethod("ChooseDefault")!.Invoke(null, [choices, null]);
    Require(AddressOf(chosen) == "192.168.50.13", "默认地址应优先局域网地址而不是虚拟网卡");
    var saved = managerType.GetMethod("ChooseDefault")!.Invoke(null, [choices, "198.18.0.1"]);
    Require(AddressOf(saved) == "198.18.0.1", "已保存的地址应被保留");
    var missing = managerType.GetMethod("ChooseDefault")!.Invoke(null, [choices, "10.0.0.8"]);
    Require(AddressOf(missing) == "192.168.50.13", "失效的已保存地址应回退到局域网地址");
    Console.WriteLine("通过：多网卡默认选择局域网地址，并保留用户选择");

    object Choice(string address, string adapter, bool gateway)
    {
        var choice = Activator.CreateInstance(choiceType)!;
        choiceType.GetProperty("Address")!.SetValue(choice, address);
        choiceType.GetProperty("Adapter")!.SetValue(choice, adapter);
        choiceType.GetProperty("HasGateway")!.SetValue(choice, gateway);
        return choice;
    }

    string? AddressOf(object? choice) => (string?)choiceType.GetProperty("Address")!.GetValue(choice);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static bool SamePath(string left, string right)
{
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    if (string.Equals(left, right, comparison)) return true;
    return !OperatingSystem.IsWindows() &&
        string.Equals(CanonicalPath(left), CanonicalPath(right), StringComparison.Ordinal);
}

static string CanonicalPath(string path)
{
    var full = Path.GetFullPath(path);
    if (File.Exists(full))
    {
        var link = File.ResolveLinkTarget(full, returnFinalTarget: true);
        if (link != null)
        {
            var linkDirectory = Path.GetDirectoryName(full)!;
            full = Path.IsPathFullyQualified(link.FullName)
                ? Path.GetFullPath(link.FullName)
                : Path.GetFullPath(Path.Combine(linkDirectory, link.FullName));
        }
    }
    var directory = Path.GetDirectoryName(full);
    var name = Path.GetFileName(full);
    if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return full;
    var canonical = CanonicalDirectory(directory);
    return string.IsNullOrEmpty(name) ? canonical : Path.Combine(canonical, name);
}

static string CanonicalDirectory(string path)
{
    var info = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
    var parts = new Stack<string>();
    var guard = 0;
    while (info.Parent != null)
    {
        if (++guard > 64) throw new InvalidOperationException("符号链接循环: " + path);
        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        if (target != null)
        {
            var full = Path.IsPathFullyQualified(target.FullName)
                ? target.FullName
                : Path.GetFullPath(Path.Combine(info.Parent.FullName, target.FullName));
            info = new DirectoryInfo(Path.TrimEndingDirectorySeparator(full));
            continue;
        }
        parts.Push(info.Name);
        info = info.Parent;
    }
    var result = info.FullName;
    foreach (var part in parts)
        result = Path.Combine(result, part);
    return result;
}

sealed class CheckContext(string directory) : IApplicationContext
{
    public string ApplicationId => "file-share";
    public string DataDirectory => directory;
    public IWindowService Windows => null!;
    public IApplicationShell Shell => null!;
    public ISystemMetricsService SystemMetrics => null!;
    public ILoadingService Loading => null!;
    public IUserFeedbackService Feedback => null!;
}
