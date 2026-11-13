# Encoding preset benchmark

[简体中文](encoding-benchmark.zh-CN.md) | English · [Project home](../../README.en.md) · [Test data](data/README.md)

FrameFlow's encoding presets come from on-device tests recorded on October 29–30, 2026. The test configuration corresponds to version 3.4.7 and covers software encoding, Intel QSV, file size, quality, and transcoding speed.

## Presets

| Purpose | Encoder | Core settings | Overrides |
| --- | --- | --- | --- |
| Software archive AVC | libx264 | medium, CRF21 | `aq-mode=2:aq-strength=0.8` |
| Software archive HEVC | libx265 | medium, CRF23 | `rdoq-level=2:psy-rdoq=1` |
| Software archive AV1 | libsvtav1 | preset4, CRF32, tune0, lp0 | `enable-qm=1:qm-min=8:qm-max=15` |
| Faster AV1 alternative | libsvtav1 | preset6, CRF32, tune0, lp4 | Same QM settings, plus `hierarchical-levels=4` |
| Quick share AVC | h264_qsv | TU4, ICQ22 | `async_depth=4` |
| Quick share HEVC | hevc_qsv | TU4, ICQ24 | `async_depth=4` |
| Quick share AV1 | av1_qsv | TU4, ICQ26 | `async_depth=4` |

Configuration: [recommended-presets.json](../../Assets/recommended-presets.json). The `archive`, `archive_fast_av1`, `share`, and `audio` sections come from the test configuration. File hashes are in [preset-provenance.json](data/preset-provenance.json).

[Transcoder.cs](../../Core/Transcoder.cs) sets maximum GOP durations of about eight seconds for software encoding and two seconds for Quick share, with `pools=16` for x265 and `low_power=1` for QSV. Output settings select bit depth and chroma sampling. SVT-AV1's `lp` selects the parallelism level; `lp=0` chose level 5 on this machine.

Archive copies audio tracks by default. Quick share uses stereo AAC at 256 kbps for AVC / HEVC and stereo Opus VBR at 192 kbps / 48 kHz for AV1.

## Test environment

| Item | Recorded configuration |
| --- | --- |
| Computer | Xiaomi Book Pro 14 |
| CPU / GPU | Intel Core Ultra X7 358H / Intel Arc B390 |
| CPU resources / RAM | 16 cores, 16 logical processors; nominal 32 GB |
| GPU driver | 32.0.101.9150 |
| Power | AC connected; Windows Balanced plan |
| FFmpeg | 9.0.2-full_build-www.gyan.dev |
| x264 | core 165 r3223 0480cb0 |
| x265 | 4.3+45-116b875 |
| SVT-AV1 | 4.2.0-123-g0c1c4deca; AVX2 |
| OBS / HandBrake | 33.0.0 / 1.11.2, used in related original tests |

Environment record: [environment.json](data/environment.json). Quality tests use SDR; the HEVC / AV1 QSV sweep uses 10-bit P010 input.

## Sources and method

| Source | Original format | Role |
| --- | --- | --- |
| `Timeline 1.mov` | 3840×2160, 30 fps, HEVC Main, 8-bit 4:2:0, BT.709, approximately 214.85 Mbps | 4K animation / dance content |
| `Timeline 1.XQ.mov` | 1920×1080, 30 fps, ProRes XQ, 12-bit 4:4:4, BT.709 primaries / matrix, approximately 487.34 Mbps | 1080p animation / dance content; unspecified transfer interpreted as BT.709 |
| Synthetic desktop | 1080p / 4K, 60 unique frames per second | Small text, gradients, light/dark backgrounds, moving windows |

The 1080p and 4K tests use their respective original files. Samples are grouped as follows:

- `screen`: four seconds each from positions 20, 75, and 125, totaling 12 seconds and 360 frames.
- `holdout`: seconds 45–63, used to validate the selected settings.
- `desktop`: synthetic desktop content with small text, gradients, and moving windows.

Quality references use native-resolution 10-bit 4:2:0 FFV1, BT.709 limited. Scores are measured against these references. The initial sweep uses VMAF v0.6.1, PSNR, and SSIM; the advanced sweep uses VMAF NEG. Sampling strides are recorded in the data. P5 is the fifth percentile of sampled frame scores.

Parameter sweeps start from predecoded references. Separate source-file timing runs cover FFmpeg startup, decoding, format conversion, encoding, and muxing. The initial sweep contains 92 records. The advanced sweep contains 411: 277 software and 134 hardware trials, including 120 successful and 14 failed or stalled hardware trials.

## Software encoding: size and quality

Baselines use x264 medium / CRF20, x265 medium / CRF22, and SVT-AV1 preset6 / CRF32 / tune0 / lp0. Selected settings are the Software archive entries in the preset table above.

Mbps is the average bitrate of the video-only output file. Size change is `(selected bytes / baseline bytes − 1) × 100%`.

| Sample | Resolution | Codec | Old Mbps | New Mbps | Size change | Old NEG | New NEG |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| screen | 1080 | AVC | 7.152 | 6.326 | -11.5% | 97.591 | 97.538 |
| screen | 1080 | HEVC | 4.113 | 3.865 | -6.0% | 97.157 | 97.335 |
| screen | 1080 | AV1 | 3.901 | 3.396 | -12.9% | 97.368 | 97.569 |
| screen | 4k | AVC | 20.857 | 16.564 | -20.6% | 97.420 | 97.505 |
| screen | 4k | HEVC | 9.320 | 8.795 | -5.6% | 97.115 | 97.346 |
| screen | 4k | AV1 | 8.529 | 7.517 | -11.9% | 96.629 | 97.021 |
| holdout | 1080 | AVC | 8.944 | 7.944 | -11.2% | 98.538 | 98.499 |
| holdout | 1080 | HEVC | 4.944 | 4.663 | -5.7% | 98.174 | 98.314 |
| holdout | 1080 | AV1 | 4.952 | 4.244 | -14.3% | 98.475 | 98.606 |
| holdout | 4k | AVC | 23.616 | 19.544 | -17.2% | 98.399 | 98.481 |
| holdout | 4k | HEVC | 10.951 | 10.350 | -5.5% | 98.171 | 98.333 |
| holdout | 4k | AV1 | 10.158 | 8.988 | -11.5% | 97.878 | 98.204 |
| desktop | 1080 | AVC | 1.995 | 1.910 | -4.2% | 95.506 | 95.399 |
| desktop | 1080 | HEVC | 1.915 | 1.911 | -0.2% | 95.575 | 95.398 |
| desktop | 1080 | AV1 | 2.338 | 2.015 | -13.8% | 97.240 | 97.298 |
| desktop | 4k | AVC | 6.830 | 7.249 | +6.1% | 95.687 | 95.607 |
| desktop | 4k | HEVC | 6.514 | 6.584 | +1.1% | 95.936 | 95.785 |
| desktop | 4k | AV1 | 7.616 | 6.913 | -9.2% | 97.334 | 97.369 |

Data: [software-pairs.csv](data/software-pairs.csv), 18 pairs. Trial parameters are in [advanced-trials.csv](data/advanced-trials.csv).

On the animation samples, x264 reduces size by about 11–21%, x265 by 5–6%, and AV1 by 12–14%. The 4K desktop size changes are +6.1% for AVC, +1.1% for HEVC, and −9.2% for AV1.

x265 uses CRF23; CRF23.5 trial notes record more visible dark-region blocking. AV1 uses preset4 with QM8. QSV retains the driver's automatic reference structure.

## Source-file transcoding speed

### Software archive

Runs use seconds 20–32 of each original file after warmup, in baseline / selected / selected / baseline order. The table gives the median fps and CPU usage and the maximum peak working set across the two selected runs. CPU is normalized to all 16 logical processors; memory is the FFmpeg process working set.

| Resolution | Codec | fps | CPU % | Peak working set GiB |
| --- | --- | ---: | ---: | ---: |
| 1080 | AVC | 83.5 | 88.1 | 1.13 |
| 1080 | HEVC | 29.4 | 74.5 | 1.22 |
| 1080 | AV1 | 24.2 | 67.4 | 2.47 |
| 4k | AVC | 23.4 | 87.5 | 3.80 |
| 4k | HEVC | 9.0 | 88.0 | 3.65 |
| 4k | AV1 | 10.5 | 81.1 | 8.61 |

Full records: [archive-native-timings.csv](data/archive-native-timings.csv), 24 runs.

The longer AV1 test uses seconds 20–80 of the 4K original, totaling 1,800 frames, with one run per configuration:

| Configuration | fps | Elapsed | Peak working set GiB |
| --- | ---: | ---: | ---: |
| preset4 / lp0, default | 11.45 | 157.17 s | 9.72 |
| preset6 / lp4 / H4, faster alternative | 17.02 | 105.77 s | 5.11 |

Data: [av1-long-timings.csv](data/av1-long-timings.csv).

### QSV Quick share

Each codec / resolution combination runs once on seconds 20–50, totaling 900 frames. The 1080p ProRes source uses CPU decoding; the 4K HEVC source uses QSV decoding. Audio is AAC at 160 kbps.

| Resolution | Codec | fps | CPU % |
| --- | --- | ---: | ---: |
| 1080 | AVC | 198.0 | 62.1 |
| 1080 | HEVC | 172.2 | 58.2 |
| 1080 | AV1 | 208.0 | 70.3 |
| 4k | AVC | 55.2 | 1.5 |
| 4k | HEVC | 55.6 | 1.3 |
| 4k | AV1 | 56.4 | 1.5 |

Data: [share-native-timings.csv](data/share-native-timings.csv).

## QSV VBR comparison

Additional tests use TU4 / TU7, VBR, and 60 fps. Average / maximum bitrate targets are 12 / 20 Mbps at 1080p and 30 / 50 Mbps at 4K. Each frame of the same 30 fps animation reference is repeated twice. The table shows TU7 results using VMAF:

| Resolution | Codec | TU | Actual Mbps | VMAF | Sampled P5 |
| --- | --- | ---: | ---: | ---: | ---: |
| 1080 | AV1 | 7 | 12.433 | 95.257 | 93.889 |
| 1080 | H264 | 7 | 12.500 | 94.797 | 91.977 |
| 1080 | HEVC | 7 | 12.343 | 94.460 | 91.509 |
| 4k | AV1 | 7 | 31.001 | 95.074 | 93.311 |
| 4k | H264 | 7 | 31.418 | 94.819 | 92.371 |
| 4k | HEVC | 7 | 30.422 | 94.450 | 92.231 |

Data: [qsv-vbr-60fps.csv](data/qsv-vbr-60fps.csv), 12 runs. Additional OBS encoder records are in [obs-recording-summary.csv](data/obs-recording-summary.csv).

## Data files

The [data guide](data/README.md) lists fields, calculations, and source files. CSVs retain recorded precision; tables use rounded values. `parameters` stores requested settings. `metric_reused_from` identifies quality scores reused after matching the encoded bitstreams.
