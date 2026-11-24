"""Build a pinned, self-contained WebP encoder dylib for the native Mac sender."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[1]
SPEC = json.loads((ROOT / 'packaging/desktop-dependencies.json').read_text())['webp']
VERSION, SHA256, URL = SPEC['version'], SPEC['sha256'], SPEC['url']

def prepare(output):
    output = Path(output).resolve(); output.mkdir(parents=True, exist_ok=True)
    cache = ROOT / 'artifacts/dependencies/webp'; cache.mkdir(parents=True, exist_ok=True)
    archive = cache / f'libwebp-{VERSION}.tar.gz'
    if not archive.exists():
        temporary = archive.with_suffix('.download')
        subprocess.run(['curl', '-fsSL', '--retry', '3', URL, '-o', str(temporary)], check=True)
        temporary.replace(archive)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != SHA256:
        raise RuntimeError('WebP source checksum mismatch')
    source = cache / f'libwebp-{VERSION}'
    if not source.exists():
        with tarfile.open(archive) as tar:
            # Extract only the checksum-pinned upstream release.
            tar.extractall(cache)
    library = cache / 'libMiPlayWebP.dylib'
    if not library.exists():
        # libtool's static-archive aggregation does not support this repo's
        # space-containing path. Build in an isolated temporary directory.
        build_root = Path(tempfile.gettempdir()) / ('miplay-webp-' + SHA256[:12])
        build_root.mkdir(parents=True, exist_ok=True)
        build_source = build_root / f'libwebp-{VERSION}'
        if not build_source.exists():
            with tarfile.open(archive) as tar: tar.extractall(build_root)
        source = build_source
        env = dict(os.environ, CFLAGS='-O2 -fPIC -mmacosx-version-min=14.2', LDFLAGS='-mmacosx-version-min=14.2')
        with (cache / 'build.log').open('w') as log:
            subprocess.run(['./configure', '--disable-shared', '--enable-static', '--disable-gl',
                            '--disable-sdl', '--disable-png', '--disable-jpeg', '--disable-tiff',
                            '--disable-gif', '--disable-libwebpdemux', '--disable-libwebpmux'],
                           cwd=source, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
            subprocess.run(['make', '-j2'], cwd=source, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
            subprocess.run(['xcrun', 'clang', '-dynamiclib', '-mmacosx-version-min=14.2',
                            '-Wl,-force_load,' + str(source / 'src/.libs/libwebp.a'),
                            '-Wl,-force_load,' + str(source / 'sharpyuv/.libs/libsharpyuv.a'),
                            '-install_name', '@rpath/libMiPlayWebP.dylib', '-o', str(library)],
                           stdout=log, stderr=subprocess.STDOUT, check=True)
    shutil.copy2(library, output / library.name)
    return source

if __name__ == '__main__':
    prepare(sys.argv[1])
