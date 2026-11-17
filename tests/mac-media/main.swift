import AppKit
import SQLite3
import ImageIO

func check(_ condition: @autoclosure () -> Bool, _ description: String) {
    guard condition() else { fatalError(description) }
}
let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1024, pixelsHigh: 1024,
    bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
    bytesPerRow: 0, bitsPerPixel: 0)!
let artwork = bitmap.representation(using: .png, properties: [:])!.base64EncodedString()
let track: [String: Any] = ["title": "歌曲 A", "artist": "Artist", "album": "Album A", "playing": true,
    "bundleIdentifier": "test.player", "duration": 120.0, "elapsedTime": 15.0, "artworkData": artwork]
let first = MediaSnapshot.decode(track)!
check(first.artwork != nil && first.artwork!.size.width == 1024, "Keep 1024-pixel source cover instead of downsampling to 512")
check(first.playing && first.duration == 120 && first.elapsed == 15, "Playback fields")
var late = track; late.removeValue(forKey: "artworkData")
check(MediaSnapshot.decode(late)?.artwork == nil, "Initial text-only snapshot")
check(MediaSnapshot.decode(track)?.artwork != nil, "Late artwork updates are accepted")
late["title"] = "歌曲 B"; late["album"] = "Album B"
check(MediaSnapshot.decode(late)?.artwork == nil, "New track cannot retain old cover")
late["bundleIdentifier"] = "other.player"
check(MediaSnapshot.decode(late)?.artwork == nil, "Player change cannot retain old cover")
late["artworkData"] = "not an image"
late["duration"] = Double.nan; late["elapsedTime"] = -1
check(MediaSnapshot.decode(late)?.artwork == nil && MediaSnapshot.decode(late)?.duration == nil &&
      MediaSnapshot.decode(late)?.elapsed == nil, "Malformed image/times are ignored")
late["artworkData"] = String(repeating: "A", count: 12 * 1024 * 1024)
check(MediaSnapshot.decode(late)?.artwork == nil, "Oversized artwork is rejected")
check(MediaSnapshot.decode([:]) == nil && MediaSnapshot.decode(["title": ""]) == nil, "Empty state clears metadata")
let serialized = try JSONSerialization.data(withJSONObject: ["type": "data", "diff": false, "payload": track])
var parser = MediaLineBuffer(), result: [Data] = []
var input = serialized; input.append(10); input.append(contentsOf: [123, 125, 10])
for byte in input { result += try parser.append(Data([byte])) }
check(result.count == 2 && result[0] == serialized && result[1] == Data("{}".utf8), "Split UTF-8, image and multiple lines")
var bounded = MediaLineBuffer()
do { _ = try bounded.append(Data(repeating: 65, count: MediaSnapshot.maxFrameBytes + 1)); fatalError("Expected size limit") }
catch MediaLineBuffer.FrameError.tooLarge { }
let pipe = Pipe(), signal = DispatchSemaphore(value: 0)
try pipe.fileHandleForWriting.write(contentsOf: Data("small frame\n".utf8))
DispatchQueue.global().async {
    let data = try! MediaPipeReader.read(pipe.fileHandleForReading)
    check(data == Data("small frame\n".utf8), "Pipe frame contents")
    signal.signal()
}
let promptly = signal.wait(timeout: .now() + 0.5) == .success
try pipe.fileHandleForWriting.close()
check(promptly, "A short frame must arrive before the persistent pipe closes")
var timed = track; timed["timestamp"] = "2026-11-15T10:00:00Z"; timed["playbackRate"] = 2.0
let now = ISO8601DateFormatter().date(from: "2026-11-15T10:00:10Z")!
check(MediaSnapshot.decode(timed)!.position(at: now) == 35, "Progress follows system timestamp and playback rate")
timed["playing"] = false
check(MediaSnapshot.decode(timed)!.position(at: now) == 15, "Paused progress remains still")
timed["playing"] = true
check(MediaSnapshot.decode(timed)!.position(at: now.addingTimeInterval(200)) == 120, "Progress never exceeds duration")
timed["elapsedTime"] = 0.0
check(MediaSnapshot.decode(timed)!.elapsed == 0, "Seek/reset position is accepted")
timed["timestamp"] = "2026-11-15T10:00:00.500Z"
check(MediaSnapshot.decode(timed)!.position(at: now) == 19, "Fractional system timestamps preserve progress")
check(first.bundleIdentifier == "test.player", "Player identity is retained for transport targeting")
check(MediaSnapshot.fullSnapshot(from: Data(#"{"type":"log","message":"ready"}"#.utf8)) == nil, "Non-data messages do not clear metadata")
check(MediaSnapshot.fullSnapshot(from: Data(#"{"type":"data","diff":true,"payload":{}}"#.utf8)) == nil, "Partial messages do not replace a full snapshot")
check(MediaSnapshot.fullSnapshot(from: Data(#"{"type":"data","diff":false,"payload":null}"#.utf8))?.isEmpty == true, "Explicit null player clears metadata")
let monitor = NowPlayingMonitor()
monitor.setPreview()
monitor.acceptSnapshot(first)
monitor.playerTerminated("unrelated.app")
check(monitor.item?.identity == first.identity, "Unrelated application exit does not clear media")
monitor.playerTerminated("test.player")
check(monitor.item == nil, "Player exit immediately clears metadata and artwork")
monitor.acceptSnapshot(first)
check(monitor.item == nil, "Late metadata cannot resurrect an exited player")
var paused = first; paused.playing = false
let toggle = MediaActionExpectation(before: first, command: .toggle, seekPosition: nil)
check(!toggle.fulfilled(by: first) && toggle.fulfilled(by: paused), "Transport success requires observed playing state")
paused.bundleIdentifier = "other.player"
check(!toggle.fulfilled(by: paused), "Another player cannot confirm a transport action")
var sought = first; sought.elapsed = 50; sought.timestamp = now
let seek = MediaActionExpectation(before: first, command: nil, seekPosition: 50)
check(seek.fulfilled(by: sought, at: now) && !seek.fulfilled(by: first, at: now), "Seek confirmation uses actual position")
sought.title = "Next song"
check(!seek.fulfilled(by: sought, at: now), "Seek confirmation cannot belong to another track")
let next = MediaActionExpectation(before: first, command: .next, seekPosition: nil)
check(next.fulfilled(by: sought) && !next.fulfilled(by: first), "Next-track confirmation requires updated track identity")
var refreshedID = first; refreshedID.trackIdentifier = "new-on-every-QQ-Music-update"
check(refreshedID.identity == first.identity, "Volatile system identifiers do not change track identity")
check(!next.fulfilled(by: refreshedID), "A regenerated identifier cannot falsely confirm next track")
var timeline = MediaProgressTracker()
var running = first; running.elapsed = 10; running.timestamp = now; running.playbackRate = 1
_ = timeline.accept(running, at: now)
var stopped = running; stopped.playing = false
let frozen = timeline.accept(stopped, at: now.addingTimeInterval(20))!
check(frozen.position(at: now.addingTimeInterval(80)) == 30, "A stale pause anchor freezes the current position without jumping backward")
let artworkUpdate = timeline.accept(stopped, at: now.addingTimeInterval(40))!
check(artworkUpdate.position(at: now.addingTimeInterval(80)) == 30, "Duplicate paused metadata cannot reset the frozen position")
let continued = timeline.accept(running, at: now.addingTimeInterval(80))!
check(continued.position(at: now.addingTimeInterval(83)) == 33, "Resume with a stale anchor excludes time spent paused")
var accurate = running; accurate.elapsed = 70; accurate.timestamp = now.addingTimeInterval(84)
check(timeline.accept(accurate, at: now.addingTimeInterval(84))!.position(at: now.addingTimeInterval(84)) == 70, "Fresh player anchors take precedence after seek")
accurate.title = "Another song"; accurate.elapsed = 0
check(timeline.accept(accurate, at: now.addingTimeInterval(84))!.elapsed == 0, "New song never inherits the old frozen position")
let jpeg = MediaSnapshot.jpegArtwork(first.artwork)
check(!jpeg.isEmpty && Data(base64Encoded: jpeg)!.count <= ArtworkOptions().maxBytes, "Forwarded artwork fits the command-channel budget")
let jpegBytes = Data(base64Encoded: jpeg)!
check(jpegBytes.starts(with: [0xff, 0xd8, 0xff]) && NSImage(data: jpegBytes) != nil, "Forwarded artwork is a real decodable JPEG")
check(MediaSnapshot.jpegArtwork(nil).isEmpty, "Missing artwork explicitly clears the old cover")
check(NSBitmapImageRep(data: jpegBytes)?.pixelsWide == 1024, "Forwarded simple cover keeps 1024 pixels")
check(first.artworkBase64 == jpeg, "Reader prepares the actual forwarded JPEG before UI delivery")
let sameCover = MediaSnapshot.decode(track)!
check(sameCover.artwork === first.artwork && sameCover.artworkBase64 == first.artworkBase64,
      "Progress refresh reuses the processed cover and encoded bytes")
func syntheticCover(_ width: Int, _ height: Int, noise: Bool = false) -> NSImage {
    let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height,
        bitsPerSample: 8, samplesPerPixel: 3, hasAlpha: false, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    var seed: UInt32 = 12345
    for i in 0..<(bitmap.bytesPerRow * height) {
        seed = seed &* 1664525 &+ 1013904223
        bitmap.bitmapData![i] = noise ? UInt8(truncatingIfNeeded: seed >> 24) : [200, 90, 40][i % 3]
    }
    return NSImage(cgImage: bitmap.cgImage!, size: .zero)
}
let rectangular = Data(base64Encoded: MediaSnapshot.jpegArtwork(syntheticCover(600, 539)))!
let rectanglePixels = NSBitmapImageRep(data: rectangular)!
var smallSource = track; smallSource["artworkData"] = rectangular.base64EncodedString()
let decodedSmall = MediaSnapshot.decode(smallSource)!
let decodedSmallPixels = NSBitmapImageRep(data: Data(base64Encoded: decodedSmall.artworkBase64)!)!
check(decodedSmallPixels.pixelsWide == 600 && decodedSmallPixels.pixelsHigh == 539,
      "Full system snapshot pipeline does not upscale smaller source images")
check(rectanglePixels.pixelsWide == 600 && rectanglePixels.pixelsHigh == 539,
      "600x539 source keeps its original resolution and aspect ratio without upscaling")
let oversizedPixels = NSBitmapImageRep(data: Data(base64Encoded: MediaSnapshot.jpegArtwork(syntheticCover(2048, 1024)))!)!
check(oversizedPixels.pixelsWide == 1024 && oversizedPixels.pixelsHigh == 512, "Large covers downsample proportionally to 1024")
let dense = Data(base64Encoded: MediaSnapshot.jpegArtwork(syntheticCover(1024, 1024, noise: true)))!
check(!dense.isEmpty && dense.count <= ArtworkOptions().maxBytes && NSBitmapImageRep(data: dense) != nil,
      "High-entropy artwork progressively fits the byte cap instead of disappearing")
check(MediaSnapshot.decode(late)?.artworkBase64.isEmpty == true, "Invalid new cover cannot reuse previous JPEG")
check(decodedSmall.artworkBase64 == rectangular.base64EncodedString(), "Matching source JPEG is sent byte-for-byte without generation loss")
var pngOptions = ArtworkOptions(); pngOptions.format = .png
let pngResult = MediaSnapshot.decode(smallSource, artworkOptions: pngOptions)!
let pngData = Data(base64Encoded: pngResult.artworkBase64)!
check(pngData.starts(with: [0x89, 0x50, 0x4e, 0x47]) && NSBitmapImageRep(data: pngData) != nil,
      "PNG setting replaces cached JPEG with an actual decodable PNG")
check(WebPArtworkEncoder.available, "Self-contained WebP encoder is bundled with the test runtime")
var webpOptions = ArtworkOptions(); webpOptions.format = .webp
let webpResult = MediaSnapshot.decode(smallSource, artworkOptions: webpOptions)!
let webpData = Data(base64Encoded: webpResult.artworkBase64)!
check(webpData.prefix(4) == Data("RIFF".utf8) && webpData[8..<12] == Data("WEBP".utf8) && NSImage(data: webpData) != nil,
      "WebP setting produces actual decodable WebP bytes")
let webpSource = CGImageSourceCreateWithData(webpData as CFData, nil)!
let webpPixels = CGImageSourceCreateImageAtIndex(webpSource, 0, nil)!
check(webpPixels.width == 600 && webpPixels.height == 539, "WebP keeps source aspect ratio")
let roundtrip = NSBitmapImageRep(cgImage: webpPixels).colorAt(x: 100, y: 100)!.usingColorSpace(.deviceRGB)!
let originalColor = rectanglePixels.colorAt(x: 100, y: 100)!.usingColorSpace(.deviceRGB)!
check(abs(roundtrip.redComponent - originalColor.redComponent) < 0.03 &&
      abs(roundtrip.greenComponent - originalColor.greenComponent) < 0.03 &&
      abs(roundtrip.blueComponent - originalColor.blueComponent) < 0.03,
      "WebP RGBA pixel layout preserves the original JPEG colors")
var webpIncoming = smallSource; webpIncoming["artworkData"] = webpData.base64EncodedString()
check(MediaSnapshot.decode(webpIncoming, artworkOptions: webpOptions)?.artworkBase64 == webpData.base64EncodedString(),
      "Already suitable WebP is preserved as original bytes")
var reducedOptions = ArtworkOptions(); reducedOptions.maxDimension = 256; reducedOptions.maxBytes = 48 * 1024
let reduced = MediaSnapshot.decode(smallSource, artworkOptions: reducedOptions)!
let reducedPixels = NSBitmapImageRep(data: Data(base64Encoded: reduced.artworkBase64)!)!
check(reducedPixels.pixelsWide == 256, "Changing dimension invalidates prior full-resolution artwork cache")
check(ArtworkOptions(dictionary: webpOptions.dictionary) == webpOptions, "Artwork options survive persistence roundtrip")
let invalidOptions = ArtworkOptions(dictionary: ["format":"unknown", "maxDimension":Int.max, "maxBytes":-1, "quality":999])
check(invalidOptions == ArtworkOptions(), "Corrupt preferences safely return to bounded defaults")
print("Mac media checks passed (metadata, exit, stale frames, transport, seek and bounded HD artwork)")

var musicTrack = first
musicTrack.bundleIdentifier = "com.apple.Music"
let lyricReply: [String: Any] = ["status": "ok", "stable": true, "title": musicTrack.title,
    "artist": musicTrack.artist, "album": musicTrack.album, "duration": 120.0,
    "lyrics": "[00:01.00]Test line\n[00:02.00]Another line"]
check(!AppleMusicLyrics.matchedLyrics(lyricReply, expected: musicTrack).isEmpty, "Matching Music lyrics accepted")
var wrongLyrics = lyricReply; wrongLyrics["title"] = "Another recording"
check(AppleMusicLyrics.matchedLyrics(wrongLyrics, expected: musicTrack).isEmpty, "Late lyric response cannot cross tracks")
wrongLyrics = lyricReply; wrongLyrics["stable"] = false
check(AppleMusicLyrics.matchedLyrics(wrongLyrics, expected: musicTrack).isEmpty, "Song changing during query rejects lyrics")
wrongLyrics = lyricReply; wrongLyrics["duration"] = 150.0
check(AppleMusicLyrics.matchedLyrics(wrongLyrics, expected: musicTrack).isEmpty, "Different duration rejects recording")
wrongLyrics = lyricReply; wrongLyrics["lyrics"] = String(repeating: "字", count: 6000)
check(AppleMusicLyrics.matchedLyrics(wrongLyrics, expected: musicTrack).isEmpty, "Lyric limit measures UTF-8 bytes")
wrongLyrics = lyricReply; wrongLyrics["status"] = "denied"
check(AppleMusicLyrics.matchedLyrics(wrongLyrics, expected: musicTrack).isEmpty, "Permission denial supplies no lyrics")
check(AppleMusicLyrics.matchedLyrics(lyricReply, expected: first).isEmpty, "Other players cannot consume Music lyrics")
print("Apple Music lyric identity/size/permission checks passed")
let lyricMonitor = NowPlayingMonitor()
lyricMonitor.setPreview()
var withLyrics = musicTrack; withLyrics.lyrics = "Synthetic line"
lyricMonitor.acceptSnapshot(withLyrics)
check(lyricMonitor.item?.title == musicTrack.title, "Lyrics never substituted for native song title")
lyricMonitor.acceptSnapshot(musicTrack)
check(lyricMonitor.item?.lyrics == "Synthetic line", "Progress-only refresh preserves lyrics without a transient clear")
var otherSong = musicTrack; otherSong.title = "Different song"
lyricMonitor.acceptSnapshot(otherSong)
check(lyricMonitor.item?.title == otherSong.title, "Track change preserves actual new title")
check(lyricMonitor.item?.lyrics == "", "Song change clears previous lyrics immediately")
lyricMonitor.playerTerminated("com.apple.Music")
check(lyricMonitor.item == nil, "Music exit clears lyric-bearing item")
print("Lyric refresh/track-change/exit checks passed")

var changedDuration = musicTrack; changedDuration.duration = 125
let durationMonitor = NowPlayingMonitor(); durationMonitor.setPreview()
durationMonitor.acceptSnapshot(withLyrics); durationMonitor.acceptSnapshot(changedDuration)
check(durationMonitor.item?.lyrics == "", "Different duration clears same-named recording lyrics")

let ttml = #"""
<tt xmlns="http://www.w3.org/ns/ttml"><head><metadata><translation><text>Excluded translation</text></translation></metadata></head>
<body><div><p begin="1.253" end="3.5"><span begin="1.253">Synthetic</span> <span>line &amp; test</span></p>
<p begin="00:04.500" end="6">第二行</p></div></body></tt>
"""#
let expectedLRC = "[00:01.25]Synthetic line & test\n[00:04.50]第二行"
check(AppleMusicTTML.lrc(ttml, duration: 120) == expectedLRC, "Original TTML timing and word order preserved; metadata translation excluded")
check(AppleMusicTTML.seconds("1000ms") == 1 && AppleMusicTTML.seconds("2.5s") == 2.5 &&
      AppleMusicTTML.seconds("01:02:03.125") == 3723.125, "TTML clock and offset time formats")
for value in ["-1", "NaN", "infinity", "00:99.1", "00:00:00:12", "12f"] {
    check(AppleMusicTTML.seconds(value) == nil, "Invalid/unsupported TTML time rejected")
}
check(AppleMusicTTML.lrc(ttml.replacingOccurrences(of: "begin=\"1.253\"", with: "begin=\"130\""), duration: 120) == nil,
      "Out-of-track timestamp rejected")
check(AppleMusicTTML.lrc(ttml.replacingOccurrences(of: "end=\"3.5\"", with: "end=\"0.5\""), duration: 120) == nil,
      "Reversed time range rejected")
check(AppleMusicTTML.lrc(ttml.replacingOccurrences(of: "http://www.w3.org/ns/ttml", with: "urn:unexpected"), duration: 120) == nil,
      "Non-TTML document rejected")
check(AppleMusicTTML.lrc("<!DOCTYPE tt [<!ENTITY test SYSTEM 'file:///etc/passwd'>]>" + ttml, duration: 120) == nil,
      "External/internal entity declarations rejected")
check(AppleMusicTTML.lrc(ttml.replacingOccurrences(of: "第二行", with: String(repeating: "字", count: 6000)), duration: 120) == nil,
      "Converted lyrics obey UTF-8 transport budget")
check(AppleMusicTTML.lrc("<tt", duration: 120) == nil, "Truncated cache XML rejected")
check(AppleMusicTTML.lrc(ttml.replacingOccurrences(of: "begin=\"00:04.500\"", with: ""), duration: 120) == nil,
      "Missing line timestamps are not fabricated")
let cachedSong: [String: Any] = ["id": "12345", "type": "songs", "attributes": ["durationInMillis": 120000],
    "relationships": ["syllable-lyrics": ["data": [["type": "syllable-lyrics", "attributes": ["ttmlLocalizations": ttml]]]]]]
let cachedBody = try JSONSerialization.data(withJSONObject: ["data": [cachedSong]])
check(AppleMusicCachedLyrics.decode(cachedBody, catalogID: "12345", duration: 120) == expectedLRC, "Exact catalogue ID selects local Music response")
check(AppleMusicCachedLyrics.decode(cachedBody, catalogID: "54321", duration: 120) == nil, "Different catalogue ID never falls back to title matching")
check(AppleMusicCachedLyrics.decode(cachedBody, catalogID: "12345", duration: 150) == nil, "Cache duration must match actual recording")
let testCache = FileManager.default.temporaryDirectory.appendingPathComponent("miplay-cache-test-" + UUID().uuidString)
try FileManager.default.createDirectory(at: testCache.appendingPathComponent("fsCachedData"), withIntermediateDirectories: true)
defer { try? FileManager.default.removeItem(at: testCache) }
var fixtureDB: OpaquePointer?
check(sqlite3_open(testCache.appendingPathComponent("Cache.db").path, &fixtureDB) == SQLITE_OK, "Synthetic cache opens")
defer { sqlite3_close(fixtureDB) }
let schema = """
CREATE TABLE cfurl_cache_response(entry_ID INTEGER PRIMARY KEY, request_key TEXT);
CREATE TABLE cfurl_cache_receiver_data(entry_ID INTEGER PRIMARY KEY, isDataOnFS INTEGER, receiver_data BLOB);
INSERT INTO cfurl_cache_response VALUES(1,'https://amp-api.music.apple.com/v1/catalog/cn/songs?include=syllable-lyrics');
INSERT INTO cfurl_cache_receiver_data VALUES(1,1,'test-response');
"""
check(sqlite3_exec(fixtureDB, schema, nil, nil, nil) == SQLITE_OK, "Synthetic cache fixture prepared")
let loadingTime = Date(timeIntervalSince1970: 1000)
var delayedLyrics = AppleMusicLyricsRefreshState()
let absentBody = AppleMusicCachedLyrics.read(catalogID: "12345", duration: 120, cache: testCache)
check(absentBody == nil, "Indexed lyric body may not have finished downloading")
delayedLyrics.complete(text: absentBody ?? "", status: "loading", at: loadingTime)
check(delayedLyrics.nextAttempt <= loadingTime.addingTimeInterval(3), "Missing lyrics retry promptly without a seek or track event")
try cachedBody.write(to: testCache.appendingPathComponent("fsCachedData/test-response"))
delayedLyrics.complete(text: AppleMusicCachedLyrics.read(catalogID: "12345", duration: 120, cache: testCache) ?? "",
                       status: "loaded", at: delayedLyrics.nextAttempt)
check(delayedLyrics.text == expectedLRC, "Late cache arrival is delivered for the unchanged recording")
check(delayedLyrics.nextAttempt == loadingTime.addingTimeInterval(63), "Loaded lyrics avoid repeated high-frequency Apple events")
delayedLyrics.complete(text: "", status: "unavailable", failed: true, at: delayedLyrics.nextAttempt)
check(delayedLyrics.text == expectedLRC && delayedLyrics.status == "loaded", "Transient read error does not remove loaded TV lyrics")
delayedLyrics.complete(text: "", status: "loading", at: delayedLyrics.nextAttempt)
check(delayedLyrics.text == expectedLRC, "Transient empty response also preserves the verified recording")
delayedLyrics.complete(text: "", status: "denied", denied: true, at: loadingTime)
check(delayedLyrics.text.isEmpty && delayedLyrics.nextAttempt == .distantFuture, "Revoked permission clears lyrics and stops retrying")
delayedLyrics = AppleMusicLyricsRefreshState()
check(delayedLyrics.text.isEmpty && delayedLyrics.nextAttempt < loadingTime, "Track reset clears old lyrics and allows immediate query")
delayedLyrics.complete(text: "", status: "timeout", failed: true, at: loadingTime)
check(delayedLyrics.nextAttempt == loadingTime.addingTimeInterval(15), "Errors back off instead of repeatedly launching failed queries")
check(AppleMusicCachedLyrics.read(catalogID: "12345", duration: 120, cache: testCache) == expectedLRC, "Reads body file through readonly cache database")
check(AppleMusicCachedLyrics.read(catalogID: "54321", duration: 120, cache: testCache) == nil, "Readonly cache refuses unrelated record")
check(AppleMusicCachedLyrics.read(catalogID: "12345 OR 1=1", duration: 120, cache: testCache) == nil, "Catalogue ID cannot contain SQL or paths")
check(sqlite3_exec(fixtureDB, "UPDATE cfurl_cache_receiver_data SET receiver_data='../outside';", nil, nil, nil) == SQLITE_OK, "Traversal fixture prepared")
check(AppleMusicCachedLyrics.read(catalogID: "12345", duration: 120, cache: testCache) == nil, "Cache body path cannot escape folder")
print("Apple Music local cache and TTML conversion checks passed (synthetic data; no network)")
var identified = lyricReply
identified["catalogID"] = "12345"; identified["catalogSourceVerified"] = true
check(AppleMusicLyrics.cacheCatalogID(identified, expected: musicTrack) == "12345", "Verified active Music store identifier accepted")
identified["catalogSourceVerified"] = false
check(AppleMusicLyrics.cacheCatalogID(identified, expected: musicTrack) == nil, "Unverified store identifier never triggers cache lookup")
identified["catalogSourceVerified"] = true; identified["catalogID"] = "queue-uuid"
check(AppleMusicLyrics.cacheCatalogID(identified, expected: musicTrack) == nil, "Queue identifier cannot stand in for Music catalogue ID")
identified["catalogID"] = "12345"; identified["stable"] = false
check(AppleMusicLyrics.cacheCatalogID(identified, expected: musicTrack) == nil, "Track change during direct query rejects cache lookup")
identified["stable"] = true
check(AppleMusicLyrics.cacheCatalogID(identified, expected: first) == nil, "QQ playback cannot consume Apple's cached lyrics")

// Direct artwork uses the exact recording and official image host, never a search.
let coverBody: [String: Any] = ["data": [["id": "12345", "type": "songs", "attributes": [
    "name": musicTrack.title, "artistName": musicTrack.artist, "albumName": musicTrack.album,
    "durationInMillis": 120000, "artwork": ["width": 4000, "height": 4000,
    "url": "https://is1-ssl.mzstatic.com/image/thumb/test/{w}x{h}{c}.{f}"]]]]]
let coverJSON = try JSONSerialization.data(withJSONObject: coverBody)
let directURL = AppleMusicArtworkCache.imageURL(coverJSON, catalogID: "12345", expected: musicTrack)
check(directURL?.lastPathComponent == "2048x2048bb.jpg", "Original source URL requests bounded HD dimensions")
check(AppleMusicArtworkCache.imageURL(coverJSON, catalogID: "54321", expected: musicTrack) == nil, "Wrong catalogue ID rejected")
check(AppleMusicArtworkCache.imageURL(coverJSON, catalogID: "12345", expected: otherSong) == nil, "Same ID with inconsistent track rejected")
check(AppleMusicArtworkCache.imageURL(coverJSON, catalogID: "12345", expected: first) == nil, "Other player cannot consume Music cover")
for address in ["http://is1-ssl.mzstatic.com/image/a", "https://mzstatic.com.evil.test/image/a",
                "https://is1-ssl.mzstatic.com:8080/image/a", "https://user@is1-ssl.mzstatic.com/image/a",
                "file:///image/a", "https://is1-ssl.mzstatic.com/account/a"] {
    check(!AppleMusicArtworkCache.allowed(URL(string: address)!), "Untrusted image destination rejected")
}
check(!AppleMusicLyrics.artworkScript.contains("track.lyrics()"), "Cover permission independent from lyrics query")
check(AppleMusicLyrics.script.contains("track.lyrics()"), "Lyrics query still reads original field")
try coverJSON.write(to: testCache.appendingPathComponent("fsCachedData/test-response"))
check(sqlite3_exec(fixtureDB, "UPDATE cfurl_cache_receiver_data SET receiver_data='test-response';", nil, nil, nil) == SQLITE_OK, "Artwork cache fixture prepared")
check(AppleMusicArtworkCache.read(catalogID: "12345", expected: musicTrack, cache: testCache) == directURL, "Readonly Music cache resolves exact cover")
check(AppleArtworkDownload.dimensions(Data("invalid".utf8)) == nil, "Invalid network image rejected")
check(AppleArtworkDownload.dimensions(Data(base64Encoded: artwork)!)?.0 == 1024, "Image dimensions inspected")
var preparedCover = musicTrack; preparedCover.artworkBase64 = "synthetic-hd-cover"
var coverState = DirectArtworkState(expected: musicTrack, raw: "synthetic-source", prepared: preparedCover, options: ArtworkOptions())
var newerMedia = musicTrack; newerMedia.elapsed = 65; newerMedia.playing = false; newerMedia.lyrics = "[00:01.00]Synthetic lyrics"
let mergedCover = coverState.applying(to: newerMedia, options: ArtworkOptions())
check(mergedCover.artworkBase64 == "synthetic-hd-cover", "HD survives normal media refresh")
check(mergedCover.title == newerMedia.title && mergedCover.lyrics == newerMedia.lyrics && mergedCover.elapsed == 65 && !mergedCover.playing, "Async cover preserves song title, newer lyrics and playback state")
check(coverState.applying(to: otherSong, options: ArtworkOptions()).artworkBase64 == otherSong.artworkBase64, "Late cover cannot cross song boundary")
var newOptions = ArtworkOptions(); newOptions.maxDimension = 256
check(coverState.applying(to: newerMedia, options: newOptions).artworkBase64 == newerMedia.artworkBase64, "Stale encoding cannot override changed settings")
print("Direct artwork identity, bounded URLs, image validation and media isolation checks passed")

for cap in [512 * 1024, 1024 * 1024] {
    var larger = ArtworkOptions(); larger.maxBytes = cap
    check(ArtworkOptions(dictionary: larger.dictionary) == larger, "Larger selected cap persists")
}
check(ArtworkOptions().maxBytes == 256 * 1024, "Default cover budget remains unchanged")
check(ArtworkOptions(dictionary: ["maxBytes": 2 * 1024 * 1024]).maxBytes == 256 * 1024, "Unsupported oversized preference is rejected")
var largeCoverOptions = ArtworkOptions(); largeCoverOptions.maxBytes = 1024 * 1024
let largeCoverData = MediaSnapshot.encodeArtwork(syntheticCover(1024, 1024, noise: true), options: largeCoverOptions).data!
check(largeCoverData.count > 256 * 1024 && largeCoverData.count <= 1024 * 1024,
      "Experimental cap actually permits a larger encoded image")
check(NSBitmapImageRep(data: largeCoverData)?.pixelsWide == 1024, "Larger cap preserves detailed source resolution")
print("Experimental artwork size limits and default compatibility checks passed")
