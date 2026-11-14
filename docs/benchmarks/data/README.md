# 测试数据 / Benchmark data

[中文报告](../encoding-benchmark.zh-CN.md) · [English report](../encoding-benchmark.en.md)

本目录保存编码预设测试的环境、参数和结果。CSV 使用原始记录精度。

## 文件

| 文件 / File | 内容 / Contents |
| --- | --- |
| [motion-photo-timings.json](motion-photo-timings.json) | 4K Ultra HDR 封面与 3 秒动态视频导出耗时 / 4K Ultra HDR cover and three-second Motion Photo export timings |
| [runtime-performance.json](runtime-performance.json) | 本地化、进度快照及能力查询开销 / Localization, progress snapshots and capability-query overhead |
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

## 样本标记

| 标记 | 内容 |
| --- | --- |
| `screen` | 动画原片中的三个 4 秒片段 |
| `holdout` | 动画原片中用于验证的独立片段 |
| `desktop` | 小字、渐变与移动窗口组成的合成桌面 |
| `1080` / `4k` | 1920×1080 / 3840×2160 |
| `svt` / 软件表中的 `av1` | SVT-AV1 |

## 字段

| 字段 | 含义 |
| --- | --- |
| `bytes` / `bytes_container` | 输出文件字节数，包含封装 |
| `mbps` / `mbps_container` | 输出文件平均码率，单位为十进制 Mbps |
| `vmaf` / `vmaf_neg` | VMAF / VMAF NEG 得分 |
| `metric_stride` | 评分采样步长 |
| `P5` | 采样帧得分的第 5 百分位 |
| `returncode` / `invalid` | 退出码与失败信息；评分空值表示缺失 |
| `parameters` | 请求参数，保存为 Python 字典字面量，可用 `ast.literal_eval` 读取 |
| `metric_reused_from` | 相同码流对应的复用评分记录 |
| `portable_ffmpeg_argv` | FFmpeg 参数的 JSON 数组；替换输入、输出占位符后逐参数调用 |

## 计算

- 体积变化：`size_delta_pct = (selected_bytes / baseline_bytes - 1) * 100`
- 画质分差：`vmaf_neg_delta = selected_vmaf_neg - baseline_vmaf_neg`
- 转码速度：`fps = frames / wall_s`
- 峰值工作集：`GiB = peak_working_set_bytes / 2**30`
- CPU 使用率按整机逻辑处理器归一化；`cpu_delta_pp` 表示百分点变化。

## English

The directory contains environment records, requested parameters, and measured results. CSVs preserve recorded precision. `screen`, `holdout`, and `desktop` identify the main animation sample, its validation sample, and the synthetic desktop. File bitrate includes container bytes; quality metrics record their sampling stride. Empty score fields represent missing values.

`parameters` uses Python dictionary literals readable with `ast.literal_eval`. `portable_ffmpeg_argv` is a JSON argument array with input/output placeholders. The formulas above define size change, quality difference, throughput, and working-set units.
