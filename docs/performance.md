# 性能优化 / Performance

LumaRecode 的 GUI 前端使用 WinUI 3 和 Native AOT，转换核心使用 C# / .NET，直接管理 FFmpeg 任务。

## 实现

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
| 单文件入口 | 提取依赖并启动 AOT 主程序；GUI 启动后入口退出 |

## 内存结果

测试环境：Windows 11 x64、Core Ultra X7 358H、.NET 11、Windows App SDK 2.6.5；HDR 开启，深色主题，Mica 与窗口大小保持一致。测量日期为 2026-11-12。

采样场景为主界面空闲。每种版本独立启动两次，每次运行一个实例，启动至少 30 秒后读取三次性能计数器，间隔约 1 秒。

| 构建 | 私有工作集 MiB | 私有提交 MiB | 总工作集 MiB |
| --- | ---: | ---: | ---: |
| 3.6.3 JIT，运行 1 | 121.754 | 151.125 | 255.801 |
| 3.6.3 JIT，运行 2 | 122.855 | 151.816 | 256.805 |
| 3.6.4 AOT 单文件，运行 1 | 117.293 | 145.699 | 216.844 |
| 3.6.4 AOT 单文件，运行 2 | 115.992 | 143.230 | 213.660 |

私有工作集均值由 122.3 降至 116.6 MiB，下降约 5.7 MiB。私有提交记录进程的私有提交量，总工作集包含共享映射。

## 运行开销

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

## 复测

[内存采样脚本](../qa/MemoryChecks/Measure-Process.ps1)记录实际 GUI 进程的计数器。AOT 单文件版的 GUI 位于缓存内的 `Application/LumaRecode.exe`。CLI 按入口、主程序和任务进程的合计占用采样。

命令见 [QA 指南](../qa/README.md)，构建方式见[开发文档](dependencies.md)。

## English

The WinUI frontend uses lazy appearance controls, compiled XAML, generated localization mappings, source-generated JSON, cached control references, and Native AOT. A single-file entry extracts dependencies and starts the AOT application, then exits for GUI use.

Two isolated startup runs per version measured a mean private working set of 122.3 MiB for 3.6.3 and 116.6 MiB for 3.6.4. Samples were collected from the idle main window at least 30 seconds after launch. The table records private working set, private commit, and total working set. CLI sampling includes the entry process and task processes.

The 4.0.5 microbenchmarks measure managed allocation and execution time separately from GUI working set. Translation catalog construction uses 60% less allocation, and the in-process progress bridge reads detached snapshots directly. Batch jobs reuse successful FFmpeg capability reports; the table and linked JSON contain the measured workloads and results.

## 动态照片导出

Core Ultra X7 358H / Arc B390、FFmpeg 9.0.2。素材为 Pocket 3 的 4K 29.97 fps HLG 静态屏幕图案，选取 90 帧（3.003 秒）动态视频，并生成 4K Ultra HDR 封面。每种组合运行两次，交换 CPU / Intel 顺序。耗时包含图片生成、视频编码、封装与校验。

| 动态视频分辨率 | CPU：x265 fast / CRF23 | Intel：HEVC TU6 / ICQ24 |
| --- | --- | --- |
| 原始 3840×2160 | 6.49–6.91 秒 | 5.36–5.73 秒 |
| 1920×1080 | 4.80–4.85 秒 | 5.39–5.41 秒 |

原始数据、输出格式与参数见 [motion-photo-timings.json](benchmarks/data/motion-photo-timings.json)。

Motion Photo exports were measured on a Core Ultra X7 358H / Arc B390 with FFmpeg 9.0.2, using a static Pocket 3 HLG screen chart. Each export includes a 4K Ultra HDR cover and 90 video frames (3.003 seconds). The table reports two runs per configuration with reversed CPU/Intel order. Wall time includes still encoding, video encoding, packaging and verification.
