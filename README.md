# HyperV-Tray

一个轻量级 Windows 系统托盘工具，监控本机 Hyper-V 虚拟机的运行状态。

## 功能

- 托盘图标常驻，按状态变色：Hyper-V 服务未启用显示**红色**，有虚拟机运行显示**绿色**，有已保存虚拟机显示**黄色**，全部关闭显示**蓝色**
- 悬停可查看正在运行的虚拟机及全部关闭状态
- 托盘菜单列出所有虚拟机，实时显示运行中虚拟机的 CPU、已使用/分配（动态内存显示"最大"）内存、运行时长，每秒自动刷新
- 每台虚拟机按状态支持：
  - 运行中：连接虚拟机 / 保存虚拟机状态 / 优雅关闭虚拟机（超时自动强制关闭）
  - 已保存：连接虚拟机 / 恢复虚拟机 / 销毁保存的虚拟机（带确认）
  - 已暂停：连接虚拟机 / 恢复虚拟机
  - 已关闭：连接虚拟机 / 启动虚拟机
  - 启动中/停止中/暂停中等过渡状态：仅显示状态与连接，避免误操作
- 批量操作（带确认弹窗，完成后仅弹一条汇总通知）：
  - 关闭全部虚拟机
  - 保存全部虚拟机
  - 恢复所有保存的虚拟机
  - 销毁所有保存的虚拟机
- 连接所有运行中的虚拟机
- 单击或右键点击托盘图标均弹出管理菜单（Win32 原生菜单，系统原生外观，自动跟随系统深色主题）
- 高 DPI 支持（PerMonitorV2）：高分屏 125%/150%/200% 缩放下菜单与托盘图标按原生分辨率渲染，不模糊
- 虚拟机启动/已保存/关闭时弹出文字气泡通知（无附加图标）
- 支持开机自启（菜单开关，写入启动文件夹快捷方式）
- 单实例保护（全局 Mutex，异常退出后自动接管，无需重启系统即可再次启动）
- WMI 查询失败时托盘变红并提示"查询失败"，与"无虚拟机"明确区分
- 基于 WMI 事件订阅实时响应，空闲时零轮询、零 CPU 开销；另有 60 秒兜底刷新防止漏报

## 依赖

- Windows + Hyper-V 功能
- 当前用户具备 Hyper-V 管理员权限

## 使用

单文件绿色版，无需安装。直接运行 `HyperV-Tray.exe` 即可；如需开机自启，可在托盘菜单勾选"开机自启"（写入启动文件夹快捷方式）。

在无 Hyper-V 的环境可用 `HyperV-Tray.exe --test-notify` 发送测试通知（不依赖 Hyper-V，发送后自动退出）。

## 调试

设置环境变量 `HYPERV_TRAY_DEBUG=1` 后运行，程序会把关键异常写入 `%TEMP%\HyperV-Tray.log`（带时间戳与来源标记），用于排查 WMI 查询、虚拟机操作（含批量操作/关机/状态查询）、后台事件订阅等失败原因。正常使用无需开启。

## 原理

使用 `ManagementEventWatcher` 订阅 WMI 事件（`root\virtualization\v2` 命名空间的 `__InstanceModificationEvent` / `__InstanceCreationEvent` / `__InstanceDeletionEvent`，`TargetInstance ISA 'Msvm_ComputerSystem'`）。后台线程阻塞等待，虚拟机状态变化时由 WMI 推送通知，秒级更新托盘图标；空闲时进程零 CPU 占用。另设 60 秒兜底轮询避免事件订阅异常时漏报。运行中虚拟机的 CPU / 内存 / 运行时长通过一次 `Msvm_SummaryInformation` 查询（`ProcessorLoad` / `MemoryUsage` / `UpTime`）批量获取，查询结果带 1.5 秒缓存，右键菜单即时弹出不卡顿。启动/关闭通过 `Msvm_ShutdownComponent.InitiateShutdown`（优雅关机）与 `Msvm_ComputerSystem.RequestStateChange`（启动/强制关闭）实现；保存(6)/恢复(2)/销毁(3)亦通过 `RequestStateChange` 实现。

## 构建

需要 .NET Framework 4.x（Windows 自带）：

```powershell
csc /nologo /target:winexe /win32icon:HyperV-Tray.ico /out:HyperV-Tray.exe /resource:green.ico,HyperVTray.Green /resource:yellow.ico,HyperVTray.Yellow /resource:red.ico,HyperVTray.Red /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.ServiceProcess.dll HyperV-Tray.cs
```

## 文件

- `HyperV-Tray.cs` — 源码
- `HyperV-Tray.exe` — 编译产物
- `HyperV-Tray.ico` — 应用图标（蓝色版）
- `green.ico` — 运行状态图标（绿色版，编译时嵌入）
- `yellow.ico` — 已保存状态图标（黄色版，编译时嵌入）
- `red.ico` — 服务未启用图标（红色版，编译时嵌入）
