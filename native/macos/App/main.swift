import AppKit
import SwiftUI

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    let engine: EngineClient
    var window: NSWindow!
    var statusItem: NSStatusItem?
    init(preview: Bool) { engine = EngineClient(preview: preview) }
    func applicationDidFinishLaunching(_ notification: Notification) {
        let menu = NSMenu(), appMenu = NSMenu(), item = NSMenuItem()
        appMenu.addItem(withTitle: "显示投放面板", action: #selector(showWindow), keyEquivalent: "0").target = self
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "退出 MiPlay", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        item.submenu = appMenu; menu.addItem(item); NSApp.mainMenu = menu
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 480, height: 760),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView], backing: .buffered, defer: false)
        window.title = "MiPlay"; window.titleVisibility = .hidden; window.titlebarAppearsTransparent = true
        window.backgroundColor = .clear; window.isOpaque = false
        window.appearance = CommandLine.arguments.contains("--dark") ? NSAppearance(named: .darkAqua) : CommandLine.arguments.contains("--light") ? NSAppearance(named: .aqua) : nil
        if CommandLine.arguments.contains("--light") {
            window.contentView = NSHostingView(rootView: CastPanel(engine: engine).environment(\.colorScheme, .light))
        } else if CommandLine.arguments.contains("--dark") {
            window.contentView = NSHostingView(rootView: CastPanel(engine: engine).environment(\.colorScheme, .dark))
        } else { window.contentView = NSHostingView(rootView: CastPanel(engine: engine)) }
        window.minSize = NSSize(width: 420, height: 650); window.delegate = self; window.isReleasedWhenClosed = false
        window.center(); window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true)
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem?.button?.image = NSImage(systemSymbolName: "hifispeaker.2", accessibilityDescription: "MiPlay 声音投放")
        let statusMenu = NSMenu()
        statusMenu.addItem(withTitle: "显示投放面板", action: #selector(showWindow), keyEquivalent: "").target = self
        statusMenu.addItem(withTitle: "停止全部投放", action: #selector(stopAll), keyEquivalent: "").target = self
        statusMenu.addItem(.separator())
        statusMenu.addItem(withTitle: "退出 MiPlay", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        statusItem?.menu = statusMenu
        engine.start()
        if let index = CommandLine.arguments.firstIndex(of: "--snapshot"), CommandLine.arguments.count > index + 1 {
            let path = CommandLine.arguments[index + 1]
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [self] in
                if CommandLine.arguments.contains("--screen-snapshot") {
                    let capture = Process()
                    capture.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
                    capture.arguments = ["-x", "-o", "-l", String(window.windowNumber), path]
                    do { try capture.run(); capture.waitUntilExit(); exit(capture.terminationStatus) }
                    catch { fputs("\(error)\n", stderr); exit(1) }
                }
                guard let view = window.contentView, let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { exit(1) }
                view.cacheDisplay(in: view.bounds, to: bitmap)
                do { try bitmap.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: path)); exit(0) }
                catch { fputs("\(error)\n", stderr); exit(1) }
            }
        }
    }
    @objc func showWindow() { window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true) }
    @objc func stopAll() { engine.stopAll() }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { showWindow(); return true }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        engine.shutdown { DispatchQueue.main.async { sender.reply(toApplicationShouldTerminate: true) } }
        return .terminateLater
    }
}

if CommandLine.arguments.contains("--media-check") {
    let monitor = NowPlayingMonitor()
    monitor.start()
    RunLoop.main.run(until: Date().addingTimeInterval(2.5))
    print("Media query: \(monitor.availability); title=\(monitor.item != nil); artwork=\(monitor.item?.artwork != nil)")
    monitor.stop()
    exit(0)
}
if CommandLine.arguments.contains("--self-check") {
    let child = Process()
    child.executableURL = Bundle.main.executableURL!.deletingLastPathComponent().appendingPathComponent("MiPlay.Engine")
    child.arguments = ["--self-check"]
    do { try child.run(); child.waitUntilExit(); exit(child.terminationStatus) }
    catch { fputs("\(error)\n", stderr); exit(1) }
}
let app = NSApplication.shared
app.setActivationPolicy(.regular)
let delegate = AppDelegate(preview: CommandLine.arguments.contains("--preview") || CommandLine.arguments.contains("--snapshot"))
app.delegate = delegate
app.run()
