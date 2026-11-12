# FrameFlow encoding preset benchmark

[简体中文](encoding-benchmark.zh-CN.md) | English · [Project home](../../README.en.md) · [Data guide](data/README.md)

This report covers the methodology, quality, size, and speed of FrameFlow's built-in encoding presets.

Measurements were recorded on **October 29–30, 2026**, using the FrameFlow **3.4.7** preset configuration. Software and driver versions are listed below.

## Findings and scope

- **Quick share:** Intel QSV TU4, with ICQ 22 / 24 / 26 for AVC / HEVC / AV1. Extra overrides did not show sufficiently consistent benefits; the driver's automatic reference structure is retained.
- **Software archive:** x264 medium / CRF21, x265 medium / CRF23, and SVT-AV1 preset4 / CRF32, with the overrides below. AV1 also has a faster alternative.
- On the animation excerpts, the selected software configurations reduced size by roughly 5–21% relative to the older baselines. Synthetic desktop results differed: 4K AVC grew by about 6%. These are not universal equal-quality savings.
- The original quality sweep used **SDR**. HEVC / AV1 sharing quality tests primarily used **10-bit P010**; current FrameFlow SDR Quick share defaults to 8-bit. The same ICQ value does not establish equivalence across bit depths.
- Later format and workflow checks for HDR, SDR8, 4:2:2, and LUT conversion are not new quality-tuning sweeps for every combination. The original batch had no real HDR reference footage for quality calibration.

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

See [environment.json](data/environment.json) for recorded hardware and source metadata. Results should not be directly generalized to other machines, drivers, or encoder versions.

## Sources and methodology

| Source | Original format | Role |
| --- | --- | --- |
| `Timeline 1.mov` | 3840×2160, 30 fps, HEVC Main, 8-bit 4:2:0, BT.709, approximately 214.85 Mbps | 4K animation / dance content |
| `Timeline 1.XQ.mov` | 1920×1080, 30 fps, ProRes XQ, 12-bit 4:4:4, BT.709 primaries / matrix, approximately 487.34 Mbps | 1080p animation / dance content; unspecified transfer interpreted as BT.709 |
| Synthetic desktop | 1080p / 4K, 60 unique frames per second | Small text, gradients, light/dark backgrounds, moving windows |

The two originals contain different pictures. Their results are not a controlled same-scene resolution-scaling comparison. The synthetic desktop is not an actual game or browser capture.

1. The main sample combines four seconds starting at 20, 75, and 125 seconds: 12 seconds, 360 frames. Its original label, `screen`, refers to animation excerpts. `holdout` is an independent 18-second excerpt at 45–63 seconds, excluded from tuning. `desktop` is the synthetic stress source above.
2. Quality references were normalized to native-resolution, 10-bit 4:2:0 FFV1, BT.709 limited. Loss from normalizing the 4:4:4 / 12-bit original is outside the encoder quality score.
3. The first sweep used ordinary VMAF v0.6.1, mainly every third frame, alongside PSNR / SSIM. The advanced sweep and software table below use **VMAF NEG**. Sampling stride is recorded per trial; some desktop tests use every frame. P5 is the fifth percentile of sampled frame scores.
4. Compare scores only within a matching reference, mode, and resolution. Ordinary VMAF and NEG are different metrics. These SDR scores do not certify HDR appearance, 4K viewing-distance quality, or visual losslessness.
5. Parameter sweeps used predecoded references. Their fps values are not complete transcoding throughput; original-file pipeline timings are listed separately.

The archive retains 92 initial results and 411 advanced trials. The latter comprise 277 software and 134 hardware trials; 120 hardware trials completed with scores, while 14 lookahead combinations failed or stalled. Failed trials remain in the data and are excluded from successful-result conclusions.

## Current presets and provenance

| Purpose | Encoder | Core settings | Overrides |
| --- | --- | --- | --- |
| Software archive AVC | libx264 | medium, CRF21 | `aq-mode=2:aq-strength=0.8` |
| Software archive HEVC | libx265 | medium, CRF23 | `rdoq-level=2:psy-rdoq=1` |
| Software archive AV1 | libsvtav1 | preset4, CRF32, tune0, lp0 | `enable-qm=1:qm-min=8:qm-max=15` |
| Faster AV1 alternative | libsvtav1 | preset6, CRF32, tune0, lp4 | Same QM settings, plus `hierarchical-levels=4` |
| Quick share AVC | h264_qsv | TU4, ICQ22 | `async_depth=4` |
| Quick share HEVC | hevc_qsv | TU4, ICQ24 | `async_depth=4` |
| Quick share AV1 | av1_qsv | TU4, ICQ26 | `async_depth=4` |

The current [recommended-presets.json](../../Worker/assets/recommended-presets.json) matches the source configuration exactly in its `archive`, `archive_fast_av1`, `share`, and `audio` sections. [preset-provenance.json](data/preset-provenance.json) records both full-file SHA-256 values; the files differ overall because the source also contained other tools and notes.

[Worker/engine.py](../../Worker/engine.py) supplies runtime settings: a maximum GOP of approximately eight seconds for software archives and two seconds for sharing, x265 `pools=16`, and QSV `low_power=1`. Current UI options select bit depth and chroma, overriding the JSON's default pixel format. SVT `lp` is a parallelism level, not a thread count; `lp=0` selected level 5 on this machine.

Audio defaults are source copying for archives, stereo AAC 256 kbps for AVC / HEVC sharing, and stereo Opus 192 kbps / 48 kHz VBR for AV1 sharing, with an AAC compatibility option. These are workflow policies, not video-codec requirements or listening-test claims of optimal audio quality.

## Software archive: size and quality

The older baselines are x264 medium / CRF20, x265 medium / CRF22, and SVT-AV1 preset6 / CRF32 / tune0 / lp0, without the selected additional overrides. Selected settings are the archive defaults above. Some quality values and presets change together; total gains cannot be attributed to one switch.

Mbps is average audio-free file bitrate, including minor container overhead. Size change is `(selected bytes / baseline bytes − 1) × 100%`; negative means smaller.

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

Data: [software-pairs.csv](data/software-pairs.csv), 18 pairs. Trial IDs link these pairs to [advanced-trials.csv](data/advanced-trials.csv).

The x264 combination saves space on animation with similar scores, but increases 4K desktop size. The x265 combination saves about 5–6% on animation with slightly higher NEG; desktop results do not show the same benefit. The original visual inspection found more visible local dark blocking at CRF23.5, so the more conservative CRF23 was retained.

AV1 saves about 12–13% on the main excerpts and 11–14% on the holdout. Most of the improvement comes from preset4; QM8 contributes a smaller additional gain. Longer lookahead, extra search, reference frames, and visual enhancements did not consistently improve all samples, so they were not all added to the defaults.

## Original-file pipeline speed

### Software archive

The test reads seconds 20–32 of the originals. After warmup, runs alternate baseline / selected / selected / baseline. The table shows selected settings only: median fps and CPU across two runs, maximum peak working set. Timing includes FFmpeg startup, decoding, format processing, encoding, and muxing; it excludes wrapper probing and post-run output verification.

| Resolution | Codec | fps | CPU % | Peak working set GiB |
| --- | --- | ---: | ---: | ---: |
| 1080 | AVC | 83.5 | 88.1 | 1.13 |
| 1080 | HEVC | 29.4 | 74.5 | 1.22 |
| 1080 | AV1 | 24.2 | 67.4 | 2.47 |
| 4k | AVC | 23.4 | 87.5 | 3.80 |
| 4k | HEVC | 9.0 | 88.0 | 3.65 |
| 4k | AV1 | 10.5 | 81.1 | 8.61 |

CPU is normalized across the machine's 16 logical processors. Memory is process working set, not whole-system usage; sampling may miss brief peaks. [archive-native-timings.csv](data/archive-native-timings.csv) contains all 24 baseline / selected runs.

A separate one-minute AV1 test uses seconds 20–80 of the same 4K original: 1,800 frames, one run per configuration.

| Configuration | fps | Elapsed | Peak working set GiB |
| --- | ---: | ---: | ---: |
| preset4 / lp0, default | 11.45 | 157.17 s | 9.72 |
| preset6 / lp4 / H4, faster alternative | 17.02 | 105.77 s | 5.11 |

Data: [av1-long-timings.csv](data/av1-long-timings.csv). This uses a different content interval from the 12-second timing test; differences cannot be explained solely by startup overhead. The alternative trades compression performance for speed and memory; it changes more than parallelism.

### QSV Quick share

Each codec / resolution was measured once on seconds 20–50: 900 frames. The 1080p ProRes source uses CPU decoding; the 4K HEVC source uses QSV decoding, so CPU differences are not simply resolution effects. These runs used **AAC 160 kbps before the audio-policy update** and do not precisely time every current audio combination.

| Resolution | Codec | fps | CPU % |
| --- | --- | ---: | ---: |
| 1080 | AVC | 198.0 | 62.1 |
| 1080 | HEVC | 172.2 | 58.2 |
| 1080 | AV1 | 208.0 | 70.3 |
| 4k | AVC | 55.2 | 1.5 |
| 4k | HEVC | 55.6 | 1.3 |
| 4k | AV1 | 56.4 | 1.5 |

Data: [share-native-timings.csv](data/share-native-timings.csv). LUTs, tone mapping, and bit-depth changes alter the pipeline. Small differences between single runs do not establish a strict encoder performance ranking.

## Additional QSV, VBR, and OBS checks

Some advanced QSV requests were adjusted, ignored, or already enabled by default. Successful command execution does not prove that every requested switch took effect. Some quality metrics were reused only after matching compressed-packet hashes, extradata, color, frame rate, and related properties; see `metric_reused_from`.

MBBRC and forced B3 were not made sharing defaults. HEVC MBBRC at ICQ24 reduced size but noticeably lowered scores; the automatic reference structure at ICQ26 already produced a smaller file with a similar score. Forced B3 also had mode- and content-dependent tradeoffs.

Separate **VBR / TU7 / 60 fps** tests used average / maximum budgets of 12 / 20 Mbps at 1080p and 30 / 50 Mbps at 4K. The TU7 subset below uses the same animation reference with 30 fps repeated to 60 fps. Its scores are ordinary VMAF, not the software table's NEG.

| Resolution | Codec | TU | Actual Mbps | VMAF | Sampled P5 |
| --- | --- | ---: | ---: | ---: | ---: |
| 1080 | AV1 | 7 | 12.433 | 95.257 | 93.889 |
| 1080 | H264 | 7 | 12.500 | 94.797 | 91.977 |
| 1080 | HEVC | 7 | 12.343 | 94.460 | 91.509 |
| 4k | AV1 | 7 | 31.001 | 95.074 | 93.311 |
| 4k | H264 | 7 | 31.418 | 94.819 | 92.371 |
| 4k | HEVC | 7 | 30.422 | 94.450 | 92.231 |

Data: [qsv-vbr-60fps.csv](data/qsv-vbr-60fps.csv), 12 TU4 / TU7 runs. AV1 scored higher but also had higher actual bitrate. Equal target budgets do not mean equal achieved bitrate or establish an equal-bitrate efficiency ranking.

Additional OBS encoder measurements from the same batch are retained in [obs-recording-summary.csv](data/obs-recording-summary.csv).

## Data, checks, and future use

The [data guide](data/README.md) links environment records, result extracts, per-run settings, and source-file hashes. CSVs retain recorded precision; report tables are rounded. Some requested parameters in sweep records may not have been honored by the driver.

Source footage is not distributed. Reproduction requires the same sources, software, and drivers. After driver / encoder changes, or for different content, recheck size, shadows, gradients, motion, and small text on representative footage. CRF / ICQ numbers are not equivalent across encoders.
