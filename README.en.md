<p align="center">
  <img src="Brand/LumaRecode.svg" width="104" height="104" alt="LumaRecode icon" />
</p>
<h1 align="center">映码 · LumaRecode</h1>

[简体中文](README.md) | English | [日本語](README.ja.md) | [Español](README.es.md)

Video transcoding and color conversion for Windows 11, built with C# and .NET. LumaRecode provides a native WinUI 3 interface and a command line for FFmpeg, with video compression, camera Log to HDR / SDR, and HDR to SDR workflows.

[Download](https://github.com/MIKU552/LumaRecode/releases/latest) · [Documentation](docs/README.md) · [Command line](docs/cli.en.md) · [Changelog](CHANGELOG.md)

## Features

- Frame preview and trimming, HDR / SDR stills, and Android Motion Photos with a selected cover frame.
- Quick share and Software archive presets for HEVC, AV1, and H.264, with MP4 / MKV output.
- DJI D-Log M, Samsung Log, Mi-Log, Apple Log and Apple Log 2 to HLG HDR or Rec.709 SDR; HLG / PQ to SDR.
- ProRes RAW development, Dolby Vision / HDR10+ detection and conversion, and dynamic metadata preservation with software encoding.
- Source inspection for resolution, frame rate, bit depth, color tags, and audio tracks, with automatic defaults.
- Chroma sampling, Limited / Full range, and audio controls, including lossless FLAC audio.
- Progress, encoding fps, position, remaining time, and output format verification.
- Windows notifications for completed and failed jobs, controlled by the checkbox beside Open folder.
- Mica, light / dark / system themes, accent colors, and Simplified Chinese, English, Japanese and Spanish.
- Batch processing, configuration files, command previews, and JSON events through the CLI.

## Quick start

1. Download the EXE for your device from [Releases](https://github.com/MIKU552/LumaRecode/releases). See the variants below. Required runtimes are included.
2. Obtain a Windows build of [FFmpeg](https://ffmpeg.org/download.html) containing `ffmpeg.exe` and `ffprobe.exe`. LumaRecode checks `PATH` on first use; paths can also be selected with the gear button.
3. Drop a video or choose **Choose video**, then confirm the output range, preset, and color conversion.
4. Choose the output path and select **Start conversion**.

Quick share uses Intel QSV. Software archive uses CPU encoding.

| Download | Platform and requirements |
| --- | --- |
| `LumaRecode.exe` | X64, using the common x64 instruction-set profile |
| `LumaRecode-APX.exe` | x64 with Intel APX support in both the CPU and Windows |
| `LumaRecode-ARM64.exe` | Windows ARM64, with a native ARM64 app and image codec |

ARM64 defaults to Software archive. Encoding speed depends on the external FFmpeg build, hardware and selected preset.

Dolby Vision / HDR10+ sources default to software HEVC with dynamic metadata preservation. Select Standard HDR for hardware quick sharing. Supported Dolby Vision profiles are single-layer 5, 8.1, 8.4, 10.1 and 10.4. ProRes RAW checks FFmpeg decoder compatibility, then develops with camera white balance, color matrix and gain, with HLG output by default. See [Color conversion](docs/color-conversion.md) for formats and processing paths.

## Preview, trimming and images

Open **Preview & frame trimming** on the video page to scrub, step through frames and set the first and last frames. The default is the whole video. GUI frame numbers start at 1 and include the selected last frame.

The **Images & Motion Photos** page exports the selected frame as HDR AVIF (BT.2020 / PQ, 10 or 12 bits), Ultra HDR JPEG, sRGB JPEG, or lossless sRGB PNG (8-bit or a 16-bit container).

JPEG and Ultra HDR JPEG can include Android Motion Photo video. Choose the cover and video range independently; the default is about three seconds around the cover. A single `MP.jpg` file follows [Google Motion Photo 1.0](https://developer.android.com/media/platform/motion-photo-format). Ultra HDR encoding uses Google's [libultrahdr](https://github.com/google/libultrahdr). Motion video defaults to CPU encoding, with Intel quick encoding available.

The frame preview is SDR. Export uses the selected image format. Frame-accurate video cuts re-encode audio as AAC, Opus or eligible FLAC.

Motion video keeps the source resolution by default, with optional 1080p and 720p limits. Dedicated short-clip presets use x264 fast / CRF20, x265 fast / CRF23, or Intel TU6. Audio is AAC-LC at 48 kHz, preserving mono or using stereo. See [motion-photo-presets.json](Assets/motion-photo-presets.json).

## Encoding and color

| Purpose | Encoders | Default container | Default audio |
| --- | --- | --- | --- |
| Quick share | Intel QSV: HEVC / AV1 / H.264 | MP4 | AAC for HEVC / H.264; Opus for AV1 |
| Software archive | x265 / SVT-AV1 / x264 | MKV | Copy source tracks |

Container, chroma sampling, bit depth, color range, and audio can be selected separately. Quick share defaults to 8-bit SDR and 10-bit HDR. Software archive keeps source bit depth within encoder limits: 8 / 10 / 12 bits for HEVC and 8 / 10 bits for AVC and AV1.

Software HEVC / H.264 archive keeps source sampling. RGB input defaults to 4:4:4 YCbCr; AV1 and Quick share use 4:2:0. Sampling and bit depth can be reduced within source limits: 8-bit 4:2:0 stays 8-bit 4:2:0, while 10-bit 4:2:2 allows 10 / 8 bits and 4:2:2 / 4:2:0. Archive keeps source Limited / Full range; Quick share defaults to Limited. Sampling, depth, and range are in **Color conversion**; container and audio are in **Format & audio**.

FLAC accepts confirmed lossless integer audio, including integer PCM, and preserves each track's sample rate, channels, and integer precision. FLAC audio uses MKV.

SDR sources default to SDR output, HLG / PQ sources retain HDR, and recognized Log profiles convert to HLG. Input interpretation and Camera profile let you select the source type manually.

Video is re-encoded with the selected preset; audio can be copied or transcoded. See [recommended-presets.json](Assets/recommended-presets.json) for the settings and the [encoding benchmark](docs/benchmarks/encoding-benchmark.en.md) for measurements.

## Requirements

- Windows 11 x64 or ARM64, with the instructions required by the selected variant.
- User-provided FFmpeg / ffprobe with the required encoders and filters.
- Intel QSV support for Quick share.
- `zscale` and `lut3d` for Log conversion; `libplacebo` and Vulkan for HDR / Log to SDR.

Language and theme follow the system by default. Chinese, English, Japanese and Spanish system languages are recognized; other languages default to English. Settings are stored at `%LOCALAPPDATA%\LumaRecode\settings.json`.

On first launch, LumaRecode imports language, appearance and tool paths from FrameFlow settings.

Source Sans 3, Source Han Sans, and Source Code Pro are included; no font installation is needed.

## Command line

Run commands through an output pipeline in PowerShell 7:

```powershell
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
./LumaRecode.exe --help | Out-Host
./LumaRecode.exe probe clip.mp4 --json | ConvertFrom-Json
./LumaRecode.exe convert clip.mp4 --preset archive-hevc | Out-Host
./LumaRecode.exe convert dlog.mp4 --input-kind log --profile pocket3 --range hdr | Out-Host
```

For first-time unattended use, read the component terms with `license --text`, then run `license --accept-terms`. See the [CLI guide](docs/cli.en.md) for commands, batch processing, and exit codes.

## Build from source

Requires .NET SDK 11.0.100, PowerShell 7, and Visual Studio C++ x64 / ARM64 build tools.

```powershell
git clone https://github.com/MIKU552/LumaRecode.git
cd LumaRecode
./build.ps1
./build.ps1 -Target X64
./build.ps1 -Target Apx
./build.ps1 -Target Arm64
```

Native is the default automatic mode: ARM64 on ARM64 Windows, APX on x64 Windows with APX support, and X64 otherwise. It optimizes for the build CPU and produces `dist/SelfContained/LumaRecode.exe`.

Explicit X64, Apx and Arm64 targets compile for their respective instruction sets and write to `dist/SelfContained-X64/`, `dist/SelfContained-Apx/` and `dist/SelfContained-Arm64/`. Releases contain these three fixed-target EXEs.

Open `LumaRecode.sln` in Visual Studio 2026 18.12 or later. Set `App` as the startup project and use Debug for debugging. `Release | Native` selects and optimizes for the build machine; `Release | x64`, `Release-APX | x64` and `Release | ARM64` select fixed targets. The `Packaging` project creates the single EXE. See [development and distribution](docs/dependencies.md) for setup and the [QA guide](qa/README.md) for checks.

## Project structure

```text
App/       WinUI 3 UI, appearance, localization, and job interaction
Bundle/    Single-file packaging entry
Packaging/ Visual Studio build and distribution entry
Core/      Source inspection, format policies, FFmpeg execution, and CLI
Assets/    LUTs, encoding presets, and source records
Brand/     Icon sources and generation tools
tools/     License collection and localization code generation
qa/        Automated checks and memory sampling
docs/      User guides, development notes, and benchmark data
licenses/  Third-party licenses, notices, and provenance
```

## Development and license

Original project code was developed with **Codex for Windows**, using **[GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol)** with **Extra high (`xhigh`)** reasoning. **[MIKU552](https://github.com/MIKU552)** defines requirements, supplies hardware and footage, and verifies the application.

Original code, documentation, graphics, and generated camera Log LUTs use the **[MIT License](LICENSE)**. Licenses and sources for third-party LUTs, fonts, and runtimes are listed in [NOTICE](NOTICE) and [third-party components](Assets/THIRD_PARTY.md).

## Documentation

- [Command line](docs/cli.en.md)
- [Development and distribution](docs/dependencies.md)
- [Performance](docs/performance.md)
- [Encoding benchmark](docs/benchmarks/encoding-benchmark.en.md)
- [Changelog](CHANGELOG.md)
