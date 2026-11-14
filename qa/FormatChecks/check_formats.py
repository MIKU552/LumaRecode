"""Encode synthetic sources and check sample ranges, chroma, and lossless audio."""

import hashlib
import shutil
import subprocess
import sys
import tempfile
from dataclasses import replace
from pathlib import Path

root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / "qa/BackendChecks"))
from native_backend import (
    Job,
    Options,
    Tools,
    build_command,
    flac_eligibility,
    inspect_media,
    public_media,
    resolve_format,
    validate,
)

tools = Tools(shutil.which("ffmpeg"), shutil.which("ffprobe"))
assert tools.ffmpeg and tools.ffprobe


def run(args):
    result = subprocess.run(
        [tools.ffmpeg, "-v", "error", "-nostdin", *map(str, args)], capture_output=True
    )
    assert result.returncode == 0, result.stderr.decode("utf-8", "replace")
    return result.stdout


def reject(media, options):
    try:
        validate(options, media)
    except ValueError:
        return
    raise AssertionError("Unsupported conversion was accepted")


def bounds(path):
    """Read decoded edge luma samples, verifying range conversion beyond tags."""
    media = inspect_media(tools, path)
    depth = media.pixel["components"][0]["bit_depth"]
    raw = run(
        ["-i", path, "-vf", "extractplanes=y", "-frames:v", "1", "-f", "rawvideo", "-"]
    )
    step = 1 if depth == 8 else 2
    width, height = media.video["width"], media.video["height"]
    y = height // 2
    values = [
        int.from_bytes(
            raw[(y * width + x) * step : (y * width + x + 1) * step], "little"
        )
        for x in (0, width - 1)
    ]
    return depth, values


with tempfile.TemporaryDirectory(prefix="lumarecode-formats-") as temporary:
    folder = Path(temporary)
    base = folder / "full-444.mkv"
    run(
        [
            "-f",
            "lavfi",
            "-i",
            "nullsrc=s=320x192:r=12:d=0.5,format=yuv444p10le,geq=lum=X/(W-1)*1023:cb=512:cr=512",
            "-c:v",
            "ffv1",
            "-color_range",
            "pc",
            "-color_primaries",
            "bt709",
            "-color_trc",
            "bt709",
            "-colorspace",
            "bt709",
            base,
        ]
    )
    counter = 0

    def encode(media, options, name):
        global counter
        # FFV1 fixtures can omit transfer tags; exercise the input override.
        if media.kind == "unknown" and options.source_kind == "auto":
            options = replace(options, source_kind="sdr")
        path = folder / (name + options.suffix)
        logs = []
        try:
            result = Job().run(
                tools,
                media,
                options,
                path,
                emit=lambda event, data: logs.append((event, data)),
            )
        except Exception:
            print("\n".join(str(v) for e, v in logs), flush=True)
            raise
        counter += 1
        print(
            "PASS",
            name,
            result.video["pix_fmt"],
            result.video.get("color_range"),
            flush=True,
        )
        return path, result

    source = inspect_media(tools, base)
    reject(
        source,
        Options(output="sdr", source_kind="sdr", transform="log-sdr", profile="milog"),
    )
    reject(
        source,
        Options(output="sdr", source_kind="hlg", transform="log-sdr", profile="milog"),
    )
    for transfer, kind in [
        ("bt709", "sdr"),
        ("arib-std-b67", "hlg"),
        ("smpte2084", "pq"),
    ]:
        fixture = folder / (kind + ".mkv")
        run(
            [
                "-i",
                base,
                "-c:v",
                "copy",
                "-color_trc",
                transfer,
                "-color_primaries",
                "bt2020" if kind != "sdr" else "bt709",
                "-colorspace",
                "bt2020nc" if kind != "sdr" else "bt709",
                fixture,
            ]
        )
        media = inspect_media(tools, fixture)
        for purpose, color_range in [
            ("archive", "auto"),
            ("archive", "limited"),
            ("share", "auto"),
            ("share", "full"),
        ]:
            output = "sdr" if kind == "sdr" else "hdr"
            options = Options(purpose=purpose, output=output, color_range=color_range)
            path, result = encode(media, options, f"{kind}-{purpose}-{color_range}")
            limited = (
                purpose == "share" and color_range == "auto" or color_range == "limited"
            )
            assert result.video["color_range"] == ("tv" if limited else "pc")
            assert (
                result.video["pix_fmt"].replace("yuvj", "yuv")
                == resolve_format(options, media).pixel_format
            )
            depth, actual = bounds(path)
            expected = (
                [16 << (depth - 8), 235 << (depth - 8)]
                if limited
                else [0, (1 << depth) - 1]
            )
            assert max(abs(a - b) for a, b in zip(actual, expected, strict=False)) <= (
                4 << (depth - 8)
            ), (
                kind,
                purpose,
                actual,
                expected,
            )
        limited_source = folder / ("limited-" + kind + ".mkv")
        run(
            [
                "-i",
                fixture,
                "-vf",
                "zscale=rangein=full:range=limited,format=yuv420p10le",
                "-c:v",
                "libx265",
                "-preset",
                "ultrafast",
                "-x265-params",
                "lossless=1:log-level=error",
                "-color_range",
                "tv",
                "-color_trc",
                transfer,
                "-color_primaries",
                "bt2020" if kind != "sdr" else "bt709",
                "-colorspace",
                "bt2020nc" if kind != "sdr" else "bt709",
                limited_source,
            ]
        )
        media = inspect_media(tools, limited_source)
        for purpose, color_range in [
            ("archive", "auto"),
            ("archive", "full"),
            ("share", "auto"),
            ("share", "full"),
        ]:
            options = Options(
                purpose=purpose,
                output="sdr" if kind == "sdr" else "hdr",
                color_range=color_range,
            )
            if purpose == "share" and color_range == "auto":
                command, _ = build_command(
                    tools, media, options, folder / "preview.mp4"
                )
                assert "-hwaccel" in command, "QSV decode/VPP path was not exercised"
            path, result = encode(
                media, options, f"limited-{kind}-{purpose}-{color_range}"
            )
            depth, actual = bounds(path)
            expected = (
                [0, (1 << depth) - 1]
                if color_range == "full"
                else [16 << (depth - 8), 235 << (depth - 8)]
            )
            assert max(abs(a - b) for a, b in zip(actual, expected, strict=False)) <= (
                4 << (depth - 8)
            ), (
                kind,
                purpose,
                actual,
                expected,
            )
    print(
        "PASS range conversion: decoded black/white samples, SDR/HLG/PQ, archive/share",
        flush=True,
    )

    for pixel in ("yuv420p10le", "yuv422p10le", "yuv444p10le", "gbrp10le", "gray10le"):
        fixture = folder / (pixel + ".mkv")
        matrix = "rgb" if pixel.startswith("gbr") else "bt709"
        run(
            [
                "-i",
                base,
                "-vf",
                "format=" + pixel,
                "-c:v",
                "ffv1",
                "-color_range",
                "pc",
                "-colorspace",
                matrix,
                "-color_trc",
                "bt709",
                "-color_primaries",
                "bt709",
                fixture,
            ]
        )
        media = inspect_media(tools, fixture)
        _, result = encode(media, Options(purpose="archive"), "keep-" + pixel)
        assert public_media(media, tools)["archive_sampling"] == (
            "444" if pixel.startswith("gbr") else "source"
        )
        desired = "yuv444p10le" if pixel.startswith("gbr") else pixel
        assert result.video["pix_fmt"] == desired
        _, result = encode(
            media, Options(purpose="archive", codec="avc"), "avc-" + pixel
        )
        assert result.video["pix_fmt"].replace("yuvj", "yuv") == desired or (
            pixel == "gray10le" and result.video["_monochrome"]
        )
        if pixel != "yuv420p10le":
            reject(media, Options(purpose="archive", codec="av1", sampling="source"))
        _, result = encode(
            media,
            Options(purpose="archive", codec="av1", variant="fast"),
            "av1-" + pixel,
        )
        assert result.video["pix_fmt"] == "yuv420p10le"

    for pixel in ("yuv440p", "yuva444p10le"):
        fixture = folder / (pixel + ".mkv")
        run(
            [
                "-i",
                base,
                "-vf",
                "format=" + pixel,
                "-c:v",
                "ffv1",
                "-color_trc",
                "bt709",
                "-color_primaries",
                "bt709",
                fixture,
            ]
        )
        media = inspect_media(tools, fixture)
        reject(media, Options(purpose="archive", sampling="source"))
        _, converted = encode(media, Options(purpose="archive"), "default-" + pixel)
        assert converted.video["pix_fmt"] == (
            "yuv420p" if pixel == "yuv440p" else "yuv444p10le"
        )
        encode(media, Options(purpose="archive", sampling="420"), "explicit-" + pixel)

    for profile in ("milog", "pocket3", "samsung"):
        for target in ("hdr", "sdr"):
            options = Options(
                purpose="archive",
                output=target,
                source_kind="log",
                profile=profile,
                transform="log-" + target,
                color_range="full",
            )
            _, result = encode(source, options, profile + "-" + target)
            assert result.video["color_range"] == "pc"
            assert result.video["color_transfer"] == (
                "arib-std-b67" if target == "hdr" else "bt709"
            )

    for depth in (8, 12):
        for sampling in ("420", "422", "444"):
            pixel = f"yuv{sampling}p" + (f"{depth}le" if depth > 8 else "")
            fixture = folder / f"limits-{depth}-{sampling}.mkv"
            run(
                [
                    "-i",
                    base,
                    "-vf",
                    "format=" + pixel,
                    "-c:v",
                    "ffv1",
                    "-color_range",
                    "pc",
                    fixture,
                ]
            )
            media = inspect_media(tools, fixture)
            assert public_media(media, tools)["source_depth"] == depth
            for codec in ("hevc", "avc", "av1"):
                wanted_depth = min(depth, 12 if codec == "hevc" else 10)
                wanted_sampling = "420" if codec == "av1" else sampling
                options = Options(
                    purpose="archive",
                    codec=codec,
                    variant="fast" if codec == "av1" else "standard",
                )
                _, result = encode(media, options, f"limits-{depth}-{sampling}-{codec}")
                wanted_pixel = f"yuv{wanted_sampling}p" + (
                    f"{wanted_depth}le" if wanted_depth > 8 else ""
                )
                assert result.video["pix_fmt"].replace("yuvj", "yuv") == wanted_pixel
                if depth == 8:
                    for higher in ("10", "12"):
                        reject(media, replace(options, bit_depth=higher))
                if depth == 12 and codec != "hevc":
                    reject(media, replace(options, bit_depth="12"))
            if sampling == "420":
                reject(media, Options(purpose="archive", sampling="422"))
                reject(media, Options(purpose="archive", sampling="444"))
            if sampling == "422":
                reject(media, Options(purpose="archive", sampling="444"))
            if depth == 8:
                reject(media, Options(output="hdr", source_kind="hlg"))
    for codec in ("hevc", "avc", "av1"):
        _, result = encode(
            source,
            Options(
                purpose="archive",
                codec=codec,
                bit_depth="8",
                variant="fast" if codec == "av1" else "standard",
            ),
            "down-depth-" + codec,
        )
        assert result.pixel["components"][0]["bit_depth"] == 8
    print(
        "PASS source limits: 8/10/12-bit, 420/422/444, downward conversion and 10-bit software AVC",
        flush=True,
    )

    pcm = folder / "pcm.mkv"
    run(
        [
            "-i",
            base,
            "-f",
            "lavfi",
            "-i",
            "sine=r=44100:d=0.5",
            "-f",
            "lavfi",
            "-i",
            "anoisesrc=r=48000:d=0.5:seed=7",
            "-map",
            "0:v",
            "-map",
            "1:a",
            "-map",
            "2:a",
            "-map",
            "2:a",
            "-c:v",
            "copy",
            "-c:a:0",
            "pcm_s16le",
            "-c:a:1",
            "flac",
            "-sample_fmt:a:1",
            "s32",
            "-bits_per_raw_sample:a:1",
            "24",
            "-c:a:2",
            "pcm_s24le",
            "-ac:a:0",
            "1",
            "-ac:a:1",
            "6",
            "-ac:a:2",
            "2",
            pcm,
        ]
    )
    media = inspect_media(tools, pcm)
    assert flac_eligibility(media)[0], flac_eligibility(media)[1]
    assert public_media(media, tools)["flac_eligible"]
    path, result = encode(media, Options(purpose="archive", audio="flac"), "flac-multi")

    # Decode both sides to the same signed 32-bit PCM layout before hashing;
    # matching container/codec labels alone would not establish losslessness.
    def pcm_hash(file, index=0):
        return hashlib.sha256(
            run(
                [
                    "-i",
                    file,
                    "-map",
                    f"0:a:{index}",
                    "-c:a",
                    "pcm_s32le",
                    "-f",
                    "s32le",
                    "-",
                ]
            )
        ).digest()

    for index in range(3):
        assert pcm_hash(pcm, index) == pcm_hash(path, index), index
    reject(
        inspect_media(tools, path),
        Options(purpose="archive", audio="flac", container="mp4"),
    )
    for codec in ("aac", "pcm_f32le", "pcm_s32le"):
        fixture = folder / (codec + ".mkv")
        if codec == "pcm_s32le":
            run(
                [
                    "-i",
                    base,
                    "-f",
                    "lavfi",
                    "-i",
                    "anoisesrc=r=48000:d=0.5:seed=17",
                    "-map",
                    "0:v",
                    "-map",
                    "1:a",
                    "-c:v",
                    "copy",
                    "-c:a",
                    codec,
                    fixture,
                ]
            )
        else:
            run(
                [
                    "-i",
                    pcm,
                    "-map",
                    "0:v",
                    "-map",
                    "0:a:0",
                    "-c:v",
                    "copy",
                    "-c:a",
                    codec,
                    fixture,
                ]
            )
        media = inspect_media(tools, fixture)
        if codec == "pcm_s32le":
            path, _ = encode(media, Options(purpose="archive", audio="flac"), "flac-32")
            assert pcm_hash(fixture) == pcm_hash(path)
        else:
            assert not flac_eligibility(media)[0]
            reject(media, Options(purpose="archive", audio="flac"))
    mixed = folder / "mixed.mkv"
    run(
        ["-i", pcm, "-map", "0", "-c:v", "copy", "-c:a", "copy", "-c:a:1", "aac", mixed]
    )
    reject(inspect_media(tools, mixed), Options(purpose="archive", audio="flac"))
    reject(source, Options(purpose="archive", audio="flac"))
    print(
        "PASS FLAC sample hashes, 16/24/32-bit precision, multi-track/rate/channels, eligibility",
        flush=True,
    )
    assert not list(folder.glob(".video-converter-*"))
    print(
        f"Passed {counter} real encodes and format/audio rejection checks.", flush=True
    )
