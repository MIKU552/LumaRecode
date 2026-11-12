# 命令行

[English](cli.en.md) · [返回 README](../README.md)

GUI 与 CLI 使用同一个 `FrameFlow.exe`，不需要任何附加启动文件。双击或无参数运行时打开 GUI；带 `probe`、`convert` 等命令时无界面运行。终端支持目标为 **PowerShell 7**。

EXE 保留 Windows GUI 子系统，避免双击时出现控制台窗口。PowerShell 7 对 GUI EXE 的交互式等待与普通控制台程序不同：**人类可读命令推荐接 `| Out-Host`，JSON 命令接 `| ConvertFrom-Json`**，让管道等待任务并可靠设置 `$LASTEXITCODE`。脚本/其他程序也可通过进程 API 等待同一个 EXE，无须第二个程序。仅重定向 `>` 不应替代等待管道。

程序输出使用 UTF-8。会话仍使用 GBK 等旧代码页时，先运行下面的编码设置，让中文帮助和进度正常显示；只影响当前 PowerShell 会话。JSON 对非 ASCII 字符使用标准转义，因此解析后的中文路径在旧代码页下也保持完整。

## 开始使用

```powershell
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
./FrameFlow.exe --help | Out-Host
./FrameFlow.exe --version | Out-Host
./FrameFlow.exe license --text | Out-Host
./FrameFlow.exe license --accept-terms | Out-Host
./FrameFlow.exe doctor --json | ConvertFrom-Json
./FrameFlow.exe probe "D:\Videos\clip.mp4" --json | ConvertFrom-Json
./FrameFlow.exe convert "D:\Videos\clip.mp4" | Out-Host
```

GUI 已接受过当前组件条款时可直接使用。首次无人值守运行可在命令上加 `--accept-terms`；此选项表示接受随附第三方组件的适用条款。MIT 项目许可与第三方条款分别保留。任何命令行操作都不会打开文件选择或许可弹窗。

FFmpeg / ffprobe 默认先从 PATH 查找，再使用 GUI 保存的路径。可用 `--ffmpeg PATH --ffprobe PATH` 覆盖；`--python PATH` 可覆盖内置 Python。`--no-settings` 忽略 GUI 保存的路径与许可状态，且不保存设置，适合可重复脚本，此时需显式 `--accept-terms`。

## 命令

| 命令 | 功能 |
| --- | --- |
| `probe INPUT` | 位深、帧率、色彩、Log 证据、全部音轨、流标签与首帧侧数据 |
| `presets` | 七组实测预设与原始编码参数 |
| `profiles` | 内置 Mi-Log、Pocket 3 D-Log M、Samsung Log 曲线与 LUT |
| `doctor` | 工具版本、编码器及滤镜；列出编码器不等于确认显卡支持所有模式 |
| `license [--text]` | 项目许可、第三方条款、完整许可目录与清单 |
| `plan INPUT...` | 探测输入，列出解析后的选项、FFmpeg argv、工作目录和预期色彩，不编码 |
| `convert INPUT...` | 单文件或顺序批量转换，完成后校验输出 |

各命令均可使用 `--help`。`--lang zh|en|system` 控制输出语言。`presets`、`profiles`、`probe`、`doctor`、`license` 返回 JSON；转换的人类进度写入 stderr，结果写入 stdout。

## 编码与色彩选项

`convert` 与 `plan` 共用下列选项：

| 选项 | 值 / 默认行为 |
| --- | --- |
| `--preset` | `share-avc`、`share-hevc`、`share-av1`、`archive-avc`、`archive-hevc`、`archive-av1`、`archive-av1-fast` |
| `--purpose` | `share` / `archive`；默认 `share`，即 Intel QSV 快速分享 |
| `--codec` | `hevc` / `av1` / `avc`；默认 `hevc` |
| `--range` / `--dynamic-range` | `auto` / `sdr` / `hdr`；默认按素材动态范围选择 |
| `--transform` | `auto` / `none` / `log-hdr` / `log-sdr` / `hdr-sdr` |
| `--profile` | `auto` / `milog` / `pocket3` / `samsung` |
| `--input-kind` | `auto` / `sdr` / `hlg` / `pq` / `log`；用于人工纠正原片标签 |
| `--sampling` | `420` / `422`；默认 `420`；422 需要软件 HEVC / AVC |
| `--container` | `auto` / `mp4` / `mkv`；分享默认 MP4、归档默认 MKV |
| `--audio` | `auto` / `aac` / `opus` / `copy`；自动策略与 GUI 一致 |
| `--variant` | `standard` / `fast`；fast 仅用于软件 AV1 备用档 |
| `--cpu-decode` / `--no-cpu-decode` | 强制软件解码 / 使用自动解码路径 |
| `--duration SECONDS` | 只转换指定的正数秒数，便于试片 |
| `--config FILE.json` | 载入编码选项；显式命令行选项优先 |

默认：SDR → SDR、HLG/PQ → 保留 HDR、已确认曲线的 Log → HLG。只知道是 Log 而不知道曲线时返回错误，需指定 `--profile`；完全不明的色彩标签需 `--input-kind`。非法组合会报错，例如 HDR + AVC、QSV + 422、普通 SDR 直接输出 HDR。

```powershell
# 无 Intel 硬件，使用 CPU 归档
./FrameFlow.exe convert clip.mp4 --preset archive-hevc | Out-Host

# Pocket 3：人工确认 D-Log M，输出 HLG HEVC
./FrameFlow.exe convert dlog.mp4 --input-kind log --profile pocket3 --range hdr | Out-Host

# HDR 转 SDR，或 Log 转 SDR；需要 FFmpeg libplacebo / Vulkan
./FrameFlow.exe convert hdr.mp4 --range sdr | Out-Host
./FrameFlow.exe convert dlog.mp4 --input-kind log --profile pocket3 --range sdr | Out-Host

# 422 归档并保留原音轨
./FrameFlow.exe convert input.mov --preset archive-hevc --sampling 422 --audio copy | Out-Host

# 不编码，查看最终参数
./FrameFlow.exe plan clip.mp4 --preset archive-av1-fast --json | ConvertFrom-Json
```

配置文件使用下划线字段名；`range` 对应动态范围，输出文件路径通过 `--output` 指定。

```json
{"preset":"archive-hevc","range":"auto","sampling":"422","audio":"copy","cpu_decode":true}
```

配置只接受表中编码字段和 `preset`；不接受任意 FFmpeg 参数。优先级为：GUI 等价默认值 → 预设 → 配置字段 → 显式命令行字段。若命令指定 `--preset`，其预设选择覆盖配置中的 `preset`，其他配置字段仍生效。

## 批量、输出与覆盖

```powershell
./FrameFlow.exe convert a.mp4 b.mov --output-dir "D:\Exports" | Out-Host
./FrameFlow.exe convert "D:\Videos" --recursive --output-dir "D:\Exports" | Out-Host
./FrameFlow.exe convert "D:\Videos\**\*.mp4" --recursive --output-dir "D:\Exports" --continue-on-error --json | ConvertFrom-Json
./FrameFlow.exe convert clip.mp4 --output "D:\Exports\result.mp4" --overwrite | Out-Host
```

目录扫描支持常见视频扩展名，默认不扫描子目录；通配符应加引号，`**` 需要 `--recursive`。目录扫描保留相对的子目录；通配符以匹配文件共同父目录为根；显式文件按文件名输出。多个输入映射到同一输出时会拒绝整批，不自动覆盖。

默认在源文件旁生成 `文件名_SDR或HDR_编码_用途.mp4或mkv`。`--output` 只能用于一个输入，不能同时使用 `--output-dir`。输出目录可自动创建。已有输出默认报错，可选 `--skip-existing` 跳过，或 `--overwrite` 在临时文件编码及校验成功后原子替换。源文件始终禁止作为输出；失败和正常取消会清理临时文件。

默认遇错停止；`--continue-on-error` 尽量处理其余输入，最终仍返回非零退出码。处理按顺序进行，避免多个任务争用同一硬件编码器。

计划中的 `argv` 用请求的最终文件名展示参数。实际转换把最后的输出参数换为随机临时目录中的文件，校验后再移动或替换最终文件；因此 FFmpeg 本身仍使用 `-n`，`--overwrite` 由程序的原子替换流程完成。

## 自动化接口

`convert --json` 和 `plan --json` 在 stdout 输出逐行 JSON，schema_version 为 1。转换事件包括 `start`、`log`、`status`、`progress`、`done`、`skipped`、`error`、`cancelled`、`summary`；plan 使用 `plan`。任务事件带 `job`、`total_jobs`、`input`、`output`；`data` 为事件内容。

进度包含 `fps`、`percent`、`position_seconds`、`duration_seconds`、`elapsed_seconds`、`eta_seconds`，未知的百分比或剩余时间为 null。错误包含稳定的 `code` 和可读的 `message`。`--log FILE` 向文件追加相同的 JSON Lines 事件，`--verbose` 在人类模式额外显示 FFmpeg 日志。

```powershell
./FrameFlow.exe convert clip.mp4 --json --log job.jsonl | ForEach-Object { $_ | ConvertFrom-Json }
$LASTEXITCODE
```

| 退出码 | 含义 |
| --- | --- |
| 0 | 成功，包括按要求跳过已有输出 |
| 1 | 探测、编码、输出校验失败，或批量任务部分失败 |
| 2 | 参数、配置或选项组合错误 |
| 3 | 缺少 / 无效的运行时或 FFmpeg 工具 |
| 4 | 输入不存在、没有匹配文件，或色彩/Log 曲线需人工确认 |
| 5 | 输出、日志路径或文件冲突 |
| 6 | 尚未接受第三方组件条款 |
| 130 | CLI 协作取消 |

脚本应读取进程退出码或 `$LASTEXITCODE`；机器调用建议使用 `--json`，按 `code` 判断错误，不解析本地化的文本。

在 PowerShell 7 管道中按 Ctrl+C 会同时中断 PowerShell 的管道，任务停止并清理临时输出，但管道可能无法继续传回 `cancelled` 事件或更新 `$LASTEXITCODE` 为 130。通过进程 API 发送控制台取消信号并等待 CLI 正常退出时，可读取 130；自动化任务应区分宿主中断与 CLI 正常返回的错误码。
