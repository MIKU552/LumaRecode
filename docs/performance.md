# 性能优化 / Performance

[简体中文](#简体中文) · [English](#english)

## 简体中文

LumaRecode 的 GUI 前端使用 WinUI 3 和 Native AOT，转换核心使用 C# / .NET，直接管理 FFmpeg 任务。

### 实现

| 改动 | 实现方式 |
| --- | --- |
| 按需加载 | 首次打开时创建配色面板，首次展开时创建 ColorPicker |
| 编译视图 | XAML 编译为 XBF，本地化使用生成的控件映射 |
| 源生成序列化 | 为设置、翻译资源和命令行 JSON 生成序列化代码 |
| 控件缓存 | 复用主视图控件引用，路径与日志禁用拼写检查和文字预测 |
| Native AOT | 发布时将界面与转换核心编译为本机代码 |
| 进程内转换核心 | GUI 与 CLI 直接调用同一 C# 核心，按需启动 FFmpeg / ffprobe |
| 进度事件 | 直接传递 JSON 节点快照，最多排队 128 个事件 |
| 日志刷新 | 有界文本缓冲，合并界面刷新；CLI 按需生成 JSON |
| 本地化模板 | 按需创建正则匹配器，先匹配固定前后缀 |
| 批量能力查询 | 同一批次复用成功的编码器与滤镜报告 |
| 字体更新 | 仅写入发生变化的字体属性，减少布局失效 |
| 预览调度 | 单解码器、最新请求合并、12 帧 / 8 MiB LRU 缓存 |
| 预览渲染 | 色彩处理前缩小至 1024 像素，双线程解码、有界 PNG 管道与池化缓冲 |
| 布局刷新 | 拖动只更新帧相关控件；页面结构变化时才调整布局 |
| 帧索引 | Span 解析压缩包字段，保留紧凑、只读的时间戳数组 |
| 单文件入口 | 提取依赖并启动 AOT 主程序；GUI 启动后入口退出 |

### 逐帧预览

2026-11-14，Core Ultra X7 358H / Arc B390、FFmpeg 9.0.2、.NET 11 Release。三条 Pocket 3 素材均为 3840×2160、29.97 fps 的静态屏幕图案。依次请求第 0、30、60、90 帧；旧实现运行一轮，新实现运行三轮。

耗时包含 FFmpeg 启动、所选帧解码、色彩处理及 PNG 返回。素材探测和时间轴建立在计时前完成，下表为各组中位数。

| 输入 | 4.3.0 | 4.3.1 | 耗时下降 |
| --- | ---: | ---: | ---: |
| SDR | 852 ms | 329 ms | 61% |
| HLG | 1,075 ms | 560 ms | 48% |
| D-Log M | 1,062 ms | 607 ms | 43% |

新实现每次请求的托管分配量中位数分别为 0.54、0.64、0.41 MB。预览缓存最多保留 12 张 PNG、合计 8 MiB；隐藏预览或切换素材时释放。完整样本、素材哈希与采样记录见 [preview-performance.json](benchmarks/data/preview-performance.json)。

### 内存结果

测试环境：Windows 11 x64、Core Ultra X7 358H、.NET 11、Windows App SDK 2.6.5；HDR 开启，深色主题，Mica 与窗口大小保持一致。测量日期为 2026-11-12。

采样场景为主界面空闲。每种版本独立启动两次，每次运行一个实例，启动至少 30 秒后读取三次性能计数器，间隔约 1 秒。

| 构建 | 私有工作集 MiB | 私有提交 MiB | 总工作集 MiB |
| --- | ---: | ---: | ---: |
| 3.6.3 JIT，运行 1 | 121.754 | 151.125 | 255.801 |
| 3.6.3 JIT，运行 2 | 122.855 | 151.816 | 256.805 |
| 3.6.4 AOT 单文件，运行 1 | 117.293 | 145.699 | 216.844 |
| 3.6.4 AOT 单文件，运行 2 | 115.992 | 143.230 | 213.660 |

私有工作集均值由 122.3 降至 116.6 MiB，下降约 5.7 MiB。私有提交记录进程的私有提交量，总工作集包含共享映射。

2026-11-14 对 4.3.0 与 4.3.1 各独立启动一次，保持 HDR、主题和窗口大小一致，30 秒后采样三次：

| 场景 | 4.3.0 | 4.3.1 |
| --- | ---: | ---: |
| GUI 空闲私有工作集 | 127.9 MiB | 126.5 MiB |
| GUI 空闲私有提交 | 155.4 MiB | 154.7 MiB |
| 4K HLG 预览解码器峰值工作集 | 1,247.5 MiB | 196.1 MiB |

解码器测量使用同一 HLG 素材的界面第 150 帧，采样包含 PATH 启动垫片下的实际 FFmpeg 进程。解码器峰值下降约 84%；该计数器记录进程工作集，GPU 显存另计。空闲 GUI 与解码器分别采样，原始记录见预览数据文件。

### 运行开销

2026-11-13 在同一台电脑上使用 .NET 11 Release 构建，每项预热后测量五次，取中位数。下表记录微基准耗时与托管分配量，原始数据见 [runtime-performance.json](benchmarks/data/runtime-performance.json)。

| 场景 | 4.0.4 | 4.0.5 | 托管分配量 |
| --- | ---: | ---: | ---: |
| 创建本地化目录 10 次 | 13.99 ms | 4.85 ms | 11.68 → 4.69 MB |
| 翻译查找 8,000 次 | 161.03 ms | 28.09 ms | 31.95 MB |
| 读取进度快照 10,000 次 | 19.76 ms | 0.46 ms | 13.35 → 0 MB |

同一批次中，编码器列表、x265 帮助和滤镜列表三项 FFmpeg 查询首次耗时中位数为 141.39 ms，再次查询命中缓存为 0.057 ms。缓存以可执行文件及参数为键，批次结束后释放。

复测命令：

```powershell
dotnet run --project qa/PerformanceChecks/PerformanceChecks.csproj -c Release
dotnet run --project qa/PerformanceChecks/PerformanceChecks.csproj -c Release -- --tools
dotnet run --project qa/PerformanceChecks/PerformanceChecks.csproj -c Release -- --check
```

默认命令测量本地化与进度快照，`--tools` 测量本机 FFmpeg 能力查询，`--check` 检查事件顺序、界面线程上下文、背压、取消、日志边界与缓存失效行为。GUI 工作集另按内存采样方法记录。

### 复测

[内存采样脚本](../qa/MemoryChecks/Measure-Process.ps1)记录实际 GUI 进程的计数器。AOT 单文件版的 GUI 位于缓存内的 `Application/LumaRecode.exe`。CLI 按入口、主程序和任务进程的合计占用采样。

命令见 [QA 指南](../qa/README.md)，构建方式见[开发文档](dependencies.md)。

### 动态照片导出

Core Ultra X7 358H / Arc B390、FFmpeg 9.0.2。素材为 Pocket 3 的 4K 29.97 fps HLG 静态屏幕图案，选取 90 帧（3.003 秒）动态视频，并生成 4K Ultra HDR 封面。每种组合运行两次，交换 CPU / Intel 顺序。耗时包含图片生成、视频编码、封装与校验。

| 动态视频分辨率 | CPU：x265 fast / CRF23 | Intel：HEVC TU6 / ICQ24 |
| --- | --- | --- |
| 原始 3840×2160 | 6.49–6.91 秒 | 5.36–5.73 秒 |
| 1920×1080 | 4.80–4.85 秒 | 5.39–5.41 秒 |

原始数据、输出格式与参数见 [motion-photo-timings.json](benchmarks/data/motion-photo-timings.json)。

## English

The WinUI 3 frontend uses Native AOT. A C# / .NET conversion core manages FFmpeg jobs directly.

### Implementation

| Change | Implementation |
| --- | --- |
| Lazy controls | Create the appearance panel on first use and ColorPicker on first expansion |
| Compiled views | Compile XAML to XBF; use generated control mappings for localization |
| Generated serialization | Generate serializers for settings, translation resources and CLI JSON |
| Control caching | Reuse main-view references; disable spell checking and text prediction for paths and logs |
| Native AOT | Compile the interface and conversion core to native code at publish time |
| In-process core | GUI and CLI call the same C# core and start FFmpeg / ffprobe on demand |
| Progress events | Pass JSON-node snapshots directly; queue at most 128 events |
| Log updates | Bound the text buffer and coalesce UI refreshes; generate CLI JSON on demand |
| Localization templates | Create regex matchers on demand and check fixed prefixes/suffixes first |
| Batch capability queries | Reuse successful encoder and filter reports within a batch |
| Font updates | Write changed font properties to reduce layout invalidation |
| Preview scheduling | One renderer, replacement of pending requests and a 12-frame / 8 MiB LRU |
| Preview rendering | Scale to 1024 pixels before color processing; two decoder threads, bounded PNG pipes and pooled buffers |
| Layout updates | Scrubbing updates frame-related controls; structural layout changes follow page changes |
| Frame indexing | Parse packet fields with spans; retain compact, read-only timestamp arrays |
| Single-file entry | Extract dependencies, start the AOT application and exit after GUI startup |

### Frame preview

Measured on November 14, 2026 with a Core Ultra X7 358H / Arc B390, FFmpeg 9.0.2 and .NET 11 Release. Three Pocket 3 clips show a static screen chart at 3840×2160 and 29.97 fps. Requests visit frames 0, 30, 60 and 90 in order. The old implementation runs once; the new implementation runs three times.

Timing includes FFmpeg startup, decoding the selected frame, color processing and returning the PNG. Source probing and timeline construction finish before timing begins. The table shows each workload's median.

| Input | 4.3.0 | 4.3.1 | Time reduction |
| --- | ---: | ---: | ---: |
| SDR | 852 ms | 329 ms | 61% |
| HLG | 1,075 ms | 560 ms | 48% |
| D-Log M | 1,062 ms | 607 ms | 43% |

Median managed allocation per request is 0.54, 0.64 and 0.41 MB respectively. The preview cache holds at most 12 PNGs and 8 MiB and is released when hidden or when the source changes. Samples, source hashes and memory records are in [preview-performance.json](benchmarks/data/preview-performance.json).

### Memory results

Environment: Windows 11 x64, Core Ultra X7 358H, .NET 11 and Windows App SDK 2.6.5. HDR is enabled; dark theme, Mica and window size remain the same. Measurements were taken on November 12, 2026.

The main window is idle. Each version is started independently twice, with one instance at a time. After at least 30 seconds, three performance-counter samples are taken about one second apart.

| Build | Private working set MiB | Private commit MiB | Total working set MiB |
| --- | ---: | ---: | ---: |
| 3.6.3 JIT, run 1 | 121.754 | 151.125 | 255.801 |
| 3.6.3 JIT, run 2 | 122.855 | 151.816 | 256.805 |
| 3.6.4 AOT single EXE, run 1 | 117.293 | 145.699 | 216.844 |
| 3.6.4 AOT single EXE, run 2 | 115.992 | 143.230 | 213.660 |

Mean private working set falls from 122.3 to 116.6 MiB, a reduction of about 5.7 MiB. Private commit records private committed memory; total working set includes shared mappings.

On November 14, 2026, 4.3.0 and 4.3.1 were each started independently once with the same HDR, theme and window size. Three samples were taken after 30 seconds:

| Scenario | 4.3.0 | 4.3.1 |
| --- | ---: | ---: |
| Idle GUI private working set | 127.9 MiB | 126.5 MiB |
| Idle GUI private commit | 155.4 MiB | 154.7 MiB |
| Peak decoder working set for 4K HLG preview | 1,247.5 MiB | 196.1 MiB |

The decoder test uses GUI frame 150 of the same HLG source and includes the actual FFmpeg process behind the PATH shim. Peak working set falls by about 84%. This counter measures process working set; GPU memory is separate. Idle GUI and decoder samples are collected separately. Raw records are in the preview data file.

### Runtime overhead

Measurements use .NET 11 Release on the same computer, dated November 13, 2026. Each workload is warmed up, measured five times and reported as the median. The table records microbenchmark time and managed allocation; raw data is in [runtime-performance.json](benchmarks/data/runtime-performance.json).

| Workload | 4.0.4 | 4.0.5 | Managed allocation |
| --- | ---: | ---: | ---: |
| Construct a localization catalog 10 times | 13.99 ms | 4.85 ms | 11.68 → 4.69 MB |
| Perform 8,000 translation lookups | 161.03 ms | 28.09 ms | 31.95 MB |
| Read 10,000 progress snapshots | 19.76 ms | 0.46 ms | 13.35 → 0 MB |

Within one batch, initial queries for encoder lists, x265 help and filter lists have a median combined time of 141.39 ms. Repeated cached queries take 0.057 ms. The cache is keyed by executable and arguments and is released at the end of the batch.

Run the measurements with:

```powershell
dotnet run --project qa/PerformanceChecks/PerformanceChecks.csproj -c Release
dotnet run --project qa/PerformanceChecks/PerformanceChecks.csproj -c Release -- --tools
dotnet run --project qa/PerformanceChecks/PerformanceChecks.csproj -c Release -- --check
```

The default command measures localization and progress snapshots. `--tools` measures local FFmpeg capability queries. `--check` covers event ordering, UI synchronization context, backpressure, cancellation, log boundaries and cache invalidation. GUI working set is collected separately with the memory-sampling method.

### Reproducing measurements

The [memory-sampling script](../qa/MemoryChecks/Measure-Process.ps1) records counters for the actual GUI process. The AOT single-file application's GUI is cached at `Application/LumaRecode.exe`. CLI measurements include the entry, main application and task processes.

Commands are in the [QA guide](../qa/README.md#english); build options are in [Development and distribution](dependencies.md#english).

### Motion Photo exports

Measured with a Core Ultra X7 358H / Arc B390 and FFmpeg 9.0.2. The Pocket 3 source is a static 4K, 29.97 fps HLG screen chart. Each export includes a 4K Ultra HDR cover and 90 video frames (3.003 seconds). Each combination is run twice with CPU / Intel order reversed. Wall time includes still encoding, video encoding, packaging and verification.

| Motion video resolution | CPU: x265 fast / CRF23 | Intel: HEVC TU6 / ICQ24 |
| --- | --- | --- |
| Original 3840×2160 | 6.49–6.91 s | 5.36–5.73 s |
| 1920×1080 | 4.80–4.85 s | 5.39–5.41 s |

Raw timings, output formats and parameters are in [motion-photo-timings.json](benchmarks/data/motion-photo-timings.json).
