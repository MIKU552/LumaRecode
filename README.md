<p align="center"><img src="Brand/LumaRecode.svg" width="96" height="96" alt="LumaRecode 图标" /></p>
<h1 align="center">映码 · LumaRecode</h1>

简体中文 | [English](docs/readme/en.md) | [日本語](docs/readme/ja.md) | [Español](docs/readme/es.md)

LumaRecode 是一款 Windows 和 macOS 视频工具。用它压缩视频、把相机 Log 转成可分享的 HDR 或 SDR、按帧裁剪和导出照片。两套原生界面共用一套 FFmpeg 转码核心。

[项目网站](https://lumarecode.miku552.top/zh/) · [GitHub 下载](https://github.com/MIKU552/LumaRecode/releases) · [命令行](docs/cli/zh-CN.md) · [文档](docs/README.md) · [更新记录](CHANGELOG.md)

<a href="https://apps.microsoft.com/detail/9N71VF8GJT6F">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://get.microsoft.com/images/zh-cn%20light.svg" />
    <source media="(prefers-color-scheme: light)" srcset="https://get.microsoft.com/images/zh-cn%20dark.svg" />
    <img src="https://get.microsoft.com/images/zh-cn%20light.svg" alt="从 Microsoft Store 获取映码（Windows）" width="200" />
  </picture>
</a>

## 功能

| | Windows 11 · x64 / ARM64 | macOS 27+ · Apple Silicon |
| --- | --- | --- |
| 界面 | WinUI 3，Mica，系统通知 | SwiftUI，系统色彩和字体 |
| 视频 | HEVC、AV1、H.264 · MP4 / MKV | HEVC、AV1、H.264 · MP4 / MKV |
| 快速分享 | Intel QSV、NVIDIA NVENC、AMD AMF、Media Foundation | Apple VideoToolbox |
| HDR | HLG / PQ、Dolby Vision 与 HDR10+ 素材 | HLG / PQ、Dolby Vision 与 HDR10+ 素材；支持 HLG → Dolby Vision 8.4 |
| 图片 | SDR JPEG / PNG、HDR AVIF、Ultra HDR JPEG | SDR JPEG、HDR HEIC、Adaptive HDR HEIC |
| 动态照片 | Android Motion Photo | Apple 实况照片 |

快速分享使用探测通过的硬件编码器，软件归档使用 x264、x265 或 SVT-AV1。支持 DJI D-Log M、Samsung Log、Mi-Log、Apple Log 和 Apple Log 2。ProRes RAW 支持取决于 FFmpeg 解码器。[色彩转换](docs/color-conversion.md#简体中文)

## 安装

- **Windows** · 通过 [Microsoft Store](https://apps.microsoft.com/detail/9N71VF8GJT6F) 安装，或从 GitHub Releases 手动下载。Microsoft Store 分发的 MSIX 安装包由微软官方签名。选 x64 或 ARM64。单文件 EXE 可直接运行。便携 ZIP 解压后打开 `LumaRecode.exe`。MSI 将应用装到当前用户目录。AOT 版可直接启动，外置 .NET 版缺少运行时时可从启动提示打开微软下载页。[.NET 11 Runtime](https://dotnet.microsoft.com/download/dotnet/11.0)
- **macOS** · 把 DMG 中的 LumaRecode 拖进“应用程序”。FFmpeg Full 版附带 FFmpeg、ffprobe、libplacebo 和 MoltenVK。[首次启动与工具配置](docs/dependencies.md#macos-安装与分发)

外置 FFmpeg 版会查找 PATH（macOS 也查找 Homebrew），还可以在应用中指定 `ffmpeg` 和 `ffprobe`。Log 处理需要 `zscale` 和 `lut3d`。HDR 转 SDR 和部分 Dolby Vision 源需要 `libplacebo` 与 Vulkan。

## 预览、裁剪与图片

1. 打开素材，选择快速分享或软件归档，并确认输出色彩与格式。
2. 打开时间轴播放或逐帧定位，设置开始帧和结束帧。也可以保留整段视频。
3. 在图片页面选择封面帧。Windows 的 Android 动态照片默认把 HDR 素材导出为 **Ultra HDR JPEG + SDR 视频**，可分别切换封面和视频的格式。macOS 可导出 SDR JPEG 或 Adaptive HDR HEIC 与 MOV 配对的实况照片。

## 命令行

```powershell
./LumaRecode.exe probe clip.mp4 --json | ConvertFrom-Json
./LumaRecode.exe convert clip.mp4 --preset archive-hevc | Out-Host
```

```sh
/Applications/LumaRecode.app/Contents/MacOS/LumaRecode probe clip.mov
/Applications/LumaRecode.app/Contents/MacOS/LumaRecode convert clip.mov -o out.mkv
```

参数、批量任务和输出格式见[命令行手册](docs/cli/zh-CN.md)。

## 开发

- **Windows** · 用 Visual Studio 打开 `LumaRecode.sln`，或在 PowerShell 7 运行 `./build.ps1`。打包用 `./tools/Package-Windows.ps1`。
- **macOS** · 用 Xcode 打开 `macOS/LumaRecode.xcodeproj`，或运行 `sh tools/build-macos.sh Release --dmg`。
- **共享核心** · `engine/` 提供 C ABI、C/C++ 转码核心和 Rust 元数据模块。[代码结构](docs/architecture.md#简体中文)

[工具链与分发](docs/dependencies.md#简体中文) · [编码预设测试](docs/benchmarks/encoding-benchmark.md#简体中文) · [QA 指南](qa/README.md)

## 许可与作者

自主代码采用 [MIT License](LICENSE)。第三方许可与来源见 [NOTICE](NOTICE)。

Windows 版 Codex 使用 GPT-6.1 Sol 和 GPT-6.2 Sol（均为 `xhigh`）编写项目代码。[MIKU552](https://github.com/MIKU552) 负责需求、素材和实机核验。
