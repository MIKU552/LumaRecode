# 视频转换 2.0

双击 VideoConverter.exe 即可运行，不需要安装 Python。界面采用 Windows 11 Fluent 视觉风格，使用 PySide6 / Qt 实现。

## 开始使用

1. 拖入视频，或点击“选择文件”。
2. 程序读取相机元数据，自动选择 SDR / HDR、HEVC 快速分享和匹配的 Log 曲线。
3. 需要普通视频就选 SDR，需要 HDR 就选 HDR。确认保存位置后点击“开始转换”。

首次运行优先查找 PATH 中的 ffmpeg.exe 和 ffprobe.exe。缺少时会弹出路径窗口，可以自行选择，两项路径保存在当前用户设置中。EXE 内置界面运行环境和 LUT，没有内置 FFmpeg。APV 输入需要支持 APV 解码的 FFmpeg；本次使用 9.0.2 full build 验证。

## 编码选择

依次选择“用途 → 编码器 → 质量档”。原实测质量参数保留。

| 用途 | 编码 | 原实测参数 | 默认音频 |
| --- | --- | --- | --- |
| 快速分享 | HEVC | Intel QSV TU4 / ICQ24 | AAC 256 kbps |
| 快速分享 | AV1 | Intel QSV TU4 / ICQ26 | Opus 192 kbps |
| 快速分享 | H.264 | Intel QSV TU4 / ICQ22 | AAC 256 kbps |
| 软件归档 | HEVC | x265 medium / CRF23 | 复制 |
| 软件归档 | AV1 | SVT-AV1 preset4 / CRF32 | 复制 |
| 软件归档 | AV1 更快备用 | SVT-AV1 preset6 / CRF32 / lp4 | 复制 |
| 软件归档 | H.264 | x264 medium / CRF21 | 复制 |

快速分享使用 Intel QSV，需要受支持的 Intel 显卡和驱动。软件归档用 CPU 编码。音轨可以自行选择 AAC / Opus / 复制。快速分享输出 MP4，归档输出 MKV。

SDR 快速分享使用 SDR8，HDR 始终 10 位并禁用 H.264。SDR 软件归档 HEVC / AV1 使用实测的 10 位档，H.264 为 8 位。

## 三组色彩转换

- Log → HDR：Mi-Log、Pocket 3 D-Log M、Samsung Log 分别使用匹配的 LUT，输出 BT.2020 HLG。
- Log → SDR：先使用对应 LUT 解码，再通过 BT.2390 色调映射到 Rec.709。
- HDR → SDR：按 HLG / PQ 原片色彩做 BT.2390 色调映射，输出 Rec.709。
- 已是 SDR 或 HDR 的素材可以选择“不套用”。HDR 原片保留原 HLG / PQ 类型。

这些操作在一个 FFmpeg 转码过程中完成，会重新编码视频。修改色彩像素需要重新编码。
Log → SDR 和 HDR → SDR 使用 libplacebo 色调映射，界面保留统一的三组入口；需要 FFmpeg 支持 lut3d、zscale、libplacebo 和可用的 Vulkan 驱动。

HLG 方案适合直接分享，平台是否保留 HDR 仍取决于接收端。LUT 使用固定曝光和高光映射，过曝、欠曝和白平衡问题仍可能影响成片。Pocket 3 LUT 来自 OpenOSV 拟合模型，不是 DJI 官方 HDR LUT。第三方说明见 src/assets/THIRD_PARTY.md。

曲线与 LUT 来源：[小米 Mi-Log](https://www.mi.com/global/product/aces/)、[Samsung Log](https://developer.samsung.com/mobile/samsung-log-video.html)、[OpenOSV v0.6.4](https://github.com/Kemerd/OpenOSV/releases/tag/v0.6.4)。

## 自动识别与采样格式

- 小米读取相机的 record_log 标记。
- 三星读取相机的 logvideo 标记，能识别 APV Log。
- Pocket 3 读取 djmd 专用元数据，避免仅凭 BT.709 标签误判这次样片的 D-Log M。
- 标准 HDR 读取 HLG / PQ 曲线；SDR 读取常规传递曲线。
- 只知道是 Log、无法确定曲线时，会要求手动选择。标签完全丢失的 Log 可能与普通 SDR 无法自动区分，可以手动指定“输入解释 → Log”。

默认输出 4:2:0。输入 4:2:2 会在同一次转换中自动降采样，不需要预先转换。4:2:2 输出可用于 HEVC / H.264 软件归档。当前快速分享档和 AV1 只开放 4:2:0。把 4:2:0 改为 4:2:2 不会补回缺失的色彩细节。

## 输出检查

转码完成后自动核对位深、采样、传递曲线、色域和范围，再保存最终文件。PQ / HLG 原片保留流程还会检查原有 HDR 静态元数据。SDR 转换清除 HDR 元数据。

HDR10+ 可转换 SDR；当前版本不负责重新生成或保留动态 HDR 元数据。带 Dolby Vision 的素材会提示使用专门流程。
不会覆盖现有文件。取消操作清理本次临时输出，原片不会修改。错误信息可在“查看日志”读取。

## 从源代码构建

在安装 Python 3.14 x64 的 Windows 上，运行 build.ps1。脚本在本目录创建隔离环境，生成 dist/VideoConverter.exe。可更换 requirements.txt 中的 PySide / Qt 版本后重新构建。

验证包括三类 Log、HLG / PQ、SDR、APV 4:2:2 → 4:2:0、三种硬件编码、软件归档和取消操作。长片转换速度取决于滤镜、分辨率与硬件。
