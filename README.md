# HyperV-Tray

一个轻量级 Windows 系统托盘工具，监控本机 Hyper-V 虚拟机的运行状态。

## 功能

- 有虚拟机运行时在托盘显示图标，全部关闭后自动隐藏
- 悬停图标查看正在运行的虚拟机
- 右键菜单列出所有虚拟机，支持：
  - 打开 ExHyperV 管理界面
  - 优雅关闭虚拟机（超时自动强制关闭）
  - 启动虚拟机
- 双击图标打开 ExHyperV
- 基于 WMI 事件订阅实时响应，空闲时零轮询、零 CPU 开销；另有 60 秒兜底刷新防止漏报

## 依赖

- Windows + Hyper-V 功能
- 当前用户具备 Hyper-V 管理员权限
- [ExHyperV](https://github.com/Justsenger/ExHyperV)（点击联动打开的管理界面）

## 使用

直接运行 `HyperV-Tray.exe`，或将快捷方式放入 `shell:startup` 实现开机自启。

## 原理

使用 `ManagementEventWatcher` 订阅 WMI 事件（`root\virtualization\v2` 命名空间的 `__InstanceModificationEvent` / `__InstanceCreationEvent` / `__InstanceDeletionEvent`，`TargetInstance ISA 'Msvm_ComputerSystem'`）。后台线程阻塞等待，虚拟机状态变化时由 WMI 推送通知，秒级更新托盘图标；空闲时进程零 CPU 占用。另设 60 秒兜底轮询避免事件订阅异常时漏报。启动/关闭通过 `Msvm_ShutdownComponent.InitiateShutdown`（优雅关机）与 `Msvm_ComputerSystem.RequestStateChange`（启动/强制关闭）实现。

## 构建

需要 .NET Framework 4.x（Windows 自带）：

```powershell
csc /nologo /target:winexe /win32icon:HyperV-Tray.ico /out:HyperV-Tray.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll HyperV-Tray.cs
```

## 文件

- `HyperV-Tray.cs` — 源码
- `HyperV-Tray.exe` — 编译产物
- `HyperV-Tray.ico` — 应用图标
