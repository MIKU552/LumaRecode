"""Verify native CLI execution of camera transforms and HDR-to-SDR conversion."""

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
    ffmpeg = shutil.which("ffmpeg")
    assert ffmpeg

    def call(*words):
        p = subprocess.run(
            [str(exe), *map(str, words), "--no-settings", "--accept-terms", "--json"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=90,
        )
        assert p.returncode == 0 and not p.stderr, (p.returncode, p.stdout, p.stderr)
        return [json.loads(line) for line in p.stdout.splitlines()]

    def probe(path):
        p = subprocess.run(
            [str(exe), "probe", str(path), "--no-settings", "--accept-terms", "--json"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=30,
        )
        assert p.returncode == 0, p.stderr
        return json.loads(p.stdout)

    with tempfile.TemporaryDirectory(prefix="LumaRecode-native-") as temporary:
        folder = Path(temporary)
        source = folder / "原片.mp4"
        subprocess.run(
            [
                ffmpeg,
                "-v",
                "error",
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=320x180:rate=24:duration=1",
                "-f",
                "lavfi",
                "-i",
                "sine=frequency=440:sample_rate=48000:duration=1",
                "-c:v",
                "libx265",
                "-preset",
                "ultrafast",
                "-pix_fmt",
                "yuv420p10le",
                "-x265-params",
                "log-level=error:colorprim=bt709:transfer=bt709:colormatrix=bt709",
                "-color_primaries",
                "bt709",
                "-color_trc",
                "bt709",
                "-colorspace",
                "bt709",
                "-c:a",
                "aac",
                "-shortest",
                str(source),
            ],
            check=True,
        )
        for profile in ["pocket3", "samsung", "milog", "applelog", "applelog2"]:
            hlg = folder / (profile + "-hlg.mp4")
            events = call(
                "convert",
                source,
                "--input-kind",
                "log",
                "--profile",
                profile,
                "-o",
                hlg,
            )
            assert any(e["event"] == "progress" for e in events)
            v = probe(hlg)
            assert (
                v["kind"] == "hlg"
                and v["video"]["pix_fmt"] == "yuv420p10le"
                and v["audio"]
            )
            sdr = folder / (profile + "-sdr.mp4")
            call(
                "convert",
                source,
                "--input-kind",
                "log",
                "--profile",
                profile,
                "--range",
                "sdr",
                "-o",
                sdr,
            )
            v = probe(sdr)
            assert v["kind"] == "sdr" and v["video"]["pix_fmt"] == "yuv420p"
            print(
                "PASS native CLI",
                profile,
                "to HLG/SDR with audio and progress",
                flush=True,
            )
        sdr = folder / "tone-mapped.mp4"
        call("convert", hlg, "--range", "sdr", "-o", sdr)
        assert probe(sdr)["video"]["color_transfer"] == "bt709"
        print("PASS native CLI HLG to SDR with Vulkan/libplacebo", flush=True)
        pq = folder / "pq.mp4"
        metadata = "master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50):max-cll=1000,400"
        subprocess.run(
            [
                ffmpeg,
                "-v",
                "error",
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=320x180:rate=24:duration=1",
                "-c:v",
                "libx265",
                "-preset",
                "ultrafast",
                "-pix_fmt",
                "yuv420p10le",
                "-x265-params",
                "log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:"
                + metadata,
                "-color_primaries",
                "bt2020",
                "-color_trc",
                "smpte2084",
                "-colorspace",
                "bt2020nc",
                str(pq),
            ],
            check=True,
        )
        for preset in ["archive-hevc", "archive-av1-fast"]:
            output = folder / (preset + "-pq.mkv")
            call("convert", pq, "--preset", preset, "-o", output)
            result = probe(output)
            side = result["video"].get("side_data_list", []) + [
                item
                for frame in result["first_frames"]
                for item in frame.get("side_data_list", [])
            ]
            assert {"Mastering display metadata", "Content light level metadata"} <= {
                item["side_data_type"] for item in side
            }
            print("PASS PQ static HDR metadata preservation:", preset, flush=True)
        original = hashlib.sha256(source.read_bytes()).hexdigest()
        alias = folder / "alias.mp4"
        os.link(source, alias)
        for words in [
            ("convert", source, "-o", alias, "--overwrite"),
            ("convert", source, "--log", alias),
        ]:
            p = subprocess.run(
                [
                    str(exe),
                    *map(str, words),
                    "--no-settings",
                    "--accept-terms",
                    "--json",
                ],
                capture_output=True,
                text=True,
                encoding="utf-8",
                timeout=30,
            )
            assert p.returncode == 5 and "output_conflict" in p.stdout, p.stdout
            assert hashlib.sha256(source.read_bytes()).hexdigest() == original
        print("PASS hard-linked output/log protection and source hashes", flush=True)


if __name__ == "__main__":
    main()
