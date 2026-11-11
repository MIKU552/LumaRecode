# SPDX-License-Identifier: Apache-2.0
"""Reproduce the project's Mi-Log and Samsung Log HLG tables using stdlib only.

This is the project's own transfer-function implementation, not vendor code.
Technical references and licensing scope: ../assets/THIRD_PARTY.md.
The independently licensed OpenOSV LUT is not generated or changed here.
"""
import argparse
import hashlib
import json
from math import expm1, log, sqrt
from pathlib import Path
import tempfile

SIZE = 65
HLG_A = 0.17883277
HLG_B = 1 - 4 * HLG_A
HLG_C = 0.5 - HLG_A * log(4 * HLG_A)
GRAY_SCALE = 0.38 ** 2 / (3 * 0.18)
SHOULDER = 0.45
ASSETS = Path(__file__).resolve().parents[1] / 'assets'


def linear_milog(code):
    black = -0.09023729
    toe_scale = 18.10531998
    if code < toe_scale * (0.01974185 - black) ** 2:
        return sqrt(code / toe_scale) + black
    return 2 ** ((code - 0.67291850) / 0.09271529) - 0.01384578


def linear_samsung_log(code):
    if code < 0.206561909:
        return 0.016904 - 10 ** ((code + 0.24597) / -0.20942)
    return 10 ** ((code - 0.720504856) / 0.258984868) - 0.0003645


def hlg_oetf(value):
    if value <= 1 / 12:
        return sqrt(3 * value)
    return HLG_A * log(12 * value - HLG_B) + HLG_C


def make_hlg_cube(path, title, decode_log):
    levels = [max(0, decode_log(i / (SIZE - 1))) * GRAY_SCALE for i in range(SIZE)]
    with path.open('w', encoding='ascii', newline='\n') as cube:
        cube.write(f'TITLE "{title}"\n')
        cube.write(f'LUT_3D_SIZE {SIZE}\nDOMAIN_MIN 0.0 0.0 0.0\nDOMAIN_MAX 1.0 1.0 1.0\n')
        for blue in levels:
            for green in levels:
                for red in levels:
                    peak = max(red, green, blue)
                    scale = 1.0
                    if peak > SHOULDER:
                        headroom = 1 - SHOULDER
                        rolled = SHOULDER - headroom * expm1(-(peak - SHOULDER) / headroom)
                        scale = rolled / peak
                    values = (hlg_oetf(channel * scale) for channel in (red, green, blue))
                    cube.write(' '.join(f'{value:.8f}' for value in values) + '\n')


def generate(folder):
    folder.mkdir(parents=True, exist_ok=True)
    profiles = [
        ('REDMI_K90_MiLog_to_Rec2100_HLG.cube', 'REDMI K90 Mi-Log to Rec.2100 HLG', linear_milog),
        ('Samsung_Log_to_Rec2100_HLG.cube', 'Samsung Log to Rec.2100 HLG', linear_samsung_log),
    ]
    for name, title, decoder in profiles:
        path = folder / name
        make_hlg_cube(path, title, decoder)
        yield path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir', type=Path, default=ASSETS)
    parser.add_argument('--check', action='store_true', help='Compare temporary output with committed tables.')
    options = parser.parse_args()
    if options.check:
        with tempfile.TemporaryDirectory(prefix='frameflow-lut-check-') as temporary:
            for path in generate(Path(temporary)):
                content = path.read_bytes()
                if content != (options.output_dir / path.name).read_bytes():
                    raise SystemExit('Generated LUT differs: ' + path.name)
                print(json.dumps({'file': path.name, 'matches': True, 'sha256': hashlib.sha256(content).hexdigest()}))
    else:
        for path in generate(options.output_dir):
            print(path)


if __name__ == '__main__':
    main()
