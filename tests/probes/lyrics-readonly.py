#!/usr/bin/env python3
"""Read-only lyric feasibility checks. Print counts, never lyric text or credentials."""
import argparse
import base64
import json
import re
import subprocess
import urllib.request


def summarize(text):
    return {"characters": len(text), "utf8Bytes": len(text.encode("utf-8")),
            "timestampMarkers": len(re.findall(r"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]", text))}


def request(module, method, param):
    body = {"comm": {"ct": 24, "cv": 0, "format": "json"},
            "req": {"module": module, "method": method, "param": param}}
    query = urllib.request.Request("https://u.y.qq.com/cgi-bin/musicu.fcg",
        data=json.dumps(body).encode(), headers={"Content-Type": "application/json",
        "Referer": "https://y.qq.com/", "User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(query, timeout=10) as response:
        raw = response.read(1024 * 1024 + 1)
    if len(raw) > 1024 * 1024:
        raise ValueError("Response exceeds size limit")
    result = json.loads(raw)
    if result.get("code") != 0 or result.get("req", {}).get("code") != 0:
        raise ValueError("Provider rejected query")
    return result["req"]["data"]


def qq(mid):
    if not re.fullmatch(r"[A-Za-z0-9]{14}", mid):
        raise ValueError("Expected a QQ songmid, not a URL or credentials")
    track = request("music.pf_song_detail_svr", "get_song_detail_yqq", {"song_mid": mid})["track_info"]
    if track.get("mid") != mid:
        raise ValueError("Track identity mismatch")
    data = request("music.musichallSong.PlayLyricInfo", "GetPlayLyricInfo",
                   {"songID": track["id"], "songMID": mid, "crypt": 0, "qrc": 0, "trans": 0, "roma": 0})
    if str(data.get("songID")) != str(track["id"]) or data.get("crypt") not in (0, "0", None):
        raise ValueError("Unexpected identity or encrypted response")
    text = data.get("lyric", "")
    encoding = "plain"
    if text and not re.search(r"\[\d{1,3}:\d{2}", text):
        try:
            text = base64.b64decode(text, validate=True).decode("utf-8")
            encoding = "base64-utf8"
        except (ValueError, UnicodeError):
            raise ValueError("Unrecognized lyric encoding")
    return {"source": "qq-web", "songmid": mid, "songID": track["id"],
            "encoding": encoding, **summarize(text)}


def music():
    # No launch, playback, set, keypress or UI automation commands.
    script = '''function run() {
      const app=Application('com.apple.Music');
      if(!app.running()) return JSON.stringify({status:'not_running'});
      try {
        const t=app.currentTrack(), id=t.persistentID(), lyrics=t.lyrics() || '';
        return JSON.stringify({status:'ok',stable:id===app.currentTrack().persistentID(),
          characters:lyrics.length,timestampMarkers:(lyrics.match(/\\[\\d{1,3}:\\d{2}(?:[.:]\\d{1,3})?\\]/g)||[]).length});
      } catch(e) { return JSON.stringify({status:'error',code:e.errorNumber || 0}); }
    }'''
    result = subprocess.run(["/usr/bin/osascript", "-l", "JavaScript", "-e", script],
                            capture_output=True, text=True, timeout=15, check=True)
    return {"source": "apple-music-script", **json.loads(result.stdout)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", choices=["music", "qq"])
    parser.add_argument("songmid", nargs="?")
    args = parser.parse_args()
    try:
        print(json.dumps(music() if args.source == "music" else qq(args.songmid or ""), ensure_ascii=False))
    except Exception as error:
        # Exception text can contain remote data; retain only the exception class.
        print(json.dumps({"status": "failed", "errorType": type(error).__name__}))
        raise SystemExit(1)
