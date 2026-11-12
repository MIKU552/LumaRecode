# 开发环境与分发 / Development and distribution

当前版本：3.6.4。“最新稳定”核对于 **2026-11-12**，是版本快照，不承诺永远最新。正式构建不使用 preview / experimental 包。

| 项目 / Item | 版本 / Version | 用途 / Purpose |
| --- | --- | --- |
| .NET SDK | 11.0.100 | `global.json` 固定开发工具；最新正式 C# |
| .NET runtime / Native AOT | 11.0.0 | AOT 前端；单文件解包入口使用自包含 .NET |
| Windows App SDK | 2.6.5 | WinUI / Runtime / Base / Foundation / InteractiveExperiences |
| Windows SDK BuildTools | 10.0.28000.2957 | 构建 Windows 资源、清单 |
| Windows target contract | 10.0.26100.0 | Windows API 编译合约，与 BuildTools 版本用途不同 |
| CPython | 3.15.2 | 官方嵌入式 x64 包，随默认 EXE 分发 |
| MSVC native linker | 14.44.35230（本次构建） | 仅开发构建，不随 EXE 分发 |
| PySide6 | 6.12.0 | 可选图标生成工具，不属于应用运行时 |

直接工具与依赖已更新，传递依赖使用供应商指定的兼容组合并由 `App/packages.lock.json` 记录。WebView2 SDK / BuildTools.MSIX 以及 Python 内 OpenSSL、SQLite 等由上游发行包选择，不擅自替换官方 Python 包内 DLL。FFmpeg 是用户选定的外部工具；[原编码测试报告](benchmarks/encoding-benchmark.zh-CN.md)仍记录历史环境，升级 GUI 不表示在新版 FFmpeg 上重做了画质参数扫描。

Direct tooling and dependencies use this stable snapshot. Vendor-selected transitive dependencies are locked rather than independently substituted. FFmpeg remains user-selected; preset measurements retain their historical environment and are not presented as fresh benchmarks.

## .NET 11 与支持周期 / Support

.NET 11 于 2026-11-10 正式发布，属 STS，支持到 2028-11-14；.NET 10 LTS 当前也支持到该日期。.NET 8 于 2026-11-10 结束支持。当前项目使用 .NET 11；后续仍须持续更新安全补丁。[微软支持周期](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)

.NET 11 is a supported production release, not a preview. .NET 10 LTS is a valid fallback; .NET 8 has reached end of support. Keep patch releases current.

Windows App SDK 2.6.5 属于 Stable，服务周期见[官方发布渠道](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)。只引用需要的组件，避免整包带入未使用的 AI、ML、Search、Widgets；保留既有原生 WinUI 界面、HDR Mica 与独立弹出层方案。

## 发布 / Deployment

默认 `./build.ps1` 将 WinUI 前端编译为 Native AOT，再用 `Bundle/` 的 .NET 单文件入口打包，生成单个 `dist/SelfContained/FrameFlow.exe`，包含所需 Windows App SDK 文件、官方 Python、worker、LUT 和解包入口运行时；FFmpeg / ffprobe 仍由用户提供。首次启动解压到 .NET 的用户临时缓存，不要求管理员权限或全局安装运行时。

无参数启动时，解包入口启动缓存内的 AOT 主程序后退出，GUI 不保留额外 CLR 宿主。CLI 模式下入口等待子进程并传回退出码，直接继承标准句柄和原工作目录；此时额外宿主及实际 Python / FFmpeg 任务内存不属于 GUI 空闲数据。下载仍只有一个入口 EXE，缓存中的内部组件不要求用户手动管理。AOT 前端独立构建使用 `App/packages.aot.lock.json`，普通 JIT 回退使用原锁文件；打包项目使用自己的锁文件。

WinUI 资源和 Python 子进程需要磁盘文件，故保留 `IncludeAllContentForSelfExtract`。这是 .NET 单文件兼容提取模式，.NET 11 会提示 NETSDK1244；不表示使用旧版 .NET 或编译失败。不能为消除提示而破坏资源、许可或 worker 路径。

`./build.ps1 -Compilation Jit` 保留原 JIT 单文件构建作为回退。`./build.ps1 -Deployment FrameworkDependent` 可选构建 JIT 精简 ZIP，用户需安装 .NET 11 Runtime、Windows App Runtime 2.6、Python ≥3.14 并配置工具路径；默认下载为 AOT 主程序、内置运行时的 GUI + CLI 单 EXE。

GUI 和命令行使用同一个下载 EXE，不另发终端入口。PowerShell 7 通过输出管道等待 GUI 子系统命令完成；完整说明见 [CLI 手册](cli.zh-CN.md)。默认 AOT 开发构建需要[微软支持的 C++ 工具链](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)。常规 Visual Studio C++ 安装可直接使用；自行配置 PATH / LIB 的开发环境可用 `-UseEnvironmentalNativeTools`。本次本地验证使用官方 VS 2022 catalog 的带 SHA-256 校验工具文件和 Windows SDK 库；编译器、链接器及 SDK 安装器不在下载 EXE 内。构建者须遵守所选工具链许可；JIT 回退不需要原生链接器。

Default deployment wraps the Native AOT UI in the existing .NET single-file extraction mechanism. The entry process exits after starting the GUI, and waits/preserves exit codes for CLI use. Users still download one EXE with no runtime installation. AOT builds require a supported native C++ toolchain; `-Compilation Jit` keeps the old build available. The optional framework-dependent ZIP requires external runtimes. Neither mode bundles FFmpeg. See the [memory report](validation-3.6.4.md) for measurements and limits.

## 分发依据与检查 / Evidence and checks

1. Python 官方嵌入式 ZIP 保持完整，SHA-256 固定为 `d012f3aa29cf07436dae8aea1fec0fd195c2612a0b059b1f915ae0afb9ad1f62`。[官方发布页](https://www.python.org/downloads/release/python-3152/)
2. NuGet 执行签名、完整性及还原检查，锁文件记录内容哈希。`tools/Collect-Licenses.ps1` 合并 AOT 应用与单文件入口的实际依赖图，收集许可与声明，遇到 Engineering Preview 许可或缺失 WinUI 记录即终止；还读取 SDK 的 `downloadDependencies`，补收 Native AOT / .NET / ASP.NET / Desktop 运行时和 Windows SDK .NET 投影包等隐式依赖。Native AOT runtime pack 的实际 MIT 与 THIRD-PARTY-NOTICES 原文一并保留，不将整个 Windows 单文件程序统一归为 MIT。
3. `licenses/manifest.json` 记录版本、来源、文本哈希；Python 依赖原文、上游 SBOM 及来源在 `licenses/Python-dependencies/`。SBOM 的 `NOASSERTION` 不是许可结论，依据是原始文本；该 SBOM 还含跨平台或未随 Windows 嵌入式包分发的项目，不将其全表算作 payload。
4. Windows App SDK 正式包第 3 节、Windows 单文件运行时适用的 .NET Library License 第 3 节、Windows SDK REDIST 的 .NET 投影包条目，以及 Python Windows binary 附加条件分别提供当前授权链。不能把 Windows .NET 二进制统一当作 MIT。首次启动提供接收者条款与全文入口；完整说明及旧审计修正见[第三方组件](../Worker/assets/THIRD_PARTY.md)。
5. 自主源码 MIT 与第三方组件条款分开；不能仅从“开源”或“免费下载”推导转发权。运行时升级不等于取得编解码器专利或商标权。

Official references: [Windows App SDK deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app?tabs=csharp), [Python Windows distribution](https://docs.python.org/3.15/using/windows.html), [Microsoft redistribution documentation](https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution). Original component texts are retained verbatim and govern over these explanatory notes.
