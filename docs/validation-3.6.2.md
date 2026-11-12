# 3.6.2 发布与内存观察 / Release and memory observations

日期：2026-11-12。

## 已运行 3.6.1 的只读采样

读取维护者现有进程，未操作窗口、启动转码或强制回收内存。这是已使用过界面的会话，不是固定窗口状态的冷启动基准。

- Windows 私有工作集约 **160.9 MiB**，私有提交约 **180.6 MiB**；总工作集约 **431.8 MiB**，包含可共享映射，与私有工作集口径不同。
- 未发现随该会话常驻的 Python / FFmpeg / ffprobe 进程。
- 官方 `dotnet-counters` 10.0.750501 采样 8 秒：最近一次 GC 的托管堆提交量约 **3.77 MiB**，各代堆大小合计约 **1.11 MiB**。这些是最近一次 GC 的数据，不是此刻所有存活对象或整个程序的内存。
- 采样期间没有 GC，托管分配约 **16–40 KiB/s**。短时间快照不足以排除长期泄漏，也不能把私有提交与托管堆的差值精确归到某个 UI 或图形组件。

口径参考：[.NET runtime metrics](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime) · [dotnet-counters](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters)。

## 改动与检查范围

- 移除工具窗口重复的许可链接，在“关于”和首次许可窗口保留入口。
- 配色面板内容延迟到首次打开，完整 ColorPicker 延迟到首次展开高级颜色区；未打开这些功能时不创建其控件树。首次使用后保留控件以复用，切换语言时不提前重建内容。
- 保留既有 HDR 独立弹出层的配置与不透明背景样式，不改变 Mica、阴影或动画策略。
- Release 构建与源码静态检查通过。遵循维护者不做实机回归的要求，未启动 3.6.2 GUI 或编码任务，**没有测得新版节省量**，也不将旧版会话数值用作严格的前后对比。
- 编码预设、三张 LUT 与第三方原始许可未改变；保留既有 NETSDK1244 单文件提取提示。

Read-only observations of the maintainer's existing 3.6.1 session are not a cold-start baseline or an old/new comparison. Version 3.6.2 defers unused appearance controls, receives build/static checks only, and has no measured memory reduction claim. No working-set trimming or forced GC is used.
