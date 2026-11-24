<p align="center"><img src="../../Brand/LumaRecode.svg" width="96" height="96" alt="LumaRecode icon" /></p>
<h1 align="center">LumaRecode</h1>

[简体中文](../../README.md) | English | [日本語](ja.md) | [Español](es.md)

LumaRecode is a video tool for Windows and macOS. Compress clips, turn camera Log into shareable HDR or SDR, trim by frame, and export stills. Each platform has a native interface backed by the same FFmpeg transcoding core.

[Downloads](https://github.com/MIKU552/LumaRecode/releases) · [CLI](../cli/en.md) · [Documentation](../README.md#english) · [Changelog](../../CHANGELOG.md#english)

## Features

| | Windows 11 · x64 / ARM64 | macOS 27+ · Apple silicon |
| --- | --- | --- |
| Interface | WinUI 3 with Mica and system notifications | SwiftUI with system colors and fonts |
| Video | HEVC, AV1, H.264 · MP4 / MKV | HEVC, AV1, H.264 · MP4 / MKV |
| Quick share | Intel QSV, NVIDIA NVENC, AMD AMF, Media Foundation | Apple VideoToolbox |
| HDR | HLG / PQ, Dolby Vision and HDR10+ input | HLG / PQ, Dolby Vision and HDR10+ input, HLG → Dolby Vision 8.4 export |
| Stills | SDR JPEG / PNG, HDR AVIF, Ultra HDR JPEG | SDR JPEG, HDR HEIC, Adaptive HDR HEIC |
| Motion | Android Motion Photo | Apple Live Photo |

Quick share uses a hardware encoder that passes the device check. Software archive uses x264, x265, or SVT-AV1. Supported camera profiles include DJI D-Log M, Samsung Log, Mi-Log, Apple Log, and Apple Log 2. ProRes RAW support depends on the FFmpeg decoder. [Color conversion](../color-conversion.md#english)

## Install

- **Windows** · Choose x64 or ARM64. Run the single EXE directly, extract the portable ZIP and open `LumaRecode.exe`, or install the MSI for your user account. AOT builds run directly. Framework-dependent builds require .NET 11 Runtime.
- **macOS** · Drag LumaRecode from the DMG into Applications. The FFmpeg Full edition includes FFmpeg, ffprobe, libplacebo, and MoltenVK. [First launch and tool paths](../dependencies.md#macos-installation-and-distribution)

Editions that use external FFmpeg check PATH and, on macOS, Homebrew. You can also set the paths to `ffmpeg` and `ffprobe` in the app. Log conversion uses `zscale` and `lut3d`. HDR-to-SDR and some Dolby Vision sources use `libplacebo` and Vulkan.

## Preview, trimming and images

1. Open a clip, choose quick share or software archive, and review the output signal and format.
2. Open the timeline to play, step through frames, and set the first and last frames. You can also keep the whole clip.
3. Choose a cover frame on the photo page. On Windows, Android Motion Photo defaults to **Ultra HDR JPEG + SDR video** for HDR footage, with separate controls for the still and video formats. On macOS, Live Photo pairs an SDR JPEG or Adaptive HDR HEIC with a MOV.

## Command line

```powershell
./LumaRecode.exe probe clip.mp4 --json | ConvertFrom-Json
./LumaRecode.exe convert clip.mp4 --preset archive-hevc | Out-Host
```

```sh
/Applications/LumaRecode.app/Contents/MacOS/LumaRecode probe clip.mov
/Applications/LumaRecode.app/Contents/MacOS/LumaRecode convert clip.mov -o out.mkv
```

See the [CLI reference](../cli/en.md) for flags, batch jobs, and output formats.

## Development

- **Windows** · Open `LumaRecode.sln` in Visual Studio or run `./build.ps1` in PowerShell 7. Use `./tools/Package-Windows.ps1` to create packages.
- **macOS** · Open `macOS/LumaRecode.xcodeproj` in Xcode or run `sh tools/build-macos.sh Release --dmg`.
- **Shared core** · `engine/` contains the C ABI, C/C++ transcoding core, and Rust metadata modules. [Architecture](../architecture.md#english)

[Tools and distribution](../dependencies.md#english) · [Encoding benchmarks](../benchmarks/encoding-benchmark.md#english) · [QA guide](../../qa/README.md#english)

## License and credits

Original code is under the [MIT License](../../LICENSE). See [NOTICE](../../NOTICE) for third-party licenses and sources.

Codex for Windows wrote the project code using GPT-6.1 Sol and GPT-6.2 Sol, both at `xhigh`. [MIKU552](https://github.com/MIKU552) defines requirements, supplies test footage, and verifies the app on hardware.
