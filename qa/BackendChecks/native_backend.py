"""Python test adapter for the C# backend checks executable."""

import atexit
import json
import shutil
import subprocess
from dataclasses import asdict, dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "Assets"
_server = None


def request(action, **values):
    global _server
    if _server is None:
        _server = subprocess.Popen(
            [
                shutil.which("dotnet"),
                str(ROOT / "qa/BackendChecks/bin/Release/net11.0/BackendChecks.dll"),
                "--server",
            ],
            cwd=ROOT,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            text=True,
            encoding="utf-8",
        )

    def encode(value):
        if hasattr(value, "__dataclass_fields__"):
            return asdict(value)
        if isinstance(value, Path):
            return str(value)
        raise TypeError(type(value).__name__)

    _server.stdin.write(json.dumps(dict(action=action, **values), default=encode) + "\n")
    _server.stdin.flush()
    result = json.loads(_server.stdout.readline())
    if not result["ok"]:
        raise ValueError(result["error"])
    return result["data"]


@atexit.register
def close():
    if _server is not None:
        _server.stdin.close()
        try:
            _server.wait(timeout=5)
        except subprocess.TimeoutExpired:
            _server.kill()
            _server.wait()


@dataclass
class Tools:
    ffmpeg: str
    ffprobe: str


@dataclass
class Media:
    path: Path
    video: dict
    frames: list
    tags: dict
    kind: str
    profile: str | None
    evidence: str
    duration: float
    streams: list
    pixel: dict


@dataclass(frozen=True)
class Options:
    purpose: str = "share"
    codec: str = "hevc"
    output: str = "sdr"
    transform: str = "none"
    profile: str | None = None
    sampling: str = "auto"
    variant: str = "standard"
    audio: str = "auto"
    source_kind: str = "auto"
    cpu_decode: bool = False
    container: str = "auto"
    color_range: str = "auto"
    bit_depth: str = "auto"

    @property
    def suffix(self):
        return "." + (
            ("mkv" if self.purpose == "archive" or self.audio == "flac" else "mp4")
            if self.container == "auto"
            else self.container
        )

    @property
    def pixel_format(self):
        return ("gray" if self.sampling == "gray" else f"yuv{self.sampling}p") + (
            f"{self.bit_depth}le" if int(self.bit_depth) > 8 else ""
        )


def _media(data):
    data["path"] = Path(data["path"])
    return Media(**data)


def inspect_media(tools, path):
    return _media(request("inspect", path=path))


def resolve_format(options, media):
    return Options(**request("resolve", options=options, media=media))


def validate(options, media):
    return request("validate", options=options, media=media)


def public_media(media, tools):
    return request("public", media=media)


def flac_eligibility(media):
    return tuple(request("flac", media=media))


def build_command(tools, media, options, destination):
    result = request("plan", media=media, options=options, output=destination)
    return result["argv"], result["colors"]


class Job:
    def run(self, tools, media, options, output, **kwargs):
        return _media(
            request(
                "run",
                media=media,
                options=options,
                output=output,
                seconds=kwargs.get("seconds"),
                overwrite=kwargs.get("overwrite", False),
            )
        )
