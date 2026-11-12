# 内存测量 / Memory measurements

这些是本地开发诊断工具，不随 FrameFlow 附件分发。测量脚本只读取进程计数器，不强制 GC、不裁剪工作集、不修改系统输入法或图形设置。

1. 保持同样的窗口大小、主题、HDR 和输入法，单独打开待测 EXE。
2. 在获取 UI Automation 界面树或执行转码之前测量：

```powershell
./qa/MemoryChecks/Measure-Process.ps1 -Executable ./dist/SelfContained/FrameFlow.exe -Label startup
```

等待进程年龄至少 30 秒后采样三次，输出私有工作集、私有提交、总工作集及累计 CPU 时间。`-OutputFile samples.jsonl` 可追加记录，数据不包含文件路径或进程 ID。菜单交互后用 `-AgeSeconds 0 -Label panels` 另行测量。基线与交互样本分别比较；预热缓存和重复运行并不等于冷启动基准。

默认 AOT 单文件版会将实际 GUI 提取到 `.net/FrameFlow/<bundle-id>/Application/FrameFlow.exe`。测量该实际 GUI 路径，同时确认下载 EXE 对应的解包入口已经退出；不要把已退出入口的 0 MB 当作 GUI 内存。每次只留一个测试实例。单独测 CLI 时应统计入口、主程序及任务子进程，不能套用 GUI 空闲数字。正式数据和排除原因见[3.6.4 报告](../../docs/validation-3.6.4.md)。

`MinimalApp` 是独立的极简 WinUI 对照，使用同版 .NET、WinUI、DPI 清单和原生 Mica，仅含标题文字和可选的两个文本框，无 FrameFlow 任务逻辑、Python 或 FFmpeg。它不是所有 WinUI 应用的内存下限。

```powershell
dotnet publish qa/MemoryChecks/MinimalApp/MinimalApp.csproj -c Release -p:Platform=x64 -o ./diagnostics/MinimalTextbox
```

输出目录名称包含 `Textbox` 时显示两个文本框并聚焦输入，其他目录名称只显示文字。首次启动先预热，再按上面的脚本测量。构建和测试产生的 EXE 仅用于本地诊断；SDK 与运行时的许可沿用[主项目说明](../../docs/dependencies.md)。

These tools are not part of the shipped application. Run one test process per executable path, keep the UI and machine settings consistent, and sample before UI Automation or encoding. Report private working set, private commit and total working set separately. The standalone minimal WinUI window is a local control case, not a universal framework memory floor.
