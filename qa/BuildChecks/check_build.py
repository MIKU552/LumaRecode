"""Check compilation modes, native provenance and the contents of single-file releases."""

import argparse
import ctypes
import hashlib
import json
import mmap
import re
import struct
import subprocess
import xml.etree.ElementTree as ET
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TARGETS = {
    "X64": ("x64", "x64", "x86-64-v2", "SelfContained-X64"),
    "Apx": ("x64", "x64", "apx", "SelfContained-Apx"),
    "Arm64": ("arm64", "ARM64", "armv8-a", "SelfContained-Arm64"),
}


def check_selection():
    script = r"""
    . ./tools/BuildTarget.ps1
    $cases = foreach ($compilation in @('Jit','Aot')) {
        foreach ($architecture in @('X64','Arm64')) {
            foreach ($apx in @($false,$true)) {
                foreach ($target in @('Native','X64','Apx','Arm64')) {
                    $plan = $null; $errorText = $null
                    try { $plan = Resolve-LumaBuildTarget -Target $target -Compilation $compilation -SystemArchitecture $architecture -ApxAvailable $apx }
                    catch { $errorText = $_.Exception.Message }
                    [pscustomobject]@{Compilation=$compilation;HostArchitecture=$architecture;ApxAvailable=$apx;Target=$target;Plan=$plan;Error=$errorText}
                }
            }
        }
    }
    @{Host=(Get-LumaBuildHost);Cases=@($cases)} | ConvertTo-Json -Depth 5 -Compress
    """
    result = subprocess.run(
        ["pwsh", "-NoProfile", "-Command", script],
        cwd=ROOT,
        capture_output=True,
        check=True,
        text=True,
        encoding="utf-8",
        timeout=30,
    )
    data = json.loads(result.stdout)
    native_machine, process_machine = ctypes.c_ushort(), ctypes.c_ushort()
    wow64 = ctypes.windll.kernel32.IsWow64Process2
    wow64.argtypes = [
        ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_ushort),
        ctypes.POINTER(ctypes.c_ushort),
    ]
    assert wow64(
        ctypes.c_void_p(-1), ctypes.byref(process_machine), ctypes.byref(native_machine)
    )
    architecture = {0x8664: "X64", 0xAA64: "Arm64"}[native_machine.value]
    apx_available = bool(ctypes.windll.kernel32.IsProcessorFeaturePresent(90))
    assert data["Host"] == {"Architecture": architecture, "ApxAvailable": apx_available}
    for case in data["Cases"]:
        jit, requested = case["Compilation"] == "Jit", case["Target"]
        if jit and requested == "Apx":
            assert case["Plan"] is None and "-Compilation Aot" in case["Error"]
            continue
        assert case["Error"] is None
        plan, host = case["Plan"], case["HostArchitecture"]
        automatic = (
            "Arm64"
            if host == "Arm64"
            else "Apx"
            if not jit and case["ApxAvailable"]
            else "X64"
        )
        expected = automatic if requested == "Native" else requested
        assert plan["ResolvedTarget"] == expected
        assert plan["Architecture"] == ("arm64" if expected == "Arm64" else "x64")
        assert plan["InstructionSet"] == (
            "jit"
            if jit
            else "native"
            if requested == "Native"
            else TARGETS[expected][2]
        )
    return architecture, len(data["Cases"]) + 1


def evaluate(project, properties, names):
    result = subprocess.run(
        [
            "dotnet",
            "msbuild",
            str(ROOT / project),
            "-nologo",
            "-getProperty:" + ",".join(names),
            *(f"-p:{key}={value}" for key, value in properties.items()),
        ],
        capture_output=True,
        check=True,
        text=True,
        encoding="utf-8",
        timeout=60,
    )
    return json.loads(result.stdout)["Properties"]


def pe_machine(data):
    assert data[:2] == b"MZ"
    offset = struct.unpack_from("<I", data, 0x3C)[0]
    assert data[offset : offset + 4] == b"PE\x00\x00"
    return struct.unpack_from("<H", data, offset + 4)[0]


def machine(path):
    with path.open("rb") as stream:
        return pe_machine(stream.read(65536))


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read_string(stream):
    length, shift = 0, 0
    while True:
        value = stream.read(1)[0]
        length |= (value & 127) << shift
        if value < 128:
            break
        shift += 7
        assert shift <= 28
    assert length <= 4096
    return stream.read(length).decode("utf-8")


def bundle_entries(path):
    # The SDK's v6 bundle manifest records offsets and optional raw-deflate sizes.
    # Inspect both architectures without executing an ARM64 binary on x64 Windows.
    signature = bytes.fromhex(
        "8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae"
    )
    with path.open("rb") as stream:
        with mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as mapped:
            index = mapped.find(signature)
            assert index >= 8
            header = struct.unpack_from("<q", mapped, index - 8)[0]
        stream.seek(header)
        major, minor, count = struct.unpack("<IIi", stream.read(12))
        assert major == 6 and minor == 0 and 0 < count < 10000
        read_string(stream)
        deps_offset, deps_size, config_offset, config_size, flags = struct.unpack(
            "<qqqqQ", stream.read(40)
        )
        assert (
            deps_offset > 0 and deps_size > 0 and config_offset > 0 and config_size > 0
        )
        assert flags & 1  # Disk resources required by WinUI remain extracted.
        entries = {}
        for _ in range(count):
            offset, size, compressed, kind = struct.unpack("<qqqB", stream.read(25))
            name = read_string(stream)
            assert name not in entries and 0 <= offset < path.stat().st_size
            entries[name] = (offset, size, compressed, kind)
        return entries


def bundled_bytes(path, record):
    offset, size, compressed, _ = record
    with path.open("rb") as stream:
        stream.seek(offset)
        data = stream.read(compressed or size)
    if compressed:
        data = zlib.decompress(data, -15)
    assert len(data) == size
    return data


def check_jit_bundle(path, architecture, version):
    entries = bundle_entries(path)
    for name in ("LumaRecode.dll", "LumaRecode.Core.dll"):
        assert not re.search(
            rb"[A-Za-z]:[\\/]+Users[\\/]", bundled_bytes(path, entries[name])
        ), f"Developer home path in {name}"
    assert {
        "LumaRecode.dll",
        "LumaRecode.Core.dll",
        "System.Private.CoreLib.dll",
        "LumaRecode.runtimeconfig.json",
        "uhdr.dll",
        "LumaRecode.Native.dll",
        "LumaRecodeCore.dll",
        "licenses/manifest.json",
    } <= entries.keys()
    assert not any(name.startswith("Application/") for name in entries)
    assert not any(name.startswith("Native/") for name in entries)
    expected_machine = 0xAA64 if architecture == "arm64" else 0x8664
    count = 2
    for name, record in entries.items():
        if record[3] == 2 and name.endswith((".dll", ".exe")):
            assert pe_machine(bundled_bytes(path, record)) == expected_machine, name
            count += 1
    manifest = json.loads(bundled_bytes(path, entries["licenses/manifest.json"]))
    assert (
        manifest["architecture"] == architecture and manifest["app_version"] == version
    )
    assert (
        hashlib.sha256(bundled_bytes(path, entries["uhdr.dll"])).hexdigest()
        == manifest["native_codecs"]["binary"]["sha256"]
    )
    assert manifest["native_codecs"]["binary"]["file"] == "uhdr.dll"
    assert manifest["backend"] == "shared-native"
    assert hashlib.sha256(bundled_bytes(path, entries["LumaRecodeCore.dll"])).hexdigest() == manifest["shared_core"]["sha256"]
    for name in manifest["shared_core"]["notices"]:
        assert "licenses/" + name in entries, name
    assert any(name.startswith("Assets/Fonts/") for name in entries)
    assert any(
        name.startswith("Assets/Media/") and name.endswith(".cube") for name in entries
    )
    return count + 5


def check(artifacts, aot_artifacts):
    host_architecture, count = check_selection()
    mappings = [
        line.strip().split(" = ")[0]
        for line in (ROOT / "LumaRecode.sln").read_text().splitlines()
        if ".ActiveCfg = " in line or ".Build.0 = " in line
    ]
    assert len(mappings) == len(set(mappings)), (
        "Duplicate solution configuration mappings"
    )
    solution = ET.parse(ROOT / "LumaRecode.slnx").getroot()
    assert len(solution.findall("Configurations/BuildType[@Name='Release-AOT']")) == 1
    count += 2
    targets = {
        "Native": (
            host_architecture.lower(),
            "ARM64" if host_architecture == "Arm64" else "x64",
            "native",
            "SelfContained",
        ),
        **TARGETS,
    }
    version = ET.parse(ROOT / "App/VideoConverter.WinUI.csproj").findtext(
        "PropertyGroup/Version"
    )
    manifests = json.loads(
        (ROOT / "App/Native/manifest.json").read_text(encoding="utf-8-sig")
    )
    for architecture, record in manifests["binaries"].items():
        binary = ROOT / "App/Native" / record["file"]
        assert (
            digest(binary) == record["sha256"]
            and binary.stat().st_size == record["bytes"]
        )
        assert machine(binary) == (0xAA64 if architecture == "arm64" else 0x8664)
        count += 2
    for mode in ("Jit", "Aot"):
        for target, (architecture, platform, isa, output) in targets.items():
            if mode == "Jit" and target == "Apx":
                continue
            data = evaluate(
                "App/VideoConverter.WinUI.csproj",
                {
                    "LumaBuildTarget": target,
                    "Platform": platform,
                    "LumaCompilation": mode,
                    "PublishAot": str(mode == "Aot").lower(),
                },
                [
                    "RuntimeIdentifier",
                    "PlatformTarget",
                    "BaseIntermediateOutputPath",
                    "NuGetLockFilePath",
                    "PublishTrimmed",
                ],
            )
            assert data["RuntimeIdentifier"] == "win-" + architecture
            assert data["PlatformTarget"].lower() == architecture
            assert (
                data["BaseIntermediateOutputPath"].replace("\\", "/")
                == f"obj/{mode}/{target}/"
            )
            expected_lock = (
                "packages"
                + (".arm64" if architecture == "arm64" else "")
                + (".aot" if mode == "Aot" else "")
                + ".lock.json"
            )
            assert (
                Path(data["NuGetLockFilePath"] or "packages.lock.json").name
                == expected_lock
            )
            assert data["PublishTrimmed"] == str(mode == "Aot").lower()
            count += 5
            if mode == "Jit" and artifacts:
                executable = ROOT / "dist" / output / "LumaRecode.exe"
                assert machine(executable) == (
                    0xAA64 if architecture == "arm64" else 0x8664
                )
                count += check_jit_bundle(executable, architecture, version) + 1
            if mode == "Aot" and aot_artifacts:
                payload = ROOT / "dist/NativeAot" / target
                expected_machine = 0xAA64 if architecture == "arm64" else 0x8664
                assert machine(payload / "LumaRecode.exe") == expected_machine
                assert (
                    machine(ROOT / "dist" / ("Aot-" + output) / "LumaRecode.exe")
                    == expected_machine
                )
                license_data = json.loads(
                    (payload / "licenses/manifest.json").read_text(encoding="utf-8-sig")
                )
                assert (
                    license_data["architecture"] == architecture
                    and license_data["app_version"] == version
                )
                assert (
                    digest(payload / "uhdr.dll")
                    == license_data["native_codecs"]["binary"]["sha256"]
                )
                response = next(
                    (ROOT / "App/obj/Aot" / target).rglob("LumaRecode.ilc.rsp")
                )
                assert (
                    f"--instruction-set:{isa}"
                    in response.read_text(encoding="utf-8-sig").splitlines()
                )
                count += 5
    for config, platform, target, mode in [
        ("Release", "Native", "Native", "Jit"),
        ("Release", "x64", "X64", "Jit"),
        ("Release", "ARM64", "Arm64", "Jit"),
        ("Release-AOT", "Native", "Native", "Aot"),
        ("Release-AOT", "x64", "X64", "Aot"),
        ("Release-AOT", "ARM64", "Arm64", "Aot"),
        ("Release-APX", "x64", "Apx", "Aot"),
    ]:
        data = evaluate(
            "Packaging/LumaRecode.Packaging.csproj",
            {"Configuration": config, "Platform": platform},
            ["LumaRecodeTarget", "LumaCompilation", "DisableFastUpToDateCheck"],
        )
        assert data["LumaRecodeTarget"] == target and data["LumaCompilation"] == mode
        assert data["DisableFastUpToDateCheck"] == "true"
        count += 1
    for requested, resolved, has_guard in [
        ("Native", "Apx", True),
        ("Native", "X64", False),
        ("Apx", "Apx", True),
    ]:
        data = evaluate(
            "Bundle/LumaRecode.Bundle.csproj",
            {"LumaBuildTarget": requested, "LumaResolvedTarget": resolved},
            ["DefineConstants", "RuntimeIdentifier"],
        )
        assert ("LUMARECODE_APX" in data["DefineConstants"].split(";")) == has_guard
        count += 1
    if artifacts:
        result = subprocess.run(
            [str(ROOT / "dist/SelfContained/LumaRecode.exe"), "--version"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=True,
            timeout=30,
        )
        assert result.stdout.strip() == "LumaRecode " + version
        count += 1
    if aot_artifacts:
        result = subprocess.run(
            [
                str(ROOT / "dist/Aot-SelfContained-Apx/LumaRecode.exe"),
                "--version",
                "--json",
            ],
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=30,
        )
        if ctypes.windll.kernel32.IsProcessorFeaturePresent(90):
            assert result.returncode == 0
        else:
            error = json.loads(result.stdout)
            assert (
                result.returncode == 3
                and error["data"]["code"] == "runtime_unavailable"
            )
            assert "APX" in error["data"]["message"]
        count += 1
    print(f"Build target and provenance checks passed: {count}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--artifacts",
        action="store_true",
        help="Inspect Native, X64 and ARM64 JIT single EXEs",
    )
    parser.add_argument(
        "--aot-artifacts",
        action="store_true",
        help="Inspect all four optional AOT targets",
    )
    args = parser.parse_args()
    check(args.artifacts, args.aot_artifacts)
