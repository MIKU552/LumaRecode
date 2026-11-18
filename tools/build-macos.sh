#!/bin/sh
set -eu

task_configuration=${1:-Release}
task_mode=${2:-app}
case "$task_configuration:$task_mode" in Debug:app|Release:app|Debug:--dmg|Release:--dmg) ;; *)
    echo 'Usage: build-macos.sh [Debug|Release] [--dmg]' >&2
    exit 2
    ;;
esac
if [ "$(uname -s)" != Darwin ] || [ "$(uname -m)" != arm64 ]; then
    echo 'The macOS frontend builds on Apple Silicon with Xcode.' >&2
    exit 2
fi
task_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
task_build="$task_root/out/macos-build"
xcodebuild -project "$task_root/macOS/LumaRecode.xcodeproj" -scheme LumaRecode \
    -configuration "$task_configuration" -destination 'generic/platform=macOS' \
    -derivedDataPath "$task_build" build
task_app="$task_build/Build/Products/$task_configuration/LumaRecode.app"
codesign --verify --deep --strict "$task_app"
if [ "$task_mode" = --dmg ]; then
    task_dmg="$task_build/LumaRecode-ARM64.dmg"
    hdiutil create -ov -volname LumaRecode -srcfolder "$task_app" -format UDZO "$task_dmg"
    hdiutil verify "$task_dmg"
    printf '%s\n' "$task_dmg"
fi
printf '%s\n' "$task_app"
