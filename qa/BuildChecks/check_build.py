"""Check MSBuild target isolation, native codec provenance and published architectures."""

import argparse
import ctypes
import hashlib
import json
import struct
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TARGETS = {
    "X64": ("x64", "x64", "x86-64-v2", "SelfContained-X64"),
    "Apx": ("x64", "x64", "apx", "SelfContained-Apx"),
    "Arm64": ("arm64", "ARM64", "armv8-a", "SelfContained-Arm64"),
}


def check_selection():
    # Exercise the real PowerShell resolver for each host/target combination,
    # including an ARM host that reports an unrelated x86 feature flag.
    script = r"""
    . ./tools/BuildTarget.ps1
    $cases = foreach ($architecture in @('X64','Arm64')) {
        foreach ($apx in @($false,$true)) {
            foreach ($target in @('Native','X64','Apx','Arm64')) {
                [pscustomobject]@{
                    HostArchitecture=$architecture;ApxAvailable=$apx
                    Plan=(Resolve-LumaBuildTarget -Target $target -SystemArchitecture $architecture -ApxAvailable $apx)
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
    native_machine = ctypes.c_ushort()
    process_machine = ctypes.c_ushort()
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
        plan = case["Plan"]
        host = case["HostArchitecture"]
        automatic = (
            "Arm64" if host == "Arm64" else "Apx" if case["ApxAvailable"] else "X64"
        )
        requested = plan["RequestedTarget"]
        expected = automatic if requested == "Native" else requested
        assert plan["ResolvedTarget"] == expected
        assert plan["Architecture"] == ("arm64" if expected == "Arm64" else "x64")
        isa = "native" if requested == "Native" else TARGETS[expected][2]
        assert plan["InstructionSet"] == isa
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


def machine(path):
    with path.open("rb") as stream:
        assert stream.read(2) == b"MZ", path
        stream.seek(0x3C)
        offset = struct.unpack("<I", stream.read(4))[0]
        stream.seek(offset)
        assert stream.read(4) == b"PE\x00\x00", path
        return struct.unpack("<H", stream.read(2))[0]


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def check(artifacts):
    host_architecture, count = check_selection()
    targets = {
        "Native": (
            host_architecture.lower(),
            "ARM64" if host_architecture == "Arm64" else "x64",
            "native",
            "SelfContained",
        ),
        **TARGETS,
    }
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
    for target, (architecture, platform, isa, output) in targets.items():
        props = {"LumaBuildTarget": target, "Platform": platform, "PublishAot": "true"}
        data = evaluate(
            "App/VideoConverter.WinUI.csproj",
            props,
            [
                "RuntimeIdentifier",
                "PlatformTarget",
                "BaseIntermediateOutputPath",
                "NuGetLockFilePath",
            ],
        )
        assert data["RuntimeIdentifier"] == "win-" + architecture
        assert data["PlatformTarget"].lower() == architecture
        assert data["BaseIntermediateOutputPath"].replace("\\", "/") == f"obj/{target}/"
        expected = (
            "packages.arm64.aot.lock.json"
            if architecture == "arm64"
            else "packages.aot.lock.json"
        )
        assert Path(data["NuGetLockFilePath"]).name == expected
        count += 4
        if not artifacts:
            continue
        payload = ROOT / "dist/NativeAot" / target
        expected_machine = 0xAA64 if architecture == "arm64" else 0x8664
        assert machine(payload / "LumaRecode.exe") == expected_machine
        assert machine(ROOT / "dist" / output / "LumaRecode.exe") == expected_machine
        for binary in payload.glob("*.dll"):
            assert machine(binary) == expected_machine, binary
            count += 1
        license_data = json.loads(
            (payload / "licenses/manifest.json").read_text(encoding="utf-8-sig")
        )
        assert license_data["architecture"] == architecture
        assert (
            digest(payload / "uhdr.dll")
            == license_data["native_codecs"]["binary"]["sha256"]
        )
        assert license_data["native_codecs"]["binary"]["file"] == "uhdr.dll"
        response = next((ROOT / "App/obj" / target).rglob("LumaRecode.ilc.rsp"))
        assert (
            f"--instruction-set:{isa}"
            in response.read_text(encoding="utf-8-sig").splitlines()
        )
        count += 6
    for config, platform, target in [
        ("Release", "Native", "Native"),
        ("Release", "x64", "X64"),
        ("Release-APX", "x64", "Apx"),
        ("Release", "ARM64", "Arm64"),
    ]:
        data = evaluate(
            "Packaging/LumaRecode.Packaging.csproj",
            {"Configuration": config, "Platform": platform},
            ["LumaRecodeTarget", "DisableFastUpToDateCheck"],
        )
        assert (
            data["LumaRecodeTarget"] == target
            and data["DisableFastUpToDateCheck"] == "true"
        )
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
        version = ET.parse(ROOT / "App/VideoConverter.WinUI.csproj").findtext(
            "PropertyGroup/Version"
        )
        native = ROOT / "dist/SelfContained/LumaRecode.exe"
        result = subprocess.run(
            [str(native), "--version"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=True,
            timeout=30,
        )
        assert result.stdout.strip() == "LumaRecode " + version
        apx = ROOT / "dist/SelfContained-Apx/LumaRecode.exe"
        result = subprocess.run(
            [str(apx), "--version", "--json"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
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
        count += 2
    print(f"Build target and provenance checks passed: {count}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--artifacts",
        action="store_true",
        help="Also inspect Native and all three explicit targets",
    )
    check(parser.parse_args().artifacts)
