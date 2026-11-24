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
            add("重新扫描音箱", #selector(scan), enabled: engine.connected)
            add("停止全部投放", #selector(stopAll), enabled: engine.connected && engine.snapshot.speakers.contains { $0.selected })
            menu.addItem(.separator())
            add("系统音频权限…", #selector(openPermission))
            add("导出诊断…", #selector(exportDiagnostics), enabled: engine.connected)
            return menu
        }
        @objc func open(_ sender: NSButton) {
            makeMenu().popUp(positioning: nil, at: NSPoint(x: 0, y: sender.bounds.minY - 4), in: sender)
        }
        @objc func showSettings() { NotificationCenter.default.post(name: .miplayShowSettings, object: nil) }
        @objc func scan() { engine.send("scan") }
        @objc func stopAll() { engine.stopAll() }
        @objc func exportDiagnostics() { engine.send("export") }
        @objc func openPermission() {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
        }
    }
}
