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
}

/// Read-only, best-effort system metadata. No player commands, audio capture, network artwork lookup or logging.
/// MediaRemote is private and can change; absence of metadata never changes the casting lifecycle.
final class NowPlayingMonitor: ObservableObject {
    @Published var item: NowPlayingItem?
    @Published var availability = "idle"
    @Published var enabled: Bool = UserDefaults.standard.object(forKey: "showSystemMedia") as? Bool ?? true {
        didSet {
            UserDefaults.standard.set(enabled, forKey: "showSystemMedia")
            invalidateRequest()
            if enabled { refresh() } else { availability = "disabled" }
        }
    }
    private typealias InfoCallback = @convention(block) (CFDictionary?) -> Void
    private typealias InfoFunction = @convention(c) (DispatchQueue, @escaping InfoCallback) -> Void
    private typealias PIDCallback = @convention(block) (Int32) -> Void
    private typealias PIDFunction = @convention(c) (DispatchQueue, @escaping PIDCallback) -> Void
    private var library: UnsafeMutableRawPointer?
    private var getInfo: InfoFunction?
    private var getPID: PIDFunction?
    private var timer: Timer?
    private var generation = 0
    private var pending = false
    private var preview = false
    private var scriptProcess: Process?

    init() {
        library = dlopen("/System/Library/PrivateFrameworks/MediaRemote.framework/MediaRemote", RTLD_LAZY | RTLD_LOCAL)
        if let library, let symbol = dlsym(library, "MRMediaRemoteGetNowPlayingInfo") {
            getInfo = unsafeBitCast(symbol, to: InfoFunction.self)
            if let pidSymbol = dlsym(library, "MRMediaRemoteGetNowPlayingApplicationPID") {
                getPID = unsafeBitCast(pidSymbol, to: PIDFunction.self)
            }
        }
        // Asynchronous framework blocks can outlive a request; retain the loaded image until process exit.
    }
    func start() {
        guard timer == nil, !preview else { return }
        refresh()
        let timer = Timer(timeInterval: 3, repeats: true) { [weak self] _ in self?.refresh() }
        RunLoop.main.add(timer, forMode: .common); self.timer = timer
    }
    func stop() { timer?.invalidate(); timer = nil; invalidateRequest() }
    private func invalidateRequest() {
        generation += 1; pending = false; item = nil
        if let scriptProcess, scriptProcess.isRunning { scriptProcess.terminate() }
        scriptProcess = nil
    }
    func refresh() {
        guard enabled, !pending, !preview else { return }
        generation += 1
        let request = generation
        pending = true
        if let getInfo {
            getInfo(.main) { [weak self] dictionary in
                guard let self, request == self.generation, self.enabled else { return }
                if let values = dictionary as? [String: Any], let metadata = Self.decode(values) {
                    self.item = metadata; self.pending = false; self.availability = "available"
                    self.getPID?(.main) { [weak self] pid in
                        guard let self, request == self.generation else { return }
                        if let name = NSRunningApplication(processIdentifier: pid)?.localizedName { self.item?.source = name }
                    }
                } else { self.querySystemScript(request: request) }
            }
        } else { querySystemScript(request: request) }
        DispatchQueue.main.asyncAfter(deadline: .now() + 4.5) { [weak self] in
            guard let self, request == self.generation, self.pending else { return }
            self.invalidateRequest(); self.availability = "timeout"
        }
    }
    static func decode(_ values: [String: Any]) -> NowPlayingItem? {
        guard let title = values["kMRMediaRemoteNowPlayingInfoTitle"] as? String, !title.isEmpty else { return nil }
        func text(_ key: String) -> String { values["kMRMediaRemoteNowPlayingInfo" + key] as? String ?? "" }
        func number(_ key: String) -> Double? {
            guard let value = values["kMRMediaRemoteNowPlayingInfo" + key] as? NSNumber,
                  value.doubleValue.isFinite, value.doubleValue >= 0 else { return nil }
            return value.doubleValue
        }
        var artwork: NSImage?
        if let bytes = values["kMRMediaRemoteNowPlayingInfoArtworkData"] as? Data, bytes.count <= 2_000_000 {
            artwork = NSImage(data: bytes)
        } else if let encoded = values["artworkBase64"] as? String, encoded.utf8.count < 2_700_000,
                  let bytes = Data(base64Encoded: encoded) { artwork = NSImage(data: bytes) }
        var source = "系统媒体"
        if let bundle = values["sourceBundle"] as? String,
           let name = NSWorkspace.shared.runningApplications.first(where: { $0.bundleIdentifier == bundle })?.localizedName { source = name }
        return NowPlayingItem(title: title, artist: text("Artist"), album: text("Album"), source: source,
            artwork: artwork, duration: number("Duration"), elapsed: number("ElapsedTime"), playing: (number("PlaybackRate") ?? 0) > 0)
    }
    private func querySystemScript(request: Int) {
        // Recent macOS restricts direct clients. Query the same read-only service through the system script host.
        // This does not disable SIP, inject code into a service, request Apple Events access or modify system settings.
        let child = Process(), output = Pipe()
        child.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        child.arguments = ["-l", "JavaScript", "-e", Self.script]
        child.standardOutput = output; child.standardError = FileHandle.nullDevice
        do { try child.run(); scriptProcess = child }
        catch { pending = false; item = nil; availability = "unavailable"; return }
        let timeout = DispatchWorkItem { if child.isRunning { child.terminate() } }
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 2, execute: timeout)
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let data = output.fileHandleForReading.readDataToEndOfFile()
            child.waitUntilExit(); timeout.cancel()
            let values = data.count < 3_000_000 ? (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] : nil
            DispatchQueue.main.async {
                guard let self, request == self.generation, self.enabled else { return }
                self.scriptProcess = nil; self.pending = false
                self.item = values.flatMap(Self.decode)
                self.availability = self.item == nil ? "empty_or_restricted" : "available_via_system_script"
            }
        }
    }
    func setPreview() { preview = true; availability = "preview" }
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
            const image = dictionary.objectForKey('kMRMediaRemoteNowPlayingInfoArtworkData');
            if (image && !image.isNil() && image.isKindOfClass($.NSData) && image.length <= 2000000)
                result.artworkBase64 = ObjC.unwrap(image.base64EncodedStringWithOptions(0));
            const path = request.localNowPlayingPlayerPath;
            if (path && !path.isNil() && path.client && path.client.bundleIdentifier)
                result.sourceBundle = ObjC.unwrap(path.client.bundleIdentifier);
            return JSON.stringify(result);
        } catch (_) { return '{}'; }
    }
    """#
}
