"""Fetch checksum-pinned FFmpeg dependencies; build a small standalone encoder on macOS."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def prepare_lame():
    """Build pinned static MP3 encoder in a path without spaces for libtool."""
    spec = json.loads((ROOT / "packaging/desktop-dependencies.json").read_text())["lame"]
    cache = ROOT / "artifacts/dependencies/macos"
    cache.mkdir(parents=True, exist_ok=True)
    archive = cache / "lame-3.100.tar.gz"
    if not archive.exists():
        temporary = archive.with_suffix(".download")
        subprocess.run(["curl", "-fsSL", "--retry", "3", spec["url"], "-o", str(temporary)], check=True)
        temporary.replace(archive)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != spec["sha256"]:
        raise RuntimeError("LAME archive checksum mismatch")
    source = cache / "lame-3.100"
    if not source.exists():
        with tarfile.open(archive) as tar:
            tar.extractall(cache)
    prefix = Path(tempfile.gettempdir()) / ("miplay-lame-" + spec["sha256"][:12])
    library = prefix / "lib/libmp3lame.a"
    if not library.exists():
        with tempfile.TemporaryDirectory(prefix="miplay-lame-build-") as temporary:
            work = Path(temporary) / "source"
            shutil.copytree(source, work)
            environment = dict(os.environ, CFLAGS="-O2 -fPIC -mmacosx-version-min=14.2")
            with (cache / "lame-build.log").open("w") as log:
                subprocess.run(["./configure", "--prefix=" + str(prefix), "--disable-shared", "--enable-static",
                    "--disable-frontend", "--disable-decoder", "--disable-gtktest", "--disable-nasm"],
                    cwd=work, env=environment, stdout=log, stderr=subprocess.STDOUT, check=True)
                subprocess.run(["make", "-j2"], cwd=work, stdout=log, stderr=subprocess.STDOUT, check=True)
                subprocess.run(["make", "install"], cwd=work, stdout=log, stderr=subprocess.STDOUT, check=True)
    return prefix


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
        lame = prepare_lame()
        options = ["--disable-everything", "--disable-autodetect", "--disable-network", "--disable-doc", "--disable-debug",
                   "--disable-ffprobe", "--enable-ffmpeg", "--enable-small", "--enable-libmp3lame", "--enable-encoder=aac,pcm_s16le,libmp3lame",
                   "--enable-decoder=aac,pcm_s16le,mp3", "--enable-demuxer=aac,pcm_s16le,mp3", "--enable-muxer=adts,pcm_s16le,mp3",
                   "--enable-protocol=file,pipe", "--enable-filter=aresample,aformat,anull", "--enable-static", "--disable-shared",
                   f"--extra-cflags=-mmacosx-version-min=14.2 -I{lame}/include", f"--extra-ldflags=-mmacosx-version-min=14.2 -L{lame}/lib"]
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
