"""Exercise the packaged Windows host without a GPU, settings or private footage."""

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import tempfile
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", type=Path, required=True)
    args = parser.parse_args()
    exe = args.exe.resolve()
    ffmpeg = os.environ.get("LUMA_TEST_FFMPEG") or shutil.which("ffmpeg")
    ffprobe = os.environ.get("LUMA_TEST_FFPROBE") or shutil.which("ffprobe")
    assert exe.is_file(), exe
    assert ffmpeg and ffprobe, (
        "Install FFmpeg/ffprobe or set LUMA_TEST_FFMPEG/LUMA_TEST_FFPROBE."
    )

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
            timeout=90,
        )
        assert result.returncode == status, (
            result.returncode,
            result.stdout,
            result.stderr,
        )
        assert not result.stderr, result.stderr
        return result.stdout

    with tempfile.TemporaryDirectory(prefix="lumarecode-host-") as temporary:
        folder = Path(temporary)
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
