# 映流 · FrameFlow

面向 Windows 11 的视频转码工具，提供 FFmpeg 图形界面，可压缩普通视频、将相机 Log 转换为 HDR / SDR，或将 HDR 视频转换为 SDR。支持输出 MP4 / MKV，使用 HEVC、AV1 或 H.264 编码。

[下载最新版本](https://github.com/MIKU552/FrameFlow/releases/latest) · [更新记录](CHANGELOG.md) · [从源码构建](#从源码构建)

## 快速开始

1. 从 [Releases](https://github.com/MIKU552/FrameFlow/releases) 下载 **FrameFlow.exe** 并运行。发布版为 Windows x64 单文件程序，已包含界面运行时、转换组件和 LUT，无须安装 .NET 或 Python。
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

## 从源码构建

需要 Windows x64、**.NET 8 SDK** 和 **Python 3.14**，并确保 `dotnet`、`python` 可在命令行中调用。

```powershell
git clone https://github.com/MIKU552/FrameFlow.git
cd FrameFlow
.\build.ps1
```

构建脚本会创建 Python 虚拟环境，使用 PyInstaller 打包转换组件，再通过 .NET 发布 WinUI 3 前端。首次构建需要从 PyPI 和 NuGet 下载依赖。

构建产物：

```text
dist/FrameFlow.exe
```

运行语言与资源检查：

```powershell
dotnet run --project qa/LanguageChecks/LanguageChecks.csproj -c Release
```

## 项目结构

```text
App/                  WinUI 3 界面、主题、本地化与任务交互
Worker/               素材识别、FFmpeg 命令生成与输出校验
Worker/assets/        LUT、编码预设与第三方许可
Brand/                品牌图形与图标生成脚本
qa/LanguageChecks/    语言策略、翻译及资源检查
build.ps1             Windows 单文件 EXE 构建脚本
```

## 文档与第三方组件

- [CHANGELOG.md](CHANGELOG.md)：版本更新记录。
- [HISTORY.md](HISTORY.md)：早期源码快照的归档说明。
- [THIRD_PARTY.md](Worker/assets/THIRD_PARTY.md)：组件许可及 LUT 来源。Pocket 3 的 D-Log M → HLG LUT 来自第三方 OpenOSV 项目。
