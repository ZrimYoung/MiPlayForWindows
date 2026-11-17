import AppKit
import Combine
import Darwin

struct NowPlayingItem {
    var title: String
    var artist: String
    var album: String
    var source: String
    var artwork: NSImage?
    var duration: Double?
    var elapsed: Double?
    var playing: Bool
    var timestamp: Date? = nil
    var playbackRate: Double = 1
    var bundleIdentifier: String = ""
    var trackIdentifier: String = ""
    var lyrics: String = ""
    var artworkBase64: String = ""
    var artworkSummary: String = "当前没有封面"
    // QQ Music regenerates MediaRemote's uniqueIdentifier on pause/seek updates.
    // Use displayed track metadata to guard actions against a different song.
    var identity: String { [bundleIdentifier, title, artist, album].joined(separator: "\u{1f}") }

    func position(at date: Date) -> Double? {
        guard let elapsed else { return nil }
        let delta = playing ? max(0, date.timeIntervalSince(timestamp ?? date)) * playbackRate : 0
        return min(max(0, elapsed + delta), duration ?? Double.greatestFiniteMagnitude)
    }
}

/// Playback-state notifications can precede a refreshed position anchor. Keep the
/// displayed position at a pause/resume boundary until the player supplies one.
struct MediaProgressTracker {
    private var raw: NowPlayingItem?
    private var displayed: NowPlayingItem?
    mutating func accept(_ incoming: NowPlayingItem?, at now: Date = Date()) -> NowPlayingItem? {
        guard var next = incoming else { raw = nil; displayed = nil; return nil }
        if let raw, let displayed, raw.identity == next.identity,
           raw.elapsed == next.elapsed, raw.timestamp == next.timestamp {
            if displayed.playing != next.playing {
                next.elapsed = displayed.position(at: now)
                next.timestamp = now
            } else {
                // Artwork and duplicate notifications must preserve the rebased anchor.
                next.elapsed = displayed.elapsed
                next.timestamp = displayed.timestamp
            }
        }
        raw = incoming; displayed = next
        return next
    }
}

enum MediaTransportCommand: String { case toggle, previous, next }

struct MediaActionExpectation {
    let before: NowPlayingItem
    let command: MediaTransportCommand?
    let seekPosition: Double?
    func fulfilled(by current: NowPlayingItem?, at date: Date = Date()) -> Bool {
        guard let current, current.bundleIdentifier == before.bundleIdentifier else { return false }
        if let seekPosition {
            return current.identity == before.identity && current.position(at: date).map { abs($0 - seekPosition) < 3 } == true
        }
        if command == .toggle { return current.playing != before.playing }
        return current.identity != before.identity ||
            (command == .previous && (before.position(at: date) ?? 0) > 5 && (current.position(at: date) ?? 999) < 3)
    }
}

/// Observation and explicit transport actions use separate child processes.
final class NowPlayingMonitor: ObservableObject {
    @Published var item: NowPlayingItem?
    @Published var availability = "idle"
    @Published private(set) var sendingCommand = false
    @Published private(set) var commandError = ""
    @Published private(set) var lyricsStatus = "等待 Apple Music 播放"
    @Published private(set) var highResolutionArtworkStatus = "等待 Apple Music 播放"
    @Published var highResolutionArtworkEnabled = UserDefaults.standard.bool(forKey: "fetchAppleMusicArtwork") {
        didSet {
            UserDefaults.standard.set(highResolutionArtworkEnabled, forKey: "fetchAppleMusicArtwork")
            clearDirectArtwork(retryPermission: true)
            refreshArtwork()
            refreshHighResolutionArtwork()
        }
    }
    @Published var lyricsEnabled = UserDefaults.standard.bool(forKey: "readAppleMusicLyrics") {
        didSet {
            UserDefaults.standard.set(lyricsEnabled, forKey: "readAppleMusicLyrics")
            lyricsReader.reset(retryPermission: true)
            item?.lyrics = ""
            refreshLyrics()
        }
    }
    @Published var enabled: Bool = UserDefaults.standard.object(forKey: "showSystemMedia") as? Bool ?? true {
        didSet {
            UserDefaults.standard.set(enabled, forKey: "showSystemMedia")
            invalidateRequest()
            if enabled && active { startStream() } else { availability = "disabled" }
        }
    }
    private var generation = 0
    private var preview = false
    private var active = false
    private var child: Process?
    private var commandChild: Process?
    private var fallbackTimer: Timer?
    private var pendingFallback = false
    private var receivedStream = false
    private var retryTimer: Timer?
    private var workspaceObservers: [NSObjectProtocol] = []
    private var terminatedSources: Set<String> = []
    private var pendingAction: MediaActionExpectation?
    private var actionTimeout: DispatchWorkItem?
    private var progress = MediaProgressTracker()
    private let lyricsReader = AppleMusicLyrics()
    private let artworkReader = AppleMusicArtwork()
    private var artworkRevision = 0
    private var directArtwork: DirectArtworkState?
    private var lyricsTimer: Timer?
    private var artworkObserver: NSObjectProtocol?
    private var latestSnapshot: [String: Any]?
    private let artworkWorker = DispatchQueue(label: "MiPlay.artwork-refresh", qos: .utility)
    private let reader = DispatchQueue(label: "MiPlay.system-media", qos: .utility)

    func start() {
        guard !active, !preview else { return }
        active = true
        artworkObserver = NotificationCenter.default.addObserver(forName: ArtworkPreferences.changed, object: nil, queue: .main) { [weak self] _ in
            self?.refreshArtwork()
        }
        let lyricsTimer = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            self?.refreshLyrics(); self?.refreshHighResolutionArtwork()
        }
        RunLoop.main.add(lyricsTimer, forMode: .common); self.lyricsTimer = lyricsTimer
        let center = NSWorkspace.shared.notificationCenter
        workspaceObservers = [center.addObserver(forName: NSWorkspace.didTerminateApplicationNotification, object: nil, queue: .main) { [weak self] notice in
            guard let app = notice.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication,
                  let bundle = app.bundleIdentifier else { return }
            self?.playerTerminated(bundle)
        }, center.addObserver(forName: NSWorkspace.didLaunchApplicationNotification, object: nil, queue: .main) { [weak self] notice in
            if let app = notice.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication,
               let bundle = app.bundleIdentifier { self?.terminatedSources.remove(bundle) }
        }]
        if enabled { startStream() } else { availability = "disabled" }
    }
    func stop() {
        active = false; invalidateRequest()
        if let artworkObserver { NotificationCenter.default.removeObserver(artworkObserver) }
        artworkObserver = nil
        lyricsTimer?.invalidate(); lyricsTimer = nil
        workspaceObservers.forEach { NSWorkspace.shared.notificationCenter.removeObserver($0) }
        workspaceObservers.removeAll(); terminatedSources.removeAll()
    }
    private var controlScript: URL {
        let bundled = Bundle.main.resourceURL?.appendingPathComponent("control-media.pl")
        return bundled.flatMap { FileManager.default.fileExists(atPath: $0.path) ? $0 : nil }
            ?? Bundle.main.executableURL!.deletingLastPathComponent().appendingPathComponent("control-media.pl")
    }
    var canControl: Bool {
        item != nil && !sendingCommand && (preview || (enabled && active && item?.bundleIdentifier.isEmpty == false && FileManager.default.fileExists(atPath: controlScript.path)))
    }
    var canSeek: Bool { canControl && item?.elapsed != nil && (item?.duration ?? 0) > 0 }
    func send(_ command: MediaTransportCommand) {
        guard canControl, let before = item else { return }
        perform([command.rawValue, before.bundleIdentifier], expectation: MediaActionExpectation(before: before, command: command, seekPosition: nil))
    }
    func seek(to seconds: Double, expectedIdentity: String) {
        guard canSeek, let before = item, before.identity == expectedIdentity, seconds.isFinite,
              let duration = before.duration, seconds >= 0, seconds <= duration else { return }
        perform(["seek", before.bundleIdentifier, String(format: "%.3f", locale: Locale(identifier: "en_US_POSIX"), seconds)],
                expectation: MediaActionExpectation(before: before, command: nil, seekPosition: seconds))
    }
    private func perform(_ arguments: [String], expectation: MediaActionExpectation) {
        guard !preview else { return }
        let request = generation, process = Process(), output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/perl")
        process.arguments = [controlScript.path,
            Bundle.main.executableURL!.deletingLastPathComponent().appendingPathComponent("libMiPlayMedia.dylib").path,
        ] + arguments
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = output; process.standardError = FileHandle.nullDevice
        cancelAction()
        commandError = ""; sendingCommand = true; pendingAction = expectation
        do { try process.run(); commandChild = process }
        catch { cancelAction(); commandError = "暂时无法控制播放器"; return }
        let timeout = DispatchWorkItem { Self.terminate(process) }
        // Allow the target lookup (1 s) and MediaRemote delivery barrier (2 s).
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 4, execute: timeout)
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let response = output.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit(); timeout.cancel()
            try? output.fileHandleForReading.close()
            let accepted = process.terminationStatus == 0 && String(decoding: response, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines) == "1"
            DispatchQueue.main.async {
                guard let self, self.generation == request else { return }
                guard self.commandChild === process else { return }
                self.commandChild = nil
                if !accepted { self.cancelAction(); self.commandError = "播放器已切换或未接受操作，请重试"; return }
                if self.confirmAction() { return }
                let actionTimeout = DispatchWorkItem { [weak self] in
                    guard let self, self.generation == request, self.pendingAction != nil else { return }
                    self.cancelAction(); self.commandError = "播放器尚未更新，请在播放器中确认"
                }
                self.actionTimeout = actionTimeout
                DispatchQueue.main.asyncAfter(deadline: .now() + 3, execute: actionTimeout)
            }
        }
    }
    private func invalidateRequest() {
        generation += 1
        latestSnapshot = nil
        clearDirectArtwork()
        lyricsReader.reset()
        cancelAction(); commandError = ""
        retryTimer?.invalidate(); retryTimer = nil
        fallbackTimer?.invalidate(); fallbackTimer = nil
        if let child { Self.terminate(child) }
        child = nil; pendingFallback = false; receivedStream = false; item = nil
        progress = MediaProgressTracker()
    }
    private func startStream() {
        let executableDirectory = Bundle.main.executableURL!.deletingLastPathComponent()
        let bundledScript = Bundle.main.resourceURL?.appendingPathComponent("observe-media.pl")
        let script = bundledScript.flatMap { FileManager.default.fileExists(atPath: $0.path) ? $0 : nil }
            ?? executableDirectory.appendingPathComponent("observe-media.pl")
        let library = executableDirectory.appendingPathComponent("libMiPlayMedia.dylib")
        guard FileManager.default.fileExists(atPath: script.path), FileManager.default.fileExists(atPath: library.path) else {
            startFallback(); return
        }
        let request = generation
        let process = Process(), output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/perl")
        process.arguments = [script.path, library.path]
        process.standardOutput = output; process.standardError = FileHandle.nullDevice
        process.standardInput = FileHandle.nullDevice
        do { try process.run(); child = process; availability = "loading" }
        catch { startFallback(); return }
        reader.async { [weak self] in
            var lines = MediaLineBuffer()
            do {
                while true {
                    let bytes = try MediaPipeReader.read(output.fileHandleForReading)
                    if bytes.isEmpty { break }
                    for line in try lines.append(bytes) {
                        guard let values = MediaSnapshot.fullSnapshot(from: line) else { continue }
                        let options = ArtworkOptions.load()
                        let metadata: NowPlayingItem? = autoreleasepool { MediaSnapshot.decode(values, artworkOptions: options) }
                        DispatchQueue.main.async {
                            guard let self, self.generation == request, self.active, self.enabled else { return }
                            self.receivedStream = true
                            self.latestSnapshot = values
                            self.acceptSnapshot(metadata)
                            if options != ArtworkOptions.load() { self.refreshArtwork() }
                            self.availability = self.item == nil ? "empty_or_restricted" : "available_via_media_bridge"
                        }
                    }
                }
            } catch { Self.terminate(process) }
            process.waitUntilExit()
            try? output.fileHandleForReading.close()
            DispatchQueue.main.async {
                guard let self, self.generation == request, self.active, self.enabled else { return }
                self.child = nil; self.item = nil
                self.startFallback()
            }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
            guard let self, self.generation == request, !self.receivedStream else { return }
            self.invalidateRequest()
            if self.active && self.enabled { self.startFallback() }
        }
    }
    private func startFallback() {
        guard active, enabled, fallbackTimer == nil else { return }
        generation += 1
        querySystemScript()
        let timer = Timer(timeInterval: 3, repeats: true) { [weak self] _ in self?.querySystemScript() }
        RunLoop.main.add(timer, forMode: .common); fallbackTimer = timer
        let retry = Timer(timeInterval: 15, repeats: false) { [weak self] _ in
            guard let self, self.active, self.enabled else { return }
            self.invalidateRequest(); self.startStream()
        }
        RunLoop.main.add(retry, forMode: .common); retryTimer = retry
    }
    private func querySystemScript() {
        guard !pendingFallback, active, enabled else { return }
        pendingFallback = true
        let request = generation, process = Process(), output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        process.arguments = ["-l", "JavaScript", "-e", Self.script]
        process.standardOutput = output; process.standardError = FileHandle.nullDevice
        process.standardInput = FileHandle.nullDevice
        do { try process.run(); child = process }
        catch { pendingFallback = false; availability = "unavailable"; return }
        let timeout = DispatchWorkItem { Self.terminate(process) }
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 2, execute: timeout)
        reader.async { [weak self] in
            // Text-only fallback is small; never buffer arbitrary child output.
            var data = Data()
            while let part = try? MediaPipeReader.read(output.fileHandleForReading), !part.isEmpty {
                data.append(part)
                if data.count > 64 * 1024 { Self.terminate(process); break }
            }
            process.waitUntilExit(); timeout.cancel()
            try? output.fileHandleForReading.close()
            let values = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            var converted: [String: Any] = [:]
            for (from, to) in [("Title", "title"), ("Artist", "artist"), ("Album", "album"), ("Duration", "duration"),
                               ("ElapsedTime", "elapsedTime"), ("PlaybackRate", "playbackRate")] {
                converted[to] = values?["kMRMediaRemoteNowPlayingInfo" + from]
            }
            converted["bundleIdentifier"] = values?["sourceBundle"]
            let metadata = MediaSnapshot.decode(converted)
            DispatchQueue.main.async {
                guard let self, self.generation == request, self.active, self.enabled else { return }
                self.child = nil; self.pendingFallback = false; self.acceptSnapshot(metadata)
                self.availability = self.item == nil ? "empty_or_restricted" : "text_only_fallback"
            }
        }
    }
    func setPreview() { stop(); preview = true; availability = "preview" }
    func acceptSnapshot(_ metadata: NowPlayingItem?) {
        if let metadata, terminatedSources.contains(metadata.bundleIdentifier) { return }
        if item?.bundleIdentifier != metadata?.bundleIdentifier { cancelAction(); commandError = "" }
        if item?.identity != metadata?.identity || item?.duration != metadata?.duration { clearDirectArtwork() }
        var next = progress.accept(metadata)
        if highResolutionArtworkEnabled, let incoming = next {
            next = directArtwork?.applying(to: incoming, options: ArtworkOptions.load()) ?? incoming
        }
        if let previous = item, previous.identity == next?.identity, previous.duration == next?.duration {
            next?.lyrics = previous.lyrics
        }
        item = next
        refreshLyrics()
        refreshHighResolutionArtwork()
        if highResolutionArtworkEnabled, let directArtwork,
           directArtwork.prepared == nil || directArtwork.options != ArtworkOptions.load() { refreshArtwork() }
        if metadata == nil { cancelAction(); commandError = "" }
        else if commandChild == nil { _ = confirmAction() }
    }
    func playerTerminated(_ bundle: String) {
        terminatedSources.insert(bundle)
        if item?.bundleIdentifier == bundle { acceptSnapshot(nil); availability = "empty_or_restricted" }
    }
    private func clearDirectArtwork(retryPermission: Bool = false) {
        artworkRevision += 1
        artworkReader.reset(retryPermission: retryPermission)
        directArtwork = nil
        highResolutionArtworkStatus = "等待 Apple Music 播放"
    }
    private func refreshHighResolutionArtwork() {
        guard active, enabled, highResolutionArtworkEnabled, !preview,
              let expected = item, expected.bundleIdentifier == "com.apple.Music" else { return }
        let revision = artworkRevision
        artworkReader.refresh(expected) { [weak self] data, status in
            guard let self, self.active, self.enabled, self.highResolutionArtworkEnabled,
                  self.artworkRevision == revision, self.item?.identity == expected.identity,
                  self.item?.duration == expected.duration else { return }
            self.highResolutionArtworkStatus = status
            if let data {
                self.directArtwork = DirectArtworkState(expected: expected, raw: data.base64EncodedString())
                self.refreshArtwork()
            }
        }
    }
    private func refreshArtwork() {
        guard active, enabled, !preview, var values = latestSnapshot, let expected = item else { return }
        let request = generation, revision = artworkRevision, options = ArtworkOptions.load()
        let originalArt = values["artworkData"] as? String
        let direct = highResolutionArtworkEnabled ? directArtwork : nil
        if let direct, direct.matches(expected) { values["artworkData"] = direct.raw }
        let imageValues = values
        artworkWorker.async { [weak self] in
            guard options == ArtworkOptions.load() else { return }
            let updated = autoreleasepool { MediaSnapshot.decode(imageValues, artworkOptions: options) }
            DispatchQueue.main.async {
                guard let self, self.active, self.enabled, self.generation == request,
                      self.artworkRevision == revision, options == ArtworkOptions.load(),
                      self.item?.identity == expected.identity, self.item?.duration == expected.duration,
                      updated?.identity == expected.identity,
                      self.latestSnapshot?["artworkData"] as? String == originalArt,
                      var current = self.item else { return }
                // Publish only artwork, never rewind playback or replace titles/lyrics.
                if let direct, direct.matches(current), let updated, !updated.artworkBase64.isEmpty {
                    var prepared = direct; prepared.prepared = updated; prepared.options = options
                    self.directArtwork = prepared
                    current = prepared.applying(to: current, options: options)
                } else if direct == nil {
                    current.artwork = updated?.artwork
                    current.artworkBase64 = updated?.artworkBase64 ?? ""
                    current.artworkSummary = updated?.artworkSummary ?? "当前没有封面"
                }
                self.item = current
            }
        }
    }
    private func refreshLyrics() {
        guard active, enabled, lyricsEnabled, !preview else { lyricsReader.reset(); return }
        guard let expected = item, expected.bundleIdentifier == "com.apple.Music" else {
            lyricsReader.reset(); lyricsStatus = "等待 Apple Music 播放"; return
        }
        lyricsReader.refresh(expected) { [weak self] identity, lyrics, status in
            guard let self, self.active, self.enabled, self.lyricsEnabled,
                  self.item?.identity == identity, self.item?.duration == expected.duration else { return }
            self.lyricsStatus = status
            if self.item?.lyrics != lyrics { self.item?.lyrics = lyrics }
        }
    }
    private func cancelAction() {
        actionTimeout?.cancel(); actionTimeout = nil; pendingAction = nil; sendingCommand = false
        if let commandChild { Self.terminate(commandChild) }
        commandChild = nil
    }
    @discardableResult private func confirmAction() -> Bool {
        guard let pendingAction, pendingAction.fulfilled(by: item) else { return false }
        cancelAction(); commandError = ""; return true
    }
    private static func terminate(_ process: Process) {
        guard process.isRunning else { return }
        process.terminate()
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 1) {
            if process.isRunning { kill(process.processIdentifier, SIGKILL) }
        }
    }
    private static let script = #"""
    ObjC.import('Foundation');
    function run() {
        try {
            $.NSBundle.bundleWithPath('/System/Library/PrivateFrameworks/MediaRemote.framework/').load;
            const request = $.NSClassFromString('MRNowPlayingRequest');
            const current = request.localNowPlayingItem;
            if (!current || current.isNil()) return '{}';
            const dictionary = current.nowPlayingInfo;
            const result = {};
            ['Title', 'Artist', 'Album', 'Duration', 'ElapsedTime', 'PlaybackRate'].forEach(function(field) {
                const key = 'kMRMediaRemoteNowPlayingInfo' + field;
                const value = dictionary.objectForKey(key);
                if (value && !value.isNil() && (value.isKindOfClass($.NSString) || value.isKindOfClass($.NSNumber))) result[key] = ObjC.unwrap(value);
            });
            const path = request.localNowPlayingPlayerPath;
            if (path && !path.isNil() && path.client && path.client.bundleIdentifier)
                result.sourceBundle = ObjC.unwrap(path.client.bundleIdentifier);
            return JSON.stringify(result);
        } catch (_) { return '{}'; }
    }
    """#
}
