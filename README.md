# HyperV-Tray

一个轻量级 Windows 系统托盘工具，监控本机 Hyper-V 虚拟机的运行状态。

## 功能

- 托盘图标常驻，按状态变色：Hyper-V 服务未启用显示**红色**，有虚拟机运行显示**绿色**，全部关闭显示**蓝色**
- 悬停可查看正在运行的虚拟机及全部关闭状态
- 右键菜单列出所有虚拟机，实时显示运行中虚拟机的 CPU、已使用/分配（动态内存显示"最大"）内存、运行时长，每秒自动刷新
- 每台虚拟机支持：
  - 使用 vmconnect 连接
  - 启动 / 优雅关闭虚拟机（超时自动强制关闭）
- 关闭全部虚拟机（带确认弹窗）
- 连接所有运行中的虚拟机
- 虚拟机启动/关闭时弹出文字气泡通知（无附加图标）
- 打开 ExHyperV 管理界面（存在时联动）；不存在时自动回退到系统 Hyper-V 管理器
- 支持开机自启（菜单开关，写入启动文件夹快捷方式）
- 单实例保护（全局 Mutex）
- 基于 WMI 事件订阅实时响应，空闲时零轮询、零 CPU 开销；另有 60 秒兜底刷新防止漏报

## 依赖

- Windows + Hyper-V 功能
- 当前用户具备 Hyper-V 管理员权限
- [ExHyperV](https://github.com/Justsenger/ExHyperV)（可选：存在时自动扫描并联动打开，不存在时回退系统 Hyper-V 管理器）

## 使用

单文件绿色版，无需安装。直接运行 `HyperV-Tray.exe` 即可；如需开机自启，可在托盘右键菜单勾选"开机自启"（写入启动文件夹快捷方式）。

## 原理

使用 `ManagementEventWatcher` 订阅 WMI 事件（`root\virtualization\v2` 命名空间的 `__InstanceModificationEvent` / `__InstanceCreationEvent` / `__InstanceDeletionEvent`，`TargetInstance ISA 'Msvm_ComputerSystem'`）。后台线程阻塞等待，虚拟机状态变化时由 WMI 推送通知，秒级更新托盘图标；空闲时进程零 CPU 占用。另设 60 秒兜底轮询避免事件订阅异常时漏报。启动/关闭通过 `Msvm_ShutdownComponent.InitiateShutdown`（优雅关机）与 `Msvm_ComputerSystem.RequestStateChange`（启动/强制关闭）实现。

## 构建

需要 .NET Framework 4.x（Windows 自带）：

```powershell
csc /nologo /target:winexe /win32icon:HyperV-Tray.ico /out:HyperV-Tray.exe /resource:green.ico,HyperVTray.Green /resource:red.ico,HyperVTray.Red /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.ServiceProcess.dll HyperV-Tray.cs
```

## 文件

- `HyperV-Tray.cs` — 源码
- `HyperV-Tray.exe` — 编译产物
- `HyperV-Tray.ico` — 应用图标（蓝色版）
- `green.ico` — 运行状态图标（绿色版，编译时嵌入）
- `red.ico` — 服务未启用图标（红色版，编译时嵌入）
