# QSV 兼容性 / QSV compatibility

快速分享使用 Intel QSV 的 H.264、HEVC 和 AV1 编码器。滤镜和解码路径按输入格式选择，编码后的文件通过格式与色彩校验后才写入最终位置。

复现环境：Windows 11、Intel Arc B390、驱动 32.0.101.9150、FFmpeg 9.0.2（Gyan）。

## 路径选择

| 输入或操作 | 处理方式 |
| --- | --- |
| 受支持的 4:2:0 素材，颜色及位深保持一致 | QSV 解码与编码 |
| LUT、色调映射、RGB、4:2:2 / 4:4:4、软件归档 | 软件解码与滤镜；编码器按所选用途确定 |
| 带旋转或镜像显示矩阵 | 软件侧应用方向变换，快速分享继续使用 QSV 编码 |
| 可变帧率素材 | 绕开 VPP 的时间戳量化；位深转换使用软件滤镜，H.264 / HEVC 使用 I/P 帧编码 |

常见可变帧率素材通过标称帧率与平均帧率的差异识别。未提前识别的 DTS 冲突由运行时兼容重试处理。

## 错误恢复

以下恢复只用于快速分享，并保留编码器、质量值、preset、输出位深、色彩和音轨选择。

| 错误 | 恢复方式 |
| --- | --- |
| QSV 解码器初始化、送包或硬件帧格式转换失败 | 改用软件解码，继续使用 QSV 编码 |
| 带 HDR 静态元数据的 AV1，硬件解码后的滤镜报 `File exists` | 改用软件解码，保留 HDR 静态元数据 |
| `Low power mode is unsupported` | 将 LowPower 从 `1` 改为自动选择 `-1` |
| `Non-monotonic DTS`、`Non-monotonous DTS` 等时间戳冲突 | 使用可变帧率兼容路径 |

每种恢复最多应用一次，一项任务最多重试三次。未匹配的错误按失败结束。取消会终止当前子进程树；已有输出仅在编码与校验成功后替换。

快速分享的 GOP 为约两秒，并限制在 240 帧以内，用于避开部分 Arc 驱动的长 GOP 故障。

## 已核对的上游报告

| 报告 | 与本项目的关系 |
| --- | --- |
| [FFmpeg：VPP 输出时间基准](https://ffmpeg.org/pipermail/ffmpeg-cvslog/2021-June/127945.html)、[后续帧率选项修改](https://ffmpeg.org/pipermail/ffmpeg-devel/2023-January/306079.html) | 当前 VPP 路径会按标称帧间隔重标时间。可变帧率路径绕开 VPP，保留原始显示时间。 |
| [HandBrake #6786：QSV DTS 冲突](https://github.com/HandBrake/HandBrake/issues/6786)、[编码器接口迁移 #6446](https://github.com/HandBrake/HandBrake/pull/6446) | 社区存在类似症状。HandBrake 自行保存和恢复时间戳；该方案涉及编码、码率控制和封装接口。 |
| [FFmpeg #24429：AV1 HDR side data 重复](https://code.ffmpeg.org/FFmpeg/FFmpeg/pulls/24429) | 在当前测试环境复现。限定输入类型与错误特征后自动切换软件解码。 |
| [Intel #2017：8/10 位硬件帧格式协商](https://github.com/intel/media-driver/issues/2017) | 软件滤镜使用软件帧；不同任务独立启动 FFmpeg，避免跨任务复用硬件帧池。 |
| [Intel #2066：RGB 转换误用 BT.601](https://github.com/intel/media-driver/issues/2066) | 报告涉及 Linux VA-API。项目在软件侧转换 RGB，并明确指定色彩矩阵、原色、传递函数和范围。 |
| [Intel：Arc 长 GOP 编码故障](https://www.intel.com/content/www/us/en/support/articles/000095403.html) | 快速分享限制 GOP 上限；30 / 60 / 120 fps 的常规两秒 GOP 保持原值。 |
| [FFmpeg #11426：QSV 解码器 flush 内存增长](https://ffmpeg.org/pipermail/ffmpeg-trac/2025-January/072436.html) | 报告涉及部分旧 GPU。每个任务独立持有进程和解码会话，任务结束即释放。 |
| [Intel cartwheel-ffmpeg #334：非交互 Windows 会话初始化失败](https://github.com/intel/cartwheel-ffmpeg/issues/334) | 保留硬件初始化诊断，硬件不可用时可选择软件归档。 |
| [Intel #1597：Linux DG2 的 AV1 ICQ 支持](https://github.com/intel/media-driver/issues/1597) | Windows 测试环境支持 ICQ，兼容重试继续使用相同的质量模式。 |
| [Intel #2043：Linux B580 AV1 超时](https://github.com/intel/media-driver/issues/2043) | 与本次 Windows B390 环境不同。任务保持进程隔离，设备故障不自动循环重试。 |
| [FFmpeg：编码输入尺寸检查](https://github.com/FFmpeg/FFmpeg/commit/9b99973b2bbecee0b7e1e57035b95498b42f54a6) | 使用 FFmpeg 默认 autoscale。底层尺寸检查修复由外部 FFmpeg 提供。 |

## B 帧与可变帧率

可变帧率可以使用 B 帧。当前兼容策略针对 QSV 返回的 DTS 冲突。

在绕开 VPP 后，对实际相机素材分别测试了单个 B 帧、两个 B 帧、关闭 B 金字塔及自适应 B 帧、关闭低功耗模式，四组仍产生 DTS 倒退。I/P 帧路径保留了全部 1451 帧及其显示时间。

软件归档可以继续使用 B 帧。保留 QSV B 帧并正确恢复原始时间，需要进一步处理编码器返回的时间戳；固定帧率转换则会改变帧的时间分布。

## 检查

```powershell
dotnet build qa/BackendChecks/BackendChecks.csproj -c Release
python qa/TimingChecks/check_timing.py
python qa/TimingChecks/check_timing.py --sample '<相机原片路径>'
python qa/TimingChecks/check_qsv_recovery.py
```

检查涵盖逐帧显示时间、DTS 顺序、帧数、8/10 位、SDR/HLG/PQ、AV1 HDR 解码、旋转与镜像、240 fps GOP、错误分类、重试上限和已有文件保护。故障注入入口仅编译进 QA 工具；生产程序不包含该入口。

## English

Quick share uses QSV H.264, HEVC and AV1. Software filters receive software frames, and display-matrix transforms are applied before hardware encoding. VFR compatibility preserves frame presentation times and uses I/P-only encoding for H.264 and HEVC.

Runtime recovery handles specific decoder, AV1 HDR side-data, LowPower and DTS failures. Each fallback is applied at most once, with at most three retries. Encoder selection, quality settings and requested output formats remain unchanged. Unclassified device failures terminate the job, and an existing destination is replaced only after successful verification.

The linked upstream reports distinguish confirmed local reproductions from Linux-specific and older-driver issues. The QA commands above exercise timing, HDR metadata, orientation, high-frame-rate GOPs and bounded recovery through actual encodes.
