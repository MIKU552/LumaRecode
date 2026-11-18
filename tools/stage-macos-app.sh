#!/bin/sh
set -eu

task_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
# Xcode started from Finder does not inherit the user's interactive shell PATH.
export PATH="$PATH:${CARGO_HOME:-$HOME/.cargo}/bin:/opt/homebrew/bin:/usr/local/bin"
sh "$task_root/tools/build-native-core.sh" "$CONFIGURATION" --build-only
task_configuration=$(printf '%s' "$CONFIGURATION" | tr '[:upper:]' '[:lower:]')
task_library="$task_root/out/native-arm64-$task_configuration/engine/libLumaRecodeCore.dylib"
task_frameworks="$TARGET_BUILD_DIR/$FRAMEWORKS_FOLDER_PATH"
task_resources="$TARGET_BUILD_DIR/$UNLOCALIZED_RESOURCES_FOLDER_PATH"
mkdir -p "$task_frameworks" "$task_resources/Media/OpenOSV" "$task_resources/Licenses"
cp "$task_library" "$task_frameworks/"

cp "$task_root"/Assets/*.cube "$task_root"/Assets/*-presets.json "$task_root/Assets/license-manifest.json" "$task_resources/Media/"
cp "$task_root/Assets/OpenOSV/LICENSE" "$task_root/Assets/OpenOSV/NOTICE" "$task_resources/Media/OpenOSV/"
cp "$task_root/LICENSE" "$task_resources/Licenses/LICENSE"
cp "$task_root/NOTICE" "$task_resources/Licenses/NOTICE"
cp "$task_root/Assets/OpenOSV/LICENSE" "$task_resources/Licenses/OpenOSV-LICENSE"
cp "$task_root/Assets/OpenOSV/NOTICE" "$task_resources/Licenses/OpenOSV-NOTICE"
cp -R "$task_root/licenses/Rust" "$task_root/licenses/yyjson" "$task_resources/Licenses/"

task_iconset="$DERIVED_FILE_DIR/LumaRecode.iconset"
mkdir -p "$task_iconset"
for task_size in 16 32 128 256 512; do
    sips -z "$task_size" "$task_size" "$task_root/Brand/LumaRecode.png" --out "$task_iconset/icon_${task_size}x${task_size}.png" >/dev/null
    task_double=$((task_size * 2))
    sips -z "$task_double" "$task_double" "$task_root/Brand/LumaRecode.png" --out "$task_iconset/icon_${task_size}x${task_size}@2x.png" >/dev/null
done
iconutil -c icns "$task_iconset" -o "$task_resources/LumaRecode.icns"
