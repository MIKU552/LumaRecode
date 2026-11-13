"""Exercise native CLI localization and component notices in the packaged executable."""

import argparse
import json
import subprocess
from pathlib import Path

root = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", type=Path, default=root / "dist/SelfContained/LumaRecode.exe")
    args = parser.parse_args()
    assert args.exe.is_file(), "Build the application first."
    count = 0
    for code, heading, missing in [
        ("zh", "位置参数", "未找到"),
        ("en", "positional arguments", "was not found"),
        ("ja", "位置引数", "見つかりません"),
        ("es", "argumentos posicionales", "No se encontró"),
    ]:

        def invoke(*words):
            result = subprocess.run(
                [str(args.exe.resolve()), "--lang", code, *words, "--no-settings"],
                capture_output=True,
                text=True,
                encoding="utf-8",
                timeout=30,
            )
            assert not result.stderr, result.stderr
            return result.returncode, result.stdout

        status, text = invoke("--help")
        assert status == 0 and heading in text and "--python" not in text
        count += 1
        status, text = invoke("license", "--text", "--json")
        value = json.loads(text)
        assert (
            status == 0
            and "LumaRecode" in value["component_terms"]
            and "Python" not in value["component_terms"]
        )
        count += 1
        status, text = invoke(
            "doctor", "--ffmpeg", "missing-ffmpeg.exe", "--accept-terms", "--json"
        )
        error = json.loads(text)
        assert (
            status == 3
            and error["data"]["code"] == "tool_unavailable"
            and missing in error["data"]["message"]
        )
        count += 1
        status, text = invoke("profiles", "--json")
        assert status == 0 and len(json.loads(text)["profiles"]) == 3
        count += 1
    print(f"PASS {count} packaged native CLI language and notice checks.")


if __name__ == "__main__":
    main()
