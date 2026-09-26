# Linux 无人值守被控

被控依赖当前用户已登录的 Wayland 桌面。注销、休眠或没有可采集输出时，不保证桌面远控可用。本实现不创建虚拟显示器。

被控启动、网络及屏幕采集的可恢复故障自动重试。每 3 秒检查输出拓扑，变化后释放旧会话并重新发现显示器；无选中输出时保持等待。屏幕选择按当前排序序号保留。桌面恢复后重新创建会话。手动停止被控保存 HostEnabled=false，重启不会自动开启；旧设置缺省为 true。

被控由星栈在后台初始化，因此星栈本身需要随桌面会话启动。安装器 `build/linux/install.sh` 提示的软链接即是最简单的自启动方式：

```bash
ln -s ~/.local/share/applications/AsterDock.desktop ~/.config/autostart/AsterDock.desktop
```

需要崩溃后自动拉起时，改用 systemd 用户服务，`ExecStart` 指向 `~/.local/opt/asterdock/<版本>/AsterDock.Host`，并设置 `Restart=always`、`RestartSec=5`；维护时用 `systemctl --user stop asterdock.service`。关闭主窗口在 Linux 上会结束进程，被控随之停止；无人值守场景请不要关闭主窗口。

诊断：`systemctl --user status asterdock.service`；用户数据与日志位于 `~/.local/share/AsterDock`。日志只记录事件及异常类型，不记录账号令牌或屏幕正文。

验证：Release 构建通过；尚未实测物理屏幕关机/拔插、注销后重新登录及整机重启。系统休眠不会因本服务被阻止。
