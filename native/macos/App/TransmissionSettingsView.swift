import AppKit
import SwiftUI

extension Notification.Name {
    static let miplayShowSettings = Notification.Name("MiPlayShowSettings")
}

/// NSMenu tracking remains stable while media/device snapshots update the view.
struct SettingsChoice: NSViewRepresentable {
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
        button.menu?.delegate = context.coordinator
        for (index, item) in (button.menu?.items ?? []).enumerated() { item.representedObject = values[index] }
        button.setAccessibilityLabel(accessibilityLabel)
        return button
    }
    func updateNSView(_ button: NSPopUpButton, context: Context) {
        context.coordinator.parent = self
        button.isEnabled = enabled
        guard !context.coordinator.tracking else { return }
        if button.itemTitles != labels {
            button.removeAllItems(); button.addItems(withTitles: labels)
            for (index, item) in (button.menu?.items ?? []).enumerated() { item.representedObject = values[index] }
        } else {
            for (index, item) in (button.menu?.items ?? []).enumerated() { item.representedObject = values[index] }
        }
        if let index = values.firstIndex(of: selection), button.indexOfSelectedItem != index { button.selectItem(at: index) }
        for (i, item) in (button.menu?.items ?? []).enumerated() { item.isEnabled = !unavailable.contains(values[i]) }
    }
    final class Coordinator: NSObject, NSMenuDelegate {
        var tracking = false
        func menuWillOpen(_ menu: NSMenu) { tracking = true }
        func menuDidClose(_ menu: NSMenu) { tracking = false }
        var parent: SettingsChoice
        init(_ parent: SettingsChoice) { self.parent = parent }
        @objc func changed(_ sender: NSPopUpButton) {
            guard let value = sender.selectedItem?.representedObject as? String else { return }
            parent.selection = value
        }
    }
}

private enum SettingsPane: String, CaseIterable, Identifiable {
    case appearance, source, casting, routing, artwork, media, diagnostics
    var id: String { rawValue }
    var title: String {
        switch self {
        case .appearance: return "外观"
        case .source: return "音源"
        case .casting: return "音频与连接"
        case .routing: return "声道分配"
        case .artwork: return "封面"
        case .media: return "媒体与歌词"
        case .diagnostics: return "诊断"
        }
    }
    var icon: String {
        switch self {
        case .appearance: return "paintpalette"
        case .source: return "waveform.circle"
        case .casting: return "antenna.radiowaves.left.and.right"
        case .routing: return "hifispeaker.2"
        case .artwork: return "photo"
        case .media: return "music.note"
        case .diagnostics: return "waveform.path.ecg"
        }
    }
}

struct TransmissionSettingsView: View {
    @ObservedObject var engine: EngineClient
    @ObservedObject var preferences = ArtworkPreferences.shared
    @State private var selected: SettingsPane? = .appearance
    private var pane: SettingsPane { selected ?? .appearance }
    private func number(_ key: WritableKeyPath<ArtworkOptions, Int>) -> Binding<String> {
        Binding(get: { String(preferences.options[keyPath: key]) }, set: { value in
            guard let number = Int(value) else { return }
            var options = preferences.options; options[keyPath: key] = number; preferences.options = options
        })
    }
    var body: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 12) {
                Label("MiPlay", systemImage: "airplay.audio").font(.system(size: 19, weight: .semibold))
                    .padding(.horizontal, 18).padding(.top, 24)
                List {
                    ForEach(SettingsPane.allCases) { page in
                        Button { selected = page } label: {
                            HStack(spacing: 9) {
                                Image(systemName: page.icon).symbolRenderingMode(.monochrome)
                                    .foregroundStyle(pane == page ? Color.white : Color.secondary).frame(width: 19)
                                Text(page.title).foregroundStyle(pane == page ? Color.white : Color.primary).lineLimit(1)
                            }.font(.system(size: 13, weight: pane == page ? .medium : .regular))
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(.horizontal, 10).padding(.vertical, 9)
                                .foregroundStyle(pane == page ? Color.white : Color.primary)
                                .background(pane == page ? Color.accentColor : Color.clear,
                                            in: RoundedRectangle(cornerRadius: 7))
                                .contentShape(Rectangle())
                        }.buttonStyle(.plain)
                            .accessibilityAddTraits(pane == page ? [.isSelected] : [])
                            .listRowInsets(EdgeInsets(top: 2, leading: 5, bottom: 2, trailing: 5))
                    }
                }.listStyle(.sidebar).scrollContentBackground(.hidden)
            }.frame(width: 185).background(.quaternary.opacity(0.25))
            Divider()
            VStack(alignment: .leading, spacing: 0) {
                Text(pane.title).font(.system(size: 23, weight: .semibold))
                    .padding(.horizontal, 26).padding(.top, 24).padding(.bottom, 6)
                Form {
                    switch pane {
                    case .appearance: AppearanceSettingsView()
                    case .source: AudioSourceSettings(engine: engine)
                    case .casting: audioSection
                    case .routing:
                        if engine.snapshot.speakers.contains(where: { $0.groupId?.isEmpty != false }) {
                            ChannelRoutingSettings(engine: engine)
                        } else {
                            Section {
                                Text("发现独立音箱后，可在这里指定左右声道。")
                                Text("官方立体声组合自动管理左右声道，继续在主窗口合并控制。")
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                        }
                    case .artwork: artworkSection
                    case .media: MediaPreferencesSection(media: engine.media)
                    case .diagnostics: SettingsDiagnostics(engine: engine)
                    }
                }.formStyle(.grouped)
            }.frame(maxWidth: .infinity, maxHeight: .infinity)
        }.background(Color(nsColor: .windowBackgroundColor)).frame(minWidth: 760, minHeight: 560)
            .onAppear {
                if let index = CommandLine.arguments.firstIndex(of: "--settings-section"), CommandLine.arguments.count > index + 1,
                   let page = SettingsPane(rawValue: CommandLine.arguments[index + 1]) { selected = page }
            }
    }
    private var audioSection: some View {
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
                Text("同步投放仅适用于 MiPlay。切换后需重新选择音箱；各设备的延迟仍在主窗口调整。").font(.caption).foregroundStyle(.secondary)
                Toggle("发现 DLNA 设备（实验）", isOn: Binding(get: { engine.snapshot.dlnaEnabled ?? false }, set: {
                    engine.send("dlnaEnabled", value: $0)
                })).disabled(!engine.connected)
                Text("DLNA 可连接更多接收设备，采用单独的传输路径，不支持 MiPlay 同步和实时延迟调节。")
                    .font(.caption).foregroundStyle(.secondary)
            } header: { Label("音频", systemImage: "waveform") }
    }
    private var artworkSection: some View {
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
    }
}

private struct SettingsDiagnostics: View {
    @ObservedObject var engine: EngineClient
    var body: some View {
        Section {
            LabeledContent("投放服务", value: engine.connected ? "已连接" : "未连接")
            Button("导出诊断记录…") { engine.send("export") }.disabled(!engine.connected)
            Text("记录连接与传输状态，不保存原始音频。队列缓冲并非实际听到声音的总延迟。")
                .font(.caption).foregroundStyle(.secondary)
        } header: { Label("诊断记录", systemImage: "doc.text.magnifyingglass") }
        if engine.snapshot.speakers.contains(where: { $0.diagnostics != nil }) {
            Section {
                ForEach(engine.snapshot.speakers) { card in
                    if let d = card.diagnostics {
                        DisclosureGroup(card.name) {
                            LabeledContent("传输", value: card.transport ?? "MiPlay")
                            LabeledContent("缓冲", value: "\(d.bufferedMilliseconds) ms")
                            LabeledContent("队列溢出 / 缺帧", value: "\(d.overruns) / \(d.underruns)")
                            if !card.isDlna {
                                LabeledContent("编码输入 / 输出积压", value: "\(d.encoderPendingInputFrames) / \(d.encoderBufferedAccessUnits)")
                                LabeledContent("最大发送间隔", value: String(format: "%.1f ms", d.maximumMediaSendGapMilliseconds))
                            }
                            Text(card.message).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
            } header: { Text("当前会话") }
        }
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


private struct AudioSourceSettings: View {
    @ObservedObject var engine: EngineClient
    @State private var draft = DesktopAudioSource()
    @State private var dirty = false
    private var outputs: [DesktopAudioSourceItem] {
        var values = engine.snapshot.outputDevices ?? []
        if !draft.outputDeviceId.isEmpty && !values.contains(where: { $0.id == draft.outputDeviceId }) {
            values.append(DesktopAudioSourceItem(id: draft.outputDeviceId, displayName: "所选设备已断开"))
        }
        return values
    }
    private var processes: [DesktopAudioSourceItem] {
        var values = engine.snapshot.candidateProcesses ?? []
        if let pid = draft.processId, !values.contains(where: { $0.processId == pid }) {
            values.append(DesktopAudioSourceItem(id: "missing:\(pid)", displayName: "所选应用当前无声音或已退出", processId: pid))
        }
        return values
    }
    private var canApply: Bool {
        draft.mode != "process" || (engine.snapshot.candidateProcesses ?? []).contains { $0.processId == draft.processId && draft.processId != nil }
    }
    private func choice(_ key: WritableKeyPath<DesktopAudioSource, String>) -> Binding<String> {
        Binding(get: { draft[keyPath: key] }, set: { draft[keyPath: key] = $0; dirty = true })
    }
    var body: some View {
        Section {
            HStack {
                Text("捕获范围"); Spacer()
                SettingsChoice(labels: ["系统声音", "指定应用"], values: ["system", "process"],
                    selection: choice(\.mode), enabled: engine.connected, accessibilityLabel: "捕获范围").frame(width: 235)
            }
            if draft.mode == "system" {
                HStack {
                    Text("输出设备"); Spacer()
                    SettingsChoice(labels: ["跟随系统默认"] + outputs.map(\.displayName), values: [""] + outputs.map(\.id),
                        selection: choice(\.outputDeviceId), enabled: engine.connected,
                        accessibilityLabel: "捕获输出设备").frame(width: 235)
                }
                Text("固定设备时，只捕获播放到该设备的声音；设备断开后停止投放。")
                    .font(.caption).foregroundStyle(.secondary)
            } else {
                HStack {
                    Text("应用"); Spacer()
                    SettingsChoice(labels: ["请选择正在出声的应用"] + processes.map(\.displayName),
                        values: [""] + processes.map { $0.processId.map(String.init) ?? "" },
                        selection: Binding(get: { draft.processId.map(String.init) ?? "" }, set: { draft.processId = Int($0); dirty = true }),
                        enabled: engine.connected, accessibilityLabel: "捕获应用").frame(width: 235)
                }
                Toggle("包含应用的辅助进程", isOn: Binding(get: { draft.includeChildren }, set: { draft.includeChildren = $0; dirty = true }))
                Text("只捕获所选应用。应用退出后停止投放；重新打开应用需重新选择。")
                    .font(.caption).foregroundStyle(.secondary)
            }
            HStack {
                Button("刷新音源") { engine.send("audioSources") }.disabled(!engine.connected)
                Spacer()
                Button("应用音源") { engine.send("audioSource", value: draft.dictionary); dirty = false }
                    .disabled(!engine.connected || !dirty || !canApply)
            }
            Text("更改音源会停止当前投放，需要重新选择音箱。")
                .font(.caption).foregroundStyle(.secondary)
        } header: { Label("音源", systemImage: "waveform.circle") }
        .onAppear { draft = engine.snapshot.audioSource ?? DesktopAudioSource(); engine.send("audioSources") }
        .onChange(of: engine.snapshot.audioSource) { _, source in if !dirty { draft = source ?? DesktopAudioSource() } }
    }
}


private struct ChannelRoutingSettings: View {
    @ObservedObject var engine: EngineClient
    private var speakers: [SpeakerCard] { engine.snapshot.speakers.filter { $0.groupId?.isEmpty != false } }
    var body: some View {
        if !speakers.isEmpty {
            Section {
                ForEach(speakers) { card in
                    HStack {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(card.name).lineLimit(1).help(card.name)
                            Text(card.isDlna ? "DLNA" : "MiPlay").font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        SettingsChoice(labels: ["完整双声道", "仅左声道", "仅右声道"],
                            values: ["Stereo", "LeftAsMono", "RightAsMono"],
                            selection: Binding(get: { card.channelRoute ?? "Stereo" }, set: {
                                engine.send("channelRoute", id: card.id, value: $0)
                            }), enabled: engine.connected, accessibilityLabel: "\(card.name)声道").frame(width: 180)
                    }
                }
                Text("更改声道会停止当前全部投放，需要重新选择音箱。官方立体声组合由设备管理左右声道，不提供单独修改。")
                    .font(.caption).foregroundStyle(.secondary)
            } header: { Label("声道分配", systemImage: "hifispeaker.2") }
        }
    }
}
