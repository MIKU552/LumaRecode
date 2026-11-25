#!/bin/sh
set -eu

task_configuration=${1:-Release}
task_mode=${2:-app}
case "$task_configuration:$task_mode" in Debug:app|Release:app|Debug:--dmg|Release:--dmg|Debug:--dmg-only|Release:--dmg-only|Release:--dmg-full-tools) ;; *)
    echo 'Usage: build-macos.sh [Debug|Release] [--dmg|--dmg-only|--dmg-full-tools]' >&2
    exit 2
    ;;
esac
if [ "$(uname -s)" != Darwin ] || [ "$(uname -m)" != arm64 ]; then
    echo 'The macOS frontend builds on Apple Silicon with Xcode.' >&2
    exit 2
fi
task_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
task_build="$task_root/out/macos-build"
if [ "$task_mode" = app ] || [ "$task_mode" = --dmg ]; then
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
    if [ "$task_mode" = --dmg-full-tools ]; then
        task_dmg="$task_build/LumaRecode-ARM64-FFmpeg-Full.dmg"
        task_staged="$task_package/source/LumaRecode.app"
        task_tools="$task_staged/Contents/Resources/FFmpeg"
        if [ -n "${LUMA_STAGED_FFMPEG:-}" ]; then
            ditto "$LUMA_STAGED_FFMPEG" "$task_tools"
        else
            task_sources="$task_package/FFmpeg-Full-Sources"
            python3 "$task_root/tools/stage-macos-ffmpeg.py" "$task_tools" "$task_sources"
            brew ruby "$task_sources/Packaging/collect-brew-sources.rb"
            tar -czf "$task_package/FFmpeg-Full-Sources.tar.gz" -C "$task_package" FFmpeg-Full-Sources
        fi
        codesign --force --sign - "$task_staged"
    fi
    ln -s /Applications "$task_package/source/Applications"
    task_image="$task_package/compressed.dmg"
    diskutil image create from --format UDZO --volumeName LumaRecode "$task_package/source" "$task_image"
    diskutil image attach --readOnly --mountPoint "$task_mount" "$task_image"
    task_mounted=yes
    codesign --verify --deep --strict "$task_mount/LumaRecode.app"
    if [ "$task_mode" = --dmg-full-tools ]; then
        task_tools="$task_mount/LumaRecode.app/Contents/Resources/FFmpeg/bin"
        env -i PATH=/usr/bin:/bin "$task_tools/ffmpeg" -hide_banner -filters | grep -w libplacebo
        env -i PATH=/usr/bin:/bin "$task_tools/ffprobe" -version
        env -i PATH=/usr/bin:/bin "$task_mount/LumaRecode.app/Contents/MacOS/LumaRecode" doctor
        if [ "${LUMA_VALIDATE_FULL_FFMPEG:-0}" = 1 ]; then
            python3 "$task_root/qa/RuntimeChecks/check_macos_host.py" --app "$task_mount/LumaRecode.app" --bundled-hdr10plus
        fi
        if [ -f "$task_package/FFmpeg-Full-Sources.tar.gz" ]; then
            mv -f "$task_package/FFmpeg-Full-Sources.tar.gz" "$task_build/FFmpeg-Full-Sources.tar.gz"
        fi
    fi
    diskutil eject "$task_mount"
    task_mounted=no
    mv -f "$task_image" "$task_dmg"
    printf '%s\n' "$task_dmg"
fi
printf '%s\n' "$task_app"
