"""Prepend the bilingual download guide to a GitHub release body."""

import json
import re
import sys


GUIDE_START = "<!-- release-downloads:start -->"
GUIDE_END = "<!-- release-downloads:end -->"
GUIDE_BLOCK = re.compile(rf"^{GUIDE_START}\n.*?^{GUIDE_END}\n*", re.MULTILINE | re.DOTALL)


def download_guide(version, repository_url, assets):
    tag = f"v{version}"
    downloads = f"{repository_url}/releases/download/{tag}"
    sizes = {asset["name"]: asset["size"] for asset in assets}

    def link(label, filename, show_size=False):
        if not sizes.get(filename):
            raise ValueError(f"Missing or empty release asset: {filename}")
        if show_size:
            mib = sizes[filename] / (1024 * 1024)
            label += f" · {mib:.1f} MiB" if mib < 10 else f" · {mib:.0f} MiB"
        return f"[{label}]({downloads}/{filename})"

    def windows_row(label, arch, profile, ffmpeg, show_size=False):
        stem = f"LumaRecode-{version}-{arch}-{profile}-ffmpeg-{ffmpeg}"
        links = [
            link("MSI", f"{stem}.msi", show_size),
            link("ZIP", f"{stem}-portable.zip", show_size),
            link("EXE", f"{stem}.exe", show_size),
        ]
        return f"| {label} | {' | '.join(links)} |"

    platforms = (("x64", "x64"), ("arm64", "ARM64"))
    windows_recommended = "\n".join(
        windows_row(f"Windows 11 {label}", arch, "aot", "bundled", show_size=True)
        for arch, label in platforms
    )
    windows_other = "\n".join(
        windows_row(f"{label} · {runtime_label} · {ffmpeg_label}", arch, profile, ffmpeg)
        for arch, label in platforms
        for profile, runtime_label, ffmpeg, ffmpeg_label in (
            ("aot", "AOT", "external", "外置 FFmpeg / External FFmpeg"),
            ("jitexternal", ".NET 11", "bundled", "内置 FFmpeg / Bundled FFmpeg"),
            ("jitexternal", ".NET 11", "external", "外置 FFmpeg / External FFmpeg"),
        )
    )
    full_dmg = link("Full DMG", "LumaRecode-macOS-arm64-FFmpeg-Full.dmg", show_size=True)
    standard_dmg = link("Standard DMG", "LumaRecode-macOS-arm64.dmg", show_size=True)
    guide_url = f"{repository_url}/blob/{tag}/docs/dependencies.md"
    checksums = link("SHA256SUMS.txt", "SHA256SUMS.txt")
    sdk_rows = "\n".join(
        f"| {label} | {link('FFmpeg 源码 / Source', source)} | {link('SDK ZIP', sdk)} |"
        for label, source, sdk in (
            ("Windows x64", "LumaRecode-Windows-x64-FFmpeg-Sources.zip", "LumaRecode-SDK-Windows-x64.zip"),
            ("Windows ARM64", "LumaRecode-Windows-arm64-FFmpeg-Sources.zip", "LumaRecode-SDK-Windows-arm64.zip"),
            ("macOS ARM64", "LumaRecode-macOS-FFmpeg-Full-Sources.tar.gz", "LumaRecode-SDK-macOS-arm64.zip"),
        )
    )

    return f"""{GUIDE_START}
## 下载 / Downloads

### Windows 11

**推荐选 MSI。** 下表都是内置 FFmpeg 的 Native AOT 版。MSI 安装到当前用户，ZIP 解压后运行 `LumaRecode.exe`，单文件 EXE 首次运行时会解包。

**Start with the MSI.** All three formats below bundle FFmpeg and use Native AOT. The MSI installs for your account. For the portable ZIP, extract it and open `LumaRecode.exe`. The single EXE unpacks on first launch.

| 平台 / Platform | 安装包 / Installer | 便携包 / Portable | 单文件 / Single file |
| --- | --- | --- | --- |
{windows_recommended}

<details>
<summary>其他 Windows 版本 / Other Windows builds</summary>

已有兼容的 FFmpeg 和 ffprobe 时，可选外置 FFmpeg 版。标有 **.NET 11** 的版本需要安装同架构的 .NET 11 Runtime。

Choose an external FFmpeg build if you already have compatible FFmpeg and ffprobe executables. Builds labeled **.NET 11** require the runtime for your architecture. [.NET 11 Runtime](https://dotnet.microsoft.com/download/dotnet/11.0)

| 版本 / Build | 安装包 / Installer | 便携包 / Portable | 单文件 / Single file |
| --- | --- | --- | --- |
{windows_other}

</details>

### macOS 27+ · Apple Silicon

| 版本 / Edition | 适合 / Choose this if | 下载 / Download |
| --- | --- | --- |
| 内置 FFmpeg Full · 推荐 / Recommended | 随包附带 FFmpeg 和 HDR 转换组件。 / Includes FFmpeg and the HDR conversion libraries. | {full_dmg} |
| 标准版 / Standard | 已安装兼容的 FFmpeg 和 ffprobe。 / You have compatible FFmpeg and ffprobe executables. | {standard_dmg} |

打开 DMG，把 LumaRecode 拖进「应用程序」。 / Open the DMG and drag LumaRecode into Applications. [macOS 安装说明 / Installation guide]({guide_url})

下载校验 / Verify downloads · {checksums}

<details>
<summary>FFmpeg 源码和开发 SDK / FFmpeg sources and developer SDKs</summary>

| 平台 / Platform | 对应源码 / Corresponding source | 开发工具包 / SDK |
| --- | --- | --- |
{sdk_rows}

</details>
{GUIDE_END}"""


if __name__ == "__main__":
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    version, repository_url = sys.argv[1:]
    release = json.load(sys.stdin)
    guide = download_guide(version, repository_url, release["assets"])
    body = release["body"].replace("\r\n", "\n")
    notes = GUIDE_BLOCK.sub("", body).strip()
    print(f"{guide}\n\n{notes}" if notes else guide)
