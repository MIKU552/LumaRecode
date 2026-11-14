"""Validate local links in maintained project documentation."""

import re
import subprocess
from pathlib import Path
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[2]
files = [
    ROOT / name
    for name in subprocess.check_output(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "*.md"],
        cwd=ROOT,
        text=True,
        encoding="utf-8",
    ).splitlines()
    if not name.startswith("licenses/") and (ROOT / name).exists()
]
failures = []
links = 0
for file in sorted(files):
    text = re.sub(r"```.*?```", "", file.read_text(encoding="utf-8"), flags=re.S)
    targets = re.findall(r"!?\[[^\]]*\]\(([^)]+)\)", text)
    targets += re.findall(r'<img\b[^>]*\bsrc="([^"]+)"', text)
    for target in targets:
        target = target.split(' "', 1)[0].strip("<>")
        url = urlsplit(target)
        if url.scheme or url.netloc or not url.path:
            continue
        links += 1
        path = (file.parent / unquote(url.path)).resolve()
        if not path.is_relative_to(ROOT) or not path.exists():
            failures.append(f"{file.relative_to(ROOT)}: {target}")
if failures:
    raise SystemExit("Broken documentation links:\n" + "\n".join(failures))
print(f"Documentation links checked: {links}")
