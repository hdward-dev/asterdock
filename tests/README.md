# Tests

自动化测试项目放在此目录。接入 PDF 渲染层后，优先覆盖文件过滤、两张一页分页和打印布局计算。

## 宿主轻应用生命周期

`dotnet run --project tests/AsterDock.Host.Checks -p:BundleApplications=false`

无界面验证运行中列表、后台初始化、关闭角标、资源释放、独立窗口关闭、返回主页和重新启动。测试请使用临时数据目录（Linux 可设置 `XDG_DATA_HOME`），不启动真实轻应用。
