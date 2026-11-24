import Foundation
import SQLite3

/// Reads Music's existing response cache by an exact catalogue ID. No requests,
/// credentials, database writes, title search or cross-provider matching.
enum AppleMusicCachedLyrics {
    static let maxBodyBytes = 2 * 1024 * 1024
    static func read(catalogID: String, duration: Double, cache: URL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Caches/com.apple.Music")) -> String? {
        guard catalogID.range(of: #"^[1-9][0-9]{0,19}$"#, options: .regularExpression) != nil,
              duration.isFinite, duration > 0, duration <= 86400 else { return nil }
        var database: OpaquePointer?
        guard sqlite3_open_v2(cache.appendingPathComponent("Cache.db").path, &database, SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX, nil) == SQLITE_OK else {
            if database != nil { sqlite3_close(database) }; return nil
        }
        defer { sqlite3_close(database) }
        sqlite3_busy_timeout(database, 50)
        let sql = """
        SELECT d.isDataOnFS, d.receiver_data FROM cfurl_cache_response r
        JOIN cfurl_cache_receiver_data d ON r.entry_ID=d.entry_ID
        WHERE r.request_key LIKE 'https://amp-api.music.apple.com/v1/catalog/%'
          AND r.request_key LIKE '%lyric%'
        ORDER BY r.entry_ID DESC LIMIT 32
        """
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database, sql, -1, &statement, nil) == SQLITE_OK else { return nil }
        defer { sqlite3_finalize(statement) }
        while sqlite3_step(statement) == SQLITE_ROW {
            let body: Data
            if sqlite3_column_int(statement, 0) != 0 {
                guard let pointer = sqlite3_column_text(statement, 1) else { continue }
                let name = String(cString: pointer)
                guard !name.isEmpty, name != ".", name != "..", !name.contains("/"), !name.contains("\\") else { continue }
                let folder = cache.appendingPathComponent("fsCachedData").resolvingSymlinksInPath()
                let file = folder.appendingPathComponent(name).resolvingSymlinksInPath()
                guard file.deletingLastPathComponent() == folder,
                      let properties = try? file.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey]),
                      properties.isRegularFile == true, let size = properties.fileSize, size <= maxBodyBytes,
                      let handle = try? FileHandle(forReadingFrom: file) else { continue }
                defer { try? handle.close() }
                guard let bytes = try? handle.read(upToCount: maxBodyBytes + 1), bytes.count <= maxBodyBytes else { continue }
                body = bytes
            } else {
                let size = Int(sqlite3_column_bytes(statement, 1))
                guard size > 0, size <= maxBodyBytes, let pointer = sqlite3_column_blob(statement, 1) else { continue }
                body = Data(bytes: pointer, count: size)
            }
            if let text = decode(body, catalogID: catalogID, duration: duration) { return text }
        }
        return nil
    }

    static func decode(_ body: Data, catalogID: String, duration: Double) -> String? {
        guard body.count <= maxBodyBytes,
              let json = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any],
              let songs = json["data"] as? [[String: Any]] else { return nil }
        for song in songs where song["id"] as? String == catalogID && song["type"] as? String == "songs" {
            guard let metadata = song["attributes"] as? [String: Any],
                  let milliseconds = (metadata["durationInMillis"] as? NSNumber)?.doubleValue,
                  abs(milliseconds / 1000 - duration) <= 2,
                  let relationships = song["relationships"] as? [String: Any],
                  let lyrics = relationships["syllable-lyrics"] as? [String: Any],
                  let items = lyrics["data"] as? [[String: Any]], items.count == 1,
                  let attributes = items[0]["attributes"] as? [String: Any],
                  let xml = attributes["ttmlLocalizations"] as? String ?? attributes["ttml"] as? String else { continue }
            return AppleMusicTTML.lrc(xml, duration: duration)
        }
        return nil
    }
}

/// Preserve Music's line timing. Word spans flatten into their original line;
/// translations in <head> are excluded, never assigned guessed timestamps.
final class AppleMusicTTML: NSObject, XMLParserDelegate {
    private var bodyDepth = 0
    private var depth = 0
    private var lineDepth = 0
    private var lineStart: Double?
    private var lineText = ""
    private var invalid = false
    private var rootSeen = false
    private var lines: [(Double, String)] = []
    private let duration: Double
    private init(duration: Double) { self.duration = duration }

    static func lrc(_ xml: String, duration: Double) -> String? {
        guard duration.isFinite, duration > 0, duration <= 86400, xml.utf8.count <= 256 * 1024,
              xml.range(of: "<!DOCTYPE", options: .caseInsensitive) == nil,
              xml.range(of: "<!ENTITY", options: .caseInsensitive) == nil else { return nil }
        let delegate = AppleMusicTTML(duration: duration), parser = XMLParser(data: Data(xml.utf8))
        parser.shouldProcessNamespaces = true; parser.shouldResolveExternalEntities = false; parser.delegate = delegate
        guard parser.parse(), !delegate.invalid, delegate.rootSeen, !delegate.lines.isEmpty else { return nil }
        // Stable order for simultaneous lines (e.g. backing vocals).
        let lines = delegate.lines.enumerated().sorted { $0.element.0 == $1.element.0 ? $0.offset < $1.offset : $0.element.0 < $1.element.0 }
        let result = lines.map { _, line in
            let hundredths = Int((line.0 * 100).rounded())
            return String(format: "[%02d:%02d.%02d]", locale: Locale(identifier: "en_US_POSIX"), hundredths / 6000, hundredths / 100 % 60, hundredths % 100) + line.1
        }.joined(separator: "\n")
        return result.utf8.count <= AppleMusicLyrics.maxBytes && !result.contains("\0") ? result : nil
    }
    static func seconds(_ value: String?) -> Double? {
        guard let value, !value.isEmpty else { return nil }
        let result: Double?
        if value.hasSuffix("ms") { result = Double(value.dropLast(2)).map { $0 / 1000 } }
        else if value.hasSuffix("s") { result = Double(value.dropLast()) }
        else if value.contains(":") {
            let parts = value.split(separator: ":", omittingEmptySubsequences: false)
            guard (2...3).contains(parts.count), let last = Double(parts.last!), last >= 0, last < 60 else { return nil }
            var total = last
            for (index, part) in parts.dropLast().reversed().enumerated() {
                guard let integer = Int(part), integer >= 0, index != 0 || parts.count != 3 || integer < 60 else { return nil }
                total += Double(integer) * (index == 0 ? 60 : 3600)
            }
            result = total
        } else { result = Double(value) }
        guard let result, result.isFinite, result >= 0 else { return nil }; return result
    }
    func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?, attributes: [String: String]) {
        depth += 1
        if depth > 32 { invalid = true; parser.abortParsing(); return }
        if depth == 1 {
            rootSeen = name == "tt" && namespaceURI == "http://www.w3.org/ns/ttml"
            if !rootSeen { invalid = true; parser.abortParsing(); return }
        }
        if name == "body", namespaceURI == "http://www.w3.org/ns/ttml" { bodyDepth = depth }
        guard bodyDepth > 0 else { return }
        if name == "p" {
            guard lineDepth == 0, lines.count < 1000, let start = Self.seconds(attributes["begin"]), start <= duration + 2 else {
                invalid = true; parser.abortParsing(); return
            }
            if let end = attributes["end"], Self.seconds(end).map({ $0 >= start && $0 <= duration + 2 }) != true {
                invalid = true; parser.abortParsing(); return
            }
            lineDepth = depth; lineStart = start; lineText = ""
        } else if name == "br", lineDepth > 0 { lineText += " " }
    }
    func parser(_ parser: XMLParser, foundCharacters string: String) { if lineDepth > 0 { lineText += string } }
    func parser(_ parser: XMLParser, foundCDATA data: Data) {
        guard lineDepth > 0 else { return }
        guard let text = String(data: data, encoding: .utf8) else { invalid = true; parser.abortParsing(); return }
        lineText += text
    }
    func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
        if lineDepth == depth, let start = lineStart {
            let text = lineText.split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")
            if !text.isEmpty { lines.append((start, text)) }
            lineDepth = 0; lineStart = nil; lineText = ""
        }
        if bodyDepth == depth { bodyDepth = 0 }
        depth -= 1
    }
}
