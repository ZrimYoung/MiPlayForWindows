"""Exercise the packaged native app's IPC service without starting capture or casting."""
import argparse
import base64
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
        assert initial["synchronizedPlayback"]
        child.stdin.write(b'{"command":"synchronizedPlayback","value":false}\n')
        child.stdin.flush()
        deadline = time.monotonic() + 8
        while True:
            snapshot = event("snapshot")["snapshot"]
            assert not any(s["selected"] for s in snapshot["speakers"])
            if not snapshot["synchronizedPlayback"]:
                break
            assert time.monotonic() < deadline, "Playback mode did not change"
        assert json.loads(Path(profile, "settings.json").read_text())["SynchronizedPlayback"] is False
        for bitrate in (320000, 512000, 256000):
            child.stdin.write((json.dumps({"command":"audioBitRate", "value":bitrate}) + "\n").encode())
            child.stdin.flush()
            snapshot = event("snapshot")["snapshot"]
            while snapshot["audioBitRate"] != bitrate:
                snapshot = event("snapshot")["snapshot"]
            assert not any(s["selected"] for s in snapshot["speakers"])
            assert json.loads(Path(profile, "settings.json").read_text())["AudioBitRate"] == bitrate
        cover = b"\xff\xd8\xff" + bytes(1024 * 1024 - 3)
        media = {"title":"Synthetic cover", "artist":"Test", "album":"Test", "source":"Test",
                 "durationMilliseconds":100000, "positionMilliseconds":0, "playing":False,
                 "artworkBase64":base64.b64encode(cover).decode()}
        child.stdin.write((json.dumps({"command":"media", "value":media}) + "\n").encode())
        # Media updates are synchronous and have no success event. An invalid
        # sentinel proves the large update did not emit an earlier rejection.
        child.stdin.write(b'{"command":"artwork-test-sentinel"}\n'); child.stdin.flush()
        assert event("error")["message"] == "不支持的操作。"
        media["artworkBase64"] = base64.b64encode(cover + b"x").decode()
        child.stdin.write((json.dumps({"command":"media", "value":media}) + "\n").encode())
        child.stdin.flush()
        assert event("error")["message"] == "封面过大。"
        child.stdin.write(b'{"command":"media","value":null}\n')
        child.stdin.flush()
        child.stdin.write(b'{"command":"volume","id":"missing","value":40}\n')
        child.stdin.flush()
        assert event("error")["message"]
        child.stdin.write(b'{"command":"groupVolume","id":"missing-group","value":40}\n')
        child.stdin.flush()
        assert "立体声组合已不在列表中" in event("error")["message"]
        child.stdin.close()  # Same EOF produced when the native parent exits unexpectedly.
        assert child.wait(timeout=8) == 0, child.stderr.read().decode(errors="replace")
        print("PASS: packaged IPC, bounded large artwork, shared/compatibility mode persistence, no auto-cast, invalid commands and parent-exit cleanup")
    finally:
        selector.close()
        if child.poll() is None:
            child.kill()
            child.wait(timeout=3)
