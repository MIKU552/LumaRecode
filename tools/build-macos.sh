#!/bin/sh
set -eu

task_configuration=${1:-Release}
task_mode=${2:-app}
case "$task_configuration:$task_mode" in Debug:app|Release:app|Debug:--dmg|Release:--dmg|Debug:--dmg-only|Release:--dmg-only) ;; *)
    echo 'Usage: build-macos.sh [Debug|Release] [--dmg|--dmg-only]' >&2
    exit 2
    ;;
esac
if [ "$(uname -s)" != Darwin ] || [ "$(uname -m)" != arm64 ]; then
    echo 'The macOS frontend builds on Apple Silicon with Xcode.' >&2
    exit 2
fi
task_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
task_build="$task_root/out/macos-build"
if [ "$task_mode" != --dmg-only ]; then
    xcodebuild -project "$task_root/macOS/LumaRecode.xcodeproj" -scheme LumaRecode \
        -configuration "$task_configuration" -destination 'generic/platform=macOS' \
        -derivedDataPath "$task_build" build
fi
task_app="$task_build/Build/Products/$task_configuration/LumaRecode.app"
codesign --verify --deep --strict "$task_app"
if [ "$task_mode" != app ]; then
    task_dmg="$task_build/LumaRecode-ARM64.dmg"
    task_package=$(mktemp -d "$task_build/package.XXXXXXXX")
    task_mount="$task_package/mounted"
    task_mounted=no
    cleanup_package() {
        if [ "$task_mounted" = yes ] && ! diskutil eject "$task_mount"; then
            return
        fi
        rm -rf -- "$task_package"
    }
    trap cleanup_package EXIT
    mkdir "$task_package/source" "$task_mount"
    ditto "$task_app" "$task_package/source/LumaRecode.app"
    ln -s /Applications "$task_package/source/Applications"
    task_image="$task_package/compressed.dmg"
    if ! diskutil image create from --format ULFO --volumeName LumaRecode "$task_package/source" "$task_image"; then
        task_image="$task_package/deflate.dmg"
        diskutil image create from --format UDZO --volumeName LumaRecode "$task_package/source" "$task_image"
    fi
    diskutil image attach --readOnly --mountPoint "$task_mount" "$task_image"
    task_mounted=yes
    codesign --verify --deep --strict "$task_mount/LumaRecode.app"
    diskutil eject "$task_mount"
    task_mounted=no
    mv -f "$task_image" "$task_dmg"
    printf '%s\n' "$task_dmg"
fi
printf '%s\n' "$task_app"
