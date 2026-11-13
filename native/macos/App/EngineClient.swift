import AppKit
import SwiftUI

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
}
struct DesktopSnapshot: Codable {
    var revision: Int64 = 0
    var speakers: [SpeakerCard] = []
    var scanning = false
    var discoveryMessage = "正在查找附近音箱…"
    var muteLocalOutput = false
    var supportsLocalMute = true
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
    let media = NowPlayingMonitor()
    let preview: Bool

    init(preview: Bool = false) {
        self.preview = preview
        if preview {
            media.setPreview()
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
        }
    }
    func start() {
        guard !preview, process == nil else { return }
        let executable = Bundle.main.executableURL!.deletingLastPathComponent().appendingPathComponent("MiPlay.Engine")
        let child = Process(), stdin = Pipe(), stdout = Pipe(), stderr = Pipe()
        child.executableURL = executable
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
                if let snapshot = event.snapshot, snapshot.revision >= self.snapshot.revision { self.snapshot = snapshot }
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
    func stopAll() { send("stopAll") }
    func shutdown(_ completion: @escaping () -> Void) {
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
