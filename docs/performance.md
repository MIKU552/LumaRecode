# 性能优化 / Performance

FrameFlow 的优化重点是降低 GUI 空闲开销，同时保留 WinUI 3、Mica、HDR 弹出层、阴影和动画。

## 优化路径

| 阶段 | 实现 | 结果 |
| --- | --- | --- |
| 按需创建控件 | 配色面板首次打开时创建，ColorPicker 首次展开时创建 | 减少未使用功能的初始化；启动内存对照未显示明显下降 |
| 减少运行时解析 | 编译 XAML、静态本地化映射、源生成 JSON | 移除运行时 XML 解析和属性反射，支持 AOT 编译 |
| 减少重复工作 | 缓存控件，关闭路径和日志的拼写检查及文字预测，启用 WinUI 性能选项 | 减少重复查询和无关文本处理 |
| Native AOT | 将 WinUI 前端预编译为本机代码 | 本机启动私有工作集下降约 5–6 MiB |
| 单文件分发 | .NET 单文件入口提取依赖并启动 AOT 主程序；GUI 启动后入口退出 | 保持单 EXE 下载，无额外常驻 GUI 宿主 |

## 内存结果

测试环境：Windows 11 x64、Core Ultra X7 358H、.NET 11、Windows App SDK 2.6.5；HDR 开启，深色主题，Mica 与窗口大小保持一致。测量日期为 2026-11-12。

每种版本独立启动两次，每次仅保留一个测试实例。进程启动至少 30 秒后读取三次性能计数器，间隔约 1 秒。采样前未打开菜单、获取界面树或执行编码任务。

| 构建 | 私有工作集 MiB | 私有提交 MiB | 总工作集 MiB |
| --- | ---: | ---: | ---: |
| 3.6.3 JIT，运行 1 | 121.754 | 151.125 | 255.801 |
| 3.6.3 JIT，运行 2 | 122.855 | 151.816 | 256.805 |
| 3.6.4 AOT 单文件，运行 1 | 117.293 | 145.699 | 216.844 |
| 3.6.4 AOT 单文件，运行 2 | 115.992 | 143.230 | 213.660 |

私有工作集均值由 122.3 降至 116.6 MiB。总工作集包含共享映射，单独列出。以上为已预热桌面会话中的短期样本；启动与菜单使用后的占用应分别比较。

## 复测

使用 [内存采样脚本](../qa/MemoryChecks/Measure-Process.ps1) 测量实际 GUI 进程。AOT 单文件版的 GUI 位于缓存内的 `Application/FrameFlow.exe`；CLI 测量还需计入等待入口及 Python、FFmpeg 等任务进程。

检查命令见 [QA 指南](../qa/README.md)，编译及分发方式见[开发文档](dependencies.md)。

## English

The optimization path moves from lazy control creation to compiled XAML, static localization, source-generated JSON, reduced repeated work, and Native AOT. The standard .NET single-file entry preserves one-EXE distribution and exits after GUI startup.

Two isolated runs per version on the same HDR-enabled Windows 11 system measured 121.8–122.9 MiB private working set for the JIT build and 116.0–117.3 MiB for the AOT build. These are local startup measurements; shared mappings, menu interaction, and encoding tasks are separate workloads. Reproduction commands are in the linked QA guide.
