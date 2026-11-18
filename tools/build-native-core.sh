#!/bin/sh
set -eu
task_configuration=${1:-Release}
case "$task_configuration" in Debug) task_preset=native-debug ;; Release) task_preset=native-release ;; *) echo 'Usage: build-native-core.sh [Debug|Release]' >&2; exit 2 ;; esac
task_mode=${2:-test}
case "$task_mode" in test|--build-only) ;; *) echo 'Usage: build-native-core.sh [Debug|Release] [--build-only]' >&2; exit 2 ;; esac
task_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$task_root"
case "$(uname -s)" in Darwin) task_triple=aarch64-apple-darwin; task_platform=arm64 ;; *) echo 'This entry targets macOS Apple Silicon.' >&2; exit 2 ;; esac
rustup target add "$task_triple"
task_output="$task_root/out/native-$task_platform-$(printf '%s' "$task_configuration" | tr '[:upper:]' '[:lower:]')"
cmake --preset "$task_preset" -B "$task_output" -DLUMA_RUST_TARGET="$task_triple" -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=27.0
if [ "$task_mode" = test ]; then
    cmake --build "$task_output"
    ctest --test-dir "$task_output" --output-on-failure
else
    cmake --build "$task_output" --target luma_core
fi
codesign --force --sign - "$task_output/engine/libLumaRecodeCore.dylib"
codesign --verify --strict "$task_output/engine/libLumaRecodeCore.dylib"
