"""Exercise the packaged Windows host without a GPU, settings or private footage."""

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import time
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--portable", action="store_true")
    args = parser.parse_args()
    exe = args.exe.resolve()
    ffmpeg = os.environ.get("LUMA_TEST_FFMPEG") or shutil.which("ffmpeg")
    ffprobe = os.environ.get("LUMA_TEST_FFPROBE") or shutil.which("ffprobe")
    assert exe.is_file(), exe
    assert ffmpeg and ffprobe, (
        "Install FFmpeg/ffprobe or set LUMA_TEST_FFMPEG/LUMA_TEST_FFPROBE."
    )
    if args.portable:
        with tempfile.TemporaryDirectory(prefix="lumarecode-no-extraction-") as temporary:
            os.environ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = temporary
            run_suite(exe, ffmpeg, ffprobe, portable=True)
            assert not list(Path(temporary).iterdir()), "Portable Windows app extracted a .NET bundle"
        return
    run_suite(exe, ffmpeg, ffprobe, portable=False)


def run_suite(exe, ffmpeg, ffprobe, portable):
    def call(*words, status=0):
        result = subprocess.run(
            [
                str(exe),
                *map(str, words),
                "--no-settings",
                "--accept-terms",
                "--json",
                "--ffmpeg",
                ffmpeg,
                "--ffprobe",
                ffprobe,
            ],
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=120,
        )
        assert result.returncode == status, (
            result.returncode,
            result.stdout,
            result.stderr,
        )
        assert not result.stderr, result.stderr
        return result.stdout

    hardware = json.loads(call("doctor"))["hardware_encoders"]
    assert {row["backend"] for row in hardware} == {"qsv", "nvenc", "amf", "mf"}
    assert all(
        row["status"] in {"available", "not_listed", "trial_failed", "timed_out"}
        for row in hardware
    )
    print("PASS packaged hardware detection", flush=True)

    with tempfile.TemporaryDirectory(prefix="lumarecode-host-") as temporary:
        folder = Path(temporary)
        launch = exe
        if not portable:
            launch = folder / "LumaRecode (downloaded).exe"
            shutil.copyfile(exe, launch)
        with subprocess.Popen([str(launch)]) as gui:
            try:
                time.sleep(6)
                assert gui.poll() is None, f"Windows GUI exited with {gui.returncode}"
            finally:
                if gui.poll() is None:
                    gui.terminate()
        print("PASS portable Windows GUI launch" if portable else "PASS renamed Windows GUI launch", flush=True)
        for kind, transfer, depth in [
            ("sdr", "bt709", 8),
            ("hlg", "arib-std-b67", 10),
            ("pq", "smpte2084", 10),
        ]:
            source = folder / f"素材 {kind}.mkv"
            primaries = "bt709" if kind == "sdr" else "bt2020"
            matrix = "bt709" if kind == "sdr" else "bt2020nc"
            subprocess.run(
                [
                    ffmpeg,
                    "-hide_banner",
                    "-v",
                    "error",
                    "-nostdin",
                    "-f",
                    "lavfi",
                    "-i",
                    "testsrc2=size=128x72:rate=24:duration=0.5",
                    "-c:v",
                    "libx265",
                    "-preset",
                    "ultrafast",
                    "-pix_fmt",
                    "yuv420p" if depth == 8 else "yuv420p10le",
                    "-x265-params",
                    f"log-level=error:colorprim={primaries}:transfer={transfer}:"
                    f"colormatrix={matrix}",
                    source,
                ],
                check=True,
                capture_output=True,
                timeout=30,
            )
            original = hashlib.sha256(source.read_bytes()).hexdigest()
            media = json.loads(call("probe", source))
            assert media["kind"] == kind and media["video"]["codec_name"] == "hevc"
            if kind == "sdr":
                plan = json.loads(call("plan", source).splitlines()[0])["data"][
                    "options"
                ]
                available = [
                    row
                    for row in hardware
                    if row["status"] == "available" and row["bit_depth"] == 8
                ]
                if available:
                    assert plan["purpose"] == "share" and any(
                        row["backend"] == plan["hardware"]
                        and row["codec"] == plan["codec"]
                        and row["bit_depth"] == int(plan["bit_depth"])
                        for row in available
                    ), plan
                else:
                    assert plan["purpose"] == "archive", plan
            output = folder / f"{kind}-output.mkv"
            events = [
                json.loads(line)
                for line in call(
                    "convert", source, "--preset", "archive-hevc", "-o", output
                ).splitlines()
            ]
            assert output.is_file() and events[-1]["data"] == {
                "completed": 1,
                "skipped": 0,
                "failed": 0,
            }
            assert any(event["event"] == "progress" for event in events)
            result = json.loads(call("probe", output))
            assert result["kind"] == kind
            assert result["video"]["pix_fmt"] == media["video"]["pix_fmt"]
            call("convert", source, "--preset", "archive-hevc", "-o", output, status=5)
            call(
                "convert",
                source,
                "--preset",
                "archive-hevc",
                "-o",
                output,
                "--overwrite",
            )
            assert hashlib.sha256(source.read_bytes()).hexdigest() == original
            assert not list(folder.glob(".lumarecode-*"))
            print(
                f"PASS packaged shared host: {kind}, software encode, progress, "
                "verification and overwrite",
                flush=True,
            )


if __name__ == "__main__":
    main()
