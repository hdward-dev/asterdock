# 对话框与通知

轻应用通过 `IApplicationContext.Feedback`（`IUserFeedbackService`）显示对话框和右上角通知。调用可以来自任意线程，服务会切回界面线程。复杂的业务窗口（打印机选择、导入向导、订阅设置等）仍使用 `Windows.ShowDialogAsync`；容器会在这些窗口上挂上同一套反馈层。

## 对话框

| 方法 | 用途 |
| --- | --- |
| `ShowInfoAsync` / `ShowSuccessAsync` / `ShowWarningAsync` | 需要用户读完再继续的说明 |
| `ShowErrorAsync(title, message, exception)` | 失败。异常会放进可折叠的详情，并提供复制 |
| `ConfirmAsync(title, message, confirmText, cancelText, danger)` | 确认。主按钮返回 `true`，取消和 Esc 返回 `false`。不可恢复的操作把 `danger` 设为 `true` |

同一时刻只显示一个对话框，其余按顺序排队。

## 通知

```csharp
context.Feedback.Toast(FeedbackKind.Success, "配置已保存");
context.Feedback.Toast(FeedbackKind.Error, "发送失败，请重新连接", "串口");
```

信息、成功和警告会自动消失。错误默认保留到用户关闭。相同类型、标题和正文会合并，并显示重复次数。同时最多显示 4 条，多出来的排队。

## 异步事件

`async void` 事件处理不要自己吞掉异常：

```csharp
private void Save_Click(object? sender, RoutedEventArgs e) =>
    context.Feedback.Run(SaveAsync, "保存失败");
```

`Run` / `RunAsync` 会捕获异常（取消除外）并打开错误对话框。已有 `Exception` 时也可以：

```csharp
context.Feedback.Report("打开失败", exception);
context.Feedback.Notify(FeedbackKind.Warning, "请先选择串口");
```

## 什么时候用哪一种

- 会丢掉数据或改变系统状态的操作：`ConfirmAsync`，危险操作使用危险按钮。
- 带堆栈、用户必须知道的失败：`ShowErrorAsync`。
- 连接、发送这类会反复出现的失败：错误通知，页面上可以保留一行简短状态。
- “已连接”“正在加速”这类持续状态：留在页面里，不要弹窗。
- 正在进行的工作：继续用 `ILoadingService` 和 `AdLoadingOverlay`，不要用对话框表示忙碌。

## 系统通知

窗口正在前台，并且调用方所属的轻应用页面正打开时，提示仍显示在窗口里。否则自动改走操作系统通知：窗口最小化、失焦、收到托盘，或者那个轻应用页面没有被选中。插件不用改现有调用。

需要指定去向时，在原方法最后加上 `FeedbackDelivery`：

| 值 | 行为 |
| --- | --- |
| `Auto` | 前台且页面可见时只在窗口内显示，否则发系统通知 |
| `InApp` | 总是立即在窗口内显示 |
| `System` | 只发系统通知。系统不可用时，留到页面重新显示 |
| `Both` | 窗口内和系统通知都要。页面没打开时，窗口内的部分先排队 |

`ConfirmAsync` 永远留在窗口里，因为它要立刻返回选择。发到系统的错误在通知送出时就结束等待；用户点开通知后，星栈会回到前台、打开来源轻应用，并再次显示错误详情。8 秒内相同模块、类型、标题和正文只发一条。

设置里的「允许系统通知」默认开启。关掉，或者当前平台没有通知服务时，提示改在页面重新出现时显示，错误不会丢掉。后台事件（连接断开、订阅更新失败）可以显式传 `FeedbackDelivery.System`。

Linux 使用会话总线上的 `org.freedesktop.Notifications`（Tmds.DBus.Protocol）。应用名是「星栈」，`desktop-entry` 提示为 `asterdock`，并带上应用图标。KDE Plasma 6、NixOS、Wayland 经 XWayland 运行时协议不变；安装 `build/linux/asterdock.desktop` 后，桌面环境才能把 `asterdock` 对上这个程序。没有该服务时静默退回窗口内提示。

Windows 使用未打包应用的系统通知，应用用户模型 ID 是 `dev.hdward.asterdock`。点击由进程内的 COM 激活接收，标识 `{A7C4E1B2-5D83-4F06-9E21-6B8D0C4F2A19}`。

macOS 使用 `UNUserNotificationCenter`，首次使用时请求通知权限。包标识是 `dev.hdward.asterdock`（见 `build/macos/Info.plist`）。没有打成应用包的 `dotnet` 运行可能发不出系统通知，这时仍退回窗口内提示。
