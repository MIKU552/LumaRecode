# 第三方组件

界面使用 Microsoft Windows App SDK / WinUI 3，源代码 https://github.com/microsoft/WindowsAppSDK 和 https://github.com/microsoft/microsoft-ui-xaml ，按微软随 SDK 提供的许可使用。
界面运行时使用 .NET 8，源代码 https://github.com/dotnet/runtime 。许可及第三方通知随源代码包附带，见 DotNet-LICENSE.txt 和 DotNet-THIRD-PARTY-NOTICES.txt。
- Python 3.14 的许可见 Python-LICENSE.txt，源代码 https://www.python.org/downloads/source/
- PyInstaller 运行时遵循 GPL v2 及其打包例外，见 PyInstaller-COPYING.txt。https://pyinstaller.org/

FFmpeg / ffprobe 由使用者自行提供，没有打包进 EXE。本版本不使用 Qt / PySide。

Pocket 3 D-Log M → HLG LUT 来自 OpenOSV v0.6.4。
https://github.com/Kemerd/OpenOSV/releases/tag/v0.6.4
原始 LUT 没有修改。其 Apache 2.0 LICENSE 与 NOTICE 位于 OpenOSV 子目录。
这是独立项目的拟合转换，不是 DJI 官方 HDR LUT。

Mi-Log 与 Samsung Log 的 HLG LUT 根据公开曲线生成，包含固定的曝光和高光映射。
它们是便于直接分享的技术转换，不会恢复已剪切的亮部或替代逐镜头曝光调整。
