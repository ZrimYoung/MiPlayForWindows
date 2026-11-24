#!/bin/bash
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
output=${1:?Output directory required}
mkdir -p "$output"
python3 "$root/scripts/prepare-desktop-webp.py" "$output"
bash "$root/native/macos/MediaRemote/build.sh" "$output"
cp "$root/native/macos/MediaRemote/observe.pl" "$output/observe-media.pl"
cp "$root/native/macos/MediaRemote/control.pl" "$output/control-media.pl"
xcrun swiftc -swift-version 5 -O -target arm64-apple-macos14.2 -module-cache-path "${TMPDIR:-/tmp}/miplay-swift-cache" \
  -framework SwiftUI -framework AppKit "$root"/native/macos/App/*.swift -o "$output/MiPlay.Desktop"
