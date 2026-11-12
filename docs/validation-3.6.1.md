# 3.6.1 发布检查 / Release checks

日期：2026-11-12。依维护者要求，本版只进行源码静态检查和 Windows x64 Release 构建，不启动 GUI，不执行 CLI 或视频转码回归。

- WinUI / C# 发布构建：通过，生成单文件 FrameFlow.exe。
- JSON、XAML、Python 语法及新增界面文案的资源引用：静态检查通过。
- 许可窗口按语言加载组件条款说明；完整第三方许可原文与来源文件保持。中文版是项目提供的说明，不替代第三方原始许可。
- 许可选择、关于窗口的实际交互与视觉表现尚未进行本版实机核验。
- 编码预设与三张 LUT 未修改。3.6.0 的历史集成结果见 [validation-3.6.0.md](validation-3.6.0.md)，不作为本版重新执行过测试的声明。
- 保留既有 NETSDK1244 单文件提取模式提示，原因见[依赖说明](dependencies.md)。

At the maintainer's request, this release receives static source checks and a Windows x64 Release build only. No GUI launch, CLI execution or video encoding tests are performed for 3.6.1. Its new dialog interactions and appearance remain unverified on the device.
