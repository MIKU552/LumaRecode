<p align="center">
  <img src="Brand/FrameFlow.svg" width="104" height="104" alt="FrameFlow icon" />
</p>
<h1 align="center">映流 · FrameFlow</h1>

[简体中文](README.md) | English

Video transcoding and color conversion for Windows 11. FrameFlow provides a native WinUI 3 interface and a command line for FFmpeg, with video compression, camera Log to HDR / SDR, and HDR to SDR workflows.

[Download](https://github.com/MIKU552/FrameFlow/releases/latest) · [Command line](docs/cli.en.md) · [Changelog](CHANGELOG.md)

## Features

- Quick share and Software archive presets for HEVC, AV1, and H.264, with MP4 / MKV output.
- Mi-Log, Pocket 3 D-Log M, and Samsung Log to HLG HDR or Rec.709 SDR; HLG / PQ to SDR.
- Source inspection for resolution, frame rate, bit depth, color tags, and audio tracks, with automatic defaults.
- Chroma sampling, Limited / Full range, and audio controls, including lossless FLAC audio.
- Progress, encoding fps, position, remaining time, and output format verification.
- Mica, light / dark / system themes, accent colors, and Chinese / English switching.
- Batch processing, configuration files, command previews, and JSON events through the CLI.

## Quick start

1. Download **FrameFlow.exe** from [Releases](https://github.com/MIKU552/FrameFlow/releases). Required runtimes are included.
2. Obtain a Windows build of [FFmpeg](https://ffmpeg.org/download.html) containing `ffmpeg.exe` and `ffprobe.exe`. FrameFlow checks `PATH` on first use; paths can also be selected with the gear button.
3. Drop a video or choose **Select video**, then confirm the output range, preset, and color conversion.
4. Choose the output path and select **Start conversion**.

Quick share uses Intel QSV. Software archive uses CPU encoding.

## Encoding and color

| Purpose | Encoders | Default container | Default audio |
| --- | --- | --- | --- |
| Quick share | Intel QSV: HEVC / AV1 / H.264 | MP4 | AAC for HEVC / H.264; Opus for AV1 |
| Software archive | x265 / SVT-AV1 / x264 | MKV | Copy source tracks |

Container, chroma sampling, color range, and audio can be selected separately. HDR uses 10-bit HEVC or AV1; SDR Quick share uses 8-bit. Software HEVC / AV1 archive uses 10-bit, and H.264 uses 8-bit.

Software HEVC / H.264 archive keeps source sampling and supports 4:2:0, 4:2:2, 4:4:4, and grayscale. RGB input defaults to 4:4:4 YCbCr; AV1 and Quick share use 4:2:0. Archive keeps source Limited / Full range; Quick share defaults to Limited. Sampling and range controls are in **Color conversion**; container and audio controls are in **Format & audio**.

FLAC accepts confirmed lossless integer audio, including integer PCM, and preserves each track's sample rate, channels, and integer precision. FLAC audio uses MKV.

SDR sources default to SDR output, HLG / PQ sources retain HDR, and recognized Log profiles convert to HLG. Input interpretation and Camera profile let you select the source type manually.

Video is re-encoded with the selected preset; audio can be copied or transcoded. See [recommended-presets.json](Worker/assets/recommended-presets.json) for the settings and the [encoding benchmark](docs/benchmarks/encoding-benchmark.en.md) for measurements.

## Requirements

- Windows 11 x64.
- User-provided FFmpeg / ffprobe with the required encoders and filters.
- Intel QSV support for Quick share.
- `zscale` and `lut3d` for Log conversion; `libplacebo` and Vulkan for HDR / Log to SDR.

Language and theme follow the system by default. Chinese systems use Simplified Chinese; other system languages default to English. Settings are stored at `%LOCALAPPDATA%\FrameFlow\settings.json`.

## Command line

Run commands through an output pipeline in PowerShell 7:

```powershell
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
./FrameFlow.exe --help | Out-Host
./FrameFlow.exe probe clip.mp4 --json | ConvertFrom-Json
./FrameFlow.exe convert clip.mp4 --preset archive-hevc | Out-Host
./FrameFlow.exe convert dlog.mp4 --input-kind log --profile pocket3 --range hdr | Out-Host
```

For first-time unattended use, read the component terms with `license --text`, then run `license --accept-terms`. See the [CLI guide](docs/cli.en.md) for commands, batch processing, and exit codes.

## Build from source

Requires .NET SDK 11.0.100, PowerShell 7, and Visual Studio C++ tools supported by Native AOT.

```powershell
git clone https://github.com/MIKU552/FrameFlow.git
cd FrameFlow
./build.ps1
```

The output is `dist/SelfContained/FrameFlow.exe`. See [development and distribution](docs/dependencies.md) for build options and dependency versions, and the [QA guide](qa/README.md) for checks.

## Project structure

```text
App/       WinUI 3 UI, appearance, localization, and job interaction
Bundle/    Single-file packaging entry
Worker/    CLI, source inspection, FFmpeg execution, and LUTs
Brand/     Icon sources and generation tools
tools/     License collection and localization code generation
qa/        Automated checks and memory sampling
docs/      User guides, development notes, and benchmark data
licenses/  Third-party licenses, notices, and provenance
```

## Development and license

Original project code was developed with **Codex for Windows**, using **[GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol)** with **Extra high (`xhigh`)** reasoning. **[MIKU552](https://github.com/MIKU552)** defines requirements, supplies hardware and footage, and verifies the application.

Original code, documentation, graphics, and generated Mi-Log / Samsung Log LUTs use the **[MIT License](LICENSE)**. Licenses and sources for third-party LUTs and runtimes are listed in [NOTICE](NOTICE) and [third-party components](Worker/assets/THIRD_PARTY.md).

## Documentation

- [Command line](docs/cli.en.md)
- [Development and distribution](docs/dependencies.md)
- [Performance](docs/performance.md)
- [Encoding benchmark](docs/benchmarks/encoding-benchmark.en.md)
- [Changelog](CHANGELOG.md)
