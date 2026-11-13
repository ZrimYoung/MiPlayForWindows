"""Exercise the packaged native app's IPC service without starting capture or casting."""
import argparse
import json
import os
from pathlib import Path
import selectors
import subprocess
import tempfile
import time

parser = argparse.ArgumentParser()
parser.add_argument("engine", type=Path)
args = parser.parse_args()
with tempfile.TemporaryDirectory(prefix="miplay-ipc-") as profile:
    env = dict(os.environ, MIPLAY_PROFILE_DIRECTORY=profile)
    for key in ("DOTNET_ROOT", "DOTNET_ROOT_ARM64", "DLNACAST_FFMPEG"):
        env.pop(key, None)
    env["PATH"] = "/usr/bin:/bin"
    child = subprocess.Popen([str(args.engine.resolve())], stdin=subprocess.PIPE,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env)
    selector = selectors.DefaultSelector()
    selector.register(child.stdout, selectors.EVENT_READ)
    buffered = b""

    def event(kind):
        global buffered
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            if b"\n" not in buffered:
                if not selector.select(0.2):
                    continue
                chunk = os.read(child.stdout.fileno(), 65536)
                if not chunk:
                    raise RuntimeError("Engine exited before its IPC response")
                buffered += chunk
            while b"\n" in buffered:
                line, buffered = buffered.split(b"\n", 1)
                value = json.loads(line)
                if value.get("event") == kind:
                    return value
        raise TimeoutError("Missing IPC response: " + kind)

    try:
        initial = event("snapshot")["snapshot"]
        assert not any(s["selected"] for s in initial["speakers"])
        child.stdin.write(b'{"command":"volume","id":"missing","value":40}\n')
        child.stdin.flush()
        assert event("error")["message"]
        child.stdin.close()  # Same EOF produced when the native parent exits unexpectedly.
        assert child.wait(timeout=8) == 0, child.stderr.read().decode(errors="replace")
        print("PASS: packaged IPC, no auto-cast, invalid-command handling and parent-exit cleanup")
    finally:
        selector.close()
        if child.poll() is None:
            child.kill()
            child.wait(timeout=3)
