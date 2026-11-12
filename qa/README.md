# 开发检查 / Development checks

在仓库根目录运行以下命令。.NET SDK 版本由 `global.json` 指定，Python 检查需要本机安装 Python 3.14 或更新版本。

## 资源与序列化

```powershell
dotnet run --project qa/LanguageChecks/LanguageChecks.csproj -c Release
dotnet run --project qa/SerializationChecks/SerializationChecks.csproj -c Release
python tools/Update-ViewText.py --check
python Worker/tools/generate_hlg_luts.py --check
```

这些检查覆盖语言选择、翻译资源、XAML 映射、设置格式、worker JSON 协议和生成的 LUT。

## CLI 与 worker

先运行 `./build.ps1`，再执行集成检查：

```powershell
python qa/CliChecks/check_cli.py --exe dist/SelfContained/FrameFlow.exe
python qa/RuntimeChecks/check_worker.py --deployment dist/NativeAot
```

集成检查需要 FFmpeg、ffprobe、可用的 Intel QSV 编码器和 Vulkan / libplacebo。它们执行实际编码、输出校验、PowerShell 管道、取消和临时文件清理。测试使用临时目录与合成输入。

## 内存采样

保持窗口大小、主题、HDR 和输入法一致，每次仅运行一个待测实例，在菜单操作和编码之前采样：

```powershell
./qa/MemoryChecks/Measure-Process.ps1 -Executable '<实际 GUI 路径>/FrameFlow.exe' -Label startup -OutputFile samples.jsonl
```

脚本等待进程年龄至少 30 秒后采样三次，记录私有工作集、私有提交、总工作集和累计 CPU 时间。菜单操作后可用 `-AgeSeconds 0 -Label panels` 单独记录。

AOT 单文件版的实际 GUI 位于 `.net/FrameFlow/<bundle-id>/Application/FrameFlow.exe`。测量时确认解包入口已退出；CLI 应统计入口、主程序和任务子进程。脚本只读计数器，结果不包含文件路径或进程 ID。

优化设计与结果见[性能优化](../docs/performance.md)。

## English

Run resource and serialization checks before integration tests. CLI and worker checks perform real conversions and require the configured FFmpeg filters and GPU support. For memory comparisons, use one process at a time and keep the UI state and process age consistent. Sample the extracted AOT GUI, and account for all processes when measuring CLI jobs.
