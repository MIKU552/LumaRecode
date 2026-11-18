"""Exercise the Swift host through the packaged app's command-line entry."""

import argparse
import hashlib
import json
import os
import shutil
import signal
import subprocess
import tempfile
import time
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--app", type=Path, required=True)
    args = parser.parse_args()
    exe = args.app.resolve() / "Contents/MacOS/LumaRecode"
    ffmpeg = os.environ.get("LUMA_TEST_FFMPEG") or shutil.which("ffmpeg")
    ffprobe = os.environ.get("LUMA_TEST_FFPROBE") or shutil.which("ffprobe")
    assert exe.is_file() and ffmpeg and ffprobe

    def call(*words, status=0):
        result = subprocess.run(
            [str(exe), *map(str, words), "--ffprobe", ffprobe],
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=90,
        )
        assert result.returncode == status, (
            result.returncode,
            result.stdout,
            result.stderr,
        )
        return result.stdout

    with tempfile.TemporaryDirectory(prefix="lumarecode-macos-") as temporary:
        folder = Path(temporary)
        for kind, transfer, pixel in [
            ("sdr", "bt709", "yuv420p"),
            ("hlg", "arib-std-b67", "yuv420p10le"),
            ("pq", "smpte2084", "yuv420p10le"),
        ]:
            source = folder / f"原片 {kind}.mkv"
            primaries = "bt709" if kind == "sdr" else "bt2020"
            matrix = "bt709" if kind == "sdr" else "bt2020nc"
            subprocess.run(
                [
                    ffmpeg,
                    "-v",
                    "error",
                    "-f",
                    "lavfi",
                    "-i",
                    "testsrc2=size=128x72:rate=24",
                    "-frames:v",
                    "16",
                    "-c:v",
                    "libx265",
                    "-preset",
                    "ultrafast",
                    "-pix_fmt",
                    pixel,
                    "-x265-params",
                    f"log-level=error:colorprim={primaries}:transfer={transfer}:colormatrix={matrix}",
                    str(source),
                ],
                check=True,
                capture_output=True,
                timeout=30,
            )
            source_hash = hashlib.sha256(source.read_bytes()).digest()
            summary = json.loads(call("probe", source))
            assert summary["kind"] == kind and summary["video"]["pix_fmt"] == pixel, (
                summary
            )
            assert json.loads(call("frames", source))["frame_count"] == 16
            ext = "mp4" if kind == "sdr" else "mkv"
            output = folder / f"変換 {kind}.{ext}"
            convert = (
                "convert",
                source,
                "-o",
                output,
                "--ffmpeg",
                ffmpeg,
                "--start-frame",
                "2",
                "--end-frame",
                "6",
            )
            call(*convert)
            result = json.loads(call("probe", output))
            assert (
                result["kind"] == kind and result["video"]["color_transfer"] == transfer
            ), result
            assert json.loads(call("frames", output))["frame_count"] == 5
            call(*convert, status=1)
            call(*convert, "--overwrite")
            assert hashlib.sha256(source.read_bytes()).digest() == source_hash
            assert not list(folder.glob(".lumarecode-*"))
            print(
                f"PASS macOS host: {kind}, trim, tagged output, and overwrite",
                flush=True,
            )

        old_hash = hashlib.sha256(output.read_bytes()).digest()
        marker = folder / "running"
        fake = folder / "ffmpeg-block"
        fake.write_text(
            '#!/bin/sh\n: > "$LUMA_SMOKE_MARKER"\nsleep 120\n', encoding="utf-8"
        )
        fake.chmod(0o755)
        with subprocess.Popen(
            [
                str(exe),
                "convert",
                str(source),
                "-o",
                str(output),
                "--ffmpeg",
                str(fake),
                "--ffprobe",
                ffprobe,
                "--overwrite",
            ],
            env={**os.environ, "LUMA_SMOKE_MARKER": str(marker)},
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        ) as process:
            try:
                deadline = time.monotonic() + 30
                while (
                    not marker.exists()
                    and process.poll() is None
                    and time.monotonic() < deadline
                ):
                    time.sleep(0.1)
                assert marker.exists(), process.poll()
                process.send_signal(signal.SIGINT)
                stdout, stderr = process.communicate(timeout=15)
                assert process.returncode == 130, (process.returncode, stdout, stderr)
            finally:
                if process.poll() is None:
                    process.kill()
        assert hashlib.sha256(output.read_bytes()).digest() == old_hash
        assert not list(folder.glob(".lumarecode-*"))
        print("PASS macOS host: interrupt, cleanup, and retained output", flush=True)


if __name__ == "__main__":
    main()
