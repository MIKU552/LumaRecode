# SPDX-License-Identifier: MIT
"""Reproduce the project's camera Log HLG tables using stdlib only.

This is the project's own transfer-function implementation, not vendor code.
Technical references and licensing scope: ../Assets/THIRD_PARTY.md.
The independently licensed OpenOSV LUT is not generated or changed here.
"""

import argparse
import hashlib
import json
import tempfile
from math import expm1, log, sqrt
from pathlib import Path

SIZE = 65
HLG_A = 0.17883277
HLG_B = 1 - 4 * HLG_A
HLG_C = 0.5 - HLG_A * log(4 * HLG_A)
# Place 18% scene gray at HLG signal 0.38 in the square-root branch.
# SHOULDER is a scene-linear onset after that exposure scaling.
GRAY_SCALE = 0.38**2 / (3 * 0.18)
SHOULDER = 0.45
ASSETS = Path(__file__).resolve().parents[1] / "Assets"


def linear_milog(code):
    """Decode normalized full-range Mi-Log signal into scene-linear light."""
    black = -0.09023729
    toe_scale = 18.10531998
    if code < toe_scale * (0.01974185 - black) ** 2:
        return sqrt(code / toe_scale) + black
    return 2 ** ((code - 0.67291850) / 0.09271529) - 0.01384578


def linear_samsung_log(code):
    """Decode normalized Samsung Log signal into scene-linear light."""
    if code < 0.206561909:
        return 0.016904 - 10 ** ((code + 0.24597) / -0.20942)
    return 10 ** ((code - 0.720504856) / 0.258984868) - 0.0003645


def linear_apple_log(code):
    """Decode the transfer curve shared by Apple Log and Apple Log 2."""
    threshold = 47.28711236 * (0.01 + 0.05641088) ** 2
    if code < threshold:
        return sqrt(max(0, code) / 47.28711236) - 0.05641088
    return 2 ** ((code - 0.69336945) / 0.08550479) - 0.00964052


def invert3(matrix):
    a, b, c = matrix
    cofactors = [
        [
            b[1] * c[2] - b[2] * c[1],
            b[2] * c[0] - b[0] * c[2],
            b[0] * c[1] - b[1] * c[0],
        ],
        [
            a[2] * c[1] - a[1] * c[2],
            a[0] * c[2] - a[2] * c[0],
            a[1] * c[0] - a[0] * c[1],
        ],
        [
            a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0],
        ],
    ]
    determinant = sum(a[i] * cofactors[0][i] for i in range(3))
    return [[cofactors[j][i] / determinant for j in range(3)] for i in range(3)]


def multiply(matrix, vector):
    return tuple(sum(a * b for a, b in zip(row, vector)) for row in matrix)


def rgb_to_xyz(primaries):
    """Derive an RGB-to-XYZ matrix from xy primaries and a D65 white."""
    columns = [(x / y, 1, (1 - x - y) / y) for x, y in primaries]
    matrix = list(zip(*columns))
    scale = multiply(
        invert3(matrix), (0.3127 / 0.3290, 1, (1 - 0.3127 - 0.3290) / 0.3290)
    )
    return [[value * scale[j] for j, value in enumerate(row)] for row in matrix]


def apple_wide_to_bt2020(rgb):
    """Convert linear Apple Wide Gamut to BT.2020, compressing negative channels toward gray."""
    xyz = multiply(APPLE_WIDE_TO_XYZ, rgb)
    converted = multiply(XYZ_TO_BT2020, xyz)
    # Both spaces use D65. Blend toward equal-energy RGB at constant BT.2020
    # luminance when the wider source lies outside the target gamut.
    luminance = max(0, sum(a * b for a, b in zip(converted, (0.2627, 0.6780, 0.0593))))
    minimum = min(converted)
    saturation = (
        luminance / (luminance - minimum) if minimum < 0 and luminance > 0 else 1
    )
    return tuple(
        max(0, luminance + saturation * (value - luminance)) for value in converted
    )


APPLE_WIDE_TO_XYZ = rgb_to_xyz(((0.725, 0.301), (0.221, 0.814), (0.068, -0.076)))
XYZ_TO_BT2020 = invert3(rgb_to_xyz(((0.708, 0.292), (0.170, 0.797), (0.131, 0.046))))


def hlg_oetf(value):
    """Apply the Rec.2100 HLG OETF to nonnegative scene-linear light."""
    if value <= 1 / 12:
        return sqrt(3 * value)
    return HLG_A * log(12 * value - HLG_B) + HLG_C


def make_hlg_cube(path, title, decode_log, convert_gamut=None):
    levels = [max(0, decode_log(i / (SIZE - 1))) * GRAY_SCALE for i in range(SIZE)]
    with path.open("w", encoding="ascii", newline="\n") as cube:
        cube.write(f'TITLE "{title}"\n')
        cube.write(
            f"LUT_3D_SIZE {SIZE}\nDOMAIN_MIN 0.0 0.0 0.0\nDOMAIN_MAX 1.0 1.0 1.0\n"
        )
        # .cube ordering has red varying fastest. Scale all RGB components
        # together in the highlight shoulder to retain their linear ratios.
        for blue in levels:
            for green in levels:
                for red in levels:
                    rgb = (red, green, blue)
                    if convert_gamut:
                        rgb = convert_gamut(rgb)
                    peak = max(rgb)
                    scale = 1.0
                    if peak > SHOULDER:
                        headroom = 1 - SHOULDER
                        rolled = SHOULDER - headroom * expm1(
                            -(peak - SHOULDER) / headroom
                        )
                        scale = rolled / peak
                    values = (hlg_oetf(channel * scale) for channel in rgb)
                    cube.write(" ".join(f"{value:.8f}" for value in values) + "\n")


def generate(folder):
    folder.mkdir(parents=True, exist_ok=True)
    profiles = [
        (
            "REDMI_K90_MiLog_to_Rec2100_HLG.cube",
            "REDMI K90 Mi-Log to Rec.2100 HLG",
            linear_milog,
            None,
        ),
        (
            "Samsung_Log_to_Rec2100_HLG.cube",
            "Samsung Log to Rec.2100 HLG",
            linear_samsung_log,
            None,
        ),
        (
            "Apple_Log_to_Rec2100_HLG.cube",
            "Apple Log BT.2020 to Rec.2100 HLG",
            linear_apple_log,
            None,
        ),
        (
            "Apple_Log_2_to_Rec2100_HLG.cube",
            "Apple Log 2 Apple Wide Gamut to Rec.2100 HLG",
            linear_apple_log,
            apple_wide_to_bt2020,
        ),
    ]
    for name, title, decoder, gamut in profiles:
        path = folder / name
        make_hlg_cube(path, title, decoder, gamut)
        yield path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, default=ASSETS)
    parser.add_argument(
        "--check",
        action="store_true",
        help="Compare temporary output with committed tables.",
    )
    options = parser.parse_args()
    if options.check:
        with tempfile.TemporaryDirectory(prefix="lumarecode-lut-check-") as temporary:
            for path in generate(Path(temporary)):
                content = path.read_bytes()
                if content != (options.output_dir / path.name).read_bytes():
                    raise SystemExit("Generated LUT differs: " + path.name)
                print(
                    json.dumps(
                        {
                            "file": path.name,
                            "matches": True,
                            "sha256": hashlib.sha256(content).hexdigest(),
                        }
                    )
                )
    else:
        for path in generate(options.output_dir):
            print(path)


if __name__ == "__main__":
    main()
