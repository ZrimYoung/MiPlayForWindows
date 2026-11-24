import AppKit
import SwiftUI
import Combine
import ImageIO
import CoreImage
import CryptoKit

/// Appearance choices are independent of audio and media transmission settings.
enum PlaybackTheme: String, CaseIterable, Identifiable {
    case system, light, dark
    var id: String { rawValue }
    var label: String {
        switch self { case .system: return "跟随系统"; case .light: return "日间"; case .dark: return "夜间" }
    }
}
enum PlaybackBackgroundStyle: String, CaseIterable, Identifiable {
    case artwork, nativeGlass
    var id: String { rawValue }
    var label: String {
        switch self { case .artwork: return "专辑氛围"; case .nativeGlass: return "原生毛玻璃" }
    }
}

final class AppearancePreferences: ObservableObject {
    static let shared = AppearancePreferences()
    static let themeKey = "playbackAppearanceTheme"
    static let backgroundKey = "playbackAppearanceBackground"
    let persistsChanges: Bool
    @Published var theme: PlaybackTheme {
        didSet {
            if persistsChanges { UserDefaults.standard.set(theme.rawValue, forKey: Self.themeKey) }
            apply()
        }
    }
    @Published var background: PlaybackBackgroundStyle {
        didSet {
            if persistsChanges { UserDefaults.standard.set(background.rawValue, forKey: Self.backgroundKey) }
        }
    }
    private init() {
        let arguments = CommandLine.arguments
        let preview = ["--preview", "--snapshot", "--popover-check", "--settings-menu-check", "--appearance-check", "--artwork-fixture", "--light", "--dark", "--native-glass"].contains { arguments.contains($0) }
        persistsChanges = !preview
        let storedTheme = PlaybackTheme(rawValue: UserDefaults.standard.string(forKey: Self.themeKey) ?? "") ?? .system
        theme = arguments.contains("--dark") ? .dark : arguments.contains("--light") ? .light : storedTheme
        background = arguments.contains("--native-glass") ? .nativeGlass : PlaybackBackgroundStyle(rawValue: UserDefaults.standard.string(forKey: Self.backgroundKey) ?? "") ?? .artwork
    }
    func apply() {
        switch theme {
        case .system: NSApp.appearance = nil
        case .light: NSApp.appearance = NSAppearance(named: .aqua)
        case .dark: NSApp.appearance = NSAppearance(named: .darkAqua)
        }
    }
}

/// Intended as the contents of the settings detail Form, without an embedded Form.
struct AppearanceSettingsView: View {
    @ObservedObject private var preferences = AppearancePreferences.shared
    var body: some View {
        Section {
            Picker("外观", selection: $preferences.theme) {
                ForEach(PlaybackTheme.allCases) { theme in Text(theme.label).tag(theme) }
            }.pickerStyle(.segmented)
            Text("主窗口、菜单栏浮窗和设置窗口立即采用所选外观。跟随系统会随系统深浅色切换。")
                .font(.caption).foregroundStyle(.secondary)
        } header: { Label("外观", systemImage: "circle.lefthalf.filled") }
        Section {
            Picker("背景", selection: $preferences.background) {
                ForEach(PlaybackBackgroundStyle.allCases) { style in Text(style.label).tag(style) }
            }.pickerStyle(.radioGroup)
            Text(preferences.background == .artwork
                ? "从当前专辑封面生成柔和的模糊背景。没有封面时，日间为纯白，夜间为纯深色。"
                : "使用 macOS 原生毛玻璃，随窗口后方的内容变化。")
                .font(.caption).foregroundStyle(.secondary)
            Text("系统开启“减少透明度”时，使用纯色背景。")
                .font(.caption).foregroundStyle(.secondary)
        } header: { Label("背景", systemImage: "photo.on.rectangle.angled") }
    }
}

/// Downsample and blur once per artwork change. The view only stretches a tiny
/// prepared bitmap, instead of decoding or blurring a full cover during redraw.
private final class ArtworkBackdropModel: ObservableObject {
    @Published private(set) var image: NSImage?
    @Published private(set) var imageKey = ""
    private var subscriptions: [AnyCancellable] = []
    private var sourceImage: ObjectIdentifier?
    private var sourceIdentity = ""
    private var sourceEncoded = ""
    private var revision = 0
    private static let worker = DispatchQueue(label: "MiPlay.artwork-background", qos: .utility)
    private static let cache: NSCache<NSString, NSImage> = {
        let cache = NSCache<NSString, NSImage>(); cache.countLimit = 4; cache.totalCostLimit = 512 * 1024
        return cache
    }()
    init(media: NowPlayingMonitor) {
        Publishers.CombineLatest(media.$item, AppearancePreferences.shared.$background)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] item, style in self?.accept(item, style: style) }.store(in: &subscriptions)
    }
    private func accept(_ item: NowPlayingItem?, style: PlaybackBackgroundStyle) {
        guard style == .artwork, let item, let artwork = item.artwork else {
            revision += 1; sourceImage = nil; sourceIdentity = ""; sourceEncoded = ""
            if image != nil { image = nil; imageKey = "" }
            return
        }
        let object = ObjectIdentifier(artwork), encoded = item.artworkBase64
        if !encoded.isEmpty, encoded == sourceEncoded { sourceImage = object; sourceIdentity = item.identity; return }
        guard sourceImage != object || sourceIdentity != item.identity || sourceEncoded != encoded else { return }
        revision += 1
        let request = revision, identity = item.identity
        sourceImage = object; sourceIdentity = identity; sourceEncoded = encoded
        Self.worker.async { [weak self] in
            let prepared: (NSImage, String)? = autoreleasepool {
                let bytes = Data(base64Encoded: encoded)
                let key = bytes.map { SHA256.hash(data: $0).map { String(format: "%02x", $0) }.joined() }
                    ?? "\(identity):\(object)"
                if let cached = Self.cache.object(forKey: key as NSString) { return (cached, key) }
                let thumbnail: CGImage?
                if let bytes, !bytes.isEmpty, bytes.count <= MediaSnapshot.maxArtworkBytes,
                   let source = CGImageSourceCreateWithData(bytes as CFData, nil) {
                    thumbnail = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                        kCGImageSourceCreateThumbnailFromImageAlways: true,
                        kCGImageSourceCreateThumbnailWithTransform: true,
                        kCGImageSourceThumbnailMaxPixelSize: 48,
                        kCGImageSourceShouldCacheImmediately: true
                    ] as CFDictionary)
                } else {
                    // Preview fixtures and players with only an in-memory cover.
                    thumbnail = artwork.cgImage(forProposedRect: nil, context: nil, hints: nil)
                }
                guard let thumbnail,
                      let context = CGContext(data: nil, width: 48, height: 48, bitsPerComponent: 8,
                        bytesPerRow: 48 * 4, space: CGColorSpaceCreateDeviceRGB(),
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
                context.interpolationQuality = .medium
                context.draw(thumbnail, in: CGRect(x: 0, y: 0, width: 48, height: 48))
                guard let tiny = context.makeImage() else { return nil }
                let input = CIImage(cgImage: tiny)
                let blurred = input.clampedToExtent().applyingFilter("CIGaussianBlur", parameters: [kCIInputRadiusKey: 9])
                let renderer = CIContext(options: [.useSoftwareRenderer: true, .cacheIntermediates: false])
                guard let result = renderer.createCGImage(blurred, from: input.extent) else { return nil }
                let image = NSImage(cgImage: result, size: NSSize(width: 48, height: 48))
                Self.cache.setObject(image, forKey: key as NSString, cost: 48 * 48 * 4)
                return (image, key)
            }
            DispatchQueue.main.async {
                guard let self, self.revision == request, let prepared, self.imageKey != prepared.1 else { return }
                self.image = prepared.0; self.imageKey = prepared.1
            }
        }
    }
}

struct PlaybackBackground: View {
    @ObservedObject private var preferences = AppearancePreferences.shared
    @StateObject private var artwork: ArtworkBackdropModel
    @Environment(\.colorScheme) private var scheme
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    private let compact: Bool
    init(media: NowPlayingMonitor, compact: Bool = false) {
        _artwork = StateObject(wrappedValue: ArtworkBackdropModel(media: media)); self.compact = compact
    }
    private var plain: Color { scheme == .dark ? Color(red: 0.055, green: 0.06, blue: 0.07) : .white }
    var body: some View {
        GeometryReader { geometry in
            ZStack {
                if reduceTransparency { plain }
                else if preferences.background == .nativeGlass { NativePlaybackGlass(compact: compact) }
                else {
                    plain
                    if let image = artwork.image {
                        Image(nsImage: image).resizable().interpolation(.high)
                            .scaledToFill().frame(width: geometry.size.width, height: geometry.size.height).clipped()
                            .opacity(scheme == .dark ? 0.56 : 0.32).id(artwork.imageKey).transition(.opacity)
                        (scheme == .dark ? Color.black : Color.white).opacity(scheme == .dark ? 0.24 : 0.20)
                    }
                }
            }.clipped()
                .animation(reduceMotion ? nil : .easeInOut(duration: 0.4), value: artwork.imageKey)
                .animation(reduceMotion ? nil : .easeInOut(duration: 0.2), value: preferences.background)
        }.ignoresSafeArea().allowsHitTesting(false)
    }
}

private struct NativePlaybackGlass: NSViewRepresentable {
    let compact: Bool
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = compact ? .popover : .sidebar
        view.blendingMode = .behindWindow; view.state = .active
        return view
    }
    func updateNSView(_ view: NSVisualEffectView, context: Context) { view.material = compact ? .popover : .sidebar }
}
