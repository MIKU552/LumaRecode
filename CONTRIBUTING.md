# 参与贡献 / Contributing

[简体中文](#简体中文) · [English](#english)

## 简体中文

1. 在 [GitHub Issues](https://github.com/MIKU552/LumaRecode/issues) 搜索已有问题。报告转换或预览故障时，提供应用版本、系统和架构、FFmpeg 版本及编码器，说明预期和实际结果。分享日志前请删掉本机路径和私人信息。安全问题请用[私密渠道](SECURITY.md#简体中文)。
2. 按[开发与分发指南](docs/dependencies.md#简体中文)准备工具链。Windows 可用 Visual Studio 或 PowerShell 构建，macOS 可用 Xcode 或 Shell 脚本。改动共享核心时，请核对两个平台的调用契约。
3. 根据[检查指南](qa/README.md#简体中文)运行相关检查，在拉取请求里注明已验证的平台、构建和操作。行为变更请更新相应语言的说明。硬件专属改动请说明验证过的设备与驱动。

提交到项目的内容应可按[项目许可](LICENSE)分发。请遵守[行为准则](CODE_OF_CONDUCT.md#简体中文)。

## English

1. Search [GitHub Issues](https://github.com/MIKU552/LumaRecode/issues) for existing reports. For conversion and preview issues, include the app version, OS, architecture, FFmpeg version, encoder, and expected and actual results. Remove local paths and private information before sharing logs. Use the [private channel](SECURITY.md#english) for security issues.
2. Set up the toolchain using the [development and distribution guide](docs/dependencies.md#english). Build with Visual Studio or PowerShell on Windows, and Xcode or shell scripts on macOS. Check both hosts' calling contracts when changing the shared core.
3. Run the relevant [QA checks](qa/README.md#english) and list the tested platforms, builds and tasks in the pull request. Update the affected language editions of the documentation when behavior changes. Include device and driver details for hardware-specific changes.

Contribute material that can be distributed under the [project license](LICENSE) and follow the [code of conduct](CODE_OF_CONDUCT.md#english).
