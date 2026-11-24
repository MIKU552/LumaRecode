"""Relocate the Homebrew ffmpeg-full tools and collect their redistribution files."""

import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys


def run(*args: str | Path) -> str:
    return subprocess.check_output(args, text=True).strip()


def dependencies(binary: Path) -> list[str]:
    return [line.strip().split(" (", 1)[0] for line in run("otool", "-L", binary).splitlines()[1:]]


def install_id(binary: Path) -> str | None:
    lines = run("otool", "-D", binary).splitlines()
    return lines[1] if len(lines) > 1 else None


class Bundle:
    def __init__(self, target: Path):
        self.target = target
        self.cellar = Path(run("brew", "--cellar")).resolve()
        self.prefix = Path(run("brew", "--prefix"))
        self.libraries = target / "lib"
        self.libraries.mkdir(parents=True)
        self.copies: dict[Path, Path] = {}
        self.formulae: dict[str, Path] = {}

    def copy(self, source: Path, target: Path) -> None:
        source = source.resolve(strict=True)
        if source in self.copies:
            if self.copies[source] != target:
                raise ValueError(f"Conflicting destinations for {source}")
            return
        if target.exists():
            raise ValueError(f"Conflicting library name: {target.name}")
        name, version, *_ = source.relative_to(self.cellar).parts
        keg = self.cellar / name / version
        if self.formulae.setdefault(name, keg) != keg:
            raise ValueError(f"Multiple linked versions of {name}")
        shutil.copy2(source, target)
        target.chmod(0o755)
        self.copies[source] = target
        identity = install_id(source)
        changes = ["-id", f"@loader_path/{target.name}"] if identity else []
        for dependency in dependencies(source):
            if dependency == identity or dependency.startswith(("/usr/lib/", "/System/Library/")):
                continue
            path = self.resolve(source, dependency)
            resolved = path.resolve(strict=True)
            library = self.libraries / resolved.name
            self.copy(resolved, library)
            replacement = "@loader_path/" + os.path.relpath(library, target.parent)
            if dependency != replacement:
                changes.extend(("-change", dependency, replacement))
        if changes:
            run("install_name_tool", *changes, target)
        run("codesign", "--force", "--sign", "-", target)

    def resolve(self, source: Path, dependency: str) -> Path:
        if dependency.startswith("@loader_path/"):
            return source.parent / dependency.removeprefix("@loader_path/")
        if dependency.startswith("@rpath/"):
            name = dependency.removeprefix("@rpath/")
            load_commands = run("otool", "-l", source)
            search = [Path(item.replace("@loader_path", str(source.parent)))
                      for item in re.findall(r"cmd LC_RPATH\s+cmdsize \d+\s+path (.+?) \(offset", load_commands)]
            candidates = [root / name for root in [*search, source.parent, self.prefix / "lib"]]
            for path in candidates:
                if path.is_file():
                    return path
        if dependency.startswith(str(self.prefix) + "/"):
            return Path(dependency)
        raise ValueError(f"Unresolved dependency {dependency} in {source}")


def main() -> None:
    target, sources = (Path(arg).resolve() for arg in sys.argv[1:])
    bundle = Bundle(target)
    bins = target / "bin"
    bins.mkdir()
    ffmpeg = Path(run("brew", "--prefix", "ffmpeg-full"))
    molten = Path(run("brew", "--prefix", "molten-vk"))
    for tool, name in (("ffmpeg", "ffmpeg-bin"), ("ffprobe", "ffprobe")):
        bundle.copy(ffmpeg / "bin" / tool, bins / name)
    driver = (molten / "lib" / "libMoltenVK.dylib").resolve(strict=True)
    bundle.copy(driver, bundle.libraries / driver.name)
    icd = json.loads((molten / "etc/vulkan/icd.d/MoltenVK_icd.json").read_text())
    icd["ICD"]["library_path"] = "lib/" + driver.name
    (target / "MoltenVK_icd.json").write_text(json.dumps(icd, indent=2) + "\n")
    launcher = bins / "ffmpeg"
    launcher.write_text(
        '#!/bin/sh\nset -eu\n'
        'bin=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)\n'
        'export VK_DRIVER_FILES="${VK_DRIVER_FILES:-$bin/../MoltenVK_icd.json}"\n'
        'exec "$bin/ffmpeg-bin" "$@"\n'
    )
    launcher.chmod(0o755)
    if not re.search(r"\blibplacebo\b", run(launcher, "-hide_banner", "-filters")):
        raise ValueError("ffmpeg-full is missing libplacebo")
    config = run(launcher, "-version")
    if "--enable-nonfree" in config or "--enable-gpl" not in config:
        raise ValueError("Expected the redistributable GPL build of ffmpeg-full")
    run(bins / "ffprobe", "-version")

    sources.mkdir(parents=True)
    scripts = sources / "Packaging"
    scripts.mkdir()
    for script in (Path(__file__), Path(__file__).with_name("collect-brew-sources.rb")):
        shutil.copy2(script, scripts)
    (target / "BUILDINFO.txt").write_text(config + "\n\n" + "\n".join(
        f"{name} {keg.name}" for name, keg in sorted(bundle.formulae.items())
    ) + "\n")
    recipes = {}
    notices = target / "Licenses"
    for name, keg in sorted(bundle.formulae.items()):
        [recipe] = (keg / ".brew").glob("*.rb")
        recipes[name] = str(recipe)
        destination = notices / name
        destination.mkdir(parents=True)
        for file in keg.iterdir():
            if file.is_file() and file.name.upper().startswith(("LICENSE", "COPYING", "COPYRIGHT", "NOTICE", "AUTHORS")):
                shutil.copy2(file, destination)
    # Homebrew strips custom environment variables. The input contains local
    # paths and is removed before creating the published source archive.
    recipe_list = scripts / "installed-recipes.json"
    recipe_list.write_text(json.dumps(recipes))
    try:
        subprocess.run(["brew", "ruby", str(scripts / "collect-brew-sources.rb")], check=True)
    finally:
        recipe_list.unlink()
    shutil.copy2(target / "BUILDINFO.txt", sources)


if __name__ == "__main__":
    main()
