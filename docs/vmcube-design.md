# VMCUBE 0.5.1：本机虚拟机与远程 KVM

VMCUBE 是星栈内的虚拟机轻应用，默认打开本机工作区，同时支持已有远程 KVM 主机。两种模式共用内置 SPICE/VNC 控制台。

## 界面与使用流程

- 参照 U远程采用左侧工作区/虚拟机详情、中间虚拟机卡片、右侧导航的三栏布局；右侧切换「我的虚拟机」「远程主机」「直接连接」「操作记录」「引擎与设置」。
- 本机模式：选择或安装 QEMU 引擎 → 创建虚拟机 → 配置名称、CPU、内存、磁盘、ISO、显示协议 → 启动 → 连接桌面。
- 远程模式：填写 `qemu+ssh://user@host/system`（支持自定义 SSH 端口和 `/session`）→ 连接 → 选择虚拟机 → 生命周期管理/连接桌面。
- 「直接连接桌面」可以独立连接 SPICE/VNC，不需要先添加主机。
- 沿用 U远程的青绿色强调色、圆角卡片、图标与字体，适配星栈亮暗主题。窄窗口收起左栏，详情改为中间可展开区域，右栏保留图标导航和提示。

## 0.5.1 卡片桌面预览与图标操作

- 控制台绘制新帧后，同步更新对应虚拟机卡片的桌面缩略图；复用已经打开的 SPICE/VNC 会话，不另开后台桌面连接。
- 预览按连接 URI 和虚拟机 ID 区分，最长边限制在 320×200 内，最多每 750 ms 更新一次，最多保留 32 个预览。画面仅在内存中保存，退出模块时释放。
- 连接期间标注「实时预览」与帧时间，断开后保留本次会话最后一帧并标注「最近画面」。搜索、筛选、切换布局后重新显示缓存画面。
- 卡片底部使用连接/启动/恢复、暂停、正常关机和详情图标，附悬浮提示及无障碍名称；侧栏生命周期按钮同样使用图标，关机确认仍显示明确文字。
- 立方体 LOGO 移至右侧 VMCUBE 名称上方；窄窗口同步缩小图标。
- UI 验证覆盖真实桌面同步到卡片、搜索后保留预览、断开/重连状态和图标提示。可用 `--existing-workspace <目录> --existing-console` 在私有临时配置副本中连接已运行来宾以验证实际预览；只关闭查看器，不停止来宾。

## 0.5.0 界面更新

- 中间卡片直接显示运行状态、vCPU、配置内存和本机控制台协议，提供启动/恢复/连接桌面。未连接时显示占位图；连接后显示从控制台收到的真实桌面缩略图，不伪造资源使用率。
- 支持双列卡片与单列列表切换、名称搜索、状态筛选，以及按本机/远程连接区分并持久保存的收藏。
- 左侧选中虚拟机提供连接、暂停/恢复、正常关机和原有强制关机确认；磁盘路径与 UUID 收入可展开详情。筛选切换选中虚拟机时清除旧控制台密码及待确认操作。
- 引擎检测、路径和演示模式收进设置页；直接连接成为独立表单，保留 SPICE TLS / CA 校验。日志独立成页，错误仍显示在底部状态栏，可查看完整提示。
- 界面操作失败保留上一次虚拟机列表，方便重试和查看日志。
- UI 自动验证覆盖页面导航、收藏落盘、卡片/列表、关机确认、亮暗/窄窗口布局，以及从卡片按钮进入真实临时 SPICE 虚拟机。

## 本机工作区

- 使用 QEMU 运行 x86_64 虚拟机，QMP 获取真实状态并执行暂停、恢复、正常关机；不使用 virsh，也不要求额外服务器。
- 根据平台优先尝试 Linux KVM、Windows WHPX 或 Intel macOS HVF，再回退到 TCG 软件模拟。ARM 主机当前运行 x86_64 软件模拟，尚无原生 ARM 来宾创建向导。
- `qemu-img` 创建按需占用空间的 qcow2；配置与磁盘保存在应用数据目录 `local/<UUID>/`。安装 ISO 由用户提供。
- 显示协议按引擎能力选择。部分平台 QEMU 发行版不带 SPICE 服务端，此时默认 VNC；远程 SPICE 客户端仍可使用。
- QMP 验证实例名称；Linux 运行记录用 PID、内核启动 tick 和 boot ID 验证进程身份，跨进程重新接管不依赖有微小漂移的墙钟时间。其他平台验证 PID 与进程启动时间。
- 关闭控制台、退出或卸载 VMCUBE 后，本机虚拟机会继续运行；再次进入可以重新接管。应用不持有长期后台读流或永久进程句柄。
- 正常关机通过来宾 ACPI 请求完成；强制关机需要页内明确确认。停止后可以移除安装 ISO，虚拟磁盘保留。
- 新建虚拟机默认 x86_64 BIOS/SATA/e1000；现有 qcow2 可以导入并指定原有 UEFI/TPM。完整硬件编辑和删除向导尚未实现。

## 导入已有虚拟机

- 点击「导入现有虚拟机」，选择 qcow2，填写原有 CPU、内存及硬件设置；也可以读取 `.vmcube.json` 配置。配置文件只包含固定字段，不执行 Python/shell 启动脚本，不接受任意 QEMU 附加参数。
- 直接使用原有磁盘，不创建替代磁盘、不格式化、不重新安装系统。UEFI CODE 和可写 NVRAM 必须成对指定；TPM 必须使用包含 `tpm2-00.permall` 的已有状态目录以及原有 swtpm，不会初始化替代 TPM。
- 支持原 QEMU 可执行文件、KVM/host CPU、本地时钟、Secure Boot、SATA 磁盘、e1000/e1000e/virtio 网卡、固定 MAC、std/qxl/virtio 显卡及原有 Guest Agent Unix socket。这里的导入面向 q35/x86_64；其他机器类型或复杂设备拓扑暂不支持。
- 导入和启动时用 `qemu-img info` 只读检查磁盘；不使用强制共享选项，QEMU 保留自身写锁。工作区锁防止并发重复导入/启动；已注册磁盘会拒绝重复导入。请先正常关闭原启动入口的虚拟机。
- Linux swtpm 使用原有状态目录并启用锁，控制 socket 放在用户 runtime 目录或工作区私有目录，仅当前用户可访问；不会删除仍有监听者的 socket。QEMU 退出时 swtpm 随控制通道关闭而退出；启动失败时清理新建的进程。
- Unix 启动的 QEMU/swtpm 输出写入各虚拟机私有目录的 `qemu.log` / `swtpm.log`；启动失败会在界面显示具体错误和日志路径。使用固定 shell 程序重定向，全部可变参数通过参数列表和环境传递，没有后台托管读流。
- 与新建虚拟机一样，可启动、暂停/恢复、正常关机、连接内置 SPICE/VNC，退出应用后重新接管。导入不依赖 libvirt 或 virsh。
- 工作区注册表使用 `FormatVersion: 2` 包装，兼容读取旧数组格式并在进入工作区时迁移。旧模块不能把导入配置误当成应用自建磁盘；更新后需退出旧星栈进程并重新打开，页面会显示当前模块版本。

配置示例（相对路径按配置文件目录解析；可选字段可省略）：

```json
{
  "Name": "Windows 11",
  "Cpus": 4,
  "MemoryMiB": 8192,
  "DiskPath": "state/windows.qcow2",
  "QemuPath": "/usr/bin/qemu-system-x86_64",
  "FirmwareCodePath": "/usr/share/OVMF/OVMF_CODE.fd",
  "NvramPath": "state/OVMF_VARS.fd",
  "TpmStateDirectory": "state/tpm",
  "SwtpmPath": "/usr/bin/swtpm",
  "SecureBoot": true,
  "CpuHost": true,
  "RequireKvm": true,
  "UseLocalTime": true,
  "NetworkModel": "e1000e",
  "VideoModel": "std",
  "MacAddress": "52:54:00:12:34:56",
  "UserNicHostForwards": ["tcp:127.0.0.1:8000-:8000"]
}
```

`UserNicHostForwards` 可选：用户态网卡（`-nic user`）的 QEMU `hostfwd` 列表。每项为 `tcp|udp:[host:]hostport-:guestport`，例如 `tcp:127.0.0.1:8000-:8000` 将本机回环 8000 转到虚拟机 8000（可用于 WindowsMCP）。

## 远程管理

- 通过系统 OpenSSH 调用目标主机的 virsh，本机无需安装 virsh。目标主机应已有 libvirt/virsh、SSH 密钥登录与主机信任配置。
- 服务器就是运行虚拟机的那台电脑，不是 VMCUBE 提供的云服务；本机模式完全不依赖远程主机。
- SSH 启用严格主机密钥校验，禁止交互密码提示。远端 shell 仅接收固定命令、已验证 UUID 与固定 libvirt URI，用户输入不拼进远程命令。
- 查询列表、CPU/内存、域 XML；支持启动、正常关机、暂停/恢复。不提供远程强制关机按钮。

## 内置控制台

- VNC 复用 U远程托管 RFB 3.3/3.7/3.8 实现，支持无认证或传统 VNC 密码认证。
- SPICE 使用 spice-client-glib，在隔离原生工作进程解码画面；Avalonia 自行绘制 BGRA，不依赖外部查看器或 GTK 窗口。
- 支持主显示器画面、32/16 位像素格式、分辨率变化、键盘扫描码、鼠标、滚轮、全屏与 Ctrl+Alt+Del。Ctrl+Alt+Esc 释放输入。
- SPICE 直接连接支持 TLS 和可选 CA 文件，保留服务器证书校验。TLS-only 远程控制台应使用证书匹配主机名直接连接，不把 TLS 校验降级绕过。
- 远程 TCP 控制台可自动走仅绑定本机回环的 SSH 隧道；关闭连接时终止隧道和工作进程。
- 密码只在内存中使用，SPICE 密码经 stdin 传递，不放进命令行；本机 QEMU 的随机控制台凭据存于私有虚拟机目录。
- 本版不提供 SPICE 音频、剪贴板、USB/文件重定向、多显示器和 GL scanout。Unix socket 控制台需改为 TCP 监听；不支持的配置会提示。

## 0.3.1 引擎检测修复

- 进入本机工作区时检测内置引擎、系统 PATH、NixOS 系统/用户 profile，并显示“QEMU 已就绪”；悬停状态可查看实际二进制路径。
- 引擎按钮先检查已有 QEMU 与 qemu-img，找到后直接使用，不再误走网上下载安装。
- 修复“本机已有内置引擎，点击安装却提示正式发布没有引擎资源”的误导流程。

## 引擎与打包

- `scripts/build-vmcube-native.sh` 构建 SPICE 工作进程；`scripts/bundle-vmcube-native.py` 收集共享库、可选 QEMU 引擎和 x86 BIOS 固件。
- 原生文件放在 `Native/bin/<RID>/`，模块构建复制到 `native/<RID>/`。
- 官方通用应用包包含各平台 SPICE 客户端；较大的 QEMU 引擎拆成 `VMCUBE-QEMU-<RID>.zip`，在应用内按需安装，避免超出容器包体限制。
- 引擎安装只接受官方 AsterDock Release 资产，验证 GitHub 提供的 SHA-256，限制下载/解压大小与路径；安装到应用数据目录，不修改系统。
- 尚未发布对应引擎资产时，安装按钮会说明原因；仍可选择已有 QEMU 引擎目录。当前本地 Linux 开发包直接包含引擎。
- CI 配置包含 Linux x64/arm64、macOS x64/arm64、Windows x64 原生构建。跨平台构建配置不等于已在目标系统实测；本次实测平台为 Linux x64。

## 验证

- `dotnet run --project tests/VMCube.Checks`：参数、解析、命令白名单、SSH、归档路径与 VNC 回环测试。
- 加上 `--spice-helper <路径> --qemu <路径>`：启动本机临时 QEMU，测试真实 SPICE 密码认证、画面和键鼠，以及本机 qcow2/生命周期/重新接管。
- 再加 `--uefi-code <CODE> --uefi-vars <VARS 模板> --swtpm <程序>`：使用临时固件副本和新建测试 TPM 验证 UEFI/TPM 导入、原磁盘不变、运行中磁盘拒绝、SPICE 画面、独立 .NET 进程重新接管、重复启动保护、启动失败具体诊断与重启后 TPM 清理。
- `tests/VMCube.UiChecks`：在虚拟显示环境打开实际 Avalonia 页面，验证选择、搜索、筛选及亮暗/窄窗口布局并生成截图。

参考：[QEMU QMP](https://www.qemu.org/docs/master/interop/qemu-qmp-ref.html)、[libvirt virsh](https://www.libvirt.org/manpages/virsh.html)、[SPICE API](https://www.spice-space.org/api/spice-gtk/api-reference.html)。
