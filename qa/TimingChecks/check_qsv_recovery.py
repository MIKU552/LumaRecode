"""Reproduce AV1 HDR decode failures and test bounded QSV recovery with real encodes."""

import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from check_timing import check_timing

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "BackendChecks"))
import native_backend as native


def reset_server():
    native.close()
    native._server = None


def make_source(tools, path, hdr=False, high_fps=False):
    color = ("bt2020", "smpte2084", "bt2020nc") if hdr else ("bt709", "bt709", "bt709")
    parameters = f"pools=2:log-level=error:colorprim={color[0]}:transfer={color[1]}:colormatrix={color[2]}"
    if hdr:
        parameters += ":master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50):max-cll=1000,400"
    subprocess.run(
        [
            tools.ffmpeg,
            "-v",
            "error",
            "-f",
            "lavfi",
            "-i",
            "testsrc2=size=320x180:rate=240:duration=1.25"
            if high_fps
            else "testsrc2=size=320x180:rate=30:duration=2",
            "-c:v",
            "libx265",
            "-preset",
            "ultrafast",
            "-x265-params",
            parameters,
            "-pix_fmt",
            "yuv420p10le" if hdr else "yuv420p",
            "-color_range",
            "tv",
            str(path),
        ],
        check=True,
        timeout=60,
    )


def first_frame(tools, path):
    return subprocess.run(
        [
            tools.ffmpeg,
            "-v",
            "error",
            "-i",
            str(path),
            "-frames:v",
            "1",
            "-pix_fmt",
            "rgb24",
            "-f",
            "rawvideo",
            "pipe:1",
        ],
        capture_output=True,
        check=True,
        timeout=30,
    ).stdout


def main():
    tools = native.Tools(shutil.which("ffmpeg"), shutil.which("ffprobe"))
    assert tools.ffmpeg and tools.ffprobe
    stub = native.ROOT / "qa/BackendChecks/bin/Release/net11.0/BackendChecks.exe"
    assert stub.exists(), "Build qa/BackendChecks first"
    original = {
        key: os.environ.get(key)
        for key in ("LUMARECODE_QSV_STUB", "LUMARECODE_QSV_TRACE")
    }
    count = 0
    try:
        with tempfile.TemporaryDirectory(
            prefix="LumaRecode-qsv-recovery-"
        ) as temporary:
            folder = Path(temporary)
            source = folder / "cfr.mp4"
            make_source(tools, source)
            source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
            media = native.inspect_media(tools, source)
            for scenario, attempts in [
                ("decoder", 2),
                ("power", 2),
                ("format", 2),
                ("decoder-power-timestamp", 4),
                ("fatal", 1),
                ("file-exists", 1),
            ]:
                reset_server()
                trace = folder / (scenario + ".jsonl")
                os.environ["LUMARECODE_QSV_STUB"] = scenario
                os.environ["LUMARECODE_QSV_TRACE"] = str(trace)
                output = folder / (scenario + ".mp4")
                output.write_bytes(b"existing result")
                failed = scenario in ("fatal", "file-exists")
                try:
                    result = native.request(
                        "run-events",
                        media=media,
                        options=native.Options(),
                        output=output,
                        overwrite=True,
                        ffmpeg=str(stub),
                    )
                    assert not failed, scenario
                    check_timing(source, output)
                    statuses = [
                        e["data"] for e in result["events"] if e["event"] == "status"
                    ]
                    assert len(statuses) >= attempts, statuses
                    decoders = [
                        e["data"] for e in result["events"] if e["event"] == "decoder"
                    ]
                    assert decoders[0] == "qsv"
                    assert decoders[-1] == (
                        "software"
                        if "decoder" in scenario or scenario == "format"
                        else "qsv"
                    ), (scenario, decoders)
                except ValueError:
                    assert failed, scenario
                    assert output.read_bytes() == b"existing result"
                commands = [
                    json.loads(line)
                    for line in trace.read_text(encoding="utf-8").splitlines()
                ]
                assert len(commands) == attempts, (scenario, len(commands))
                for command in commands:
                    assert command[command.index("-global_quality:v") + 1] == "24"
                    assert command[command.index("-preset") + 1] == "4"
                if "decoder" in scenario or scenario == "format":
                    assert "-hwaccel" not in commands[-1]
                if "power" in scenario:
                    assert commands[-1][commands[-1].index("-low_power") + 1] == "-1"
                if "timestamp" in scenario:
                    assert commands[-1][commands[-1].index("-bf:v") + 1] == "0"
                assert not list(folder.glob(".video-converter-*"))
                assert hashlib.sha256(source.read_bytes()).hexdigest() == source_hash
                count += 1
            reset_server()
            os.environ.pop("LUMARECODE_QSV_STUB", None)
            os.environ.pop("LUMARECODE_QSV_TRACE", None)
            pq = folder / "pq.mp4"
            make_source(tools, pq, hdr=True)
            pq_media = native.inspect_media(tools, pq)
            av1 = folder / "av1-pq.mp4"
            native.Job().run(
                tools, pq_media, native.Options(codec="av1", output="hdr"), av1
            )
            av1_media = native.inspect_media(tools, av1)
            for codec in ("hevc", "av1"):
                output = folder / ("av1-hdr-to-" + codec + ".mp4")
                result = native.request(
                    "run-events",
                    media=av1_media,
                    options=native.Options(codec=codec, output="hdr"),
                    output=output,
                )
                commands = [
                    e["data"]
                    for e in result["events"]
                    if e["event"] == "log" and "-progress" in str(e["data"])
                ]
                assert 1 <= len(commands) <= 2
                if len(commands) == 2:
                    assert "-hwaccel" in commands[0] and "-hwaccel" not in commands[1]
                assert result["media"]["kind"] == "pq"
                check_timing(av1, output)
                count += 1
            for label, rotation in [
                ("90", ["-display_rotation:v:0", "90"]),
                ("180", ["-display_rotation:v:0", "180"]),
                ("270", ["-display_rotation:v:0", "270"]),
                ("mirror", ["-display_hflip:v:0"]),
            ]:
                rotated = folder / (label + ".mp4")
                subprocess.run(
                    [
                        tools.ffmpeg,
                        "-v",
                        "error",
                        *rotation,
                        "-i",
                        str(source),
                        "-c",
                        "copy",
                        str(rotated),
                    ],
                    check=True,
                    timeout=30,
                )
                media = native.inspect_media(tools, rotated)
                expected = first_frame(tools, rotated)
                for codec in ("avc", "hevc", "av1"):
                    output = folder / (label + "-" + codec + ".mp4")
                    options = native.Options(codec=codec)
                    command, _ = native.build_command(tools, media, options, output)
                    assert "-hwaccel" not in command
                    result = native.Job().run(tools, media, options, output)
                    dimensions = (result.video["width"], result.video["height"])
                    assert dimensions == (
                        (180, 320) if label in ("90", "270") else (320, 180)
                    ), dimensions
                    assert not any(
                        s.get("side_data_type") == "Display Matrix"
                        for s in result.video.get("side_data_list", [])
                    )
                    actual = first_frame(tools, output)
                    assert len(actual) == len(expected)
                    error = sum((a - b) ** 2 for a, b in zip(actual, expected)) / len(
                        actual
                    )
                    assert error < 150, (label, codec, error)
                    check_timing(rotated, output)
                    count += 1
            rotated = folder / "pq-90.mp4"
            subprocess.run(
                [
                    tools.ffmpeg,
                    "-v",
                    "error",
                    "-display_rotation:v:0",
                    "90",
                    "-i",
                    str(pq),
                    "-c",
                    "copy",
                    str(rotated),
                ],
                check=True,
                timeout=30,
            )
            for codec in ("hevc", "av1"):
                output = folder / ("pq-90-" + codec + ".mp4")
                media = native.inspect_media(tools, rotated)
                result = native.Job().run(
                    tools, media, native.Options(codec=codec, output="hdr"), output
                )
                assert (result.video["width"], result.video["height"], result.kind) == (
                    180,
                    320,
                    "pq",
                )
                check_timing(rotated, output)
                count += 1
            fast = folder / "240fps.mp4"
            make_source(tools, fast, high_fps=True)
            output = folder / "240fps-share.mp4"
            media = native.inspect_media(tools, fast)
            native.Job().run(tools, media, native.Options(), output)
            check_timing(fast, output)
            result = subprocess.run(
                [
                    tools.ffprobe,
                    "-v",
                    "error",
                    "-select_streams",
                    "v:0",
                    "-show_entries",
                    "packet=pts_time,flags",
                    "-of",
                    "json",
                    str(output),
                ],
                capture_output=True,
                check=True,
            )
            keys = [
                float(p["pts_time"])
                for p in json.loads(result.stdout)["packets"]
                if "K" in p["flags"]
            ]
            assert len(keys) >= 2 and keys[1] <= 1.001, keys
            count += 1
    finally:
        reset_server()
        for key, value in original.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value
    print(
        f"PASS {count} QSV recovery cases: AV1 HDR, rotation/mirroring, bounded retries, parameter/source preservation and high-fps GOPs."
    )


if __name__ == "__main__":
    main()
