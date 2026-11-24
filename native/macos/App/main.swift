import AppKit
import SwiftUI

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSPopoverDelegate {
    let engine: EngineClient
    var window: NSWindow!
    private var settingsWindow: NSWindow?
    var statusItem: NSStatusItem?
    private let popover = NSPopover()
    private let statusMenu = NSMenu()
    init(preview: Bool) { engine = EngineClient(preview: preview) }
    func applicationDidFinishLaunching(_ notification: Notification) {
        AppearancePreferences.shared.apply()
        if engine.preview && CommandLine.arguments.contains("--artwork-fixture") { installArtworkFixture() }
        let menu = NSMenu(), appMenu = NSMenu(), item = NSMenuItem()
        appMenu.addItem(withTitle: "显示投放面板", action: #selector(showWindow), keyEquivalent: "0").target = self
        appMenu.addItem(withTitle: "设置…", action: #selector(showSettings), keyEquivalent: ",").target = self
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "退出 MiPlay", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        item.submenu = appMenu; menu.addItem(item); NSApp.mainMenu = menu
        let compactPreview = engine.preview && CommandLine.arguments.contains("--menu-preview")
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: compactPreview ? 400 : 480, height: 760),
                          styleMask: compactPreview ? [.borderless] : [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView], backing: .buffered, defer: false)
        window.title = "MiPlay"; window.titleVisibility = .hidden; window.titlebarAppearsTransparent = true
        window.backgroundColor = .clear; window.isOpaque = false
        let root = compactPreview ? AnyView(MenuBarPanel(engine: engine)) : AnyView(CastPanel(engine: engine))
        let host = NSHostingView(rootView: root)
        window.contentView = host
        if compactPreview { window.setContentSize(host.fittingSize) }
        else { window.minSize = NSSize(width: 420, height: 650) }
        window.delegate = self; window.isReleasedWhenClosed = false
        window.center(); window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true)
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem?.button?.image = NSImage(systemSymbolName: "hifispeaker.2", accessibilityDescription: "MiPlay 声音投放")
        statusItem?.button?.target = self
        statusItem?.button?.action = #selector(togglePopover)
        statusItem?.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])
        let panel = NSHostingController(rootView: MenuBarPanel(engine: engine))
        panel.sizingOptions = [.preferredContentSize]
        popover.contentViewController = panel
        popover.behavior = .transient; popover.delegate = self
        popover.animates = !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        statusMenu.addItem(withTitle: "显示投放面板", action: #selector(showWindow), keyEquivalent: "").target = self
        statusMenu.addItem(withTitle: "停止全部投放", action: #selector(stopAll), keyEquivalent: "").target = self
        statusMenu.addItem(withTitle: "设置…", action: #selector(showSettings), keyEquivalent: "").target = self
        statusMenu.addItem(.separator())
        statusMenu.addItem(withTitle: "退出 MiPlay", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        NotificationCenter.default.addObserver(self, selector: #selector(showSettings), name: .miplayShowSettings, object: nil)
        engine.start()
        if engine.preview && CommandLine.arguments.contains("--appearance-check") { checkAppearance() }
        if engine.preview && CommandLine.arguments.contains("--settings-preview") { showSettings(); window.orderOut(nil) }
        if engine.preview && CommandLine.arguments.contains("--settings-menu-check") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [self] in
                func findButton(_ view: NSView) -> NSButton? {
                    if let button = view as? NSButton, button.accessibilityLabel() == "更多设置" { return button }
                    return view.subviews.compactMap(findButton).first
                }
                guard let button = findButton(window.contentView!) else { exit(5) }
                var tracking: NSMenu?
                var ended = false
                var updates = 0
                let began = NotificationCenter.default.addObserver(forName: NSMenu.didBeginTrackingNotification, object: nil, queue: nil) {
                    tracking = $0.object as? NSMenu
                }
                let finished = NotificationCenter.default.addObserver(forName: NSMenu.didEndTrackingNotification, object: nil, queue: nil) { _ in ended = true }
                let timer = Timer(timeInterval: 0.05, repeats: true) { [self] _ in
                    updates += 1
                    engine.snapshot.revision += 1
                    engine.snapshot.scanning.toggle()
                    engine.media.item = NowPlayingItem(title: "刷新 \(updates)", artist: "测试", album: "", source: "测试", artwork: nil,
                                                      duration: 200, elapsed: Double(updates), playing: true, timestamp: Date())
                    if updates == 12 {
                        guard !ended, let menu = tracking,
                              menu.items.contains(where: { $0.title == "投放设置…" }),
                              menu.items.contains(where: { $0.title == "重新扫描音箱" }),
                              !menu.items.contains(where: { $0.title.contains("kbps") || $0.title.contains("读取 Apple Music") || $0.title.contains("显示系统媒体") }) else { exit(6) }
                        menu.cancelTracking()
                    }
                }
                RunLoop.main.add(timer, forMode: .common)
                button.performClick(nil)
                timer.invalidate()
                NotificationCenter.default.removeObserver(began); NotificationCenter.default.removeObserver(finished)
                guard updates >= 12 else { exit(7) }
                print("Quick menu stayed open across 12 media/device updates; settings entry retained without duplicate configuration actions")
                exit(0)
            }
        }
        if engine.preview && CommandLine.arguments.contains("--popover-check") {
            window.orderOut(nil)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [self] in
                togglePopover()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [self] in
                    let firstSize = popover.contentSize
                    guard popover.isShown, firstSize.width == 400, firstSize.height > 200,
                          let index = engine.snapshot.speakers.firstIndex(where: { !$0.selected }) else { exit(1) }
                    engine.snapshot.speakers[index].selected = true
                    engine.snapshot.speakers[index].state = "Casting"
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [self] in
                        guard popover.contentSize.height > firstSize.height else { exit(2) }
                        popover.animates = false
                        popover.performClose(nil)
                        guard !popover.isShown else { exit(3) }
                        togglePopover()
                        guard popover.isShown else { exit(4) }
                        popover.performClose(nil)
                        print("Popover: open, resize after selection, close and reopen passed; preview only")
                        exit(0)
                    }
                }
            }
        }
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
                guard let view = (CommandLine.arguments.contains("--settings-preview") ? settingsWindow : window)?.contentView, let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { exit(1) }
                view.cacheDisplay(in: view.bounds, to: bitmap)
                do { try bitmap.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: path)); exit(0) }
                catch { fputs("\(error)\n", stderr); exit(1) }
            }
        }
    }
    private func installArtworkFixture() {
        guard let context = CGContext(data: nil, width: 64, height: 64, bitsPerComponent: 8, bytesPerRow: 64 * 4,
            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }
        let colors = [CGColor(red: 0.88, green: 0.38, blue: 0.25, alpha: 1), CGColor(red: 0.19, green: 0.40, blue: 0.68, alpha: 1)] as CFArray
        if let gradient = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: colors, locations: [0, 1]) {
            context.drawLinearGradient(gradient, start: .zero, end: CGPoint(x: 64, y: 64), options: [])
        }
        guard let cg = context.makeImage() else { return }
        let image = NSImage(cgImage: cg, size: NSSize(width: 64, height: 64))
        let encoded = NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:])?.base64EncodedString() ?? ""
        engine.media.item = NowPlayingItem(title: "封面背景预览", artist: "模拟媒体", album: "外观测试", source: "预览",
            artwork: image, duration: 240, elapsed: 80, playing: true, artworkBase64: encoded)
    }
    private func checkAppearance() {
        let preferences = AppearancePreferences.shared
        let defaults = UserDefaults.standard
        let beforeTheme = defaults.string(forKey: AppearancePreferences.themeKey)
        let beforeBackground = defaults.string(forKey: AppearancePreferences.backgroundKey)
        preferences.theme = .light
        guard NSApp.appearance?.name == .aqua else { exit(20) }
        preferences.theme = .dark
        guard NSApp.appearance?.name == .darkAqua else { exit(21) }
        preferences.theme = .system
        guard NSApp.appearance == nil else { exit(22) }
        preferences.background = .nativeGlass; preferences.background = .artwork
        guard !preferences.persistsChanges, defaults.string(forKey: AppearancePreferences.themeKey) == beforeTheme,
              defaults.string(forKey: AppearancePreferences.backgroundKey) == beforeBackground else { exit(23) }
        print("Appearance: light, dark, system inheritance and preview preference isolation passed; no player commands")
        exit(0)
    }
    @objc func togglePopover() {
        if NSApp.currentEvent?.type == .rightMouseUp {
            popover.performClose(nil)
            statusItem?.menu = statusMenu
            statusItem?.button?.performClick(nil)
            statusItem?.menu = nil
            return
        }
        if popover.isShown { popover.performClose(nil); return }
        guard let button = statusItem?.button else { return }
        NSApp.activate(ignoringOtherApps: true)
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        popover.contentViewController?.view.window?.makeKey()
    }
    @objc func showSettings() {
        popover.performClose(nil)
        if settingsWindow == nil {
            let settings = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 860, height: 680),
                styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
            settings.title = "MiPlay 设置"; settings.isReleasedWhenClosed = false
            settings.minSize = NSSize(width: 760, height: 560)
            settings.contentView = NSHostingView(rootView: TransmissionSettingsView(engine: engine))
            settings.center(); settingsWindow = settings
        }
        NSApp.activate(ignoringOtherApps: true); settingsWindow?.makeKeyAndOrderFront(nil)
    }
    @objc func showWindow() { popover.performClose(nil); window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true) }
    @objc func stopAll() { engine.stopAll() }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { showWindow(); return true }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        engine.shutdown { DispatchQueue.main.async { sender.reply(toApplicationShouldTerminate: true) } }
        return .terminateLater
    }
}

if CommandLine.arguments.contains("--media-check") {
    // Diagnostic options stay in the process argument domain, never user defaults.
    if let index = CommandLine.arguments.firstIndex(of: "--artwork-format"), CommandLine.arguments.count > index + 1,
       let format = ArtworkFormat(rawValue: CommandLine.arguments[index + 1]) {
        var options = ArtworkOptions(); options.format = format; options.preserveOriginal = false
        UserDefaults.standard.setVolatileDomain([ArtworkOptions.preferenceKey: options.dictionary], forName: UserDefaults.argumentDomain)
    }
    if CommandLine.arguments.contains("--artwork-refresh-check") {
        UserDefaults.standard.setVolatileDomain([ArtworkOptions.preferenceKey: ArtworkOptions().dictionary], forName: UserDefaults.argumentDomain)
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) {
            var options = ArtworkOptions(); options.format = .webp; options.preserveOriginal = false
            UserDefaults.standard.setVolatileDomain([ArtworkOptions.preferenceKey: options.dictionary], forName: UserDefaults.argumentDomain)
            NotificationCenter.default.post(name: ArtworkPreferences.changed, object: nil)
        }
    }
    let highResolutionCheck = CommandLine.arguments.contains("--high-resolution-artwork-check")
    if highResolutionCheck {
        var options = ArtworkOptions(); options.maxDimension = 2048; options.maxBytes = 256 * 1024; options.format = .webp
        if let index = CommandLine.arguments.firstIndex(of: "--artwork-byte-limit"), CommandLine.arguments.count > index + 1,
           let cap = Int(CommandLine.arguments[index + 1]), ArtworkOptions.byteLimits.contains(cap) { options.maxBytes = cap }
        UserDefaults.standard.setVolatileDomain(["fetchAppleMusicArtwork": true, "showSystemMedia": true,
            "readAppleMusicLyrics": false, ArtworkOptions.preferenceKey: options.dictionary], forName: UserDefaults.argumentDomain)
    }
    let monitor = NowPlayingMonitor()
    monitor.start()
    RunLoop.main.run(until: Date().addingTimeInterval(highResolutionCheck ? 25 : 7))
    if highResolutionCheck { print("Direct cover: \(monitor.highResolutionArtworkStatus)") }
    print("Media query: \(monitor.availability); title=\(monitor.item != nil); artwork=\(monitor.item?.artwork != nil); album=\(monitor.item?.album.isEmpty == false); duration=\(monitor.item?.duration != nil); position=\(monitor.item?.elapsed != nil)")
    if let data = Data(base64Encoded: monitor.item?.artworkBase64 ?? ""),
       let bitmap = NSBitmapImageRep(data: data) {
        print("Forwarded cover: \(bitmap.pixelsWide)x\(bitmap.pixelsHigh) pixels; \(data.count) image bytes; \(monitor.item?.artworkSummary ?? "")")
    }
    monitor.stop()
    RunLoop.main.run(until: Date().addingTimeInterval(1.2))
    exit(0)
}
if CommandLine.arguments.contains("--self-check") {
    guard WebPArtworkEncoder.available else { fputs("Bundled WebP encoder is unavailable\n", stderr); exit(1) }
    let child = Process()
    child.executableURL = Bundle.main.executableURL!.deletingLastPathComponent().appendingPathComponent("MiPlay.Engine")
    child.arguments = ["--self-check"]
    do { try child.run(); child.waitUntilExit(); exit(child.terminationStatus) }
    catch { fputs("\(error)\n", stderr); exit(1) }
}
let app = NSApplication.shared
app.setActivationPolicy(.regular)
let delegate = AppDelegate(preview: CommandLine.arguments.contains("--preview") || CommandLine.arguments.contains("--snapshot") || CommandLine.arguments.contains("--popover-check") || CommandLine.arguments.contains("--settings-menu-check") || CommandLine.arguments.contains("--appearance-check") || CommandLine.arguments.contains("--artwork-fixture"))
app.delegate = delegate
app.run()
