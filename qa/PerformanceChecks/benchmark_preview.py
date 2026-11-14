"""Measure native viewport rendering; source inspection/indexing precedes the timer."""

import argparse
import json
import statistics
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "qa/BackendChecks"))
import native_backend as native

parser = argparse.ArgumentParser(description=__doc__)
for kind in ("sdr", "hlg", "log"):
    parser.add_argument("--" + kind, type=Path)
parser.add_argument("--profile", default="pocket3")
parser.add_argument("--frames", default="0,30,60,90")
parser.add_argument("--repeat", type=int, default=3)
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
if args.repeat < 1 or not any(getattr(args, kind) for kind in ("sdr", "hlg", "log")):
    parser.error("Provide at least one source and a positive repeat count.")
frames = [int(value) for value in args.frames.split(",")]
result = {
    "ffmpeg": subprocess.check_output(["ffmpeg", "-version"], text=True).splitlines()[
        0
    ],
    "method": "Sequential viewport requests; probe and frame indexing excluded; managed allocation includes this backend process only.",
    "sources": {},
}
for kind in ("sdr", "hlg", "log"):
    source = getattr(args, kind)
    if source is None:
        continue
    samples = []
    for trial in range(args.repeat):
        data = native.request(
            "preview-benchmark",
            path=source.resolve(),
            frames=frames,
            source_kind=kind,
            profile=args.profile if kind == "log" else None,
        )
        samples.extend(dict(trial=trial + 1, **sample) for sample in data)
    result["sources"][kind] = {
        "file": source.name,
        "median_ms": statistics.median(sample["milliseconds"] for sample in samples),
        "median_managed_allocated_bytes": statistics.median(
            sample["managed_allocated_bytes"] for sample in samples
        ),
        "samples": samples,
    }
args.output.write_text(
    json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
)
print(
    json.dumps(
        {kind: value["median_ms"] for kind, value in result["sources"].items()},
        indent=2,
    )
)
