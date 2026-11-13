# 开发检查 / Development checks

在仓库根目录运行以下命令。.NET SDK 版本由 `global.json` 指定，Python 检查需要本机安装 Python 3.14 或更新版本。

## 资源与序列化

```powershell
dotnet run --project qa/LanguageChecks/LanguageChecks.csproj -c Release
dotnet run --project qa/SerializationChecks/SerializationChecks.csproj -c Release
dotnet build qa/BackendChecks/BackendChecks.csproj -c Release
python qa/LanguageChecks/check_cli_languages.py
python tools/Update-ViewText.py --check
python tools/generate_hlg_luts.py --check
```

这些检查覆盖四种语言的选择、翻译资源与格式占位符、XAML 映射、设置格式、命令行 JSON 格式和生成的 LUT。可给 `check_cli_languages.py` 传入 `--exe dist/SelfContained/LumaRecode.exe`，检查打包后的 CLI 语言资源。

## CLI 与转换核心

先运行 `./build.ps1`，再执行集成检查：

```powershell
python qa/CliChecks/check_cli.py --exe dist/SelfContained/LumaRecode.exe
python qa/RuntimeChecks/check_runtime.py --exe dist/SelfContained/LumaRecode.exe
```

集成检查需要 FFmpeg、ffprobe、可用的 Intel QSV 编码器和 Vulkan / libplacebo。它们执行实际编码、输出校验、PowerShell 管道、取消和临时文件清理。测试使用临时目录与合成输入。

## GUI 启动

发布前直接打开最终的 `LumaRecode.exe`，确认主窗口完成加载，再检查语言切换、配色面板和“关于”窗口。关闭后重新打开，确认语言设置恢复。检查 `%LOCALAPPDATA%\LumaRecode\error.log` 的更新时间及内容。

## 采样、范围与音频

```powershell
python qa/FormatChecks/check_formats.py
```

`qa/BackendChecks/` 提供 C# 核心的测试入口，格式检查通过它执行原生代码。可运行 `python qa/BackendChecks/check_migration.py` 对照 3.8.0 的格式规则与 FFmpeg 参数，使用 `--sample PATH` 增加相机原片识别检查。

检查 SDR / HLG / PQ 编码后的黑白像素值、8 / 10 / 12 位与 4:2:0 / 4:2:2 / 4:4:4 / RGB / 灰度输入、源格式上限，以及 FLAC 多音轨的解码样本哈希。需要 Intel QSV 和 Vulkan / libplacebo。

## 内存采样

保持窗口大小、主题、HDR 和输入法一致，每次仅运行一个待测实例，在菜单操作和编码之前采样：

```powershell
./qa/MemoryChecks/Measure-Process.ps1 -Executable '<实际 GUI 路径>/LumaRecode.exe' -Label startup -OutputFile samples.jsonl
```

脚本等待进程年龄至少 30 秒后采样三次，记录私有工作集、私有提交、总工作集和累计 CPU 时间。菜单操作后可用 `-AgeSeconds 0 -Label panels` 单独记录。

AOT 单文件版的实际 GUI 位于 `.net/LumaRecode/<bundle-id>/Application/LumaRecode.exe`。GUI 按主程序采样；CLI 按入口、主程序和任务子进程的合计占用采样。

优化设计与结果见[性能优化](../docs/performance.md)。

## English

Run resource and serialization checks before integration tests. CLI and native backend checks perform real conversions and require the configured FFmpeg filters and GPU support. For memory comparisons, use one process at a time and keep the UI state and process age consistent. Sample the extracted AOT GUI, and account for all processes when measuring CLI jobs.
