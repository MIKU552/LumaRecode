# 编码预设测试 / Encoding preset benchmark

[简体中文](#简体中文) · [English](#english)

## 简体中文

FrameFlow 的编码预设来自 2026 年 10 月 29–30 日的本机测试。测试配置对应 3.4.7 版本，覆盖软件编码、Intel QSV、体积、画质和转码速度。

### 预设

| 用途 | 编码器 | 核心设置 | 额外设置 |
| --- | --- | --- | --- |
| 软件归档 AVC | libx264 | medium、CRF21 | `aq-mode=2:aq-strength=0.8` |
| 软件归档 HEVC | libx265 | medium、CRF23 | `rdoq-level=2:psy-rdoq=1` |
| 软件归档 AV1 | libsvtav1 | preset4、CRF32、tune0、lp0 | `enable-qm=1:qm-min=8:qm-max=15` |
| AV1 速度备用档 | libsvtav1 | preset6、CRF32、tune0、lp4 | 上述 QM，加 `hierarchical-levels=4` |
| 快速分享 AVC | h264_qsv | TU4、ICQ22 | `async_depth=4` |
| 快速分享 HEVC | hevc_qsv | TU4、ICQ24 | `async_depth=4` |
| 快速分享 AV1 | av1_qsv | TU4、ICQ26 | `async_depth=4` |

配置文件：[recommended-presets.json](../../Assets/recommended-presets.json)。其中 `archive`、`archive_fast_av1`、`share` 和 `audio` 四节来自测试配置，文件哈希见 [preset-provenance.json](data/preset-provenance.json)。

[Transcoder.cs](../../Core/Transcoder.cs) 设置软件编码最大 GOP 约 8 秒、快速分享约 2 秒，并为 x265 设置 `pools=16`、QSV 设置 `low_power=1`。位深和色度采样取自输出设置。SVT-AV1 的 `lp` 表示并行等级；本机 `lp=0` 选择等级 5。

归档默认复制音轨；分享 AVC / HEVC 使用 AAC 256 kbps 双声道，AV1 使用 Opus 192 kbps、48 kHz 双声道 VBR。

### 测试环境

| 项目 | 记录 |
| --- | --- |
| 设备 | Xiaomi Book Pro 14 |
| CPU / GPU | Intel Core Ultra X7 358H / Intel Arc B390 |
| CPU 资源 / 内存 | 16 核、16 逻辑处理器；标称 32 GB |
| GPU 驱动 | 32.0.101.9150 |
| 供电 / 电源计划 | 接通电源；Windows Balanced |
| FFmpeg | 9.0.2-full_build-www.gyan.dev |
| x264 | core 165 r3223 0480cb0 |
| x265 | 4.3+45-116b875 |
| SVT-AV1 | 4.2.0-123-g0c1c4deca；AVX2 |
| OBS / HandBrake | 33.0.0 / 1.11.2；用于原始相关测试 |

环境记录：[environment.json](data/environment.json)。画质测试使用 SDR；HEVC / AV1 的 QSV 筛选输入为 10 位 P010。

### 素材与方法

| 素材 | 原始规格 | 用途 |
| --- | --- | --- |
| `Timeline 1.mov` | 3840×2160、30 fps、HEVC Main、8 位 4:2:0、BT.709，约 214.85 Mbps | 4K 动画 / 舞蹈内容 |
| `Timeline 1.XQ.mov` | 1920×1080、30 fps、ProRes XQ、12 位 4:4:4、BT.709 原色 / 矩阵，约 487.34 Mbps | 1080p 动画 / 舞蹈内容；未指定的传递函数按 BT.709 解释 |
| 合成桌面 | 1080p / 4K、每秒 60 个独立帧 | 小字、渐变、明暗背景与移动窗口压力测试 |

1080p 和 4K 使用各自的原片。测试分为三组：

- `screen`：原片第 20、75、125 秒起各 4 秒，合计 12 秒、360 帧。
- `holdout`：原片第 45–63 秒，用于验证所选配置。
- `desktop`：包含小字、渐变和移动窗口的合成桌面。

画质参考统一为原生分辨率的 10 位 4:2:0 FFV1、BT.709 limited。评分以该参考为基准。初轮使用 VMAF v0.6.1、PSNR 和 SSIM；高级筛选使用 VMAF NEG。采样步长记录在数据中，P5 为采样帧得分的第 5 百分位。

参数筛选从预解码参考开始；原片测速另行记录 FFmpeg 的启动、解码、格式处理、编码和封装。初轮包含 92 条记录，高级筛选包含 411 条记录：软件 277 条、硬件 134 条，其中硬件成功 120 条、失败或停滞 14 条。

### 软件编码：体积与画质

基线配置为 x264 medium / CRF20、x265 medium / CRF22，以及 SVT-AV1 preset6 / CRF32 / tune0 / lp0；所选配置为上方预设表中的软件归档参数。

表中 Mbps 为无音轨输出文件的平均码率。体积变化按 `(所选配置字节数 / 基线字节数 − 1) × 100%` 计算。

| 样本 | 分辨率 | 编码 | 旧 Mbps | 新 Mbps | 体积变化 | 旧 NEG | 新 NEG |
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

数据：[software-pairs.csv](data/software-pairs.csv)，共 18 对。试验参数见 [advanced-trials.csv](data/advanced-trials.csv)。

动画样本中，x264 体积减少约 11–21%，x265 减少约 5–6%，AV1 减少约 12–14%。4K 桌面样本的 AVC、HEVC 和 AV1 体积变化分别为 +6.1%、+1.1% 和 −9.2%。

x265 采用 CRF23；CRF23.5 的测试记录显示暗部块状变化更明显。AV1 采用 preset4 与 QM8。QSV 保留驱动自动参考结构。

### 原片转码速度

#### 软件归档

取原片 20–32 秒，预热后按“基线、所选配置、所选配置、基线”的顺序运行。下表为所选配置两次运行的 fps、CPU 中位数及峰值工作集最大值。CPU 按整机 16 个逻辑处理器归一化，内存记录 FFmpeg 进程工作集。

| 分辨率 | 编码 | fps | CPU % | 峰值工作集 GiB |
| --- | --- | ---: | ---: | ---: |
| 1080 | AVC | 83.5 | 88.1 | 1.13 |
| 1080 | HEVC | 29.4 | 74.5 | 1.22 |
| 1080 | AV1 | 24.2 | 67.4 | 2.47 |
| 4k | AVC | 23.4 | 87.5 | 3.80 |
| 4k | HEVC | 9.0 | 88.0 | 3.65 |
| 4k | AV1 | 10.5 | 81.1 | 8.61 |

完整记录：[archive-native-timings.csv](data/archive-native-timings.csv)，共 24 次。

AV1 长片段测试取 4K 原片 20–80 秒，共 1,800 帧，每个配置运行一次：

| 配置 | fps | 处理耗时 | 峰值工作集 GiB |
| --- | ---: | ---: | ---: |
| preset4 / lp0，默认 | 11.45 | 157.17 秒 | 9.72 |
| preset6 / lp4 / H4，速度备用 | 17.02 | 105.77 秒 | 5.11 |

数据：[av1-long-timings.csv](data/av1-long-timings.csv)。

#### QSV 快速分享

取原片 20–50 秒，共 900 帧，每个编码器与分辨率组合运行一次。1080p ProRes 使用 CPU 解码，4K HEVC 使用 QSV 解码；音频设置为 AAC 160 kbps。

| 分辨率 | 编码 | fps | CPU % |
| --- | --- | ---: | ---: |
| 1080 | AVC | 198.0 | 62.1 |
| 1080 | HEVC | 172.2 | 58.2 |
| 1080 | AV1 | 208.0 | 70.3 |
| 4k | AVC | 55.2 | 1.5 |
| 4k | HEVC | 55.6 | 1.3 |
| 4k | AV1 | 56.4 | 1.5 |

数据：[share-native-timings.csv](data/share-native-timings.csv)。

### QSV VBR 对照

附加测试使用 TU4 / TU7、VBR 和 60 fps。1080p 的平均 / 最高码率为 12 / 20 Mbps，4K 为 30 / 50 Mbps。输入为同一动画参考，每个 30 fps 源帧重复两次。下表列出 TU7 结果，评分使用 VMAF：

| 分辨率 | 编码 | TU | 实际 Mbps | VMAF | 采样 P5 |
| --- | --- | ---: | ---: | ---: | ---: |
| 1080 | AV1 | 7 | 12.433 | 95.257 | 93.889 |
| 1080 | H264 | 7 | 12.500 | 94.797 | 91.977 |
| 1080 | HEVC | 7 | 12.343 | 94.460 | 91.509 |
| 4k | AV1 | 7 | 31.001 | 95.074 | 93.311 |
| 4k | H264 | 7 | 31.418 | 94.819 | 92.371 |
| 4k | HEVC | 7 | 30.422 | 94.450 | 92.231 |

数据：[qsv-vbr-60fps.csv](data/qsv-vbr-60fps.csv)，共 12 次。附加 OBS 编码记录见 [obs-recording-summary.csv](data/obs-recording-summary.csv)。

### 数据文件

[数据说明](data/README.md)列出字段、计算方式和文件索引。CSV 保存原始精度，表格数值经过四舍五入。`parameters` 保存请求参数，`metric_reused_from` 标记核对相同码流后复用的画质分数。

## English

FrameFlow's encoding presets come from on-device tests recorded on October 29–30, 2026. The test configuration corresponds to version 3.4.7 and covers software encoding, Intel QSV, file size, quality, and transcoding speed.

### Presets

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

### Test environment

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

### Sources and method

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

### Software encoding: size and quality

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

### Source-file transcoding speed

#### Software archive

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

#### QSV Quick share

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

### QSV VBR comparison

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

### Data files

The [data guide](data/README.md#english) lists fields, calculations, and source files. CSVs retain recorded precision; tables use rounded values. `parameters` stores requested settings. `metric_reused_from` identifies quality scores reused after matching the encoded bitstreams.
