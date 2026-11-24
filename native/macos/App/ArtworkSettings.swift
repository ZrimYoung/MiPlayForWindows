import AppKit
import Combine
import Darwin

/// Interactive previews keep settings in memory, including existing media switches.
enum DesktopPreferences {
    static var preview: Bool {
        ["--preview", "--snapshot", "--popover-check", "--settings-menu-check", "--appearance-check", "--artwork-fixture", "--media-check"]
            .contains { CommandLine.arguments.contains($0) }
    }
    static func set(_ value: Any, forKey key: String) {
        if preview {
            var values = UserDefaults.standard.volatileDomain(forName: UserDefaults.argumentDomain)
            values[key] = value
            UserDefaults.standard.setVolatileDomain(values, forName: UserDefaults.argumentDomain)
        } else { UserDefaults.standard.set(value, forKey: key) }
    }
}

enum ArtworkFormat: String, CaseIterable {
    case jpeg, webp, png
    var label: String {
        switch self { case .jpeg: return "JPEG · 兼容"; case .webp: return "WebP · 实验"; case .png: return "PNG · 实验" }
    }
}

struct ArtworkOptions: Equatable, Hashable {
    var format: ArtworkFormat = .jpeg
    var maxDimension = 1024
    var maxBytes = 256 * 1024
    var quality = 90
    var preserveOriginal = true
    static let dimensions = [256, 512, 1024, 2048]
    static let byteLimits = [48 * 1024, 128 * 1024, 256 * 1024, 512 * 1024, 1024 * 1024]
    static let qualities = [80, 90, 100]
    static let preferenceKey = "artworkTransmission"
    init() {}
    init(dictionary: [String: Any]) {
        if let raw = dictionary["format"] as? String, let value = ArtworkFormat(rawValue: raw) { format = value }
        if let value = dictionary["maxDimension"] as? Int, Self.dimensions.contains(value) { maxDimension = value }
        if let value = dictionary["maxBytes"] as? Int, Self.byteLimits.contains(value) { maxBytes = value }
        if let value = dictionary["quality"] as? Int, Self.qualities.contains(value) { quality = value }
        if let value = dictionary["preserveOriginal"] as? Bool { preserveOriginal = value }
    }
    var dictionary: [String: Any] { ["format":format.rawValue, "maxDimension":maxDimension,
        "maxBytes":maxBytes, "quality":quality, "preserveOriginal":preserveOriginal] }
    static func load() -> Self { Self(dictionary: UserDefaults.standard.dictionary(forKey: preferenceKey) ?? [:]) }
    var cacheKey: String { "\(format.rawValue):\(maxDimension):\(maxBytes):\(quality):\(preserveOriginal)" }
}

final class ArtworkPreferences: ObservableObject {
    static let shared = ArtworkPreferences()
    static let changed = Notification.Name("MiPlayArtworkPreferencesChanged")
    @Published var options = ArtworkOptions.load() {
        didSet {
            guard oldValue != options else { return }
            DesktopPreferences.set(options.dictionary, forKey: ArtworkOptions.preferenceKey)
            NotificationCenter.default.post(name: Self.changed, object: nil)
        }
    }
}

/// The app bundles the pinned upstream codec; no Homebrew or system library lookup.
enum WebPArtworkEncoder {
    private typealias Encode = @convention(c) (UnsafePointer<UInt8>?, Int32, Int32, Int32, Float,
        UnsafeMutablePointer<UnsafeMutablePointer<UInt8>?>?) -> Int
    private typealias Free = @convention(c) (UnsafeMutableRawPointer?) -> Void
    private static let handle: UnsafeMutableRawPointer? = {
        guard let directory = Bundle.main.executableURL?.deletingLastPathComponent() else { return nil }
        return dlopen(directory.appendingPathComponent("libMiPlayWebP.dylib").path, RTLD_NOW | RTLD_LOCAL)
    }()
    private static let encode: Encode? = handle.flatMap { dlsym($0, "WebPEncodeRGBA") }.map { unsafeBitCast($0, to: Encode.self) }
    private static let free: Free? = handle.flatMap { dlsym($0, "WebPFree") }.map { unsafeBitCast($0, to: Free.self) }
    static var available: Bool { encode != nil && free != nil }
    static func data(context: CGContext, quality: Int, maxBytes: Int) -> Data? {
        guard let encode, let free, let pixels = context.data, context.width <= 2048, context.height <= 2048 else { return nil }
        var output: UnsafeMutablePointer<UInt8>?
        let count = encode(pixels.assumingMemoryBound(to: UInt8.self), Int32(context.width), Int32(context.height),
                           Int32(context.bytesPerRow), Float(quality), &output)
        defer { if let output { free(UnsafeMutableRawPointer(output)) } }
        guard let output, count > 0, count <= maxBytes else { return nil }
        return Data(bytes: output, count: count)
    }
}
