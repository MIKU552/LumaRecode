# Command line

[简体中文](cli.zh-CN.md) · English · [日本語](cli.ja.md) · [Español](cli.es.md) · [README](../README.en.md)

Run `LumaRecode.exe` in **PowerShell 7**. No arguments opens the GUI; subcommands such as `probe` and `convert` run the CLI.

The program uses the Windows GUI subsystem. **Pipe text output to `Out-Host`, or JSON output to `ConvertFrom-Json`** to wait for completion and read `$LASTEXITCODE`. Other programs can wait through a process API.

Output uses UTF-8. The examples below set the current PowerShell session encoding, display help and tool information, and convert a video.

```powershell
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
./LumaRecode.exe --help | Out-Host
./LumaRecode.exe --version | Out-Host
./LumaRecode.exe license --text | Out-Host
./LumaRecode.exe license --accept-terms | Out-Host
./LumaRecode.exe doctor --json | ConvertFrom-Json
./LumaRecode.exe probe "D:\Videos\clip.mp4" --json | ConvertFrom-Json
./LumaRecode.exe convert "D:\Videos\clip.mp4" | Out-Host
```

GUI and CLI share component-term acceptance. For first-time batch use, read the terms with `license --text`, then run `license --accept-terms` or add `--accept-terms` to the task. `--no-settings` selects independent configuration, ignores saved GUI settings, and disables settings writes; add `--accept-terms` in this mode.

Tools resolve from PATH, then saved GUI paths. Override with `--ffmpeg PATH --ffprobe PATH`; `--python PATH` overrides bundled Python. `--lang system|en|zh|ja|es` selects the output language.

## Commands and options

| Command | Purpose |
| --- | --- |
| `probe INPUT` | Video/audio format, bit depth, color/Log identification, streams, tags, and first-frame side data |
| `presets` | Encoding presets and encoder settings |
| `profiles` | Built-in Mi-Log, Pocket 3 D-Log M and Samsung Log profiles/LUTs |
| `doctor` | Tool versions and encoder/filter lists; hardware modes depend on the GPU and driver |
| `license [--text]` | MIT project license, component terms, full notice directory and manifest |
| `plan INPUT...` | Preview conversion options, FFmpeg arguments, working directory, and output colors |
| `convert INPUT...` | Convert one file, a directory or wildcard matches sequentially, then verify output |

Every command supports `--help`. Probe/catalog/doctor/license results are JSON. Text progress goes to stderr; results go to stdout.

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
| `--sampling` | `auto` (default), `source`, `420`, `422`, `444`, `gray`; Auto keeps source sampling for software HEVC/AVC archive, selects 4:4:4 for RGB, and uses 4:2:0 for AV1/share; 422 / 444 / gray use software HEVC/AVC |
| `--color-range` | `auto` (default), `source`, `limited`, `full`; Auto keeps source range for archive and converts to Limited for share |
| `--bit-depth` | `auto`, `8`, `10`, `12`; archive follows source depth and encoder limits; share defaults to 8-bit SDR / 10-bit HDR |
| `--container` | `auto` (share MP4, archive MKV), `mp4`, `mkv` |
| `--audio` | `auto`, `aac`, `opus`, `copy`, `flac`; defaults match the GUI |
| `--variant` | `standard`, `fast` (software AV1 only) |
| `--cpu-decode` / `--no-cpu-decode` | Force software decode / choose the automatic decode path |
| `--duration SECONDS` | Encode a positive finite duration for a sample |
| `--config FILE.json` | Load encoding options, overridden by explicit CLI fields |

Auto mode selects SDR → SDR, HLG / PQ → HDR, and Log → HLG. Use `--profile` to select a Log curve and `--input-kind` to set the source type. HDR output uses HEVC / AV1.

Sampling and depth are checked against the source. An 8-bit 4:2:0 source stays 8-bit 4:2:0; a 10-bit 4:2:2 source allows 8 / 10 bits and 4:2:0 / 4:2:2. Software HEVC supports 8 / 10 / 12 bits; software AVC and AV1 support 8 / 10 bits. `--sampling source` converts RGB to 4:4:4 YCbCr. Grayscale can use `source` or `gray`.

Range conversion processes pixel values. For sources with missing range tags, pixel format and camera profile determine the input interpretation.

`--audio flac` checks every audio track and accepts confirmed lossless integer input, preserving sample rates, channels, and integer precision. FLAC audio uses MKV.

```powershell
./LumaRecode.exe convert clip.mp4 --preset archive-hevc | Out-Host
./LumaRecode.exe convert dlog.mp4 --input-kind log --profile pocket3 --range hdr | Out-Host
./LumaRecode.exe convert hdr.mp4 --range sdr | Out-Host
./LumaRecode.exe convert dlog.mp4 --input-kind log --profile pocket3 --range sdr | Out-Host
./LumaRecode.exe convert source-422.mov --preset archive-hevc --sampling 422 --audio copy | Out-Host
./LumaRecode.exe plan clip.mp4 --preset archive-av1-fast --json | ConvertFrom-Json
./LumaRecode.exe convert lossless.mov --preset archive-hevc --sampling source --color-range source --audio flac | Out-Host
```

HDR/Log-to-SDR needs FFmpeg libplacebo and Vulkan. A JSON config accepts the encoding fields above, using underscore keys, plus `preset`:

```json
{"preset":"archive-hevc","range":"auto","sampling":"420","bit_depth":8,"audio":"copy","cpu_decode":true}
```

Precedence: GUI-equivalent defaults → chosen preset → config fields → explicit CLI fields. CLI `--preset` selects the preset in preference to the config's preset; other config fields still apply. Config keys use underscores, such as `color_range`. `range` describes dynamic range; file destinations use `--output`.

## Batch and destination policy

```powershell
./LumaRecode.exe convert a.mp4 b.mov --output-dir "D:\Exports" | Out-Host
./LumaRecode.exe convert "D:\Videos" --recursive --output-dir "D:\Exports" | Out-Host
./LumaRecode.exe convert "D:\Videos\**\*.mp4" --recursive --output-dir "D:\Exports" --continue-on-error --json | ConvertFrom-Json
./LumaRecode.exe convert clip.mp4 --output "D:\Exports\result.mp4" --overwrite | Out-Host
```

Quote patterns; `**` requires `--recursive`. Directory scans filter common video extensions and preserve relative subdirectories. Wildcards use the matched files' common parent; explicit files use their filenames. Colliding destinations are rejected before conversion. Output directories are created as needed.

Default names are `stem_SDR-or-HDR_codec_purpose.mp4-or-mkv`, beside the source. Use `--output` for one file or `--output-dir` for a batch; these options are mutually exclusive. Existing outputs fail by default: use `--skip-existing` or `--overwrite`. A temporary output is encoded and verified before replacement. Source and destination paths must differ. Failures and cancellation clean up temporary files.

Jobs run sequentially and stop on error by default. `--continue-on-error` processes the remaining inputs, with a batch exit code reflecting any failures.

Planned `argv` shows the final output path. Conversion writes to a temporary directory, verifies the result, and moves it to the destination. `--overwrite` uses atomic replacement.

## Scripting and progress

`convert --json` / `plan --json` produce JSON Lines on stdout, with `schema_version: 1`. Events: `start`, `log`, `status`, `progress`, `done`, `skipped`, `error`, `cancelled`, `summary`; planning emits `plan`. Job records include `job`, `total_jobs`, `input`, `output` and event `data`.

Progress includes `fps`, `percent`, `position_seconds`, `duration_seconds`, `elapsed_seconds`, `eta_seconds`; unknown percent/ETA are null. Errors include a stable `code` and readable `message`. `--log FILE` appends the same events to a JSON Lines file; `--verbose` prints FFmpeg logs in text mode.

```powershell
./LumaRecode.exe convert clip.mp4 --json --log job.jsonl | ForEach-Object { $_ | ConvertFrom-Json }
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

Read the process exit code or `$LASTEXITCODE` for the result, and JSON `code` fields for error handling.

Interactive Ctrl+C interrupts both the PowerShell pipeline and the conversion job and cleans up temporary output. To read cancellation events and the exit code, send a console cancellation signal through a process API and wait for the program to exit. Cooperative cancellation returns 130.
