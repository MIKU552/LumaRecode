"""Verify the framework-dependent apphost before managed code can run."""

import argparse
import os
import subprocess
import tempfile
from pathlib import Path
from urllib.parse import parse_qs, urlsplit


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--arch", required=True, choices=("x64", "arm64"))
    parser.add_argument("executables", type=Path, nargs="+")
    args = parser.parse_args()

    with tempfile.TemporaryDirectory(prefix="lumarecode-no-runtime-") as root:
        no_runtime = os.environ | {
            f"DOTNET_ROOT_{args.arch.upper()}": root,
            "DOTNET_DISABLE_GUI_ERRORS": "1",
        }
        for executable in args.executables:
            command = [str(executable.resolve(strict=True)), "--version"]
            result = subprocess.run(
                command, capture_output=True, text=True, env=no_runtime, timeout=30
            )
            assert result.returncode & 0xFFFFFFFF == 0x80008083, result
            links = [
                urlsplit(line)
                for line in result.stderr.splitlines()
                if line.startswith("https://aka.ms/dotnet-core-applaunch?")
            ]
            assert len(links) == 1, result.stderr
            query = parse_qs(links[0].query)
            assert query.get("arch") == [args.arch], links[0].geturl()
            assert query.get("apphost_version", [""])[0].startswith("11."), links[0].geturl()

            result = subprocess.run(command, capture_output=True, text=True, timeout=45)
            assert result.returncode == 0 and "LumaRecode" in result.stdout, result
            print(f"PASS .NET discovery and download link: {executable.name}", flush=True)


if __name__ == "__main__":
    main()
