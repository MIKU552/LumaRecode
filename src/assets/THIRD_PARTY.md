# 第三方组件

应用使用 PySide6 / Shiboken6 / Qt 6.12.0，依 LGPL v3 使用其开放源代码版本。
本应用没有修改这些组件。许可全文见 LGPL-3.0-only.txt、GPL-3.0-only.txt。

- PySide / Shiboken 源代码 https://code.qt.io/cgit/pyside/pyside-setup.git/ ，标签 v6.12.0
- Qt 源代码 https://code.qt.io/cgit/qt/qtbase.git/ ，标签 v6.12.0
- Qt 官方源代码归档 https://download.qt.io/official_releases/qt/6.12/6.12.0/submodules/
- Python 3.14 的许可见 Python-LICENSE.txt，源代码 https://www.python.org/downloads/source/
- PyInstaller 运行时遵循 GPL v2 及其打包例外，见 PyInstaller-COPYING.txt。https://pyinstaller.org/

随附源代码和构建脚本允许你替换 Qt / PySide 版本，重新生成应用，调试或修改应用。
不限制为调试上述组件修改而进行的逆向工程。FFmpeg / ffprobe 由使用者自行提供，没有打包进 EXE。

Pocket 3 D-Log M → HLG LUT 来自 OpenOSV v0.6.4。
https://github.com/Kemerd/OpenOSV/releases/tag/v0.6.4
原始 LUT 没有修改。其 Apache 2.0 LICENSE 与 NOTICE 位于 OpenOSV 子目录。
这是独立项目的拟合转换，不是 DJI 官方 HDR LUT。

Mi-Log 与 Samsung Log 的 HLG LUT 根据公开曲线生成，包含固定的曝光和高光映射。
它们是便于直接分享的技术转换，不会恢复已剪切的亮部或替代逐镜头曝光调整。
