# 色彩转换 / Color conversion

[简体中文](#简体中文) · [English](#english)

## 简体中文

### 相机 Log

| 曲线 | 输入 RGB 色域 | 输出 |
| --- | --- | --- |
| DJI Pocket 3 D-Log M | OpenOSV Pocket 3 转换 | HLG / Rec.709 |
| Samsung Log | BT.2020 | HLG / Rec.709 |
| Xiaomi Mi-Log | BT.2020 | HLG / Rec.709 |
| Apple Log | BT.2020 | HLG / Rec.709 |
| Apple Log 2 | Apple Wide Gamut | HLG / Rec.709 |

Apple Log 与 Apple Log 2 使用同一传递曲线，区别在输入 RGB 色域。Apple Log 2 在场景线性域转换到 BT.2020，再应用项目的 HLG 高光映射。LUT 以 18% 场景灰对应 HLG 0.38，并按 RGB 峰值统一压缩高光。

相机曲线与 YCbCr 矩阵、Limited / Full 范围分别处理。自动识别依据明确的相机标记；缺少标记的素材可手动指定输入解释和曲线。

### 动态 HDR

| 输入 | 软件 HEVC 保留 | 软件 AV1 保留 | 标准 HDR / SDR |
| --- | --- | --- | --- |
| Dolby Vision Profile 5 | Profile 5 | — | libplacebo Dolby Vision 映射 |
| Dolby Vision Profile 8.1 / 8.4 | Profile 8.1 / 8.4 | Profile 10.1 / 10.4 | libplacebo Dolby Vision 映射 |
| Dolby Vision Profile 10.1 / 10.4 | Profile 8.1 / 8.4 | Profile 10.1 / 10.4 | libplacebo Dolby Vision 映射 |
| HEVC / AV1 HDR10+ | HDR10+ | — | PQ 基础图像 / HDR 转 SDR 映射 |

动态 HDR 素材默认使用软件 HEVC、原片范围、10 位和 4:2:0。保留模式保持编码样本的色彩表示，并逐帧传递元数据。Profile 5 的 IPTPQc2 数据直接送入编码器；软件 HEVC 启用 Dolby Vision 所需的 VBV，`vbv-maxrate=100000`、`vbv-bufsize=100000`。

HDR10+ 由内置 C# 解析并导出为 x265 JSON。HEVC 路径扫描 SEI，按数据包 PTS 排序，并排除容器标记为丢弃的前置帧。重复 SEI 按解码顺序继承，再按显示顺序导出。AV1、无时间戳的裸码流及时间线存在歧义的素材使用 ffprobe 解码后的逐帧元数据。JSON 逐帧写入任务临时目录。保留支持单窗口 Profile A / B。

选择“标准 HDR”后可使用硬件快速分享。Dolby Vision 转换由 libplacebo 应用 RPU 后输出标准 HLG / PQ；HDR10+ 使用 PQ 基础图像。转 SDR 时应用色调映射。已应用的动态元数据随后移除。

双层增强层素材、其他兼容层和需要方向变换的保留任务会在编码前提示。带方向信息的单层素材可选择标准 HDR 转换。

### ProRes RAW

ProRes RAW 使用独立显影流程：线性 Bayer 解码 → 双线性去马赛克 → 黑电平归一化 → 相机白平衡、RGB-to-XYZ 矩阵和增益 → BT.2020 → HLG。白平衡与矩阵逐帧读取，推荐裁切与方向变换在去马赛克之后处理。SDR 输出在显影后应用色调映射。

RAW 显影需要 FFmpeg 9 或更新版本。FFmpeg 9.0.2 的 RGGB 样片已通过转换测试。iPhone 17 Pro 的真实样片使用 Bayer pattern 3，当前解码器返回 `Not yet implemented`。程序会用所选 FFmpeg 试解首帧；不支持的素材仍可查看信息，并显示兼容性提示。

RAW 软件显影的计算量高于常规视频转码。x64 解码使用 `-cpuflags -avx2`，规避受影响构建中的 ProRes RAW IDCT 排列问题。

### 参考资料

- [Apple Log 2 色彩空间](https://developer.apple.com/documentation/avfoundation/avcapturecolorspace/applelog2)
- [OpenColorIO Apple 相机转换](https://github.com/AcademySoftwareFoundation/OpenColorIO/blob/main/src/OpenColorIO/transforms/builtins/AppleCameras.cpp)
- [Dolby 官方样片](https://github.com/DolbyLaboratories/dolby-vision-contents)
- [FFmpeg ProRes RAW 解码器](https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/libavcodec/prores_raw.c)
- [FFmpeg ProRes RAW AVX2 问题与补丁](https://code.ffmpeg.org/FFmpeg/FFmpeg/pulls/25547)
- [AV1 HDR10+ 元数据处理](https://aomediacodec.github.io/av1-hdr10plus/v1.0.0.html)
- [FFmpeg HDR10+ 字段定义](https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/libavutil/hdr_dynamic_metadata.h)
- [x265 HDR10+ JSON 输入](https://github.com/videolan/x265/blob/master/source/dynamicHDR10/metadataFromJson.cpp)

## English

### Camera Log

| Profile | Input RGB gamut | Output |
| --- | --- | --- |
| DJI Pocket 3 D-Log M | OpenOSV Pocket 3 transform | HLG / Rec.709 |
| Samsung Log | BT.2020 | HLG / Rec.709 |
| Xiaomi Mi-Log | BT.2020 | HLG / Rec.709 |
| Apple Log | BT.2020 | HLG / Rec.709 |
| Apple Log 2 | Apple Wide Gamut | HLG / Rec.709 |

Apple Log and Apple Log 2 share a transfer curve and use different RGB primaries. Log 2 converts scene-linear Apple Wide Gamut into BT.2020 before HLG rendering. The generated tables place 18% scene gray at HLG signal 0.38 and roll highlights using a shared RGB scale.

Camera Log profiles are independent of the YCbCr matrix and sample range. Detection uses explicit camera metadata; select the input interpretation and curve manually when those tags are absent.

### Dynamic HDR

| Input | Preserve with software HEVC | Preserve with software AV1 | Standard HDR / SDR |
| --- | --- | --- | --- |
| Dolby Vision Profile 5 | Profile 5 | — | libplacebo Dolby Vision mapping |
| Dolby Vision Profile 8.1 / 8.4 | Profile 8.1 / 8.4 | Profile 10.1 / 10.4 | libplacebo Dolby Vision mapping |
| Dolby Vision Profile 10.1 / 10.4 | Profile 8.1 / 8.4 | Profile 10.1 / 10.4 | libplacebo Dolby Vision mapping |
| HEVC / AV1 HDR10+ | HDR10+ | — | PQ base / HDR-to-SDR mapping |

Dynamic HDR defaults to software HEVC, source sample range, 10-bit and 4:2:0. Preservation keeps the source sample representation and carries per-frame metadata. Profile 5 IPTPQc2 samples pass directly to the encoder. HEVC enables the VBV settings required by Dolby Vision: `vbv-maxrate=100000` and `vbv-bufsize=100000`.

Built-in C# code parses HDR10+ and exports x265 JSON. HEVC scans SEI payloads, orders packets by PTS and excludes container-discarded preroll. Repeated SEI inherits state in decode order before presentation ordering. AV1, untimestamped elementary streams and ambiguous timelines use decoded frame side data from ffprobe. JSON is streamed to the job directory. Preservation supports single-window Profile A / B.

Standard HDR enables hardware quick sharing. libplacebo applies Dolby Vision RPU mapping for standard HLG / PQ; HDR10+ uses its PQ base image. SDR output applies tone mapping. Consumed dynamic metadata is then removed.

Dual-layer enhancement inputs, other compatibility layers and preservation jobs with display transforms are rejected before encoding. Single-layer sources with display transforms can use Standard HDR.

### ProRes RAW

ProRes RAW follows a separate development path: linear Bayer decoding, bilinear demosaicing, black normalization, camera white balance and RGB-to-XYZ matrix/gain, BT.2020, then HLG. Calibration is read per frame. Recommended crop and orientation are applied after demosaicing. SDR adds tone mapping after development.

RAW development requires FFmpeg 9 or newer. The tested FFmpeg 9.0.2 build develops RGGB footage. The real iPhone 17 Pro sample uses Bayer pattern 3, for which this decoder returns `Not yet implemented`. LumaRecode probes decoding with the selected FFmpeg; unsupported sources remain inspectable with a compatibility message.

RAW software development costs more processing than regular video transcoding. On x64, decoding uses `-cpuflags -avx2` to avoid the ProRes RAW IDCT permutation issue in affected builds.

### References

- [Apple Log 2 color space](https://developer.apple.com/documentation/avfoundation/avcapturecolorspace/applelog2)
- [OpenColorIO Apple camera transforms](https://github.com/AcademySoftwareFoundation/OpenColorIO/blob/main/src/OpenColorIO/transforms/builtins/AppleCameras.cpp)
- [Dolby official sample content](https://github.com/DolbyLaboratories/dolby-vision-contents)
- [FFmpeg ProRes RAW decoder](https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/libavcodec/prores_raw.c)
- [FFmpeg ProRes RAW AVX2 issue and patch](https://code.ffmpeg.org/FFmpeg/FFmpeg/pulls/25547)

- [AV1 HDR10+ metadata handling](https://aomediacodec.github.io/av1-hdr10plus/v1.0.0.html)
- [FFmpeg HDR10+ field definitions](https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/libavutil/hdr_dynamic_metadata.h)
- [x265 HDR10+ JSON input](https://github.com/videolan/x265/blob/master/source/dynamicHDR10/metadataFromJson.cpp)
