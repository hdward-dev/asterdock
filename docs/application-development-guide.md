# 星栈应用开发规范

本文档定义星栈的模块开发、清单、UI、生命周期、目录和打包规范。当前实现基于 .NET 10、Avalonia 12.1 和独立 `AssemblyLoadContext`。

## 1. 基本原则

- 容器外壳只负责窗口、应用发现、导航、安装和加载。
- 应用必须独立成 .NET 类库，不能引用 `AsterDock.Host`。
- 应用只能通过 `AsterDock.Contracts` 与容器建立编译期关系。
- 应用主界面必须是 Avalonia `Control`，推荐使用 `UserControl`。
- 应用必须释放自身创建的资源、后台任务和事件订阅。
- 应用应同时兼容 Windows 和 macOS；平台专用功能必须显式保护。

## 2. 推荐工程结构

```text
MyApplication.Module/
├─ MyApplication.Module.csproj
├─ app.json
├─ MyApplicationModule.cs
├─ Models/
├─ Services/
├─ ViewModels/
├─ Views/
│  ├─ MyApplicationView.axaml
│  └─ MyApplicationView.axaml.cs
└─ Assets/
```

应用逻辑较多时，可以另外建立不依赖 Avalonia 的 Core 项目：

```text
MyApplication.Core/       业务逻辑、数据处理
MyApplication.Module/     Avalonia UI 和容器入口
```

## 3. 项目文件

应用模块目标框架必须为 `net10.0`。建议开启 `CopyLocalLockFileAssemblies`，确保第三方依赖和本机运行库能够进入应用目录。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>MyApplication.Module</RootNamespace>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AsterDock.Contracts\AsterDock.Contracts.csproj" />
    <PackageReference Include="Avalonia" Version="12.1.0" />
  </ItemGroup>

  <ItemGroup>
    <None Update="app.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

要求：

- 不得引用 `AsterDock.Host`。
- Avalonia 版本必须与容器保持一致。
- 使用原生库时，必须把对应平台文件放入 `runtimes/<RID>/native/`。
- 不要把其他版本的 `AsterDock.Contracts.dll` 当作私有 API 使用。

## 4. 应用入口

入口类必须：

- 实现 `IApplicationModule`。
- 需要容器服务时同时实现 `IApplicationContextAware`。
- 提供公开的无参数构造函数。
- 通过 `CreateView()` 返回应用主视图。
- 在 `Dispose()` 中释放资源。

```csharp
using AsterDock.Contracts;
using Avalonia.Controls;
using MyApplication.Module.Views;

namespace MyApplication.Module;

public sealed class MyApplicationModule : IApplicationModule, IApplicationContextAware
{
    private MyApplicationView? _view;
    private IApplicationContext? _context;

    public void Initialize(IApplicationContext context)
        => _context = context;

    public Control CreateView()
        => _view ??= new MyApplicationView(_context!);

    public void Dispose()
    {
        _view?.DisposeResources();
        _view = null;
        _context = null;
    }
}
```

容器会先调用 `Initialize()`，再调用 `CreateView()`，并缓存返回的控件。应用不应在每次显示时重复创建昂贵资源。旧应用可以只实现 `IApplicationModule`，因此新增上下文接口不会破坏现有应用。

## 5. app.json 清单

应用根目录必须包含 `app.json`：

```json
{
  "id": "my-application",
  "name": "我的应用",
  "description": "应用功能说明",
  "version": "1.0.0",
  "entryAssembly": "MyApplication.Module.dll",
  "entryType": "MyApplication.Module.MyApplicationModule",
  "icon": "app",
  "category": "工具",
  "order": 20
}
```

字段说明：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | 是 | 全局唯一应用标识，只使用字母、数字、`.`、`-`、`_` |
| `name` | 是 | 应用显示名称 |
| `description` | 否 | 设置页和应用选择器中的功能说明 |
| `version` | 是 | 建议使用语义化版本，例如 `1.2.0` |
| `entryAssembly` | 是 | 入口程序集文件名，必须位于应用根目录内 |
| `entryType` | 是 | 实现 `IApplicationModule` 的完整类型名 |
| `icon` | 否 | 图标类型；当前支持 `home`、`printer`、`monitor`、`network`，其他值使用默认应用图标 |
| `category` | 否 | 悬浮应用选择器中的分类，默认是“工具” |
| `order` | 否 | 显示顺序，数字越小越靠前 |

同一 `id` 存在多个应用时，容器选择版本号更高的一个。

## 6. 发布目录

构建后的应用目录必须自包含运行所需的托管依赖和本机依赖：

```text
MyApplication/
├─ app.json
├─ MyApplication.Module.dll
├─ MyApplication.Module.deps.json
├─ AsterDock.Contracts.dll
├─ ThirdParty.Dependency.dll
├─ Assets/
└─ runtimes/
   ├─ win-x64/native/
   ├─ osx-x64/native/
   └─ osx-arm64/native/
```

`app.json` 和 `entryAssembly` 指定的 DLL 必须位于应用根目录。容器不会搜索 `bin/Debug` 等开发目录。

## 7. UI 规范

- 主视图使用 `UserControl`，不要替换容器的主窗口。
- 应用需要打开普通窗口或模态窗口时，优先使用 `IApplicationContext.Windows`，不要自行修改 `ApplicationLifetime.MainWindow`。

### 7.1 窗口服务

容器通过 `IWindowService` 统一管理应用创建的窗口。被管理的窗口会在模块卸载、重新加载或容器退出时自动关闭。

打开普通的所属窗口：

```csharp
var window = new MyToolWindow();
_context.Windows.Show(window);
```

打开不隶属于容器主窗口的独立窗口：

```csharp
_context.Windows.Show(new MyIndependentWindow(), owned: false);
```

打开模态窗口并取得返回值：

```csharp
var accepted = await _context.Windows.ShowDialogAsync<bool>(
    new MyConfirmDialog());
```

避免重复打开同一种窗口：

```csharp
_context.Windows.ShowOrActivate(
    "main-editor",
    () => new MyEditorWindow());
```

同一个 `key` 已存在时，容器会激活原窗口；窗口关闭后，该 `key` 可以再次创建窗口。

`IWindowService` 提供：

| 方法 | 用途 |
| --- | --- |
| `Show(window, owned)` | 打开并跟踪普通窗口 |
| `ShowOrActivate(key, factory, owned)` | 打开或激活单例窗口 |
| `ShowDialogAsync<TResult>(window)` | 打开模态窗口并返回结果 |
| `CloseAll()` | 关闭当前应用通过服务打开的全部窗口 |

### 7.2 容器导航与应用状态

容器通过 `IApplicationContext.Shell` 向应用提供受控导航。应用不得通过查找主窗口控件或反射 Host 类型来切换页面。

| 成员 | 用途 |
| --- | --- |
| `Applications` | 当前已加载应用的只读摘要 |
| `RecentApplications` | 当前容器会话中的最近使用记录 |
| `StateChanged` | 应用列表或最近使用发生变化 |
| `OpenApplication(id)` | 按应用 ID 打开应用 |
| `ShowSettings()` | 打开容器设置工作区 |
| `ShowApplicationSwitcher()` | 显示悬浮应用选择器 |
| `TryExecuteApplicationAction(appId, actionId)` | 执行其他应用公开的快捷动作 |

```csharp
_context.Shell.OpenApplication("invoice-printer");
_context.Shell.ShowSettings();
_context.Shell.ShowApplicationSwitcher();
```

订阅 `StateChanged` 的应用必须在 `Dispose()` 中退订。不要长期保存 `ApplicationSummary`；应用重新加载后应从 `Applications` 重新读取。

### 7.3 容器共享的设备指标

CPU、GPU、内存、磁盘和网络指标必须使用 `IApplicationContext.SystemMetrics`。应用不得自行创建硬件采集器或重复启动轮询定时器。

```csharp
private IDisposable? _metricsSubscription;

public void Activate()
{
    _metricsSubscription ??= _context.SystemMetrics.Subscribe(snapshot =>
    {
        Dispatcher.UIThread.Post(() => CpuUsage = snapshot.CpuUsage);
    });
}

public void Deactivate()
{
    Interlocked.Exchange(ref _metricsSubscription, null)?.Dispose();
}
```

容器只运行一个底层采集器：

- 第一个订阅者出现时按需启动，每秒生成一份共享快照。
- 新订阅者可以先收到最近一次缓存快照。
- 最后一个订阅者退订后等待 8 秒；期间没有新订阅者才停止采集并释放系统计数器。
- 页面离开可视树、悬浮窗关闭或模块释放时必须退订。
- 订阅回调来自后台线程；更新 Avalonia 控件或视图模型时必须切换到 UI 线程。
- 设备静态信息通过 `GetDeviceDetailsAsync()` 获取，由容器缓存，不应反复查询系统。
- 应用只能依赖 Contracts 中的 `ISystemMetricsService`，不得引用 Host 的具体实现。

### 7.4 托盘快捷动作

应用需要把常用功能发布到容器托盘时，可以选择实现 `IApplicationQuickActionProvider`。容器仍然只依赖 Contracts，不会引用具体应用程序集。

```csharp
public sealed class MyApplicationModule :
    IApplicationModule,
    IApplicationContextAware,
    IApplicationQuickActionProvider
{
    public IReadOnlyList<ApplicationQuickAction> GetQuickActions() =>
    [
        new ApplicationQuickAction(
            "toggle-widget",
            "显示/隐藏悬浮窗",
            ToggleWidget)
    ];
}
```

要求：

- 动作 `id` 在当前应用内必须唯一且保持稳定。
- 动作显示名称应简短明确。
- 动作由 Avalonia UI 线程调用，不得执行阻塞操作。
- 动作创建的窗口仍必须通过 `IApplicationContext.Windows` 打开。
- 应用卸载时必须关闭动作创建的窗口并释放委托引用。
- 容器可以选择展示哪些动作；实现接口不代表动作一定出现在所有平台的托盘菜单中。

- 文件和目录选择必须使用 Avalonia `StorageProvider`：

```csharp
var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
```

- 优先使用 Avalonia FluentTheme 已有组件，例如 `Button`、`MenuFlyout`、`ListBox`、`TabControl`、`ToggleSwitch`、`NumericUpDown` 和 `ProgressBar`。
- 应用主视图必须适应容器尺寸变化，避免依赖固定窗口位置。
- 耗时工作必须异步执行，不得阻塞 Avalonia UI 线程。

容器提供以下动态资源：

| 资源 | 用途 |
| --- | --- |
| `AppPageBrush` | 页面背景 |
| `AppSurfaceBrush` | 卡片和主要表面 |
| `AppChromeBrush` | 标题和工具区域 |
| `AppBorderBrush` | 边框和分隔线 |
| `AppMutedBrush` | 次要文字 |
| `AppPreviewBrush` | 预览区域背景 |

使用示例：

```xml
<Border Background="{DynamicResource AppSurfaceBrush}"
        BorderBrush="{DynamicResource AppBorderBrush}"
        BorderThickness="1"/>
```

### 7.5 微应用内部导航

微应用存在详情页、编辑页等内部页面时，可以选择实现 `IApplicationNavigationProvider`。宿主会在右上角子胶囊中自动显示返回按钮：

```csharp
public bool CanGoBack => _navigationStack.Count > 1;
public event EventHandler? NavigationStateChanged;

public void GoBack()
{
    if (!CanGoBack) return;
    _navigationStack.Pop();
    NavigationStateChanged?.Invoke(this, EventArgs.Empty);
}
```

要求：

- 内部页面层级发生变化后触发 `NavigationStateChanged`。
- `GoBack()` 只处理微应用内部导航，不得切换宿主应用。
- 无内部页面时 `CanGoBack` 返回 `false`。
- 模块释放后不得继续触发导航事件。

## 8. 生命周期与资源释放

应用被替换、重新加载或容器关闭时会调用 `Dispose()`。容器会在模块释放时关闭通过 `IWindowService` 创建的窗口。应用仍必须释放：

- `Bitmap`、文件流和数据库连接。
- `CancellationTokenSource` 和后台任务。
- 定时器、文件监听器和操作系统句柄。
- 对容器、窗口或静态对象的事件订阅。
- 应用创建但仍保持引用的临时文件。

避免使用无法卸载的全局静态状态。否则即使模块使用可回收 `AssemblyLoadContext`，程序集仍可能无法卸载。

## 9. 数据目录

不要把用户数据或配置写入应用安装目录。实现 `IApplicationContextAware` 后，使用容器分配的数据目录：

```csharp
var dataRoot = _context.DataDirectory;
```

容器按应用 `id` 创建数据目录：`AsterDock/AppData/<application-id>`。

临时输出使用 `Path.GetTempPath()`，并在不再需要时清理。

## 10. 跨平台规范

共享逻辑应保持平台无关。平台专用代码必须显式判断：

```csharp
if (OperatingSystem.IsWindows())
{
    // Windows 实现
}
else if (OperatingSystem.IsMacOS())
{
    // macOS 实现
}
```

要求：

- 不使用 WPF、WinForms 或仅 Windows 可用的 UI 类型。
- Windows P/Invoke 必须位于 Windows 分支之后。
- macOS 外部命令使用参数列表传参，不拼接未转义的用户输入。
- 发布前至少验证 `win-x64`、`osx-x64` 和 `osx-arm64`。

## 11. 应用加载位置

容器会扫描两类位置。

内置应用：

```text
<容器目录>/Apps/
```

用户应用：

- Windows：`%LOCALAPPDATA%\AsterDock\Apps`
- macOS：`~/Library/Application Support/AsterDock/Apps`

用户可以在设置页加载应用目录或 `.appbundle` 文件。

## 12. .appbundle 规范

`.appbundle` 是 ZIP 格式的应用包。压缩包根目录必须直接包含 `app.json`，不能再嵌套一层应用文件夹。

正确：

```text
MyApplication.appbundle
├─ app.json
├─ MyApplication.Module.dll
└─ runtimes/...
```

错误：

```text
MyApplication.appbundle
└─ MyApplication/
   ├─ app.json
   └─ MyApplication.Module.dll
```

当前安全限制：

- 解压后总大小不超过 512 MB。
- 文件条目不超过 2000 个。
- 禁止路径穿越。
- 应用包解压到用户缓存后再加载。

可以参考项目脚本生成发票应用包：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/package-invoice-app.ps1
```

```bash
bash scripts/package-invoice-app.sh
```

## 13. 构建与验证

```powershell
dotnet build src/MyApplication.Module/MyApplication.Module.csproj
```

检查输出目录至少包含：

- `app.json`
- 入口 DLL
- `.deps.json`
- 所有第三方托管依赖
- 目标平台需要的 `runtimes` 文件

然后通过星栈设置页加载目录或 `.appbundle`，验证：

1. 应用出现在“已加载的应用”列表。
2. 描述、版本、分类和图标正确。
3. 点击应用 Logo 可以打开主视图。
4. `Space + A` 应用选择器中可以找到并打开应用。
5. 重新加载或退出后没有文件锁和后台任务残留。

## 14. 发布检查清单

- [ ] 目标框架是 `net10.0`
- [ ] 仅引用 `AsterDock.Contracts`，未引用 Host
- [ ] Avalonia 版本与容器一致
- [ ] 入口类实现 `IApplicationModule`
- [ ] 需要窗口或数据目录时实现 `IApplicationContextAware`
- [ ] 入口类具有公开无参数构造函数
- [ ] `app.json` 位于应用根目录
- [ ] `id` 唯一且格式合法
- [ ] `version` 已更新
- [ ] 描述、分类和显示顺序已填写
- [ ] 入口 DLL 和所有依赖已复制
- [ ] Windows/macOS 本机运行库完整
- [ ] UI 不阻塞主线程
- [ ] 新窗口通过 `IWindowService` 打开并由容器跟踪
- [ ] 容器导航仅通过 `IApplicationContext.Shell` 完成
- [ ] 硬件指标仅通过 `IApplicationContext.SystemMetrics` 订阅，并在不可见或释放时退订
- [ ] 已在 `Dispose()` 中退订 `Shell.StateChanged`
- [ ] 托盘快捷动作 ID 唯一且执行过程不阻塞 UI
- [ ] `Dispose()` 能释放所有资源
- [ ] `.appbundle` 根目录结构正确
- [ ] 已完成 Windows 和 macOS 验证

## 15. 参考实现

当前标准参考应用：

- `src/InvoicePrinter.Module`
- `src/InvoicePrinter.Module/app.json`
- `src/InvoicePrinter.Module/InvoicePrinterApplicationModule.cs`
- `src/DeviceInformation.Core`（跨平台硬件采集和平台能力降级）
- `src/DeviceInformation.Module`（主视图和独立透明悬浮窗）
- `src/Home.Module`（默认主页、容器导航和设备概览）

容器契约与加载器：

- `src/AsterDock.Contracts`
- `src/AsterDock.Contracts/IApplicationContext.cs`
- `src/AsterDock.Host/Modules/ModuleCatalog.cs`
- `src/AsterDock.Host/Modules/AppModuleLoadContext.cs`
- `src/AsterDock.Host/Modules/AppPackageExtractor.cs`
- `src/AsterDock.Host/Services/ApplicationWindowService.cs`
