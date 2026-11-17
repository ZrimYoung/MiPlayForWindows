import AppKit
import ApplicationServices

// Observe QQ's own lyric table. Never click, scroll, change playback, read the
// clipboard, query a lyric service or print lyric text. Authorization is manual.
func output(_ value: [String: Any], code: Int32 = 0) -> Never {
    print(String(decoding: try! JSONSerialization.data(withJSONObject: value, options: [.sortedKeys]), as: UTF8.self))
    exit(code)
}
func attribute(_ element: AXUIElement, _ name: String) -> Any? {
    var value: CFTypeRef?
    guard AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success else { return nil }
    return value
}
guard AXIsProcessTrusted() else { output(["status": "accessibility_permission_required"], code: 1) }
guard let app = NSWorkspace.shared.runningApplications.first(where: { $0.bundleIdentifier == "com.tencent.QQMusicMac" }) else {
    output(["status": "qq_not_running"], code: 1)
}
let root = AXUIElementCreateApplication(app.processIdentifier)
AXUIElementSetMessagingTimeout(root, 0.5) // Client timeout only, not a player setting.
let windows = attribute(root, kAXWindowsAttribute) as? [AXUIElement] ?? []
var queue = windows.map { ($0, 0) }, visited = 0
let deadline = Date().addingTimeInterval(12)
while !queue.isEmpty && visited < 2000 && Date() < deadline {
    let (element, depth) = queue.removeFirst(); visited += 1
    if attribute(element, "AXIdentifier") as? String == "KSLyricColumn" {
        guard let rows = attribute(element, "AXRows") as? [AXUIElement], rows.count <= 2000 else {
            output(["status": "invalid_lyric_table"], code: 1)
        }
        var readable = 0, bytes = 0, timed = 0, incomplete = false
        for row in rows {
            var children = [row], checked = 0, text: String?
            while !children.isEmpty && checked < 32 && Date() < deadline {
                let child = children.removeFirst(); checked += 1
                if attribute(child, kAXRoleAttribute) as? String == "AXStaticText",
                   let value = attribute(child, kAXValueAttribute) as? String {
                    text = value; break
                }
                children += (attribute(child, kAXChildrenAttribute) as? [AXUIElement] ?? []).prefix(16)
            }
            guard let text else { incomplete = true; continue }
            readable += 1; bytes += text.utf8.count
            if text.range(of: #"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]"#, options: .regularExpression) != nil { timed += 1 }
        }
        output(["status": "lyric_table_read", "source": "qq-accessibility", "rows": rows.count,
                "readableRows": readable, "timestampedRows": timed, "utf8Bytes": bytes,
                "incomplete": incomplete])
    }
    if depth < 14 {
        queue += (attribute(element, kAXChildrenAttribute) as? [AXUIElement] ?? []).prefix(500).map { ($0, depth + 1) }
    }
}
output(["status": "lyric_table_not_visible", "windowCount": windows.count, "visited": visited], code: 1)
