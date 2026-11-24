import AppKit
import SwiftUI
import Combine

struct SpeakerCard: Codable, Identifiable {
    let id: String
    var name: String
    var model: String
    var online: Bool
    var selected: Bool
    var state: String
    var message: String
    var volume: Int?
    var delayMilliseconds: Int
    var groupId: String? = nil
    var groupName: String? = nil
    var channel: String? = nil
    var failure: String? = nil
    var transport: String? = nil
    var channelRoute: String? = nil
    var diagnostics: DesktopDiagnostic? = nil
    var isDlna: Bool { transport == "DLNA" }
}
struct DesktopAudioSource: Codable, Equatable {
    var mode = "system"
    var outputDeviceId = ""
    var processId: Int? = nil
    var includeChildren = true
    var dictionary: [String: Any] {
        ["mode": mode, "outputDeviceId": outputDeviceId, "processId": processId.map { $0 as Any } ?? NSNull(), "includeChildren": includeChildren]
    }
}
struct DesktopAudioSourceItem: Codable, Identifiable, Equatable {
    let id: String
    let displayName: String
    var processId: Int? = nil
}
struct DesktopDiagnostic: Codable {
    var bufferedMilliseconds: Int
    var overruns: Int64
    var underruns: Int64
    var accessUnits: Int64
    var rtpFrames: Int64
    var wireBytes: Int64
    var maximumMediaSendGapMilliseconds: Double
    var maximumInterReceiverSendSkewMilliseconds: Double
    var encoderPendingInputFrames: Int
    var encoderBufferedAccessUnits: Int
}
struct DesktopSnapshot: Codable {
    var revision: Int64 = 0
    var speakers: [SpeakerCard] = []
    var scanning = false
    var discoveryMessage = "正在查找附近音箱…"
    var muteLocalOutput = false
    var supportsLocalMute = true
    var synchronizedPlayback = true
    var audioBitRate = 256000
    var audioSource: DesktopAudioSource? = nil
    var outputDevices: [DesktopAudioSourceItem]? = nil
    var candidateProcesses: [DesktopAudioSourceItem]? = nil
    var dlnaEnabled: Bool? = nil
    var captureDescription: String {
        guard let source = audioSource else { return "此 Mac 正在播放的所有声音" }
        if source.mode == "process" {
            let name = candidateProcesses?.first(where: { $0.processId == source.processId })?.displayName ?? "所选应用"
            return "仅捕获 \(name) 的声音"
        }
        if !source.outputDeviceId.isEmpty {
            let name = outputDevices?.first(where: { $0.id == source.outputDeviceId })?.displayName ?? "所选输出设备"
            return "捕获 \(name) 的声音"
        }
        return "此 Mac 正在播放的所有声音"
    }
}
private struct EngineEvent: Decodable {
    let event: String
    let snapshot: DesktopSnapshot?
    let message: String?
    let path: String?
}

final class EngineClient: ObservableObject {
    @Published var snapshot = DesktopSnapshot()
    @Published var error = ""
    @Published var connected = false
    private var process: Process?
    private var input: FileHandle?
    private var outputPipe: Pipe?
    private var errorPipe: Pipe?
    private let reader = DispatchQueue(label: "com.zrim.miplay.reader")
    private let writer = DispatchQueue(label: "com.zrim.miplay.writer")
    private var incoming = Data()
    private var quitting = false
    private var onExit: (() -> Void)?
    private var sleepObserver: NSObjectProtocol?
    private var delayTimer: Timer?
    private var pendingDelays: [String: (id: String, grouped: Bool, value: Int)] = [:]
    private var mediaTimer: Timer?
    private var mediaSubscription: AnyCancellable?
    let media = NowPlayingMonitor()
    let preview: Bool

    init(preview: Bool = false) {
        self.preview = preview
        if preview {
            media.setPreview()
            if CommandLine.arguments.contains("--media-fixture") {
                media.item = NowPlayingItem(title: "示例歌曲", artist: "示例歌手", album: "示例专辑 · 2026", source: "QQ 音乐",
                    artwork: nil, duration: 218, elapsed: 82, playing: true, timestamp: Date())
            }
            connected = true
            snapshot.discoveryMessage = "3 台音箱在线 · 自动发现中"
            snapshot.speakers = [
                SpeakerCard(id: "art", name: "客厅 · 小爱音箱 Art", model: "", online: true, selected: true,
                            state: "Casting", message: "正在投放", volume: 38, delayMilliseconds: 200),
                SpeakerCard(id: "pro", name: "书房 · Xiaomi 智能音箱 Pro", model: "", online: true, selected: true,
                            state: "Casting", message: "正在投放", volume: 26, delayMilliseconds: 500),
                SpeakerCard(id: "other", name: "卧室音箱", model: "", online: true, selected: false,
                            state: "Idle", message: "点选加入投放", volume: nil, delayMilliseconds: 1000)
            ]
            if CommandLine.arguments.contains("--stereo-fixture") {
                snapshot.speakers = [
                    SpeakerCard(id: "pro", name: "ZrSpeaker Pro", model: "OH2P", online: true, selected: false,
                                state: "Idle", message: "点选加入投放", volume: 30, delayMilliseconds: 1000),
                    SpeakerCard(id: "left", name: "ZrSpeaker Art L", model: "L09A", online: true, selected: true,
                                state: "Casting", message: "正在投放", volume: 32, delayMilliseconds: 1000,
                                groupId: "preview", groupName: "ZrSpeakerStereo", channel: "left"),
                    SpeakerCard(id: "right", name: "ZrSpeaker Art R", model: "L09A", online: true, selected: true,
                                state: "Casting", message: "正在投放", volume: 28, delayMilliseconds: 1000,
                                groupId: "preview", groupName: "ZrSpeakerStereo", channel: "right")
                ]
                if CommandLine.arguments.contains("--group-offline-fixture") || CommandLine.arguments.contains("--group-partial-fixture") {
                    snapshot.speakers[1].selected = false
                    snapshot.speakers[1].state = "Error"
                    snapshot.speakers[1].failure = "Offline"
                    snapshot.speakers[1].message = "音箱连接中断"
                    snapshot.speakers[1].online = !CommandLine.arguments.contains("--group-offline-fixture")
                }
            }
            if CommandLine.arguments.contains("--sources-fixture") {
                snapshot.audioSource = DesktopAudioSource()
                snapshot.outputDevices = [DesktopAudioSourceItem(id: "builtin", displayName: "Mac 扬声器"), DesktopAudioSourceItem(id: "display", displayName: "外接显示器")]
                snapshot.candidateProcesses = [DesktopAudioSourceItem(id: "process:100", displayName: "音乐", processId: 100), DesktopAudioSourceItem(id: "process:200", displayName: "QQ 音乐", processId: 200)]
                snapshot.dlnaEnabled = true
                snapshot.speakers[0].channelRoute = "LeftAsMono"
                snapshot.speakers[0].diagnostics = DesktopDiagnostic(bufferedMilliseconds: 34, overruns: 0, underruns: 2,
                    accessUnits: 5800, rtpFrames: 12000, wireBytes: 1500000, maximumMediaSendGapMilliseconds: 24,
                    maximumInterReceiverSendSkewMilliseconds: 0.7, encoderPendingInputFrames: 1, encoderBufferedAccessUnits: 2)
                snapshot.speakers[2].transport = "DLNA"
            }
        }
    }
    func start() {
        guard !preview, process == nil else { return }
        let executable = Bundle.main.executableURL!.deletingLastPathComponent().appendingPathComponent("MiPlay.Engine")
        let child = Process(), stdin = Pipe(), stdout = Pipe(), stderr = Pipe()
        child.executableURL = executable
        var environment = ProcessInfo.processInfo.environment
        environment["MIPLAY_SOURCE_NAME"] = Host.current().localizedName ?? ProcessInfo.processInfo.hostName
        child.environment = environment
        child.standardInput = stdin; child.standardOutput = stdout; child.standardError = stderr
        input = stdin.fileHandleForWriting; outputPipe = stdout; errorPipe = stderr
        stdout.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty else { handle.readabilityHandler = nil; return }
            self?.reader.async { [weak self] in self?.consume(data) }
        }
        stderr.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty else { handle.readabilityHandler = nil; return }
            let message = String(decoding: data.prefix(2048), as: UTF8.self)
            DispatchQueue.main.async { [weak self] in self?.error = message }
        }
        child.terminationHandler = { [weak self] _ in
            DispatchQueue.main.async {
                guard let self else { return }
                self.connected = false
                self.stopMediaForwarding()
                self.media.stop()
                self.outputPipe?.fileHandleForReading.readabilityHandler = nil
                self.errorPipe?.fileHandleForReading.readabilityHandler = nil
                self.input = nil
                if !self.quitting {
                    self.error = "投放服务已退出。请重新打开应用。"
                    for index in self.snapshot.speakers.indices {
                        self.snapshot.speakers[index].selected = false
                        self.snapshot.speakers[index].state = "Error"
                        self.snapshot.speakers[index].message = "投放服务已退出"
                    }
                }
                self.onExit?(); self.onExit = nil
            }
        }
        do {
            try child.run(); process = child; connected = true
            mediaSubscription = media.$item.receive(on: DispatchQueue.main).sink { [weak self] item in
                self?.forwardMedia(item)
            }
            let timer = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
                guard let self else { return }; self.forwardMedia(self.media.item)
            }
            RunLoop.main.add(timer, forMode: .common); mediaTimer = timer
            media.start()
            sleepObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.willSleepNotification,
                object: nil, queue: .main) { [weak self] _ in self?.send("stopAll") }
        } catch { self.error = "无法启动投放服务：\(error.localizedDescription)" }
    }
    private func consume(_ data: Data) {
        incoming.append(data)
        while let newline = incoming.firstIndex(of: 10) {
            let line = incoming.prefix(upTo: newline)
            incoming.removeSubrange(...newline)
            guard let event = try? JSONDecoder().decode(EngineEvent.self, from: line) else { continue }
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                if let snapshot = event.snapshot, snapshot.revision >= self.snapshot.revision {
                    let sourceChanged = snapshot.audioSource != self.snapshot.audioSource
                    self.snapshot = snapshot
                    if sourceChanged {
                        self.send("media", value: NSNull())
                        self.media.setCaptureBundle(self.selectedCaptureBundle)
                    }
                }
                if let message = event.message { self.error = message }
                if let path = event.path { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)]) }
            }
        }
    }
    func send(_ command: String, id: String? = nil, value: Any? = nil) {
        guard !preview, connected, !quitting else { return }
        var object: [String: Any] = ["command": command]
        if let id { object["id"] = id }
        if let value { object["value"] = value }
        guard var data = try? JSONSerialization.data(withJSONObject: object) else { return }
        data.append(10)
        let handle = input
        writer.async { [weak self] in
            do { try handle?.write(contentsOf: data) }
            catch { DispatchQueue.main.async { [weak self] in self?.error = "无法发送操作，请重新打开应用。" } }
        }
    }
    // Send the latest target at most every 120 ms, including while dragging.
    // Coalescing lives outside the view so snapshot refreshes cannot cancel a gesture.
    func setDelay(id: String, grouped: Bool, milliseconds: Int) {
        pendingDelays["\(grouped):\(id)"] = (id, grouped, milliseconds)
        guard delayTimer == nil else { return }
        let timer = Timer(timeInterval: 0.12, repeats: false) { [weak self] _ in
            guard let self else { return }
            let values = self.pendingDelays.values
            self.pendingDelays.removeAll(); self.delayTimer = nil
            for target in values {
                self.send(target.grouped ? "groupDelay" : "delay", id: target.id, value: target.value)
            }
        }
        RunLoop.main.add(timer, forMode: .common); delayTimer = timer
    }
    private var selectedCaptureBundle: String? {
        guard snapshot.audioSource?.mode == "process" else { return nil }
        guard let pid = snapshot.audioSource?.processId,
              let app = NSRunningApplication(processIdentifier: pid_t(pid)), !app.isTerminated,
              let bundle = app.bundleIdentifier, !bundle.isEmpty else { return "" }
        return bundle
    }
    private func forwardMedia(_ item: NowPlayingItem?) {
        guard connected, !preview, !quitting else { return }
        guard let item, media.enabled else {
            send("media", value: NSNull()); return
        }
        if let captureBundle = selectedCaptureBundle, item.bundleIdentifier != captureBundle {
            send("media", value: NSNull()); return
        }
        func milliseconds(_ value: Double?) -> Int64 {
            guard let value, value.isFinite else { return 0 }
            return Int64(min(Double(Int32.max), max(0, value * 1000)))
        }
        send("media", value: [
            "title": String(item.title.prefix(512)), "artist": String(item.artist.prefix(512)),
            "album": String(item.album.prefix(512)), "source": String(item.source.prefix(512)),
            "durationMilliseconds": milliseconds(item.duration),
            "positionMilliseconds": milliseconds(item.position(at: Date())),
            "playing": item.playing, "playbackRate": min(4, max(0, item.playbackRate)),
            "artworkBase64": item.artworkBase64,
            "lyrics": item.lyrics
        ])
    }
    private func stopMediaForwarding() {
        mediaTimer?.invalidate(); mediaTimer = nil; mediaSubscription?.cancel(); mediaSubscription = nil
    }
    func stopAll() { send("stopAll") }
    func shutdown(_ completion: @escaping () -> Void) {
        delayTimer?.invalidate(); delayTimer = nil; pendingDelays.removeAll()
        stopMediaForwarding()
        media.stop()
        guard let process, process.isRunning else { completion(); return }
        quitting = true; onExit = completion
        if let sleepObserver { NSWorkspace.shared.notificationCenter.removeObserver(sleepObserver) }
        let handle = input
        writer.async { try? handle?.close() } // EOF triggers the engine's awaited cleanup.
        DispatchQueue.main.asyncAfter(deadline: .now() + 8) { [weak self] in
            guard let self, self.process?.isRunning == true else { return }
            self.process?.terminate()
        }
    }
}
