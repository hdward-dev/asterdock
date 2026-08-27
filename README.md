<p align="center">
  <img src="docs/assets/asterdock-lockup.svg" alt="星栈 AsterDock" width="450">
</p>

# AsterDock（星栈）

基于 .NET 10 与 Avalonia 12 的 Windows/macOS 跨平台应用容器。外壳只负责窗口、导航和模块加载，当前内置主页、发票打印助手、设备信息、网络加速、Android 投屏与串口调试六个独立应用模块。

- 发票打印助手：PDF/图片导入、A4 每页两张预览、虚线分隔及直接打印；打印或手动触发后自动识别每张小票的类型（如 A 类）、金额与编号，支持导出 CSV。
- 设备信息：CPU、GPU、内存、磁盘与网络状态，支持桌面右上角透明常驻监控窗。
- 网络加速：使用 sing-box JSON 订阅，支持规则/全局/直连、TUN 模式、节点切换与延迟测试。
- 主页：常用应用、最近使用、设备概览和容器快捷操作。
- Android 投屏：通过 scrcpy 投屏并控制已开启 USB 调试的 Android 设备，支持 USB 与 TCP/IP 连接，并可从官方 Release 自动下载核心。
- 串口调试：支持多串口平铺、独立收发、HEX/文本模式、循环发送、快捷指令和双端口联调编排。

容器提供系统托盘入口。关闭主窗口时程序隐藏到托盘；通过托盘可以重新打开星栈、显示或隐藏设备监控窗，选择“退出”才会结束进程。

应用开发与打包要求请参阅：[星栈应用开发规范](docs/application-development-guide.md)。

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10/11，或受支持的 macOS 版本
- Windows TUN 模式需要在启动加速时允许一次 UAC 提权
- macOS 正式分发需要 Apple Developer 签名与公证

项目不要求预先安装 sing-box。网络加速应用可以在界面内下载与当前平台和架构匹配的核心，并校验官方 Release 资产提供的 SHA-256 摘要。

## 工程结构

```text
asterdock/
├─ src/
│  ├─ AsterDock.Contracts/  # 容器与应用之间的稳定契约
│  ├─ AsterDock.UI/         # Semi/Ursa 主题、设计令牌与共享控件
│  ├─ AsterDock.Host/       # Avalonia 容器外壳与模块加载器
│  ├─ AsterDock.NetworkElevatedHost/ # Windows TUN 最小权限辅助进程
│  ├─ AndroidScreen.Module/      # 基于 scrcpy 的 Android 投屏与控制
│  ├─ DeviceInformation.Module/  # 设备信息 UI 与透明悬浮窗
│  ├─ DeviceInformation.Core/    # 容器共享的跨平台硬件采集实现
│  ├─ Home.Module/               # 默认主页与容器工作台
│  ├─ InvoicePrinter.Module/     # 可独立加载/打包的发票应用
│  ├─ InvoicePrinter.Core/       # PDF 解析与 A4 排版
│  ├─ NetworkAccelerator.Module/ # sing-box 网络加速 UI 模块
│  ├─ NetworkAccelerator.Core/   # 订阅、配置、核心进程与 Clash API
│  └─ SerialDebugger.Module/     # 多串口调试与联调工作区
├─ scripts/
└─ AsterDock.slnx
```

## 运行容器

```powershell
dotnet run --project src/AsterDock.Host/AsterDock.Host.csproj
```

首次运行前可以恢复并构建整个解决方案：

```powershell
dotnet restore AsterDock.slnx
dotnet build AsterDock.slnx -c Release --no-restore
```

仓库不会提交 `bin`、`obj`、`artifacts`、运行日志、用户配置、订阅内容或本地下载的 sing-box 核心。

从旧版“应用中心”升级时，星栈会在首次访问相应目录时，将 `%LOCALAPPDATA%\ApplicationHub`（macOS 上对应旧的 Application Support 目录）中的用户应用和模块数据复制到新的 `AsterDock` 目录。旧目录不会被删除，可用于回退。

构建宿主时，内置应用模块会分别复制到 `Apps/Home`、`Apps/InvoicePrinter`、`Apps/DeviceInformation`、`Apps/NetworkAccelerator`、`Apps/AndroidScreen` 和 `Apps/SerialDebugger`。设备信息应用只依赖容器契约；硬件采集实现由宿主统一持有。设备信息应用的核心输出结构为：

```text
AsterDock.Host/bin/<Configuration>/net10.0/Apps/DeviceInformation/
├─ app.json
├─ DeviceInformation.Module.dll
└─ AsterDock.Contracts.dll
```

宿主不引用具体应用模块，只扫描 `Apps` 目录下的 `app.json`，再通过独立 `AssemblyLoadContext` 加载入口程序集。主页、设备信息页和桌面悬浮窗通过容器提供的同一个 `ISystemMetricsService` 订阅设备数据；首个订阅者启动采集，全部退订 8 秒后自动休眠。

界面基线由 `AsterDock.UI` 统一提供：宿主加载 Semi.Avalonia 与 Ursa Semi 主题，模块通过共享控件、`App*` 设计令牌和 `ad-*` 语义样式复用页面标题、卡片、分段按钮、状态徽标、紧凑输入等外观。页面只保留业务专属样式。

网络加速应用首次使用时，可在页面内点击“安装核心”。应用会从 SagerNet 官方 Release 下载与当前 Windows/macOS 架构匹配的 sing-box 稳定版，并使用 GitHub Release 资产自带的 digest 校验 SHA-256；核心安装在该应用的数据目录，不写入容器安装目录。订阅输入支持 HTTPS 地址或本地 sing-box JSON 配置文件。

Windows 开启 TUN 后，在“开始加速”时会弹出一次 UAC。应用容器本身保持普通权限，只有独立的网络辅助进程和 sing-box 获得管理员权限；停止加速通过受控信号完成，不会再次弹出 UAC。

## 应用包

开发阶段可以直接放置应用文件夹；分发阶段可以生成 `.appbundle`（ZIP 格式）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/package-invoice-app.ps1
```

```bash
bash scripts/package-invoice-app.sh
```

把应用目录或 `.appbundle` 放入用户应用目录后重启容器：

- Windows：`%LOCALAPPDATA%\AsterDock\Apps`
- macOS：`~/Library/Application Support/AsterDock/Apps`

应用包会经过路径与大小检查，安全解包到用户缓存后加载。

## macOS 容器

```bash
bash scripts/package-macos.sh osx-arm64
bash scripts/package-macos.sh osx-x64
```

产物位于 `artifacts/macos/<RID>/星栈.app`。正式分发前仍需 Apple Developer 证书签名和公证。
