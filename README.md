<p align="center">
  <img src="Brand/FrameFlow.svg" width="104" height="104" alt="FrameFlow 图标" />
</p>
<h1 align="center">映流 · FrameFlow</h1>

简体中文 | [English](README.en.md)

面向 Windows 11 的视频转码与色彩转换工具，提供原生 WinUI 3 界面和命令行，使用 FFmpeg 完成视频压缩、相机 Log 转 HDR / SDR，以及 HDR 转 SDR。

[下载](https://github.com/MIKU552/FrameFlow/releases/latest) · [命令行文档](docs/cli.zh-CN.md) · [更新记录](CHANGELOG.md)

## 功能

- 按快速分享与软件归档选择预设，支持 HEVC、AV1、H.264 和 MP4 / MKV。
- 支持 Mi-Log、Pocket 3 D-Log M、Samsung Log 转换为 HLG HDR 或 Rec.709 SDR，以及 HLG / PQ 转 SDR。
- 读取分辨率、帧率、位深、色彩标签及音轨，按素材设置默认选项。
- 控制色度采样与音频策略，支持 4:2:2 输入转 4:2:0。
- 显示进度、编码帧率、当前位置与剩余时间，完成后校验输出格式。
- 提供 Mica、浅色 / 深色 / 系统主题、强调色和中英文切换。
- 命令行支持批量、配置文件、参数预演和 JSON 事件输出。

## 快速开始

1. 从 [Releases](https://github.com/MIKU552/FrameFlow/releases) 下载 **FrameFlow.exe**。程序内置所需运行时，可直接运行。
2. 准备 Windows 版 [FFmpeg](https://ffmpeg.org/download.html)，包含 `ffmpeg.exe` 和 `ffprobe.exe`。首次运行会从 `PATH` 查找，也可在右上角齿轮中指定路径。
3. 拖入视频或点击“选择视频”，确认输出动态范围、编码预设和色彩转换。
4. 选择保存位置，点击“开始转换”。

“快速分享”使用 Intel QSV；显卡不支持所选编码器时，选择“软件归档”使用 CPU。

## 编码与色彩

| 用途 | 编码器 | 默认封装 | 默认音频 |
| --- | --- | --- | --- |
| 快速分享 | Intel QSV：HEVC / AV1 / H.264 | MP4 | HEVC、H.264 使用 AAC；AV1 使用 Opus |
| 软件归档 | x265 / SVT-AV1 / x264 | MKV | 复制原音轨 |

封装、采样和音频均可单独选择。HDR 输出为 10 位 HEVC 或 AV1；SDR 快速分享为 8 位。软件 HEVC / AV1 归档为 10 位，H.264 为 8 位。软件 HEVC / H.264 可选 4:2:2。

SDR 默认保持 SDR，HLG / PQ 默认保持 HDR，已识别曲线的 Log 默认转换为 HLG。元数据不足时，在“输入解释”和“相机曲线”中手动选择。Log 转换需要对应曲线的原片；LUT 使用固定曝光和高光映射。当前支持 HLG / PQ 输出，不重建 HDR10+ 或 Dolby Vision 动态元数据。

完整预设见 [recommended-presets.json](Worker/assets/recommended-presets.json)，参数来源见[编码实测报告](docs/benchmarks/encoding-benchmark.zh-CN.md)。转换会重新编码视频；“复制原音轨”仅影响音频。

## 运行要求

- Windows 11 x64。
- 用户提供的 FFmpeg / ffprobe，包含任务所需的编码器和滤镜。
- 快速分享需要相应 Intel QSV 支持。
- Log 转换需要 `zscale`、`lut3d`；HDR / Log 转 SDR 还需要 `libplacebo` 和 Vulkan。

语言和主题默认跟随系统；中文系统使用简体中文，其他语言系统默认英文。设置保存在 `%LOCALAPPDATA%\FrameFlow\settings.json`。

## 命令行

在 PowerShell 7 中运行；为 GUI 子系统 EXE 接上输出管道，以等待命令结束：

```powershell
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
./FrameFlow.exe --help | Out-Host
./FrameFlow.exe probe clip.mp4 --json | ConvertFrom-Json
./FrameFlow.exe convert clip.mp4 --preset archive-hevc | Out-Host
./FrameFlow.exe convert dlog.mp4 --input-kind log --profile pocket3 --range hdr | Out-Host
```

首次无人值守使用前，运行 `license --text` 阅读组件条款，再运行 `license --accept-terms`。全部命令、批量用法和退出码见[命令行文档](docs/cli.zh-CN.md)。

## 从源码构建

需要 .NET SDK 11.0.100、PowerShell 7，以及支持 Native AOT 的 Visual Studio C++ 工具链。

```powershell
git clone https://github.com/MIKU552/FrameFlow.git
cd FrameFlow
./build.ps1
```

产物为 `dist/SelfContained/FrameFlow.exe`。构建方式与依赖版本见[开发文档](docs/dependencies.md)，检查命令见 [QA 指南](qa/README.md)。

## 项目结构

```text
App/       WinUI 3 界面、主题、本地化和任务交互
Bundle/    单文件打包入口
Worker/    命令行、素材探测、FFmpeg 调用和 LUT
Brand/     图标源文件与生成工具
tools/     许可收集和本地化代码生成
qa/        自动检查与内存采样工具
docs/      使用说明、开发文档和测试数据
licenses/  第三方许可、声明与来源清单
```

## 开发与许可

项目自主代码、界面、构建和仓库维护由 **Windows 版 Codex** 使用 **[GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol)**、**极高（xhigh）** 推理设置完成。**[MIKU552](https://github.com/MIKU552)** 提出需求、提供设备与素材，并进行实机核验。Git 提交由双方共同署名，使用维护者的 GPG 密钥签名。

自主代码、文档、图形及项目生成的 Mi-Log / Samsung Log LUT 采用 **[MIT License](LICENSE)**。第三方 LUT 和运行时保留各自许可，归属及分发依据见 [NOTICE](NOTICE) 和[第三方组件](Worker/assets/THIRD_PARTY.md)。MIT 自 3.5.1 起适用，历史版本保留原许可。

## 文档

- [命令行](docs/cli.zh-CN.md)
- [开发与分发](docs/dependencies.md)
- [性能优化](docs/performance.md)
- [编码实测报告](docs/benchmarks/encoding-benchmark.zh-CN.md)
- [更新记录](CHANGELOG.md)
