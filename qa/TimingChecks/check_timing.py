"""Check QSV VFR cadence, depth conversion and bounded timestamp-error recovery."""

import argparse
import copy
import json
import shutil
import subprocess
import sys
import tempfile
from fractions import Fraction
from itertools import pairwise
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "BackendChecks"))
import native_backend as native


def packets(path):
    result = subprocess.run(
        [
            shutil.which("ffprobe"),
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_entries",
            "stream=time_base:packet=pts,dts",
            "-of",
            "json",
            str(path),
        ],
        capture_output=True,
        check=True,
        text=True,
        encoding="utf-8",
        timeout=60,
    )
    data = json.loads(result.stdout)
    base = Fraction(data["streams"][0]["time_base"])
    return sorted(int(p["pts"]) * base for p in data["packets"]), [
        int(p["dts"]) * base for p in data["packets"]
    ]


def check_timing(source, output):
    before, _ = packets(source)
    after, dts = packets(output)
    assert len(after) == len(before), (len(before), len(after))
    # QSV timestamps use 90 kHz internally. Compare the whole presentation
    # timeline, including frame-rate transitions, within one hardware tick.
    assert all(abs(a - b) <= Fraction(1, 90000) for a, b in zip(before, after))
    assert all(b > a for a, b in pairwise(dts))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--sample",
        type=Path,
        help="Optional camera VFR sample, kept outside the repository",
    )
    args = parser.parse_args()
    tools = native.Tools(shutil.which("ffmpeg"), shutil.which("ffprobe"))
    assert tools.ffmpeg and tools.ffprobe
    count = 0
    with tempfile.TemporaryDirectory(prefix="LumaRecode-timing-") as directory:
        folder = Path(directory)
        for depth, kind in [(8, "sdr"), (10, "sdr"), (10, "hlg"), (10, "pq")]:
            hdr = kind != "sdr"
            source = folder / f"vfr-{depth}-{kind}.mp4"
            color = (
                ("bt2020", "smpte2084" if kind == "pq" else "arib-std-b67", "bt2020nc")
                if hdr
                else ("bt709", "bt709", "bt709")
            )
            metadata = (
                ":master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50):max-cll=1000,400"
                if kind == "pq"
                else ""
            )
            subprocess.run(
                [
                    tools.ffmpeg,
                    "-v",
                    "error",
                    "-f",
                    "lavfi",
                    "-i",
                    "testsrc2=size=320x180:rate=60:duration=6",
                    "-vf",
                    "settb=1/90000,setpts='if(lt(N,120),N*3003,360360+(N-120)*1499)'",
                    "-fps_mode:v",
                    "passthrough",
                    "-enc_time_base:v",
                    "filter",
                    "-c:v",
                    "libx265",
                    "-preset",
                    "ultrafast",
                    "-x265-params",
                    f"pools=2:log-level=error:colorprim={color[0]}:transfer={color[1]}:colormatrix={color[2]}{metadata}",
                    "-pix_fmt",
                    "yuv420p10le" if depth == 10 else "yuv420p",
                    "-color_range",
                    "tv",
                    "-color_primaries",
                    color[0],
                    "-color_trc",
                    color[1],
                    "-colorspace",
                    color[2],
                    str(source),
                ],
                check=True,
                timeout=60,
            )
            media = native.inspect_media(tools, source)
            for codec in ("hevc", "av1") if hdr else ("hevc", "avc", "av1"):
                for target in (
                    (8, 10)
                    if depth == 10 and not hdr and codec != "avc"
                    else (10 if hdr else 8,)
                ):
                    options = native.Options(
                        codec=codec,
                        output="hdr" if hdr else "sdr",
                        bit_depth=str(target),
                    )
                    output = folder / f"out-{depth}-{kind}-{codec}-{target}.mp4"
                    plan, _ = native.build_command(tools, media, options, output)
                    assert "vpp_qsv=" not in plan[plan.index("-vf") + 1]
                    assert ("-hwaccel" in plan) == (depth == target)
                    assert ("-bf:v" in plan) == (codec in ("avc", "hevc"))
                    result = native.Job().run(tools, media, options, output)
                    assert result.video["pix_fmt"] == (
                        "yuv420p10le" if target == 10 else "yuv420p"
                    )
                    assert result.kind == kind
                    check_timing(source, output)
                    count += 1
            if depth == 8:
                output = folder / "software-decode.mp4"
                native.Job().run(tools, media, native.Options(cpu_decode=True), output)
                check_timing(source, output)
                count += 1
            # A CFR header must retain the normal, previously tuned QSV plan.
            cfr = copy.deepcopy(media)
            cfr.video["avg_frame_rate"] = cfr.video["r_frame_rate"] = "60/1"
            plan, _ = native.build_command(
                tools,
                cfr,
                native.Options(output="hdr" if hdr else "sdr"),
                folder / "cfr.mp4",
            )
            assert "-bf:v" not in plan and "vpp_qsv=" in plan[plan.index("-vf") + 1]
            # Software archiving never needs the QSV reordering workaround.
            plan, _ = native.build_command(
                tools,
                media,
                native.Options(purpose="archive", output="hdr" if hdr else "sdr"),
                folder / "archive.mkv",
            )
            assert "-bf:v" not in plan and "-hwaccel" not in plan
        if args.sample:
            media = native.inspect_media(tools, args.sample.resolve())
            output = folder / "camera.mp4"
            native.Job().run(tools, media, native.Options(), output)
            check_timing(args.sample, output)
            # Simulate incomplete/misleading CFR headers without changing the
            # file: the first attempt must fail and recover exactly once.
            hidden_vfr = copy.deepcopy(media)
            hidden_vfr.video["avg_frame_rate"] = hidden_vfr.video["r_frame_rate"]
            output = folder / "camera-retry.mp4"
            result = native.request(
                "run-events", media=hidden_vfr, options=native.Options(), output=output
            )
            commands = [
                e
                for e in result["events"]
                if e["event"] == "log" and "-progress" in str(e["data"])
            ]
            assert len(commands) == 2, commands
            assert any(
                "QSV" in str(e["data"]) and e["event"] == "status"
                for e in result["events"]
            )
            check_timing(args.sample, output)
            # An ordinary FFmpeg failure does not trigger a timestamp retry.
            broken = copy.deepcopy(media)
            broken.path = folder / "missing.mp4"
            try:
                native.Job().run(tools, broken, native.Options(), folder / "failed.mp4")
                raise AssertionError("Missing source should fail")
            except ValueError:
                pass
            assert not (folder / "failed.mp4").exists()
            assert not list(folder.glob(".video-converter-*"))
            count += 2
    print(
        f"PASS {count} QSV encodes: exact frame counts/cadence, strictly increasing DTS, SDR/HLG/PQ and 8/10-bit."
    )


if __name__ == "__main__":
    main()
