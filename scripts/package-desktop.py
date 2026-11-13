"""Create local test artifacts only. Does not publish releases or install certificates."""
import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def run(*args):
    subprocess.run([str(a) for a in args], cwd=ROOT, check=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("platform", choices=["macos", "windows"])
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location("ffmpeg_dependency", ROOT / "scripts/prepare-desktop-ffmpeg.py")
    dependency = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(dependency)
    ffmpeg, archive = dependency.prepare(args.platform)
    rid = "osx-arm64" if args.platform == "macos" else "win-x64"
    artifacts = ROOT / "artifacts/desktop"
    stage = artifacts / rid
    if stage.exists():
        shutil.rmtree(stage)
    if args.platform == "macos":
        app = stage / "MiPlay Desktop.app"
        output = app / "Contents/MacOS"
        resources = app / "Contents/Resources"
        resources.mkdir(parents=True)
    else:
        output = stage / "MiPlayDesktop"
        resources = output
    output.mkdir(parents=True)
    publish = [os.environ.get("MIPLAY_DOTNET", "dotnet"), "publish", "src/MiPlay.Desktop/MiPlay.Desktop.csproj",
               "-c", "Release", "-r", rid, "--self-contained", "true", "-o", str(output), "-m:1", "-p:UseSharedCompilation=false"]
    if args.platform == "windows":
        publish.append("-p:DesktopPlatform=Windows")
    else:
        # Keep managed PE assemblies inside the apphost rather than treating
        # loose .dll files in Contents/MacOS as unsigned nested native code.
        publish += ["-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=false", "-p:DebugType=None"]
    run(*publish)
    if args.platform == "windows":
        for binary in ffmpeg.parent.iterdir():
            if binary.name == "ffmpeg.exe" or binary.suffix.lower() == ".dll":
                shutil.copy2(binary, output / binary.name)
        notices = resources / "third-party/ffmpeg"
        notices.mkdir(parents=True)
        for path in ffmpeg.parent.parent.iterdir():
            if path.is_file(): shutil.copy2(path, notices / path.name)
        if sys.platform == "win32":
            encoders = subprocess.check_output([str(output / "ffmpeg.exe"), "-hide_banner", "-encoders"], text=True)
            if "aac_mf" not in encoders: raise RuntimeError("Packaged Windows FFmpeg does not provide aac_mf")
            run(output / "MiPlay.Desktop.exe", "--self-check")
    else:
        run("bash", "native/macos/build.sh", output)
        shutil.copy2(ffmpeg, output / "ffmpeg")
        notices = resources / "third-party/ffmpeg"
        notices.mkdir(parents=True)
        shutil.copy2(archive, notices / archive.name)
        shutil.copy2(ffmpeg.parent / "COPYING.LGPLv2.1", notices / "COPYING.LGPLv2.1")
        shutil.copy2(ROOT / "scripts/prepare-desktop-ffmpeg.py", notices / "build-recipe.py")
        with (app / "Contents/Info.plist").open("wb") as file:
            plistlib.dump({"CFBundleName": "MiPlay Desktop", "CFBundleDisplayName": "MiPlay Desktop",
                          "CFBundleIdentifier": "com.zrim.miplaydesktop", "CFBundleExecutable": "MiPlay.Desktop",
                          "CFBundlePackageType": "APPL", "CFBundleShortVersionString": "0.3.0", "CFBundleVersion": "1",
                          "LSMinimumSystemVersion": "14.2", "NSHighResolutionCapable": True,
                          "NSAudioCaptureUsageDescription": "捕获电脑正在播放的声音，并投放到你选择的小米妙播音箱。",
                          "NSLocalNetworkUsageDescription": "查找并连接同一局域网中的小米妙播音箱。",
                          "NSBonjourServices": ["_mi-connect._udp"]}, file)
        macho = {b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xca\xfe\xba\xbe"}
        executable = output / "MiPlay.Desktop"
        for binary in output.rglob("*"):
            if binary.is_file():
                with binary.open("rb") as file: magic = file.read(4)
                if magic in macho and binary != executable: run("codesign", "--force", "--sign", "-", binary)
                elif magic not in macho:
                    if binary.suffix == ".dll": raise RuntimeError(f"Unexpected loose managed assembly: {binary}")
                    shutil.move(binary, resources / binary.name)
    shutil.copy2(ROOT / "packaging/desktop-dependencies.json", resources / "dependencies.json")
    shutil.copy2(ROOT / "docs/desktop-validation.md", resources / "VALIDATION.md")
    (resources / "TEST-BUILD.txt").write_text(
        "个人测试构建；真机兼容性、两小时播放和 Windows 安装验收尚需独立确认。\n"
        "上游主程序复用授权待确认。本测试包不代表公开分发许可。\n"
        "FFmpeg 为独立进程；来源与校验值见 dependencies.json，许可见 third-party。\n", encoding="utf-8")
    try:
        commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True, stderr=subprocess.DEVNULL).strip()
        dirty = bool(subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=no"], cwd=ROOT, text=True).strip())
    except (FileNotFoundError, subprocess.CalledProcessError):
        commit, dirty = "unversioned", None
    (resources / "build-info.json").write_text(json.dumps({
        "source_commit": commit, "working_tree_dirty": dirty, "runtime": rid,
        "built_at_utc": datetime.now(timezone.utc).isoformat(),
        "speaker_hardware_validation": "pending", "public_release": False
    }, indent=2), encoding="utf-8")
    # Resource files must be present before the final bundle signature.
    if args.platform == "macos":
        run("codesign", "--force", "--sign", "-", executable)
        run("codesign", "--force", "--sign", "-", app)
        run("codesign", "--verify", "--deep", "--strict", app)
        run(executable, "--self-check")
    destination = artifacts / f"MiPlayDesktop-0.3.0-{rid}.zip"
    destination.unlink(missing_ok=True)
    if args.platform == "macos":
        run("ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", app, destination)
    else:
        with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED) as zipped:
            for path in stage.rglob("*"):
                if path.is_file(): zipped.write(path, path.relative_to(stage))
    checksum = hashlib.sha256(destination.read_bytes()).hexdigest()
    destination.with_suffix(".zip.sha256").write_text(f"{checksum}  {destination.name}\n")
    print(destination)


if __name__ == "__main__":
    main()
