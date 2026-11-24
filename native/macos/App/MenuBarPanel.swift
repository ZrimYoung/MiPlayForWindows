import SwiftUI
import AppKit

struct MenuBarPanel: View {
    @ObservedObject var engine: EngineClient
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    private var sections: [SpeakerSection] { SpeakerSection.group(engine.snapshot.speakers) }
    private var listHeight: CGFloat {
        if sections.isEmpty { return 114 }
        return min(392, sections.reduce(CGFloat(0)) { height, section in
            height + 54 + (section.cards.contains(where: \.selected) ? 24 : 0) +
                (section.cards.contains(where: \.selected) && !section.cards.allSatisfy(\.selected) ? 28 : 0)
        } + CGFloat(max(0, sections.count - 1)) * 6 + 4)
    }
    var body: some View {
        VStack(spacing: 12) {
            SystemMediaCard(media: engine.media, fallbackSubtitle: engine.snapshot.captureDescription)
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("播放到").font(.system(size: 12, weight: .semibold))
                    Spacer()
                    if engine.snapshot.scanning { ProgressView().controlSize(.mini) }
                    Text(engine.snapshot.speakers.isEmpty ? "正在寻找音箱" : "自动发现")
                        .font(.system(size: 10)).foregroundStyle(.secondary)
                }.padding(.horizontal, 6)
                ScrollView {
                    VStack(spacing: 6) {
                        if sections.isEmpty {
                            VStack(spacing: 8) {
                                Image(systemName: "hifispeaker.2").font(.title2)
                                Text("让音箱和 Mac 连接同一网络")
                                    .font(.system(size: 11)).foregroundStyle(.secondary)
                            }.frame(maxWidth: .infinity).frame(height: 110)
                        }
                        ForEach(sections) { section in
                            CompactSpeakerRow(cards: section.cards, engine: engine)
                        }
                    }.padding(.horizontal, 2).padding(.bottom, 2)
                }.frame(height: listHeight).scrollIndicators(.automatic)
            }
            if !engine.error.isEmpty {
                Text(engine.error).font(.caption).foregroundStyle(.red).lineLimit(3)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }.padding(12).frame(width: 400)
            .background(PlaybackBackground(media: engine.media, compact: true))
            .animation(reduceMotion ? nil : .easeInOut(duration: 0.18), value: engine.snapshot.speakers.map(\.selected))
    }
}

/// A stereo pair is one route with one shared volume and one shared delay.
struct CompactSpeakerRow: View {
    let cards: [SpeakerCard]
    @ObservedObject var engine: EngineClient
    var showsDelay = false
    @Environment(\.colorScheme) private var scheme
    @State private var hovering = false
    @State private var expanded = false
    private var grouped: Bool { cards.first?.groupId?.isEmpty == false }
    private var selected: Bool { cards.contains(where: \.selected) }
    private var partial: Bool { selected && !cards.allSatisfy(\.selected) }
    private var busy: Bool { cards.contains { $0.state == "Connecting" || $0.state == "Reconnecting" } }
    private var ready: Bool {
        if !grouped { return cards.first?.online == true }
        return cards.count == 2 && cards.allSatisfy(\.online) && Set(cards.compactMap(\.channel)) == Set(["left", "right"])
    }
    private var name: String {
        if grouped { return cards.first?.groupName?.isEmpty == false ? cards.first!.groupName! : "立体声组合" }
        return cards.first?.name ?? "音箱"
    }
    private var message: String? {
        if grouped && !ready { return "组合不完整 · \(cards.filter(\.online).count)/2 台在线" }
        if partial && ready && !busy { return "部分音箱未连接，可重试加入" }
        if !selected && ready && cards.contains(where: { $0.failure == "Offline" }) { return "已重新发现 · 点选重新投放" }
        if let failure = cards.first(where: { $0.state == "Error" }) { return failure.message }
        if busy { return "正在连接…" }
        if !ready && !selected { return grouped ? "等待左右音箱上线" : "离线" }
        if let warning = cards.first(where: { $0.selected && $0.message.contains("未获确认") }) { return warning.message }
        if cards.first?.isDlna == true { return "DLNA" }
        return grouped ? (partial ? "立体声 · 部分音箱已选择" : "立体声") : nil
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Button {
                guard let first = cards.first else { return }
                engine.send(grouped ? "selectGroup" : "select", id: grouped ? first.groupId : first.id, value: !selected)
            } label: {
                HStack(spacing: 11) {
                    Image(systemName: grouped ? "hifispeaker.2.fill" : "hifispeaker.fill")
                        .font(.system(size: 19)).foregroundStyle(selected ? Color.accentColor : .secondary)
                        .frame(width: 25)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(name).font(.system(size: 13, weight: .medium)).lineLimit(1).help(name)
                        if let message {
                            Text(message).font(.system(size: 10))
                                .foregroundStyle(cards.contains { $0.state == "Error" } ? Color.red : .secondary)
                                .lineLimit(2)
                        }
                    }
                    Spacer(minLength: 4)
                    if busy { ProgressView().controlSize(.small) }
                    Image(systemName: partial ? "minus.circle.fill" : selected ? "checkmark.circle.fill" : "circle")
                        .font(.system(size: 20)).foregroundStyle(selected ? Color.accentColor : .secondary.opacity(0.4))
                }.frame(minHeight: 30).contentShape(Rectangle())
            }.buttonStyle(.plain).disabled(!engine.connected || (!selected && !ready))
                .accessibilityLabel("\(selected ? "停止投放到" : "投放到")\(name)")
            if selected {
                RouteVolume(cards: cards, name: name, engine: engine)
                if partial && ready && !busy {
                    Button("重新连接缺失音箱") {
                        engine.send("selectGroup", id: cards.first?.groupId, value: true)
                    }.controlSize(.small).disabled(!engine.connected)
                }
                if showsDelay {
                    DisclosureGroup("延迟微调", isExpanded: $expanded) {
                        VStack(spacing: 12) {
                            RouteDelay(cards: cards, engine: engine)
                        }.padding(.top, 8)
                    }.font(.system(size: 11)).foregroundStyle(.secondary)
                }
            }
        }.padding(.horizontal, 13).padding(.vertical, 11)
            .background(selected ? Color.primary.opacity(scheme == .dark ? 0.09 : 0.065) : Color.primary.opacity(hovering ? 0.05 : 0.025),
                        in: RoundedRectangle(cornerRadius: 13))
            .overlay(RoundedRectangle(cornerRadius: 13).strokeBorder(.primary.opacity(selected ? 0.07 : 0.025), lineWidth: 0.5))
            .onHover { hovering = $0 }
    }
}

private struct RouteVolume: View {
    let cards: [SpeakerCard]
    let name: String
    @ObservedObject var engine: EngineClient
    @State private var volume = 30.0
    @State private var dragging = false
    private var values: [Int?] { cards.map(\.volume) }
    private var agreedVolume: Int? {
        guard let first = cards.first?.volume, cards.allSatisfy({ $0.volume == first }) else { return nil }
        return first
    }
    private var initialVolume: Double { Double(agreedVolume ?? cards.compactMap(\.volume).min() ?? 30) }
    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: volume == 0 ? "speaker.slash.fill" : "speaker.wave.1.fill")
                .font(.system(size: 10)).foregroundStyle(.secondary).frame(width: 16)
            Slider(value: $volume, in: 0...100, onEditingChanged: { editing in
                dragging = editing
                if !editing, let first = cards.first {
                    let grouped = first.groupId?.isEmpty == false
                    engine.send(grouped ? "groupVolume" : "volume", id: grouped ? first.groupId : first.id, value: Int(volume.rounded()))
                }
            }).controlSize(.small).accessibilityLabel("\(name)音量")
                .disabled(!engine.connected || !cards.contains { $0.selected && $0.state == "Casting" })
            Text(agreedVolume == nil && !dragging ? "—" : "\(Int(volume.rounded()))%")
                .font(.system(size: 10)).monospacedDigit().foregroundStyle(.secondary).frame(width: 30, alignment: .trailing)
        }
        .help((cards.count > 1 ? "统一调整整组音量；— 表示左右音量不同或尚未确认。" : "调整音箱音量。") +
              (engine.snapshot.synchronizedPlayback ? "实体按键变化会自动读取。" : "兼容模式暂不读取实体按键变化。"))
        .onAppear { volume = initialVolume }
        .onChange(of: values) { _, _ in if !dragging { volume = initialVolume } }
    }
}

struct RouteDelay: View {
    let cards: [SpeakerCard]
    @ObservedObject var engine: EngineClient
    @State private var value = 1000.0
    @State private var dirty = false
    @State private var dragging = false
    private var delays: [Int] { cards.map(\.delayMilliseconds) }
    private var binding: Binding<Double> {
        Binding(get: { value }, set: { newValue in
            value = (newValue / 10).rounded() * 10; dirty = true
            guard let first = cards.first else { return }
            let grouped = first.groupId?.isEmpty == false
            engine.setDelay(id: grouped ? first.groupId! : first.id, grouped: grouped, milliseconds: Int(value))
        })
    }
    var body: some View {
        VStack(spacing: 8) {
            HStack {
                Text(cards.count > 1 ? "组合播放延迟" : "播放延迟")
                Spacer()
                Text("\(Int(value)) ms").monospacedDigit()
                Stepper("播放延迟", value: binding, in: 0...2000, step: 10).labelsHidden().controlSize(.small)
            }
            Slider(value: binding, in: 0...2000, step: 10, onEditingChanged: { dragging = $0 })
                .controlSize(.small).accessibilityLabel("播放延迟")
            HStack {
                ForEach([200, 500, 1000], id: \.self) { preset in
                    Button("\(preset) ms") { binding.wrappedValue = Double(preset) }.controlSize(.small)
                }
                Spacer()
            }
            Text("拖动即生效，无需重连；降低过多可能卡顿。数值不代表实测总延迟。")
                .font(.system(size: 10)).foregroundStyle(.secondary).frame(maxWidth: .infinity, alignment: .leading)
        }
        .disabled(!engine.connected)
        .onAppear { value = Double(cards.first?.delayMilliseconds ?? 1000) }
        .onChange(of: delays) { _, values in
            if values.allSatisfy({ $0 == Int(value) }) { dirty = false }
            if !dirty && !dragging { value = Double(values.first ?? 1000) }
        }
    }
}
