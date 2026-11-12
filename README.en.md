# 映流 · FrameFlow

[简体中文](README.md) | English

A video transcoding app for Windows 11, with a native graphical interface for FFmpeg. Compress regular footage, convert camera Log to HDR or SDR, and tone-map HDR to SDR. Export MP4 or MKV using HEVC, AV1, or H.264.

[Download the latest release](https://github.com/MIKU552/FrameFlow/releases/latest) · [Changelog — Chinese](CHANGELOG.md) · [Build from source](#build-from-source)

## Quick start

1. Download **FrameFlow.exe** from [Releases](https://github.com/MIKU552/FrameFlow/releases) and run it. This same EXE provides the [command line](docs/cli.en.md). The Windows x64 single-file application includes the UI runtime, conversion worker, and LUTs. You do not need to install .NET or Python.
2. Obtain a Windows build of [FFmpeg](https://ffmpeg.org/download.html). The **Windows EXE Files** section links to prebuilt packages. You need both **ffmpeg.exe** and **ffprobe.exe**. FrameFlow first checks `PATH`; if they are missing, select them in the tool-path dialog. Use the gear button at the top right to change the paths later.
3. Drop a video into the source card or choose **Select video**. FrameFlow reads resolution, frame rate, bit depth, color tags, and audio tracks, then checks available Log and HDR metadata.
4. Confirm SDR or HDR output, purpose, and video codec. SDR footage defaults to SDR, HDR footage defaults to retaining HDR, and recognized Log profiles default to HLG HDR conversion.
5. Confirm the output path and choose **Start conversion**. The footer shows progress, encoding fps, current position, and estimated time remaining. The conversion log contains the full FFmpeg command and error details.

**First run:** Quick share uses Intel QSV hardware encoding. Your GPU must support the selected codec. Choose Software archive for CPU encoding if suitable Intel hardware is unavailable.

## Features

- **Video compression:** presets grouped into Quick share and Software archive, with HEVC, AV1, and H.264.
- **Color conversion:** separate camera Log → HLG HDR, Log → Rec.709 SDR, and HLG/PQ HDR → SDR workflows.
- **Format controls:** independent MP4/MKV, chroma sampling, and audio choices. Accept 4:2:2 sources and convert them to 4:2:0.
- **Source inspection:** detailed video and per-track audio information; defaults based on source metadata, with manual overrides when the input type or camera curve is uncertain.
- **Output verification:** check bit depth, sampling, color tags, and required static HDR metadata. Keep the source, avoid overwriting existing outputs, and clean up this job's temporary output on cancellation.
- **Native Windows UI:** WinUI 3, Mica, light/dark/system themes, system or custom accent colors, and instant Chinese/English switching.

## Encoding and output

| Purpose | Encoders | Automatic container | Default audio |
| --- | --- | --- | --- |
| Quick share | Intel QSV: HEVC / AV1 / H.264 | MP4 | AAC for HEVC/H.264; Opus for AV1 |
| Software archive | CPU: x265 / SVT-AV1 / x264 | MKV | Copy source audio |

You can override the container and audio policy. Format & audio offers AAC, Opus, and audio copying. When copying audio into MP4, FrameFlow first checks container compatibility.

- **HDR:** 10-bit output using HEVC or AV1.
- **SDR:** Quick share outputs 8-bit. Software archive outputs 10-bit for HEVC/AV1 and 8-bit for H.264.
- **Chroma sampling:** 4:2:0 by default; software HEVC/H.264 archives can use 4:2:2.

Encoder settings appear in the UI. The full preset catalog is in [recommended-presets.json](Worker/assets/recommended-presets.json). Video is re-encoded; choosing Copy source audio changes only audio handling.

The core encoder settings come from actual tests on the maintainer's **Xiaomi Book Pro 14 / Core Ultra X7 358H / Arc B390 / 32 GB**. See the [encoding benchmark report](docs/benchmarks/encoding-benchmark.en.md) for methodology, quality and size, complete-pipeline speed, parameter choices, and retained measurements. These are defaults for one machine, a limited set of sources, and specific software versions. Functional checks of HDR, SDR8, 4:2:2, and LUT conversion do not constitute a fresh quality-tuning sweep for every combination.

## Color conversion

| Source | Processing | Output |
| --- | --- | --- |
| Regular SDR | No LUT; preserve / normalize | Rec.709 SDR |
| Camera Log | Built-in LUT for the selected curve | BT.2020 HLG HDR or Rec.709 SDR |
| HLG / PQ HDR | Retain HDR | Retain the source HLG / PQ type |
| HLG / PQ HDR | HDR → SDR tone mapping | Rec.709 SDR |

Built-in profiles cover **Xiaomi Mi-Log, DJI Osmo Pocket 3 D-Log M, and Samsung Log**. Automatic detection depends on source metadata. Check Input interpretation and Camera curve when tags are missing or the profile is uncertain.

Scope:

- Log conversion requires high-bit-depth originals from the matching camera. The LUTs use fixed exposure and highlight mappings; they cannot recover clipped source highlights.
- SDR → HDR conversion and reconstruction of HDR10+ / Dolby Vision dynamic metadata are outside the current scope.
- HDR playback at the receiving end depends on the device, player, and sharing platform.

## Requirements

| Component | Requirement |
| --- | --- |
| Operating system | Windows 11 x64 |
| FFmpeg / ffprobe | Supplied by the user; the chosen build must include the decoders, encoders, and filters required for the task |
| Intel QSV | Required only for Quick share; the GPU and driver must support the selected codec |
| Log conversion | FFmpeg `zscale` and `lut3d` filters |
| Log / HDR → SDR | FFmpeg `libplacebo` and a working Vulkan driver; Log sources also require the Log conversion filters above |

Supported inputs depend on the selected FFmpeg build's decoding capabilities. If an encoder or filter is missing, FrameFlow lists the requirement. Change tool paths using the gear button.

Language and theme follow the system by default. Chinese system locales use Simplified Chinese; other system languages fall back to English. Settings are stored in `%LOCALAPPDATA%\FrameFlow\settings.json`.

## Command line

The CLI shares the GUI's presets, LUTs, detection, and output verification. It supports files, directories, wildcard batches, JSON events, command planning, config files, Ctrl+C cancellation, and documented exit codes.

```powershell
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
./FrameFlow.exe --help | Out-Host
./FrameFlow.exe probe clip.mp4 --json | ConvertFrom-Json
./FrameFlow.exe convert clip.mp4 --preset archive-hevc | Out-Host
./FrameFlow.exe convert dlog.mp4 --input-kind log --profile pocket3 --range hdr | Out-Host
./FrameFlow.exe convert "D:\Videos" --recursive --output-dir "D:\Exports" --json | ConvertFrom-Json
```

PowerShell 7 is the target shell; use an output pipeline to wait for GUI-subsystem EXE commands. For unattended first use, read `license --text`, then run `license --accept-terms`. See the [complete CLI reference](docs/cli.en.md).

## Build from source

Use Windows 11 x64, **.NET SDK 11.0.100** (pinned by `global.json`), and PowerShell 7. The default build does not require an installed Python interpreter.

```powershell
git clone https://github.com/MIKU552/FrameFlow.git
cd FrameFlow
.\build.ps1
```

The script restores locked NuGet dependencies, downloads and verifies the official Python 3.15.2 embeddable distribution, collects full legal notices, and publishes a single EXE containing .NET 11, Windows App SDK 2.6.5, and Python. PyInstaller is no longer used. FFmpeg remains user-supplied. First launch requests acceptance of the bundled third-party binary terms; full notices remain accessible from Tool paths.

Output:

```text
dist/SelfContained/FrameFlow.exe
```

`./build.ps1 -Deployment FrameworkDependent` optionally builds a smaller ZIP requiring a separately installed .NET 11 Runtime, Windows App Runtime 2.6, and Python 3.14 or later, with tool paths configured in the app. The default single EXE bundles the required runtimes.

Optional artwork tools are pinned in `Brand/requirements.txt`; normal builds do not require Qt / PySide6. See [dependency notes](docs/dependencies.md) for versions, support, and distribution evidence.

Run the language and resource checks:

```powershell
dotnet run --project qa/LanguageChecks/LanguageChecks.csproj -c Release
```

## Project structure

```text
App/                  WinUI 3 UI, appearance, localization, and job interaction
Worker/               Command line, source inspection, FFmpeg commands, and output verification
Worker/assets/        LUTs, encoding presets, and LUT provenance
licenses/             Original legal texts, notices, and version manifest
tools/                Legal collection and release checks
Brand/                Branding assets and icon-generation scripts
qa/LanguageChecks/    Language-policy, translation, and resource checks
build.ps1             Windows single-file EXE build script
docs/benchmarks/      Encoding reports, preset provenance, and retained measurements
```

## Development and contributors

All original project code, UI implementation, build work, and repository maintenance were completed through **Codex for Windows**, using **[GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol)** with **Extra high (`xhigh`)** reasoning. The maintainer, **[MIKU552](https://github.com/MIKU552)**, defines requirements, supplies hardware and source footage, chooses approaches, and personally verifies behavior and results on the actual device.

This describes the project's development process. FFmpeg, WinUI 3, third-party LUTs, and other dependencies retain their original authorship and licenses.

Git commits credit both [Codex](https://github.com/codex) and MIKU552 and are signed with the maintainer's existing GPG key.

## License

Original project code, documentation, graphics, and the project's generated Mi-Log / Samsung Log LUTs use the **[MIT License](LICENSE)**. Commercial use, modification, and proprietary redistribution are permitted, with the copyright and permission notices retained in copies or substantial portions of the software. See [NOTICE](NOTICE) for third-party attribution.

Original project work uses MIT starting with **3.5.1**. Earlier releases retain their original licenses.

Third-party LUTs and runtimes retain their own terms. The current version uses production Windows App SDK packages and includes applicable licenses, notices, and component terms. See [third-party components](Worker/assets/THIRD_PARTY.md) and [dependency and distribution notes](docs/dependencies.md).

## Documentation and third-party components

- [Encoding benchmark report](docs/benchmarks/encoding-benchmark.en.md): empirical basis, scope, and data provenance for the built-in presets.
- [CHANGELOG.md](CHANGELOG.md): release history, in Chinese.
- [HISTORY.md](HISTORY.md): provenance of the imported early source snapshots, in Chinese.
- [THIRD_PARTY.md](Worker/assets/THIRD_PARTY.md): licenses and LUT sources. The Pocket 3 D-Log M → HLG LUT comes from the third-party OpenOSV project.
