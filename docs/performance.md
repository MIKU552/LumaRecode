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

## 复测

[内存采样脚本](../qa/MemoryChecks/Measure-Process.ps1)记录实际 GUI 进程的计数器。AOT 单文件版的 GUI 位于缓存内的 `Application/LumaRecode.exe`。CLI 按入口、主程序和任务进程的合计占用采样。

命令见 [QA 指南](../qa/README.md)，构建方式见[开发文档](dependencies.md)。

## English

The WinUI frontend uses lazy appearance controls, compiled XAML, generated localization mappings, source-generated JSON, cached control references, and Native AOT. A single-file entry extracts dependencies and starts the AOT application, then exits for GUI use.

Two isolated startup runs per version measured a mean private working set of 122.3 MiB for 3.6.3 and 116.6 MiB for 3.6.4. Samples were collected from the idle main window at least 30 seconds after launch. The table records private working set, private commit, and total working set. CLI sampling includes the entry process and task processes.
