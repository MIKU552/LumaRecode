# 测试数据 · Benchmark data

[中文报告](../encoding-benchmark.zh-CN.md) · [English report](../encoding-benchmark.en.md)

本目录包含编码预设测试的环境、结果和参数记录。CSV 保留原始数值精度。

This directory contains the environment, results, and parameter records for the preset benchmark. CSV files preserve recorded numeric precision.

| 文件 / File | 内容 / Contents |
| --- | --- |
| [environment.json](environment.json) | 设备、版本、素材及方法 / Hardware, versions, sources, methodology |
| [preset-provenance.json](preset-provenance.json) | 四节预设匹配结果与完整文件哈希 / Four matched preset sections and full-file hashes |
| [quality-baseline.csv](quality-baseline.csv) | 初轮 92 条 / 92 initial trials; ordinary VMAF |
| [advanced-trials.csv](advanced-trials.csv) | 411 条高级筛选，含失败项 / 411 advanced trials, including failures |
| [software-pairs.csv](software-pairs.csv) | 18 对旧基线 / 新配置 / 18 baseline / selected software pairs |
| [archive-native-timings.csv](archive-native-timings.csv) | 软件原片测速 24 次 / 24 original-file software timing runs |
| [av1-long-timings.csv](av1-long-timings.csv) | 4K 一分钟 AV1 对比 2 次 / Two one-minute 4K AV1 runs |
| [share-native-timings.csv](share-native-timings.csv) | QSV 原片测速 6 次，早期音频策略 / Six original-file QSV runs, earlier audio policy |
| [qsv-vbr-60fps.csv](qsv-vbr-60fps.csv) | TU4 / TU7 VBR 对比 12 次 / 12 TU4 / TU7 VBR runs |
| [obs-recording-summary.csv](obs-recording-summary.csv) | 独立 60 fps 桌面 OBS 实录 21 次 / 21 OBS runs on a unique-frame 60 fps desktop |
| [source-manifest.json](source-manifest.json) | 留存原记录的相对文件名与 SHA-256 / Relative filenames and SHA-256 of retained source records |

## 字段与计算 / Fields and calculations

- `screen` 实为动画样本，`holdout` 是独立片段，`desktop` 是合成桌面。`screen` means animation excerpts; it does not mean desktop capture.
- `1080` = 1920×1080；`4k` = 3840×2160。`svt` 与软件表中的 `av1` 均指 SVT-AV1。`svt` and software `av1` labels both refer to SVT-AV1.
- `bytes` / `bytes_container` 为输出文件大小；`mbps` / `mbps_container` 为十进制 Mbps，含封装开销。File bitrate includes container overhead; it is not strictly elementary video-stream bitrate.
- `size_delta_pct = (selected_bytes / baseline_bytes - 1) * 100`；`vmaf_neg_delta = selected_vmaf_neg - baseline_vmaf_neg`。
- `fps = frames / wall_s`；GiB = `peak_working_set_bytes / 2**30`。CPU 以整机逻辑处理器归一化；`cpu_delta_pp` 是百分点增量。CPU is machine-normalized; OBS deltas are percentage points.
- `vmaf` 与 `vmaf_neg` 为不同指标；`metric_stride` 为采样步长，P5 为采样分数第 5 百分位。Ordinary VMAF and NEG are different metrics; P5 is the sampled-score fifth percentile.
- `returncode` / `invalid` 保留错误信息；空评分不是零分。`parameters` 是当时记录的 Python 字典文本，不能用 `eval` 读取；可用 `ast.literal_eval`。Parameters are requested settings, not guaranteed negotiated settings. Empty scores are missing, not zero.
- `metric_reused_from` 标记核对相同码流后复用的评分；筛选用的 `fps` 不能代替完整处理链速度。Reused metrics do not imply reused timing measurements.
- `portable_ffmpeg_argv` 是 JSON 参数数组，使用 `<source-1080p.mov>`、`<source-4k.mov>`、`<output.mkv>` 占位符。替换路径后逐参数调用，不能直接当作 shell 命令字符串。These are argument arrays, not ready-to-run shell command strings.

完整质量复现还需要原片、参考生成步骤、相同软件和驱动；本目录没有包含这些视频。Full quality reproduction requires the original footage, reference generation, matching software, and drivers; those videos are not included.
