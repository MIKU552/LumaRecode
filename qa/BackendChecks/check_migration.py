"""Compare native format policies and FFmpeg plans with the released v3.8.0 backend."""

import argparse
import itertools
import shutil
import subprocess
import sys
import tempfile
from dataclasses import asdict
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--sample", type=Path, action="append", default=[])
args = parser.parse_args()
root = Path(__file__).resolve().parents[2]
# Extract the signed release's reference into a disposable directory. Keeping
# it outside the source tree prevents retired Python code from being packaged.
reference = tempfile.TemporaryDirectory(prefix="LumaRecode-reference-")
atexit = __import__("atexit")
atexit.register(reference.cleanup)
ref = Path(reference.name)
(ref / "assets").mkdir()
for name in ["engine.py", "media.py", "metadata_helpers.py", "output_policy.py"]:
    data = subprocess.check_output(["git", "show", "v3.8.0:Worker/" + name], cwd=root)
    (ref / name).write_bytes(data)
(ref / "assets/recommended-presets.json").write_bytes(
    subprocess.check_output(
        ["git", "show", "v3.8.0:Worker/assets/recommended-presets.json"], cwd=root
    )
)
sys.path.insert(0, str(ref))
from engine import Options, build_command, resolve_format, validate
from media import Media, Tools, classify

sys.path.insert(0, str(root / "qa/BackendChecks"))
import native_backend as native

tools = Tools(shutil.which("ffmpeg"), shutil.which("ffprobe"))
count = 0
for sampling, depth, kind, scope in itertools.product(
    ["420", "422", "444", "gray", "rgb"],
    [8, 10, 12],
    ["sdr", "hlg", "pq", "log"],
    ["limited", "full"],
):
    shifts = {"420": (1, 1), "422": (1, 0), "444": (0, 0), "gray": (0, 0), "rgb": (0, 0)}
    w, h = shifts[sampling]
    pixel = {
        "flags": {"rgb": sampling == "rgb"},
        "nb_components": 1 if sampling == "gray" else 3,
        "components": [{"bit_depth": depth}] * (1 if sampling == "gray" else 3),
        "log2_chroma_w": w,
        "log2_chroma_h": h,
    }
    pix = (
        "gray" if sampling == "gray" else "gbrp" if sampling == "rgb" else "yuv" + sampling + "p"
    ) + (str(depth) + "le" if depth > 8 else "")
    video = {
        "index": 0,
        "codec_name": "hevc",
        "pix_fmt": pix,
        "width": 640,
        "height": 360,
        "avg_frame_rate": "30000/1001",
        "color_range": "tv" if scope == "limited" else "pc",
        "color_primaries": "bt709" if kind == "sdr" else "bt2020",
        "color_space": "gbr" if sampling == "rgb" else "bt709" if kind == "sdr" else "bt2020nc",
        "color_transfer": "smpte2084"
        if kind == "pq"
        else "arib-std-b67"
        if kind == "hlg"
        else "bt709",
    }
    m = Media(
        Path("C:/fixtures/source.mp4"),
        video,
        [],
        {},
        kind,
        "pocket3" if kind == "log" else None,
        "",
        3,
        [],
        pixel,
    )
    for purpose, codec, sample, bit in itertools.product(
        ["share", "archive"],
        ["hevc", "avc", "av1"],
        ["auto", "source", "420", "422", "444"],
        ["auto", "8", "10", "12"],
    ):
        o = Options(
            purpose=purpose,
            codec=codec,
            sampling=sample,
            bit_depth=bit,
            output="sdr" if kind == "sdr" else "hdr",
            transform="log-hdr" if kind == "log" else "none",
            profile="pocket3" if kind == "log" else None,
        )

        def old():
            try:
                validate(o, m)
                resolved = resolve_format(o, m)
                argv, colors = build_command(tools, m, o, Path("C:/fixtures/output" + o.suffix))
                argv[0] = argv[0].lower()
                return asdict(resolved), argv, colors
            except ValueError as e:
                return str(e)

        expected = old()
        try:
            actual = native.request("resolve", media=m, options=o)
            plan = native.request(
                "plan", media=m, options=o, output="C:/fixtures/output" + o.suffix
            )
            plan["argv"][0] = plan["argv"][0].lower()
            actual = (actual, plan["argv"], plan["colors"])
        except ValueError as e:
            actual = str(e)
        assert actual == expected, (sampling, depth, kind, scope, o, expected, actual)
        count += 1
print("PASS", count, "native/reference format-policy and exact FFmpeg-command comparisons.")
for tags in [
    {},
    {"com.xiaomi.record_log": 1},
    {"com.samsung.android.logvideo": True},
    {"gamma": "D-Log M", "model": "Osmo Pocket 3"},
    {"is_log": True},
    {"Gamma": "HLG", "gamma": "D-Log M", "model": "Pocket 3"},
]:
    for transfer in ["bt709", "arib-std-b67", "smpte2084", "unknown"]:
        for dji in [
            None,
            ("dvtm_PP-101.proto", 19),
            ("dvtm_ac206.proto", 19),
            ("dvtm_PP-101.proto", 9),
            ("dvtm_PP-101.proto", 0),
        ]:
            v = {"color_transfer": transfer}
            assert native.request("classify", video=v, tags=tags, dji=dji) == list(
                classify(v, tags, dji)
            )
            count += 1
for path in args.sample:
    from media import inspect_media

    m = inspect_media(tools, path)
    n = native.request("inspect", path=path)
    for key in ["kind", "profile", "evidence", "duration", "pixel", "video"]:
        assert n[key] == getattr(m, key), (path, key, n[key], getattr(m, key))
    count += 1
print("PASS", count, "total parity checks, including supplied camera samples.")
