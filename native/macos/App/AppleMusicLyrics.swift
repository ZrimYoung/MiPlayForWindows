import AppKit

/// One verified recording only; the reader replaces this state on track/source
/// changes. An empty response can mean Music has not finished loading its cache.
struct AppleMusicLyricsRefreshState {
    private(set) var nextAttempt = Date.distantPast
    private(set) var text = ""
    private(set) var status = "等待 Apple Music 歌词加载"

    mutating func complete(text incoming: String, status incomingStatus: String,
                           failed: Bool = false, denied: Bool = false, at now: Date = Date()) {
        if denied {
            text = ""; status = incomingStatus; nextAttempt = .distantFuture
            return
        }
        // A transient empty cache/read failure must not clear lyrics already
        // verified for this recording. Reset, disable and source changes still do.
        if !incoming.isEmpty || text.isEmpty { text = incoming; status = incomingStatus }
        let interval: TimeInterval = !text.isEmpty ? 60 : (failed ? 15 : 3)
        nextAttempt = now.addingTimeInterval(interval)
    }
}

/// Optional read-only Apple event query. Online catalogue lyrics may be absent
/// even when Music's own lyrics panel can display them. Never log lyric text.
final class AppleMusicLyrics {
    static let maxBytes = 16 * 1024
    private var process: Process?
    private var revision = 0
    private var identity = ""
    private var duration: Double?
    private var refreshState = AppleMusicLyricsRefreshState()
    private var permissionDenied = false

    func reset(retryPermission: Bool = false) {
        if retryPermission { permissionDenied = false }
        revision += 1
        if let process { Self.stop(process) }
        process = nil; identity = ""; duration = nil; refreshState = AppleMusicLyricsRefreshState()
    }

    func refresh(_ item: NowPlayingItem?, completion: @escaping (String, String, String) -> Void) {
        guard let item, item.bundleIdentifier == "com.apple.Music" else { reset(); return }
        if identity != item.identity || duration != item.duration { reset(); identity = item.identity; duration = item.duration }
        if permissionDenied { completion(identity, "", "请在系统设置的自动化权限中允许访问音乐"); return }
        guard process == nil else { return }
        guard Date() >= refreshState.nextAttempt else { completion(identity, refreshState.text, refreshState.status); return }
        let request = revision, expected = item, child = Process(), pipe = Pipe()
        child.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        child.arguments = ["-l", "JavaScript", "-e", Self.script]
        child.standardInput = FileHandle.nullDevice; child.standardOutput = pipe
        child.standardError = FileHandle.nullDevice
        do { try child.run(); process = child }
        catch {
            refreshState.complete(text: "", status: "暂时无法读取歌词", failed: true)
            completion(identity, refreshState.text, refreshState.status); return
        }
        let timeout = DispatchWorkItem { Self.stop(child) }
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 10, execute: timeout)
        DispatchQueue.global(qos: .utility).async { [weak self] in
            var data = Data()
            while let part = try? MediaPipeReader.read(pipe.fileHandleForReading), !part.isEmpty {
                data.append(part)
                if data.count > 128 * 1024 { Self.stop(child); break }
            }
            child.waitUntilExit(); timeout.cancel(); try? pipe.fileHandleForReading.close()
            let values = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            var text = Self.matchedLyrics(values, expected: expected)
            var fromCache = false
            if text.isEmpty, let catalogID = Self.cacheCatalogID(values, expected: expected), let duration = expected.duration,
               let cached = AppleMusicCachedLyrics.read(catalogID: catalogID, duration: duration) {
                text = cached; fromCache = true
            }
            let status: String
            if values?["status"] as? String == "denied" { status = "请在系统设置的自动化权限中允许访问音乐" }
            else if child.terminationStatus != 0 || values == nil { status = "歌词读取超时或不可用" }
            else if fromCache { status = "已读取 Apple Music 本地歌词及时间轴" }
            else if !text.isEmpty { status = text.range(of: #"\[\d{1,3}:\d{2}[.:]\d{1,3}\]|\[\d{1,3}:\d{2}\]"#, options: .regularExpression) == nil ? "已读取文本歌词（无时间轴）" : "已读取带时间戳歌词" }
            else { status = "等待 Apple Music 歌词加载；暂无可读取歌词" }
            let deliveredText = text
            let failed = child.terminationStatus != 0 || values == nil || values?["status"] as? String == "unavailable"
            DispatchQueue.main.async {
                guard let self, self.revision == request, self.identity == expected.identity else { return }
                self.process = nil
                self.refreshState.complete(text: deliveredText, status: status, failed: failed,
                                           denied: values?["status"] as? String == "denied")
                // A denial must not trigger repeated permission requests.
                if values?["status"] as? String == "denied" { self.permissionDenied = true }
                completion(expected.identity, self.refreshState.text, self.refreshState.status)
            }
        }
    }

    static func matchedLyrics(_ values: [String: Any]?, expected: NowPlayingItem) -> String {
        guard expected.bundleIdentifier == "com.apple.Music", let values,
              values["status"] as? String == "ok", values["stable"] as? Bool == true,
              values["title"] as? String == expected.title,
              values["artist"] as? String == expected.artist,
              values["album"] as? String == expected.album,
              let lyrics = values["lyrics"] as? String, lyrics.utf8.count <= maxBytes,
              !lyrics.contains("\0") else { return "" }
        if let duration = expected.duration {
            guard let actual = values["duration"] as? Double, actual.isFinite,
                  abs(actual - duration) <= 2 else { return "" }
        }
        return lyrics.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func cacheCatalogID(_ values: [String: Any]?, expected: NowPlayingItem) -> String? {
        guard expected.bundleIdentifier == "com.apple.Music", let values,
              values["status"] as? String == "ok", values["stable"] as? Bool == true,
              values["title"] as? String == expected.title, values["artist"] as? String == expected.artist,
              values["album"] as? String == expected.album,
              values["catalogSourceVerified"] as? Bool == true,
              let id = values["catalogID"] as? String,
              id.range(of: #"^[1-9][0-9]{0,19}$"#, options: .regularExpression) != nil,
              let duration = expected.duration, let actual = values["duration"] as? Double,
              actual.isFinite, abs(actual - duration) <= 2 else { return nil }
        return id
    }

    private static func stop(_ child: Process) {
        guard child.isRunning else { return }
        child.terminate()
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 1) {
            if child.isRunning { kill(child.processIdentifier, SIGKILL) }
        }
    }

    static let script = queryTemplate.replacingOccurrences(of: "@@LYRICS@@", with:
        "const lyrics=track.lyrics() || ''; result.lyrics=lyrics.length <= 16384 ? lyrics : '';")
    static let artworkScript = queryTemplate.replacingOccurrences(of: "@@LYRICS@@", with: "")
    private static let queryTemplate = #"""
    ObjC.import('Foundation');
    function run() {
      const app = Application('com.apple.Music');
      if (!app.running()) return JSON.stringify({status:'not_running'});
      try {
        const track=app.currentTrack(), id=track.persistentID();
        const result={status:'ok',title:track.name(),artist:track.artist(),album:track.album(),duration:track.duration()};
        @@LYRICS@@
        // A store identifier is authoritative only for the same currently active
        // Music item. Never turn a queue UUID, database ID or title into a store ID.
        try {
          $.NSBundle.bundleWithPath('/System/Library/PrivateFrameworks/MediaRemote.framework/').load;
          const request=$.NSClassFromString('MRNowPlayingRequest');
          const path=request.localNowPlayingPlayerPath;
          const info=request.localNowPlayingItem.nowPlayingInfo;
          const get=function(field) {
            const value=info.objectForKey('kMRMediaRemoteNowPlayingInfo'+field);
            return value && !value.isNil() && (value.isKindOfClass($.NSString) || value.isKindOfClass($.NSNumber)) ? ObjC.unwrap(value) : null;
          };
          if (ObjC.unwrap(path.client.bundleIdentifier)==='com.apple.Music' &&
              get('Title')===result.title && get('Artist')===result.artist && get('Album')===result.album &&
              typeof get('Duration')==='number' && Math.abs(get('Duration')-result.duration)<=2) {
            const store=get('iTunesStoreIdentifier');
            if (store!==null && /^[1-9][0-9]{0,19}$/.test(String(store))) {
              result.catalogID=String(store); result.catalogSourceVerified=true;
            }
          }
        } catch(_) {}
        result.stable=typeof id==='string' && id.length>0 && id===app.currentTrack().persistentID();
        return JSON.stringify(result);
      } catch(e) { return JSON.stringify({status:e.errorNumber===-1743 ? 'denied' : 'unavailable'}); }
    }
    """#
}
