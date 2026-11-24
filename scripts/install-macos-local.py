"""Install the local Mac test build at a stable path, retaining application settings."""
import argparse
import importlib.util
from pathlib import Path
import plistlib
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
DESTINATION = Path('/Applications/MiPlay Desktop.app')
BUNDLE_ID = 'com.zrim.miplaydesktop'


def validate(app):
    with (app / 'Contents/Info.plist').open('rb') as file:
        info = plistlib.load(file)
    if info.get('CFBundleIdentifier') != BUNDLE_ID:
        raise RuntimeError('Refusing to replace an application with a different identifier.')
    return info['CFBundleShortVersionString']


def main():
    spec = importlib.util.spec_from_file_location('desktop_package', ROOT / 'scripts/package-desktop.py')
    package = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(package)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=ROOT / 'artifacts/desktop' / package.VERSION / 'osx-arm64/MiPlay Desktop.app')
    args = parser.parse_args()
    source = args.source.resolve(strict=True)
    version = validate(source)
    if source == DESTINATION or DESTINATION.is_symlink():
        raise RuntimeError('Expected a test build source and a regular Applications destination.')
    if DESTINATION.exists():
        validate(DESTINATION)
    subprocess.run(['codesign', '--verify', '--deep', '--strict', str(source)], check=True)
    with tempfile.TemporaryDirectory(prefix='.MiPlay-update-', dir='/Applications') as folder:
        stage = Path(folder) / 'new.app'
        previous = Path(folder) / 'previous.app'
        subprocess.run(['ditto', str(source), str(stage)], check=True)
        subprocess.run(['codesign', '--verify', '--deep', '--strict', str(stage)], check=True)
        # NSRunningApplication terminates only this bundle, without scripting the player.
        script = r'''
import AppKit
let apps = NSRunningApplication.runningApplications(withBundleIdentifier: "com.zrim.miplaydesktop")
for app in apps { guard app.terminate() else { fputs("MiPlay refused graceful shutdown\n", stderr); exit(1) } }
let deadline = Date().addingTimeInterval(12)
while apps.contains(where: { !$0.isTerminated }) && Date() < deadline {
    RunLoop.main.run(until: Date().addingTimeInterval(0.1))
}
guard apps.allSatisfy({ $0.isTerminated }) else { fputs("MiPlay is still shutting down\n", stderr); exit(1) }
'''
        subprocess.run(['xcrun', 'swift', '-module-cache-path', str(Path(tempfile.gettempdir()) / 'miplay-swift-cache'), '-e', script], check=True)
        if DESTINATION.exists():
            DESTINATION.rename(previous)
        try:
            stage.rename(DESTINATION)
            subprocess.run(['codesign', '--verify', '--deep', '--strict', str(DESTINATION)], check=True)
            subprocess.run(['open', '-a', str(DESTINATION)], check=True)
        except BaseException:
            if DESTINATION.exists():
                shutil.rmtree(DESTINATION)
            if previous.exists():
                previous.rename(DESTINATION)
            raise
    print(f'Installed and opened MiPlay Desktop {version}: {DESTINATION}')


if __name__ == '__main__':
    main()
