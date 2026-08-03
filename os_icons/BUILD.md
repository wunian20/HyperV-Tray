# os_icons 生成说明

本目录的 `.ico` 文件用于 HyperV-Tray 通知气泡中的操作系统图标，来源为
[ExHyperV](https://github.com/Justsenger/ExHyperV) 的 `src\Assets\VectorIcons.xaml`
（`Vector.{OS}` DrawingImage 矢量图标），与 VM Notes 的 `[OSType:xxx]` 标签对应。

生成流程（一次性，需 WPF 运行时）：

1. `git clone` ExHyperV，取得 `src\Assets\VectorIcons.xaml`。
2. 用 WPF 渲染工具将每个 `Vector.{OS}` 按 16/32/48/256px 四个尺寸渲染为 PNG 帧，
   打包成标准 `.ico`（文件名即 OS 类型，如 `Kali.ico`）。

编译时通过 `/resource:<file>,HyperVTray.OsIcons.{OS}` 嵌入程序集；
运行时按 VM 的 OS 类型从 `GetManifestResourceStream` 加载对应图标。
