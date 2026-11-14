"""Validate documentation links, parallel language sections and fallback labels."""

import html
import re
import subprocess
from collections import Counter
from pathlib import Path
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[2]
LANGUAGES = ("zh-CN", "en", "ja", "es")
FENCE = chr(96) * 3
names = subprocess.check_output(
    ["git", "ls-files", "--cached", "--others", "--exclude-standard", "*.md"],
    cwd=ROOT,
    text=True,
    encoding="utf-8",
).splitlines()
documents = {
    name: (ROOT / name).read_text(encoding="utf-8")
    for name in names
    if not name.startswith("licenses/") and (ROOT / name).exists()
}
localized = {
    "README.md",
    *(f"docs/readme/{lang}.md" for lang in LANGUAGES if lang != "zh-CN"),
    *(f"docs/cli/{lang}.md" for lang in LANGUAGES),
}
bilingual = set(documents) - localized
failures = []


def prose(text):
    return re.sub(
        "^" + FENCE + r"[^\n]*\n.*?^" + FENCE + r"\s*$", "", text, flags=re.M | re.S
    )


def anchors(text):
    """GitHub-style heading slugs, including duplicate-heading suffixes."""
    counts = Counter()
    result = set(re.findall(r'<a\b[^>]*(?:id|name)="([^"]+)"', text))
    for heading in re.findall(r"^#{1,6}\s+(.+?)\s*#*\s*$", prose(text), re.M):
        heading = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", heading)
        heading = html.unescape(re.sub(r"<[^>]*>", "", heading)).lower()
        slug = "".join(c for c in heading if c.isalnum() or c in " _-").replace(
            " ", "-"
        )
        result.add(slug if not counts[slug] else f"{slug}-{counts[slug]}")
        counts[slug] += 1
    return result


def structure(text):
    """Compare heading depth, table coverage and executable example coverage."""
    plain = prose(text)
    headings = tuple(len(v) for v in re.findall(r"^(#{2,6}) ", plain, re.M))
    tables, rows = [], 0
    for line in plain.splitlines() + [""]:
        if line.startswith("|"):
            rows += 1
        elif rows:
            tables.append(rows)
            rows = 0
    fences = tuple(re.findall("^" + FENCE + r"([^\n]*)", text, re.M)[::2])
    return headings, tuple(tables), fences


def require(condition, message):
    if not condition:
        failures.append(message)


require(localized <= set(documents), "Missing a README or CLI translation")
require(
    {n for n in documents if "/" not in n and n.startswith("README")} == {"README.md"},
    "README translations belong in docs/readme/",
)
for name in sorted(bilingual):
    text = documents[name]
    require(
        re.findall(r"^## (.+)$", prose(text), re.M) == ["简体中文", "English"],
        f"{name}: expected complete Chinese and English sections",
    )
    if "\n## 简体中文\n" not in text or "\n## English\n" not in text:
        continue
    chinese, english = text.split("\n## 简体中文\n", 1)[1].split("\n## English\n", 1)
    require(
        structure(chinese) == structure(english),
        f"{name}: translated section, table or example structure differs",
    )

for group in (
    ["README.md", *(f"docs/readme/{lang}.md" for lang in LANGUAGES[1:])],
    [f"docs/cli/{lang}.md" for lang in LANGUAGES],
):
    reference = structure(documents[group[0]])
    for name in group[1:]:
        require(
            structure(documents[name]) == reference,
            f"{name}: translated README/CLI structure differs",
        )

anchor_cache = {name: anchors(text) for name, text in documents.items()}
links, fallbacks = 0, 0
for name, text in sorted(documents.items()):
    plain = prose(text)
    targets = re.findall(r"!?\[([^\]]*)\]\(([^)]+)\)", plain)
    targets += [("", v) for v in re.findall(r'<img\b[^>]*\bsrc="([^"]+)"', plain)]
    for label, target in targets:
        target = target.split(' "', 1)[0].strip("<>")
        url = urlsplit(target)
        if url.scheme or url.netloc:
            continue
        path = (
            ((ROOT / name).parent / unquote(url.path)).resolve()
            if url.path
            else ROOT / name
        )
        links += 1
        if not path.is_relative_to(ROOT) or not path.exists():
            failures.append(f"{name}: broken link {target}")
            continue
        resolved = path.relative_to(ROOT).as_posix()
        if url.fragment and resolved in anchor_cache:
            require(
                unquote(url.fragment) in anchor_cache[resolved],
                f"{name}: missing anchor {target}",
            )
        language = Path(name).stem
        if name.startswith(("docs/readme/", "docs/cli/")) and language in ("ja", "es"):
            if resolved in bilingual:
                marker = "中国語・英語" if language == "ja" else "chino e inglés"
                require(
                    marker in label, f"{name}: missing bilingual label for {target}"
                )
                require(
                    url.fragment == "english",
                    f"{name}: fallback should open English: {target}",
                )
                fallbacks += 1
            elif resolved in (
                "LICENSE",
                "NOTICE",
                "docs/readme/en.md",
                "docs/cli/en.md",
            ):
                marker = "英語" if language == "ja" else "inglés"
                require(
                    label == "English" or marker in label,
                    f"{name}: missing English label for {target}",
                )
                fallbacks += 1

if failures:
    raise SystemExit("Documentation checks failed:\n" + "\n".join(failures))
print(
    f"Documentation checked: {links} local links/anchors, "
    f"{len(bilingual)} parallel bilingual pages, "
    f"{len(localized)} localized guides, {fallbacks} annotated fallbacks."
)
