# 第三方组件与许可 · Third-party components and licenses

审阅范围：2026-11-11 的 FrameFlow 3.4.7 源码、已解析的 NuGet 包和留存的转换组件。文件来源、版本与 SHA-256 见 [license-manifest.json](license-manifest.json)。这不是对所有历史二进制的许可结论。

Scope: the FrameFlow 3.4.7 source tree, resolved NuGet packages, and retained conversion worker reviewed on 2026-11-11. The manifest records provenance and hashes; it does not clear every historical binary.

## 项目许可范围 / Project license scope

根目录 `LICENSE` 是未修改的 Apache-2.0 正文，适用于自主代码、文档、项目内绘制的图形及项目生成的小米 / 三星 HLG LUT；第三方文件和运行时保留各自条款。根目录 `NOTICE` 记录项目及 LUT 来源。

The root Apache-2.0 license covers original code, documentation, graphics, and the generated Xiaomi / Samsung HLG tables. Third-party material retains its own terms below; the root NOTICE records attribution.

许可允许商用与闭源衍生使用，要求保留许可及适用声明、标明修改。专利授权仅涉及贡献者有权授予的相关权利，不替使用者取得其他权利人的编解码器专利许可，也不解决项目名称的商标冲突。许可不能证明纯 AI 生成内容在所有法域均有可主张的版权；Codex 的工具署名不等同于将 OpenAI 列为项目版权人。[Apache-2.0](https://www.apache.org/licenses/LICENSE-2.0) · [美国版权局 AI 作者资格说明](https://www.copyright.gov/newsnet/2025/1060.html)

Commercial and proprietary derivatives are allowed under Apache-2.0's notice and modification conditions. The grant does not obtain third-party patents or resolve branding conflicts. AI-tool attribution does not establish copyright subsistence or make OpenAI a project copyright holder; human-authorship rules remain jurisdiction-specific.

## 随包与传递依赖 / Included and transitive components

| 组件 / Component | 许可与范围 / License and scope | 保留文本 / Retained text |
| --- | --- | --- |
| OpenOSV v0.6.4 Pocket 3 HLG LUT | Apache-2.0；仅 LUT，文件未修改 / LUT only, unchanged | [LICENSE](OpenOSV/LICENSE)、[NOTICE](OpenOSV/NOTICE) |
| Windows App SDK 2.0.1 / Base / Foundation / DWrite / InteractiveExperiences / Widgets / Runtime | Microsoft 软件条款，不将 NuGet 二进制整体改标 MIT / Microsoft package terms | [LICENSE](WindowsAppSDK-LICENSE.txt)、[完整声明 / full notices](WindowsAppSDK-NOTICE.txt) |
| WinUI 2.0.12 | 包内 Engineering Preview 条款，见发布事项 / Engineering Preview terms; see release status | [LICENSE](WindowsAppSDK-WinUI-LICENSE.txt)、[NOTICE](WindowsAppSDK-WinUI-NOTICE.txt)、[tools NOTICE](WindowsAppSDK-WinUI-Tools-NOTICE.txt) |
| Windows App SDK ML / Windows AI MachineLearning 2.0.300 | Microsoft Windows ML 与第三方条款；传递依赖 / Microsoft terms, transitive dependencies | [ML LICENSE](WindowsAppSDK-ML-LICENSE.txt)、[MachineLearning LICENSE](WindowsAI-MachineLearning-LICENSE.txt)、[NOTICES](WindowsML-THIRD-PARTY-NOTICES.txt) |
| WebView2 SDK 1.0.3719.77 | BSD-3-Clause 与上游声明；未打包浏览器安装器 / BSD-3-Clause; no browser installer bundled | [LICENSE](WebView2-LICENSE.txt)、[NOTICE](WebView2-NOTICE.txt) |
| .NET 8 | MIT 与第三方声明 / MIT and third-party notices | [LICENSE](DotNet-LICENSE.txt)、[NOTICES](DotNet-THIRD-PARTY-NOTICES.txt) |
| System.Numerics.Tensors 9.0.0 | MIT 与第三方声明；传递依赖 / MIT, transitive dependency | [LICENSE](SystemNumericsTensors-LICENSE.txt)、[NOTICES](SystemNumericsTensors-THIRD-PARTY-NOTICES.txt) |
| CPython 3.14.7 | Python / PSF 与历史、嵌入组件许可 / Python / PSF and included notices | [Python-LICENSE.txt](Python-LICENSE.txt) |
| PyInstaller 6.22.3 运行时 / runtime | GPL-2.0 与打包例外，部分文件为 Apache-2.0 / bootloader exception; designated Apache-2.0 files | [COPYING](PyInstaller-COPYING.txt)、[上游说明](https://pyinstaller.org/en/stable/license.html) |
| OpenSSL 3.5.7：libcrypto-3.dll / libssl-3.dll | Apache-2.0；在转换组件中检出 / found in the retained worker | [LICENSE](OpenSSL-LICENSE.txt)、[AUTHORS](OpenSSL-AUTHORS.md) |
| VCRUNTIME140.dll 14.51.36247.0 | Microsoft 运行库分发条款；不是 Python 或项目的 Apache 许可 / Microsoft runtime terms | 精确版本的分发依据仍需补证 / version-matched evidence pending |

传递包出现在解析清单中，不表示应用使用了其中每个功能或发布了所有文件。Future releases must review the actual payload, not assume that every resolved package is used or shipped. The ML dependencies do not mean that FrameFlow performs AI inference.

## LUT 来源 / LUT provenance

- **Pocket 3**：使用 [OpenOSV v0.6.4](https://github.com/Kemerd/OpenOSV/releases/tag/v0.6.4) 的 `luts/Rec2100_HLG/DJI_Osmo_DLogM_to_Rec2100_HLG_BT2408_Neutral.cube`。LUT、LICENSE、NOTICE 均逐字节核对上游 Git blob，保持原样。这是独立项目的拟合转换，不是 DJI 官方 HDR LUT。上游 NOTICE 同时说明整个 OpenOSV 应用的依赖，保留它不代表 FrameFlow 集成了那些运行库。
- **Mi-Log / Samsung Log**：由 [generate_hlg_luts.py](../tools/generate_hlg_luts.py) 根据公开传递函数参数及本项目固定曝光、高光映射独立生成，仅使用 Python 标准库。运行 `python Worker/tools/generate_hlg_luts.py --check` 可核对现有两张表。仓库不包含厂商原始 Rec.709 LUT、DCTL 或白皮书，也不把“可下载”当作转发官方文件的许可。
- 技术参考：[小米 Mi-Log / ACES 资料说明](https://web.vip.miui.com/page/info/mio/mio/detail?fromPathname=mioSingleBoard&postId=47863325)、[Samsung Log Video / White Paper](https://developer.samsung.com/mobile/samsung-log-video.html)。品牌名仅说明兼容性，不代表厂商认证或背书；固定 LUT 不能恢复已剪切的高光。

The Pocket 3 LUT and its LICENSE / NOTICE match OpenOSV v0.6.4 byte for byte. Xiaomi / Samsung tables are independently generated from published numerical transfer-function parameters and the project's mapping. The generator reproduces the committed tables; vendor LUTs, DCTL source and white papers are not redistributed. Manufacturer names describe compatibility, not endorsement.

## 外部与开发依赖 / External and development dependencies

- **FFmpeg / ffprobe**：由使用者提供，以独立子进程调用；不打包它们、不链接 `libav*`、不复制其源码。具体构建可能含 LGPL、GPL 或其他受限组件，不能一概称为 LGPL。现有独立调用方式没有仅因使用 FFmpeg 就要求把项目改成 GPL 的依据。未来若集成库、打包二进制或修改源码，应重新核对义务。[官方许可说明](https://ffmpeg.org/legal.html)
- **PySide6 / Qt**：仅用于 `Brand/render_icon.py` 将项目原创 SVG 绘制为 PNG / ICO。当前转换组件没有检出 Qt / PySide DLL。重绘工具仍受所用 Qt 模块的 LGPL / GPL / 商业条款约束，不将这些库重新标为 Apache-2.0。[Qt 许可](https://doc.qt.io/qtforpython-6/licenses.html)
- **Windows SDK BuildTools / MSIX / .NET ILLink / PyInstaller 构建工具**：按各自安装许可使用；不在源码仓库中再分发整套 SDK 或安装包。
- **图形与字体**：品牌 SVG、简单箭头由项目内代码绘制。WinUI FontIcon 使用系统字体，未将 Segoe 字体文件复制进仓库；系统字体不属于本项目授权的素材。

FFmpeg is a separate user-supplied program, not linked or bundled. PySide / Qt are optional artwork-generation tools, not the current UI runtime. Build tools and system fonts retain their own rights. A future bundled or linked FFmpeg release requires another review.

## 发布事项 / Release status

**自主源码采用 Apache-2.0；现有 Windows EXE 尚未据此获得完整的公开再分发结论。** 当前需要处理的实证事项：

1. **WinUI 预览条款**：已解析的 `Microsoft.WindowsAppSDK.WinUI 2.0.12` 在[官方 NuGet 许可页](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.0.12/License)及本地包中均带 Engineering Preview 条款，涉及开发 / 测试、正式环境、期限及再分发限制。不能只凭版本号或父包的一般许可推定可正式发行。应使用具有明确正式分发授权的依赖组合，或取得微软适用授权 / 澄清，再验证 HDR Mica、弹出层和布局。
2. **VC 运行库证据**：留存转换组件确有 `VCRUNTIME140.dll 14.51.36247.0`，现有许可目录没有这个精确版本的原始分发条款。公开发行前须从实际运行库 / Python 分发来源补齐适用的 Microsoft 分发依据及文本；本次未将其他 Visual Studio 版本的许可冒充为它的许可。
3. **旧 EXE 未更新**：本次补齐源码库法律文本和构建复制步骤，没有重打包旧 Release，也没有更改仓库可见性。公开二进制前须先解决前两项并重建，让接收者取得随包许可与声明。构建会将许可嵌入转换组件，并额外输出可读的 `dist/licenses/` 目录。

**The source license is Apache-2.0; this does not clear existing Windows EXEs for public redistribution.** Resolve the WinUI package's preview restrictions, obtain the version-matched VC runtime redistribution evidence, and rebuild with complete accessible notices before public binary distribution. Existing release attachments and repository visibility are unchanged.

清单和哈希记录已核对的文件，不是法律担保，也不替代未来版本及实际发行地区的复核。
