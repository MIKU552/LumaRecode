# 开发与分发 / Development and distribution

[简体中文](#简体中文) · [English](#english)

## 简体中文

### 工具链

| 平台 | 开发环境 |
| --- | --- |
| Windows x64 / ARM64 | Visual Studio 2026 18.12+ 的 WinUI 和桌面 C++ 工具，PowerShell 7，.NET SDK |
| macOS ARM64 | Apple Silicon Mac，macOS 27+，Xcode 27.2 |
| 共享核心 | Rustup、CMake、Ninja 和目标平台 C/C++ 工具链 |

`global.json` 固定 .NET SDK，`rust-toolchain.toml` 固定 Rust，`tools/native-build-tools.json` 固定 CMake 和 Ninja。NuGet 锁文件按架构和编译方式区分，包含 Windows App SDK 及其运行时依赖。

### Windows 构建

打开 `LumaRecode.sln` 或 `LumaRecode.slnx`，选 `LumaRecode.App` 进行调试。解决方案的 `Release-AOT` 配置构建 AOT，`Release` 构建 JIT，`Release-APX | x64` 构建 APX。无需通过 IDE 时，从仓库根目录运行：

```powershell
./tools/Install-NativeBuildTools.ps1
./build.ps1
./build.ps1 -Target Arm64 -Deployment Portable
./build.ps1 -Target X64 -Compilation Jit -Deployment FrameworkDependent
```

| 目标 | Native AOT 指令集 |
| --- | --- |
| `Native` | 识别真实 Windows 架构，并按构建机器支持的指令集优化 |
| `X64` | x86-64-v2 |
| `Apx` | Intel APX，启动时检查 CPU 和 Windows 支持 |
| `Arm64` | ARMv8-A |

交叉编译需要目标架构的 C++ 和 Rust 工具。JIT 可选相同平台的基本架构，运行时根据设备选择指令；支持 APX 的设备可通过 `DOTNET_EnableAPX` 控制。.NET Native AOT 工具链和跨架构要求见 [Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile)。

### Windows 打包

| 配置 | 应用运行时 | 可用格式 |
| --- | --- | --- |
| `Aot`（默认） | Native AOT，含所需运行组件 | EXE、ZIP、MSI；x64 / ARM64 也可生成 MSIX |
| `JitExternal` | 用户安装 .NET 11 Runtime；包内含 WinUI | EXE、ZIP、MSI |
| `JitBundled` | 包内含 .NET 及 WinUI | EXE、ZIP、MSI |

单文件 EXE 由引导程序展开后启动。便携 ZIP 和 MSI 从应用目录加载资源。MSI 以当前用户安装，包含开始菜单快捷方式。EXE、ZIP 和 MSI 可独立选择外置或内置 FFmpeg：

```powershell
./tools/Package-Windows.ps1 -Target X64 -Profile Aot -Format Exe,Zip,Msi
$tools = ./tools/Stage-WindowsFfmpeg.ps1 -Target X64
./tools/Package-Windows.ps1 -Target X64 -Profile JitExternal -Ffmpeg Both -FfmpegDirectory $tools
./tools/Package-Windows.ps1 -Target X64 -Format Msix
```

生成文件在 `dist/windows/`。WiX 用于生成 MSI，打包脚本从工具清单还原。打包内置工具需要匹配架构的 `bin/ffmpeg.exe`、`bin/ffprobe.exe` 和 `BUILDINFO.txt`。发布含 FFmpeg 的包时，应同时提供该版本对应的源码和库的许可。[FFmpeg 许可](https://ffmpeg.org/legal.html)

GitHub 正式版的 Windows EXE、ZIP 和 MSI 均提供内置或外置 FFmpeg。内置版使用 MSYS2 的 FFmpeg，包含 `zscale`、`lut3d` 和 `libplacebo`。应用包内含可执行工具、依赖、许可证和构建信息。各架构对应的源码和签名可在[同版本 Release](https://github.com/MIKU552/LumaRecode/releases) 的附件中单独下载。选择外置版时，安装具备这些滤镜的 FFmpeg，在工具路径面板检测或指定程序。

外置 .NET 版启动时由 .NET 宿主查找匹配架构的运行时。桌面启动遇到缺失时，弹窗可打开微软下载页。终端自动化可设置 `DOTNET_DISABLE_GUI_ERRORS=1`，通过退出码、标准错误和下载链接处理缺失的运行时。安装对应 x64 或 ARM64 的 **.NET 11 Runtime** 后重新启动。[微软官方下载](https://dotnet.microsoft.com/download/dotnet/11.0)

### Microsoft Store

开发 MSIX 使用 `MIKU552.LumaRecode.Dev`。发布构建使用 `-PartnerCenter` 读取 [StoreIdentity.json](../Packaging/StoreIdentity.json)，再把 x64 和 ARM64 包合成 `.msixupload`。其中的包名、发布者、发布者显示名和商店 ID 可公开。Entra 凭据应配置在单独的 GitHub `microsoft-store` 环境中。[应用标识字段](https://learn.microsoft.com/en-us/windows/apps/publish/view-app-identity-details)

首次在 Partner Center 补齐价格与可用性、年龄分级、商店内容及认证说明，在“包”页面上传正式发布运行的 `lumarecode-store-<commit>` 工件中的 `.msixupload`。[包上传](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages)

上传时出现 `runFullTrust` 警告后，在「提交选项」填写受限能力用途。应用以当前用户权限运行 WinUI 3 桌面程序，启动随包的 FFmpeg 和 ffprobe，读写用户选择的本地输入和输出，并通过打包 COM 激活完成通知。微软会在认证时审查该说明。认证备注可另附导入素材和软件归档转换的步骤。[微软的受限能力说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations#restricted-capabilities)

首次发布上线后，可为 Microsoft Entra 应用配置 Partner Center 的 Manager 角色，在 `microsoft-store` 环境中设置 `STORE_TENANT_ID`、`STORE_SELLER_ID`、`STORE_CLIENT_ID`、`STORE_CLIENT_SECRET`。在 `main` 手动运行 **Submit Microsoft Store update** 并输入正式发布的 Actions 运行 ID。工作流验证对应的商店包，确认现有提交已处理后送交认证。[微软商店更新文档](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/github-actions)

### macOS 安装与分发

将 DMG 中的应用拖到“应用程序”。如果首次打开被 macOS 拦截，确认下载来源后，在“系统设置 → 隐私与安全性”选择“仍要打开”。[Apple 使用手册](https://support.apple.com/guide/mac-help/open-a-mac-app-from-an-unidentified-developer-mh40616/mac)

使用 `macOS/LumaRecode.xcodeproj` 构建和归档。也可以运行：

```sh
sh tools/build-macos.sh Release --dmg
brew install ffmpeg-full molten-vk
sh tools/build-macos.sh Release --dmg-full-tools
```

标准 DMG 使用系统上的 FFmpeg。FFmpeg Full DMG 把 FFmpeg、ffprobe、libplacebo 和 MoltenVK 放入应用，配套源码包包含对应的上游源码、Homebrew 配方、资源和补丁。两种 DMG 都使用 ad-hoc 签名。

### 第三方资源

Windows 界面使用 Source Sans 3 和思源黑体，日志使用 Source Code Pro，并回退到思源黑体。字体与原始许可一同打包。版本和哈希见[字体清单](../licenses/Fonts/manifest.json)。

Ultra HDR 使用 Google libultrahdr 和 libjpeg-turbo，可通过 `tools/Build-UltraHdr.ps1` 为各架构重建。源码版本、构建设置和二进制哈希见[组件清单](../App/Native/manifest.json)。

构建脚本从锁定的依赖图收集许可，校验字体和原生组件；单文件包也收集引导程序依赖。[项目许可](../LICENSE) · [第三方声明](../NOTICE)

### 构建来源与验证

手动运行 **Native core** 并勾选 `release_packages` 可准备候选版。Windows 生成两种架构的 AOT 和外置 .NET 包，macOS 生成标准和 FFmpeg Full DMG，同时生成共享 SDK、SHA-256 校验文件和商店上传包。

在 `main` 的正式版本提交上创建并推送已签名的版本标签，手动运行 **Publish GitHub release** 并输入相同的版本号。工作流运行回归检查、验证构建来源，再从草稿发布 GitHub Release。普通提交和 PR 检查不会发版。

```sh
gh attestation verify <file> --repo MIKU552/LumaRecode --signer-workflow MIKU552/LumaRecode/.github/workflows/native-core.yml
```

可用 `--source-ref` 和 `--source-digest` 核对指定标签和提交。[构建来源证明](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations) · [QA 指南](../qa/README.md)

## English

### Toolchains

| Platform | Development environment |
| --- | --- |
| Windows x64 / ARM64 | Visual Studio 2026 18.12+ with WinUI and desktop C++ tools, PowerShell 7, .NET SDK |
| macOS ARM64 | Apple silicon Mac, macOS 27+, Xcode 27.2 |
| Shared core | Rustup, CMake, Ninja, and the platform's C/C++ toolchain |

`global.json` pins the .NET SDK, `rust-toolchain.toml` pins Rust, and `tools/native-build-tools.json` pins CMake and Ninja. The NuGet lock files cover Windows App SDK and runtime dependencies by architecture and compilation mode.

### Windows builds

Open `LumaRecode.sln` or `LumaRecode.slnx` and select `LumaRecode.App` for debugging. The `Release-AOT` solution configuration compiles with AOT, `Release` builds JIT, and `Release-APX | x64` targets APX. To build from the command line, run from the repository root:

```powershell
./tools/Install-NativeBuildTools.ps1
./build.ps1
./build.ps1 -Target Arm64 -Deployment Portable
./build.ps1 -Target X64 -Compilation Jit -Deployment FrameworkDependent
```

| Target | Native AOT instruction set |
| --- | --- |
| `Native` | Detects the real Windows architecture and optimizes for the build CPU |
| `X64` | x86-64-v2 |
| `Apx` | Intel APX, with CPU and OS checks at launch |
| `Arm64` | ARMv8-A |

Cross-compiling requires C++ and Rust tools for the target architecture. JIT selects instructions on the running device. On eligible systems, `DOTNET_EnableAPX` controls APX. See [Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile) for Native AOT toolchain and cross-compilation requirements.

### Windows packages

| Profile | Application runtime | Formats |
| --- | --- | --- |
| `Aot` (default) | Native AOT with its runtime components | EXE, ZIP, MSI, and MSIX on baseline x64 / ARM64 |
| `JitExternal` | Installed .NET 11 Runtime, with WinUI in the package | EXE, ZIP, MSI |
| `JitBundled` | .NET and WinUI in the package | EXE, ZIP, MSI |

The single EXE extracts the app before starting it. Portable ZIPs and MSI installations load resources from the app directory. The MSI installs per user and adds a Start menu shortcut. EXE, ZIP, and MSI support either external or bundled FFmpeg:

```powershell
./tools/Package-Windows.ps1 -Target X64 -Profile Aot -Format Exe,Zip,Msi
$tools = ./tools/Stage-WindowsFfmpeg.ps1 -Target X64
./tools/Package-Windows.ps1 -Target X64 -Profile JitExternal -Ffmpeg Both -FfmpegDirectory $tools
./tools/Package-Windows.ps1 -Target X64 -Format Msix
```

Packages appear in `dist/windows/`. The packaging script restores WiX from the tool manifest. Bundling FFmpeg requires an architecture-matched `bin/ffmpeg.exe` and `bin/ffprobe.exe`, plus a `BUILDINFO.txt`. For public distribution, provide the corresponding source and library license texts for the exact binaries. [FFmpeg licensing](https://ffmpeg.org/legal.html)

GitHub releases offer each Windows EXE, ZIP, and MSI with or without FFmpeg. Bundled editions use MSYS2 FFmpeg with `zscale`, `lut3d`, and `libplacebo`. The application packages contain the executables, dependencies, license texts and build information. The corresponding source and signatures for each architecture are separate assets on the [same version's release page](https://github.com/MIKU552/LumaRecode/releases). For an external edition, install an FFmpeg build with those filters and use the tool path panel to detect or specify it.

The framework-dependent edition uses the .NET app host to locate a runtime for the executable's architecture. If it is missing on a desktop launch, the dialog offers a Microsoft download link. Set `DOTNET_DISABLE_GUI_ERRORS=1` for unattended command-line use and handle the nonzero exit code, diagnostic, and download link on standard error. Install the **.NET 11 Runtime** for x64 or ARM64 as appropriate, then relaunch. [Microsoft downloads](https://dotnet.microsoft.com/download/dotnet/11.0)

### Microsoft Store

The development MSIX uses `MIKU552.LumaRecode.Dev`. Release builds use `-PartnerCenter` to read [StoreIdentity.json](../Packaging/StoreIdentity.json) and combine the x64 and ARM64 packages into an `.msixupload`. The package name, publisher, publisher display name and Store ID can be public. Configure Entra credentials in the separate `microsoft-store` GitHub environment. [App identity fields](https://learn.microsoft.com/en-us/windows/apps/publish/view-app-identity-details)

For the first submission, complete pricing and availability, age ratings, listings, and certification notes in Partner Center. On its Packages page, upload the `.msixupload` file from the official release run's `lumarecode-store-<commit>` artifact. [Package upload](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages)

When the upload reports the `runFullTrust` warning, describe the restricted capability under **Submission options**. LumaRecode runs as a WinUI 3 desktop app under the signed-in user's permissions, launches the bundled FFmpeg and ffprobe, reads and writes the local paths the user selects, and uses packaged COM activation for completion notifications. Microsoft reviews the explanation during certification. Include sample import and Software archive conversion steps in the certification notes. [Microsoft's restricted capability guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations#restricted-capabilities)

After the first version goes live, assign the Microsoft Entra application the Partner Center Manager role. Set `STORE_TENANT_ID`, `STORE_SELLER_ID`, `STORE_CLIENT_ID`, and `STORE_CLIENT_SECRET` in the `microsoft-store` environment. Manually run **Submit Microsoft Store update** on `main` with the official release's Actions run ID. The workflow verifies the package and sends it for certification once any existing submission is resolved. [Microsoft Store update documentation](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/github-actions)

### macOS installation and distribution

Drag the app from the DMG to Applications. If macOS blocks the first launch, verify the download source and choose **Open Anyway** in **System Settings → Privacy & Security**. [Apple User Guide](https://support.apple.com/guide/mac-help/open-a-mac-app-from-an-unidentified-developer-mh40616/mac)

Build and archive with `macOS/LumaRecode.xcodeproj` or run:

```sh
sh tools/build-macos.sh Release --dmg
brew install ffmpeg-full molten-vk
sh tools/build-macos.sh Release --dmg-full-tools
```

The standard DMG uses system-provided FFmpeg. The FFmpeg Full DMG includes FFmpeg, ffprobe, libplacebo, and MoltenVK in the app. Its accompanying source archive contains upstream sources, Homebrew formulas, resources, and patches. Both editions use ad-hoc signing.

### Third-party resources

Windows uses Source Sans 3 and Source Han Sans in the interface. Logs use Source Code Pro with Source Han Sans as a fallback. The original licenses ship with the fonts. See the [font manifest](../licenses/Fonts/manifest.json) for revisions and hashes.

Ultra HDR uses Google libultrahdr and libjpeg-turbo. Rebuild them for either architecture with `tools/Build-UltraHdr.ps1`. See the [component manifest](../App/Native/manifest.json) for source versions, build settings, and binary hashes.

Build scripts collect licenses from the locked dependency graph and verify the fonts and native components. The single EXE also includes the launcher dependency notices. [Project license](../LICENSE) · [Third-party notices](../NOTICE)

### Provenance and validation

Run **Native core** manually with `release_packages` checked to prepare a release candidate. It produces Windows AOT and framework-dependent packages for both architectures, standard and FFmpeg Full macOS DMGs, shared SDKs, SHA-256 checksums, and the Store upload.

Create and push a signed version tag on the release commit of `main`, then manually run **Publish GitHub release** with the matching version. The workflow runs regression checks, verifies build provenance, and publishes the GitHub release from a draft. Regular pushes and pull request checks do not publish releases.

```sh
gh attestation verify <file> --repo MIKU552/LumaRecode --signer-workflow MIKU552/LumaRecode/.github/workflows/native-core.yml
```

Use `--source-ref` and `--source-digest` to check a specific tag and commit. [Artifact attestations](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations) · [QA guide](../qa/README.md)
