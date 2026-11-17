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
| P8 发布准备 | 四语文档、许可、免费签名和分发包 | 整体验收与隐私检查完成 |
| P9 合并发布 | 保留提交历史合并、最终标签和 Release | 产物、来源和标签一致 |

混合核心已通过 Windows x64、Windows ARM64 和 macOS ARM64 的 Actions 编译、契约检查及每个平台 6,422 项差异检查；macOS 同时验证 Swift 调用。P1 继续接入任务执行。现有 Windows 应用仍使用 `Core/`；完整媒体执行、硬件适配和 macOS 应用按上述阶段迁移。

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

两端最终提供可完成应用编译与打包的 Visual Studio / Xcode 工程，与脚本和 Actions 共用构建定义。Windows 默认 JIT，保留可选 AOT；C / C++ / Rust 及 FFmpeg 各自管理 CPU 基线和运行时派发。

现有 Visual Studio 解决方案已加入 `Interop` 和 `NativeCoreChecks` 项目。原生核心可通过 Visual Studio 的“打开文件夹”加载根目录 `CMakePresets.json`。[`engine/examples/format.c`](../engine/examples/format.c) 展示 C 调用，使用调用方持有的结构获取归档输出格式。

### 验证与分发

本机已执行 C 数据边界、C++ 策略、Rust HDR10+、.NET 生成式导入，以及与现有 C# 规则和解析器的差异检查。跨架构结果以 Actions 的实际运行记录为准。

测试覆盖结构布局、失败后的输出状态、正负行步长、容量、溢出、精度限制、HDR 整数码值和截断 / 变异输入。后续任务检查包括取消、回调、子进程回收、同文件识别、覆盖和临时文件保护，并处理长时间 4K HDR10+ 素材。

缺少 GPU 的环境运行编译、规则、模拟能力和软件媒体检查。硬件检查只有真实初始化和执行后计为通过；没有条件的用例记录为未运行。

Windows 保留自包含单 EXE，FFmpeg / ffprobe 由用户提供。macOS 使用免费 ad-hoc 签名，准备首次打开说明；Windows 接近发布时申请 SignPath Foundation 免费签名，按其流程批准正式签名。主许可为 MIT，第三方组件及静态链接内容保留各自声明。

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
| P8 Release preparation | Four-language docs, licenses, free signing and distribution | Full acceptance and privacy checks complete |
| P9 Merge and release | History-preserving merge, final tag and Release | Artifacts, provenance and tag agree |

The mixed core passes Actions builds, contract checks and 6,422 differential checks per platform on Windows x64, Windows ARM64 and macOS ARM64. macOS also verifies the Swift caller. P1 continues with task execution. The existing Windows application still uses `Core/`. Full media execution, hardware adapters and the macOS application migrate through the stages above.

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

Both platforms will provide Visual Studio/Xcode projects covering complete application builds and packaging, using the same definitions as scripts and Actions. Windows defaults to JIT with optional AOT. C/C++, Rust and FFmpeg manage their CPU baselines and runtime dispatch independently.

The existing Visual Studio solutions include `Interop` and `NativeCoreChecks`. Use Visual Studio's Open Folder command with the root `CMakePresets.json` for the native core. [`engine/examples/format.c`](../engine/examples/format.c) demonstrates a C caller resolving archive output using caller-owned structures.

### Verification and distribution

Local checks cover C data boundaries, C++ policies, Rust HDR10+, generated .NET imports and differential behavior against existing C# policies and parsers. Cross-architecture status follows actual Actions execution records.

Coverage includes layout, unchanged output after failure, signed strides, capacities, overflow, precision restrictions, HDR integer codes and truncated/mutated inputs. Task checks add cancellation, callbacks, child-process cleanup, file identity, overwrite and temporary-file protection, including long 4K HDR10+ recordings.

GPU-free environments run builds, policies, capability simulations and software media checks. Hardware checks pass only after real initialization and execution. Unavailable cases are recorded as not run.

Windows retains self-contained single-EXE delivery; users provide FFmpeg/ffprobe. macOS uses free ad-hoc signing with first-open instructions. Apply for SignPath Foundation signing near the Windows release and approve signing through its required process. The project remains MIT; third-party and statically linked components retain their notices.

Keep stage results and architecture changes in this guide, existing compatibility docs and test data. Check source, logs and packages for personal paths, credentials, footage and unrelated files.
