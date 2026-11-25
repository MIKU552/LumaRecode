"""Stage signed MSYS2 tools and collect the sources shared by both Windows builds."""

import argparse
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
from urllib.request import urlopen
from zipfile import ZIP_STORED, ZipFile

PLATFORMS = {
    "x64": ("ucrt64", 0x8664),
    "arm64": ("clangarm64", 0xAA64),
}
SOURCE_ROOT = "https://repo.msys2.org/mingw/sources"
SOURCE_ARCHIVE = "LumaRecode-Windows-FFmpeg-Sources.zip"


def run(tool, *args):
    return subprocess.run(
        [str(tool), *map(str, args)],
        check=True,
        stdout=subprocess.PIPE,
        encoding="utf-8",
        errors="replace",
        timeout=180,
    ).stdout


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def imports(path, machine):
    import pefile

    # Delay imports also need to be available when the user selects optional filters.
    tables = ("IMAGE_DIRECTORY_ENTRY_IMPORT", "IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT")
    with pefile.PE(str(path), fast_load=True) as image:
        if image.FILE_HEADER.Machine != machine:
            raise ValueError(f"Unexpected architecture: {path}")
        image.parse_data_directories([pefile.DIRECTORY_ENTRY[table] for table in tables])
        for field in ("DIRECTORY_ENTRY_IMPORT", "DIRECTORY_ENTRY_DELAY_IMPORT"):
            for entry in getattr(image, field, ()):
                yield entry.dll.decode("ascii").lower()


def collect_binaries(prefix, machine, destination):
    index = {path.name.lower(): path for path in prefix.glob("*") if path.is_file()}
    pending = ["ffmpeg.exe", "ffprobe.exe", "vulkan-1.dll"]
    shipped = set()
    while pending:
        name = pending.pop()
        if name in shipped:
            continue
        if name not in index:
            raise FileNotFoundError(prefix / name)
        path = index[name]
        shutil.copy2(path, destination / path.name)
        shipped.add(name)
        # Windows API and driver imports are resolved by the target system.
        pending.extend(dll for dll in imports(path, machine) if dll in index)
    return shipped


def find_package_sources(msys, platform, files):
    pacman = msys / "usr/bin/pacman.exe"
    bsdtar = msys / "usr/bin/bsdtar.exe"
    owners = {}
    notices = defaultdict(list)
    for row in run(pacman, "-Ql").splitlines():
        package, item = row.split(" ", 1)
        if not item.startswith(f"/{platform}/") or item.endswith("/"):
            continue
        if item.startswith(f"/{platform}/bin/"):
            owners[item.rsplit("/", 1)[-1].lower()] = package
        if item.startswith(f"/{platform}/share/licenses/"):
            notices[package].append(item.removeprefix("/"))
    versions = dict(row.split(" ", 1) for row in run(pacman, "-Q").splitlines())
    packages = sorted({owners[name] for name in files})
    ffmpeg = owners["ffmpeg.exe"]
    if versions[ffmpeg] != "9.1-1":
        raise ValueError(f"Expected the reviewed FFmpeg 9.1-1 build, found {versions[ffmpeg]}")

    cache = msys / "var/cache/pacman/pkg"
    for package in packages:
        version = versions[package].replace(":", "~")
        archives = [
            candidate
            for candidate in cache.glob(f"{package}-{version}-*.pkg.tar.*")
            if candidate.suffix in {".zst", ".xz", ".gz"}
        ]
        if len(archives) != 1:
            raise ValueError(f"Expected one signed package in the MSYS2 cache: {package} {version}")
        metadata = dict(
            row.split(" = ", 1)
            for row in run(bsdtar, "-xOf", archives[0], ".PKGINFO").splitlines()
            if " = " in row
        )
        if metadata.get("pkgname") != package or metadata.get("pkgver") != versions[package]:
            raise ValueError(f"Unexpected MSYS2 package metadata: {archives[0].name}")
        base = metadata["pkgbase"]
        if not re.fullmatch(r"[A-Za-z0-9_+.~-]+", base) or not re.fullmatch(r"[A-Za-z0-9_+.~-]+", version):
            raise ValueError("Invalid MSYS2 package identity")
        yield package, version, f"{base}-{version}.src.tar.zst", notices[package]


def download(url, path):
    with urlopen(url, timeout=120) as body, path.open("xb") as target:
        shutil.copyfileobj(body, target)


def stage_payload(options):
    platform, machine = PLATFORMS[options.arch]
    msys = options.msys.resolve(strict=True)
    out = options.output.resolve()
    out.mkdir(parents=True)
    payload = out / "FFmpeg"
    binary = payload / "bin"
    binary.mkdir(parents=True)
    files = collect_binaries(msys / platform / "bin", machine, binary)
    package_rows = []
    source_packages = set()
    for name, version, source, notices in find_package_sources(msys, platform, files):
        package_rows.append(f"{name} {version} {source}")
        source_packages.add(source)
        for item in notices:
            destination = payload / "licenses" / Path(item).relative_to(Path(platform) / "share/licenses")
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(msys / item, destination)
    buildinfo = payload / "BUILDINFO.txt"
    buildinfo.write_text(
        f"FFmpeg 9.1-1 for Windows {options.arch} ({platform}, MSYS2)\n"
        f"GPL distribution. Corresponding source: {SOURCE_ARCHIVE}\n"
        f"Upstream source packages: {SOURCE_ROOT}/\n\n"
        + "\n".join(package_rows)
        + "\n\nSHA-256 of shipped programs and DLLs\n"
        + "".join(f"{digest(path)}  {path.name}\n" for path in sorted(binary.iterdir())),
        encoding="utf-8",
    )
    manifest = out / "source-input"
    manifest.mkdir()
    shutil.copy2(buildinfo, manifest / buildinfo.name)
    (manifest / "packages.json").write_text(
        json.dumps({"architecture": options.arch, "sources": sorted(source_packages)}, indent=2) + "\n",
        encoding="utf-8",
    )
    print(payload, flush=True)


def collect_sources(options):
    msys = options.msys.resolve(strict=True)
    out = options.output.resolve()
    sources = out / "sources"
    sources.mkdir(parents=True)
    packages = set()
    buildinfo = {}
    for entry in options.inputs:
        manifest = json.loads((entry / "packages.json").read_text(encoding="utf-8"))
        arch = manifest["architecture"]
        if arch not in PLATFORMS or arch in buildinfo or not manifest["sources"]:
            raise ValueError(f"Unexpected Windows source manifest: {entry}")
        buildinfo[arch] = entry / "BUILDINFO.txt"
        for source in manifest["sources"]:
            if not re.fullmatch(r"[A-Za-z0-9_+.~-]+\.src\.tar\.zst", source):
                raise ValueError(f"Invalid source package name: {source}")
            packages.add(source)
    if set(buildinfo) != set(PLATFORMS):
        raise ValueError("Expected one source manifest per Windows architecture")

    # Both architectures frequently use the same signed MSYS2 source archive.
    filenames = sorted(packages | {f"{source}.sig" for source in packages})
    with ThreadPoolExecutor(max_workers=6) as workers:
        for future in [workers.submit(download, f"{SOURCE_ROOT}/{name}", sources / name) for name in filenames]:
            future.result()

    gpg = msys / "usr/bin/gpg.exe"
    cygpath = msys / "usr/bin/cygpath.exe"
    for source in sorted(packages):
        signature_path = run(cygpath, "-u", sources / f"{source}.sig").strip()
        archive_path = run(cygpath, "-u", sources / source).strip()
        status = run(gpg, "--batch", "--homedir", "/etc/pacman.d/gnupg", "--status-fd", "1",
                     "--verify", signature_path, archive_path)
        if not re.search(r"^\[GNUPG:\] TRUST_(FULLY|ULTIMATE)", status, re.MULTILINE):
            raise ValueError(f"Source package signature is not trusted: {source}")
        print(f"Verified source for {source}", flush=True)

    with ZipFile(out / SOURCE_ARCHIVE, "x", compression=ZIP_STORED) as archive:
        for arch, path in sorted(buildinfo.items()):
            archive.write(path, f"BUILDINFO-{arch}.txt")
        for path in sorted(sources.iterdir()):
            archive.write(path, f"sources/{path.name}")


def main():
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(required=True)
    stage = commands.add_parser("stage")
    stage.add_argument("--arch", required=True, choices=tuple(PLATFORMS))
    stage.set_defaults(handler=stage_payload)
    collect = commands.add_parser("collect")
    collect.add_argument("--inputs", required=True, type=Path, nargs=2)
    collect.set_defaults(handler=collect_sources)
    for command in (stage, collect):
        command.add_argument("--msys", required=True, type=Path)
        command.add_argument("--output", required=True, type=Path)
    options = parser.parse_args()
    options.handler(options)


if __name__ == "__main__":
    main()
