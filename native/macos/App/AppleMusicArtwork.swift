import AppKit
import ImageIO
import SQLite3

/// Only cached catalogue responses belonging to the verified current recording.
enum AppleMusicArtworkCache {
    static let maxBodyBytes = 2 * 1024 * 1024
    static func allowed(_ url: URL) -> Bool {
        url.scheme?.lowercased() == "https" && (url.host?.lowercased().hasSuffix(".mzstatic.com") == true) &&
        url.user == nil && url.password == nil && (url.port == nil || url.port == 443) && url.path.hasPrefix("/image/")
    }
    static func imageURL(_ body: Data, catalogID: String, expected: NowPlayingItem) -> URL? {
        guard expected.bundleIdentifier == "com.apple.Music", body.count <= maxBodyBytes,
              let duration = expected.duration, duration.isFinite,
              let json = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any],
              let songs = json["data"] as? [[String: Any]] else { return nil }
        for song in songs where song["id"] as? String == catalogID && song["type"] as? String == "songs" {
            guard let attributes = song["attributes"] as? [String: Any],
                  attributes["name"] as? String == expected.title,
                  attributes["artistName"] as? String == expected.artist,
                  attributes["albumName"] as? String == expected.album,
                  let ms = attributes["durationInMillis"] as? Double, ms.isFinite, abs(ms / 1000 - duration) <= 2,
                  let art = attributes["artwork"] as? [String: Any], let template = art["url"] as? String,
                  let width = art["width"] as? Int, let height = art["height"] as? Int,
                  width > 0, height > 0, width <= 30000, height <= 30000 else { continue }
            // No larger than the app's highest selectable output resolution.
            let ratio = min(1, 2048.0 / Double(max(width, height)))
            let path = template.replacingOccurrences(of: "{w}", with: String(max(1, Int(Double(width) * ratio))))
                .replacingOccurrences(of: "{h}", with: String(max(1, Int(Double(height) * ratio))))
                .replacingOccurrences(of: "{c}", with: "bb").replacingOccurrences(of: "{f}", with: "jpg")
            guard path.utf8.count <= 4096, !path.contains("{"), !path.contains("}"),
                  let url = URL(string: path), allowed(url) else { continue }
            return url
        }
        return nil
    }
    static func read(catalogID: String, expected: NowPlayingItem, cache: URL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Caches/com.apple.Music")) -> URL? {
        guard catalogID.range(of: #"^[1-9][0-9]{0,19}$"#, options: .regularExpression) != nil else { return nil }
        var database: OpaquePointer?
        guard sqlite3_open_v2(cache.appendingPathComponent("Cache.db").path, &database, SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX, nil) == SQLITE_OK else {
            if database != nil { sqlite3_close(database) }; return nil
        }
        defer { sqlite3_close(database) }
        sqlite3_busy_timeout(database, 50)
        let sql = """
        SELECT d.isDataOnFS, d.receiver_data FROM cfurl_cache_response r
        JOIN cfurl_cache_receiver_data d ON r.entry_ID=d.entry_ID
        WHERE r.request_key LIKE 'https://amp-api.music.apple.com/v1/catalog/%'
        ORDER BY r.entry_ID DESC LIMIT 64
        """
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database, sql, -1, &statement, nil) == SQLITE_OK else { return nil }
        defer { sqlite3_finalize(statement) }
        while sqlite3_step(statement) == SQLITE_ROW {
            let body: Data
            if sqlite3_column_int(statement, 0) != 0 {
                guard let pointer = sqlite3_column_text(statement, 1) else { continue }
                let name = String(cString: pointer)
                guard !name.isEmpty, name != ".", name != "..", !name.contains("/"), !name.contains("\\") else { continue }
                let folder = cache.appendingPathComponent("fsCachedData").resolvingSymlinksInPath()
                let file = folder.appendingPathComponent(name).resolvingSymlinksInPath()
                guard file.deletingLastPathComponent() == folder,
                      let properties = try? file.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey]),
                      properties.isRegularFile == true, let size = properties.fileSize, size <= maxBodyBytes,
                      let handle = try? FileHandle(forReadingFrom: file) else { continue }
                defer { try? handle.close() }
                guard let bytes = try? handle.read(upToCount: maxBodyBytes + 1), bytes.count <= maxBodyBytes else { continue }
                body = bytes
            } else {
                let size = Int(sqlite3_column_bytes(statement, 1))
                guard size > 0, size <= maxBodyBytes, let pointer = sqlite3_column_blob(statement, 1) else { continue }
                body = Data(bytes: pointer, count: size)
            }
            if let url = imageURL(body, catalogID: catalogID, expected: expected) { return url }
        }
        return nil
    }
}

/// Serial delegate queue bounds both declared and streaming response sizes.
final class AppleArtworkDownload: NSObject, URLSessionDataDelegate {
    static let maxBytes = 8 * 1024 * 1024
    private let queue = OperationQueue()
    private var session: URLSession?
    private var task: URLSessionDataTask?
    private var data = Data()
    private var redirects = 0
    private var completed = false
    private let completion: (Data?) -> Void
    init(url: URL, completion: @escaping (Data?) -> Void) {
        self.completion = completion; super.init()
        guard AppleMusicArtworkCache.allowed(url) else { completion(nil); return }
        let config = URLSessionConfiguration.ephemeral
        config.urlCache = nil; config.urlCredentialStorage = nil; config.httpCookieStorage = nil
        config.httpShouldSetCookies = false; config.timeoutIntervalForRequest = 12; config.timeoutIntervalForResource = 15
        queue.maxConcurrentOperationCount = 1; queue.qualityOfService = .utility
        let session = URLSession(configuration: config, delegate: self, delegateQueue: queue)
        self.session = session
        let task = session.dataTask(with: url); self.task = task; task.resume()
    }
    func cancel() { queue.addOperation { [weak self] in self?.task?.cancel() } }
    private func finish(_ result: Data?) {
        guard !completed else { return }; completed = true
        session?.invalidateAndCancel(); session = nil; task = nil; data.removeAll()
        DispatchQueue.main.async { self.completion(result) }
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        redirects += 1
        guard redirects <= 3, let url = request.url, AppleMusicArtworkCache.allowed(url) else { completionHandler(nil); return }
        completionHandler(request)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard let response = response as? HTTPURLResponse, response.statusCode == 200,
              let url = response.url, AppleMusicArtworkCache.allowed(url), response.expectedContentLength <= Self.maxBytes else {
            completionHandler(.cancel); return
        }
        completionHandler(.allow)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive bytes: Data) {
        guard !completed else { return }
        guard data.count + bytes.count <= Self.maxBytes else { finish(nil); return }
        data.append(bytes)
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        finish(error == nil && Self.dimensions(data) != nil ? data : nil)
    }
    static func dimensions(_ data: Data) -> (Int, Int)? {
        guard !data.isEmpty, data.count <= maxBytes, let source = CGImageSourceCreateWithData(data as CFData, nil),
              CGImageSourceGetCount(source) == 1,
              let kind = CGImageSourceGetType(source) as String?, ["public.jpeg", "public.png", "org.webmproject.webp"].contains(kind),
              let props = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = props[kCGImagePropertyPixelWidth] as? Int, let height = props[kCGImagePropertyPixelHeight] as? Int,
              width > 0, height > 0, width <= 8192, height <= 8192, width * height <= 32 * 1024 * 1024 else { return nil }
        return (width, height)
    }
}

/// All mutable request state is owned by the main queue. Reset cancels work and
/// invalidates late results even when a previous request has the same song title.
final class AppleMusicArtwork {
    private var revision = 0
    private var identity = ""
    private var duration: Double?
    private var process: Process?
    private var download: AppleArtworkDownload?
    private var nextAttempt = Date.distantPast
    private var denied = false
    private var complete = false
    func reset(retryPermission: Bool = false) {
        revision += 1
        if let process { Self.stop(process) }
        process = nil; download?.cancel(); download = nil
        identity = ""; duration = nil; nextAttempt = .distantPast; complete = false
        if retryPermission { denied = false }
    }
    func refresh(_ expected: NowPlayingItem, completion: @escaping (Data?, String) -> Void) {
        guard expected.bundleIdentifier == "com.apple.Music" else { reset(); return }
        if identity != expected.identity || duration != expected.duration { reset(); identity = expected.identity; duration = expected.duration }
        guard !complete, !denied, process == nil, download == nil, Date() >= nextAttempt else { return }
        nextAttempt = Date().addingTimeInterval(20)
        let request = revision
        completion(nil, "正在获取 Apple Music 高清封面…")
        query(expected, request: request) { [weak self] values, url in
            guard let self else { return }
            if values?["status"] as? String == "denied" {
                self.denied = true; completion(nil, "请在系统设置的自动化权限中允许访问音乐"); return
            }
            guard let id = AppleMusicLyrics.cacheCatalogID(values, expected: expected), let url else {
                completion(nil, "暂未找到当前歌曲的高清地址，使用系统封面"); return
            }
            self.download = AppleArtworkDownload(url: url) { [weak self] data in
                guard let self, self.revision == request else { return }
                self.download = nil
                guard let data, let dimensions = AppleArtworkDownload.dimensions(data) else {
                    completion(nil, "高清封面暂时不可用，使用系统封面"); return
                }
                // Recheck exact catalogue identity after the network request.
                self.query(expected, request: request, readCache: false) { [weak self] current, _ in
                    guard let self else { return }
                    guard AppleMusicLyrics.cacheCatalogID(current, expected: expected) == id else {
                        completion(nil, "歌曲已变化，等待当前歌曲的高清封面"); return
                    }
                    self.complete = true
                    completion(data, "已获取 \(dimensions.0)×\(dimensions.1) 的 Apple Music 官方封面")
                }
            }
        }
    }
    private func query(_ expected: NowPlayingItem, request: Int, readCache: Bool = true,
                       completion: @escaping ([String: Any]?, URL?) -> Void) {
        let child = Process(), pipe = Pipe()
        child.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        child.arguments = ["-l", "JavaScript", "-e", AppleMusicLyrics.artworkScript]
        child.standardInput = FileHandle.nullDevice; child.standardOutput = pipe; child.standardError = FileHandle.nullDevice
        do { try child.run(); process = child } catch { completion(nil, nil); return }
        let timeout = DispatchWorkItem { Self.stop(child) }
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 10, execute: timeout)
        DispatchQueue.global(qos: .utility).async { [weak self] in
            var data = Data()
            while let bytes = try? MediaPipeReader.read(pipe.fileHandleForReading), !bytes.isEmpty {
                data.append(bytes); if data.count > 64 * 1024 { Self.stop(child); break }
            }
            child.waitUntilExit(); timeout.cancel(); try? pipe.fileHandleForReading.close()
            let values = data.count <= 64 * 1024 && child.terminationStatus == 0
                ? (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] : nil
            let id = AppleMusicLyrics.cacheCatalogID(values, expected: expected)
            let url = readCache ? id.flatMap { AppleMusicArtworkCache.read(catalogID: $0, expected: expected) } : nil
            DispatchQueue.main.async {
                guard let self, self.revision == request else { return }
                self.process = nil; completion(values, url)
            }
        }
    }
    private static func stop(_ child: Process) {
        guard child.isRunning else { return }; child.terminate()
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 1) { if child.isRunning { kill(child.processIdentifier, SIGKILL) } }
    }
}

/// Track-bound artwork only: a cover refresh cannot replace newer media fields.
struct DirectArtworkState {
    let expected: NowPlayingItem
    let raw: String
    var prepared: NowPlayingItem?
    var options: ArtworkOptions?
    func matches(_ item: NowPlayingItem) -> Bool {
        item.bundleIdentifier == "com.apple.Music" && expected.identity == item.identity && expected.duration == item.duration
    }
    func applying(to item: NowPlayingItem, options: ArtworkOptions) -> NowPlayingItem {
        guard matches(item), self.options == options, let prepared else { return item }
        var result = item
        result.artwork = prepared.artwork; result.artworkBase64 = prepared.artworkBase64
        result.artworkSummary = prepared.artworkSummary + " · Apple Music 高清来源"
        return result
    }
}
