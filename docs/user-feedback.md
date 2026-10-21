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
