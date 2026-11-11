# 视频转换 3.0 · WinUI 3

这个版本使用 C# 和 Microsoft Windows App SDK / WinUI 3 构建原生 Windows 界面，使用原生控件、Mica 背景和系统主题资源。

## 外观设置

右上角的主题按钮提供“跟随系统 / 浅色 / 深色”。首次运行默认跟随系统，手动选择后会保存，下次启动继续使用。

旁边的调色盘按钮打开“界面配色”面板。

- “使用系统强调色”默认开启，读取 Windows 的强调色。
- 点击预设色块可切换为自定义强调色。
- 展开“自定义颜色”可使用原生 ColorPicker 的色谱、通道与十六进制输入。
- 强调色用于主按钮、单选/勾选状态、输入焦点和进度。
- “恢复默认外观”同时恢复跟随系统主题和系统强调色。

外观设置只改变界面，不改变视频画面。高对比度模式优先使用系统可访问性颜色。

启动窗口会按显示器工作区限制尺寸并居中，底部操作区保留在屏幕内。

## 转换视频

1. 拖入素材或点击“选择视频”。也可以粘贴完整路径后按 Enter。
2. 确认 SDR / HDR、用途和编码器；相机 Log 会自动选择匹配的 HLG 转换。
3. 需要调整采样或音频时，展开“格式与音频”。
4. 确认输出路径，点击“开始转换”。

保留之前的实测编码质量档和三组色彩转换。

| 分类 | 选项 |
| --- | --- |
| 快速分享 | Intel QSV HEVC TU4/ICQ24、AV1 TU4/ICQ26、H.264 TU4/ICQ22 |
| 软件归档 | x265 medium/CRF23、SVT-AV1 preset4/CRF32、x264 medium/CRF21 |
| AV1 更快备用 | SVT-AV1 preset6/CRF32/lp4 |
| 色彩转换 | Log → HLG HDR、Log → Rec.709 SDR、HLG/PQ HDR → SDR |
| Log 曲线 | 小米 Mi-Log、Pocket 3 D-Log M、Samsung Log |

HDR 始终输出 10 位，禁用 H.264 和 SDR8。SDR 快速分享输出 8 位，软件归档 HEVC/AV1 使用实测 10 位档。快速分享输出 MP4，归档输出 MKV。

默认输出 4:2:0，4:2:2 原片在同一次转换中自动降采样，无须提前处理。软件归档 HEVC / H.264 可选 4:2:2，AV1 和快速分享使用 4:2:0。

快速分享需要支持对应编码器的 Intel 显卡。套 LUT、4:2:2 输入及软件归档使用软件解码；“使用软件解码”也可手动开启。套 LUT 和修改画面会重新编码视频。

## FFmpeg

单文件 EXE 已包含原生界面运行时、本地转换组件和 LUT，不需要安装 .NET 或 Python。FFmpeg / ffprobe 没有打包，优先从 PATH 查找；缺少时会显示工具路径窗口，支持自行选择并记住路径。右上角齿轮可以随时更换工具。

APV 输入需要支持 APV 解码的 FFmpeg，本次验证使用 FFmpeg 9.0.2 full build。Log → SDR 与 HDR → SDR 使用 zscale / lut3d / libplacebo 和可用的 Vulkan 驱动。

## 识别与色彩说明

小米读取 record_log 标记，三星读取 logvideo 标记，Pocket 3 读取 DJI djmd 专用元数据。你提供的 Pocket 3 样片虽然标记 BT.709，仍能从专用元数据识别 D-Log M。

元数据不足时，需要手动确认输入类型或相机曲线。仅凭画面不能可靠区分所有丢失标记的 Log。

原生 HDR 保留原 HLG / PQ 类型。Log 直接分享方案输出 HLG。HDR → SDR 使用 BT.2390 色调映射，清理 HDR 元数据。HDR10+ 可转 SDR，当前不负责重新生成动态 HDR；Dolby Vision 使用专门流程。

LUT 采用固定曝光和高光映射，不恢复原片已剪切的高光。Pocket 3 LUT 来自 OpenOSV v0.6.4 拟合模型，是第三方转换。

参考来源：[小米](https://www.mi.com/global/product/aces/)、[Samsung](https://developer.samsung.com/mobile/samsung-log-video.html)、[OpenOSV](https://github.com/Kemerd/OpenOSV/releases/tag/v0.6.4)。

## 保存和取消

不覆盖已有文件，也不修改原片。转换成功后先核对色彩标签、位深、采样和所需 HDR 静态元数据，再保存到最终位置。取消清理本次临时输出。“转换日志”包含 FFmpeg 命令和错误信息。

底部显示进度条、完成百分比及“当前编码位置 / 总时长”、实时转码帧率（fps）、已用时间与预估剩余时间。估算基于实际编码进度和速度，会随滤镜、场景复杂度和硬件负载变化；刚开始转换时先显示“估算中”。

设置保存在当前用户的 `%LOCALAPPDATA%/VideoConverterWinUI/settings.json`。

## 从源代码构建

在 Windows x64 上安装 .NET 8 SDK 和 Python 3.14，运行 build.ps1。构建使用 Windows App SDK 1.8.260921001，通过 NuGet 获取 Windows SDK 工具，生成 dist/VideoConverter.WinUI.exe。界面为 WinUI 3，独立本地 worker 继续使用已验证的 FFmpeg 转换后端。

单文件分发采用微软支持的自包含发布，运行时会将组件展开到当前用户的缓存目录。
