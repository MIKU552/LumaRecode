# Command line

[简体中文](cli.zh-CN.md) · [README](../README.en.md)

Run `FrameFlow.exe` in **PowerShell 7**. No arguments opens the GUI; subcommands such as `probe` and `convert` run the CLI.

The program uses the Windows GUI subsystem. **Pipe text output to `Out-Host`, or JSON output to `ConvertFrom-Json`** to wait for completion and read `$LASTEXITCODE`. Other programs can wait through a process API.

Output is UTF-8. If the session uses a legacy code page such as GBK, set the encoding below for readable Chinese help/progress; this affects only the current PowerShell session. JSON uses standard escapes for non-ASCII characters, preserving decoded Unicode paths even under legacy code pages.

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

The current GUI component-term acceptance is reused. For unattended first use, read the terms and supply `--accept-terms`; CLI operations never show dialogs. `--no-settings` ignores saved GUI paths/consent and prevents setting writes, so scripts using it must explicitly accept the terms.

Tools resolve from PATH, then saved GUI paths. Override with `--ffmpeg PATH --ffprobe PATH`; `--python PATH` overrides bundled Python. `--lang system|en|zh` selects the output language.

## Commands and options

| Command | Purpose |
| --- | --- |
| `probe INPUT` | Video/audio details, component depth, color/Log evidence, streams, tags and first-frame side data |
| `presets` | Seven tested encoding presets and their actual encoder settings |
| `profiles` | Built-in Mi-Log, Pocket 3 D-Log M and Samsung Log profiles/LUTs |
| `doctor` | Tool versions and encoder/filter lists; hardware modes depend on the GPU and driver |
| `license [--text]` | MIT project license, component terms, full notice directory and manifest |
| `plan INPUT...` | Resolve source/options and print FFmpeg argv, working directory and expected colors without encoding |
| `convert INPUT...` | Convert one file, a directory or wildcard matches sequentially, then verify output |

Every command supports `--help`. Probe/catalog/doctor/license results are JSON. Human conversion progress goes to stderr; results go to stdout.

`convert` and `plan` expose the GUI's encoding and color controls:

| Option | Values |
| --- | --- |
| `--preset` | `share-avc`, `share-hevc`, `share-av1`, `archive-avc`, `archive-hevc`, `archive-av1`, `archive-av1-fast` |
| `--purpose` | `share` (Intel QSV, default), `archive` (CPU) |
| `--codec` | `hevc` (default), `av1`, `avc` |
| `--range` / `--dynamic-range` | `auto` (default), `sdr`, `hdr` |
| `--transform` | `auto` (default), `none`, `log-hdr`, `log-sdr`, `hdr-sdr` |
| `--profile` | `auto`, `milog`, `pocket3`, `samsung` |
| `--input-kind` | `auto`, `sdr`, `hlg`, `pq`, `log` |
| `--sampling` | `420` (default), `422` (software HEVC/AVC only) |
| `--container` | `auto` (share MP4, archive MKV), `mp4`, `mkv` |
| `--audio` | `auto`, `aac`, `opus`, `copy`; defaults match the GUI |
| `--variant` | `standard`, `fast` (software AV1 only) |
| `--cpu-decode` / `--no-cpu-decode` | Force software decode / choose the automatic decode path |
| `--duration SECONDS` | Encode a positive finite duration for a sample |
| `--config FILE.json` | Load encoding options, overridden by explicit CLI fields |

Auto mode retains SDR or HDR, and converts identified Log to HLG. Unidentified Log requires `--profile`; unknown transfer tags require `--input-kind`. Invalid combinations fail explicitly, including HDR+AVC, QSV+422 and SDR-to-HDR.

```powershell
./FrameFlow.exe convert clip.mp4 --preset archive-hevc | Out-Host
./FrameFlow.exe convert dlog.mp4 --input-kind log --profile pocket3 --range hdr | Out-Host
./FrameFlow.exe convert hdr.mp4 --range sdr | Out-Host
./FrameFlow.exe convert dlog.mp4 --input-kind log --profile pocket3 --range sdr | Out-Host
./FrameFlow.exe convert input.mov --preset archive-hevc --sampling 422 --audio copy | Out-Host
./FrameFlow.exe plan clip.mp4 --preset archive-av1-fast --json | ConvertFrom-Json
```

HDR/Log-to-SDR needs FFmpeg libplacebo and Vulkan. A JSON config accepts the encoding fields above, using underscore keys, plus `preset`:

```json
{"preset":"archive-hevc","range":"auto","sampling":"422","audio":"copy","cpu_decode":true}
```

Precedence: GUI-equivalent defaults → chosen preset → config fields → explicit CLI fields. CLI `--preset` selects the preset in preference to the config's preset; other config fields still apply. Arbitrary FFmpeg arguments are not accepted in this config. `range` describes dynamic range; file destinations use `--output`.

## Batch and destination policy

```powershell
./FrameFlow.exe convert a.mp4 b.mov --output-dir "D:\Exports" | Out-Host
./FrameFlow.exe convert "D:\Videos" --recursive --output-dir "D:\Exports" | Out-Host
./FrameFlow.exe convert "D:\Videos\**\*.mp4" --recursive --output-dir "D:\Exports" --continue-on-error --json | ConvertFrom-Json
./FrameFlow.exe convert clip.mp4 --output "D:\Exports\result.mp4" --overwrite | Out-Host
```

Quote patterns; `**` requires `--recursive`. Directory scans filter common video extensions and preserve relative subdirectories. Wildcards use the matched files' common parent; explicit files use their filenames. Colliding destinations are rejected before conversion. Output directories are created as needed.

Default names are `stem_SDR-or-HDR_codec_purpose.mp4-or-mkv`, beside the source. `--output` requires one input and cannot combine with `--output-dir`. Existing outputs fail by default: use `--skip-existing` or `--overwrite`. Replacement occurs only after a temporary output successfully encodes and verifies; source files cannot be destinations. Failures and normal cancellation clean temporary files.

The default stops on error. `--continue-on-error` processes other valid inputs but still returns a failure exit code if any fail. Jobs are sequential to avoid competing for the same hardware encoder.

Planned `argv` displays the requested final filename. Actual conversion substitutes a file in a random temporary directory, then moves/replaces it after verification. FFmpeg itself retains `-n`; `--overwrite` is implemented by the application's atomic replacement.

## Automation contract

`convert --json` / `plan --json` produce JSON Lines on stdout, with `schema_version: 1`. Events: `start`, `log`, `status`, `progress`, `done`, `skipped`, `error`, `cancelled`, `summary`; planning emits `plan`. Job records include `job`, `total_jobs`, `input`, `output` and event `data`.

Progress includes `fps`, `percent`, `position_seconds`, `duration_seconds`, `elapsed_seconds`, `eta_seconds`; unknown percent/ETA are null. Errors include a stable `code` and readable `message`. `--log FILE` appends the same events to a JSON Lines file; `--verbose` also prints FFmpeg logs in human mode.

```powershell
./FrameFlow.exe convert clip.mp4 --json --log job.jsonl | ForEach-Object { $_ | ConvertFrom-Json }
$LASTEXITCODE
```

| Exit | Meaning |
| --- | --- |
| 0 | Success, including requested skips |
| 1 | Probe/encode/verification failure, or partial batch failure |
| 2 | Invalid arguments, config or option combination |
| 3 | Missing/invalid runtime or FFmpeg tool |
| 4 | Missing/unmatched input, or ambiguous input/Log curve |
| 5 | Output/log path conflict or error |
| 6 | Component terms not accepted |
| 130 | Cooperative CLI cancellation |

Use process exit codes or `$LASTEXITCODE`; parse JSON `code` fields instead of localized error text.

Ctrl+C in a PowerShell 7 pipeline also interrupts the shell pipeline. The job stops and temporary output is cleaned, but the pipeline may not deliver the final `cancelled` event or update `$LASTEXITCODE` to 130. A process API that signals the CLI's console and waits for its graceful exit can read 130. Automation should distinguish host interruption from a normally returned CLI error code.
