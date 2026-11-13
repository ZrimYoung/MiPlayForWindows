#!/bin/bash
set -euo pipefail
native_root="$(cd "$(dirname "$0")" && pwd)"
output_dir="${1:-$native_root/build}"
mkdir -p "$output_dir"
xcrun clang++ -std=c++17 -fobjc-arc -O2 -mmacosx-version-min=14.2 -arch arm64 \
  -dynamiclib "$native_root/MiPlayAudio.mm" -framework CoreAudio -framework AVFoundation -framework AppKit \
  -install_name @rpath/libMiPlayAudio.dylib -o "$output_dir/libMiPlayAudio.dylib"
if [[ "${2:-}" == "--test" ]]; then
  xcrun clang++ -std=c++17 -fobjc-arc -O2 -mmacosx-version-min=14.2 -arch arm64 \
    -DMIPLAY_BRIDGE_TEST "$native_root/MiPlayAudio.mm" -framework CoreAudio -framework AVFoundation -framework AppKit \
    -o "$output_dir/resampler-test"
  "$output_dir/resampler-test"
fi
