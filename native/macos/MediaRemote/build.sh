#!/bin/bash
set -euo pipefail
root=$(cd "$(dirname "$0")" && pwd)
output=${1:?Output directory required}
mkdir -p "$output"
xcrun clang -dynamiclib -fobjc-arc -fvisibility=default -target arm64-apple-macos14.2 \
  -I "$root/vendor/include" -I "$root/vendor/src" \
  -framework Foundation -framework AppKit -framework ImageIO -framework UniformTypeIdentifiers \
  "$root"/vendor/src/adapter/*.m "$root"/vendor/src/private/*.m "$root"/vendor/src/utility/*.m "$root/control.m" \
  -o "$output/libMiPlayMedia.dylib"
