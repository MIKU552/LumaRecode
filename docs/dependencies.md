# 开发与分发 / Development and distribution

## 开发环境

| 组件 | 版本 | 用途 |
| --- | --- | --- |
| .NET SDK | 11.0.100 | 由 `global.json` 固定 |
| .NET runtime / Native AOT | 11.0.0 | 前端编译与单文件入口 |
| Windows App SDK | 2.6.5 | WinUI、Runtime、Base、Foundation、InteractiveExperiences |
| Windows SDK BuildTools | 10.0.28000.2957 | Windows 资源与清单 |
| Windows API target | 10.0.26100.0 | Windows API 编译合约 |
| CPython | 3.15.2 | 内置 worker 运行时 |
| MSVC linker | 14.44.35230 | AOT 构建使用的原生链接器 |
| PySide6 | 6.12.0 | 可选图标生成工具 |

AOT 构建需要[受支持的 Visual Studio C++ 工具链](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)。在已安装 C++ 工具的开发环境中运行：

```powershell
./build.ps1
```

自行配置 `PATH` 和 `LIB` 的原生工具环境可加 `-UseEnvironmentalNativeTools`。构建工具仅在开发时使用，遵循各自许可。

NuGet 依赖分别锁定在 `App/packages.aot.lock.json`、`App/packages.lock.json` 和 `Bundle/packages.lock.json`。Python 内的依赖随官方嵌入式发行包保留。版本支持信息见 [.NET 支持周期](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)和 [Windows App SDK 发布渠道](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)。

## 构建方式

| 命令 | 产物 | 使用要求 |
| --- | --- | --- |
| `./build.ps1` | AOT 单文件 EXE | 内置所需运行时 |
| `./build.ps1 -Compilation Jit` | JIT 单文件 EXE | 内置所需运行时；构建无需原生链接器 |
| `./build.ps1 -Deployment FrameworkDependent` | JIT ZIP | 用户安装 .NET 11 Runtime、Windows App Runtime 2.6、Python ≥3.14 |

默认产物为 `dist/SelfContained/FrameFlow.exe`。FFmpeg 与 ffprobe 由用户提供。

主视图位于 `App/MainView.xaml`。修改翻译文字后运行 `python tools/Update-ViewText.py`，更新 `App/ViewText.g.cs`；构建会检查映射是否一致。检查命令见 [QA 指南](../qa/README.md)，品牌资源生成见 [Brand/README.md](../Brand/README.md)。

## 单文件结构

`Bundle/` 使用 .NET 单文件提取机制打包 AOT 主程序、Windows App SDK、Python、worker、LUT 和许可文件。首次启动时，依赖提取到用户临时缓存。

GUI 模式下，入口启动 AOT 主程序后退出。CLI 模式下，入口继承标准句柄和工作目录，等待任务结束并返回退出码。命令行用法见 [CLI 手册](cli.zh-CN.md)。

WinUI 资源和 Python 子进程需要磁盘文件，因此构建使用 `IncludeAllContentForSelfExtract`，并产生 .NET 的 NETSDK1244 提示。性能设计与测量见[性能优化](performance.md)。

## 依赖与许可收集

构建脚本执行以下步骤：

1. 按锁文件还原 NuGet 依赖。
2. 下载并核对官方 Python 3.15.2 嵌入式 ZIP，SHA-256 为 `d012f3aa29cf07436dae8aea1fec0fd195c2612a0b059b1f915ae0afb9ad1f62`。
3. 合并应用与打包入口的依赖图，收集包许可、声明及隐式运行时记录。
4. 生成 `licenses/manifest.json` 和 Python 文件哈希清单。

`tools/Collect-Licenses.ps1` 会阻止缺失 WinUI 记录或包含 Engineering Preview 许可的构建。Python 依赖的原始文本与来源保存在 `licenses/Python-dependencies/`。

项目自主内容采用 MIT；第三方运行时和 LUT 使用各自许可。各组件的分发依据、LUT 来源及完整原文入口见[第三方组件](../Worker/assets/THIRD_PARTY.md)。

## English

Build on Windows 11 x64 with the pinned .NET SDK, PowerShell 7, and a supported Visual Studio C++ toolchain. `build.ps1` publishes a self-contained AOT single EXE; `-Compilation Jit` selects the JIT build, and `-Deployment FrameworkDependent` produces a ZIP requiring external runtimes. FFmpeg is user-supplied.

The single-file entry extracts dependencies, starts the AOT UI, and exits for GUI use. For CLI use it waits and preserves standard handles, the working directory, and exit codes. Update the static localization map after editing translated XAML. See the linked QA guide, performance notes, and third-party notices for details.
