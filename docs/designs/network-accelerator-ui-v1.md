# 网络加速应用 UI 与交互设计 v1

设计图：`network-accelerator-ui-v1.png`

## 应用定位

- 应用 ID：`network-accelerator`
- 应用名称：网络加速
- 核心：sing-box
- 目标平台：Windows、macOS，后续可扩展 Linux
- 主要能力：订阅管理、规则/全局/直连模式、TUN、节点切换、自动测速、运行状态与日志

## 首屏布局

1. **连接状态**：连接/断开、当前节点、延迟、连接时长和上下行速率。
2. **代理方式**：使用分段控件切换“规则 / 全局 / 直连”。
3. **TUN 模式**：独立开关，首次启用时引导安装或授权平台辅助组件。
4. **节点选择**：手动节点、自动选择、搜索、协议筛选、延迟及质量状态。
5. **订阅信息**：订阅名称、更新时间、剩余流量、到期时间、更新与管理入口。
6. **底部状态**：sing-box 健康状态、已加载规则数量和日志入口。

## 模式语义

| 模式 | 行为 |
| --- | --- |
| 规则 | 局域网和指定规则直连，其余流量交给当前 `selector` 节点；规则使用远程二进制 rule-set。 |
| 全局 | 除必要的本机、局域网和代理服务器防回环规则外，所有流量交给当前节点。 |
| 直连 | 保留 sing-box 运行状态，但业务流量使用 `direct`，方便快速排障。 |

## sing-box 映射

- TUN 使用 `tun` inbound，开启 `auto_route`；Windows 可配合 `strict_route` 降低多网卡 DNS 泄漏风险。
- 手动节点组使用 `selector` outbound；UI 通过只监听 `127.0.0.1` 的 Clash API 切换节点。
- 自动选择使用 `urltest` outbound，界面展示最近一次延迟测试结果。
- 规则模式使用 `route.rules`、远程 `rule_set` 和明确的 `route.final`。
- Clash API 必须使用随机密钥，并禁止监听公网地址。

## 交互规则

- 模式、TUN 或节点发生变化时，先生成候选配置并执行 `sing-box check`，校验成功后再热切换或重启核心。
- 节点切换默认不中断已有连接；设置页可提供“切换后断开旧连接”。
- 更新订阅失败时继续使用最后一次有效配置，不覆盖可用节点。
- 开启 TUN 前显示权限状态；权限不足时不进入假连接状态。
- 连接失败时在状态卡显示简短原因，完整内容进入脱敏日志。
- 容器退出或核心异常时恢复系统代理、DNS 和路由状态。

## 核心安装

- 首次使用可直接在“订阅信息”卡片点击“安装核心”。
- 当前固定使用稳定版 `1.13.12`，按 `windows-amd64`、`darwin-amd64` 或 `darwin-arm64` 自动选择官方包。
- 下载地址只允许 SagerNet 官方 GitHub Release，并校验 Release API 对应资产提供的 SHA-256 digest。
- 核心安装到应用数据目录的 `core` 子目录；也兼容应用包内 `core` 目录或系统 `PATH` 中已有的 sing-box。
- Windows/macOS 开启 TUN 仍可能需要系统管理员权限；权限失败时页面保持“未连接”并显示核心错误。
- Windows 主容器不整体提权；仅在开始 TUN 时启动 `AsterDock.NetworkElevatedHost` 并显示 UAC。辅助进程负责启动 sing-box、写入脱敏日志并监听停止信号，因此停止连接不需要第二次授权。

## 建议工程拆分

```text
NetworkAccelerator.Core/
├─ Configuration/       # sing-box 配置模型、合并与校验
├─ Engine/              # 核心进程、健康检查、Clash API
├─ Subscription/        # 订阅下载、解析、缓存与更新
├─ Routing/             # 规则模式、全局模式、rule-set
└─ Security/            # 凭据保护与日志脱敏

NetworkAccelerator.Module/
├─ Views/               # Avalonia 页面、对话框
├─ ViewModels/
├─ Models/
├─ app.json
└─ NetworkAcceleratorApplicationModule.cs
```

## 安全要求

- 订阅地址、认证信息和 Clash API 密钥不得写入普通日志。
- Windows 使用 DPAPI、macOS 使用 Keychain 保存敏感数据。
- sing-box 管理 API 只绑定环回地址。
- 下载的订阅和 rule-set 必须限制大小、超时、重定向次数并执行格式校验。
- 不把订阅内容拼接为命令行字符串；通过结构化参数和受控配置文件启动核心。

## 官方参考

- https://sing-box.sagernet.org/configuration/inbound/tun/
- https://sing-box.sagernet.org/configuration/outbound/selector/
- https://sing-box.sagernet.org/configuration/outbound/urltest/
- https://sing-box.sagernet.org/configuration/route/
- https://sing-box.sagernet.org/configuration/experimental/clash-api/
