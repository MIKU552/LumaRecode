# 映流 · FrameFlow

简体中文 | [English](README.en.md)

面向 Windows 11 的视频转码工具，提供 FFmpeg 图形界面，可压缩普通视频、将相机 Log 转换为 HDR / SDR，或将 HDR 视频转换为 SDR。支持输出 MP4 / MKV，使用 HEVC、AV1 或 H.264 编码。

[下载最新版本](https://github.com/MIKU552/FrameFlow/releases/latest) · [更新记录](CHANGELOG.md) · [从源码构建](#从源码构建)

## 快速开始

1. 从 [Releases](https://github.com/MIKU552/FrameFlow/releases) 下载便携 ZIP，解压后运行 **FrameFlow.exe**。同目录的 **FrameFlow.com** 提供命令行入口，完整说明见[命令行文档](docs/cli.zh-CN.md)。EXE 为 Windows x64 单文件程序，已包含界面运行时、转换组件和 LUT，无须安装 .NET 或 Python。
2. 准备 Windows 版 [FFmpeg](https://ffmpeg.org/download.html)，下载页的 **Windows EXE Files** 区域提供预编译版本链接。需要同时包含 **ffmpeg.exe** 和 **ffprobe.exe**。程序优先从 `PATH` 查找；未找到时，在弹出的窗口中选择这两个文件。右上角齿轮可随时修改路径。
3. 将视频拖入素材区域，或点击“选择视频”。程序会读取分辨率、帧率、位深、色彩标签和音轨，并识别可用的 Log / HDR 标记。
4. 确认 SDR / HDR 输出、用途和视频编码。普通 SDR 默认输出 SDR，HDR 默认保留 HDR，已识别曲线的 Log 默认转换为 HLG HDR。
5. 确认保存位置，点击“开始转换”。底部显示进度、转码帧率、当前编码位置和预估剩余时间；详细 FFmpeg 命令与错误信息位于“转换日志”。

**首次使用：**“快速分享”使用 Intel QSV 硬件编码，需要显卡支持所选编码器。没有相应 Intel 硬件时，选择“软件归档”使用 CPU 编码。

## 主要功能

- **视频压缩**：按“快速分享 / 软件归档”分类选择编码档，支持 HEVC、AV1、H.264。
- **色彩转换**：内置相机 Log → HLG HDR、Log → Rec.709 SDR、HLG / PQ HDR → SDR 三组转换。
- **格式控制**：独立选择 MP4 / MKV、色度采样和音频策略；4:2:2 原片可直接输入并转换为 4:2:0。
- **素材识别**：显示视频与逐条音轨信息，根据原片元数据设置默认选项；无法确认的输入类型或相机曲线可手动指定。
- **输出检查**：完成后核对位深、采样、色彩标签及所需 HDR 静态元数据；保留原片，避免覆盖已有输出，取消时清理本次临时文件。
- **原生 Windows 界面**：WinUI 3、Mica、浅色 / 深色 / 跟随系统、系统或自定义强调色，中英文即时切换。

## 编码与输出

| 用途 | 编码器 | 自动封装 | 音频默认策略 |
| --- | --- | --- | --- |
| 快速分享 | Intel QSV：HEVC / AV1 / H.264 | MP4 | HEVC、H.264 使用 AAC；AV1 使用 Opus |
| 软件归档 | CPU：x265 / SVT-AV1 / x264 | MKV | 复制原音轨 |

封装和音频均可手动调整。在“格式与音频”中可选择 AAC、Opus 或复制原音轨。MP4 配合音轨复制时，程序会先检查封装兼容性。

- **HDR**：输出 10 位，使用 HEVC 或 AV1。
- **SDR**：快速分享输出 8 位；软件归档的 HEVC / AV1 输出 10 位，H.264 输出 8 位。
- **色度采样**：默认输出 4:2:0；软件归档的 HEVC / H.264 可选 4:2:2。

编码器参数在界面中显示，完整预设见 [recommended-presets.json](Worker/assets/recommended-presets.json)。转换会重新编码视频；选择“复制原音轨”只影响音频处理。

预设的核心参数来自维护者在 **Xiaomi Book Pro 14 / Core Ultra X7 358H / Arc B390 / 32 GB** 上的实际测试。测试方法、画质与体积、真实转码速度、参数取舍及原始结果摘录见[编码测试报告](docs/benchmarks/encoding-benchmark.zh-CN.md)。这些值是本机、特定素材和软件版本下的默认起点；HDR、SDR8、4:2:2 和 LUT 转换的功能验证不等于重新完成了整套画质调参。

## 色彩转换

| 输入素材 | 转换方式 | 输出 |
| --- | --- | --- |
| 普通 SDR | 不套用 LUT，保留 / 标准化 | Rec.709 SDR |
| 相机 Log | 使用对应曲线的内置 LUT | BT.2020 HLG HDR，或 Rec.709 SDR |
| HLG / PQ HDR | 保留 HDR | 保留原 HLG / PQ 类型 |
| HLG / PQ HDR | HDR → SDR 色调映射 | Rec.709 SDR |

内置曲线支持 **小米 Mi-Log、DJI Osmo Pocket 3 D-Log M、Samsung Log**。自动识别依赖原片元数据；标记缺失或曲线无法确认时，需要核对“输入解释”和“相机曲线”。

使用范围：

- Log 转换需要对应相机的高位深原片。内置 LUT 使用固定的曝光和高光映射，不能恢复原片已剪切的高光。
- 当前不提供 SDR → HDR 转换，也不负责 HDR10+ / Dolby Vision 动态元数据的重建。
- HDR 文件在接收端能否以 HDR 显示，取决于播放设备、软件和分享平台。

## 运行要求

| 组件 | 要求 |
| --- | --- |
| 系统 | Windows 11 x64 |
| FFmpeg / ffprobe | 由使用者提供；所选构建需包含任务使用的解码器、编码器和滤镜 |
| Intel QSV | 仅“快速分享”需要；所选编码格式须受显卡及驱动支持 |
| Log 转换 | FFmpeg 的 `zscale`、`lut3d` 滤镜 |
| Log / HDR → SDR | FFmpeg 的 `libplacebo` 滤镜及可用的 Vulkan 驱动；Log 输入还需上述 Log 转换滤镜 |

可读取的输入格式取决于所选 FFmpeg 的解码能力。缺少编码器或滤镜时，程序会显示所需组件，工具路径可在右上角齿轮中更换。

界面语言和主题默认跟随系统。中文系统使用简体中文，其他系统语言默认使用英文。设置保存在 `%LOCALAPPDATA%\FrameFlow\settings.json`。

## 命令行

命令行与 GUI 共用编码预设、LUT、自动识别和输出校验，支持文件/目录/通配符批量处理、JSON 事件、预演命令、配置文件、Ctrl+C 取消和稳定退出码。

```powershell
./FrameFlow.com --help
./FrameFlow.com probe clip.mp4 --json
./FrameFlow.com convert clip.mp4 --preset archive-hevc
./FrameFlow.com convert dlog.mp4 --input-kind log --profile pocket3 --range hdr
./FrameFlow.com convert "D:\Videos" --recursive --output-dir "D:\Exports" --json
```

首次无交互使用前，可用 `license --text` 阅读组件条款，再运行 `license --accept-terms`。详见[完整命令与脚本示例](docs/cli.zh-CN.md)。

## 从源码构建

需要 Windows 11 x64、**.NET SDK 11.0.100**（由 `global.json` 固定）、PowerShell 7 和 **MinGW-w64 GCC 16.2**（构建原生终端入口）。默认构建不需要预装 Python。

```powershell
git clone https://github.com/MIKU552/FrameFlow.git
cd FrameFlow
.\build.ps1
```

构建脚本从 NuGet 还原锁定依赖，下载并校验官方 Python 3.15.2 嵌入式运行时，收集完整许可，最后生成包含 .NET 11、Windows App SDK 2.6.5 与 Python 的单文件 EXE。没有使用 PyInstaller；FFmpeg 仍由用户提供。首次启动需要接受随附第三方二进制组件的适用条款，可在工具路径窗口查看完整许可。

构建产物：

```text
dist/FrameFlow-3.6.0-win-x64.zip
# 内含 FrameFlow.exe 与 FrameFlow.com
```

另有 `./build.ps1 -Deployment FrameworkDependent` 可构建精简 ZIP，需要用户自行安装 .NET 11 Runtime、Windows App Runtime 2.6 与 Python 3.14 或更新版本，并在程序内指定工具路径；默认发布使用包含完整运行时的便携包。

可选图标重绘工具的版本在 `Brand/requirements.txt`，常规构建不需要 Qt / PySide6。依赖版本、支持周期和分发依据见[依赖说明](docs/dependencies.md)。

运行语言与资源检查：

```powershell
dotnet run --project qa/LanguageChecks/LanguageChecks.csproj -c Release
```

## 项目结构

```text
App/                  WinUI 3 界面、主题、本地化与任务交互
Native/               无第三方运行时的原生终端入口
Worker/               命令行、素材识别、FFmpeg 命令生成与输出校验
Worker/assets/        LUT、编码预设与 LUT 来源
licenses/             随程序分发的原始许可、声明和版本清单
tools/                许可收集与发布检查
Brand/                品牌图形与图标生成脚本
qa/LanguageChecks/    语言策略、翻译及资源检查
build.ps1             Windows 单文件 EXE 构建脚本
docs/benchmarks/       编码实测报告、预设来源与可核对的数据
```

## 开发方式与贡献

本项目自主代码的编写、界面实现、构建和仓库维护工作，全部通过 **Windows 版 Codex** 完成，使用 **[GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol)** 模型和 **极高（xhigh）** 推理设置。维护者 **[MIKU552](https://github.com/MIKU552)** 负责提出需求、提供设备与素材、选择方案，并亲自进行实机使用和结果核验。

以上说明针对本项目的开发过程；FFmpeg、WinUI 3、第三方 LUT 等组件保留各自作者与许可。

Git 提交由 [Codex](https://github.com/codex) 与 MIKU552 共同署名，并使用维护者现有的 GPG 密钥签名。

## 开源许可

本项目的自主代码、文档、原创图形及项目生成的 Mi-Log / Samsung Log LUT，采用 **[MIT License](LICENSE)**。允许商用、修改及闭源再分发，须在软件副本或实质性部分中保留版权与许可声明。第三方归属说明见 [NOTICE](NOTICE)。

自 **3.5.1** 起自主内容使用 MIT；此前发布版本保留其原有许可。

第三方 LUT 与运行时保留各自许可。当前版本使用正式版 Windows App SDK，并随程序保留所需许可、声明和组件条款；详见[第三方组件](Worker/assets/THIRD_PARTY.md)与[依赖及分发说明](docs/dependencies.md)。

## 文档与第三方组件

- [编码测试报告](docs/benchmarks/encoding-benchmark.zh-CN.md)：内置参数的实测依据、适用范围与数据来源。
- [CHANGELOG.md](CHANGELOG.md)：版本更新记录。
- [HISTORY.md](HISTORY.md)：早期源码快照的归档说明。
- [THIRD_PARTY.md](Worker/assets/THIRD_PARTY.md)：组件许可及 LUT 来源。Pocket 3 的 D-Log M → HLG LUT 来自第三方 OpenOSV 项目。
