"""Exercise the Swift host through the packaged app's command-line entry."""

import argparse
import hashlib
import json
import os
import shlex
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
        trials = json.loads(call("doctor", "--ffmpeg", ffmpeg))
        assert {
            (item["format"]["codec"], item["format"]["signal"]) for item in trials
        } == {
            ("avc", "sdr"),
            ("hevc", "sdr"),
            ("hevc", "hlg"),
            ("hevc", "pq"),
            ("hevc", "dolbyvision84"),
        }, trials
        blocked = folder / "ffmpeg-no-hardware"
        blocked.write_text(
            '#!/bin/sh\nfor part in "$@"; do\n'
            '  case "$part" in *_videotoolbox) echo "VideoToolbox unavailable in fixture" >&2; exit 1 ;; esac\n'
            f'done\nexec {shlex.quote(ffmpeg)} "$@"\n',
            encoding="utf-8",
        )
        blocked.chmod(0o755)
        rejected = json.loads(call("doctor", "--ffmpeg", blocked))
        assert len(rejected) == len(trials) and all(
            not item["available"] for item in rejected
        ), rejected
        assert all(
            "unavailable in fixture" in item["diagnostic"] for item in rejected
        ), rejected
        hlg_trial = next(item for item in trials if item["format"]["signal"] == "hlg")
        dovi_trial = next(
            item for item in trials if item["format"]["signal"] == "dolbyvision84"
        )
        assert (
            hlg_trial.get("diagnostic")
            != "Standard output retained dynamic HDR metadata."
        ), hlg_trial
        print(
            f"VideoToolbox engine trials: {json.dumps(trials, ensure_ascii=False)}",
            flush=True,
        )
        if os.environ.get("LUMA_REQUIRE_VIDEO_TOOLBOX_DOVI"):
            assert dovi_trial["available"], dovi_trial
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
                    "testsrc2=size=320x192:rate=24",
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
            for item in trials:
                if item["format"]["signal"] != kind or not item["available"]:
                    continue
                codec = item["format"]["codec"]
                hardware = folder / f"videotoolbox-{codec}-{kind}.mp4"
                call(
                    "convert",
                    source,
                    "-o",
                    hardware,
                    "--ffmpeg",
                    ffmpeg,
                    "--encoder",
                    "videotoolbox",
                    "--codec",
                    codec,
                    "--start-frame",
                    "2",
                    "--end-frame",
                    "6",
                )
                inspected = json.loads(call("probe", hardware))
                encoded = inspected["video"]
                assert (
                    encoded["pix_fmt"] == pixel
                    and encoded["color_transfer"] == transfer
                ), encoded
                assert (
                    inspected["dolby_vision"] is None and not inspected["hdr10plus"]
                ), inspected
                assert json.loads(call("frames", hardware))["frame_count"] == 5
                print(f"PASS real VideoToolbox: {codec} {kind}", flush=True)

            dolby = folder / f"dolby-vision-{kind}.mp4"
            dolby_export = (
                "convert",
                source,
                "-o",
                dolby,
                "--ffmpeg",
                ffmpeg,
                "--range",
                "dolbyvision84",
                "--start-frame",
                "2",
                "--end-frame",
                "6",
            )
            if kind == "hlg" and dovi_trial["available"]:
                call(*dolby_export)
                inspected = json.loads(call("probe", dolby))
                config = inspected["dolby_vision"]
                assert (
                    config["dv_profile"] == 8
                    and config["dv_bl_signal_compatibility_id"] == 4
                ), config
                assert (
                    config["rpu_present_flag"] == 1 and config["el_present_flag"] == 0
                ), config
                assert (
                    inspected["video"]["color_transfer"] == transfer
                    and not inspected["hdr10plus"]
                ), inspected
                assert json.loads(call("frames", dolby))["frame_count"] == 5
                print("PASS real VideoToolbox: HLG to Dolby Vision 8.4", flush=True)
            else:
                call(*dolby_export, status=1)
                assert not dolby.exists()
            assert not list(folder.glob(".lumarecode-*"))

            formats = ["sdr"] if kind == "sdr" else ["sdr", "hdr", "adaptive"]
            for photo_format in formats:
                ext = "jpg" if photo_format == "sdr" else "heic"
                photo = folder / f"{kind}-{photo_format}.{ext}"
                call(
                    "image",
                    source,
                    "-o",
                    photo,
                    "--frame",
                    "3",
                    "--format",
                    photo_format,
                    "--ffmpeg",
                    ffmpeg,
                )
                magic = photo.read_bytes()[:12]
                assert (
                    magic.startswith(b"\xff\xd8\xff")
                    if ext == "jpg"
                    else magic[4:8] == b"ftyp"
                ), magic
            photo = folder / f"{kind}-first.jpg"
            call("image", source, "-o", photo, "--frame", "1", "--ffmpeg", ffmpeg)
            assert photo.read_bytes() != (folder / f"{kind}-sdr.jpg").read_bytes()
            outside = folder / "outside.jpg"
            call(
                "image",
                source,
                "-o",
                outside,
                "--frame",
                "17",
                "--ffmpeg",
                ffmpeg,
                status=1,
            )
            assert not outside.exists() and not list(folder.glob(".lumarecode-*"))
            print(
                f"PASS macOS still exports: {kind} ({', '.join(formats)})", flush=True
            )
            if kind != "sdr":
                output_sdr = folder / f"from-{kind}-to-sdr.mp4"
                call(
                    "convert",
                    source,
                    "-o",
                    output_sdr,
                    "--range",
                    "sdr",
                    "--ffmpeg",
                    ffmpeg,
                )
                assert json.loads(call("probe", output_sdr))["kind"] == "sdr"
                print(f"PASS macOS software tone mapping: {kind} to SDR", flush=True)
            if kind != "pq":
                pair = folder / f"live-photo-{kind}"
                fmt, ext = ("sdr", "jpg") if kind == "sdr" else ("adaptive", "heic")
                live = (
                    "image",
                    source,
                    "-o",
                    pair,
                    "--frame",
                    "4",
                    "--format",
                    fmt,
                    "--live-photo",
                    "--motion-first",
                    "2",
                    "--motion-last",
                    "8",
                    "--ffmpeg",
                    ffmpeg,
                )
                call(*live)
                still = pair / f"{pair.name}.{ext}"
                movie = pair / f"{pair.name}.mov"
                assert set(pair.iterdir()) == {still, movie}, list(pair.iterdir())
                assert json.loads(call("frames", movie))["frame_count"] == 7
                tags = subprocess.check_output(
                    [
                        ffprobe,
                        "-v",
                        "error",
                        "-show_entries",
                        "format_tags",
                        "-of",
                        "json",
                        str(movie),
                    ],
                    text=True,
                    encoding="utf-8",
                    timeout=20,
                )
                assert "com.apple.quicktime.content.identifier" in tags
                original = hashlib.sha256(movie.read_bytes()).digest()
                call(*live, status=1)
                assert hashlib.sha256(movie.read_bytes()).digest() == original
                assert not list(folder.glob(".lumarecode-*"))
                print(f"PASS macOS Photos framework: {kind} Live Photo", flush=True)

        automatic = folder / "automatic.mkv"
        call(
            "convert", source, "-o", automatic, "--ffmpeg", blocked, "--encoder", "auto"
        )
        assert json.loads(call("probe", automatic))["kind"] == "pq"
        unavailable = folder / "unavailable.mp4"
        call(
            "convert",
            source,
            "-o",
            unavailable,
            "--ffmpeg",
            blocked,
            "--encoder",
            "videotoolbox",
            status=1,
        )
        assert not unavailable.exists() and not list(folder.glob(".lumarecode-*"))
        print(
            "PASS VideoToolbox unavailability, software fallback, and explicit hardware rejection",
            flush=True,
        )

        mastering = (
            "G(8500,39850)B(6550,2300)R(35400,14600)WP(15635,16450)L(10000000,50)"
        )
        static_input = folder / "static-pq.mkv"
        static_output = folder / "static-pq-archive.mkv"
        subprocess.run(
            [
                ffmpeg,
                "-v",
                "error",
                "-i",
                str(source),
                "-c:v",
                "libx265",
                "-preset",
                "ultrafast",
                "-x265-params",
                f"log-level=error:hdr10=1:master-display={mastering}:max-cll=1000,400",
                str(static_input),
            ],
            check=True,
            capture_output=True,
            timeout=30,
        )
        call(
            "convert",
            static_input,
            "-o",
            static_output,
            "--ffmpeg",
            ffmpeg,
            "--encoder",
            "auto",
        )
        assert "Mastering display metadata" in call("probe", static_output)
        assert "Content light level metadata" in call("probe", static_output)
        call(
            "convert",
            static_input,
            "-o",
            unavailable,
            "--ffmpeg",
            ffmpeg,
            "--encoder",
            "videotoolbox",
            status=1,
        )
        assert not unavailable.exists()
        print("PASS static PQ metadata through the software archive path", flush=True)

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
