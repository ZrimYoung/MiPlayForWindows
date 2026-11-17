import AppKit
import ImageIO
import CryptoKit
import Darwin

/// Full snapshots only. Artwork decoding runs on the reader queue, never the UI thread.
enum MediaSnapshot {
    static let maxFrameBytes = 12 * 1024 * 1024
    static let maxArtworkBytes = 8 * 1024 * 1024

    static let maxForwardedArtworkBytes = 1024 * 1024
    static let maxArtworkDimension = 1024
    private final class PreparedArtwork: NSObject {
        let image: NSImage
        let encoded: String
        let summary: String
        init(image: NSImage, data: Data, summary: String) {
            self.image = image; self.encoded = data.base64EncodedString(); self.summary = summary
        }
    }
    private static let artworkCache: NSCache<NSString, PreparedArtwork> = {
        let cache = NSCache<NSString, PreparedArtwork>()
        cache.countLimit = 2; cache.totalCostLimit = 40 * 1024 * 1024
        return cache
    }()

    // Compatibility helper for existing checks; production uses the selected options.
    static func jpegArtwork(_ image: NSImage?) -> String {
        guard let image else { return "" }
        return encodeArtwork(image, options: ArtworkOptions()).data?.base64EncodedString() ?? ""
    }
    static func encodeArtwork(_ image: NSImage, options: ArtworkOptions) -> (data: Data?, summary: String) {
        guard let source = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return (nil, "封面不可用") }
        let unavailable = options.format == .webp && !WebPArtworkEncoder.available
        let format: ArtworkFormat = unavailable ? .jpeg : options.format
        let edges = ([options.maxDimension] + [1024, 768, 512, 384, 256, 128].filter { $0 < options.maxDimension })
        for edge in edges {
            let ratio = min(1, Double(edge) / Double(max(source.width, source.height)))
            let width = max(1, Int(Double(source.width) * ratio)), height = max(1, Int(Double(source.height) * ratio))
            guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGBitmapInfo.byteOrder32Big.rawValue | CGImageAlphaInfo.premultipliedLast.rawValue) else { return (nil, "封面不可用") }
            context.setFillColor(CGColor(gray: 1, alpha: 1)); context.fill(CGRect(x: 0, y: 0, width: width, height: height))
            context.interpolationQuality = .high
            context.draw(source, in: CGRect(x: 0, y: 0, width: width, height: height))
            guard let scaled = context.makeImage() else { return (nil, "封面不可用") }
            let bitmap = NSBitmapImageRep(cgImage: scaled)
            let qualities = format == .png ? [options.quality] : [options.quality, max(60, options.quality - 10), max(50, options.quality - 20)]
            for quality in qualities {
                let data: Data?
                switch format {
                case .jpeg: data = bitmap.representation(using: .jpeg, properties: [.compressionFactor: Double(quality) / 100])
                case .png: data = bitmap.representation(using: .png, properties: [:])
                case .webp: data = WebPArtworkEncoder.data(context: context, quality: quality, maxBytes: options.maxBytes)
                }
                if let data, data.count <= options.maxBytes {
                    let note = unavailable ? " · WebP 编码不可用，已改用 JPEG" : " · 已编码"
                    return (data, "\(width)×\(height) · \(format.rawValue.uppercased()) · \(data.count / 1024) KB" + note)
                }
            }
        }
        return (nil, "封面超过大小限制，暂不发送")
    }

    static func fullSnapshot(from line: Data) -> [String: Any]? {
        guard let event = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any],
              event["type"] as? String == "data", event["diff"] as? Bool == false else { return nil }
        if event["payload"] is NSNull { return [:] }
        return event["payload"] as? [String: Any]
    }

    static func decode(_ values: [String: Any], artworkOptions: ArtworkOptions = ArtworkOptions()) -> NowPlayingItem? {
        guard let title = values["title"] as? String, !title.isEmpty else { return nil }
        func text(_ key: String) -> String { values[key] as? String ?? "" }
        func number(_ key: String) -> Double? {
            guard let value = values[key] as? NSNumber, value.doubleValue.isFinite,
                  value.doubleValue >= 0 else { return nil }
            return value.doubleValue
        }
        let bundle = text("parentApplicationBundleIdentifier").isEmpty ? text("bundleIdentifier") : text("parentApplicationBundleIdentifier")
        var source = "系统媒体"
        if let name = NSWorkspace.shared.runningApplications.first(where: { $0.bundleIdentifier == bundle })?.localizedName { source = name }
        var artwork: NSImage?
        var artworkBase64 = ""
        var artworkSummary = "当前没有封面"
        if let encoded = values["artworkData"] as? String, encoded.utf8.count <= (maxArtworkBytes + 2) / 3 * 4,
           let data = Data(base64Encoded: encoded), data.count <= maxArtworkBytes {
            let key = (SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() + artworkOptions.cacheKey) as NSString
            var prepared = artworkCache.object(forKey: key)
            if prepared == nil, let imageSource = CGImageSourceCreateWithData(data as CFData, nil),
               let cgImage = CGImageSourceCreateThumbnailAtIndex(imageSource, 0, [
                   kCGImageSourceCreateThumbnailFromImageAlways: true,
                   kCGImageSourceCreateThumbnailWithTransform: true,
                   kCGImageSourceThumbnailMaxPixelSize: artworkOptions.maxDimension,
                   kCGImageSourceShouldCacheImmediately: true
               ] as CFDictionary) {
                let image = NSImage(cgImage: cgImage, size: .zero)
                let properties = CGImageSourceCopyPropertiesAtIndex(imageSource, 0, nil) as? [CFString: Any] ?? [:]
                let width = properties[kCGImagePropertyPixelWidth] as? Int ?? 0
                let height = properties[kCGImagePropertyPixelHeight] as? Int ?? 0
                let orientation = properties[kCGImagePropertyOrientation] as? Int ?? 1
                let type = CGImageSourceGetType(imageSource) as String? ?? ""
                let wanted = [ArtworkFormat.jpeg: "public.jpeg", .png: "public.png", .webp: "org.webmproject.webp"][artworkOptions.format]!
                let result: (data: Data?, summary: String)
                if artworkOptions.preserveOriginal && type == wanted && width > 0 && height > 0 &&
                    max(width, height) <= artworkOptions.maxDimension && orientation == 1 &&
                    CGImageSourceGetCount(imageSource) == 1 && data.count <= artworkOptions.maxBytes {
                    result = (data, "\(width)×\(height) · \(artworkOptions.format.rawValue.uppercased()) · \(data.count / 1024) KB · 原图直传")
                } else { result = encodeArtwork(image, options: artworkOptions) }
                let value = PreparedArtwork(image: image, data: result.data ?? Data(), summary: result.summary)
                artworkCache.setObject(value, forKey: key, cost: cgImage.bytesPerRow * cgImage.height + value.encoded.utf8.count)
                prepared = value
            }
            artwork = prepared?.image; artworkBase64 = prepared?.encoded ?? ""
            artworkSummary = prepared?.summary ?? "当前封面无法读取"
        }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let timestamp = (values["timestamp"] as? String).flatMap {
            formatter.date(from: $0) ?? ISO8601DateFormatter().date(from: $0)
        } ?? Date()
        return NowPlayingItem(title: title, artist: text("artist"), album: text("album"), source: source,
                              artwork: artwork, duration: number("duration"), elapsed: number("elapsedTime"),
                              playing: (values["playing"] as? Bool) ?? ((number("playbackRate") ?? 0) > 0),
                              timestamp: timestamp, playbackRate: number("playbackRate") ?? 1,
                              bundleIdentifier: bundle,
                              trackIdentifier: String(describing: values["uniqueIdentifier"] ?? values["contentItemIdentifier"] ?? ""),
                              artworkBase64: artworkBase64, artworkSummary: artworkSummary)
    }
}

enum MediaPipeReader {
    /// A single POSIX read returns available bytes. Foundation's read(upToCount:) can wait to fill
    /// the requested length on a persistent pipe, delaying a partial artwork frame indefinitely.
    static func read(_ handle: FileHandle) throws -> Data {
        var bytes = Data(count: 64 * 1024)
        while true {
            let count = bytes.withUnsafeMutableBytes { Darwin.read(handle.fileDescriptor, $0.baseAddress, $0.count) }
            if count >= 0 { bytes.count = count; return bytes }
            if errno != EINTR { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        }
    }
}

/// Handles split UTF-8/JSON lines and rejects oversized frames before allocating an unbounded buffer.
struct MediaLineBuffer {
    private var buffer = Data()
    mutating func append(_ bytes: Data) throws -> [Data] {
        var lines: [Data] = []
        for part in bytes.split(separator: 10, omittingEmptySubsequences: false).enumerated() {
            if part.offset > 0 {
                if !buffer.isEmpty { lines.append(buffer) }
                buffer = Data()
            }
            guard buffer.count + part.element.count <= MediaSnapshot.maxFrameBytes else { throw FrameError.tooLarge }
            buffer.append(contentsOf: part.element)
        }
        return lines
    }
    enum FrameError: Error { case tooLarge }
}
