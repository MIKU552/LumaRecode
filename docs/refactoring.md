# 跨平台重构 / Cross-platform refactoring

[简体中文](#简体中文) · [English](#english)

## 简体中文

### 目标与基线

重构在 `next` 开发，基线为 `85b5bd2`（4.4.3）。`main` 保留稳定应用；完成整体验收后合并并统一发布主要版本。开发包通过 Actions Artifacts 提供。

Windows 使用 C#、.NET 11、WinUI 3，保留单 EXE 的 GUI / CLI。macOS 面向 macOS 27、Apple Silicon，使用 Swift、SwiftUI 和必要的 AppKit；交付原生 `.app` 与 DMG。两端共享 C / C++ 主体的原生核心，Rust 实现适合独立验证的模块。

### 架构与模块边界

| 部分 | 职责 |
| --- | --- |
| C | 公共数据契约、结构初始化、边界检查与实际需要的缓冲处理；提供独立构建、测试和示例 |
| C++ | 主要模型、规则、任务规划、执行队列、取消、文件与进程、媒体资源及硬件适配 |
| Rust | 优先承担 HDR10+ 等自有解析、校验和边界清楚的独立规则模块 |
| C ABI | 公开的版本化头文件，供 C#、Swift 与 C 客户端调用；固定宽度字段、显式容量和资源归属 |
| 平台宿主 | 原生界面、文件选择、设置、权限、通知和 CLI 启动行为 |

目前使用 `engine/`，避免与迁移期间保留的 `Core/` 在大小写不敏感文件系统上冲突。`Interop/` 保存 .NET 生成式原生导入；其职责为数据传递与生命周期适配。

模块分工为起始设计。迁移前核对现有媒体库、SDK 形态、数据耦合、资源所有权、取消、构建和维护成本。对新增语言边界做小型原型，以相同输入比较正确性、调用、复制、分配与吞吐，再决定归属。语言代码占比不作为验收指标。

对外保持统一 C ABI；内部可采用经验证的 C 接口或类型化桥接。静态 / 动态链接、目录和线程组织按实测调整。每条规则、任务状态和资源均有明确的权威实现；调整同步更新接口、依赖和对应测试。

### 功能范围

- 保留现有视频、照片、逐帧裁剪、预览、CLI、设置、四语界面、日志复制、覆盖确认和系统通知。
- Windows 接入 QSV、AMF、NVENC / NVDEC、Qualcomm 和 NVIDIA ARM64；macOS 接入 VideoToolbox、AVFoundation、Core Image、Metal、ImageIO 与 PhotoKit。
- 能力来自实际设备、驱动、组件、输入及输出组合；GUI / CLI 共用结果，不支持的选项隐藏，临时占用可重新检测。保留软件解码与硬件编码的有效组合。
- 以现有 QSV 与软件实测为基线，为其他后端查阅厂商、FFmpeg、维护者报告和可追溯的画质 / 速度测试，记录设备代际及版本适用条件。
- 保留位深、采样、范围、音轨和动态 HDR 的源精度约束。快速分享默认 4:2:0、Limited，SDR 8 位、HDR 10 位；归档按源精度和编码器能力处理，音轨默认复制。
- 保留 DJI、Samsung、小米、Apple 的曲线顺序、Apple Log / Log 2、HDR10+ 和已支持的单层 Dolby Vision。ProRes RAW 由实际兼容的解码器处理。
- 时间轴使用 PTS、时间基和显示顺序，覆盖 VFR、B 帧、偏移、旋转、音画同步和动态元数据关联。持续预览会话合并最新请求并限制缓存。
- 保留 SDR / HDR 图片、Google Ultra HDR 和 Android Motion Photo；Apple 增加 HDR HEIC、Adaptive HDR 与 Live Photo 配对资源，图库导入使用用户授权。
- Windows 保留 Source 字体、Mica 及已验证的 HDR 弹出层处理；macOS 使用系统字体与原生设计。首个可见画面完整稳定，能力检测和重型资源在后台或按需初始化。
- 单文件解包、预览和临时文件分别管理。复用当前构建，按归属、占用和预算回收旧缓存，保护设置、源素材及输出。

### 实施阶段

| 阶段 | 内容 | 退出条件 |
| --- | --- | --- |
| P0 基线 | 功能、测试、依赖、预设来源与工具链 | 稳定基线和可复用检查建立 |
| P1 原型 | C 数据模块、C++ 主干、Rust 模块、C# / Swift ABI、任务及构建 | 目标构建与资源契约验证，确定首批边界 |
| P2 核心迁移 | 模型、探测、规则、规划、时间轴、进程、文件与元数据 | 与旧核心的差异检查及实际软件媒体任务通过 |
| P3 Windows | WinUI、CLI、设置、通知、图片与既有功能接入 | Windows 功能回归通过 |
| P4 硬件 | 多厂商能力、预设、版本规则和可用选项 | 资料、自动验证和设备覆盖可追溯 |
| P5 macOS | 原生工作区、预览、裁剪、转码、CLI 与系统交互 | 基础工作流程完整 |
| P6 媒体功能 | HDR 图片、动态照片、动态 HDR 和 RAW | 格式结构及可执行验证通过 |
| P7 优化 | 启动、内存、预览延迟、缓存、长任务与参数校准 | 同条件测量达到确定的预算 |
| P8 发布准备 | 四语文档、许可、构建来源证明、平台签名与分发包 | 双平台产物来源核验、整体验收与隐私检查完成 |
| P9 合并发布 | 保留提交历史合并、最终标签和 Release | 产物、来源和标签一致 |

Windows 和 macOS 的素材探测、时间轴、转码规划、进程和视频发布已接入共享核心。两端支持兼容解码器上的 ProRes RAW 显影，以及 HEVC 软件归档的 HDR10+ 元数据保留。Windows 图片及 Android Motion Photo 使用原生输出事务，裁剪和动态照片范围可在预览旁设置。macOS 支持逐帧裁剪、HDR HEIC、Adaptive HDR、Live Photo 和 HLG 转 Dolby Vision 8.4。5.0 Beta 从 `next` 分发。硬件路径取决于 FFmpeg 构建和设备驱动，按设备验证。

### 构建入口

开发环境需要 Rustup 和本机 C / C++ 工具链。Rust 版本由 `rust-toolchain.toml` 固定，CMake / Ninja 由 `tools/native-build-tools.json` 固定；发布构建使用锁定依赖。Windows 使用 Visual Studio 的桌面 C++ 工具，macOS 使用 Xcode 27.2。

PowerShell 7 可安装仓库固定的 CMake / Ninja 到开发缓存；脚本只修改当前进程的 PATH，Actions 中通过作业 PATH 传递。

```powershell
./tools/Install-NativeBuildTools.ps1
./tools/Build-NativeCore.ps1 -Target Native -Configuration Release -Test
$nativeCpu = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
dotnet run --project qa/NativeCoreChecks/NativeCoreChecks.csproj -c Release -- "out/native-$nativeCpu-release/engine/LumaRecodeCore.dll"
```

显式选择 `X64` 或 `ARM64` 进行交叉构建；运行检查在对应设备执行。macOS 的完整核心检查同时编译并运行 Swift 调用方：

```sh
sh tools/build-native-core.sh Release
dotnet run --project qa/NativeCoreChecks/NativeCoreChecks.csproj -c Release -- out/native-arm64-release/engine/libLumaRecodeCore.dylib
```

C 基础数据模块可独立构建，无需 Rust 或 C++：

```sh
cmake -S engine/c -B out/c-data -G Ninja
cmake --build out/c-data
ctest --test-dir out/c-data --output-on-failure
```

Visual Studio 解决方案可直接构建分发包。Xcode 构建和归档应用，DMG 由脚本生成。Windows 默认 JIT，保留可选 AOT；C / C++ / Rust 及 FFmpeg 各自管理 CPU 基线和运行时派发。[开发与分发](dependencies.md)

现有 Visual Studio 解决方案已加入 `Interop` 和 `NativeCoreChecks` 项目。原生核心可通过 Visual Studio 的“打开文件夹”加载根目录 `CMakePresets.json`。[`engine/examples/format.c`](../engine/examples/format.c) 展示 C 调用，使用调用方持有的结构获取归档输出格式。

### 进程执行

[`process.h`](../engine/include/lumarecode/process.h) 定义带长度的 UTF-8 视图、执行选项、状态快照和不透明句柄。`create` 复制路径及参数，`run` 在调用线程执行一次，`cancel` 可从另一线程请求停止，全部调用返回后释放句柄。C 模块校验 UTF-8，C++ 拥有参数、管道、捕获缓冲和进程状态。

stdout 支持完整捕获、尾部环形缓冲和丢弃；stderr 保留尾部。默认容量分别为 8 MiB 和 8 KiB，默认超时 25 秒。`timeout_ms = 0` 用于长任务。容量按保留的输出字节计算；参数总量上限为 1 MiB，stdout 和 stderr 的保留上限分别为 64 MiB、1 MiB。

Windows 在创建进程时将其加入 Job Object，仅继承标准输入与输出句柄；标准输入连接 NUL。macOS 使用 `posix_spawn`、独立进程组及非阻塞管道，终态由 `waitid` 确认。回收前保持主进程可等待，避免 PID 重用；Darwin 的僵尸进程组通过成员查询确认已无活动进程。

`.NET` 的 `NativeProcess.RunAsync` 使用生成式导入和 `SafeHandle`，在后台执行阻塞调用，回收后返回终态结果。取消及超时通过结果的 `Status` 表达。Swift 检查验证同一套接口的参数复制、执行和释放。

`run_streaming` 在执行线程同步交付 stdout / stderr 字节，回调期间不持有状态锁。Windows 宿主分别解码进度和诊断，限制未完成行的长度；消费者异常先触发进程回收，再返回宿主。长任务无需保留完整日志缓冲。

`cleanup_state` 记录回收是否确认；`.NET` 结果提供 `CleanupConfirmed`。文件事务据此决定是否可以发布或清理临时输出。回收未确认时返回 I/O 错误，调用方保留任务文件。生命周期检查注入启动失败、内存不足和清理失败，验证这些终态约束。

### 文件与输出事务

[`output.h`](../engine/include/lumarecode/output.h) 提供文件身份比较、私有任务目录和输出发布。`lr_files_same` 按操作系统文件标识比较已有文件，识别硬链接和源路径别名。事务接收绝对路径，固定覆盖授权，在目标目录下建立私有临时目录。

调用顺序为 `create → 编码 → 媒体核验 → seal → commit`。`seal` 需要调用方确认相关子进程全部回收，并固定非空、单链接的普通输出文件。`commit` 复查原片、临时输出及目录的身份与修改状态，再以操作系统重命名发布；未授权覆盖时，其他任务新建的目标仍受保护。

Windows 持有原片的只读共享句柄，阻止写入和删除；临时目录使用受保护的访问控制。macOS 通过文件描述符与目录相对操作管理文件，并在封存和发布前核对原片的大小、修改时间及状态变化时间。封存后的临时输出同样接受修改检查。

`cleanup` 仅移除已知的临时输出和空目录。额外文件、目录身份变化或未确认的进程回收会保留工作目录。发布成功后，清理问题通过 `WORKSPACE_EXISTS` 标记报告，输出状态保持成功。`release` 仅关闭句柄；`.NET` 的 `NativeOutputTransaction.Dispose` 遵循相同约定。

### 源色彩与相机元数据

[`media.h`](../engine/include/lumarecode/media.h) 定义 SDR、HLG、PQ、Log 和 RAW 分类、相机曲线及证据编号。C++ 按相机标签、DJI 数据和传递曲线判断输入；Dolby Vision 配置信息优先。Apple Log 由专用曲线标签确认。通用 Log 标记保留待确认的曲线，由宿主提供手动选择。

标签在调用期间借用，按 ASCII 大小写比较。等价键采用最后一个值，并保留首次出现的优先级。每次调用最多 1,024 个标签、每个视图 64 KiB、文本总量 1 MiB；证据返回原始标签索引，界面负责本地化。

C 模块以两遍扫描解码 ffprobe 十六进制转储，先检查输入和容量，再写入输出；偏移与 ASCII 列会被跳过。Rust 模块位于 `engine/rust/metadata/`，以借用切片读取 DJI protobuf 字段及 AVC SPS，支持 avcC 与 Annex B。两种解析器均无堆分配，单条二进制记录上限为 1 MiB。DJI 模式按已识别的 schema 解释，重复字段保留第一个值。

分类与旧 C# 规则进行组合对照；解析检查覆盖截断、变异、重复字段和失败后输出保持原值。软件媒体任务另外生成 SDR / HLG / PQ 文件及单色 H.264，通过实际 ffprobe 标签和 SPS 验证结果。

### 原生探测流程

[`probe.h`](../engine/include/lumarecode/probe.h) 提供 `create / run / cancel / snapshot / copy_json`。C++ 选择可播放视频轨、排除封面并优先选择 ProRes RAW，读取首帧和 DJI 数据，再合并标签、像素描述及动态 HDR 信息。成功后返回版本为 1 的不可变 JSON 快照，包含完整轨道、选中视频、首帧、标签、像素描述、分类和证据编号。RAW 以 `RAW_CALIBRATION_PENDING` 交给显影阶段。

单个子进程默认超时 25 秒，JSON 捕获默认 8 MiB、可设置到 64 MiB。容量针对元数据，与素材文件大小无关。取消会等待子进程回收；失败与取消返回状态和诊断，成功快照只在完成后发布。Windows 在探测期间阻止源文件写入和删除；macOS 持有文件描述符并复查身份及修改状态。

yyjson 0.13.0 以原始 C 源码参与构建，来源和 SHA-256 固定在 [`licenses/yyjson/manifest.json`](../licenses/yyjson/manifest.json)，构建时校验。严格 JSON 的输入深度上限为 64；解析树、重复键索引和写入暂存共用 128 MiB 预算。重复键按既有探测规则合并为数组。探测完成后仅保留序列化结果。

像素格式报告按工具路径、大小及修改时间缓存，最多 8 项、16 MiB。命中缓存仍检查当前任务的元数据容量。C# 使用 `NativeProbe.RunAsync` 在后台执行，Swift 调用相同的 C 接口。检查覆盖分阶段失败、取消、可选 DJI 超时、源文件变化、缓存容量以及真实软件媒体任务。

### 逐帧时间轴

[`timeline.h`](../engine/include/lumarecode/timeline.h) 保存显示顺序下的原始 PTS、持续时间、关键帧和时间基。渐进式常见编码优先读取压缩包，排除丢弃预滚并按 PTS 排序；缺失或重复的包时间戳切换到解码帧索引。解码索引保留重复 PTS，拒绝逆序时间戳。

ffprobe 输出按行流式解析，单行最多 64 KiB；索引默认预算 512 MiB，包含扩容时的新旧存储。预算针对元数据，不限制视频文件大小。长视频默认不设扫描期限，支持取消和源文件身份复查。失败后不发布部分索引。

`create_from_probe` 从探测快照推导索引参数和隔行路由。`resolve` 接受从零开始、结束帧不包含在内的选择，返回源时间和前置关键帧位置。`at_or_after` 按相对首帧的播放位置进行二分查找。C# `NativeTimeline` 持有原生索引，可查询范围和复制局部帧记录。

### 转码规划与输出核验

[`plan.h`](../engine/include/lumarecode/plan.h) 接收探测快照、编码选项、版本化预设、路径和可选帧范围，返回不可变的参数向量、已解析选项、预期色彩、工作目录及辅助文件要求。规划本身不启动进程或访问文件。

源模型从像素描述、有效分量位深、存储位深、单色标记、范围标签和相机曲线推导精度；FLAC 规则检查整数无损音频、位深及声道顺序。规划复用原生输出规则，覆盖五种 Log 曲线、SDR / HDR 转换、静态与动态 HDR 策略、音频、逐帧裁剪、预览尺寸和动态照片视频参数。QSV 时间戳兼容、软件解码及低功耗重试参数由显式规划标志表达。

`verify_output` 比较新探测的临时输出与计划，检查编码、像素格式、尺寸、方向、色彩、FLAC / 动态照片音频及 HDR 元数据。静态 HDR 按目标编码的量化精度比较。核验通过后，调用方封存并发布文件；裁剪动态 HDR 时，调用方提供所选首帧的元数据。

差异检查比较旧核心与 C++ 的接受条件、完整参数、色彩及默认值。实际任务覆盖 SDR / HLG / PQ 软件 HEVC，以及带偏移、VFR 和 B 帧的 H.264、解码帧回退、逐帧裁剪和事务发布。C 与 Swift 调用相同的版本化接口。

### 验证与分发

迁移前的探测、规则、规划与时间轴实现保存在 `qa/NativeCoreChecks/Reference/`，作为独立对照，仅参与测试构建。

本机已执行 C 数据边界、C++ 策略、Rust HDR10+、.NET 生成式导入，以及与现有 C# 规则和解析器的差异检查。跨架构结果以 Actions 的实际运行记录为准。

测试覆盖结构布局、失败后的输出状态、正负行步长、容量、溢出、精度限制、HDR 整数码值和截断 / 变异输入。C 检查遍历全部 Unicode 标量值，并测试错误及截断序列。进程检查覆盖中文路径、空参数、引号、二进制管道、容量边界、超时、取消、后代进程回收和 .NET 进程并行运行。

软件媒体检查使用合成输入，执行 FFV1 编码、ffprobe 帧数与格式校验、PNG 帧提取。`tools/media-test-tools.json` 固定 CI 使用的原生架构 FFmpeg / ffprobe 版本与 SHA-256；测试工具保存在开发缓存中，应用继续使用用户提供的工具。

文件检查覆盖硬链接、覆盖竞争、封存状态、原片及输出修改、目录替换、保留与重试清理。软件媒体任务先核验临时输出，再通过共享核心发布。后续补充任务辅助文件、恢复策略和长时间 4K HDR10+ 录像检查。

缺少 GPU 的环境运行编译、规则、模拟能力和软件媒体检查。硬件检查只有真实初始化和执行后计为通过；没有条件的用例记录为未运行。

Windows 发布未签名的自包含单 EXE 便携版，FFmpeg / ffprobe 由用户提供。受信任的 Windows 代码签名作为后续可选事项，本轮不申请签名服务，也不作为重构验收条件。macOS 使用免费 ad-hoc 签名，准备首次打开说明。

P8 为 Windows 和 macOS 所有需要分发的官方 Actions 产物生成 [GitHub 构建来源证明](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations)，覆盖开发包、正式发布和共享 SDK，并按架构与编译方式区分。先完成平台签名和封装，再对最终分发文件生成证明；macOS 的 `.app` 以归档文件交付时，对归档文件出具证明。验证下载文件与预期仓库、工作流及提交的关联后再发布，用户可使用 `gh attestation verify` 核验。来源证明与 macOS ad-hoc 签名并用，不替代操作系统的发布者信任或应用审核。

官方来源证明仅由受信任的代码来源与工作流出具，使用作业级最小 OIDC 和证明写入权限。外部贡献的 PR 等不受信任构建执行编译与检查；需要正式分发时，由受信任工作流重新构建、验证并生成证明。

Microsoft Store / MSIX 商店分发留作功能完善后的独立事项，不纳入本轮重构范围。

主许可为 MIT，第三方组件及静态链接内容保留各自声明。

阶段结果和架构调整集中维护在本文、现有兼容性说明与测试数据中。源码、构建日志和分发包检查个人路径、凭据、素材及无关文件。

## English

### Goals and baseline

Development takes place on `next`, based on `85b5bd2` (4.4.3). `main` retains the stable application. Merge and publish one major release after full acceptance. Development packages use Actions Artifacts.

Windows uses C#, .NET 11 and WinUI 3, retaining one EXE for GUI and CLI. macOS targets macOS 27 on Apple Silicon using Swift, SwiftUI and AppKit where needed, distributed as a native `.app` and DMG. Both hosts share a predominantly C/C++ native core with independently verifiable Rust modules.

### Architecture and module boundaries

| Part | Responsibility |
| --- | --- |
| C | Public data contracts, initialization, boundary checks and necessary buffer operations, with independent builds, tests and examples |
| C++ | Principal models, policies, planning, execution queues, cancellation, files, processes, media resources and hardware adapters |
| Rust | Prefer custom HDR10+ and other parsing, validation and clearly bounded independent policy modules |
| C ABI | Public versioned headers for C#, Swift and C clients; fixed-width fields, explicit capacities and resource ownership |
| Platform hosts | Native UI, file selection, settings, permissions, notifications and CLI startup behavior |

The initial directory is `engine/`, avoiding a collision with the retained `Core/` on case-insensitive filesystems. `Interop/` contains generated .NET native imports for data transfer and lifetime adaptation.

Module ownership is an initial design. Before migration, examine existing libraries, SDK shape, data coupling, ownership, cancellation, builds and maintenance cost. Prototype new language boundaries and compare correctness, calls, copying, allocation and throughput using the same inputs. Language line counts are not acceptance criteria.

Keep one public C ABI. Internal boundaries can use validated C interfaces or typed bridges. Adjust linking, directories and threading from measured results. Each policy, task state and resource has an authoritative implementation. Update interfaces, dependencies and tests with each adjustment.

### Functional scope

- Retain video and image export, frame-accurate trimming, preview, CLI, settings, four UI languages, log copying, overwrite confirmation and system notifications.
- Add Windows QSV, AMF, NVENC/NVDEC, Qualcomm and NVIDIA ARM64 paths; use VideoToolbox, AVFoundation, Core Image, Metal, ImageIO and PhotoKit on macOS.
- Derive capabilities from real devices, drivers, components, inputs and output combinations. GUI and CLI share results; hide unsupported options and allow rechecking temporary failures. Keep valid software-decoding/hardware-encoding combinations.
- Preserve measured QSV/software baselines. Research vendor documentation, FFmpeg, maintainer reports and traceable quality/speed tests for other backends, recording hardware generations and version applicability.
- Preserve source-precision constraints for depth, sampling, range, audio and dynamic HDR. Sharing defaults to 4:2:0 Limited, 8-bit SDR or 10-bit HDR; archive defaults follow source precision and encoder support, with audio copy.
- Retain DJI, Samsung, Xiaomi and Apple curve ordering, Apple Log/Log 2, HDR10+ and supported single-layer Dolby Vision. Use an actually compatible decoder for ProRes RAW.
- Use PTS, time bases and presentation order for VFR, B-frames, offsets, rotation, synchronization and dynamic metadata. Persistent preview sessions coalesce requests and bound caches.
- Retain SDR/HDR images, Google Ultra HDR and Android Motion Photo. Add Apple HDR HEIC, Adaptive HDR and paired Live Photo resources; Photos imports require authorization.
- Retain Source fonts, Mica and verified HDR popup handling on Windows. Use system fonts and native design on macOS. Present a complete, stable first frame; defer capability probing and heavy resources.
- Manage extraction, preview and temporary files separately. Reuse the current build, reclaim owned unused caches within budgets, and protect settings, sources and outputs.

### Implementation stages

| Stage | Work | Exit criteria |
| --- | --- | --- |
| P0 Baseline | Features, tests, dependencies, preset provenance and toolchains | Stable baseline and reusable checks established |
| P1 Prototype | C data, C++ engine, Rust modules, C#/Swift ABI, tasks and builds | Target builds and resource contracts verified; initial boundaries selected |
| P2 Core migration | Models, probing, policies, plans, timeline, processes, files and metadata | Differential checks and real software media tasks pass |
| P3 Windows | Integrate WinUI, CLI, settings, notifications, images and existing features | Windows regression coverage passes |
| P4 Hardware | Vendor capabilities, presets, version rules and available options | Sources, automated checks and device coverage traceable |
| P5 macOS | Native workspace, preview, trimming, transcoding, CLI and system integration | Complete basic workflows |
| P6 Media features | HDR images, motion photos, dynamic HDR and RAW | Format structure and executable checks pass |
| P7 Optimization | Startup, memory, preview latency, caches, long tasks and tuning | Controlled measurements meet established budgets |
| P8 Release preparation | Four-language docs, licenses, build provenance, platform signing and packages | Both platforms' artifact provenance, acceptance and privacy checks complete |
| P9 Merge and release | History-preserving merge, final tag and Release | Artifacts, provenance and tag agree |

Windows and macOS share source inspection, timelines, encoding plans, process execution, and video publication. Both platforms support ProRes RAW development with a compatible decoder and can preserve HDR10+ in software HEVC archives. Windows stills and Android Motion Photos use native output transactions, with trim and motion ranges editable beside the preview. macOS supports frame-accurate trimming, HDR HEIC, Adaptive HDR, Live Photo, and HLG to Dolby Vision 8.4. The 5.0 beta is distributed from `next`. Hardware paths depend on the FFmpeg build and device drivers and are verified per device.

### Build entry points

Development requires Rustup and a native C/C++ toolchain. `rust-toolchain.toml` pins Rust; `tools/native-build-tools.json` pins CMake/Ninja. Release builds use locked dependencies. Windows uses Visual Studio desktop C++ tools; macOS uses Xcode 27.2.

PowerShell 7 can install the pinned CMake/Ninja into a development cache. The script changes only its process PATH; Actions propagates paths within the job.

```powershell
./tools/Install-NativeBuildTools.ps1
./tools/Build-NativeCore.ps1 -Target Native -Configuration Release -Test
$nativeCpu = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
dotnet run --project qa/NativeCoreChecks/NativeCoreChecks.csproj -c Release -- "out/native-$nativeCpu-release/engine/LumaRecodeCore.dll"
```

Select `X64` or `ARM64` explicitly for cross-builds; execute runtime checks on the matching device. The macOS core checks also compile and run a Swift caller:

```sh
sh tools/build-native-core.sh Release
dotnet run --project qa/NativeCoreChecks/NativeCoreChecks.csproj -c Release -- out/native-arm64-release/engine/libLumaRecodeCore.dylib
```

The C data module builds independently, without Rust or C++:

```sh
cmake -S engine/c -B out/c-data -G Ninja
cmake --build out/c-data
ctest --test-dir out/c-data --output-on-failure
```

The Visual Studio solution builds the distribution package. Xcode builds and archives the app, and the packaging script creates the DMG. Windows defaults to JIT with optional AOT. C/C++, Rust and FFmpeg manage their CPU baselines and runtime dispatch independently. [Development and distribution](dependencies.md#english)

The existing Visual Studio solutions include `Interop` and `NativeCoreChecks`. Use Visual Studio's Open Folder command with the root `CMakePresets.json` for the native core. [`engine/examples/format.c`](../engine/examples/format.c) demonstrates a C caller resolving archive output using caller-owned structures.

### Process execution

[`process.h`](../engine/include/lumarecode/process.h) defines length-delimited UTF-8 views, options, snapshots and opaque handles. `create` copies paths and arguments; `run` executes once on its caller's thread; `cancel` can request termination from another thread. Release handles after all calls return. C validates UTF-8; C++ owns arguments, pipes, capture buffers and process state.

stdout supports full capture, a tail ring buffer and discard; stderr retains a tail. Defaults are 8 MiB stdout, 8 KiB stderr and a 25-second deadline. Use `timeout_ms = 0` for long tasks. Capacities count retained output bytes. Arguments have a one-MiB total budget; stdout and stderr retention are capped at 64 MiB and one MiB respectively.

Windows associates the process with a Job Object at creation and inherits only standard I/O handles, with NUL input. macOS uses `posix_spawn`, an independent process group and nonblocking pipes, observing terminal events with `waitid`. The leader remains waitable through cleanup to prevent PID reuse. Darwin zombie-only groups are checked for live members before accepting cleanup.

`.NET` `NativeProcess.RunAsync` uses generated imports and `SafeHandle`, executes the blocking call in the background and returns a terminal result after cleanup. The result's `Status` expresses cancellation and timeout. Swift checks verify argument copying, execution and release through the same interface.

`run_streaming` delivers stdout/stderr bytes synchronously on the execution thread without holding the snapshot lock. The Windows host decodes progress and diagnostics separately and bounds incomplete lines. Consumer exceptions trigger process cleanup before returning to the host. Long jobs do not retain a complete log buffer.

`cleanup_state` records whether reclamation is confirmed; `.NET` exposes `CleanupConfirmed`. File transactions use it to authorize publication or removal of temporary output. Unconfirmed cleanup returns an I/O error and the caller retains task files. Lifecycle checks inject launch, allocation and cleanup failures to verify these terminal-state contracts.

### Files and output transactions

[`output.h`](../engine/include/lumarecode/output.h) provides file identity comparison, private workspaces and output publication. `lr_files_same` compares existing files by OS identity, recognizing hard links and source path aliases. Transactions accept absolute paths, fix overwrite consent and create a private workspace beside the destination.

The call sequence is `create → encode → verify media → seal → commit`. `seal` requires confirmation that all related child processes have completed cleanup, then pins a nonempty, single-link regular output. `commit` rechecks source, staging and directory identities and revisions before publishing through an OS rename. Without overwrite consent, a destination created by another job remains protected.

Windows holds the source with read-only sharing to block writes and deletion, and gives the workspace protected access control. macOS uses file descriptors and directory-relative operations, checking source size, modification time and status-change time before sealing and publication. Sealed staging output is also checked for modification.

`cleanup` removes only the known staging output and an empty directory. Extra files, changed directory identities or unconfirmed process cleanup retain the workspace. After successful publication, cleanup problems set `WORKSPACE_EXISTS` while the output remains successful. `release` closes handles only; `.NET` `NativeOutputTransaction.Dispose` follows the same contract.

### Source color and camera metadata

[`media.h`](../engine/include/lumarecode/media.h) defines SDR, HLG, PQ, Log and RAW classification, camera curves and evidence codes. C++ resolves camera tags, DJI data and transfer functions, with Dolby Vision configuration taking precedence. Dedicated curve tags confirm Apple Log. Generic Log markers retain an unconfirmed curve for the host's manual selection.

Tags are borrowed for the call and compared using ASCII case folding. Equivalent keys use the last value while retaining their original precedence. Each call allows 1,024 tags, 64 KiB per view and one MiB of text in total. Evidence refers to the original tag index; hosts supply localized labels.

The C module decodes ffprobe hex dumps in two passes, checking data and capacity before writing and skipping offsets and ASCII columns. Rust modules in `engine/rust/metadata/` read DJI protobuf fields and AVC SPS from borrowed slices, supporting avcC and Annex B. Both parsers avoid heap allocation and limit binary records to one MiB. DJI modes are interpreted within recognized schemas; duplicate fields retain their first occurrence.

Classification combinations are compared with the C# rules. Parser checks cover truncation, mutation, duplicate fields and unchanged output on failure. Software media tasks also generate SDR/HLG/PQ files and monochrome H.264, validating actual ffprobe tags and SPS data.

### Native inspection flow

[`probe.h`](../engine/include/lumarecode/probe.h) provides `create / run / cancel / snapshot / copy_json`. C++ selects playable video, excludes cover art and prefers ProRes RAW, reads first-frame and DJI data, then merges tags, pixel descriptors and dynamic HDR information. Success returns an immutable version-1 JSON snapshot containing all streams, selected video, first frames, tags, pixel descriptors, classification and evidence codes. RAW passes to the development stage with `RAW_CALIBRATION_PENDING`.

Each subprocess defaults to a 25-second deadline. JSON capture defaults to eight MiB and can be raised to 64 MiB, independently of media file size. Cancellation waits for subprocess cleanup. Failures and cancellation return status and diagnostics; successful snapshots are published after completion. Windows prevents source writes and deletion during inspection; macOS retains a file descriptor and rechecks identity and revision.

yyjson 0.13.0 builds from unmodified C sources, with provenance and SHA-256 pinned in [`licenses/yyjson/manifest.json`](../licenses/yyjson/manifest.json) and verified during configuration. Strict JSON input has a 64-level nesting limit. Trees, duplicate-key indexes and writer scratch share a 128-MiB budget. Duplicate keys become arrays following the existing probe rules. Completed inspections retain only serialized output.

Pixel-format reports are cached by tool path, size and modification time, with limits of eight entries and 16 MiB. Cache hits still enforce the current task's metadata capacity. C# dispatches work through `NativeProbe.RunAsync`; Swift uses the same C interface. Checks cover stage failures, cancellation, optional DJI timeouts, source changes, cache capacities and real software media tasks.

### Display-frame timelines

[`timeline.h`](../engine/include/lumarecode/timeline.h) retains original PTS, duration, keyframe evidence and time base in display order. Common progressive codecs first use compressed packets, excluding discarded preroll and sorting by PTS. Missing or duplicate packet timestamps trigger decoded-frame indexing. Decoded indexes retain duplicate PTS and reject descending timestamps.

ffprobe output is parsed as a stream of lines, each limited to 64 KiB. The default 512-MiB index budget includes replacement storage during growth and applies to metadata independently of movie size. Long movies have no default scan deadline. Scans support cancellation and source identity validation; failed scans publish no partial index.

`create_from_probe` derives index parameters and interlaced routing from an inspection snapshot. `resolve` accepts a zero-based, end-exclusive selection and returns source times and a preceding keyframe seek point. `at_or_after` uses binary search over positions relative to the first frame. C# `NativeTimeline` owns the native index, supports range queries and copies selected slices of frame records.

### Encoding plans and output verification

[`plan.h`](../engine/include/lumarecode/plan.h) accepts an inspection snapshot, encode choices, versioned presets, paths and an optional frame window. It returns immutable arguments, resolved choices, expected colors, working directories and required auxiliary files. Planning starts no processes and accesses no files.

Source models derive precision from pixel descriptors, usable component depth, storage depth, monochrome evidence, range tags and camera profiles. FLAC policy validates lossless integer audio, precision and channel order. Planning reuses native format policy and covers five Log curves, SDR / HDR conversion, static and dynamic HDR policy, audio, frame-accurate trimming, viewport sizes and Motion Photo video presets. Explicit plan flags express QSV timing compatibility, software decoding and low-power retry parameters.

`verify_output` compares a freshly inspected staging output with its plan, checking codec, pixels, dimensions, orientation, color, FLAC / Motion Photo audio and HDR metadata. Static HDR fields use the target codec's quantization tolerance. The caller seals and publishes verified files, supplying the selected first frame's metadata when trimming dynamic HDR.

Differential checks compare acceptance, complete arguments, colors and defaults against the existing core. Real tasks cover SDR / HLG / PQ software HEVC, offset VFR H.264 with B frames, decoded-frame fallback, frame-accurate trimming and transactional publication. C and Swift call the same versioned interfaces.

### Verification and distribution

The pre-migration inspection, policy, planning and timeline implementations live in `qa/NativeCoreChecks/Reference/` as independent test-only references.

Local checks cover C data boundaries, C++ policies, Rust HDR10+, generated .NET imports and differential behavior against existing C# policies and parsers. Cross-architecture status follows actual Actions execution records.

Coverage includes layout, unchanged output after failure, signed strides, capacities, overflow, precision restrictions, HDR integer codes and truncated/mutated inputs. C checks traverse every Unicode scalar value and test malformed and truncated sequences. Process checks cover Unicode paths, empty arguments, quotes, binary pipes, capacity boundaries, deadlines, cancellation, descendant cleanup and coexistence with .NET processes.

Software media checks use synthetic inputs for FFV1 encoding, ffprobe frame/format validation and PNG frame extraction. `tools/media-test-tools.json` pins native-architecture FFmpeg/ffprobe versions and SHA-256 values for CI. Test tools live in the development cache; the application continues to use user-provided tools.

File checks cover hard links, competing publishers, seal states, source and staging mutations, directory replacement, retention and cleanup retries. Software media tasks verify staging output before publishing through the shared core. Further checks will cover job auxiliary files, recovery policies and long 4K HDR10+ recordings.

GPU-free environments run builds, policies, capability simulations and software media checks. Hardware checks pass only after real initialization and execution. Unavailable cases are recorded as not run.

Windows ships an unsigned, self-contained portable EXE; users provide FFmpeg/ffprobe. Trusted Windows code signing remains an optional future task. This refactor does not apply for a signing service or require trusted Windows signing for acceptance. macOS uses free ad-hoc signing with first-open instructions.

P8 adds [GitHub artifact attestations](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations) to all official Windows and macOS Actions artifacts intended for distribution, including development builds, releases and shared SDKs, identified by architecture and compilation mode. Complete platform signing and packaging before attesting the final distribution files; when a macOS `.app` ships as an archive, attest that archive. Verify the downloaded files against the expected repository, workflow and commit before publication. Users can verify them with `gh attestation verify`. Provenance coexists with macOS ad-hoc signing and does not replace operating-system publisher trust or application review.

Official attestations are issued only for trusted code origins and workflows, using minimal job-scoped OIDC and attestation-write permissions. Untrusted builds, including external contribution PRs, run compilation and checks. Artifacts intended for official distribution are rebuilt, verified and attested by a trusted workflow.

Microsoft Store / MSIX Store distribution remains a separate task after further feature development, outside this refactor's scope.

The project remains MIT; third-party and statically linked components retain their notices.

Keep stage results and architecture changes in this guide, existing compatibility docs and test data. Check source, logs and packages for personal paths, credentials, footage and unrelated files.
