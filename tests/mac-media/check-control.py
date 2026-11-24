"""Read-only bridge checks: never invoke a playback/transport command."""
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
script = root / "native/macos/MediaRemote/control.pl"
library = Path(sys.argv[1]).resolve()

def run(*args):
    return subprocess.run(["/usr/bin/perl", str(script), *map(str, args)],
                          capture_output=True, text=True, timeout=5)

check = run(library, "check")
assert check.returncode == 0 and check.stdout.strip() == "1", check.stderr
for command in ["stop", "shuffle", "toggle;next", "2", ""]:
    rejected = run(library, command)
    assert rejected.returncode != 0 and not rejected.stdout
    assert "Unsupported media command" in rejected.stderr
assert run("relative.dylib", "check").returncode != 0
assert run(library, "check", "extra").returncode != 0
for args in [("toggle",), ("next", ""), ("previous", "bad/player"), ("seek", "test.player"),
             ("seek", "test.player", "-1"), ("seek", "test.player", "NaN"),
             ("seek", "test.player", "inf"), ("seek", "test.player", "31536001"),
             ("seek", "test.player", "1;next")]:
    rejected = run(library, *args)
    assert rejected.returncode != 0 and not rejected.stdout
print("Media controls: transport/seek/target symbols available; 16 invalid inputs rejected; no playback commands sent")
