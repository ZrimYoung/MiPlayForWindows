#!/bin/bash
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
output="$root/artifacts/tests/mac-media"
mkdir -p "$output"
python3 "$root/scripts/prepare-desktop-webp.py" "$output"
xcrun swiftc -swift-version 5 -O -module-cache-path "${TMPDIR:-/tmp}/miplay-swift-cache" \
  -framework AppKit "$root/native/macos/App/NowPlayingMonitor.swift" \
  "$root/native/macos/App/AppleMusicLyrics.swift" \
  "$root/native/macos/App/AppleMusicArtwork.swift" \
  "$root/native/macos/App/AppleMusicCachedLyrics.swift" \
  "$root/native/macos/App/ArtworkSettings.swift" \
  "$root/native/macos/App/MediaSnapshot.swift" "$root/tests/mac-media/main.swift" -o "$output/test-media"
"$output/test-media"
bash "$root/native/macos/MediaRemote/build.sh" "$output"
python3 "$root/tests/mac-media/check-control.py" "$output/libMiPlayMedia.dylib"
