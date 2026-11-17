import AppKit
import SwiftUI

extension Notification.Name {
    static let miplayShowSettings = Notification.Name("MiPlayShowSettings")
}

/// NSMenu tracking remains stable while media/device snapshots update the view.
private struct SettingsChoice: NSViewRepresentable {
    let labels: [String]
    let values: [String]
    @Binding var selection: String
    var enabled = true
    var unavailable: Set<String> = []
    var accessibilityLabel: String
    func makeCoordinator() -> Coordinator { Coordinator(self) }
    func makeNSView(context: Context) -> NSPopUpButton {
        let button = NSPopUpButton(frame: .zero, pullsDown: false)
        button.addItems(withTitles: labels)
        button.target = context.coordinator; button.action = #selector(Coordinator.changed(_:))
        button.menu?.autoenablesItems = false
        button.setAccessibilityLabel(accessibilityLabel)
        return button
    }
    func updateNSView(_ button: NSPopUpButton, context: Context) {
        context.coordinator.parent = self
        button.isEnabled = enabled
        if let index = values.firstIndex(of: selection), button.indexOfSelectedItem != index { button.selectItem(at: index) }
        for (i, item) in (button.menu?.items ?? []).enumerated() { item.isEnabled = !unavailable.contains(values[i]) }
    }
    final class Coordinator: NSObject {
        var parent: SettingsChoice
        init(_ parent: SettingsChoice) { self.parent = parent }
        @objc func changed(_ sender: NSPopUpButton) {
            guard parent.values.indices.contains(sender.indexOfSelectedItem) else { return }
            parent.selection = parent.values[sender.indexOfSelectedItem]
        }
    }
}

struct TransmissionSettingsView: View {
    @ObservedObject var engine: EngineClient
    @ObservedObject var preferences = ArtworkPreferences.shared
    private func number(_ key: WritableKeyPath<ArtworkOptions, Int>) -> Binding<String> {
        Binding(get: { String(preferences.options[keyPath: key]) }, set: { value in
            guard let number = Int(value) else { return }
            var options = preferences.options; options[keyPath: key] = number; preferences.options = options
        })
    }
    var body: some View {
        Form {
            Section {
                HStack {
                    Text("投放音质")
                    Spacer()
                    SettingsChoice(labels: ["AAC · 256 kbps", "AAC · 320 kbps", "AAC · 512 kbps（实验）"],
                        values: ["256000", "320000", "512000"],
                        selection: Binding(get: { String(engine.snapshot.audioBitRate) }, set: {
                            if let value = Int($0) { engine.send("audioBitRate", value: value) }
                        }), enabled: engine.connected, accessibilityLabel: "投放音质").frame(width: 235)
                }
                Text("更改音质会停止当前投放，需要重新选择音箱。").font(.caption).foregroundStyle(.secondary)
                Toggle("多音箱同步投放", isOn: Binding(get: { engine.snapshot.synchronizedPlayback }, set: {
                    engine.send("synchronizedPlayback", value: $0)
                })).disabled(!engine.connected)
                Text("切换同步模式同样需要重新选择音箱；各设备的延迟仍在主窗口调整。").font(.caption).foregroundStyle(.secondary)
            } header: { Label("音频", systemImage: "waveform") }
            Section {
                HStack {
                    Text("图片格式")
                    Spacer()
                    SettingsChoice(labels: ArtworkFormat.allCases.map(\.label), values: ArtworkFormat.allCases.map(\.rawValue),
                        selection: Binding(get: { preferences.options.format.rawValue }, set: {
                            guard let format = ArtworkFormat(rawValue: $0) else { return }
                            var options = preferences.options; options.format = format; preferences.options = options
                        }), unavailable: WebPArtworkEncoder.available ? [] : ["webp"], accessibilityLabel: "封面图片格式").frame(width: 235)
                }
                if preferences.options.format != .jpeg {
                    Text("接收端可能不支持此格式。如果封面不显示，请切回 JPEG；不会自动判断电视是否成功显示。").font(.caption).foregroundStyle(.secondary)
                }
                HStack {
                    Text("最长边")
                    Spacer()
                    SettingsChoice(labels: ["256 像素", "512 像素", "1024 像素", "2048 像素（实验）"],
                        values: ArtworkOptions.dimensions.map(String.init), selection: number(\.maxDimension),
                        accessibilityLabel: "封面最长边").frame(width: 235)
                }
                HStack {
                    Text("图片大小上限")
                    Spacer()
                    SettingsChoice(labels: ["48 KB · 兼容", "128 KB", "256 KB", "512 KB · 实验", "1 MB · 实验"],
                        values: ArtworkOptions.byteLimits.map(String.init), selection: number(\.maxBytes),
                        accessibilityLabel: "封面大小上限").frame(width: 235)
                }
                if preferences.options.maxBytes > 256 * 1024 {
                    Text("大封面会增加传输量，部分接收端可能不显示或更新较慢；如有异常请调回 256 KB。")
                        .font(.caption).foregroundStyle(.secondary)
                }
                HStack {
                    Text("编码质量")
                    Spacer()
                    SettingsChoice(labels: ["80%", "90%", "100%"], values: ArtworkOptions.qualities.map(String.init),
                        selection: number(\.quality), enabled: preferences.options.format != .png,
                        accessibilityLabel: "封面编码质量").frame(width: 235)
                }
                Toggle("保留符合限制的原图", isOn: Binding(get: { preferences.options.preserveOriginal }, set: {
                    var options = preferences.options; options.preserveOriginal = $0; preferences.options = options
                }))
                Text("格式、尺寸和大小符合时直接发送原图；否则按设置转换。超过大小上限会降低质量或尺寸，小图不会放大。PNG 为无损编码；JPEG / WebP 的 100% 不代表无损。").font(.caption).foregroundStyle(.secondary)
                Text("封面设置自动保存，并在当前投放中更新，无需重连。").font(.caption).foregroundStyle(.secondary)
                HighResolutionArtworkSettings(media: engine.media)
                CurrentArtworkStatus(media: engine.media)
                Button("还原封面默认设置") { preferences.options = ArtworkOptions() }
            } header: { Label("封面", systemImage: "photo") }
            MediaPreferencesSection(media: engine.media)
        }.formStyle(.grouped).padding(8).frame(minWidth: 520, minHeight: 640)
    }
}

private struct CurrentArtworkStatus: View {
    @ObservedObject var media: NowPlayingMonitor
    var body: some View {
        LabeledContent("当前输出", value: media.item?.artworkSummary ?? "等待播放器提供封面")
            .font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
    }
}
private struct MediaPreferencesSection: View {
    @ObservedObject var media: NowPlayingMonitor
    var body: some View {
        Section {
            Toggle("显示系统媒体信息", isOn: $media.enabled)
            Toggle("投送 Apple Music 歌词（实验）", isOn: $media.lyricsEnabled)
            if media.lyricsEnabled { Text(media.lyricsStatus).font(.caption).foregroundStyle(.secondary) }
        } header: { Label("媒体信息", systemImage: "music.note") }
    }
}

private struct HighResolutionArtworkSettings: View {
    @ObservedObject var media: NowPlayingMonitor
    var body: some View {
        Toggle("主动获取 Apple Music 高清封面", isOn: $media.highResolutionArtworkEnabled)
        Text("开启后，按当前歌曲的准确标识从 Apple 图片服务器获取高清封面，再按上述设置发送。可能需要允许访问“音乐”；获取失败时保留系统封面，不按歌名搜索。")
            .font(.caption).foregroundStyle(.secondary)
        if media.highResolutionArtworkEnabled {
            Text(media.highResolutionArtworkStatus).font(.caption).foregroundStyle(.secondary)
        }
    }
}
