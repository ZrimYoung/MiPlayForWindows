"""Fetch checksum-pinned FFmpeg dependencies; build a small standalone encoder on macOS."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def prepare(platform):
    spec = json.loads((ROOT / "packaging/desktop-dependencies.json").read_text())[platform]
    cache = ROOT / "artifacts/dependencies" / platform
    cache.mkdir(parents=True, exist_ok=True)
    archive = cache / spec["url"].rsplit("/", 1)[-1]
    if not archive.exists():
        temporary = archive.with_suffix(archive.suffix + ".download")
        subprocess.run(["curl", "-fsSL", "--retry", "3", spec["url"], "-o", str(temporary)], check=True)
        temporary.replace(archive)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != spec["sha256"]:
        raise RuntimeError(f"Checksum mismatch: {archive}")
    if platform == "windows":
        extracted = cache / "extracted"
        if not extracted.exists():
            with zipfile.ZipFile(archive) as zipped:
                zipped.extractall(extracted)
        executable = next(extracted.glob("*/bin/ffmpeg.exe"))
    else:
        source = cache / f"ffmpeg-{spec['version']}"
        if not source.exists():
            with tarfile.open(archive) as tar:
                # The complete archive was checked against the pinned SHA-256 above.
                tar.extractall(cache)
        executable = source / "ffmpeg"
        options = ["--disable-everything", "--disable-autodetect", "--disable-network", "--disable-doc", "--disable-debug",
                   "--disable-ffprobe", "--enable-ffmpeg", "--enable-small", "--enable-encoder=aac,pcm_s16le",
                   "--enable-decoder=aac,pcm_s16le", "--enable-demuxer=aac,pcm_s16le", "--enable-muxer=adts,pcm_s16le",
                   "--enable-protocol=file,pipe", "--enable-filter=aresample,aformat,anull", "--enable-static", "--disable-shared",
                   "--extra-cflags=-mmacosx-version-min=14.2", "--extra-ldflags=-mmacosx-version-min=14.2"]
        stamp = source / "miplay-options.json"
        if not executable.exists() or not stamp.exists() or json.loads(stamp.read_text()) != options:
            with (cache / "build.log").open("w") as log:
                subprocess.run(["./configure", *options], cwd=source, stdout=log, stderr=subprocess.STDOUT, check=True)
                subprocess.run(["make", "-j2"], cwd=source, stdout=log, stderr=subprocess.STDOUT, check=True)
            stamp.write_text(json.dumps(options))
    print(executable)
    return executable, archive


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("platform", choices=["macos", "windows"])
    prepare(parser.parse_args().platform)
