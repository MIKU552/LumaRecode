# 开发与分发 / Development and distribution

## 开发环境

| 组件 | 版本 | 用途 |
| --- | --- | --- |
| .NET SDK | 11.0.100 | 由 `global.json` 固定 |
| .NET runtime / Native AOT | 11.0.0 | 应用与转换核心编译、单文件入口 |
| Windows App SDK | 2.6.5 | WinUI、Runtime、Base、Foundation、InteractiveExperiences |
| Windows SDK BuildTools | 10.0.28000.2957 | Windows 资源与清单 |
| Windows API target | 10.0.26100.0 | Windows API 编译合约 |
| Visual Studio | 2026 18.12+ | 解决方案、调试与打包 |
| MSVC linker | 14.44.35230 | AOT 构建使用的原生链接器 |
| libultrahdr / libjpeg-turbo | 2.0.2 / 3.2.1 | Ultra HDR JPEG 编码 |
| PySide6 | 6.12.0 | 可选图标生成工具 |

AOT 构建需要[受支持的 Visual Studio C++ 工具链](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)。在已安装 C++ 工具的开发环境中运行：

```powershell
./build.ps1
```

自行配置 `PATH` 和 `LIB` 的原生工具环境可加 `-UseEnvironmentalNativeTools`。

NuGet 依赖按架构锁定：x64 使用 `App/packages.aot.lock.json`、`App/packages.lock.json` 和 `Bundle/packages.lock.json`；ARM64 使用对应的 `packages.arm64.aot.lock.json` 与 `packages.arm64.lock.json`。版本支持信息见 [.NET 支持周期](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)和 [Windows App SDK 发布渠道](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)。

## 编译目标

| 命令 | RID | AOT 指令集 | 输出目录 |
| --- | --- | --- | --- |
| `./build.ps1` | `win-x64` | `native`，使用构建 CPU 的指令集 | `dist/SelfContained/` |
| `./build.ps1 -Target Apx` | `win-x64` | `apx` | `dist/SelfContained-Apx/` |
| `./build.ps1 -Target Arm64` | `win-arm64` | `armv8-a` | `dist/SelfContained-Arm64/` |

每个目录生成一个 `LumaRecode.exe`。Native 版面向拥有构建 CPU 所需指令集的 x64 设备；APX 版要求 CPU 和 Windows 均支持 APX，入口在启动 AOT 应用前检查系统能力。ARM64 版使用通用 ARMv8-A 基线，并默认选择软件归档。

ARM64 交叉编译需要安装 Visual Studio C++ ARM64 / ARM64EC 构建工具；x64 需要 C++ x64 / x86 工具。[Native AOT 交叉编译文档](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile)列出了工具链要求。

目标的还原图与中间文件位于各项目的 `obj/<Target>/`、`bin/<Target>/`，AOT 载荷位于 `dist/NativeAot/<Target>/`，分发许可清单位于 `build-cache/licenses/<Target>/`。

## 部署方式

| 命令 | 产物 | 使用要求 |
| --- | --- | --- |
| `./build.ps1` | AOT 单文件 EXE | 内置所需运行时 |
| `./build.ps1 -Compilation Jit` | JIT 单文件 EXE | 内置所需运行时；构建无需原生链接器 |
| `./build.ps1 -Deployment FrameworkDependent` | JIT ZIP | 用户安装 .NET 11 Runtime、Windows App Runtime 2.6 |

`-Target` 可与部署选项组合；APX 使用 SelfContained / Aot。FFmpeg 与 ffprobe 由用户提供。

Ultra HDR 图片编解码组件位于 `App/Native/x64/uhdr.dll` 和 `App/Native/arm64/uhdr.dll`，发布时按架构选入。组件在首次导出 Ultra HDR 时加载。重新编译需要对应架构的 MSVC 工具、CMake 和 Ninja；x64 还需要 NASM：

```powershell
./tools/Build-UltraHdr.ps1 -Architecture x64
./tools/Build-UltraHdr.ps1 -Architecture arm64
```

ARM64 使用 NEON，x64 使用 libjpeg-turbo 的 x86 SIMD。固定源码版本、构建配置和二进制哈希见[原生组件清单](../App/Native/manifest.json)。

## Visual Studio

安装 Visual Studio 2026 18.12 或更新版本，以及 WinUI / C# 桌面开发工具和对应架构的 C++ 构建工具。安装 .NET SDK 11.0.100，并将 PowerShell 7 的 `pwsh` 加入 `PATH`。

打开仓库根目录的 `LumaRecode.sln`，将 `App` 设为启动项目。`Debug | x64` 和 `Debug | ARM64` 用于调试。

| 解决方案配置 | 打包目标 |
| --- | --- |
| Release / x64 | Native x64 |
| Release-APX / x64 | APX x64 |
| Release / ARM64 | ARM64 |

选择配置后执行“生成解决方案”，`Packaging` 会调用 `build.ps1` 完成许可收集、AOT 编译和单文件打包，输出位置与命令行一致。也可单独生成 `Packaging` 项目。`Bundle` 由打包流程生成，QA 项目可单独运行。

通过命令行调用同一套解决方案配置：

```powershell
dotnet build LumaRecode.sln -c Release -p:Platform=x64
dotnet build LumaRecode.sln -c Release-APX -p:Platform=x64
dotnet build LumaRecode.sln -c Release -p:Platform=ARM64
```

`LumaRecode.slnx` 提供相同的项目与配置，供支持该格式的工具使用。

主视图位于 `App/MainView.xaml`。修改翻译文字后运行 `python tools/Update-ViewText.py`，更新 `App/ViewText.g.cs`；构建会检查映射是否一致。检查命令见 [QA 指南](../qa/README.md)，品牌资源生成见 [Brand/README.md](../Brand/README.md)。

模块职责与生命周期见[代码结构](architecture.md)。提交前运行 `./tools/Check-Source.ps1`。Python 工具的格式与静态规则位于 `pyproject.toml`，可使用 Ruff 执行 `ruff check qa tools Brand` 和 `ruff format --check qa tools Brand`。

## 字体

界面使用 Source Sans 3、思源黑体中文与日文字体，日志使用 Source Code Pro，并回退到对应的思源黑体。字体随应用提供，无需安装。

原始字体位于 `App/Assets/Fonts/`，版权和 SIL OFL 1.1 原文位于 `licenses/Fonts/`。上游提交、下载地址和 SHA-256 固定在[字体来源清单](../licenses/Fonts/manifest.json)；构建时校验字体与许可文件的哈希。

## 单文件结构

`Bundle/` 使用 .NET 单文件提取机制打包 AOT 主程序、Windows App SDK、转换核心、LUT 和许可文件。首次启动时，依赖提取到用户临时缓存。

GUI 模式下，入口启动 AOT 主程序后退出。CLI 模式下，入口继承标准句柄和工作目录，等待任务结束并返回退出码。命令行用法见 [CLI 手册](cli.zh-CN.md)。

单文件发布使用 `IncludeAllContentForSelfExtract`，将 WinUI 资源和运行时提取为磁盘文件。性能设计与测量见[性能优化](performance.md)。

## 依赖收集

构建脚本执行以下步骤：

1. 按锁文件还原 NuGet 依赖。
2. 合并应用与打包入口的依赖图，收集包许可、声明及隐式运行时记录。
3. 校验字体、原生编解码库与许可文件哈希，在目标许可目录生成 `manifest.json`，随对应架构的应用分发。

应用与转换核心使用 C# / .NET，GUI 和 CLI 共用 `Core/` 中的处理逻辑。发布包包含所需的微软运行时、字体及 LUT；FFmpeg / ffprobe 由用户提供。

项目自主内容采用 MIT；第三方运行时、字体和 LUT 使用各自许可。各组件的分发依据、LUT 来源及完整原文入口见[第三方组件](../Assets/THIRD_PARTY.md)。

## English

Use the pinned .NET SDK, PowerShell 7 and Visual Studio C++ tools for x64 / ARM64. `build.ps1` defaults to Native x64 (`IlcInstructionSet=native`); `-Target Apx` selects APX x64, and `-Target Arm64` selects ARM64 with an ARMv8-A baseline. The Native variant requires the build CPU's selected instructions. APX requires CPU and OS support, checked by the launcher. ARM64 defaults to software encoding. FFmpeg is user-supplied.

Open `LumaRecode.sln` in Visual Studio 2026 18.12 or later with WinUI, C# desktop and C++ build tools installed. Use `App` as the startup project for Debug. Build `Release | x64`, `Release-APX | x64` or `Release | ARM64` to have `Packaging` run the complete single-EXE pipeline. Outputs are `dist/SelfContained/`, `dist/SelfContained-Apx/` and `dist/SelfContained-Arm64/`.

`-Compilation Jit` selects the JIT build; `-Deployment FrameworkDependent` produces a ZIP requiring external runtimes. APX uses self-contained AOT. Each target has separate restore, native payload and license directories, with architecture-specific lock files and Ultra HDR binaries.

The single-file entry extracts dependencies, starts the AOT UI, and exits for GUI use. For CLI use it waits and preserves standard handles, the working directory, and exit codes. Update the static localization map after editing translated XAML. See the linked QA guide, performance notes, and third-party notices for details.
