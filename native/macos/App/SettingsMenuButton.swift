import AppKit
import SwiftUI

// Keep the tracking NSMenu independent of frequently published media/device state.
// SwiftUI updates only the coordinator; rebuild menu items at the next opening.
struct SettingsMenuButton: NSViewRepresentable {
    let engine: EngineClient
    func makeCoordinator() -> Coordinator { Coordinator(engine: engine) }
    func makeNSView(context: Context) -> NSButton {
        let button = NSButton(image: NSImage(systemSymbolName: "ellipsis", accessibilityDescription: "更多设置")!,
                              target: context.coordinator, action: #selector(Coordinator.open(_:)))
        button.isBordered = false
        button.setAccessibilityLabel("更多设置")
        button.imageScaling = .scaleNone
        return button
    }
    func updateNSView(_ button: NSButton, context: Context) { context.coordinator.engine = engine }

    final class Coordinator: NSObject {
        var engine: EngineClient
        init(engine: EngineClient) { self.engine = engine }
        func makeMenu() -> NSMenu {
            let menu = NSMenu()
            menu.autoenablesItems = false
            func add(_ title: String, _ action: Selector, checked: Bool = false, enabled: Bool = true, tag: Int = 0) {
                let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
                item.target = self; item.state = checked ? .on : .off; item.isEnabled = enabled; item.tag = tag
                menu.addItem(item)
            }
            add("投放设置…", #selector(showSettings))
            menu.addItem(.separator())
            add("显示系统媒体信息（实验）", #selector(toggleMedia), checked: engine.media.enabled)
            add("读取 Apple Music 歌词（实验）", #selector(toggleLyrics), checked: engine.media.lyricsEnabled)
            if engine.media.lyricsEnabled {
                let status = NSMenuItem(title: engine.media.lyricsStatus, action: nil, keyEquivalent: "")
                status.isEnabled = false; menu.addItem(status)
            }
            add("同步投放（切换后需重新选音箱）", #selector(toggleSync),
                checked: engine.snapshot.synchronizedPlayback, enabled: engine.connected)
            menu.addItem(.separator())
            let heading = NSMenuItem(title: "投放音质", action: nil, keyEquivalent: "")
            heading.isEnabled = false; menu.addItem(heading)
            for (rate, label) in [(256000, "256 kbps · 标准"), (320000, "320 kbps · 高音质"), (512000, "512 kbps · 实验性")] {
                add(label, #selector(selectQuality(_:)), checked: engine.snapshot.audioBitRate == rate,
                    enabled: engine.connected, tag: rate)
            }
            let hint = NSMenuItem(title: "切换音质会停止投放，需重新选择音箱", action: nil, keyEquivalent: "")
            hint.isEnabled = false; menu.addItem(hint)
            menu.addItem(.separator())
            add("系统音频权限…", #selector(openPermission))
            add("导出诊断…", #selector(exportDiagnostics), enabled: engine.connected)
            return menu
        }
        @objc func open(_ sender: NSButton) {
            makeMenu().popUp(positioning: nil, at: NSPoint(x: 0, y: sender.bounds.minY - 4), in: sender)
        }
        @objc func showSettings() { NotificationCenter.default.post(name: .miplayShowSettings, object: nil) }
        @objc func toggleMedia() { engine.media.enabled.toggle() }
        @objc func toggleLyrics() { engine.media.lyricsEnabled.toggle() }
        @objc func toggleSync() { engine.send("synchronizedPlayback", value: !engine.snapshot.synchronizedPlayback) }
        @objc func selectQuality(_ sender: NSMenuItem) { engine.send("audioBitRate", value: sender.tag) }
        @objc func exportDiagnostics() { engine.send("export") }
        @objc func openPermission() {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
        }
    }
}
