"""Check exact display-frame cuts, still formats and Google Motion Photo packaging."""

import argparse
import json
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "qa/BackendChecks"))
import native_backend as native


def run(*args):
    return subprocess.run(
        list(map(str, args)), capture_output=True, check=True, timeout=180
    ).stdout


def frame_means(path):
    pixels = run(
        "ffmpeg",
        "-v",
        "error",
        "-i",
        path,
        "-an",
        "-vf",
        "format=gray",
        "-fps_mode",
        "passthrough",
        "-f",
        "rawvideo",
        "-pix_fmt",
        "gray",
        "-",
    )
    size = 128 * 72
    assert len(pixels) % size == 0
    return [sum(pixels[i : i + size]) / size for i in range(0, len(pixels), size)]


def fixture(folder, vfr=False):
    output = folder / ("vfr.mp4" if vfr else "cfr.mp4")
    filters = "geq=lum='16+N*2':cb=128:cr=128"
    if vfr:
        filters += ",setpts=(N+floor(N/7)*2)/(30*TB)"
    run(
        "ffmpeg",
        "-v",
        "error",
        "-f",
        "lavfi",
        "-i",
        "color=black:size=128x72:rate=30000/1001",
        "-frames:v",
        "90",
        "-vf",
        filters,
        "-fps_mode",
        "passthrough",
        "-c:v",
        "libx264",
        "-qp",
        "0",
        "-g",
        "17",
        "-bf",
        "3",
        "-pix_fmt",
        "yuv420p",
        "-x264-params",
        "colorprim=bt709:transfer=bt709:colormatrix=bt709",
        output,
    )
    return output


def cuts(folder):
    checks = 0
    for vfr in [False, True]:
        source = fixture(folder, vfr)
        media = native.request("inspect", path=source)
        index = native.request("timeline", path=source)
        assert index["count"] == 90 and index["packet_indexed"]
        before = frame_means(source)
        for start, end in [(0, 1), (1, 8), (17, 20), (20, 29), (40, 58), (70, 90)]:
            destination = folder / f"cut-{vfr}-{start}.mkv"
            native.request(
                "run-events",
                media=media,
                output=destination,
                start_frame=start,
                end_frame=end,
                options={
                    "purpose": "archive",
                    "codec": "avc",
                    "output": "sdr",
                    "sampling": "420",
                    "bit_depth": "8",
                    "audio": "aac",
                },
            )
            after = frame_means(destination)
            assert len(after) == end - start, (start, end, len(after))
            assert (
                max(abs(a - b) for a, b in zip(before[start:end], after, strict=False))
                < 1.6
            )
            checks += 1
    return source, checks


def hdr_fixture(folder):
    output = folder / "hlg.mkv"
    run(
        "ffmpeg",
        "-v",
        "error",
        "-f",
        "lavfi",
        "-i",
        "testsrc2=size=128x72:rate=24:duration=1",
        "-vf",
        "format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc",
        "-c:v",
        "ffv1",
        output,
    )
    return output


def audio_cut(folder):
    source, _output = folder / "pcm.mov", folder / "pcm-cut.mkv"
    run(
        "ffmpeg",
        "-v",
        "error",
        "-f",
        "lavfi",
        "-i",
        "testsrc2=size=128x72:rate=30000/1001:duration=3",
        "-f",
        "lavfi",
        "-i",
        "sine=frequency=997:sample_rate=48000:duration=3",
        "-c:v",
        "libx264",
        "-g",
        "17",
        "-pix_fmt",
        "yuv420p",
        "-x264-params",
        "colorprim=bt709:transfer=bt709:colormatrix=bt709",
        "-c:a",
        "pcm_s16le",
        source,
    )
    checks = 0
    for offset in [0, 5]:
        timed = source
        if offset:
            timed = folder / "pcm-offset.mov"
            run(
                "ffmpeg",
                "-v",
                "error",
                "-i",
                source,
                "-c",
                "copy",
                "-output_ts_offset",
                offset,
                timed,
            )
        media = native.request("inspect", path=timed)
        for start, end in [(0, 1), (1, 8), (40, 58), (70, 89)]:
            destination = folder / f"pcm-cut-{offset}-{start}.mkv"
            native.request(
                "run-events",
                media=media,
                output=destination,
                start_frame=start,
                end_frame=end,
                options={
                    "purpose": "archive",
                    "codec": "avc",
                    "output": "sdr",
                    "audio": "flac",
                    "bit_depth": "8",
                    "sampling": "420",
                },
            )
            actual = run(
                "ffmpeg",
                "-v",
                "error",
                "-i",
                destination,
                "-map",
                "0:a:0",
                "-f",
                "s16le",
                "-",
            )
            expected = run(
                "ffmpeg",
                "-v",
                "error",
                "-i",
                timed,
                "-map",
                "0:a:0",
                "-af",
                f"atrim=start={start * 1001 / 30000}:end={end * 1001 / 30000}",
                "-f",
                "s16le",
                "-",
            )
            assert actual == expected, (offset, start, len(actual), len(expected))
            checks += 1
    return checks


def motion_audio_and_orientation(folder):
    source, rotated = folder / "mono.mov", folder / "mono-rotated.mov"
    run(
        "ffmpeg",
        "-v",
        "error",
        "-f",
        "lavfi",
        "-i",
        "testsrc2=size=1920x1080:rate=12:duration=1",
        "-f",
        "lavfi",
        "-i",
        "sine=sample_rate=44100:duration=1",
        "-map",
        "0:v",
        "-map",
        "1:a",
        "-map",
        "1:a",
        "-c:v",
        "libx264",
        "-preset",
        "ultrafast",
        "-x264-params",
        "colorprim=bt709:transfer=bt709:colormatrix=bt709",
        "-c:a",
        "pcm_s16le",
        source,
    )
    run(
        "ffmpeg",
        "-v",
        "error",
        "-display_rotation:v:0",
        "90",
        "-i",
        source,
        "-map",
        "0",
        "-c",
        "copy",
        rotated,
    )
    for video in [source, rotated]:
        output = folder / f"{video.stem}_MP.jpg"
        native.request(
            "image-export",
            path=video,
            output=output,
            frame=4,
            format="sdr",
            motion=True,
            start_frame=0,
            end_frame=6,
            purpose="archive",
            motion_size="720",
        )
        assert b"ICC_PROFILE" in output.read_bytes()[: motion_info(output)["offset"]]
        clip = folder / f"{video.stem}-clip.mp4"
        clip.write_bytes(output.read_bytes()[motion_info(output)["offset"] :])
        streams = json.loads(
            run("ffprobe", "-v", "error", "-show_streams", "-of", "json", clip)
        )["streams"]
        assert len(streams) == 2
        image = next(s for s in streams if s["codec_type"] == "video")
        audio = next(s for s in streams if s["codec_type"] == "audio")
        assert (image["width"], image["height"]) == (
            (1280, 720) if video == source else (720, 1280)
        )
        assert (
            audio["codec_name"] == "aac"
            and audio["profile"] == "LC"
            and audio["sample_rate"] == "48000"
            and audio["channels"] == 1
        )
    return 2


def cli_checks(folder, source, executable):
    def call(*args, success=True):
        result = subprocess.run(
            [
                str(executable),
                *map(str, args),
                "--no-settings",
                "--accept-terms",
                "--json",
            ],
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=90,
        )
        assert (result.returncode == 0) == success, (
            result.returncode,
            result.stdout,
            result.stderr,
        )
        return result.stdout

    assert json.loads(call("frames", source))["frames"] == 24
    output = folder / "cli-cut.mkv"
    plan = call(
        "plan",
        source,
        "--purpose",
        "archive",
        "--start-frame",
        "4",
        "--end-frame",
        "9",
        "-o",
        output,
    )
    assert "trim=start_frame=4:end_frame=9" in plan and not output.exists()
    call("plan", source, "--start-frame", "4", "--duration", "1", success=False)
    call(
        "image",
        source,
        "--frame",
        "2",
        "--image-format",
        "hdr",
        "--dry-run",
        "-o",
        folder / "cli.avif",
    )
    call(
        "image",
        source,
        "--image-format",
        "ultrahdr",
        "--motion-size",
        "720",
        "-o",
        folder / "invalid.jpg",
        success=False,
    )
    image = folder / "cli_MP.jpg"
    result = call(
        "image",
        source,
        "--frame",
        "4",
        "--image-format",
        "ultrahdr",
        "--motion",
        "--motion-size",
        "720",
        "--motion-start-frame",
        "2",
        "--motion-end-frame",
        "9",
        "-o",
        image,
    )
    assert (
        any(row.get("event") == "done" for row in map(json.loads, result.splitlines()))
        and image.stat().st_size > 100
    )
    call(
        "image",
        source,
        "--image-format",
        "ultrahdr",
        "--motion",
        "-o",
        image,
        success=False,
    )
    return 7


def images(folder, sdr, reference):
    hdr = hdr_fixture(folder)
    checks = 0
    for source, formats in [
        (sdr, ["sdr", "png"]),
        (hdr, ["hdr", "ultrahdr", "sdr", "png"]),
    ]:
        for fmt in formats:
            suffix = ".avif" if fmt == "hdr" else ".png" if fmt == "png" else ".jpg"
            destination = folder / f"{source.stem}-{fmt}{suffix}"
            native.request(
                "image-export", path=source, output=destination, frame=7, format=fmt
            )
            assert destination.stat().st_size > 100
            if fmt == "ultrahdr":
                assert (
                    native.request("ultrahdr-inspect", path=destination)["width"] == 128
                )
                if reference:
                    decoded = folder / "official-decoded.rgba"
                    run(
                        reference,
                        "-m",
                        "1",
                        "-j",
                        destination,
                        "-o",
                        "1",
                        "-O",
                        "5",
                        "-z",
                        decoded,
                    )
                    assert decoded.stat().st_size == 128 * 72 * 4
            checks += 1
    for source, fmt, motion_range in [
        (sdr, "sdr", "sdr"),
        (hdr, "sdr", "sdr"),
        (hdr, "ultrahdr", "sdr"),
        (hdr, "sdr", "hdr"),
        (hdr, "ultrahdr", "hdr"),
    ]:
        output = folder / f"{source.stem}-{fmt}-{motion_range}_MP.jpg"
        native.request(
            "image-export",
            path=source,
            output=output,
            frame=7,
            format=fmt,
            motion=True,
            motion_range=motion_range,
            start_frame=2,
            end_frame=18,
            purpose="archive",
        )
        info = motion_info(output)
        clip = folder / f"{source.stem}-{fmt}-{motion_range}-motion.mp4"
        clip.write_bytes(output.read_bytes()[info["offset"] :])
        stream = json.loads(
            run(
                "ffprobe", "-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=codec_name,pix_fmt,color_transfer", "-of", "json", clip,
            )
        )["streams"][0]
        signal = tuple(stream.get(key) for key in ("codec_name", "pix_fmt", "color_transfer"))
        expected = (
            ("hevc", "yuv420p10le", "arib-std-b67")
            if motion_range == "hdr"
            else ("h264", "yuv420p", "bt709")
        )
        assert signal == expected, (fmt, motion_range, signal)
        frames = json.loads(
            run(
                "ffprobe",
                "-v",
                "error",
                "-select_streams",
                "v:0",
                "-show_frames",
                "-show_entries",
                "frame=pts_time",
                "-of",
                "json",
                clip,
            )
        )["frames"]
        assert len(frames) == 16
        assert abs(float(frames[5]["pts_time"]) * 1_000_000 - info["timestamp_us"]) < 1
        if fmt == "ultrahdr" and reference:
            decoded = folder / "official-motion.rgba"
            run(reference, "-m", "1", "-j", output, "-o", "1", "-O", "5", "-z", decoded)
            assert decoded.stat().st_size == 128 * 72 * 4
        checks += 1
    try:
        native.request(
            "image-export", path=sdr, output=folder / "invalid.jpg", format="ultrahdr"
        )
        raise AssertionError("SDR-to-HDR export was accepted")
    except ValueError:
        checks += 1
    return checks


def motion_info(path):
    # Read the XMP independently of the production inspector. The last item is
    # the exact appended movie length, not a legacy MicroVideoOffset guess.
    import xml.etree.ElementTree as ET

    data = path.read_bytes()
    prefix = b"http://ns.adobe.com/xap/1.0/\x00"
    at = data.index(prefix)
    length = int.from_bytes(data[at - 2 : at], "big") - 2
    xml = ET.fromstring(data[at + len(prefix) : at + length])
    ns = "http://ns.google.com/photos/1.0/"
    items = xml.findall(f".//{{{ns}container/}}Item")
    semantics = [i.attrib[f"{{{ns}container/item/}}Semantic"] for i in items]
    assert semantics in [
        ["Primary", "MotionPhoto"],
        ["Primary", "GainMap", "MotionPhoto"],
    ]
    size = int(items[-1].attrib[f"{{{ns}container/item/}}Length"])
    offset = len(data) - size
    assert (
        data[offset + 4 : offset + 8] == b"ftyp"
        and data[offset - 2 : offset] == b"\xff\xd9"
    )
    description = next(
        x for x in xml.iter() if f"{{{ns}camera/}}MotionPhoto" in x.attrib
    )
    assert description.attrib[f"{{{ns}camera/}}MotionPhotoVersion"] == "1"
    return {
        "offset": offset,
        "timestamp_us": int(
            description.attrib[f"{{{ns}camera/}}MotionPhotoPresentationTimestampUs"]
        ),
    }


def previews(folder, source):
    means = []
    for frame in (0, 16, 17, 62):
        output = folder / f"preview-{frame}.png"
        native.request("image-preview", path=source, output=output, frame=frame)
        data = output.read_bytes()
        assert data[:8] == b"\x89PNG\r\n\x1a\n" and b"iCCP" in data
        assert struct.unpack_from(">II", data, 16) == (128, 72)
        means.append(frame_means(output)[0])
    assert all(a < b for a, b in zip(means, means[1:], strict=False)), means
    # Exercise the scale limit after FFmpeg applies a display rotation.
    rotated = folder / "mono-rotated.mov"
    if rotated.exists():
        output = folder / "preview-rotated.png"
        native.request("image-preview", path=rotated, output=output, frame=4)
        width, height = struct.unpack_from(">II", output.read_bytes(), 16)
        assert height == 1024 and width < height
        return 5
    return 4


def color_order(folder, reference):
    if not reference:
        return 0
    # Packed RGB10 has R in the low ten bits. Reconstruct through Google's
    # decoder and ensure red/green/blue patches retain their channel identity.
    raw = folder / "colors.rgb10"
    colors = [(700, 30, 30), (30, 700, 30), (30, 30, 700)]
    raw.write_bytes(
        b"".join(
            struct.pack("<I", r | g << 10 | b << 20 | 3 << 30)
            for _ in range(32)
            for r, g, b in colors
            for _ in range(32)
        )
    )
    jpeg, decoded = folder / "colors.jpg", folder / "colors.rgba"
    native.request(
        "ultrahdr-encode", path=raw, output=jpeg, width=96, height=32, hlg=True
    )
    run(reference, "-m", "1", "-j", jpeg, "-o", "1", "-O", "5", "-z", decoded)
    pixels = decoded.read_bytes()
    for channel in range(3):
        pixel = struct.unpack_from("<I", pixels, (16 * 96 + channel * 32 + 16) * 4)[0]
        rgb = [(pixel >> shift) & 1023 for shift in (0, 10, 20)]
        assert rgb[channel] > max(v for i, v in enumerate(rgb) if i != channel) + 100, (
            rgb
        )
    return 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--reference", type=Path, help="Development-only Google ultrahdr_app.exe"
    )
    parser.add_argument(
        "--exe", type=Path, help="Also check frame/image commands in the packaged EXE"
    )
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="lumarecode-frames-") as temporary:
        folder = Path(temporary)
        source, count = cuts(folder)
        count += audio_cut(folder)
        count += images(folder, source, args.reference)
        count += motion_audio_and_orientation(folder)
        count += previews(folder, source)
        count += color_order(folder, args.reference)
        if args.exe:
            count += cli_checks(folder, folder / "hlg.mkv", args.exe.resolve())
    print(f"Passed {count} frame-cut, still-image, gain-map and Motion Photo checks.")
