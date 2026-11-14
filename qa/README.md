# 开发检查 / Development checks

在仓库根目录运行以下命令。.NET SDK 版本由 `global.json` 指定，Python 检查需要本机安装 Python 3.14 或更新版本。

## 源码检查

```powershell
./tools/Check-Source.ps1
```

依次检查 C# 格式、资源、序列化、核心边界、预览调度、进程管道、文档链接与构建目标。此命令使用独立的测试子进程，无需样片或 GPU。

## 构建与架构

```powershell
python qa/BuildChecks/check_build.py
python qa/BuildChecks/check_build.py --artifacts
```

基础检查验证 MSBuild 的目标映射、中间目录隔离、锁文件选择及两种架构的原生库哈希。`--artifacts` 在三个版本构建后运行，另检查 AOT 指令集、EXE / DLL 的 PE 架构、分发许可清单、Native CLI 与 APX 启动检查。在支持 APX 的设备上，该检查会运行 APX CLI；不支持时验证明确的错误响应。

ARM64 完整运行检查需要 ARM64 Windows 设备。APX 转码检查需要支持 APX 的 CPU 与 Windows。

## 资源与序列化

```powershell
dotnet run --project qa/LanguageChecks/LanguageChecks.csproj -c Release
dotnet run --project qa/SerializationChecks/SerializationChecks.csproj -c Release
dotnet run --project qa/BackendChecks/BackendChecks.csproj -c Release -- --check
python qa/LanguageChecks/check_cli_languages.py
python tools/Update-ViewText.py --check
python tools/generate_hlg_luts.py --check
```

核心边界检查还覆盖数值解析、选项值缺失、工具查找、日志错误与通配符匹配。

这些检查覆盖四种语言的选择、翻译资源与格式占位符、XAML 映射、设置格式、命令行 JSON 格式和生成的 LUT。可给 `check_cli_languages.py` 传入 `--exe dist/SelfContained/LumaRecode.exe`，检查打包后的 CLI 语言资源。

## CLI 与转换核心

先运行 `./build.ps1`，再执行集成检查：

```powershell
python qa/CliChecks/check_cli.py --exe dist/SelfContained/LumaRecode.exe
python qa/RuntimeChecks/check_runtime.py --exe dist/SelfContained/LumaRecode.exe
dotnet build qa/BackendChecks/BackendChecks.csproj -c Release
python qa/TimingChecks/check_timing.py
python qa/TimingChecks/check_qsv_recovery.py
```

集成检查需要 FFmpeg、ffprobe、可用的 Intel QSV 编码器和 Vulkan / libplacebo。它们执行实际编码、输出校验、PowerShell 管道、取消和临时文件清理。测试使用临时目录与合成输入。

时间戳检查覆盖可变帧率的 QSV 编码、8/10 位转换与 SDR/HLG/PQ，逐帧比较显示时间并检查 DTS 顺序。可给 `check_timing.py` 传入 `--sample PATH`，检查相机原片和时间戳冲突后的自动重试。

QSV 恢复检查覆盖 AV1 HDR、旋转和镜像、240 fps GOP，以及解码、LowPower、时间戳故障后的重试上限和输出文件保护。兼容策略与上游报告见 [QSV 兼容性](../docs/qsv-compatibility.md)。

## GUI 启动

发布前直接打开最终的 `LumaRecode.exe`，确认主窗口完成加载，再检查语言切换、配色面板和“关于”窗口。关闭后重新打开，确认语言设置恢复。检查 `%LOCALAPPDATA%\LumaRecode\error.log` 的更新时间及内容。

## 采样、范围与音频

```powershell
python qa/FormatChecks/check_formats.py
```

`qa/BackendChecks/` 提供 C# 核心的测试入口，格式检查通过它执行原生代码。

检查 SDR / HLG / PQ 编码后的黑白像素值、8 / 10 / 12 位与 4:2:0 / 4:2:2 / 4:4:4 / RGB / 灰度输入、源格式上限，以及 FLAC 多音轨的解码样本哈希。需要 Intel QSV 和 Vulkan / libplacebo。

## 性能与并发

```powershell
dotnet run --project qa/PerformanceChecks/PerformanceChecks.csproj -c Release -- --check
dotnet run --project qa/PerformanceChecks/PerformanceChecks.csproj -c Release
dotnet run --project qa/PerformanceChecks/PerformanceChecks.csproj -c Release -- --tools
```

检查通知、事件顺序、接收线程上下文、取消、日志容量、Unicode 边界和能力查询缓存。预览检查覆盖请求合并、LRU 淘汰、取消后的回调、源切换释放、错误恢复，以及二进制管道的字节边界与子进程回收。微基准输出五轮测量的中位数；`--tools` 需要 PATH 中的 FFmpeg。

预览渲染测速先读取素材和逐帧索引，再测量每个请求的渲染耗时、PNG 大小与托管分配量：

```powershell
dotnet build qa/BackendChecks/BackendChecks.csproj -c Release
python qa/PerformanceChecks/benchmark_preview.py --sdr D:/MediaTests/sdr.mp4 --hlg D:/MediaTests/hlg.mp4 --log D:/MediaTests/dlog.mp4 --profile pocket3 --output D:/MediaTests/preview.json
```

可分别指定任一素材。`--frames 0,30,60,90` 设置帧序号，`--repeat 3` 设置轮数。

## 内存采样

保持窗口大小、主题、HDR 和输入法一致，每次仅运行一个待测实例，在菜单操作和编码之前采样：

```powershell
./qa/MemoryChecks/Measure-Process.ps1 -Executable '<实际 GUI 路径>/LumaRecode.exe' -Label startup -OutputFile samples.jsonl
```

脚本等待进程年龄至少 30 秒后采样三次，记录私有工作集、私有提交、总工作集和累计 CPU 时间。菜单操作后可用 `-AgeSeconds 0 -Label panels` 单独记录。

AOT 单文件版的实际 GUI 位于 `.net/LumaRecode/<bundle-id>/Application/LumaRecode.exe`。GUI 按主程序采样；CLI 按入口、主程序和任务子进程的合计占用采样。

预览解码器可单独采样。先读取实际 GUI 的进程 ID，再运行以下脚本并操作预览时间轴：

```powershell
./qa/MemoryChecks/Measure-ChildProcesses.ps1 -RootProcessId 12345 -Label preview -DurationSeconds 60 -OutputFile D:/MediaTests/preview-memory.jsonl
```

脚本追踪 GUI 启动的 FFmpeg 及 PATH 垫片下的解码进程，记录每个进程的峰值工作集。

优化设计与结果见[性能优化](../docs/performance.md)。

## English

Run resource and serialization checks before integration tests. CLI and native backend checks perform real conversions and require the configured FFmpeg filters and GPU support. For memory comparisons, use one process at a time and keep the UI state and process age consistent. Sample the extracted AOT GUI, and account for all processes when measuring CLI jobs.

## HDR and RAW

```powershell
python qa/HdrChecks/check_hdr.py
python qa/HdrChecks/check_hdr.py --samples D:/MediaTests/HDR
python tools/generate_hlg_luts.py --check
```

HDR checks cover Apple transfer/gamut math, explicit Log detection, per-frame Dolby Vision metadata, HDR10+ JSON round-tripping and ProRes RAW decoder compatibility. Downloaded sample origins and excerpt commands are listed in [sample-sources.json](HdrChecks/sample-sources.json). The full HDR10+ comparison uses hdr10plus_tool on PATH as a development-only reference. Production uses the C# parser. Keep footage outside the source tree. Use `--metadata-only` for SEI ordering, edit-list, sparse-metadata and export checks.

## Frame and image exports

```powershell
python qa/FrameChecks/check_frames.py --reference D:/Dev/libultrahdr/ultrahdr_app.exe
```

Checks CFR / VFR frame identity across keyframes, sample-accurate PCM-to-FLAC cuts, HDR / SDR image exports, RGB channel order, Google gain-map decoding, Motion Photo byte ranges and the cover frame timestamp. The optional upstream decoder is a development-only oracle.
