#!/bin/bash
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
output=${1:?Output directory required}
mkdir -p "$output"
xcrun swiftc -swift-version 5 -O -target arm64-apple-macos14.2 -module-cache-path "${TMPDIR:-/tmp}/miplay-swift-cache" \
  -framework SwiftUI -framework AppKit "$root"/native/macos/App/*.swift -o "$output/MiPlay.Desktop"
