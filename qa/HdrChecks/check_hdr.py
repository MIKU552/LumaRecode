"""Check Log math, dynamic HDR metadata and camera-dependent ProRes RAW paths.

Run with --samples pointing to the downloaded source files listed in sample-sources.json.
Footage and downloaded tools stay outside the repository.
"""

import argparse
import json
import math
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "qa/BackendChecks"))
sys.path.insert(0, str(ROOT / "tools"))
import generate_hlg_luts as lut
import native_backend as native


def side(frame, name):
    return next(
        (v for v in frame.get("side_data_list", []) if v["side_data_type"] == name),
        None,
    )


def frame_data(path, seconds=None):
    args = ["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_frames"]
    if seconds is not None:
        args += ["-read_intervals", f"%+{seconds}"]
    return json.loads(subprocess.check_output(args + ["-of", "json", str(path)]))[
        "frames"
    ]


def check_math():
    # Reference values follow the Apple piecewise curve, independently encoded
    # here from scene-linear values rather than from the generator's decoder.
    for linear in [0, 0.001, 0.01, 0.18, 1, 4, 8]:
        code = (
            47.28711236 * (linear + 0.05641088) ** 2
            if linear < 0.01
            else 0.08550479 * math.log2(linear + 0.00964052) + 0.69336945
        )
        assert abs(lut.linear_apple_log(code) - linear) < 1e-7
    for level in [0, 0.001, 0.18, 1, 10]:
        converted = lut.apple_wide_to_bt2020((level,) * 3)
        assert max(abs(v - level) for v in converted) < 1e-12
    for rgb in [(1, 0, 0), (0, 1, 0), (0, 0, 1), (0.2, 0.6, 0.1)]:
        converted = lut.apple_wide_to_bt2020(rgb)
        assert all(math.isfinite(v) and v >= 0 for v in converted)
    assert abs(lut.hlg_oetf(0.18 * lut.GRAY_SCALE) - 0.38) < 1e-12
    for marker, expected in [
        ("com.apple.log.apple-log", "applelog"),
        ("com.apple.apple-wide-gamut.apple-log", "applelog2"),
    ]:
        result = native.request(
            "classify",
            video={"color_transfer": "bt709"},
            tags={"com.apple.proapps.logColorSpace": marker},
        )
        assert result[:2] == ["log", expected]
    result = native.request(
        "classify",
        video={"codec_name": "prores", "color_transfer": "bt709"},
        tags={"com.apple.quicktime.model": "iPhone 17 Pro"},
    )
    assert result[0] == "sdr"
    print(
        "PASS Apple transfer, gamut, neutral-gray and explicit-metadata checks.",
        flush=True,
    )


def check_export(samples):
    """Compare compressed-packet ordering against decoded display order."""
    with tempfile.TemporaryDirectory(prefix="lumarecode-hdr-export-") as temporary:
        folder = Path(temporary)
        for name in [
            "regular.mkv",
            "dhdr10-opt.hevc",
            "dts_injected.hevc",
            "pts_injected.hevc",
            "multimsg-sei.hevc",
        ]:
            path = samples / name
            if not path.exists():
                continue
            output, decoded = folder / "packets.json", folder / "decoded.json"
            # Each extraction gets its own job directory, just as production
            # does; this also checks the absence of external-helper settings.
            job = folder / path.stem
            job.mkdir()
            output = job / output.name
            native.request("hdr10plus-export", path=path, output=output)
            compact = subprocess.check_output(
                [
                    "ffprobe",
                    "-v",
                    "error",
                    "-select_streams",
                    "v:0",
                    "-show_frames",
                    "-show_entries",
                    "frame=:frame_side_data",
                    "-of",
                    "compact=p=1:nk=0",
                    str(path),
                ],
                text=True,
            )
            native.request("hdr10plus-export", frames=compact, output=decoded)
            actual = json.loads(output.read_text(encoding="utf-8"))
            reference = json.loads(decoded.read_text(encoding="utf-8"))
            assert actual["SceneInfo"] == reference["SceneInfo"], name
            assert actual["JSONInfo"] == reference["JSONInfo"], name

        # Build timestamped sparse-SEI and Profile B inputs through x265, plus
        # an AV1 HDR10+ input through libaom. Decoder output is an independent
        # reference for the C# bit parser and packet/display ordering.
        metadata = json.loads((folder / "regular/packets.json").read_text())
        (folder / "a.json").write_text(json.dumps(metadata))
        for frame in metadata["SceneInfo"]:
            frame["TargetedSystemDisplayMaximumLuminance"] = 1000
            frame["BezierCurveData"] = {
                "Anchors": [300, 400],
                "KneePointX": 100,
                "KneePointY": 200,
            }
        metadata["JSONInfo"]["HDR10plusProfile"] = "B"
        (folder / "b.json").write_text(json.dumps(metadata))
        for label, encoder in [
            (
                "sparse",
                [
                    "-c:v",
                    "libx265",
                    "-preset",
                    "ultrafast",
                    "-x265-params",
                    "dhdr10-info=a.json:dhdr10-opt=1:pools=2:log-level=error",
                ],
            ),
            (
                "profile-b",
                [
                    "-c:v",
                    "libx265",
                    "-preset",
                    "ultrafast",
                    "-x265-params",
                    "dhdr10-info=b.json:pools=2:log-level=error",
                ],
            ),
            (
                "av1",
                ["-t", "0.2", "-c:v", "libaom-av1", "-cpu-used", "8", "-crf", "35"],
            ),
        ]:
            path = folder / (label + ".mp4")
            subprocess.run(
                [
                    "ffmpeg",
                    "-v",
                    "error",
                    "-i",
                    str((samples / "regular.mkv").resolve()),
                    "-an",
                    "-threads",
                    "2",
                    *encoder,
                    str(path),
                ],
                cwd=folder,
                check=True,
                capture_output=True,
            )
            job = folder / label
            job.mkdir()
            native.request("hdr10plus-export", path=path, output=job / "packets.json")
            compact = subprocess.check_output(
                [
                    "ffprobe",
                    "-v",
                    "error",
                    "-select_streams",
                    "v:0",
                    "-show_frames",
                    "-show_entries",
                    "frame=:frame_side_data",
                    "-of",
                    "compact=p=1:nk=0",
                    str(path),
                ],
                text=True,
            )
            native.request(
                "hdr10plus-export", frames=compact, output=job / "decoded.json"
            )
            assert (
                json.loads((job / "packets.json").read_text())["SceneInfo"]
                == json.loads((job / "decoded.json").read_text())["SceneInfo"]
            ), label
            media = native.request("inspect", path=path)
            encoded = job / "preserved.mp4"
            native.request(
                "run-events",
                media=media,
                options={
                    "purpose": "archive",
                    "codec": "hevc",
                    "output": "hdr",
                    "transform": "none",
                    "dynamic_hdr": "preserve",
                    "sampling": "420",
                    "bit_depth": "10",
                    "audio": "aac",
                    "container": "mp4",
                },
                output=encoded,
                seconds=0.1,
            )
            native.request(
                "hdr10plus-export", path=encoded, output=job / "roundtrip.json"
            )
            restored = json.loads((job / "roundtrip.json").read_text())["SceneInfo"]
            assert (
                restored
                == json.loads((job / "packets.json").read_text())["SceneInfo"][
                    : len(restored)
                ]
            ), label

        # A copy-trimmed MP4 carries leading discard packets with negative PTS.
        # They affect decoding but must not consume an output metadata index.
        trimmed = folder / "edit-list.mp4"
        subprocess.run(
            [
                "ffmpeg",
                "-v",
                "error",
                "-ss",
                "0.15",
                "-i",
                str(samples / "regular.mkv"),
                "-t",
                "0.5",
                "-c:v",
                "copy",
                "-an",
                str(trimmed),
            ],
            check=True,
        )
        job = folder / "edit-list"
        job.mkdir()
        native.request("hdr10plus-export", path=trimmed, output=job / "packets.json")
        compact = subprocess.check_output(
            [
                "ffprobe",
                "-v",
                "error",
                "-select_streams",
                "v:0",
                "-show_frames",
                "-show_entries",
                "frame=:frame_side_data",
                "-of",
                "compact=p=1:nk=0",
                str(trimmed),
            ],
            text=True,
        )
        native.request("hdr10plus-export", frames=compact, output=job / "decoded.json")
        assert (
            json.loads((job / "packets.json").read_text())["SceneInfo"]
            == json.loads((job / "decoded.json").read_text())["SceneInfo"]
        )

        first = compact.splitlines()[0]
        profile_b = first.replace(
            "targeted_system_display_maximum_luminance=0/1",
            "targeted_system_display_maximum_luminance=1000/1",
        )
        profile_b += "|knee_point_x=100/4095|knee_point_y=200/4095|num_bezier_curve_anchors=2|bezier_curve_anchors=300/1023|bezier_curve_anchors=400/1023"
        native.request(
            "hdr10plus-export", frames=profile_b + "\n" + profile_b, output=decoded
        )
        data = json.loads(decoded.read_text())
        assert data["JSONInfo"]["HDR10plusProfile"] == "B"
        assert data["SceneInfo"][1]["SceneFrameIndex"] == 1
        assert data["SceneInfo"][0]["BezierCurveData"] == {
            "Anchors": [300, 400],
            "KneePointX": 100,
            "KneePointY": 200,
        }
        for bad in [
            first + "\nframe",
            first + "|color_saturation_weight=8/8",
            first.replace(
                "fraction_bright_pixels=0/1000", "fraction_bright_pixels=1/1000"
            ),
            first.replace("num_windows=1", "num_windows=2"),
            first + "\n" + profile_b,
        ]:
            try:
                native.request("hdr10plus-export", frames=bad, output=decoded)
            except ValueError:
                pass
            else:
                raise AssertionError(
                    "Accepted incomplete or unrepresentable HDR10+ metadata"
                )
    print(
        "PASS HDR10+ SEI ordering, edit-list preroll, sparse metadata, Profile B and rejection checks.",
        flush=True,
    )


def run(samples):
    count = 0
    with tempfile.TemporaryDirectory(prefix="lumarecode-hdr-") as temporary:
        folder = Path(temporary)

        def encode(path, target="hdr", mode="preserve", codec="hevc", **extra):
            nonlocal count
            media = native.request("inspect", path=path)
            options = {
                "purpose": "archive",
                "codec": codec,
                "output": target,
                "transform": "hdr-sdr"
                if target == "sdr" and media["kind"] != "raw"
                else "none",
                "dynamic_hdr": mode,
                "sampling": "420",
                "bit_depth": "10",
                "audio": "aac",
                "container": "mp4",
            }
            options.update(extra)
            output = folder / f"{count}-{path.stem}-{target}-{mode}-{codec}.mp4"
            data = native.request(
                "run-events", media=media, options=options, output=output, seconds=0.1
            )
            count += 1
            print("PASS", path.name, target, mode, codec, flush=True)
            return media, data["media"], output

        for path in sorted(samples.glob("BL_*.mp4")):
            for target, mode in [
                ("hdr", "preserve"),
                ("hdr", "standard"),
                ("sdr", "standard"),
            ]:
                _before, after, output = encode(path, target, mode)
                source_frames = frame_data(path, 0.3)
                output_frames = frame_data(output)
                if mode == "preserve":
                    assert len(output_frames) > 1
                    for source, encoded in zip(
                        source_frames, output_frames, strict=False
                    ):
                        assert side(source, "Dolby Vision Metadata") == side(
                            encoded, "Dolby Vision Metadata"
                        )
                else:
                    assert not any(
                        "Dolby" in s["side_data_type"] or "DOVI" in s["side_data_type"]
                        for s in after["video"].get("side_data_list", [])
                    )
                    assert all(
                        side(f, "Dolby Vision Metadata") is None for f in output_frames
                    )
            if "dvhe-08" in path.name:
                _, _, av1 = encode(path, codec="av1")
                # Re-ingest Profile 10 and convert back to HEVC Profile 8.
                encode(av1)

        hdr10plus = samples / "regular.mkv"
        for target, mode in [
            ("hdr", "preserve"),
            ("hdr", "standard"),
            ("sdr", "standard"),
        ]:
            _, after, output = encode(hdr10plus, target, mode)
            frames = frame_data(output)
            hdr_name = "HDR Dynamic Metadata SMPTE2094-40 (HDR10+)"
            assert (side(frames[0], hdr_name) is not None) == (mode == "preserve")
            if mode == "preserve":
                metadata = []
                helper = shutil.which("hdr10plus_tool")
                assert helper, "hdr10plus_tool must be on PATH for this check"
                for index, video in enumerate([hdr10plus, output]):
                    bitstream, info = folder / f"{index}.hevc", folder / f"{index}.json"
                    subprocess.run(
                        [
                            "ffmpeg",
                            "-v",
                            "error",
                            "-i",
                            str(video),
                            "-map",
                            "0:v:0",
                            "-c:v",
                            "copy",
                            "-bsf:v",
                            "hevc_mp4toannexb",
                            "-f",
                            "hevc",
                            str(bitstream),
                        ],
                        check=True,
                    )
                    subprocess.run(
                        [helper, "extract", "-o", str(info), str(bitstream)],
                        check=True,
                        capture_output=True,
                    )
                    metadata.append(
                        json.loads(info.read_text(encoding="utf-8"))["SceneInfo"]
                    )
                assert metadata[0][: len(metadata[1])] == metadata[1]

        raw = samples / "4.2K23.976ProResRAWHQ.mov"
        for target in ["hdr", "sdr"]:
            encode(raw, target, "standard")
        encode(raw, sampling="444", bit_depth="12")
        iphone = samples / "Apple17_ProRes_RAW_excerpt.mov"
        if iphone.exists():
            media = native.request("inspect", path=iphone)
            assert (
                media["kind"] == "raw" and media["video"]["_raw"]["bayer_pattern"] == 3
            )
            if not media["video"]["_raw"]["decoder_supported"]:
                try:
                    native.request("validate", media=media, options={"output": "hdr"})
                except ValueError as error:
                    assert "ProRes RAW" in str(error)
                else:
                    raise AssertionError("An unsupported RAW decoder was accepted")
                print(
                    "PASS iPhone RAW inspection and decoder-compatibility rejection.",
                    flush=True,
                )

        apple = samples / "Apple_Log_CMG_excerpt.mov"
        if apple.exists():
            encode(apple, source_kind="log", profile="applelog", transform="log-hdr")
            encode(
                apple, "sdr", source_kind="log", profile="applelog", transform="log-sdr"
            )
        print(f"PASS {count} real encodes and dynamic-HDR frame checks.", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--samples", type=Path)
    parser.add_argument("--metadata-only", action="store_true")
    arguments = parser.parse_args()
    check_math()
    if arguments.samples:
        check_export(arguments.samples)
        if not arguments.metadata_only:
            run(arguments.samples)
