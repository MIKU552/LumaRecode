# 代码结构 / Architecture

## 模块

`LumaRecode.sln` 与 `LumaRecode.slnx` 包含应用、核心、单文件入口、打包入口与 C# 检查项目。Visual Studio 的发布配置通过 `Packaging` 调用仓库根目录的 `build.ps1`。

| 目录 | 职责 |
| --- | --- |
| `App/` | WinUI 视图、设置、本地化、通知及 GUI / CLI 启动选择 |
| `Core/` | 素材探测、输出规则、命令规划、视频与图片导出、CLI |
| `Bundle/` | 提取并启动 AOT 应用，传递标准句柄与退出码 |
| `Packaging/` | 将 Visual Studio 配置映射到完整的单文件构建流程 |
| `Assets/` | LUT、视频与动态照片预设、资源来源清单 |
| `qa/` | C# 边界检查、Python 集成检查、PowerShell 采样 |
| `tools/` | 构建辅助、许可收集、资源生成与源代码检查 |
| `docs/` | 使用说明、实现说明与测量数据 |

`App/MainWindow.*.cs` 按职责拆分：`Media` 负责素材，`Options` 负责约束，`Conversion` 负责任务与进度，`Frames` 负责预览和帧范围，`Dialogs` 与 `Localization` 负责对话框和语言。

## 转换任务

GUI 的 `ConversionClient` 和 CLI 的 `NativeCli` 调用同一个转换核心：

```text
MediaProbe → OutputPolicy / Transcoder.Plan → 要求检查 → FFmpeg
                                                   ↓
目标文件 ← 校验通过后替换 ← 临时输出 ← 编码、元数据处理
```

`MediaInfo` 是一次素材探测快照。`EncodeOptions` 保存用户选择，`OutputPolicy` 根据输入位深、采样和编码器能力解析默认值。`Transcoder.Plan` 生成独立的参数数组；参数通过 `ProcessStartInfo.ArgumentList` 传递。

编码写入输出目录下独立的任务临时目录。完成后检查像素格式、色彩标签、音轨及动态 HDR；逐帧任务还检查时间轴。只有校验通过才替换目标文件。取消或失败会先停止并等待子进程，再清理临时目录。

动态 HDR 保留、标准色彩转换和 RAW 显影由各自的规则决定处理路径。`Hdr10PlusBitstream` 优先读取 HEVC 压缩包中的 SEI，时间轴存在歧义时交由解码帧元数据处理。`RawDevelopment` 将逐帧相机参数写入 FFmpeg 校准命令。

`PlatformDefaults` 统一 GUI 与 CLI 的架构默认值：ARM64 默认软件归档，并省去 x86 专用 RAW 解码参数。Ultra HDR 的 P/Invoke 接口在各架构保持一致，发布过程选择对应的原生 DLL。

## 构建与打包

`build.ps1 -Target` 选择 Native、Apx 或 Arm64。`Directory.Build.props` 按目标隔离中间文件，App 和 Bundle 按架构选择 NuGet 锁文件。App 先发布 AOT 载荷，Bundle 随后将其打包为单个 EXE；APX 入口在运行载荷前检查 Windows 报告的 APX 能力。发布选项与 Visual Studio 配置见[开发与分发](dependencies.md)。

## 线程与生命周期

GUI 控件只在 DispatcherQueue 所在线程访问。`ConversionEventPump` 在后台执行转换，最多排队 128 个事件，并按顺序回到调用线程。终态单独传递，确保取消时仍能完成收尾。

每个转换客户端拥有自己的取消源；任务返回前等待子进程退出。素材切换通过代次检查丢弃旧探测结果。窗口关闭会取消探测、预览和运行任务，并解除系统事件订阅。

## 逐帧预览

`FrameTimeline` 保存显示帧的 PTS、时长及关键帧证据。常见逐行编码优先索引压缩包；遇到缺失或重复 PTS、隔行编码等情况使用解码帧索引。内部范围从 0 开始、结束位置不包含在内；GUI 将其转换为从 1 开始的包含结束帧表示。

`FramePreviewSession` 同时运行一个渲染任务，并保留一个可替换的待处理请求。拖动不会反复终止解码器；已完成的画面用于反馈，随后处理最新位置。缓存采用 LRU，同时限制为 12 张 PNG 和 8 MiB，切换素材或关闭预览时释放。

`FramePreview` 使用单次 FFmpeg 图片管道，限制解码线程，在色彩处理前缩小到最长边 1024 像素，并输出 SDR PNG。RAW 在去马赛克后缩小。二进制缓冲使用池化存储并限制输出大小。图片与视频导出使用完整分辨率路径。

拖动时间轴只更新帧位置、范围提示、输出文件名和预览请求。响应式布局只在宽度断点、工作页面或预览可见性改变时调整结构。

## 编码约定

- C# 使用文件范围命名空间、四空格缩进和显式控制流花括号；规则位于 `.editorconfig`。
- 核心异步代码使用取消令牌并明确资源所有权；GUI 回调保留界面线程上下文。
- 数值参数和媒体时间使用 invariant culture。帧裁剪使用源时间基与 PTS。
- 注释记录单位、帧范围、色彩表示、线程约束、格式字段及上游兼容原因。
- 修改预设、字体、LUT 或原生组件时同步来源清单和许可。生成文件通过对应工具更新。
- 构建、采样、样片和临时结果分别放在忽略的 `dist/`、`build-cache/` 或仓库外目录。测试结果汇总到现有指南和数据目录。

运行 `./tools/Check-Source.ps1` 检查格式、资源和核心边界；完整转换与实机检查见 [QA 指南](../qa/README.md)。

## English

The WinUI frontend and CLI share the C# conversion core. `MediaProbe` captures source metadata, `OutputPolicy` resolves source and encoder constraints, and `Transcoder.Plan` produces a shell-free argument vector. Tasks encode into a private directory beside the destination, verify the result, then replace the destination. Cancellation joins child processes before cleanup.

GUI events pass through a bounded, ordered channel and return to the caller's synchronization context. Source generations reject stale probe results. The preview scheduler runs one renderer and replaces pending work with the newest frame. Its LRU holds at most 12 PNGs and 8 MiB; source changes and hidden previews release the cache. Viewport rendering uses a 1024-pixel SDR pipe, while exports retain full-resolution processing.

Open `LumaRecode.sln` in Visual Studio. Its `Packaging` project invokes `build.ps1` for Native, APX or ARM64, with isolated intermediate files, payloads, licenses and architecture-specific lock files. The APX launcher checks OS capability before loading the AOT payload. ARM64 defaults to software encoding and uses an ARM64 Ultra HDR library.

Run `tools/Check-Source.ps1` for source checks. Follow `.editorconfig`, keep media numbers culture-independent, document ownership and format contracts, and update provenance when changing shipped resources. Build outputs and test footage stay outside tracked source.
