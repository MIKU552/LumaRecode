# 第三方组件与许可 · Third-party components

适用版本：FrameFlow 3.5.0，审阅日期 2026-11-12。当前运行时原始许可集中在 [licenses](../../licenses/)，NuGet 包与运行时记录见 [manifest.json](../../licenses/manifest.json)，LUT 来源与哈希见 [license-manifest.json](license-manifest.json)。此结论不追溯改变旧 EXE 内的组件或条款。

Scope: the current 3.5.0 build, reviewed 2026-11-12, not every historical binary. Full original terms and notices are retained under `licenses/`; the manifests separate runtime provenance from LUT provenance.

## 自主项目 / Original project

自主代码、文档、原创图形及项目生成的 Mi-Log / Samsung Log LUT 采用根目录 [Apache-2.0](../../LICENSE)，允许商用和闭源修改，须保留许可、适用声明及标明修改。第三方文件不因此改为 Apache-2.0；[NOTICE](../../NOTICE) 保留归属说明。许可不替使用者取得其他编解码器专利，也不解决名称商标问题。Codex 工具署名不等于将 OpenAI 列为版权人，也不能证明纯 AI 内容在各法域都有版权。

Original project work uses Apache-2.0. Third-party components keep their own rights. Tool attribution does not establish copyright subsistence, third-party patent rights, trademark clearance, or OpenAI copyright ownership.

## Windows 单文件版 / Windows single-file release

| 组件 / Component | 分发依据 / Distribution basis |
| --- | --- |
| .NET 11.0.0 / apphost / ASP.NET runtime files | 多数文件为 MIT；Windows `coreclr.dll`、单文件内的运行时及指定 WPF 等二进制适用 .NET Library License，另有 Windows SDK 个别组件条款。保留全文与匹配第三方声明 / MIT plus Windows-specific binary licenses |
| Windows SDK .NET 投影 | `Microsoft.Windows.SDK.NET.Ref 10.0.26100.87` 的许可 URL 指向 Windows SDK 条款，其 REDIST 清单的同名章节明确允许随程序分发未修改文件 / SDK terms and explicit projection-package REDIST grant |
| Windows App SDK / WinUI 2.6.5 | 正式包 Microsoft Windows App SDK 条款第 3 节，允许随应用分发 NuGet 放入输出目录的代码，须满足分发要求 / Production package terms for NuGet-binplaced files |
| WebView2 SDK | WinUI 传递依赖，保留 BSD-3-Clause LICENSE / NOTICE；未打包浏览器安装器 / Transitive SDK notices; no browser installer |
| 官方 CPython 3.15.2 Windows 嵌入式包 | 完整保留官方包；PSF/Python 历史许可、嵌入组件条款，以及 LICENSE 中 Windows binary 附加条件 / Unmodified official package and its Windows-binary conditions |
| Python 内 VC Runtime | 依据官方 Python Windows 二进制的授权链保留声明、平台限制和接收者条款；不是 PSF/Apache 许可 / Microsoft-specific conditions remain applicable |
| Python 内 OpenSSL、Expat、HACL*、libffi、mpdecimal、libtommath、SQLite、XZ/liblzma、zlib-ng、zstd、bzip2 等 | 保留 PSF 汇总文本及[依赖原文和来源](../../licenses/Python-dependencies/sources.json)。zstd 选择 BSD 许可；只有 liblzma，不含 XZ 的 GPL 脚本 / Vendor libraries retain their individual terms |
| Pocket 3 LUT | OpenOSV v0.6.4，Apache-2.0，LUT 与原始 [LICENSE](OpenOSV/LICENSE) / [NOTICE](OpenOSV/NOTICE) 未修改 |

首次启动要求接受随附第三方二进制的适用条款，并提供完整原文入口；之后可从“工具路径”打开许可目录。微软要求包括保留声明、不暗示背书、遵守分发及平台限制，并向后续接收者传递适用条款。这些条件仅针对对应组件，不把自主源码变成非商业许可。原始全文优先于此摘要。

The first-start agreement and Tool paths link expose the full component terms. Preserve notices, avoid implied endorsement, obey platform and distribution restrictions, and pass required terms to recipients. These obligations apply to the respective third-party binaries, not the original Apache-2.0 source. Original texts govern.

### WinUI 与开源、旧结论修正 / WinUI and open source

WinUI 可以用于开源项目。源码许可与微软编译分发包的条款需要分别看待；应用开源不要求所有运行时都使用同一个许可。旧审计检出的 WinUI 2.0.12 确实携带 Engineering Preview 文本；新版已换为 2.6.5，逐包检查实际 LICENSE，并在构建中阻止再次混入该预览许可。不能据一个旧包推导“WinUI 不允许开源”。

此前“必须为 VC DLL 每个精确补丁版本找到独立分发条款”的表述过于保守。官方 Python LICENSE 已有 **Additional Conditions for this Windows binary build**，明确给出该 Windows 二进制再分发微软代码的条件。现在的依据是完整保留官方 Python 包和这些条件；不是声称通用 VC 最终用户协议本身给任意发布者转发权。微软最终用户条款也保留供接收者阅读。

The old preview finding concerned one package, not a ban on open-source WinUI apps. Current production terms have been checked. The earlier VC blocker overlooked explicit conditions in the official Python Windows LICENSE; that distribution chain is the basis here, rather than treating a standalone VC end-user EULA as a publisher grant.

还须区分 .NET 源码 MIT 与 Windows 二进制例外：微软的[逐组件说明](https://github.com/dotnet/core/blob/main/license-information-windows.md)明确列出单文件运行时等适用 .NET Library License，不能把整个 Windows EXE 都标作 MIT。[该许可](../../licenses/DotNet-Library-LICENSE.html)第 3 节允许随应用分发目标代码；[Windows SDK REDIST](https://learn.microsoft.com/en-us/legal/windows-sdk/redist#microsoftwindowssdknetref)明确包含 .NET 投影包。原文与阅读副本均保留在许可目录。The .NET Windows distribution has binary-license exceptions; source MIT is not a blanket binary license. The component-specific grants and full terms are retained.

## LUT 来源 / LUT provenance

- **Pocket 3**：来自 [OpenOSV v0.6.4](https://github.com/Kemerd/OpenOSV/releases/tag/v0.6.4) 的 `luts/Rec2100_HLG/DJI_Osmo_DLogM_to_Rec2100_HLG_BT2408_Neutral.cube`。LUT、LICENSE、NOTICE 已核对上游字节。这是独立项目的拟合转换，不是 DJI 官方 HDR LUT。上游 NOTICE 涵盖整个 OpenOSV 的依赖，保留它不代表 FrameFlow 集成了这些运行时。
- **Mi-Log / Samsung Log**：由 [generate_hlg_luts.py](../tools/generate_hlg_luts.py) 根据公开数值参数和本项目固定高光映射独立生成，只用标准库。`python Worker/tools/generate_hlg_luts.py --check` 可核对。没有分发厂商原始 LUT、DCTL 或白皮书。[小米资料](https://web.vip.miui.com/page/info/mio/mio/detail?fromPathname=mioSingleBoard&postId=47863325) · [Samsung Log 文档](https://developer.samsung.com/mobile/samsung-log-video.html)

The Pocket 3 LUT is an unchanged third-party fit. Xiaomi/Samsung tables are independently generated from published numerical parameters and project mapping. Manufacturer names describe compatibility, not endorsement. Fixed LUTs cannot recover clipped highlights.

## 外部与开发工具 / External and development tools

- **FFmpeg / ffprobe**：用户提供，以独立子进程调用，不链接 `libav*`、不转发。具体构建可能含 LGPL、GPL 或其他条款，不统一称作 LGPL。独立调用不因 FFmpeg 自身使用 GPL 就自动把界面源码改为 GPL；将来若改为链接、转发或修改，应重新审阅。[FFmpeg 法律说明](https://ffmpeg.org/legal.html)
- **PySide6 / Qt**：仅用于可选图标重绘，版本见 `Brand/requirements.txt`；未随界面或 worker 分发 Qt DLL，所用模块仍受 LGPL/GPL/商业条款约束。[Qt for Python 许可](https://doc.qt.io/qtforpython-6/licenses.html)
- **SDK / BuildTools / MSIX**：按开发工具条款使用，没有转发整套 SDK 安装包；自包含输出包含的文件保留对应许可与声明。
- **图形与字体**：品牌 SVG、箭头为项目绘制；FontIcon 使用系统字体，没有复制 Segoe 字体文件。
- **PyInstaller**：3.5.0 起不再使用或分发。

FFmpeg is external; Qt is an optional artwork tool; SDK installers and system fonts are not copied. PyInstaller has been removed. Future payload changes require review of the actual files.
