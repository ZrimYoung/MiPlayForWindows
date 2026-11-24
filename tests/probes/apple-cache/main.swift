import AppKit

// Read-only probe. Explicit catalogue ID reads an existing local response only;
// --current exercises the production Music reader, including its identity guard.
if CommandLine.arguments.dropFirst().first == "--monitor" {
    // Process-only preferences; do not change the user's app configuration.
    UserDefaults.standard.setVolatileDomain(["readAppleMusicLyrics": true, "showSystemMedia": true], forName: UserDefaults.argumentDomain)
    let monitor = NowPlayingMonitor()
    monitor.start()
    let deadline = Date().addingTimeInterval(20)
    while Date() < deadline && (monitor.item?.lyrics.isEmpty ?? true) {
        RunLoop.main.run(until: Date().addingTimeInterval(0.05))
    }
    let text = monitor.item?.lyrics ?? ""
    print("source=\(monitor.item?.bundleIdentifier ?? "none") availability=\(monitor.availability) lyricBytes=\(text.utf8.count) timedLines=\(text.split(separator: "\n").count) status=\(monitor.lyricsStatus)")
    monitor.stop()
    exit(text.isEmpty ? 1 : 0)
}
if CommandLine.arguments.count == 3, let duration = Double(CommandLine.arguments[2]) {
    let text = AppleMusicCachedLyrics.read(catalogID: CommandLine.arguments[1], duration: duration) ?? ""
    print("localCacheHasLyrics=\(!text.isEmpty) utf8Bytes=\(text.utf8.count) timedLines=\(text.split(separator: "\n").count)")
    exit(text.isEmpty ? 1 : 0)
}
guard ["--current", "--reader"].contains(CommandLine.arguments.dropFirst().first ?? "") else { exit(2) }
let child = Process(), pipe = Pipe()
child.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
child.arguments = ["-l", "JavaScript", "-e", AppleMusicLyrics.script]
child.standardOutput = pipe; child.standardError = FileHandle.nullDevice
try child.run()
let timeout = DispatchWorkItem { if child.isRunning { child.terminate() } }
DispatchQueue.global().asyncAfter(deadline: .now() + 10, execute: timeout)
let data = pipe.fileHandleForReading.readDataToEndOfFile(); child.waitUntilExit(); timeout.cancel()
guard var json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { exit(1) }
json["bundleIdentifier"] = "com.apple.Music"
guard let item = MediaSnapshot.decode(json) else { exit(1) }
if CommandLine.arguments.dropFirst().first == "--reader" {
    let reader = AppleMusicLyrics()
    var done = false, success = false
    reader.refresh(item) { identity, text, status in
        success = identity == item.identity && !text.isEmpty; done = true
        print("identityMatches=\(identity == item.identity) lyricBytes=\(text.utf8.count) timedLines=\(text.split(separator: "\n").count) status=\(status)")
    }
    let deadline = Date().addingTimeInterval(15)
    while !done && Date() < deadline { RunLoop.main.run(until: Date().addingTimeInterval(0.05)) }
    reader.reset()
    exit(success ? 0 : 1)
}
let id = AppleMusicLyrics.cacheCatalogID(json, expected: item)
let direct = AppleMusicLyrics.matchedLyrics(json, expected: item)
let cached = id.flatMap { AppleMusicCachedLyrics.read(catalogID: $0, duration: item.duration ?? 0) } ?? ""
print("verifiedCatalogueID=\(id != nil) scriptLyricBytes=\(direct.utf8.count) cacheLyricBytes=\(cached.utf8.count) timedLines=\(cached.split(separator: "\n").count)")
exit(direct.isEmpty && cached.isEmpty ? 1 : 0)
