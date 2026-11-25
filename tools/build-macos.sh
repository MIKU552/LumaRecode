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
        python3 "$task_root/tools/stage-macos-ffmpeg.py" "$task_tools" "$task_package/FFmpeg-Full-Sources"
        codesign --force --sign - "$task_staged"
        tar -czf "$task_package/FFmpeg-Full-Sources.tar.gz" -C "$task_package" FFmpeg-Full-Sources
    fi
    ln -s /Applications "$task_package/source/Applications"
    task_image="$task_package/compressed.dmg"
    if [ "$task_mode" = --dmg-full-tools ]; then
        # Size a writable volume explicitly for the larger bundle before
        # converting it to a compressed macOS disk image.
        task_writable="$task_package/writable.dmg"
        task_kib=$(du -sk "$task_package/source" | awk '{print $1}')
        diskutil image create blank --size "$((task_kib + task_kib / 10 + 65536))KiB" \
            --volumeName LumaRecode "$task_writable"
        diskutil image attach --mountPoint "$task_mount" "$task_writable"
        task_mounted=yes
        ditto "$task_package/source" "$task_mount"
        diskutil eject "$task_mount"
        task_mounted=no
        mkdir -p "$task_mount"
        diskutil image create from --format ULFO "$task_writable" "$task_image"
    else
        diskutil image create from --format UDZO --volumeName LumaRecode "$task_package/source" "$task_image"
    fi
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
        mv -f "$task_package/FFmpeg-Full-Sources.tar.gz" "$task_build/FFmpeg-Full-Sources.tar.gz"
    fi
    diskutil eject "$task_mount"
    task_mounted=no
    mv -f "$task_image" "$task_dmg"
    printf '%s\n' "$task_dmg"
fi
printf '%s\n' "$task_app"
